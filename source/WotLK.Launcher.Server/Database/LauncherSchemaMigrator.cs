using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed record LauncherSchemaMigrationOutcome(
    uint Version,
    string Name,
    LauncherSchemaMigrationState State);

internal enum LauncherSchemaMigrationState
{
    AlreadyApplied,
    Adopted,
    Applied,
    BlockedByCeiling
}

internal sealed class LauncherSchemaMigrator
{
    private readonly LauncherServerOptions _options;
    private readonly ILauncherSchemaMigrationSource _source;
    private readonly LauncherSchemaValidator _validator;
    private readonly string _applicationVersion;
    private readonly ILogger<LauncherSchemaMigrator> _logger;

    internal LauncherSchemaMigrator(LauncherServerOptions options)
        : this(
            options,
            new EmbeddedLauncherSchemaMigrationSource(),
            new LauncherSchemaValidator(),
            ResolveApplicationVersion(),
            NullLogger<LauncherSchemaMigrator>.Instance)
    {
    }

    internal LauncherSchemaMigrator(
        LauncherServerOptions options,
        ILogger<LauncherSchemaMigrator> logger)
        : this(
            options,
            new EmbeddedLauncherSchemaMigrationSource(),
            new LauncherSchemaValidator(),
            ResolveApplicationVersion(),
            logger)
    {
    }

    internal LauncherSchemaMigrator(
        LauncherServerOptions options,
        ILauncherSchemaMigrationSource source,
        LauncherSchemaValidator validator,
        string applicationVersion,
        ILogger<LauncherSchemaMigrator>? logger = null)
    {
        _options = options;
        _source = source;
        _validator = validator;
        _applicationVersion = string.IsNullOrWhiteSpace(applicationVersion)
            ? "unknown"
            : applicationVersion[..Math.Min(applicationVersion.Length, 64)];
        _logger = logger ?? NullLogger<LauncherSchemaMigrator>.Instance;
    }

    internal async Task<IReadOnlyList<LauncherSchemaMigrationOutcome>> MigrateAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LauncherSchemaMigration> migrations = _source.Load();
        uint ceiling = ResolveCeiling(migrations, _options.MaximumSchemaVersion);
        IReadOnlyList<LauncherSchemaMigration> eligibleMigrations = migrations
            .Where(migration => migration.Version <= ceiling)
            .ToArray();
        IReadOnlyList<LauncherSchemaMigration> blockedMigrations = migrations
            .Where(migration => migration.Version > ceiling)
            .ToArray();
        foreach (LauncherSchemaMigration blocked in blockedMigrations)
        {
            _logger.LogWarning(
                "La migration Atlas {MigrationVersion:D4} ({MigrationName}) est disponible mais bloquee par le plafond {MigrationCeiling:D4}.",
                blocked.Version,
                blocked.Name,
                ceiling);
        }

        await using MySqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        string lockName = BuildLockName(connection.Database);
        await AcquireLockAsync(connection, lockName, cancellationToken);

        try
        {
            await EnsureHistoryTableAsync(connection, cancellationToken);
            await _validator.ValidateHistoryAsync(connection, cancellationToken);
            Dictionary<uint, AppliedMigration> applied = await ReadHistoryAsync(connection, cancellationToken);
            ValidateHistory(migrations, applied);
            ValidateAppliedVersionsAgainstCeiling(applied, ceiling);

            List<LauncherSchemaMigrationOutcome> outcomes = [];
            foreach (LauncherSchemaMigration migration in eligibleMigrations)
            {
                if (applied.ContainsKey(migration.Version))
                {
                    outcomes.Add(new(migration.Version, migration.Name, LauncherSchemaMigrationState.AlreadyApplied));
                    continue;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                LauncherSchemaMigrationState state;
                if (migration.Version == 1)
                {
                    int legacyTableCount = await _validator.CountLegacyTablesAsync(connection, cancellationToken);
                    if (legacyTableCount is > 0 and < 4)
                    {
                        throw new InvalidOperationException(
                            $"Baseline Atlas incomplete : {legacyTableCount} table(s) legacy sur 4.");
                    }

                    if (legacyTableCount == 4)
                    {
                        await _validator.ValidateLegacyAsync(connection, cancellationToken);
                        state = LauncherSchemaMigrationState.Adopted;
                    }
                    else
                    {
                        await ExecuteMigrationAsync(connection, migration, cancellationToken);
                        await _validator.ValidateLegacyAsync(connection, cancellationToken);
                        state = LauncherSchemaMigrationState.Applied;
                    }
                }
                else
                {
                    await ExecuteMigrationAsync(connection, migration, cancellationToken);
                    await ValidateSchemaForVersionAsync(connection, migration.Version, cancellationToken);
                    state = LauncherSchemaMigrationState.Applied;
                }

                stopwatch.Stop();
                await RecordMigrationAsync(
                    connection,
                    migration,
                    checked((uint)Math.Min(stopwatch.ElapsedMilliseconds, uint.MaxValue)),
                    cancellationToken);
                outcomes.Add(new(migration.Version, migration.Name, state));
            }

            await ValidateSchemaForVersionAsync(connection, eligibleMigrations[^1].Version, cancellationToken);
            outcomes.AddRange(blockedMigrations.Select(migration => new LauncherSchemaMigrationOutcome(
                migration.Version,
                migration.Name,
                LauncherSchemaMigrationState.BlockedByCeiling)));
            return outcomes;
        }
        finally
        {
            await ReleaseLockAsync(connection, lockName);
        }
    }

