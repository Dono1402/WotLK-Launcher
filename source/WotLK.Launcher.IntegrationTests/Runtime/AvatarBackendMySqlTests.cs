using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using SkiaSharp;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Avatars;
using WotLK.Launcher.Server.Database;

internal static partial class AvatarBackendTests
{
    internal static async Task<int> RunMySqlAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable(TestConnectionVariable)
            ?? throw new InvalidOperationException($"{TestConnectionVariable} doit viser une base MySQL jetable.");
        MySqlConnectionStringBuilder connection = new(connectionString);
        if (!connection.Database.StartsWith("atlas_03a2b_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Le test refuse toute base qui ne porte pas le prefixe atlas_03a2b_test_.");

        LauncherServerOptions options = new()
        {
            ConnectionString = connection.ConnectionString,
            AvatarMediaRoot = NewTemporaryRoot()
        };
        List<string> roots = [options.AvatarMediaRoot];
        try
        {
            await ResetAvatarSchemaAsync(options.ConnectionString);
            LauncherSchemaMigrator migrator = new(options);
            IReadOnlyList<LauncherSchemaMigrationOutcome> migrations = await migrator.MigrateAsync();
            Equal(4, migrations.Count, "Les quatre migrations Atlas doivent etre connues.");
            await ValidateSchemaAndChecksumAsync(options);
            await ValidateAtlasProfileBoundaryAsync(options);
            await ValidateMutationLockLifecycleAsync(options);
            await ValidateRepositoryRateLimitAsync(options);
            await ValidateHttpContractAsync(options, roots);
            await ValidateDatabaseConcurrencyAsync(options, roots);
            await ValidateFailureAtomicityAsync(options, roots);
            await ValidateIpRateLimitAsync(options, roots);
            Console.WriteLine("Avatar backend MySQL/HTTP OK: persistence, API, media cache, limits, concurrency and rollback.");
            return 0;
        }
        finally
        {
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                TryDeleteDirectory(root);
        }
    }

    private static async Task ValidateSchemaAndChecksumAsync(LauncherServerOptions options)
    {
        await using MySqlConnection connection = new(options.ConnectionString);
        await connection.OpenAsync();
        await new LauncherSchemaValidator().ValidateLegacyAsync(connection, 4, CancellationToken.None);
        await new LauncherSchemaValidator().ValidateAvatarAsync(connection, 4, CancellationToken.None);
        IReadOnlyList<LauncherSchemaMigration> originals = new EmbeddedLauncherSchemaMigrationSource().Load();
        LauncherSchemaMigration changed = originals[1] with
        {
            Sql = originals[1].Sql + "-- forbidden edit\n",
            Sha256 = SHA256.HashData(Encoding.UTF8.GetBytes(originals[1].Sql + "-- forbidden edit\n"))
        };
        await ExpectAsync<InvalidOperationException>(
            () => new LauncherSchemaMigrator(
                options,
                new FixedAvatarMigrationSource([originals[0], changed, originals[2], originals[3]]),
                new LauncherSchemaValidator(),
                "03A.2b-checksum").MigrateAsync(),
            "La modification d'une migration deja appliquee doit etre refusee.");
    }

