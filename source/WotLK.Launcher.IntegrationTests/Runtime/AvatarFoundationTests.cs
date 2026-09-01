using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using SkiaSharp;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Avatars;
using WotLK.Launcher.Server.Database;

internal static class AvatarFoundationTests
{
    private const string TestConnectionVariable = "ATLAS_03A2A_TEST_DB";

    internal static async Task<int> RunAsync()
    {
        ValidateEmbeddedMigrations();
        await ValidateLocalStorageAsync();
        await ValidateImagePipelineAsync();
        Console.WriteLine("Avatar foundation OK (03A.2a, no API and no production storage)." );
        return 0;
    }

    internal static async Task<int> RunMySqlAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable(TestConnectionVariable)
            ?? throw new InvalidOperationException($"{TestConnectionVariable} doit viser une base MySQL jetable.");
        MySqlConnectionStringBuilder builder = new(connectionString);
        if (!builder.Database.StartsWith("atlas_03a2a_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Le test refuse toute base qui ne porte pas le prefixe atlas_03a2a_test_.");

        LauncherServerOptions options = new() { ConnectionString = builder.ConnectionString };
        EmbeddedLauncherSchemaMigrationSource source = new();
        LauncherSchemaValidator validator = new();

        await ResetToLegacyAsync(builder.ConnectionString);
        await using (MySqlConnection connection = new(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await validator.ValidateLegacyAsync(connection, CancellationToken.None);
        }

        LauncherSchemaMigrator firstMigrator = new(options, source, validator, "03A.2a-test");
        IReadOnlyList<LauncherSchemaMigrationOutcome> first = await firstMigrator.MigrateAsync();
        Equal(4, first.Count, "Quatre migrations doivent etre connues.");
        Equal(LauncherSchemaMigrationState.Adopted, first[0].State, "La copie reelle doit etre adoptee comme baseline.");
        Equal(LauncherSchemaMigrationState.Applied, first[1].State, "Le schema avatar doit etre applique.");
        Equal(LauncherSchemaMigrationState.Applied, first[2].State, "Le backend avatar doit etre applique.");
        Equal(LauncherSchemaMigrationState.Applied, first[3].State, "La frontiere des profils Atlas doit etre appliquee.");
        await AssertHistoryCountAsync(builder.ConnectionString, 4);

        IReadOnlyList<LauncherSchemaMigrationOutcome> second = await firstMigrator.MigrateAsync();
        True(second.All(item => item.State == LauncherSchemaMigrationState.AlreadyApplied), "La seconde execution doit etre sans effet.");

        IReadOnlyList<LauncherSchemaMigration> originals = source.Load();
        LauncherSchemaMigration changed = originals[1] with
        {
            Sql = originals[1].Sql + "-- checksum different\n",
            Sha256 = SHA256.HashData(Encoding.UTF8.GetBytes(originals[1].Sql + "-- checksum different\n"))
        };
        await ExpectAsync<InvalidOperationException>(
            () => new LauncherSchemaMigrator(
                options,
                new FixedMigrationSource([originals[0], changed, originals[2], originals[3]]),
                validator,
                "03A.2a-test").MigrateAsync(),
            "Un checksum modifie doit etre refuse.");

        await ResetToLegacyAsync(builder.ConnectionString);
        LauncherSchemaMigration failing = originals[1] with
        {
            Sql = originals[1].Sql + "\nTHIS IS NOT VALID SQL;",
            Sha256 = SHA256.HashData(Encoding.UTF8.GetBytes(originals[1].Sql + "\nTHIS IS NOT VALID SQL;"))
        };
        await ExpectAsync<MySqlException>(
            () => new LauncherSchemaMigrator(
                options,
                new FixedMigrationSource([originals[0], failing, originals[2], originals[3]]),
                validator,
                "03A.2a-test").MigrateAsync(),
            "Une migration SQL invalide doit echouer.");
        await AssertHistoryCountAsync(builder.ConnectionString, 1);
        await firstMigrator.MigrateAsync();
        await AssertHistoryCountAsync(builder.ConnectionString, 4);

        await ResetToLegacyAsync(builder.ConnectionString);
        LauncherSchemaMigrator concurrentA = new(options, source, validator, "03A.2a-concurrent-a");
        LauncherSchemaMigrator concurrentB = new(options, source, validator, "03A.2a-concurrent-b");
        await Task.WhenAll(concurrentA.MigrateAsync(), concurrentB.MigrateAsync());
        await AssertHistoryCountAsync(builder.ConnectionString, 4);

        Console.WriteLine("Avatar MySQL migrations OK: adoption, idempotence, checksum, failure recovery and concurrency.");
        return 0;
    }

    private static void ValidateEmbeddedMigrations()
    {
        IReadOnlyList<LauncherSchemaMigration> migrations = new EmbeddedLauncherSchemaMigrationSource().Load();
        Equal(4, migrations.Count, "Le serveur doit embarquer exactement quatre migrations.");
        Equal((uint)1, migrations[0].Version, "La baseline doit etre 0001.");
        Equal("legacy_baseline", migrations[0].Name, "Le nom de la baseline est incorrect.");
        Equal((uint)2, migrations[1].Version, "Le schema avatar doit etre 0002.");
        Equal("profile_avatar", migrations[1].Name, "Le nom de la migration avatar est incorrect.");
        Equal((uint)3, migrations[2].Version, "Le backend avatar doit etre 0003.");
        Equal("avatar_backend", migrations[2].Name, "Le nom de la migration backend est incorrect.");
        Equal((uint)4, migrations[3].Version, "La frontiere des profils Atlas doit etre 0004.");
        Equal("atlas_profile_identity_boundary", migrations[3].Name, "Le nom de la migration de frontiere est incorrect.");
        True(migrations.All(item => item.Sha256.Length == 32), "Chaque migration doit posseder un SHA-256.");
        True(migrations[0].Sql.Contains("avatar_key", StringComparison.Ordinal), "La compatibilite avatar_key doit rester dans la baseline.");
        True(
            migrations[3].Sql.Contains("REFERENCES atlas_launcher_profile(account_id)", StringComparison.Ordinal),
            "Les donnees Atlas doivent dependre d'un profil Atlas.");
        True(
            !migrations[3].Sql.Contains("DELETE FROM account", StringComparison.OrdinalIgnoreCase)
            && !migrations[3].Sql.Contains("DROP TABLE account", StringComparison.OrdinalIgnoreCase),
            "La migration ne doit jamais toucher aux lignes AzerothCore.");
    }

    private static async Task ValidateLocalStorageAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "AtlasAvatarStorageTests", Guid.NewGuid().ToString("N"));
        try
        {
            LocalAvatarStorage storage = new(root);
            byte[] png = CreatePng(256);
            AvatarStagingHandle handle = await storage.BeginStagingAsync(CancellationToken.None);
            await storage.WriteOriginalAsync(handle, new MemoryStream(png, writable: false), CancellationToken.None);
            foreach (int size in AvatarVariantSizes.All)
            {
                AvatarStoredVariant variant = await storage.WriteVariantAsync(
                    handle,
                    size,
                    new MemoryStream(png, writable: false),
                    CancellationToken.None);
                Equal("image/png", variant.ContentType, "Le stockage ne doit publier que du PNG.");
                Equal(32, variant.Sha256.Length, "La variante doit posseder un SHA-256.");
            }

            AvatarStorageKey key = AvatarStorageKey.Create(Guid.NewGuid(), 1);
            await storage.PublishAsync(handle, key, CancellationToken.None);
            foreach (int size in AvatarVariantSizes.All)
            {
                await using Stream stored = await storage.OpenVariantReadAsync(key, size, CancellationToken.None);
                True(stored.Length == png.Length, "Une variante publiee est incomplete.");
            }
            await ExpectAsync<IOException>(
                () => storage.OpenOriginalReadAsync(handle, CancellationToken.None),
                "L'original ne doit pas survivre a la publication.");
            True(await storage.MoveToTrashAsync(key, CancellationToken.None), "L'avatar publie doit pouvoir aller dans trash.");
            True(!await storage.MoveToTrashAsync(key, CancellationToken.None), "La mise en trash doit etre idempotente.");

            AvatarStagingHandle incomplete = await storage.BeginStagingAsync(CancellationToken.None);
            foreach (int size in AvatarVariantSizes.All.Take(3))
                await storage.WriteVariantAsync(incomplete, size, new MemoryStream(png, false), CancellationToken.None);
            await ExpectAsync<AvatarStorageException>(
                () => storage.PublishAsync(incomplete, AvatarStorageKey.Create(Guid.NewGuid(), 1), CancellationToken.None),
                "Un set incomplet ne doit jamais etre publie.");
            await storage.DiscardStagingAsync(incomplete, CancellationToken.None);

            AvatarStagingHandle quarantined = await storage.BeginStagingAsync(CancellationToken.None);
            await storage.WriteOriginalAsync(quarantined, new MemoryStream(png, false), CancellationToken.None);
            await storage.QuarantineAsync(quarantined, CancellationToken.None);
            await ExpectAsync<IOException>(
                () => storage.OpenOriginalReadAsync(quarantined, CancellationToken.None),
                "La quarantaine ne doit jamais conserver l'original.");

            AvatarStagingHandle tooLarge = await storage.BeginStagingAsync(CancellationToken.None);
            await ExpectAsync<AvatarStorageException>(
                () => storage.WriteOriginalAsync(
                    tooLarge,
                    new MemoryStream(new byte[AvatarLimits.MaximumFileBytes + 1], false),
                    CancellationToken.None),
                "La limite de 8 Mio doit etre appliquee par le stockage.");
            await storage.DiscardStagingAsync(tooLarge, CancellationToken.None);

            await ExpectAsync<ArgumentException>(
                () => Task.FromResult(AvatarStorageKey.Parse("avatars/../../etc")),
                "Une cle traversant les dossiers doit etre refusee.");

            Task[] concurrent = Enumerable.Range(0, 8).Select(async index =>
            {
                AvatarStagingHandle staged = await storage.BeginStagingAsync(CancellationToken.None);
                foreach (int size in AvatarVariantSizes.All)
                    await storage.WriteVariantAsync(staged, size, new MemoryStream(png, false), CancellationToken.None);
                await storage.PublishAsync(staged, AvatarStorageKey.Create(Guid.NewGuid(), (ulong)index + 1), CancellationToken.None);
            }).ToArray();
            await Task.WhenAll(concurrent);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ValidateImagePipelineAsync()
    {
        StringWriter output = new();
        int exitCode = await AvatarImageSpikeRunner.RunAsync(output);
        Equal(0, exitCode, "Le spike Skia local doit reussir.");
        True(output.ToString().Contains("SKIASHARP_SPIKE=PASS", StringComparison.Ordinal), "Le spike Skia n'a pas produit son verdict.");
    }

    private static async Task ResetToLegacyAsync(string connectionString)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SET FOREIGN_KEY_CHECKS = 0;
            DROP TABLE IF EXISTS atlas_launcher_avatar_upload_attempt;
            DROP TABLE IF EXISTS atlas_launcher_profile_avatar;
            DROP TABLE IF EXISTS atlas_launcher_avatar_variant;
            DROP TABLE IF EXISTS atlas_launcher_avatar_asset;
            DROP TABLE IF EXISTS atlas_launcher_schema_history;

            ALTER TABLE atlas_launcher_session
                DROP FOREIGN KEY fk_atlas_session_account,
                ADD CONSTRAINT fk_atlas_session_account
                    FOREIGN KEY (account_id) REFERENCES account(id) ON DELETE CASCADE;
            ALTER TABLE atlas_launcher_email_verification
                DROP FOREIGN KEY fk_atlas_email_account,
                ADD CONSTRAINT fk_atlas_email_account
                    FOREIGN KEY (account_id) REFERENCES account(id) ON DELETE CASCADE;
            ALTER TABLE atlas_launcher_friendship
                DROP FOREIGN KEY fk_atlas_friend_low,
                DROP FOREIGN KEY fk_atlas_friend_high,
                DROP FOREIGN KEY fk_atlas_friend_requester,
                ADD CONSTRAINT fk_atlas_friend_low
                    FOREIGN KEY (account_low_id) REFERENCES account(id) ON DELETE CASCADE,
                ADD CONSTRAINT fk_atlas_friend_high
                    FOREIGN KEY (account_high_id) REFERENCES account(id) ON DELETE CASCADE,
                ADD CONSTRAINT fk_atlas_friend_requester
                    FOREIGN KEY (requested_by_id) REFERENCES account(id) ON DELETE CASCADE;
            SET FOREIGN_KEY_CHECKS = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertHistoryCountAsync(string connectionString, int expected)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM atlas_launcher_schema_history";
        int actual = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Equal(expected, actual, "Le nombre de migrations enregistrees est incorrect.");
    }

    private static byte[] CreatePng(int size)
    {
        using SKSurface surface = SKSurface.Create(new SKImageInfo(size, size))
            ?? throw new InvalidOperationException("Impossible de creer le PNG de stockage.");
        surface.Canvas.Clear(new SKColor(219, 177, 82));
        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Impossible d'encoder le PNG de stockage.");
        return data.ToArray();
    }

    private static async Task ExpectAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Attendu={expected}, reel={actual}.");
    }

    private sealed class FixedMigrationSource(IReadOnlyList<LauncherSchemaMigration> migrations)
        : ILauncherSchemaMigrationSource
    {
        public IReadOnlyList<LauncherSchemaMigration> Load() => migrations;
    }
}