    private static uint ResolveCeiling(
        IReadOnlyList<LauncherSchemaMigration> migrations,
        uint? configuredCeiling)
    {
        uint latestVersion = migrations[^1].Version;
        if (configuredCeiling is null)
            return latestVersion;
        if (configuredCeiling.Value == 0)
            throw new InvalidOperationException("Le plafond de migration Atlas doit etre superieur a zero.");
        if (configuredCeiling.Value > latestVersion)
        {
            throw new InvalidOperationException(
                $"Le plafond de migration Atlas {configuredCeiling.Value:D4} ne correspond a aucune migration embarquee ; derniere version disponible : {latestVersion:D4}.");
        }

        return configuredCeiling.Value;
    }

    private static void ValidateAppliedVersionsAgainstCeiling(
        IReadOnlyDictionary<uint, AppliedMigration> applied,
        uint ceiling)
    {
        uint highestApplied = applied.Count == 0 ? 0 : applied.Keys.Max();
        if (highestApplied > ceiling)
        {
            throw new InvalidOperationException(
                $"La base Atlas contient deja la migration {highestApplied:D4}, au-dessus du plafond configure {ceiling:D4}.");
        }
    }

    private async Task ValidateSchemaForVersionAsync(
        MySqlConnection connection,
        uint version,
        CancellationToken cancellationToken)
    {
        if (version >= 1)
            await _validator.ValidateLegacyAsync(connection, version, cancellationToken);
        if (version >= 2)
            await _validator.ValidateAvatarAsync(connection, version, cancellationToken);
    }

    private static async Task ExecuteMigrationAsync(
        MySqlConnection connection,
        LauncherSchemaMigration migration,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = migration.Sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecordMigrationAsync(
        MySqlConnection connection,
        LauncherSchemaMigration migration,
        uint durationMilliseconds,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO atlas_launcher_schema_history
                (version, name, sha256, applied_at, duration_ms, application_version)
            VALUES
                (@version, @name, @sha256, UTC_TIMESTAMP(6), @duration, @applicationVersion)
            """;
        command.Parameters.AddWithValue("@version", migration.Version);
        command.Parameters.AddWithValue("@name", migration.Name);
        command.Parameters.AddWithValue("@sha256", migration.Sha256);
        command.Parameters.AddWithValue("@duration", durationMilliseconds);
        command.Parameters.AddWithValue("@applicationVersion", _applicationVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureHistoryTableAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS atlas_launcher_schema_history (
                version INT UNSIGNED NOT NULL PRIMARY KEY,
                name VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                sha256 BINARY(32) NOT NULL,
                applied_at DATETIME(6) NOT NULL,
                duration_ms INT UNSIGNED NOT NULL,
                application_version VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<uint, AppliedMigration>> ReadHistoryAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        Dictionary<uint, AppliedMigration> result = [];
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, name, sha256
            FROM atlas_launcher_schema_history
            ORDER BY version
            """;
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            uint version = reader.GetUInt32(0);
            result.Add(version, new AppliedMigration(reader.GetString(1), (byte[])reader.GetValue(2)));
        }
        return result;
    }

    private static void ValidateHistory(
        IReadOnlyList<LauncherSchemaMigration> migrations,
        IReadOnlyDictionary<uint, AppliedMigration> applied)
    {
        Dictionary<uint, LauncherSchemaMigration> available = migrations.ToDictionary(item => item.Version);
        foreach ((uint version, AppliedMigration recorded) in applied)
        {
            if (!available.TryGetValue(version, out LauncherSchemaMigration? migration))
                throw new InvalidOperationException($"Migration Atlas inconnue deja appliquee : {version:D4}.");
            if (!string.Equals(recorded.Name, migration.Name, StringComparison.Ordinal))
                throw new InvalidOperationException($"Le nom de la migration Atlas {version:D4} a change.");
            if (!CryptographicOperations.FixedTimeEquals(recorded.Sha256, migration.Sha256))
                throw new InvalidOperationException($"Le checksum de la migration Atlas {version:D4} a change.");
        }
    }

    private static async Task AcquireLockAsync(
        MySqlConnection connection,
        string lockName,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@name, 30)";
        command.Parameters.AddWithValue("@name", lockName);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new TimeoutException("Impossible d'obtenir le verrou des migrations Atlas.");
    }

    private static async Task ReleaseLockAsync(MySqlConnection connection, string lockName)
    {
        try
        {
            await using MySqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT RELEASE_LOCK(@name)";
            command.Parameters.AddWithValue("@name", lockName);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception) when (connection.State != System.Data.ConnectionState.Open)
        {
            // Closing the connection releases the named MySQL lock.
        }
    }

    private static string BuildLockName(string databaseName)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(databaseName));
        return $"atlas_schema:{Convert.ToHexString(hash.AsSpan(0, 16))}";
    }

    private static string ResolveApplicationVersion()
    {
        Assembly assembly = typeof(LauncherSchemaMigrator).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private sealed record AppliedMigration(string Name, byte[] Sha256);
}