    private static async Task ValidateAtlasProfileBoundaryAsync(LauncherServerOptions options)
    {
        string suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        string username = $"RNDBOT_BOUNDARY_{suffix}";
        const string password = "Atlas-technical-test-2026";
        uint technicalAccountId;
        (byte[] salt, byte[] verifier) = SrpCredentials.MakeLegacy(username, password);

        await using (MySqlConnection connection = new(options.ConnectionString))
        {
            await connection.OpenAsync();
            await using MySqlCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO account
                    (username, salt, verifier, email, reg_mail, joindate, expansion)
                VALUES
                    (@username, @salt, @verifier, @email, @email, UTC_TIMESTAMP(), 2);
                SELECT LAST_INSERT_ID();
                """;
            insert.Parameters.AddWithValue("@username", username);
            insert.Parameters.Add("@salt", MySqlDbType.Binary, 32).Value = salt;
            insert.Parameters.Add("@verifier", MySqlDbType.Binary, 32).Value = verifier;
            insert.Parameters.AddWithValue("@email", $"{username.ToLowerInvariant()}@example.test");
            technicalAccountId = Convert.ToUInt32(
                await insert.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        LauncherDatabase database = CreateDatabase(options);
        AuthResponse? login = await database.LoginAsync(
            new LoginRequest(username, password, "technical-account-test"),
            CancellationToken.None);
        True(login is null, "Un compte AzerothCore sans profil ne doit pas ouvrir de session Atlas.");
        await ExpectAsync<InvalidOperationException>(
            () => database.GetProfileAsync(technicalAccountId, CancellationToken.None),
            "Un compte technique ne doit exposer aucun profil ni AvatarDescriptor.");

        await using (MySqlConnection connection = new(options.ConnectionString))
        {
            await connection.OpenAsync();
            Equal(
                1L,
                await ScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM account WHERE id = @accountId",
                    ("@accountId", technicalAccountId)),
                "Le compte technique AzerothCore doit rester intact.");
            Equal(
                0L,
                await ScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM atlas_launcher_profile WHERE account_id = @accountId",
                    ("@accountId", technicalAccountId)),
                "La connexion ne doit jamais creer automatiquement un profil Atlas.");
        }

        AuthResponse atlasAccount = await RegisterTestAccountAsync(database, "boundary");
        AuthResponse? atlasLogin = await database.LoginAsync(
            new LoginRequest(atlasAccount.Profile.Username, "Atlas-avatar-test-2026", "atlas-profile-test"),
            CancellationToken.None);
        True(atlasLogin is not null, "Un compte possedant un profil Atlas doit toujours pouvoir se connecter.");
        FriendRequestResult request = await database.SendFriendRequestAsync(
            atlasAccount.Profile.AccountId,
            username,
            CancellationToken.None);
        Equal(FriendRequestOutcome.NotFound, request.Outcome, "Un compte technique ne doit pas etre trouvable socialement.");

        AvatarRepository repository = new(options);
        await ExpectAsync<InvalidOperationException>(
            () => repository.TryConsumeUploadPermitAsync(technicalAccountId, CancellationToken.None),
            "Un compte technique ne doit pas obtenir de quota avatar.");
        await ExpectAsync<InvalidOperationException>(
            () => repository.CreatePendingAsync(technicalAccountId, CancellationToken.None),
            "Un compte technique ne doit pas creer d'avatar.");
    }

    private static async Task ValidateRepositoryRateLimitAsync(LauncherServerOptions options)
    {
        LauncherDatabase database = CreateDatabase(options);
        AuthResponse tenMinuteAccount = await RegisterTestAccountAsync(database, "rate10");
        AvatarRepository repository = new(options);
        for (int index = 0; index < AvatarLimits.UploadsPerTenMinutes; index++)
        {
            AvatarRateLimitDecision permit = await repository.TryConsumeUploadPermitAsync(
                tenMinuteAccount.Profile.AccountId,
                CancellationToken.None);
            True(permit.Allowed, "Les cinq premiers uploads sur dix minutes doivent etre autorises.");
        }
        AvatarRateLimitDecision sixth = await repository.TryConsumeUploadPermitAsync(
            tenMinuteAccount.Profile.AccountId,
            CancellationToken.None);
        True(!sixth.Allowed && sixth.RetryAfterSeconds > 0, "Le sixieme upload doit produire 429.");

        AuthResponse dailyAccount = await RegisterTestAccountAsync(database, "rateday");
        await InsertUploadAttemptsAsync(options.ConnectionString, dailyAccount.Profile.AccountId, 19, minutesAgo: 20);
        AvatarRateLimitDecision twentieth = await repository.TryConsumeUploadPermitAsync(
            dailyAccount.Profile.AccountId,
            CancellationToken.None);
        True(twentieth.Allowed, "Le vingtieme upload journalier doit etre autorise.");
        AvatarRateLimitDecision twentyFirst = await repository.TryConsumeUploadPermitAsync(
            dailyAccount.Profile.AccountId,
            CancellationToken.None);
        True(!twentyFirst.Allowed, "Le vingt-et-unieme upload journalier doit etre refuse.");
        await repository.DeleteActiveAsync(dailyAccount.Profile.AccountId, CancellationToken.None);
        AvatarRateLimitDecision afterDelete = await repository.TryConsumeUploadPermitAsync(
            dailyAccount.Profile.AccountId,
            CancellationToken.None);
        True(!afterDelete.Allowed, "Une suppression ne doit pas remettre le quota d'upload a zero.");
    }

    private static async Task ValidateMutationLockLifecycleAsync(LauncherServerOptions options)
    {
        const uint accountId = 4_000_000_001;
        AvatarMutationLockProvider provider = new(options);
        IAvatarMutationLease lease = await provider.TryAcquireAsync(accountId, CancellationToken.None)
            ?? throw new InvalidOperationException("Le verrou MySQL témoin doit être acquis.");
        AvatarMutationLease concrete = (AvatarMutationLease)lease;
        FieldInfo connectionField = typeof(AvatarMutationLease).GetField(
            "_connection",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Connexion propriétaire du verrou introuvable.");
        MySqlConnection owner = (MySqlConnection?)connectionField.GetValue(concrete)
            ?? throw new InvalidOperationException("Connexion propriétaire du verrou absente.");
        long ownerConnectionId = await ScalarInt64Async(owner, "SELECT CONNECTION_ID()");
        string lockName = GetAvatarLockName(options.ConnectionString, accountId);
        await using MySqlConnection observer = new(options.ConnectionString);
        await observer.OpenAsync();
        long lockOwner = await ScalarInt64Async(
            observer,
            "SELECT IS_USED_LOCK(@name)",
            ("@name", lockName));
        Equal(ownerConnectionId, lockOwner,
            "GET_LOCK doit être détenu par la connexion conservée dans le bail.");

        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Equal(1L, await ScalarInt64Async(
                observer,
                "SELECT IS_FREE_LOCK(@name)",
                ("@name", lockName)),
            "Dispose doit libérer le verrou de façon idempotente.");

        await using (MySqlConnection pooled = new(options.ConnectionString))
        {
            await pooled.OpenAsync();
            Equal(1L, await ScalarInt64Async(
                    pooled,
                    "SELECT IS_FREE_LOCK(@name)",
                    ("@name", lockName)),
                "Une connexion ne doit jamais retourner au pool avec GET_LOCK encore détenu.");
        }

        await using IAvatarMutationLease reacquired = await provider.TryAcquireAsync(
            accountId,
            CancellationToken.None)
            ?? throw new InvalidOperationException("Le verrou doit pouvoir être repris après libération.");
    }

    private static async Task ValidateHttpContractAsync(LauncherServerOptions options, ICollection<string> roots)
    {
        string root = NewTemporaryRoot();
        roots.Add(root);
        await using AvatarHttpHarness harness = await AvatarHttpHarness.CreateAsync(options, root, "http");
        byte[] png = CreateImage(SKEncodedImageFormat.Png, 512, 512);

        using (HttpResponseMessage unauthorized = await SendUploadAsync(
            harness.Client, null, png, "image/png", "avatar.png", 0, 0, 1))
        {
            Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode, "L'upload doit exiger un bearer Atlas.");
        }

        using (HttpResponseMessage tooLarge = await SendUploadAsync(
            harness.Client,
            harness.AccessToken,
            new byte[AvatarLimits.MaximumFileBytes + 1],
            "image/png",
            "large.png",
            0,
            0,
            1))
        {
            await AssertApiErrorAsync(tooLarge, HttpStatusCode.RequestEntityTooLarge, "AvatarTooLarge");
        }
        using (HttpResponseMessage corrupt = await SendUploadAsync(
            harness.Client, harness.AccessToken, [0x13, 0x37], "image/png", "avatar.png", 0, 0, 1))
        {
            await AssertApiErrorAsync(corrupt, HttpStatusCode.BadRequest, "InvalidImage");
        }
        using (HttpResponseMessage invalidCrop = await SendUploadAsync(
            harness.Client, harness.AccessToken, png, "image/png", "avatar.png", 0.9, 0, 1))
        {
            await AssertApiErrorAsync(invalidCrop, HttpStatusCode.BadRequest, "InvalidCrop");
        }

        await harness.Database.ChangeAvatarAsync(
            harness.AccountId,
            "gold",
            CancellationToken.None);
        AvatarDescriptor first;
        using (HttpResponseMessage uploaded = await SendUploadAsync(
            harness.Client,
            harness.AccessToken,
            png,
            "image/png",
            "../../extension-trompeuse.gif",
            0,
            0,
            1))
        {
            Equal(HttpStatusCode.OK, uploaded.StatusCode, "Le serveur doit se fier au decodage, pas au nom du fichier.");
            first = await ReadRequiredAsync<AvatarDescriptor>(uploaded);
        }
        await ValidateProfileContractAsync(harness, first, expectedLegacyAvatarKey: "gold");
        await ValidateMediaContractAsync(harness, first);

        AuthResponse foreign = await RegisterTestAccountAsync(harness.Database, "foreign");
        using (HttpRequestMessage foreignDelete = new(HttpMethod.Delete, "/api/v1/me/avatar/photo"))
        {
            foreignDelete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", foreign.AccessToken);
            using HttpResponseMessage response = await harness.Client.SendAsync(foreignDelete);
            Equal(HttpStatusCode.NoContent, response.StatusCode, "Supprimer sans photo doit rester idempotent.");
        }
        Equal(first, await harness.Repository.GetActiveDescriptorAsync(harness.AccountId, CancellationToken.None),
            "Un compte ne doit pas pouvoir supprimer la photo d'un autre compte.");

        AvatarAssetRecord pending = await harness.Repository.CreatePendingAsync(
            harness.AccountId,
            CancellationToken.None);
        await AssertMediaNotFoundAsync(harness, AvatarDescriptor.Create(pending.Id, pending.Version).Url64);

        AvatarDescriptor replacement;
        using (HttpResponseMessage uploaded = await SendUploadAsync(
            harness.Client, harness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1))
        {
            Equal(HttpStatusCode.OK, uploaded.StatusCode, "Le remplacement HTTP doit reussir.");
            replacement = await ReadRequiredAsync<AvatarDescriptor>(uploaded);
        }
        await AssertMediaNotFoundAsync(harness, first.Url64);
        await ValidateMediaContractAsync(harness, replacement);

        using (HttpResponseMessage rateLimited = await SendUploadAsync(
            harness.Client, harness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1))
        {
            await AssertApiErrorAsync(rateLimited, HttpStatusCode.TooManyRequests, "RateLimited");
            True(rateLimited.Headers.RetryAfter?.Delta is not null
                || rateLimited.Headers.TryGetValues("Retry-After", out _),
                "Le quota compte doit fournir Retry-After.");
        }

        using (HttpRequestMessage delete = Authorized(HttpMethod.Delete, "/api/v1/me/avatar/photo", harness.AccessToken))
        using (HttpResponseMessage response = await harness.Client.SendAsync(delete))
            Equal(HttpStatusCode.NoContent, response.StatusCode, "DELETE doit detacher la photo.");
        using (HttpRequestMessage deleteAgain = Authorized(HttpMethod.Delete, "/api/v1/me/avatar/photo", harness.AccessToken))
        using (HttpResponseMessage response = await harness.Client.SendAsync(deleteAgain))
            Equal(HttpStatusCode.NoContent, response.StatusCode, "DELETE doit etre idempotent.");
        await AssertMediaNotFoundAsync(harness, replacement.Url64);
        AccountProfile deletedProfile = await GetProfileAsync(harness);
        True(deletedProfile.Avatar is null, "Le profil doit revenir au fallback par initiale apres DELETE.");
        Equal("gold", deletedProfile.AvatarKey, "Le contrat legacy avatar_key doit rester independant de la photo.");
        using (HttpResponseMessage stillLimited = await SendUploadAsync(
            harness.Client, harness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1))
            await AssertApiErrorAsync(stillLimited, HttpStatusCode.TooManyRequests, "RateLimited");
    }

    private static async Task ValidateDatabaseConcurrencyAsync(
        LauncherServerOptions options,
        ICollection<string> roots)
    {
        string root = NewTemporaryRoot();
        roots.Add(root);
        using SkiaAvatarImageProcessor inner = new();
        BlockingAvatarImageProcessor blocking = new(inner);
        await using AvatarHttpHarness harness = await AvatarHttpHarness.CreateAsync(
            options,
            root,
            "concurrency",
            processor: blocking);
        byte[] png = CreateImage(SKEncodedImageFormat.Png, 512, 512);

        Task<HttpResponseMessage> firstTask = SendUploadAsync(
            harness.Client, harness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1);
        await blocking.WaitUntilEnteredAsync();
        using (HttpResponseMessage second = await SendUploadAsync(
            harness.Client, harness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1))
        {
            await AssertApiErrorAsync(second, HttpStatusCode.Conflict, "UploadInProgress");
        }
        using (HttpRequestMessage delete = Authorized(HttpMethod.Delete, "/api/v1/me/avatar/photo", harness.AccessToken))
        using (HttpResponseMessage concurrentDelete = await harness.Client.SendAsync(delete))
        {
            await AssertApiErrorAsync(concurrentDelete, HttpStatusCode.Conflict, "UploadInProgress");
        }

        blocking.Release();
        using (HttpResponseMessage first = await firstTask)
            Equal(HttpStatusCode.OK, first.StatusCode, "L'upload detenant le verrou doit finir normalement.");
        using (HttpResponseMessage after = await SendUploadAsync(
            harness.Client, harness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1))
            Equal(HttpStatusCode.OK, after.StatusCode, "Une nouvelle mutation doit fonctionner apres liberation.");

        string cancellationRoot = NewTemporaryRoot();
        roots.Add(cancellationRoot);
        using SkiaAvatarImageProcessor cancellationInner = new();
        BlockingAvatarImageProcessor cancellationBlocking = new(cancellationInner);
        await using AvatarHttpHarness cancellationHarness = await AvatarHttpHarness.CreateAsync(
            options,
            cancellationRoot,
            "lock-cancellation",
            processor: cancellationBlocking);
        using CancellationTokenSource cancellation = new();
        Task<HttpResponseMessage> cancelledRequest = SendUploadAsync(
            cancellationHarness.Client,
            cancellationHarness.AccessToken,
            png,
            "image/png",
            "avatar.png",
            0,
            0,
            1,
            cancellation.Token);
        await cancellationBlocking.WaitUntilEnteredAsync();
        cancellation.Cancel();
        try
        {
            using HttpResponseMessage ignored = await cancelledRequest;
        }
        catch (OperationCanceledException)
        {
        }
        await AssertAvatarLockEventuallyFreeAsync(
            options.ConnectionString,
            cancellationHarness.AccountId,
            "Une annulation HTTP doit libérer le verrou de mutation.");
    }

    private static async Task ValidateFailureAtomicityAsync(
        LauncherServerOptions options,
        ICollection<string> roots)
    {
        byte[] png = CreateImage(SKEncodedImageFormat.Png, 512, 512);

        string variantRoot = NewTemporaryRoot();
        roots.Add(variantRoot);
        await using (AvatarHttpHarness variantHarness = await AvatarHttpHarness.CreateAsync(
            options,
            variantRoot,
            "variant-failure",
            wrapStorage: storage => new FaultingAvatarStorage(storage) { FailVariantSize = 64 }))
        {
            using HttpResponseMessage failed = await SendUploadAsync(
                variantHarness.Client, variantHarness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1);
            await AssertApiErrorAsync(failed, HttpStatusCode.ServiceUnavailable, "StorageFailed");
            await AssertAvatarLockEventuallyFreeAsync(
                options.ConnectionString,
                variantHarness.AccountId,
                "Une exception de stockage doit libérer le verrou de mutation.");
            True(await variantHarness.Repository.GetActiveDescriptorAsync(
                    variantHarness.AccountId, CancellationToken.None) is null,
                "Une erreur de variante ne doit jamais activer un asset incomplet.");
            Equal(0, (await variantHarness.Storage.InspectAsync(CancellationToken.None)).Staging.Count,
                "L'echec pendant 64.png doit nettoyer l'original et le staging.");
        }

        string publishRoot = NewTemporaryRoot();
        roots.Add(publishRoot);
        await using (AvatarHttpHarness publishHarness = await AvatarHttpHarness.CreateAsync(
            options,
            publishRoot,
            "publish-failure",
            wrapStorage: storage => new FaultingAvatarStorage(storage) { FailPublish = true }))
        {
            using HttpResponseMessage failed = await SendUploadAsync(
                publishHarness.Client, publishHarness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1);
            await AssertApiErrorAsync(failed, HttpStatusCode.ServiceUnavailable, "StorageFailed");
            True((await publishHarness.Storage.InspectAsync(CancellationToken.None)).Published.Count == 0,
                "Une erreur avant publication ne doit laisser aucun dossier final.");
        }

        string databaseRoot = NewTemporaryRoot();
        roots.Add(databaseRoot);
        DatabaseFailureAvatarRepository? failureRepository = null;
        await using (AvatarHttpHarness databaseHarness = await AvatarHttpHarness.CreateAsync(
            options,
            databaseRoot,
            "database-failure",
            wrapRepository: repository => failureRepository = new DatabaseFailureAvatarRepository(
                repository,
                options.ConnectionString)))
        {
            using HttpResponseMessage initial = await SendUploadAsync(
                databaseHarness.Client, databaseHarness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1);
            Equal(HttpStatusCode.OK, initial.StatusCode, "La photo de reference doit etre active.");
            AvatarDescriptor active = await ReadRequiredAsync<AvatarDescriptor>(initial);
            failureRepository!.FailNextPublication = true;
            using HttpResponseMessage failed = await SendUploadAsync(
                databaseHarness.Client, databaseHarness.AccessToken, png, "image/png", "avatar.png", 0, 0, 1);
            await AssertApiErrorAsync(failed, HttpStatusCode.ServiceUnavailable, "ProcessingFailed");
            Equal(active,
                await databaseHarness.Repository.GetActiveDescriptorAsync(
                    databaseHarness.AccountId, CancellationToken.None),
                "Un echec de transaction DB doit conserver l'ancien avatar actif.");
            AvatarCleanupPlan plan = await new AvatarCleanupInspector(
                    databaseHarness.Repository,
                    databaseHarness.Storage)
                .InspectAsync(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));
            True(plan.AbandonedPending.Count >= 1,
                "L'asset Pending apres panne DB doit etre detectable.");
            True(plan.OrphanedMedia.Count >= 1,
                "Le dossier publie avant la panne DB doit etre detectable comme orphelin.");
            True(plan.PurgeableAssets.All(item => item.Id != active.AvatarId),
                "Le cleanup ne doit jamais proposer l'avatar Ready actif.");
        }
    }

    private static async Task ValidateIpRateLimitAsync(
        LauncherServerOptions options,
        ICollection<string> roots)
    {
        string root = NewTemporaryRoot();
        roots.Add(root);
        await using AvatarHttpHarness harness = await AvatarHttpHarness.CreateAsync(options, root, "ip-rate");
        for (int index = 0; index < 30; index++)
        {
            using HttpRequestMessage request = Authorized(
                HttpMethod.Delete, "/api/v1/me/avatar/photo", harness.AccessToken);
            using HttpResponseMessage response = await harness.Client.SendAsync(request);
            Equal(HttpStatusCode.NoContent, response.StatusCode, "Les trente premieres mutations IP doivent passer.");
        }
        using HttpRequestMessage limitedRequest = Authorized(
            HttpMethod.Delete, "/api/v1/me/avatar/photo", harness.AccessToken);
        using HttpResponseMessage limited = await harness.Client.SendAsync(limitedRequest);
        Equal(HttpStatusCode.TooManyRequests, limited.StatusCode, "La protection IP doit refuser sans file d'attente.");
    }

    private static async Task ValidateProfileContractAsync(
        AvatarHttpHarness harness,
        AvatarDescriptor expected,
        string? expectedLegacyAvatarKey)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Get, "/api/v1/me", harness.AccessToken);
        using HttpResponseMessage response = await harness.Client.SendAsync(request);
        Equal(HttpStatusCode.OK, response.StatusCode, "Le profil courant doit rester accessible.");
        string json = await response.Content.ReadAsStringAsync();
        AccountProfile profile = JsonSerializer.Deserialize<AccountProfile>(json, WebJson)
            ?? throw new InvalidOperationException("Profil moderne indecodable.");
        Equal(expected, profile.Avatar, "Le descripteur photo doit etre ajoute au profil courant.");
        Equal(expectedLegacyAvatarKey, profile.AvatarKey, "avatar_key doit rester compatible.");
        LegacyProfileContract legacy = JsonSerializer.Deserialize<LegacyProfileContract>(json, WebJson)
            ?? throw new InvalidOperationException("Contrat legacy indecodable.");
        Equal(profile.Username, legacy.Username, "Un consommateur legacy doit ignorer le nouveau champ nullable.");
        True(!json.Contains("storageKey", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("/srv/", StringComparison.OrdinalIgnoreCase),
            "Le profil public ne doit exposer ni cle ni chemin de stockage.");
    }

    private static async Task ValidateMediaContractAsync(
        AvatarHttpHarness harness,
        AvatarDescriptor descriptor)
    {
        using (HttpResponseMessage unauthorized = await harness.Client.GetAsync(descriptor.Url64))
            Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode, "Le media doit etre authentifie.");

        foreach ((int size, string url) in new[]
        {
            (32, descriptor.Url32), (64, descriptor.Url64),
            (128, descriptor.Url128), (256, descriptor.Url256)
        })
        {
            using HttpRequestMessage request = Authorized(HttpMethod.Get, url, harness.AccessToken);
            using HttpResponseMessage response = await harness.Client.SendAsync(request);
            Equal(HttpStatusCode.OK, response.StatusCode, $"La variante {size}px doit etre servie.");
            Equal("image/png", response.Content.Headers.ContentType?.MediaType, "Le media doit etre PNG.");
            True(response.Headers.CacheControl is { Private: true }
                && response.Headers.CacheControl.Extensions.Any(item => item.Name == "immutable")
                && response.Headers.CacheControl.MaxAge == TimeSpan.FromDays(365),
                "Le cache media doit etre prive, annuel et immuable.");
            True(response.Headers.TryGetValues("X-Content-Type-Options", out IEnumerable<string>? nosniff)
                && nosniff.Single() == "nosniff", "Le header nosniff est obligatoire.");
            EntityTagHeaderValue etag = response.Headers.ETag
                ?? throw new InvalidOperationException("ETag media absent.");
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            Equal('"' + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + '"',
                etag.Tag, "L'ETag doit etre le SHA-256 exact de la variante.");
            AvatarMediaRecord stored = await harness.Repository.GetMediaAsync(
                descriptor.AvatarId,
                descriptor.Version,
                size,
                CancellationToken.None)
                ?? throw new InvalidOperationException("Metadonnee media absente.");
            await using (Stream storedStream = await harness.Storage.OpenVariantReadAsync(
                stored.StorageKey,
                size,
                CancellationToken.None))
            using (MemoryStream storedBytes = new())
            {
                await storedStream.CopyToAsync(storedBytes);
                True(bytes.SequenceEqual(storedBytes.ToArray()),
                    "La route media doit renvoyer exactement les octets de la variante stockee.");
            }
            using SKBitmap bitmap = SKBitmap.Decode(bytes)
                ?? throw new InvalidOperationException("PNG HTTP indecodable.");
            Equal(size, bitmap.Width, "Le serveur a renvoye la mauvaise variante.");

            using HttpRequestMessage conditional = Authorized(HttpMethod.Get, url, harness.AccessToken);
            conditional.Headers.IfNoneMatch.Add(etag);
            using HttpResponseMessage notModified = await harness.Client.SendAsync(conditional);
            Equal(HttpStatusCode.NotModified, notModified.StatusCode, "If-None-Match doit produire 304.");
            Equal(0L, (await notModified.Content.ReadAsByteArrayAsync()).LongLength, "Une reponse 304 ne doit pas contenir de corps.");
        }

        await AssertMediaNotFoundAsync(harness,
            $"/media/avatars/{descriptor.AvatarId:N}/{descriptor.Version}/512.png");
        await AssertMediaNotFoundAsync(harness,
            $"/media/avatars/{descriptor.AvatarId:N}/{descriptor.Version + 1}/64.png");
        await AssertMediaNotFoundAsync(harness,
            $"/media/avatars/{Guid.NewGuid():N}/1/64.png");
    }

    private static async Task AssertMediaNotFoundAsync(AvatarHttpHarness harness, string url)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Get, url, harness.AccessToken);
        using HttpResponseMessage response = await harness.Client.SendAsync(request);
        Equal(HttpStatusCode.NotFound, response.StatusCode, "Un media absent, non Ready ou invalide doit produire 404.");
    }

    private static async Task<AccountProfile> GetProfileAsync(AvatarHttpHarness harness)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Get, "/api/v1/me", harness.AccessToken);
        using HttpResponseMessage response = await harness.Client.SendAsync(request);
        return await ReadRequiredAsync<AccountProfile>(response);
    }

    private static async Task<HttpResponseMessage> SendUploadAsync(
        HttpClient client,
        string? token,
        byte[] bytes,
        string contentType,
        string fileName,
        double cropX,
        double cropY,
        double cropSize,
        CancellationToken cancellationToken = default)
    {
        MultipartFormDataContent multipart = new("atlas-http-" + Guid.NewGuid().ToString("N"));
        ByteArrayContent image = new(bytes);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        multipart.Add(image, "image", fileName);
        multipart.Add(new StringContent(cropX.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropX");
        multipart.Add(new StringContent(cropY.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropY");
        multipart.Add(new StringContent(cropSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropSize");
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/me/avatar/photo") { Content = multipart };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task AssertAvatarLockEventuallyFreeAsync(
        string connectionString,
        uint accountId,
        string message)
    {
        string lockName = GetAvatarLockName(connectionString, accountId);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ScalarInt64Async(
                    connection,
                    "SELECT IS_FREE_LOCK(@name)",
                    ("@name", lockName)) == 1)
            {
                return;
            }
            await Task.Delay(40);
        }
        throw new InvalidOperationException(message);
    }

    private static string GetAvatarLockName(string connectionString, uint accountId)
    {
        MySqlConnectionStringBuilder builder = new(connectionString);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.Database));
        return $"atlas_avatar:{Convert.ToHexString(hash.AsSpan(0, 8))}:{accountId}";
    }

    private static async Task<long> ScalarInt64Async(
        MySqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task AssertApiErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Equal(expectedStatus, response.StatusCode, $"Statut API incorrect pour {expectedCode}.");
        AvatarApiError error = await ReadRequiredAsync<AvatarApiError>(response);
        Equal(expectedCode, error.Code, "Categorie API instable.");
        True(error.OperationId.Length == 32, "Une erreur avatar doit fournir un OperationId opaque.");
        string json = await response.Content.ReadAsStringAsync();
        True(!json.Contains("MySql", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("/srv/", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("storage_key", StringComparison.OrdinalIgnoreCase),
            "Une erreur API ne doit exposer aucun detail technique.");
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<T>(WebJson)
            ?? throw new InvalidOperationException($"Reponse {typeof(T).Name} absente.");

    private static LauncherDatabase CreateDatabase(LauncherServerOptions options)
        => new(options, new TokenService(), new LauncherSchemaMigrator(options));

    private static async Task<AuthResponse> RegisterTestAccountAsync(
        LauncherDatabase database,
        string prefix)
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        return await database.RegisterAsync(
            new RegisterRequest($"{prefix}_{suffix}"[..Math.Min(20, prefix.Length + 11)], $"{prefix}-{suffix}@example.test", "Atlas-avatar-test-2026"),
            "03A.2b integration",
            CancellationToken.None);
    }

    private static async Task InsertUploadAttemptsAsync(
        string connectionString,
        uint accountId,
        int count,
        int minutesAgo)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO atlas_launcher_avatar_upload_attempt (account_id, attempted_at)
            VALUES (@accountId, UTC_TIMESTAMP(6) - INTERVAL @minutes MINUTE)
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@minutes", minutesAgo);
        for (int index = 0; index < count; index++)
            await command.ExecuteNonQueryAsync();
    }

    private static async Task ResetAvatarSchemaAsync(string connectionString)
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

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record LegacyProfileContract(
        uint AccountId,
        string Username,
        string Email,
        bool EmailVerified,
        string? AvatarKey,
        bool TwoFactorEnabled,
        bool RecoveryCodesGenerated,
        int Completion);

    private sealed class FixedAvatarMigrationSource(IReadOnlyList<LauncherSchemaMigration> migrations)
        : ILauncherSchemaMigrationSource
    {
        public IReadOnlyList<LauncherSchemaMigration> Load() => migrations;
    }

    private sealed class DatabaseFailureAvatarRepository(
        IAvatarRepository inner,
        string connectionString) : IAvatarRepository
    {
        internal bool FailNextPublication { get; set; }

        public Task<AvatarRateLimitDecision> TryConsumeUploadPermitAsync(uint accountId, CancellationToken cancellationToken)
            => inner.TryConsumeUploadPermitAsync(accountId, cancellationToken);
        public Task<AvatarAssetRecord> CreatePendingAsync(uint accountId, CancellationToken cancellationToken)
            => inner.CreatePendingAsync(accountId, cancellationToken);
        public async Task<AvatarPublicationResult> PublishReadyAsync(
            uint accountId,
            AvatarAssetRecord pending,
            IReadOnlyList<AvatarStoredVariant> variants,
            CancellationToken cancellationToken)
        {
            if (FailNextPublication)
            {
                FailNextPublication = false;
                await using MySqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using MySqlCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO atlas_launcher_avatar_variant
                        (avatar_asset_id, size, content_type, byte_length, sha256)
                    VALUES (@id, 32, 'image/png', 1, @sha256)
                    """;
                command.Parameters.Add("@id", MySqlDbType.Binary, 16).Value = pending.Id.ToByteArray(bigEndian: true);
                command.Parameters.Add("@sha256", MySqlDbType.Binary, 32).Value = new byte[32];
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            return await inner.PublishReadyAsync(accountId, pending, variants, cancellationToken);
        }
        public Task MarkPendingDeletedAsync(uint accountId, Guid avatarId, CancellationToken cancellationToken)
            => inner.MarkPendingDeletedAsync(accountId, avatarId, cancellationToken);
        public Task<AvatarDeletionResult> DeleteActiveAsync(uint accountId, CancellationToken cancellationToken)
            => inner.DeleteActiveAsync(accountId, cancellationToken);
        public Task<AvatarDescriptor?> GetActiveDescriptorAsync(uint accountId, CancellationToken cancellationToken)
            => inner.GetActiveDescriptorAsync(accountId, cancellationToken);
        public Task<AvatarMediaRecord?> GetMediaAsync(Guid avatarId, ulong version, int size, CancellationToken cancellationToken)
            => inner.GetMediaAsync(avatarId, version, size, cancellationToken);
        public Task<AvatarRepositoryInventory> InspectAsync(CancellationToken cancellationToken)
            => inner.InspectAsync(cancellationToken);
    }

