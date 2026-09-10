using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using WotLK.Launcher;
using WotLK.Launcher.Game;

internal static class GameInstallFileSystemSecurityTests
{
    internal static async Task<int> RunAsync()
    {
        RootLeaseLocksIdentityAndOwnershipMarker();
        OwnershipMarkerHardLinkSubstitutionIsBlocked();
        OwnershipMarkerConcurrentCreationReturnsExisting();
        ManifestAndConfigHardLinksAreRefused();
        RetiredAddonJunctionIsRefused("UnBot");
        RetiredAddonJunctionIsRefused("MultiBot");
        CleanupSkipsAlreadyMissingDirectoryTrees();
        AtomicWriteFailurePreservesSubstitutedHardLink();
        await TransferAndCleanupHardLinksAreRefusedBeforeMutationAsync();
        await DownloadedFileOrdinarySubstitutionIsBlockedAsync();
        await DownloadedFileHardLinkSubstitutionIsBlockedAsync();
        await GameTransferDirectoryStaysLockedAcrossDownloadAsync();
        await AddonsDirectoryStaysLockedAcrossDownloadAsync();
        await AddonStateHardLinkIsRefusedBeforeNetworkAsync();
        Console.WriteLine("Game install filesystem lease security OK.");
        return 0;
    }

    private static void CleanupSkipsAlreadyMissingDirectoryTrees()
    {
        string testRoot = NewNonSensitiveTestRoot("cleanup-missing-parent");
        string installRoot = Path.Combine(testRoot, "client");

        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            GameFileVerifier verifier = new(
                new InstalledManifestStore(_ => true),
                new GameClientStateReader(_ => false),
                _ => false);
            GameFileCleanupService cleanup = new(verifier);

            int deleted = cleanup.DeleteRemovedFiles(
                installRoot,
                ["Data/already-gone/client.bin"],
                CancellationToken.None,
                lease);

            Assert(deleted == 0,
                "Un ancien fichier dont tout le dossier a disparu doit rester un no-op sûr.");
            lease.Revalidate();
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static void OwnershipMarkerHardLinkSubstitutionIsBlocked()
    {
        string testRoot = NewNonSensitiveTestRoot("ownership-hardlink-swap");
        string installRoot = Path.Combine(testRoot, "client");
        string markerPath = Path.Combine(
            installRoot,
            GameInstallServices.InstallRootOwnershipMarkerFileName);
        string victim = Path.Combine(testRoot, "outside-ownership-victim.json");
        const string sentinel = "keep-ownership-victim";
        bool substitutionBlocked = false;

        Directory.CreateDirectory(installRoot);
        File.WriteAllText(victim, sentinel, Encoding.UTF8);
        File.SetAttributes(victim, File.GetAttributes(victim) | FileAttributes.Hidden);
        try
        {
            GameInstallRootOwnership written = GameInstallServices.WriteInstallRootOwnership(
                installRoot,
                markerPath,
                temporary =>
                {
                    try
                    {
                        File.Delete(temporary);
                        CreateHardLink(temporary, victim);
                    }
                    catch (Exception exception) when (exception is IOException
                                                       or UnauthorizedAccessException)
                    {
                        substitutionBlocked = true;
                    }
                });

            Assert(substitutionBlocked,
                "Le handle du marqueur de propriété doit bloquer sa substitution hardlink.");
            Assert(GameInstallServices.TryReadInstallRootOwnership(
                    installRoot,
                    out GameInstallRootOwnership? reread)
                   && reread.OwnershipId == written.OwnershipId,
                "Le marqueur renommé par handle doit conserver l’identité générée.");
            Equal(sentinel, File.ReadAllText(victim, Encoding.UTF8),
                "La substitution du marqueur doit préserver le contenu extérieur.");
            Assert((File.GetAttributes(victim) & FileAttributes.Hidden) != 0,
                "La substitution du marqueur ne doit pas normaliser les attributs extérieurs.");
        }
        finally
        {
            if (File.Exists(victim))
            {
                File.SetAttributes(victim, FileAttributes.Normal);
            }

            DeleteTestRoot(testRoot);
        }
    }

    private static void OwnershipMarkerConcurrentCreationReturnsExisting()
    {
        string testRoot = NewNonSensitiveTestRoot("ownership-concurrent-create");
        string installRoot = Path.Combine(testRoot, "client");
        string markerPath = Path.Combine(
            installRoot,
            GameInstallServices.InstallRootOwnershipMarkerFileName);
        GameInstallRootOwnership? concurrent = null;

        Directory.CreateDirectory(installRoot);
        try
        {
            GameInstallRootOwnership observed = GameInstallServices.WriteInstallRootOwnership(
                installRoot,
                markerPath,
                _ => concurrent = GameInstallServices.WriteInstallRootOwnership(
                    installRoot,
                    markerPath));

            Assert(concurrent is not null,
                "Le test doit créer un marqueur concurrent légitime.");
            GameInstallRootOwnership published = concurrent
                ?? throw new InvalidOperationException(
                    "Le marqueur concurrent légitime est absent.");
            Assert(observed.OwnershipId == published.OwnershipId,
                "Le perdant de la création concurrente doit reprendre l’identité déjà publiée.");
            Assert(GameInstallServices.TryReadInstallRootOwnership(
                    installRoot,
                    out GameInstallRootOwnership? reread)
                   && reread.OwnershipId == published.OwnershipId,
                "Le marqueur concurrent publié doit rester valide.");
            Assert(!Directory.GetFiles(
                    installRoot,
                    GameInstallServices.InstallRootOwnershipMarkerFileName + ".new-*.tmp",
                    SearchOption.TopDirectoryOnly).Any(),
                "Le temporaire perdant doit être supprimé via son handle.");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static void AtomicWriteFailurePreservesSubstitutedHardLink()
    {
        string testRoot = NewNonSensitiveTestRoot("atomic-cleanup-hardlink");
        string installRoot = Path.Combine(testRoot, "client");
        string victim = Path.Combine(testRoot, "outside-atomic-victim.txt");
        string target = Path.Combine(installRoot, "atomic-target.txt");
        string? temporary = null;
        const string sentinel = "keep-atomic-cleanup";

        Directory.CreateDirectory(testRoot);
        File.WriteAllText(victim, sentinel, Encoding.UTF8);
        File.SetAttributes(victim, File.GetAttributes(victim) | FileAttributes.Hidden);
        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);

            Throws<InvalidOperationException>(
                () => lease.WriteFileAtomically(target, stream =>
                {
                    stream.WriteByte(1);
                    temporary = Directory.GetFiles(
                            installRoot,
                            Path.GetFileName(target) + ".new-*.tmp",
                            SearchOption.TopDirectoryOnly)
                        .Single();
                    stream.Dispose();
                    File.Delete(temporary);
                    CreateHardLink(temporary, victim);
                    throw new InvalidOperationException("Échec d’écriture atomique simulé.");
                }),
                "L’échec du writer doit rester l’erreur observée après le nettoyage.");

            Assert(temporary is not null && File.Exists(temporary),
                "Le nettoyage doit conserver un temporaire substitué par hardlink.");
            Equal(sentinel, File.ReadAllText(victim, Encoding.UTF8),
                "Le nettoyage atomique doit préserver le contenu de la cible extérieure.");
            Assert((File.GetAttributes(victim) & FileAttributes.Hidden) != 0,
                "Le nettoyage atomique ne doit pas normaliser les attributs de la cible extérieure.");
            lease.Revalidate();
        }
        finally
        {
            if (File.Exists(victim))
            {
                File.SetAttributes(victim, FileAttributes.Normal);
            }

            DeleteTestRoot(testRoot);
        }
    }

    private static Task DownloadedFileOrdinarySubstitutionIsBlockedAsync()
        => DownloadedFileSubstitutionIsBlockedAsync(useHardLink: false);

    private static Task DownloadedFileHardLinkSubstitutionIsBlockedAsync()
        => DownloadedFileSubstitutionIsBlockedAsync(useHardLink: true);

    private static async Task DownloadedFileSubstitutionIsBlockedAsync(bool useHardLink)
    {
        string scenario = useHardLink ? "download-hardlink-swap" : "download-file-swap";
        string testRoot = NewNonSensitiveTestRoot(scenario);
        string installRoot = Path.Combine(testRoot, "client");
        string target = Path.Combine(installRoot, "Data", "client.bin");
        string victim = Path.Combine(testRoot, "outside-download-victim.bin");
        byte[] trustedPayload = Encoding.UTF8.GetBytes("trusted-downloaded-payload");
        const string victimSentinel = "keep-download-victim";
        bool substitutionAttempted = false;
        bool substitutionBlocked = false;

        try
        {
            Directory.CreateDirectory(testRoot);
            File.WriteAllText(victim, victimSentinel, Encoding.UTF8);
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            lease.WriteFileAtomically(
                target,
                stream => stream.Write(Encoding.UTF8.GetBytes("old-target")));

            using BlockingBytesHandler handler = new(trustedPayload);
            handler.Release.TrySetResult();
            using HttpClient http = new(handler);
            GameFileTransferService transfer = new(http);
            await transfer.DownloadAsync(
                77,
                new Uri("https://animeclub.fr/client.bin"),
                target,
                trustedPayload.Length,
                Convert.ToHexString(SHA256.HashData(trustedPayload)),
                progress =>
                {
                    if (progress.Stage != GameFileTransferStage.Applying
                        || substitutionAttempted)
                    {
                        return;
                    }

                    substitutionAttempted = true;
                    string targetDirectory = Path.GetDirectoryName(target)!;
                    string temporary = Directory.GetFiles(
                            targetDirectory,
                            "." + Path.GetFileName(target) + ".*.download",
                            SearchOption.TopDirectoryOnly)
                        .Single();
                    try
                    {
                        File.Delete(temporary);
                        if (useHardLink)
                        {
                            CreateHardLink(temporary, victim);
                        }
                        else
                        {
                            File.WriteAllText(temporary, "substituted-payload", Encoding.UTF8);
                        }
                    }
                    catch (Exception exception) when (exception is IOException
                                                       or UnauthorizedAccessException)
                    {
                        substitutionBlocked = true;
                    }
                },
                CancellationToken.None,
                lease);

            Assert(substitutionAttempted,
                "Le test doit tenter la substitution après le contrôle du hash.");
            Assert(substitutionBlocked,
                "Le handle du téléchargement vérifié doit bloquer la substitution du temporaire.");
            Equal(
                Encoding.UTF8.GetString(trustedPayload),
                File.ReadAllText(target, Encoding.UTF8),
                "Seul le contenu téléchargé et vérifié doit atteindre la destination.");
            Equal(victimSentinel, File.ReadAllText(victim, Encoding.UTF8),
                "La tentative de substitution hardlink doit préserver la cible extérieure.");
            Assert(!Directory.GetFiles(
                    Path.GetDirectoryName(target)!,
                    "." + Path.GetFileName(target) + ".*.download",
                    SearchOption.TopDirectoryOnly).Any(),
                "Le renommage par handle doit consommer le temporaire vérifié.");
            lease.Revalidate();
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task TransferAndCleanupHardLinksAreRefusedBeforeMutationAsync()
    {
        string testRoot = NewNonSensitiveTestRoot("transfer-cleanup-hardlinks");
        string installRoot = Path.Combine(testRoot, "client");
        string victim = Path.Combine(testRoot, "outside-client.bin");
        const string sentinel = "keep-transfer-cleanup";

        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            string target = Path.Combine(installRoot, "Data", "client.bin");
            using (lease.AcquireDirectory(
                       Path.GetDirectoryName(target)!,
                       createIfMissing: true))
            {
            }

            File.WriteAllText(victim, sentinel, Encoding.UTF8);
            CreateHardLink(target, victim);

            byte[] payload = Encoding.UTF8.GetBytes("replacement");
            using CountingFailureHandler handler = new();
            using HttpClient http = new(handler);
            GameFileTransferService transfer = new(http);
            await ThrowsAsync<InvalidDataException>(
                () => transfer.DownloadAsync(
                    43,
                    new Uri("https://animeclub.fr/client.bin"),
                    target,
                    payload.Length,
                    Convert.ToHexString(SHA256.HashData(payload)),
                    null,
                    CancellationToken.None,
                    lease),
                "Un transfert doit refuser une destination hardlink avant le réseau.");
            Assert(handler.Calls == 0,
                "Une destination de transfert liée doit échouer avant toute requête réseau.");
            Equal(sentinel, File.ReadAllText(victim, Encoding.UTF8),
                "Le transfert refusé doit préserver la cible extérieure.");

            GameFileVerifier verifier = new(
                new InstalledManifestStore(_ => true),
                new GameClientStateReader(_ => false),
                _ => false);
            GameFileCleanupService cleanup = new(verifier);
            Throws<InvalidDataException>(
                () => cleanup.DeleteRemovedFiles(
                    installRoot,
                    ["Data/client.bin"],
                    CancellationToken.None,
                    lease),
                "Le nettoyage doit refuser un fichier obsolète hardlink.");
            Equal(sentinel, File.ReadAllText(victim, Encoding.UTF8),
                "Le nettoyage refusé doit préserver la cible extérieure.");
            File.Delete(target);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task GameTransferDirectoryStaysLockedAcrossDownloadAsync()
    {
        string testRoot = NewNonSensitiveTestRoot("transfer-wait");
        string installRoot = Path.Combine(testRoot, "client");
        string victim = Path.Combine(testRoot, "transfer-victim");
        string victimSentinel = Path.Combine(victim, "sentinel.txt");
        byte[] payload = Encoding.UTF8.GetBytes("verified-client-file");
        Directory.CreateDirectory(victim);
        File.WriteAllText(victimSentinel, "keep-transfer-race", Encoding.UTF8);

        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            using BlockingBytesHandler handler = new(payload);
            using HttpClient http = new(handler);
            GameFileTransferService transfer = new(
                http,
                new GameFileTransferRetryPolicy(
                    ReplacementAttempts: 1,
                    ReplacementDelay: TimeSpan.Zero));
            string target = Path.Combine(installRoot, "Data", "client.bin");
            Task download = transfer.DownloadAsync(
                42,
                new Uri("https://animeclub.fr/client.bin"),
                target,
                payload.Length,
                Convert.ToHexString(SHA256.HashData(payload)),
                null,
                CancellationToken.None,
                lease);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            string targetDirectory = Path.GetDirectoryName(target)!;
            AssertJunctionSwapBlocked(
                targetDirectory,
                targetDirectory + ".parked",
                victim,
                "Le dossier cible d'un transfert doit rester verrouillé pendant le réseau.");
            AssertJunctionSwapBlocked(
                installRoot,
                installRoot + ".parked",
                victim,
                "La racine d'un transfert doit rester verrouillée pendant le réseau.");
            Equal("keep-transfer-race", File.ReadAllText(victimSentinel, Encoding.UTF8),
                "Les tentatives de permutation du transfert doivent préserver la sentinelle extérieure.");

            handler.Release.TrySetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(File.ReadAllBytes(target).SequenceEqual(payload),
                "Le transfert doit appliquer exactement le contenu validé.");
            lease.Revalidate();
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static void RootLeaseLocksIdentityAndOwnershipMarker()
    {
        string testRoot = NewNonSensitiveTestRoot("root-identity");
        string installRoot = Path.Combine(testRoot, "client");
        string parkedRoot = Path.Combine(testRoot, "client-parked");
        string victim = Path.Combine(testRoot, "victim");
        string victimMarker = Path.Combine(victim, "ownership-copy.json");
        Directory.CreateDirectory(victim);
        File.WriteAllText(
            Path.Combine(victim, "sentinel.txt"),
            "keep-root-race",
            Encoding.UTF8);

        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            string marker = Path.Combine(
                installRoot,
                GameInstallServices.InstallRootOwnershipMarkerFileName);
            byte[] markerBytes = File.ReadAllBytes(marker);
            Assert(markerBytes.Length is > 0 and <= 8 * 1024,
                "La preuve de propriété créée sous lease doit rester bornée.");
            Assert(lease.Ownership.OwnershipId != Guid.Empty,
                "La preuve de propriété créée sous lease doit avoir une identité.");
            lease.Revalidate();

            AssertJunctionSwapBlocked(
                installRoot,
                parkedRoot,
                victim,
                "La racine WotLK ne doit pas pouvoir devenir une jonction pendant le lease.");
            Equal("keep-root-race", File.ReadAllText(
                    Path.Combine(victim, "sentinel.txt"),
                    Encoding.UTF8),
                "La tentative de permutation de racine doit préserver la sentinelle extérieure.");
            lease.Revalidate();

            File.WriteAllBytes(victimMarker, markerBytes);
            File.Delete(marker);
            CreateHardLink(marker, victimMarker);
            Throws<InvalidDataException>(
                lease.Revalidate,
                "La revalidation doit refuser une preuve de propriété devenue hardlink.");
            Assert(
                File.ReadAllBytes(victimMarker).SequenceEqual(markerBytes),
                "Le refus du hardlink de propriété doit préserver le fichier extérieur.");
            File.Delete(marker);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static void ManifestAndConfigHardLinksAreRefused()
    {
        string testRoot = NewNonSensitiveTestRoot("file-hardlinks");
        string installRoot = Path.Combine(testRoot, "client");
        string victim = Path.Combine(testRoot, "victim");
        Directory.CreateDirectory(victim);

        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            using (lease.AcquireDirectory(
                       Path.Combine(installRoot, "_classic_", "WTF"),
                       createIfMissing: true))
            {
            }

            string configVictim = Path.Combine(victim, "config-victim.txt");
            string configPath = Path.Combine(
                installRoot,
                "_classic_",
                "WTF",
                "Config.wtf");
            File.WriteAllText(configVictim, "outside-config", Encoding.UTF8);
            CreateHardLink(configPath, configVictim);
            Throws<InvalidDataException>(
                () => GameInstallServices.EnsureDefaultClientConfig(
                    installRoot,
                    "frFR",
                    lease),
                "La configuration doit refuser un hardlink avant écriture.");
            Equal("outside-config", File.ReadAllText(configVictim, Encoding.UTF8),
                "Le refus de Config.wtf doit préserver sa cible extérieure.");
            File.Delete(configPath);

            string videoVictim = Path.Combine(victim, "video-marker-victim.json");
            string videoMarkerPath = Path.Combine(
                installRoot,
                "_classic_",
                "WTF",
                "launcher-video-defaults.json");
            File.WriteAllText(videoVictim, "outside-video-marker", Encoding.UTF8);
            CreateHardLink(videoMarkerPath, videoVictim);
            Throws<InvalidDataException>(
                () => GameInstallServices.EnsureDefaultClientConfig(
                    installRoot,
                    "frFR",
                    lease),
                "Le marqueur vidéo doit refuser un hardlink avant écriture.");
            Equal("outside-video-marker", File.ReadAllText(videoVictim, Encoding.UTF8),
                "Le refus du marqueur vidéo doit préserver sa cible extérieure.");
            File.Delete(videoMarkerPath);

            string cacheVictim = Path.Combine(victim, "cache-victim.json");
            string cachePath = Path.Combine(
                installRoot,
                InstalledManifestStore.CacheFileName);
            File.WriteAllText(cacheVictim, "{}", Encoding.UTF8);
            CreateHardLink(cachePath, cacheVictim);
            InstalledManifestStore store = new(_ => true);
            Throws<InvalidDataException>(
                () => store.Load(installRoot, lease),
                "Le cache de manifeste doit refuser un hardlink avant parsing.");
            Equal("{}", File.ReadAllText(cacheVictim, Encoding.UTF8),
                "Le refus du cache doit préserver sa cible extérieure.");
            File.Delete(cachePath);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static void RetiredAddonJunctionIsRefused(string retiredFolder)
    {
        string testRoot = NewNonSensitiveTestRoot(
            "retired-addon-junction-" + retiredFolder.ToLowerInvariant());
        string installRoot = Path.Combine(testRoot, "client");
        string victim = Path.Combine(testRoot, "victim");
        string sentinel = Path.Combine(victim, "sentinel.txt");
        string junction = Path.Combine(
            installRoot,
            "Interface",
            "AddOns",
            retiredFolder,
            "escape");
        Directory.CreateDirectory(victim);
        string sentinelContents = "keep-" + retiredFolder.ToLowerInvariant();
        File.WriteAllText(sentinel, sentinelContents, Encoding.UTF8);

        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            using (lease.AcquireDirectory(
                       Path.GetDirectoryName(junction)!,
                       createIfMissing: true))
            {
            }

            CreateDirectoryJunction(junction, victim);
            GameFileVerifier verifier = new(
                new InstalledManifestStore(_ => true),
                new GameClientStateReader(_ => false),
                _ => false);
            LauncherManifest manifest = new()
            {
                Version = "security",
                BaseUrl = "https://animeclub.fr/client/",
                Files = []
            };
            Throws<InvalidDataException>(
                () => verifier.FindRemovedFiles(installRoot, manifest, lease),
                $"L'énumération {retiredFolder} doit refuser une jonction imbriquée.");
            Equal(sentinelContents, File.ReadAllText(sentinel, Encoding.UTF8),
                $"Le refus de {retiredFolder} doit préserver la sentinelle extérieure.");
        }
        finally
        {
            DeleteJunction(junction);
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task AddonsDirectoryStaysLockedAcrossDownloadAsync()
    {
        string testRoot = NewNonSensitiveTestRoot("addons-wait");
        string installRoot = Path.Combine(testRoot, "client");
        string parkedAddons = Path.Combine(testRoot, "addons-parked");
        string victim = Path.Combine(testRoot, "addons-victim");
        string victimSentinel = Path.Combine(victim, "sentinel.txt");
        Directory.CreateDirectory(victim);
        File.WriteAllText(victimSentinel, "keep-addons-race", Encoding.UTF8);

        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            lease.WriteFileAtomically(
                GameInstallServices.GetGameExecutablePath(installRoot),
                stream => stream.WriteByte(1));
            lease.WriteFileAtomically(
                GameInstallServices.GetGameLauncherPath(installRoot),
                stream => stream.WriteByte(2));

            byte[] archiveBytes = CreateAddonArchive();
            AddonPackage package = new()
            {
                Id = "lease-test",
                Name = "Lease Test",
                Description = "filesystem lease test",
                Category = "Tests",
                Version = "1.0.0",
                Interface = AddonInstallServices.SupportedInterface,
                Url = "https://animeclub.fr/lease-test.zip",
                Size = archiveBytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)),
                Folders = ["AtlasLeaseTest"]
            };
            AddonCatalog catalog = new()
            {
                SchemaVersion = 1,
                ClientInterface = AddonInstallServices.SupportedInterface,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Addons = [package]
            };
            using BlockingBytesHandler handler = new(archiveBytes);
            using HttpClient http = new(handler);
            Task apply = AddonInstallServices.ApplySelectionAsync(
                http,
                catalog,
                installRoot,
                new Dictionary<string, bool> { [package.Id] = true },
                progress: null,
                log: null,
                CancellationToken.None,
                rootLease: lease);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            string addonsDirectory = AddonInstallServices.GetAddonsDirectory(installRoot);
            Assert(Directory.Exists(addonsDirectory),
                "Le dossier AddOns doit être préparé avant l'attente réseau.");
            string[] workDirectories = Directory.GetDirectories(
                addonsDirectory,
                ".atlas-work-*",
                SearchOption.TopDirectoryOnly);
            Assert(workDirectories.Length == 1,
                "Une zone de travail addon unique doit exister dans la racine verrouillée.");
            string workDirectory = workDirectories[0];
            string extractionDirectory = Path.Combine(workDirectory, "extracted");
            Assert(Directory.Exists(extractionDirectory),
                "Le dossier d'extraction doit être préparé avant l'attente réseau.");
            AssertJunctionSwapBlocked(
                extractionDirectory,
                Path.Combine(workDirectory, "extracted-parked"),
                victim,
                "La zone d'extraction ne doit pas pouvoir être remplacée pendant le téléchargement.");
            AssertJunctionSwapBlocked(
                workDirectory,
                Path.Combine(addonsDirectory, ".atlas-work-parked"),
                victim,
                "La zone de travail ne doit pas pouvoir être remplacée pendant le téléchargement.");
            AssertJunctionSwapBlocked(
                addonsDirectory,
                parkedAddons,
                victim,
                "AddOns ne doit pas pouvoir être remplacé pendant le téléchargement.");
            AssertJunctionSwapBlocked(
                installRoot,
                installRoot + ".parked",
                victim,
                "La racine ne doit pas pouvoir être remplacée pendant le téléchargement.");
            Equal("keep-addons-race", File.ReadAllText(victimSentinel, Encoding.UTF8),
                "Les tentatives de permutation AddOns doivent préserver la sentinelle extérieure.");

            handler.Release.TrySetResult();
            await apply.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(File.Exists(Path.Combine(
                    addonsDirectory,
                    "AtlasLeaseTest",
                    "core.lua")),
                "L'installation doit aboutir après la levée de l'attente réseau.");
            Assert(!Directory.Exists(workDirectory),
                "La zone de travail verrouillée doit être supprimée après l'installation.");
            lease.Revalidate();
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task AddonStateHardLinkIsRefusedBeforeNetworkAsync()
    {
        string testRoot = NewNonSensitiveTestRoot("addon-state-hardlink");
        string installRoot = Path.Combine(testRoot, "client");
        string victim = Path.Combine(testRoot, "victim-state.json");

        try
        {
            using IGameInstallRootLease lease =
                GameInstallServices.AcquireGameInstallRootLease(
                    installRoot,
                    GameInstallRootLeaseMode.PrepareInstall);
            lease.WriteFileAtomically(
                GameInstallServices.GetGameExecutablePath(installRoot),
                stream => stream.WriteByte(1));
            lease.WriteFileAtomically(
                GameInstallServices.GetGameLauncherPath(installRoot),
                stream => stream.WriteByte(2));
            string addonsDirectory = AddonInstallServices.GetAddonsDirectory(installRoot);
            using (lease.AcquireDirectory(addonsDirectory, createIfMissing: true))
            {
            }

            const string victimContents = "{\"schemaVersion\":1,\"addons\":{}}";
            File.WriteAllText(victim, victimContents, Encoding.UTF8);
            string statePath = Path.Combine(addonsDirectory, ".atlas-addons.json");
            CreateHardLink(statePath, victim);

            byte[] archiveBytes = CreateAddonArchive();
            AddonPackage package = new()
            {
                Id = "state-test",
                Name = "State Test",
                Description = "state hardlink test",
                Category = "Tests",
                Version = "1.0.0",
                Interface = AddonInstallServices.SupportedInterface,
                Url = "https://animeclub.fr/state-test.zip",
                Size = archiveBytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)),
                Folders = ["AtlasLeaseTest"]
            };
            AddonCatalog catalog = new()
            {
                SchemaVersion = 1,
                ClientInterface = AddonInstallServices.SupportedInterface,
                Addons = [package]
            };
            using CountingFailureHandler handler = new();
            using HttpClient http = new(handler);
            await ThrowsAsync<InvalidDataException>(
                () => AddonInstallServices.ApplySelectionAsync(
                    http,
                    catalog,
                    installRoot,
                    new Dictionary<string, bool> { [package.Id] = true },
                    progress: null,
                    log: null,
                    CancellationToken.None,
                    rootLease: lease),
                "L'état addon lié doit être refusé avant le réseau.");
            Assert(handler.Calls == 0,
                "Un état addon lié doit échouer avant toute requête réseau.");
            Equal(victimContents, File.ReadAllText(victim, Encoding.UTF8),
                "Le refus de l'état addon doit préserver sa cible extérieure.");
            File.Delete(statePath);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static byte[] CreateAddonArchive()
    {
        using MemoryStream bytes = new();
        using (ZipArchive archive = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (StreamWriter toc = new(
                       archive.CreateEntry("AtlasLeaseTest/AtlasLeaseTest.toc").Open(),
                       new UTF8Encoding(false)))
            {
                toc.Write("## Interface: 30403\n## Title: Lease test\ncore.lua\n");
            }

            using StreamWriter lua = new(
                archive.CreateEntry("AtlasLeaseTest/core.lua").Open(),
                new UTF8Encoding(false));
            lua.Write("return true\n");
        }

        return bytes.ToArray();
    }

    private static string NewNonSensitiveTestRoot(string scenario)
    {
        string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory));
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new InvalidOperationException("Racine de volume de test introuvable.");
        }

        return Path.Combine(
            volumeRoot,
            "AtlasLauncherLeaseTest-" + scenario + "-" + Guid.NewGuid().ToString("N"));
    }

    private static void AssertJunctionSwapBlocked(
        string source,
        string parked,
        string victim,
        string message)
    {
        bool moved = false;
        bool junctionCreated = false;
        try
        {
            Directory.Move(source, parked);
            moved = true;
            CreateDirectoryJunction(source, victim);
            junctionCreated = true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException)
        {
            return;
        }
        finally
        {
            if (junctionCreated)
            {
                DeleteJunction(source);
            }

            if (moved && Directory.Exists(parked) && !Directory.Exists(source))
            {
                Directory.Move(parked, source);
            }
        }

        throw new InvalidOperationException(message);
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
        => RunMkLink("/J", junctionPath, targetPath);

    private static void CreateHardLink(string linkPath, string targetPath)
        => RunMkLink("/H", linkPath, targetPath);

    private static void RunMkLink(string kind, string linkPath, string targetPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add(kind);
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de démarrer mklink.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "mklink a échoué: " + process.StandardError.ReadToEnd());
        }
    }

    private static void DeleteJunction(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private static void DeleteTestRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<TException>(
        Func<Task> action,
        string message)
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

    private sealed class BlockingBytesHandler(byte[] payload) : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            };
        }
    }

    private sealed class CountingFailureHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("La requête réseau ne devait pas partir."));
        }
    }
}
