using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static class GameClientMaintenanceTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeFileUriConstruction();
        await DownloadCreatesTemporaryFileThenReplacesAsync();
        await RejectInvalidSizeAndRemoveTemporaryFileAsync();
        await RejectInvalidHashAndRemoveTemporaryFileAsync();
        await PreserveLegacySingleHttpAttemptAsync();
        await FailWhenFinalFileRemainsLockedAsync();
        await CancelDuringDownloadAsync();
        await CancelAfterPayloadBeforeReplacementAsync();
        await CancelForShutdownDuringDownloadAsync();
        await RestartCleanlyAfterCancellationAsync();
        CharacterizeSafeCleanupAndHistoricalRetries();
        await InstallAbsentClientAndFinalizeInHistoricalOrderAsync();
        await RejectEmptyManifestBeforeStoppingGameAsync();
        await UpdateWithoutFileChangesStillFinalizesAsync();
        await DownloadMissingAndDifferentFilesAsync();
        await RemoveManagedObsoleteFileButKeepUserFileAsync();
        await RejectUnsafeManifestPathsAsync();
        await PreservePartialApplicationOnFailureAsync();
        await PropagateDiskAndPermissionFailuresWithoutFalseFinalizationAsync();
        await KeepOperationIdentityAndConcurrencyRulesAsync();
        KeepPreviewSideEffectFreeAndCommandsDisabled();
        AssertNoIndependentCancellationSources();
        Console.WriteLine("Shared game client maintenance pipeline OK (02D.1).");
        return 0;
    }

    private static void CharacterizeFileUriConstruction()
    {
        using HttpClient http = new(new ScriptedDownloadHandler());
        GameFileTransferService transfer = new(http);
        LauncherManifest manifest = Manifest(
            "uri-v1",
            FileEntry("Data/patch file.MPQ", [], url: string.Empty));

        Equal(
            "https://atlas.test/client/files/Data/patch%20file.MPQ",
            transfer.BuildFileUri(manifest, manifest.Files[0]).AbsoluteUri,
            "Une URL absente doit conserver files/ et l'échappement segment par segment.");

        manifest.Files[0].Url = "/packages/client.bin";
        Equal(
            "https://atlas.test/client/packages/client.bin",
            transfer.BuildFileUri(manifest, manifest.Files[0]).AbsoluteUri,
            "Une URL relative doit rester résolue depuis baseUrl.");

        manifest.Files[0].Url = "https://cdn.atlas.test/client.bin";
        Equal(
            "https://cdn.atlas.test/client.bin",
            transfer.BuildFileUri(manifest, manifest.Files[0]).AbsoluteUri,
            "Une URL absolue doit rester prioritaire.");

        manifest.BaseUrl = string.Empty;
        manifest.Files[0].Url = string.Empty;
        Throws<InvalidOperationException>(
            () => transfer.BuildFileUri(manifest, manifest.Files[0]),
            "baseUrl manquant doit conserver l'erreur legacy.");
    }

    private static async Task DownloadCreatesTemporaryFileThenReplacesAsync()
    {
        using TempDirectory temp = new("AtlasTransferSuccess");
        byte[] payload = Encoding.UTF8.GetBytes("atlas-transfer-payload");
        ScriptedDownloadHandler handler = new((_, _, _) => Response(payload));
        using HttpClient http = new(handler);
        GameFileTransferService transfer = new(http);
        string target = Path.Combine(temp.Path, "Data", "client.bin");
        bool temporaryObserved = false;
        List<GameFileTransferProgress> progress = [];

        await transfer.DownloadAsync(
            41,
            new Uri("https://atlas.test/client.bin"),
            target,
            payload.Length,
            Hash(payload),
            value =>
            {
                progress.Add(value);
                string directory = Path.GetDirectoryName(target)!;
                temporaryObserved |= Directory.EnumerateFiles(
                    directory,
                    ".client.bin.*.download").Any();
            },
            CancellationToken.None);

        SequenceEqual(payload, await File.ReadAllBytesAsync(target), "Le fichier final doit contenir le flux validé.");
        True(temporaryObserved, "Le téléchargement doit écrire dans le fichier temporaire legacy.");
        True(!Directory.EnumerateFiles(temp.Path, "*.download", SearchOption.AllDirectories).Any(), "Le temporaire doit disparaître après remplacement.");
        True(progress.Count > 0 && progress.All(item => item.OperationId == 41), "Toute progression doit porter l'OperationId.");
        Equal(1, handler.RequestCount, "Un téléchargement réussi utilise une requête HTTP.");
    }

    private static async Task RejectInvalidSizeAndRemoveTemporaryFileAsync()
    {
        using TempDirectory temp = new("AtlasTransferSize");
        byte[] payload = Encoding.UTF8.GetBytes("short");
        ScriptedDownloadHandler handler = new((_, _, _) => Response(payload));
        using HttpClient http = new(handler);
        GameFileTransferService transfer = new(http);
        string target = Path.Combine(temp.Path, "client.bin");

        InvalidOperationException error = await ThrowsAsync<InvalidOperationException>(() =>
            transfer.DownloadAsync(
                1,
                new Uri("https://atlas.test/client.bin"),
                target,
                payload.Length + 1,
                Hash(payload),
                null,
                CancellationToken.None));

        True(error.Message.Contains("Taille invalide", StringComparison.Ordinal), "L'erreur de taille legacy doit être conservée.");
        True(!File.Exists(target), "Une taille invalide ne doit jamais produire un fichier final.");
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task RejectInvalidHashAndRemoveTemporaryFileAsync()
    {
        using TempDirectory temp = new("AtlasTransferHash");
        byte[] payload = Encoding.UTF8.GetBytes("hash-source");
        ScriptedDownloadHandler handler = new((_, _, _) => Response(payload));
        using HttpClient http = new(handler);
        GameFileTransferService transfer = new(http);
        string target = Path.Combine(temp.Path, "client.bin");

        InvalidOperationException error = await ThrowsAsync<InvalidOperationException>(() =>
            transfer.DownloadAsync(
                2,
                new Uri("https://atlas.test/client.bin"),
                target,
                payload.Length,
                new string('0', 64),
                null,
                CancellationToken.None));

        True(error.Message.Contains("Hash invalide", StringComparison.Ordinal), "L'erreur SHA-256 legacy doit être conservée.");
        True(!File.Exists(target), "Un hash invalide ne doit jamais produire un fichier final.");
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task PreserveLegacySingleHttpAttemptAsync()
    {
        using TempDirectory temp = new("AtlasTransferHttpAttempt");
        byte[] payload = Encoding.UTF8.GetBytes("would-succeed-on-retry");
        ScriptedDownloadHandler handler = new((attempt, _, _) => attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(payload));
        using HttpClient http = new(handler);
        GameFileTransferService transfer = new(http);

        await ThrowsAsync<HttpRequestException>(() => transfer.DownloadAsync(
            3,
            new Uri("https://atlas.test/client.bin"),
            Path.Combine(temp.Path, "client.bin"),
            payload.Length,
            Hash(payload),
            null,
            CancellationToken.None));

        Equal(1, GameFileTransferService.LegacyHttpAttemptCount, "La v1.1.0 n'avait aucun retry HTTP implicite.");
        Equal(1, handler.RequestCount, "02D.1 doit préserver l'unique tentative HTTP legacy.");
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task FailWhenFinalFileRemainsLockedAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory temp = new("AtlasTransferLocked");
        byte[] original = Encoding.UTF8.GetBytes("locked-original");
        byte[] payload = Encoding.UTF8.GetBytes("replacement-data");
        string target = Path.Combine(temp.Path, "client.bin");
        await File.WriteAllBytesAsync(target, original);
        await using FileStream locked = new(
            target,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        ScriptedDownloadHandler handler = new((_, _, _) => Response(payload));
        using HttpClient http = new(handler);
        int delays = 0;
        GameFileTransferService transfer = new(
            http,
            new GameFileTransferRetryPolicy(2, TimeSpan.FromMilliseconds(1)),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                delays++;
                return Task.CompletedTask;
            });

        IOException error = await ThrowsAsync<IOException>(() => transfer.DownloadAsync(
            4,
            new Uri("https://atlas.test/client.bin"),
            target,
            payload.Length,
            Hash(payload),
            null,
            CancellationToken.None));

        True(error.Message.Contains("Ferme le jeu", StringComparison.Ordinal), "Le message historique de fichier verrouillé doit rester lisible.");
        Equal(2, delays, "Chaque tentative de remplacement échouée conserve son délai historique configurable en test.");
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task CancelDuringDownloadAsync()
    {
        using TempDirectory temp = new("AtlasTransferCancel");
        byte[] payload = Enumerable.Repeat((byte)0x42, 1024 * 384).ToArray();
        ScriptedDownloadHandler handler = new((_, _, _) => Response(payload, chunkSize: 32 * 1024));
        using HttpClient http = new(handler);
        GameFileTransferService transfer = new(http);
        using CancellationTokenSource cancellation = new();
        string target = Path.Combine(temp.Path, "client.bin");

        await ThrowsAsync<OperationCanceledException>(() => transfer.DownloadAsync(
            5,
            new Uri("https://atlas.test/client.bin"),
            target,
            payload.Length,
            Hash(payload),
            progress =>
            {
                if (progress.DownloadedBytes >= 32 * 1024)
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token));

        True(!File.Exists(target), "Une annulation pendant le flux ne doit pas produire de fichier final.");
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task CancelAfterPayloadBeforeReplacementAsync()
    {
        using TempDirectory temp = new("AtlasTransferCancelBeforeMove");
        byte[] payload = Encoding.UTF8.GetBytes("complete-payload-before-cancel");
        ScriptedDownloadHandler handler = new((_, _, _) => Response(payload));
        using HttpClient http = new(handler);
        GameFileTransferService transfer = new(http);
        using CancellationTokenSource cancellation = new();
        string target = Path.Combine(temp.Path, "client.bin");

        await ThrowsAsync<OperationCanceledException>(() => transfer.DownloadAsync(
            6,
            new Uri("https://atlas.test/client.bin"),
            target,
            payload.Length,
            Hash(payload),
            progress =>
            {
                if (progress.DownloadedBytes == payload.Length)
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token));

        True(!File.Exists(target), "Une annulation après réception mais avant validation ne doit pas remplacer la cible.");
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task CancelForShutdownDuringDownloadAsync()
    {
        using TempDirectory temp = new("AtlasTransferShutdown");
        byte[] payload = Enumerable.Repeat((byte)0x24, 1024 * 256).ToArray();
        ScriptedDownloadHandler handler = new((_, _, _) => Response(payload, chunkSize: 16 * 1024));
        using HttpClient http = new(handler);
        GameFileTransferService transfer = new(http);
        using LauncherOperationCoordinator operations = new();
        using LauncherOperationLease operation = Start(operations, LauncherOperationKind.GameInstall);

        await ThrowsAsync<OperationCanceledException>(() => transfer.DownloadAsync(
            operation.OperationId,
            new Uri("https://atlas.test/client.bin"),
            Path.Combine(temp.Path, "client.bin"),
            payload.Length,
            Hash(payload),
            _ => operations.CancelForShutdown(),
            operation.CancellationToken));

        True(operation.CancellationToken.IsCancellationRequested, "La fermeture doit interrompre même une opération en transfert.");
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task RestartCleanlyAfterCancellationAsync()
    {
        using MaintenanceEnvironment environment = new();
        byte[] payload = Enumerable.Repeat((byte)0x19, 1024 * 256).ToArray();
        environment.SetManifest(Manifest("resume", FileEntry("Data/client.bin", payload)));
        environment.Downloads.Responder = (attempt, _, _) => attempt == 1
            ? Response(payload, chunkSize: 16 * 1024)
            : Response(payload);
        LauncherOperationLease? firstLease = null;

        await ThrowsAsync<OperationCanceledException>(() => environment.RunAsync(
            LauncherOperationKind.GameInstall,
            progress:
            progress =>
            {
                if (progress.Phase == GameClientMaintenancePhase.Downloading
                    && progress.DownloadedBytes > 0)
                {
                    firstLease!.CancelFromUser();
                }
            },
            leaseStarted: lease => firstLease = lease));

        True(!File.Exists(environment.Store.GetPath(environment.Root)), "L'annulation ne doit pas écrire de faux cache complet.");
        Equal(0, environment.Platform.RegisterCalls, "L'annulation ne doit pas finaliser l'application Windows.");
        AssertNoTemporaryFiles(environment.Root);

        GameClientMaintenanceResult retry = await environment.RunAsync(
            LauncherOperationKind.GameInstall);
        Equal(GameClientMaintenanceOutcome.Downloaded, retry.Outcome, "Une nouvelle opération doit pouvoir reprendre par une comparaison normale.");
        SequenceEqual(payload, await File.ReadAllBytesAsync(Path.Combine(environment.Root, "Data", "client.bin")), "Le second passage doit produire le fichier final valide.");
        Equal(2, environment.Downloads.RequestCount, "La reprise legacy retélécharge le fichier sans reprise HTTP par plage.");
    }

    private static void CharacterizeSafeCleanupAndHistoricalRetries()
    {
        using TempDirectory temp = new("AtlasCleanup");
        InstalledManifestStore store = new(_ => true);
        GameFileVerifier verifier = new(store, new GameClientStateReader(_ => false), _ => false);
        GameFileCleanupService cleanup = new(
            verifier,
            new GameFileCleanupRetryPolicy(2, TimeSpan.FromMilliseconds(1)),
            _ => { });
        string managed = Write(temp.Path, "Data/obsolete.bin", "managed");
        string user = Write(temp.Path, "Screenshots/user.png", "user");

        Equal(
            1,
            cleanup.DeleteRemovedFiles(temp.Path, ["Data/obsolete.bin"], CancellationToken.None),
            "Le fichier géré obsolète doit être supprimé une fois.");
        True(!File.Exists(managed), "Le fichier géré doit être supprimé.");
        True(File.Exists(user), "Un fichier utilisateur non listé doit rester intact.");
        Throws<InvalidOperationException>(
            () => cleanup.DeleteRemovedFiles(temp.Path, ["../outside.bin"], CancellationToken.None),
            "Une séquence ../ doit être refusée avant suppression.");
        Throws<InvalidOperationException>(
            () => cleanup.DeleteRemovedFiles(temp.Path, [Path.Combine(Path.GetPathRoot(temp.Path)!, "outside.bin")], CancellationToken.None),
            "Un chemin absolu doit être refusé avant suppression.");
    }

    private static async Task InstallAbsentClientAndFinalizeInHistoricalOrderAsync()
    {
        using MaintenanceEnvironment environment = new();
        byte[] payload = Encoding.UTF8.GetBytes("new-client");
        LauncherManifest manifest = Manifest(
            "3.4.3-install",
            FileEntry("_classic_/WowClassic.exe", payload));
        environment.SetManifest(manifest);
        environment.Downloads.Responder = (_, _, _) => Response(payload);
        List<GameClientMaintenanceProgress> progress = [];

        GameClientMaintenanceResult result = await environment.RunAsync(
            LauncherOperationKind.GameInstall,
            progress.Add);

        Equal(GameClientMaintenanceOutcome.Downloaded, result.Outcome, "Un client absent doit suivre le pipeline d'installation.");
        Equal(1, environment.Platform.StopCalls, "Le jeu ouvert doit être arrêté au même point qu'en v1.1.0.");
        SequenceEqual(payload, await File.ReadAllBytesAsync(Path.Combine(environment.Root, "_classic_", "WowClassic.exe")), "Le client absent doit être installé.");
        True(File.Exists(environment.Store.GetPath(environment.Root)), "Le cache installé doit être écrit après les fichiers.");
        True(File.Exists(Path.Combine(environment.Root, GameInstallServices.ClientMarkerFileName)), "Le marqueur doit être créé par la finalisation simulée.");
        AssertOrdered(
            environment.Events,
            "manifest-load",
            "stop-processes",
            "compare-files",
            "find-removed",
            "cache-save",
            "register-game");
        AssertPhaseOrder(
            progress,
            GameClientMaintenancePhase.LoadingManifest,
            GameClientMaintenancePhase.ManifestLoaded,
            GameClientMaintenancePhase.GameProcessesStopped,
            GameClientMaintenancePhase.ComparingManifest,
            GameClientMaintenancePhase.ComparisonCompleted,
            GameClientMaintenancePhase.DownloadingStarted,
            GameClientMaintenancePhase.DownloadingFile,
            GameClientMaintenancePhase.Downloading,
            GameClientMaintenancePhase.CacheSaved,
            GameClientMaintenancePhase.Registering,
            GameClientMaintenancePhase.RegistrationCompleted,
            GameClientMaintenancePhase.Completed);
        True(progress.All(item => item.OperationId == result.OperationId), "Les phases de maintenance doivent partager le même OperationId.");
    }

    private static async Task UpdateWithoutFileChangesStillFinalizesAsync()
    {
        using MaintenanceEnvironment environment = new();
        byte[] payload = Encoding.UTF8.GetBytes("unchanged");
        LauncherManifest manifest = Manifest("3.4.3-current", FileEntry("Data/client.bin", payload));
        environment.Store.Save(environment.Root, manifest);
        environment.Events.Clear();
        environment.SetManifest(manifest);

        GameClientMaintenanceResult result = await environment.RunAsync(LauncherOperationKind.GameUpdate);

        Equal(GameClientMaintenanceOutcome.AlreadyCurrent, result.Outcome, "Un cache identique doit conserver le raccourci legacy.");
        Equal(0, environment.Downloads.RequestCount, "Aucun fichier inchangé ne doit être téléchargé.");
        Equal(1, environment.Platform.RegisterCalls, "Le legacy réenregistre même un client déjà à jour.");
        AssertOrdered(environment.Events, "cache-save", "register-game");
    }

    private static async Task RejectEmptyManifestBeforeStoppingGameAsync()
    {
        using MaintenanceEnvironment environment = new();
        environment.SetManifest(Manifest("empty"));

        InvalidOperationException error = await ThrowsAsync<InvalidOperationException>(() =>
            environment.RunAsync(LauncherOperationKind.GameInstall));

        Equal("Le manifeste ne contient aucun fichier.", error.Message, "Le manifeste vide doit conserver l'erreur legacy.");
        Equal(0, environment.Platform.StopCalls, "Le jeu ne doit pas être arrêté avant validation du manifeste.");
        Equal(0, environment.Platform.RegisterCalls, "Un manifeste vide ne doit pas finaliser l'installation.");
    }

    private static async Task DownloadMissingAndDifferentFilesAsync()
    {
        using MaintenanceEnvironment environment = new();
        byte[] missing = Encoding.UTF8.GetBytes("missing-file");
        byte[] replacement = Encoding.UTF8.GetBytes("new-same-size");
        Write(environment.Root, "Data/different.bin", "old-same-size");
        LauncherManifest manifest = Manifest(
            "3.4.3-delta",
            FileEntry("Data/missing.bin", missing),
            FileEntry("Data/different.bin", replacement));
        environment.SetManifest(manifest);
        environment.Downloads.Responder = (_, request, _) => request.RequestUri!.AbsolutePath.EndsWith("missing.bin", StringComparison.Ordinal)
            ? Response(missing)
            : Response(replacement);

        GameClientMaintenanceResult result = await environment.RunAsync(LauncherOperationKind.GameUpdate);

        Equal(2, result.DownloadedFileCount, "Le fichier absent et le fichier différent doivent être téléchargés.");
        SequenceEqual(missing, await File.ReadAllBytesAsync(Path.Combine(environment.Root, "Data", "missing.bin")), "Le fichier absent doit être créé.");
        SequenceEqual(replacement, await File.ReadAllBytesAsync(Path.Combine(environment.Root, "Data", "different.bin")), "Le fichier différent doit être remplacé.");
    }

    private static async Task RemoveManagedObsoleteFileButKeepUserFileAsync()
    {
        using MaintenanceEnvironment environment = new();
        byte[] keep = Encoding.UTF8.GetBytes("keep");
        LauncherManifest installed = Manifest(
            "old",
            FileEntry("Data/keep.bin", keep),
            FileEntry("Data/obsolete.bin", Encoding.UTF8.GetBytes("obsolete")));
        LauncherManifest remote = Manifest("new", FileEntry("Data/keep.bin", keep));
        environment.Store.Save(environment.Root, installed);
        environment.Events.Clear();
        Write(environment.Root, "Data/keep.bin", "keep");
        string obsolete = Write(environment.Root, "Data/obsolete.bin", "obsolete");
        string user = Write(environment.Root, "Screenshots/user.png", "user");
        environment.SetManifest(remote);

        GameClientMaintenanceResult result = await environment.RunAsync(LauncherOperationKind.GameUpdate);

        Equal(GameClientMaintenanceOutcome.CleanupOnly, result.Outcome, "Une suppression seule doit conserver sa branche historique.");
        Equal(1, result.DeletedFileCount, "Un seul fichier géré doit être supprimé.");
        True(!File.Exists(obsolete), "Le fichier obsolète géré doit disparaître.");
        True(File.Exists(user), "Le fichier utilisateur ne doit jamais être supprimé.");
    }

    private static async Task RejectUnsafeManifestPathsAsync()
    {
        foreach (string unsafePath in new[] { "../escape.bin", "C:\\escape.bin" })
        {
            using MaintenanceEnvironment environment = new();
            byte[] payload = Encoding.UTF8.GetBytes("unsafe");
            environment.SetManifest(Manifest("unsafe", FileEntry(unsafePath, payload)));
            environment.Downloads.Responder = (_, _, _) => Response(payload);

            await ThrowsAsync<InvalidOperationException>(() =>
                environment.RunAsync(LauncherOperationKind.GameInstall));
            Equal(0, environment.Platform.RegisterCalls, "Un chemin refusé ne doit jamais finaliser l'installation.");
            True(!File.Exists(environment.Store.GetPath(environment.Root)), "Un chemin refusé ne doit pas écrire de cache complet.");
        }
    }

    private static async Task PreservePartialApplicationOnFailureAsync()
    {
        using MaintenanceEnvironment environment = new();
        byte[] first = Encoding.UTF8.GetBytes("first-ok");
        byte[] second = Encoding.UTF8.GetBytes("second-bad");
        LauncherManifest manifest = Manifest(
            "partial",
            FileEntry("Data/first.bin", first),
            FileEntry("Data/second.bin", second));
        environment.SetManifest(manifest);
        environment.Downloads.Responder = (_, request, _) => request.RequestUri!.AbsolutePath.EndsWith("first.bin", StringComparison.Ordinal)
            ? Response(first)
            : Response(Encoding.UTF8.GetBytes("wrong"));

        await ThrowsAsync<InvalidOperationException>(() =>
            environment.RunAsync(LauncherOperationKind.GameUpdate));

        True(File.Exists(Path.Combine(environment.Root, "Data", "first.bin")), "Le legacy conserve les fichiers déjà appliqués avant l'échec.");
        True(!File.Exists(Path.Combine(environment.Root, "Data", "second.bin")), "Le fichier invalide ne doit pas devenir final.");
        True(!File.Exists(environment.Store.GetPath(environment.Root)), "Le cache complet ne doit pas être écrit après échec partiel.");
        Equal(0, environment.Platform.RegisterCalls, "Le marqueur et le registre ne doivent pas être finalisés après échec.");
    }

    private static async Task PropagateDiskAndPermissionFailuresWithoutFalseFinalizationAsync()
    {
        foreach (Exception failure in new Exception[]
                 {
                     new IOException("disk-full-test"),
                     new UnauthorizedAccessException("access-denied-test")
                 })
        {
            using MaintenanceEnvironment environment = new(
                transferOverride: new ThrowingFileTransferService(failure));
            byte[] payload = Encoding.UTF8.GetBytes("never-written");
            environment.SetManifest(Manifest("failure", FileEntry("Data/client.bin", payload)));

            Exception observed = await ThrowsAnyAsync(() =>
                environment.RunAsync(LauncherOperationKind.GameInstall));
            Equal(failure.GetType(), observed.GetType(), "L'erreur technique doit rester observable par l'orchestrateur legacy.");
            True(!File.Exists(environment.Store.GetPath(environment.Root)), "Une erreur disque ou permission ne doit pas écrire le cache final.");
            Equal(0, environment.Platform.RegisterCalls, "Une erreur disque ou permission ne doit pas créer le marqueur.");
        }
    }

    private static async Task KeepOperationIdentityAndConcurrencyRulesAsync()
    {
        using MaintenanceEnvironment environment = new();
        byte[] payload = Encoding.UTF8.GetBytes("operation-id");
        environment.SetManifest(Manifest("operation", FileEntry("Data/client.bin", payload)));
        environment.Downloads.Responder = (_, _, _) => Response(payload);
        using LauncherOperationCoordinator operations = new();
        using LauncherOperationLease first = Start(operations, LauncherOperationKind.GameInstall);
        LauncherOperationStartResult concurrent = operations.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true);
        Equal(LauncherOperationStartStatus.Busy, concurrent.Status, "Une seconde maintenance doit être refusée immédiatement.");

        List<GameClientMaintenanceProgress> progress = [];
        GameClientMaintenanceResult result = await environment.Service.InstallOrUpdateAsync(
            environment.Request,
            first,
            progress.Add);
        long oldId = first.OperationId;
        first.Complete();

        using LauncherOperationLease second = Start(operations, LauncherOperationKind.GameUpdate);
        bool staleApplied = first.TryInvoke(oldId, () => throw new InvalidOperationException("stale callback"));
        True(!staleApplied, "Un callback de l'ancienne opération doit être ignoré.");
        True(progress.All(item => item.OperationId == result.OperationId), "Aucune phase ne doit perdre l'identité du bail.");
        True(second.OperationId > oldId, "Les identifiants d'opération doivent rester monotones.");
    }

    private static void KeepPreviewSideEffectFreeAndCommandsDisabled()
    {
        foreach (GamePreviewScenario scenario in Enum.GetValues<GamePreviewScenario>())
        {
            var state = LauncherV2PreviewData.CreateGame(scenario);
            True(!state.OpenGameFolderCommand.CanExecute(null), $"Dossier preview {scenario} doit rester sans effet.");
            True(!state.OpenDiagnosticCommand.CanExecute(null), $"Diagnostic preview {scenario} doit rester sans effet.");
            True(!state.VerifyCommand.CanExecute(null), $"Vérifier preview {scenario} doit rester sans effet.");
        }

        Equal(
            LauncherStartupMode.UiV2Preview,
            App.ResolveStartupMode(["--ui-v2", "--preview-state=Downloading"]),
            "Le preview doit rester une branche de démarrage isolée.");
    }

    private static void AssertNoIndependentCancellationSources()
    {
        foreach (Type type in new[]
                 {
                     typeof(GameClientMaintenanceService),
                     typeof(GameFullFileVerifier),
                     typeof(GameFileTransferService),
                     typeof(GameFileCleanupService)
                 })
        {
            True(
                type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .All(field => field.FieldType != typeof(CancellationTokenSource)),
                type.Name + " ne doit posséder aucune seconde CancellationTokenSource.");
        }
    }

    private static LauncherOperationLease Start(
        LauncherOperationCoordinator operations,
        LauncherOperationKind kind)
    {
        LauncherOperationStartResult start = operations.TryBegin(kind, canUserCancel: true);
        Equal(LauncherOperationStartStatus.Started, start.Status, "Le bail témoin doit démarrer.");
        return start.Lease!;
    }

    private static LauncherManifest Manifest(string version, params LauncherFile[] files)
    {
        return new LauncherManifest
        {
            Version = version,
            BaseUrl = "https://atlas.test/client/",
            Files = files.ToList()
        };
    }

    private static LauncherFile FileEntry(string path, byte[] content, string? url = null)
    {
        return new LauncherFile
        {
            Path = path,
            Size = content.LongLength,
            Sha256 = Hash(content),
            Url = url ?? string.Empty
        };
    }

    private static string Hash(byte[] content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private static HttpResponseMessage Response(byte[] content, int? chunkSize = null)
    {
        Stream stream = chunkSize is null
            ? new MemoryStream(content, writable: false)
            : new ChunkedReadStream(content, chunkSize.Value);
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentLength = content.LongLength;
        return response;
    }

    private static string Write(string root, string relativePath, string content)
    {
        string path = GamePathPolicy.GetSafeTargetPath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static void AssertNoTemporaryFiles(string root)
    {
        True(
            !Directory.EnumerateFiles(root, "*.download", SearchOption.AllDirectories).Any(),
            "Aucun fichier temporaire .download ne doit subsister.");
    }

    private static void AssertPhaseOrder(
        IReadOnlyList<GameClientMaintenanceProgress> actual,
        params GameClientMaintenancePhase[] expected)
    {
        int previous = -1;
        foreach (GameClientMaintenancePhase phase in expected)
        {
            int index = actual.Select(item => item.Phase).ToList().FindIndex(
                previous + 1,
                item => item == phase);
            True(index >= 0, "Phase absente ou désordonnée: " + phase);
            previous = index;
        }
    }

    private static void AssertOrdered(
        IReadOnlyList<string> actual,
        params string[] expected)
    {
        int previous = -1;
        foreach (string value in expected)
        {
            int index = actual.ToList().FindIndex(previous + 1, item => item == value);
            True(index >= 0, "Événement absent ou désordonné: " + value);
            previous = index;
        }
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Exception attendue: " + typeof(T).Name);
    }

    private static async Task<Exception> ThrowsAnyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Une exception était attendue.");
    }

    private static void Throws<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void SequenceEqual(byte[] expected, byte[] actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; actuel={actual}.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class MaintenanceEnvironment : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LauncherOperationCoordinator _operations = new();
    private readonly RecordingManifestClient _manifestClient;

    internal MaintenanceEnvironment(
        IGameFileTransferService? transferOverride = null,
        IGameFullFileVerifier? fullVerifierOverride = null)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "AtlasMaintenance02D1",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Events = [];
        Store = new RecordingManifestStore(new InstalledManifestStore(_ => true), Events);
        GameClientStateReader stateReader = new(_ => false);
        IGameFileVerifier verifier = new RecordingFileVerifier(
            new GameFileVerifier(Store, stateReader, _ => false),
            Events);
        Downloads = new ScriptedDownloadHandler();
        _httpClient = new HttpClient(Downloads);
        IGameFileTransferService transfer = transferOverride
            ?? new GameFileTransferService(_httpClient);
        IGameFileCleanupService cleanup = new RecordingCleanupService(
            new GameFileCleanupService(verifier),
            Events);
        Platform = new RecordingInstallPlatform(Root, Events);
        _manifestClient = new RecordingManifestClient(Events);
        Service = new GameClientMaintenanceService(
            _manifestClient,
            verifier,
            Store,
            transfer,
            cleanup,
            Platform,
            fullVerifierOverride);
        Request = new GameClientMaintenanceRequest(
            Root,
            "https://atlas.test/manifest.json",
            "frFR");
    }

    internal string Root { get; }

    internal List<string> Events { get; }

    internal RecordingManifestStore Store { get; }

    internal ScriptedDownloadHandler Downloads { get; }

    internal RecordingInstallPlatform Platform { get; }

    internal IGameClientMaintenanceService Service { get; }

    internal GameClientMaintenanceRequest Request { get; }

    internal void SetManifest(LauncherManifest manifest)
    {
        _manifestClient.Manifest = manifest;
    }

    internal async Task<GameClientMaintenanceResult> RunAsync(
        LauncherOperationKind kind,
        Action<GameClientMaintenanceProgress>? progress = null,
        Action<LauncherOperationLease>? leaseStarted = null)
    {
        LauncherOperationStartResult start = _operations.TryBegin(kind, canUserCancel: true);
        if (!start.IsStarted)
        {
            throw new InvalidOperationException("Impossible d'acquérir le bail de maintenance témoin.");
        }

        using LauncherOperationLease operation = start.Lease!;
        leaseStarted?.Invoke(operation);
        try
        {
            return await Service.InstallOrUpdateAsync(Request, operation, progress);
        }
        finally
        {
            operation.Complete();
        }
    }

    internal async Task<GameClientMaintenanceResult> RunRepairAsync(
        Action<GameClientMaintenanceProgress>? progress = null,
        Action<LauncherOperationLease>? leaseStarted = null)
    {
        LauncherOperationStartResult start = _operations.TryBegin(
            LauncherOperationKind.GameRepair,
            canUserCancel: true,
            clientIsPlayable: true);
        if (!start.IsStarted)
        {
            throw new InvalidOperationException("Impossible d'acquérir le bail GameRepair témoin.");
        }

        using LauncherOperationLease operation = start.Lease!;
        leaseStarted?.Invoke(operation);
        try
        {
            return await Service.VerifyAndRepairAsync(Request, operation, progress);
        }
        finally
        {
            operation.Complete();
        }
    }

    public void Dispose()
    {
        _operations.Dispose();
        _httpClient.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class RecordingManifestClient(List<string> events) : IGameManifestClient
{
    internal LauncherManifest Manifest { get; set; } = new();

    public Task<LauncherManifest> LoadAsync(
        string manifestUrl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Add("manifest-load");
        return Task.FromResult(Manifest);
    }
}

internal sealed class RecordingManifestStore(
    IInstalledManifestStore inner,
    List<string> events) : IInstalledManifestStore
{
    internal int SaveCalls { get; private set; }

    public string GetPath(string installRoot) => inner.GetPath(installRoot);

    public LauncherManifest? Load(string installRoot) => inner.Load(installRoot);

    public void Save(string installRoot, LauncherManifest manifest)
    {
        SaveCalls++;
        events.Add("cache-save");
        inner.Save(installRoot, manifest);
    }
}

internal sealed class RecordingFileVerifier(
    IGameFileVerifier inner,
    List<string> events) : IGameFileVerifier
{
    public Task<GameFileComparisonResult> FindMissingOrChangedFilesAsync(
        string installRoot,
        LauncherManifest manifest,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        events.Add("compare-files");
        return inner.FindMissingOrChangedFilesAsync(
            installRoot,
            manifest,
            reportProgress,
            cancellationToken);
    }

    public IReadOnlyList<string> FindRemovedFiles(
        string installRoot,
        LauncherManifest manifest)
    {
        events.Add("find-removed");
        return inner.FindRemovedFiles(installRoot, manifest);
    }
}

internal sealed class RecordingCleanupService(
    IGameFileCleanupService inner,
    List<string> events) : IGameFileCleanupService
{
    public IReadOnlyList<string> FindRemovedFiles(
        string installRoot,
        LauncherManifest manifest)
    {
        return inner.FindRemovedFiles(installRoot, manifest);
    }

    public int DeleteRemovedFiles(
        string installRoot,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        events.Add("delete-removed");
        return inner.DeleteRemovedFiles(installRoot, relativePaths, cancellationToken);
    }
}

internal sealed class RecordingInstallPlatform(
    string root,
    List<string> events) : IGameInstallPlatform
{
    internal Exception? RegistrationFailure { get; set; }

    internal int StopCalls { get; private set; }

    internal int RegisterCalls { get; private set; }

    public void StopRunningGameProcesses(string installRoot)
    {
        StopCalls++;
        events.Add("stop-processes");
    }

    public GameApplicationRegistration RegisterGameApplication(
        string installRoot,
        string clientVersion,
        string gameLocale)
    {
        RegisterCalls++;
        events.Add("register-game");
        if (RegistrationFailure is not null)
        {
            throw RegistrationFailure;
        }

        string configPath = Path.Combine(root, "_classic_", "WTF", "Config.wtf");
        string uninstallerPath = Path.Combine(root, GameInstallServices.UninstallerFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "SET locale \"" + gameLocale + "\"");
        File.WriteAllText(uninstallerPath, "test-uninstaller");
        File.WriteAllText(
            Path.Combine(root, GameInstallServices.ClientMarkerFileName),
            "{\"clientVersion\":\"" + clientVersion + "\"}");
        return new GameApplicationRegistration(configPath, uninstallerPath);
    }
}

internal sealed class ThrowingFileTransferService(Exception exception) : IGameFileTransferService
{
    public Uri BuildFileUri(LauncherManifest manifest, LauncherFile file)
    {
        return new Uri("https://atlas.test/failure.bin");
    }

    public Task DownloadAsync(
        long operationId,
        Uri uri,
        string targetPath,
        long expectedSize,
        string expectedSha256,
        Action<GameFileTransferProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(exception);
    }
}

internal sealed class ScriptedDownloadHandler : HttpMessageHandler
{
    private int _requestCount;

    internal ScriptedDownloadHandler(
        Func<int, HttpRequestMessage, CancellationToken, HttpResponseMessage>? responder = null)
    {
        Responder = responder ?? ((_, _, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    internal Func<int, HttpRequestMessage, CancellationToken, HttpResponseMessage> Responder { get; set; }

    internal int RequestCount => Volatile.Read(ref _requestCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref _requestCount);
        return Task.FromResult(Responder(attempt, request, cancellationToken));
    }
}

internal sealed class ChunkedReadStream(byte[] content, int chunkSize) : Stream
{
    private readonly MemoryStream _inner = new(content, writable: false);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _inner.Read(buffer, offset, Math.Min(count, chunkSize));
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory(string name)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            name,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