    private sealed class AvatarHttpHarness : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly IDisposable? _externalProcessor;

        private AvatarHttpHarness(
            WebApplication application,
            HttpClient client,
            LauncherDatabase database,
            IAvatarRepository repository,
            IAvatarStorage storage,
            AuthResponse auth,
            IDisposable? externalProcessor)
        {
            _application = application;
            Client = client;
            Database = database;
            Repository = repository;
            Storage = storage;
            AccessToken = auth.AccessToken;
            AccountId = auth.Profile.AccountId;
            _externalProcessor = externalProcessor;
        }

        internal HttpClient Client { get; }
        internal LauncherDatabase Database { get; }
        internal IAvatarRepository Repository { get; }
        internal IAvatarStorage Storage { get; }
        internal string AccessToken { get; }
        internal uint AccountId { get; }

        internal static async Task<AvatarHttpHarness> CreateAsync(
            LauncherServerOptions baseOptions,
            string storageRoot,
            string accountPrefix,
            Func<IAvatarStorage, IAvatarStorage>? wrapStorage = null,
            Func<IAvatarRepository, IAvatarRepository>? wrapRepository = null,
            IAvatarImageProcessor? processor = null)
        {
            LauncherServerOptions options = new()
            {
                ConnectionString = baseOptions.ConnectionString,
                AvatarMediaRoot = storageRoot
            };
            LauncherDatabase database = CreateDatabase(options);
            AuthResponse auth = await RegisterTestAccountAsync(database, accountPrefix);
            IAvatarStorage storage = wrapStorage?.Invoke(new LocalAvatarStorage(storageRoot))
                ?? new LocalAvatarStorage(storageRoot);
            IAvatarRepository repository = wrapRepository?.Invoke(new AvatarRepository(options))
                ?? new AvatarRepository(options);
            IAvatarImageProcessor imageProcessor = processor ?? new SkiaAvatarImageProcessor();

            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(AvatarBackendTests).Assembly.FullName
            });
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.AddLogging();
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(database);
            builder.Services.AddSingleton<IAvatarStorage>(storage);
            builder.Services.AddSingleton<IAvatarRepository>(repository);
            builder.Services.AddSingleton<IAvatarMutationLockProvider>(_ => new AvatarMutationLockProvider(options));
            builder.Services.AddSingleton<IAvatarImageProcessor>(imageProcessor);
            builder.Services.AddSingleton(services => new AvatarMultipartUploadReader(
                services.GetRequiredService<IAvatarStorage>()));
            builder.Services.AddSingleton(services => new AvatarApplicationService(
                services.GetRequiredService<IAvatarRepository>(),
                services.GetRequiredService<IAvatarMutationLockProvider>(),
                services.GetRequiredService<IAvatarStorage>(),
                services.GetRequiredService<IAvatarImageProcessor>(),
                services.GetRequiredService<AvatarMultipartUploadReader>(),
                services.GetRequiredService<ILogger<AvatarApplicationService>>()));
            builder.Services.AddSingleton(services => new AvatarCleanupInspector(
                services.GetRequiredService<IAvatarRepository>(),
                services.GetRequiredService<IAvatarStorage>()));
            builder.Services.AddRateLimiter(rateLimiter =>
            {
                rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                rateLimiter.AddAtlasAvatarIpRateLimit();
            });

            WebApplication app = builder.Build();
            app.UseRateLimiter();
            app.MapAtlasAvatarEndpoints();
            app.MapGet("/api/v1/me", async (
                HttpContext context,
                LauncherDatabase db,
                CancellationToken cancellationToken) =>
            {
                AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(
                    context, db, cancellationToken);
                return account is null
                    ? Results.Unauthorized()
                    : Results.Ok(await db.GetProfileAsync(account.AccountId, cancellationToken));
            });
            await app.StartAsync();
            IServer server = app.Services.GetRequiredService<IServer>();
            string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Adresse HTTP de test introuvable.");
            HttpClient client = new() { BaseAddress = new Uri(address) };
            return new AvatarHttpHarness(
                app,
                client,
                database,
                repository,
                storage,
                auth,
                processor as IDisposable);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
            _externalProcessor?.Dispose();
        }
    }
}
