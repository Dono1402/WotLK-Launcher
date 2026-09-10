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
        if (ceiling >= 9 && !SupportsRequiredLockingSyntax(connection.ServerVersion))
        {
            throw new InvalidOperationException(
                "Les schemas Atlas 0009+ exigent MySQL 8 ou plus recent; MariaDB n'est pas pris en charge.");
        }
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
                else if (migration.Version == 9
                    && string.Equals(
                        migration.Name,
                        "auth_session_families",
                        StringComparison.Ordinal))
                {
                    bool alreadyComplete = await ReconcileAuthSessionFamiliesAsync(
                        connection,
                        cancellationToken);
                    await ValidateSchemaForVersionAsync(
                        connection,
                        migration.Version,
                        cancellationToken);
                    state = alreadyComplete
                        ? LauncherSchemaMigrationState.Adopted
                        : LauncherSchemaMigrationState.Applied;
                }
                else if (migration.Version == 10
                    && string.Equals(
                        migration.Name,
                        "auth_session_gc",
                        StringComparison.Ordinal))
                {
                    bool alreadyComplete = await ReconcileAuthSessionGarbageCollectionAsync(
                        connection,
                        cancellationToken);
                    await ValidateSchemaForVersionAsync(
                        connection,
                        migration.Version,
                        cancellationToken);
                    state = alreadyComplete
                        ? LauncherSchemaMigrationState.Adopted
                        : LauncherSchemaMigrationState.Applied;
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

    internal static bool SupportsRequiredLockingSyntax(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion)
            || serverVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
            return false;

        string numeric = serverVersion.Split('-', 2)[0];
        return Version.TryParse(numeric, out Version? parsed) && parsed.Major >= 8;
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
        if (version >= 6)
            await _validator.ValidateChatAsync(connection, cancellationToken);
        if (version >= 7)
            await _validator.ValidateChatV2Async(connection, cancellationToken);
        if (version >= 8)
            await _validator.ValidatePresenceAsync(connection, cancellationToken);
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

    private static async Task<bool> ReconcileAuthSessionFamiliesAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        AuthSessionFamilyMigrationState state =
            await ReadAuthSessionFamilyMigrationStateAsync(
                connection,
                cancellationToken);
        bool alreadyComplete = state.ColumnExists
            && !state.ColumnNullable
            && state.ExpiryIndexExists
            && state.HistoryTableExists
            && state.HistorySessionIndexExists
            && state.HistoryExpiryIndexExists;
        if (alreadyComplete)
            return true;

        if (!state.ColumnExists)
        {
            await ExecuteSqlAsync(
                connection,
                """
                ALTER TABLE atlas_launcher_session
                    ADD COLUMN absolute_expires_at DATETIME NULL AFTER refresh_expires_at;
                """,
                cancellationToken);
        }

        // Safe after either a fresh ADD or an interrupted earlier run. Existing
        // refresh tokens retain exactly the deadline they already had.
        await ExecuteSqlAsync(
            connection,
            """
            UPDATE atlas_launcher_session
            SET absolute_expires_at = refresh_expires_at
            WHERE absolute_expires_at IS NULL;
            """,
            cancellationToken);

        if (!state.ColumnExists || state.ColumnNullable)
        {
            await ExecuteSqlAsync(
                connection,
                """
                ALTER TABLE atlas_launcher_session
                    MODIFY absolute_expires_at DATETIME NOT NULL;
                """,
                cancellationToken);
        }

        if (!state.ExpiryIndexExists)
        {
            await ExecuteSqlAsync(
                connection,
                """
                ALTER TABLE atlas_launcher_session
                    ADD INDEX ix_atlas_session_absolute_expiry (absolute_expires_at);
                """,
                cancellationToken);
        }

        if (!state.HistoryTableExists)
        {
            await ExecuteSqlAsync(
                connection,
                """
                CREATE TABLE atlas_launcher_refresh_history (
                    token_hash BINARY(32) NOT NULL PRIMARY KEY,
                    session_id BINARY(16) NOT NULL,
                    expires_at DATETIME NOT NULL,
                    consumed_at DATETIME(6) NOT NULL,
                    INDEX ix_atlas_refresh_history_session (session_id),
                    INDEX ix_atlas_refresh_history_expiry (expires_at),
                    CONSTRAINT fk_atlas_refresh_history_session
                        FOREIGN KEY (session_id) REFERENCES atlas_launcher_session(id) ON DELETE CASCADE
                ) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
                """,
                cancellationToken);
        }
        else
        {
            List<string> missingHistoryIndexes = [];
            if (!state.HistorySessionIndexExists)
            {
                missingHistoryIndexes.Add(
                    "ADD INDEX ix_atlas_refresh_history_session (session_id)");
            }
            if (!state.HistoryExpiryIndexExists)
            {
                missingHistoryIndexes.Add(
                    "ADD INDEX ix_atlas_refresh_history_expiry (expires_at)");
            }

            if (missingHistoryIndexes.Count > 0)
            {
                await ExecuteSqlAsync(
                    connection,
                    $"ALTER TABLE atlas_launcher_refresh_history\n    {string.Join(",\n    ", missingHistoryIndexes)};",
                    cancellationToken);
            }
        }

        return false;
    }

    private static async Task<bool> ReconcileAuthSessionGarbageCollectionAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        (string Name, string[] Columns)[] indexes =
        [
            ("ix_atlas_session_revoked_gc", ["revoked_at", "id"]),
            ("ix_atlas_session_expired_gc", ["revoked_at", "absolute_expires_at", "id"]),
            ("ix_atlas_session_account_revoked", ["account_id", "revoked_at", "id"]),
            ("ix_atlas_session_account_active", ["account_id", "revoked_at", "refresh_expires_at", "id"]),
            ("ix_atlas_session_account_active_order", ["account_id", "revoked_at", "created_at", "id"])
        ];

        List<(string Name, string[] Columns)> missing = [];
        foreach ((string name, string[] columns) in indexes)
        {
            bool exists = await ReadExactNonUniqueIndexAsync(
                connection,
                "atlas_launcher_session",
                name,
                columns,
                cancellationToken);
            if (!exists)
                missing.Add((name, columns));
        }

        if (missing.Count == 0)
            return true;

        string clauses = string.Join(
            ",\n    ",
            missing.Select(index =>
                $"ADD INDEX {index.Name} ({string.Join(", ", index.Columns)})"));
        await ExecuteSqlAsync(
            connection,
            $"ALTER TABLE atlas_launcher_session\n    {clauses};",
            cancellationToken);
        return false;
    }

    internal static async Task<bool> ReadExactNonUniqueIndexAsync(
        MySqlConnection connection,
        string tableName,
        string indexName,
        IReadOnlyList<string> expectedColumns,
        CancellationToken cancellationToken)
    {
        List<(ulong NonUnique, ulong Sequence, object Column, object Direction,
            string Visible, string IndexType, object SubPart, object Expression)> actual = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME, COLLATION,
                       IS_VISIBLE, INDEX_TYPE, SUB_PART, EXPRESSION
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tableName
                  AND INDEX_NAME = @indexName
                ORDER BY SEQ_IN_INDEX;
                """;
            command.Parameters.AddWithValue("@tableName", tableName);
            command.Parameters.AddWithValue("@indexName", indexName);
            await using MySqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                actual.Add((
                    reader.GetUInt64(0),
                    reader.GetUInt64(1),
                    reader.GetValue(2),
                    reader.GetValue(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetValue(6),
                    reader.GetValue(7)));
            }
        }

        if (actual.Count == 0)
            return false;

        bool exact = actual.Count == expectedColumns.Count
            && actual.Select((column, index) =>
                    column.NonUnique == 1
                    && column.Sequence == (ulong)(index + 1)
                    && column.Column is string actualColumn
                    && string.Equals(
                        actualColumn,
                        expectedColumns[index],
                        StringComparison.OrdinalIgnoreCase)
                    && column.Direction is string direction
                    && string.Equals(direction, "A", StringComparison.Ordinal)
                    && string.Equals(column.Visible, "YES", StringComparison.Ordinal)
                    && string.Equals(column.IndexType, "BTREE", StringComparison.OrdinalIgnoreCase)
                    && column.SubPart is DBNull
                    && column.Expression is DBNull)
                .All(matches => matches);
        if (!exact)
        {
            throw new InvalidOperationException(
                $"L'index partiel {indexName} ne correspond pas a sa definition attendue.");
        }

        return true;
    }

    private static async Task<AuthSessionFamilyMigrationState>
        ReadAuthSessionFamilyMigrationStateAsync(
            MySqlConnection connection,
            CancellationToken cancellationToken)
    {
        bool columnExists = false;
        bool columnNullable = false;
        await using (MySqlCommand column = connection.CreateCommand())
        {
            column.CommandText = """
                SELECT absolute.COLUMN_TYPE,
                       absolute.IS_NULLABLE,
                       absolute.COLUMN_DEFAULT,
                       absolute.EXTRA,
                       absolute.ORDINAL_POSITION,
                       refresh.ORDINAL_POSITION
                FROM information_schema.COLUMNS absolute
                INNER JOIN information_schema.COLUMNS refresh
                    ON refresh.TABLE_SCHEMA = absolute.TABLE_SCHEMA
                   AND refresh.TABLE_NAME = absolute.TABLE_NAME
                   AND refresh.COLUMN_NAME = 'refresh_expires_at'
                WHERE absolute.TABLE_SCHEMA = DATABASE()
                  AND absolute.TABLE_NAME = 'atlas_launcher_session'
                  AND absolute.COLUMN_NAME = 'absolute_expires_at';
                """;
            await using MySqlDataReader reader =
                await column.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                columnExists = true;
                string columnType = reader.GetString(0);
                string nullable = reader.GetString(1);
                object defaultValue = reader.GetValue(2);
                string extra = reader.GetString(3);
                uint absolutePosition = reader.GetUInt32(4);
                uint refreshPosition = reader.GetUInt32(5);
                if (!string.Equals(columnType, "datetime", StringComparison.OrdinalIgnoreCase)
                    || nullable is not ("YES" or "NO")
                    || defaultValue is not DBNull
                    || !string.IsNullOrEmpty(extra)
                    || absolutePosition != refreshPosition + 1)
                {
                    throw new InvalidOperationException(
                        "La colonne partielle absolute_expires_at ne correspond pas a l'etape recuperable de la migration 0009.");
                }
                columnNullable = nullable == "YES";
            }
        }

        bool expiryIndexExists = false;
        await using (MySqlCommand index = connection.CreateCommand())
        {
            index.CommandText = """
                SELECT NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'atlas_launcher_session'
                  AND INDEX_NAME = 'ix_atlas_session_absolute_expiry'
                ORDER BY SEQ_IN_INDEX;
                """;
            await using MySqlDataReader reader =
                await index.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                expiryIndexExists = true;
                bool exact = reader.GetUInt64(0) == 1
                    && reader.GetUInt64(1) == 1
                    && string.Equals(
                        reader.GetString(2),
                        "absolute_expires_at",
                        StringComparison.OrdinalIgnoreCase)
                    && !await reader.ReadAsync(cancellationToken);
                if (!exact)
                {
                    throw new InvalidOperationException(
                        "L'index partiel ix_atlas_session_absolute_expiry ne correspond pas a la migration 0009.");
                }
            }
        }

        bool historyTableExists;
        await using (MySqlCommand table = connection.CreateCommand())
        {
            table.CommandText = """
                SELECT COUNT(*)
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'atlas_launcher_refresh_history';
                """;
            historyTableExists = Convert.ToInt32(
                await table.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        bool historySessionIndexExists = false;
        bool historyExpiryIndexExists = false;
        if (historyTableExists)
        {
            historySessionIndexExists = await ReadExactNonUniqueIndexAsync(
                connection,
                "atlas_launcher_refresh_history",
                "ix_atlas_refresh_history_session",
                ["session_id"],
                cancellationToken);
            historyExpiryIndexExists = await ReadExactNonUniqueIndexAsync(
                connection,
                "atlas_launcher_refresh_history",
                "ix_atlas_refresh_history_expiry",
                ["expires_at"],
                cancellationToken);
        }

        return new AuthSessionFamilyMigrationState(
            columnExists,
            columnNullable,
            expiryIndexExists,
            historyTableExists,
            historySessionIndexExists,
            historyExpiryIndexExists);
    }

    private static async Task ExecuteSqlAsync(
        MySqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
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

    private sealed record AuthSessionFamilyMigrationState(
        bool ColumnExists,
        bool ColumnNullable,
        bool ExpiryIndexExists,
        bool HistoryTableExists,
        bool HistorySessionIndexExists,
        bool HistoryExpiryIndexExists);
}
