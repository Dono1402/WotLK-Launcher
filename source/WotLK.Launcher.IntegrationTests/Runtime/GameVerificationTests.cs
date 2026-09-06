using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class GameVerificationTests
{
    internal static async Task<int> RunAsync()
    {
        await PreserveFastCacheAndItsKnownLimitAsync();
        await DetectManifestMetadataChangesFromCacheAsync();
        await PreserveInstalledVersionShortcutAsync();
        await FallBackToRealFileScanWithReliableCountsAsync();
        await DeriveKnowledgeOnlyFromSuccessfulComparisonAsync();
        await RefuseConcurrentVerificationImmediatelyAsync();
        await CoalesceIntermediateFileProgressAsync();
        await ReturnToStableLocalStateAfterTechnicalFailureAsync();
        await CancelOnlyForRuntimeShutdownAsync();
        await RefreshAvailabilityAfterSessionRestoreAsync();
        KeepPreviewVerificationSideEffectFree();
        await VerifyWpfCommandAndAtomicPresentationAsync();
        Console.WriteLine("Game verification compatibility OK (02C).");
        return 0;
    }

    private static async Task PreserveFastCacheAndItsKnownLimitAsync()
    {
        using VerificationEnvironment environment = new();
        LauncherManifest remote = Manifest(
            "cache-v1",
            FileEntry("Data/cache.bin", 4, "same-hash"));
        environment.Store.Save(environment.Root, remote);

        GameFileComparisonResult result = await environment.Verifier
            .FindMissingOrChangedFilesAsync(
                environment.Root,
                remote,
                _ => throw new InvalidOperationException("Le cache rapide ne doit pas scanner le disque."),
                CancellationToken.None);

        Equal(
            GameFileComparisonSource.ManifestHistory,
            result.Source,
            "L'historique doit rester prioritaire.");
        Equal(0, result.MissingOrChangedFiles.Count, "Le cache identique doit conclure sans hash.");
        True(
            !File.Exists(Path.Combine(environment.Root, "Data", "cache.bin")),
            "Le test doit conserver la limite legacy: le fichier réel peut manquer sans être relu.");
        Equal(0, result.ProcessedFileCount, "Un cache rapide ne doit inventer aucun comptage disque.");
    }

    private static async Task DetectManifestMetadataChangesFromCacheAsync()
    {
        using VerificationEnvironment environment = new();
        LauncherManifest installed = Manifest(
            "cache-v1",
            FileEntry("Data/cache.bin", 4, "old-hash"));
        LauncherManifest remote = Manifest(
            "cache-v2",
            FileEntry("Data/cache.bin", 5, "new-hash"));
        environment.Store.Save(environment.Root, installed);

        GameFileComparisonResult result = await environment.Verifier
            .FindMissingOrChangedFilesAsync(
                environment.Root,
                remote,
                null,
                CancellationToken.None);

        Equal(GameFileComparisonSource.ManifestHistory, result.Source, "La comparaison doit rester issue du cache.");
        Equal(1, result.MissingOrChangedFiles.Count, "La différence de métadonnées doit annoncer une mise à jour.");
        Equal("Data/cache.bin", result.MissingOrChangedFiles[0].Path, "Le fichier changé est incorrect.");
    }

    private static async Task PreserveInstalledVersionShortcutAsync()
    {
        using VerificationEnvironment environment = new();
        environment.WriteClientVersion("version-shortcut");
        LauncherManifest remote = Manifest(
            "version-shortcut",
            FileEntry("Data/not-read.bin", 32, "not-read"));

        GameFileComparisonResult result = await environment.Verifier
            .FindMissingOrChangedFilesAsync(
                environment.Root,
                remote,
                _ => throw new InvalidOperationException("Le raccourci de version ne doit pas scanner."),
                CancellationToken.None);

        Equal(GameFileComparisonSource.InstalledVersion, result.Source, "Le marqueur de version doit rester un raccourci.");
        Equal(0, result.MissingOrChangedFiles.Count, "La version identique doit conserver la conclusion legacy.");
        True(File.Exists(environment.Store.GetPath(environment.Root)), "Le raccourci doit amorcer le cache comme en v1.1.0.");
    }

    private static async Task FallBackToRealFileScanWithReliableCountsAsync()
    {
        using VerificationEnvironment environment = new();
        string goodPath = environment.WriteFile("Data/good.bin", "good");
        string badPath = environment.WriteFile("Data/bad.bin", "bad");
        LauncherManifest remote = Manifest(
            "scan-v1",
            FileEntry(
                "Data/good.bin",
                new FileInfo(goodPath).Length,
                await GameFileVerifier.ComputeSha256Async(goodPath, CancellationToken.None)),
            FileEntry(
                "Data/bad.bin",
                new FileInfo(badPath).Length,
                "0000000000000000000000000000000000000000000000000000000000000000"));
        List<GameVerificationProgress> progress = [];

        GameFileComparisonResult result = await environment.Verifier
            .FindMissingOrChangedFilesAsync(
                environment.Root,
                remote,
                progress.Add,
                CancellationToken.None);

        Equal(GameFileComparisonSource.FileSystem, result.Source, "Sans cache, les fichiers doivent être réellement lus.");
        Equal(2, result.ProcessedFileCount, "Le comptage final doit refléter les fichiers parcourus.");
        Equal(2, result.TotalFileCount, "Le total doit provenir du manifeste.");
        SequenceEqual([1, 2], progress.Select(item => item.ProcessedFileCount!.Value).ToArray(), "Le comptage réel est incorrect.");
        Equal(1, result.MissingOrChangedFiles.Count, "Seul le hash erroné doit être signalé.");
        Equal("Data/bad.bin", result.MissingOrChangedFiles[0].Path, "Le fichier corrompu est incorrect.");
    }

    private static async Task DeriveKnowledgeOnlyFromSuccessfulComparisonAsync()
    {
        using VerificationEnvironment environment = new();
        LauncherManifest compared = Manifest("known-v1", FileEntry("Data/a.bin", 1, "hash"));
        environment.Store.Save(environment.Root, compared);
        StubManifestClient manifestClient = new() { Manifest = compared };
        GameClientVerificationService service = environment.CreateService(manifestClient);

        GameClientVerificationResult known = await service.VerifyAsync(
            environment.Settings,
            reportFileProgress: true,
            null,
            CancellationToken.None);
        Equal(GameVerificationOutcome.UpToDate, known.Outcome, "La comparaison identique doit conclure À jour.");
        Equal(GameUpdateKnowledge.Known, known.UpdateKnowledge, "À jour exige une comparaison distante réussie.");

        manifestClient.Manifest = Manifest("empty-v1");
        GameClientVerificationResult empty = await service.VerifyAsync(
            environment.Settings,
            reportFileProgress: true,
            null,
            CancellationToken.None);
        Equal(GameVerificationOutcome.EmptyManifest, empty.Outcome, "Le manifeste vide doit rester distinct.");
        Equal(GameUpdateKnowledge.Unavailable, empty.UpdateKnowledge, "Un manifeste vide ne doit jamais produire À jour.");
    }

    private static async Task RefuseConcurrentVerificationImmediatelyAsync()
    {
        using VerificationEnvironment environment = new();
        BlockingVerificationService service = new();
        using LauncherOperationCoordinator operations = new();
        GameRuntimeCoordinator coordinator = environment.CreateCoordinator(service, operations);

        Equal(GameVerificationStartStatus.Started, coordinator.TryStartVerification(), "La première vérification doit démarrer.");
        await service.Started.Task;
        Equal(GameVerificationStartStatus.Busy, coordinator.TryStartVerification(), "Le second clic doit être refusé immédiatement.");
        Equal(1, service.Calls, "Aucune vérification ne doit être mise en file.");
        True(!coordinator.CanVerify, "CanExecute doit être faux pendant l'analyse.");

        service.Release(ResultUpToDate());
        await coordinator.WaitForIdleAsync();
        Equal(GameUpdateKnowledge.Known, coordinator.CurrentSnapshot.UpdateKnowledge, "Le résultat final doit être publié.");
        True(!coordinator.CanVerify, "Sans pipeline de maintenance injecté, la nouvelle commande manuelle doit rester indisponible.");
        Equal(GameVerificationStartStatus.Started, coordinator.TryStartVerification(), "L’analyse automatique légère doit rester relançable indépendamment du bouton manuel.");
        await coordinator.WaitForIdleAsync();
        Equal(2, service.Calls, "La seconde analyse automatique doit s’exécuter immédiatement, sans file cachée.");
    }

    private static async Task ReturnToStableLocalStateAfterTechnicalFailureAsync()
    {
        using VerificationEnvironment environment = new();
        ThrowingVerificationService service = new(
            new HttpRequestException(@"network failure C:\Users\Dono\secret-token"));
        using LauncherOperationCoordinator operations = new();
        List<string> logs = [];
        GameRuntimeCoordinator coordinator = environment.CreateCoordinator(
            service,
            operations,
            logs.Add);

        Equal(GameVerificationStartStatus.Started, coordinator.TryStartVerification(), "La vérification témoin doit démarrer.");
        await coordinator.WaitForIdleAsync();
        GameRuntimeSnapshot snapshot = coordinator.CurrentSnapshot;

        Equal(GameAction.Play, snapshot.Action, "L'erreur réseau doit revenir au client local jouable.");
        Equal(GameUpdateKnowledge.Unavailable, snapshot.UpdateKnowledge, "L'indisponibilité doit rester orthogonale à Play.");
        Equal(GameVerificationPhase.Stable, snapshot.Phase, "Aucun état Error client ne doit être forcé.");
        Equal("HttpRequestException", snapshot.FailureCategory, "Seule la catégorie technique doit être conservée.");
        Equal(1, logs.Count, "L'erreur doit être journalisée une fois.");
        True(logs[0].Contains("HttpRequestException", StringComparison.Ordinal), "Le type d'erreur doit être journalisé.");
        True(!logs[0].Contains("secret", StringComparison.OrdinalIgnoreCase), "Le message sensible ne doit pas être journalisé.");

        GameViewState view = GameStateAdapter.Project(snapshot);
        Equal(GamePreviewScenario.Ready, view.Scenario, "L'UI doit revenir à Ready, pas Error.");
        Equal("Client prêt", view.ClientStatus, "Le client local doit rester jouable.");
        Equal("Vérification indisponible", view.InstallBadgeText, "Le badge neutre d'indisponibilité est incorrect.");
    }

    private static async Task CoalesceIntermediateFileProgressAsync()
    {
        using VerificationEnvironment environment = new();
        ImmediateProgressVerificationService service = new();
        using LauncherOperationCoordinator operations = new();
        ManualTimeProvider clock = new();
        GameRuntimeCoordinator coordinator = environment.CreateCoordinator(
            service,
            operations,
            timeProvider: clock);
        List<int> publishedCounts = [];
        coordinator.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot.Phase == GameVerificationPhase.ScanningFiles
                && args.Snapshot.ProcessedFileCount is int count)
            {
                publishedCounts.Add(count);
            }
        };

        Equal(GameVerificationStartStatus.Started, coordinator.TryStartVerification(), "La progression témoin doit démarrer.");
        await coordinator.WaitForIdleAsync();

        SequenceEqual([1, 4], publishedCounts, "Les événements intermédiaires doivent être coalescés sans retarder le premier ni le dernier.");
    }

    private static async Task CancelOnlyForRuntimeShutdownAsync()
    {
        using VerificationEnvironment environment = new();
        CancellationAwareVerificationService service = new();
        using LauncherOperationCoordinator operations = new();
        GameRuntimeCoordinator coordinator = environment.CreateCoordinator(service, operations);
        List<GameRuntimeSnapshot> snapshots = [];
        coordinator.SnapshotChanged += (_, args) => snapshots.Add(args.Snapshot);

        Equal(GameVerificationStartStatus.Started, coordinator.TryStartVerification(), "La vérification doit démarrer.");
        await service.Started.Task;
        int beforeShutdown = snapshots.Count;
        coordinator.BeginShutdown();
        await coordinator.WaitForIdleAsync();

        True(service.ObservedCancellation, "Le token de cycle de vie doit interrompre la vérification.");
        Equal(beforeShutdown, snapshots.Count, "Aucun snapshot tardif ne doit être publié après fermeture.");
        Equal(GameVerificationStartStatus.ShuttingDown, coordinator.TryStartVerification(), "Une nouvelle analyse doit être refusée après fermeture.");
        True(
            coordinator.GetType().GetMethods().All(method =>
                !method.Name.Contains("CancelFromUser", StringComparison.Ordinal)
                && !method.Name.Contains("CancelCurrent", StringComparison.Ordinal)),
            "02C ne doit exposer aucune annulation utilisateur.");
    }

    private static async Task RefreshAvailabilityAfterSessionRestoreAsync()
    {
        using VerificationEnvironment environment = new();
        FakeLauncherAuthService authentication = new();
        authentication.RestoreHandler = _ =>
        {
            authentication.Session = FakeLauncherAuthService.CreateSession();
            return Task.FromResult(true);
        };
        BlockingVerificationService service = new();
        LauncherRuntime runtime = new(new LauncherRuntimeDependencies
        {
            LoadSettings = () => environment.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = environment.StateReader,
            GetLauncherVersion = () => "v1.1.0-test",
            CreateAuthorizedHttpClient = _ => new HttpClient(new EmptyHttpHandler()),
            CreateGameVerificationService = (_, _) => service,
            HasPlayableClient = _ => true
        });

        try
        {
            True(!runtime.Game.CanVerify, "Vérifier doit attendre la restauration de session.");
            LauncherSessionRestoreResult restored = await runtime.InitializeAsync();
            Equal(LauncherSessionRestoreStatus.Restored, restored.Status, "La session témoin doit être restaurée.");
            True(runtime.Game.CanVerify, "Vérifier doit devenir disponible après restauration.");
            Equal(GameVerificationStartStatus.Started, runtime.Game.TryStartVerification(), "La commande réelle doit démarrer après auth.");
            await service.Started.Task;
        }
        finally
        {
            runtime.Dispose();
        }

        await runtime.Game.WaitForIdleAsync();
        True(service.ObservedCancellation, "La fermeture du runtime doit annuler l'analyse non annulable par l'utilisateur.");
    }

    private static void KeepPreviewVerificationSideEffectFree()
    {
        foreach (GamePreviewScenario scenario in Enum.GetValues<GamePreviewScenario>())
        {
            GameUiState preview = LauncherV2PreviewData.CreateGame(scenario);
            True(!preview.VerifyCommand.CanExecute(null), $"Vérifier doit être sans effet en preview {scenario}.");
            preview.VerifyCommand.Execute(null);
        }

        Equal(
            LauncherStartupMode.UiV2Preview,
            App.ResolveStartupMode(["--ui-v2", "--preview-state=Verifying"]),
            "Le preview doit rester dans sa branche isolée.");
    }

    private static Task VerifyWpfCommandAndAtomicPresentationAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfVerification(completion))
        {
            IsBackground = true,
            Name = "Atlas V2 verification WPF bindings"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunWpfVerification(TaskCompletionSource completion)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(async () =>
        {
            Application? application = null;
            Window? host = null;
            GameCommands? localCommands = null;
            GameVerificationCommand? verifyCommand = null;
            GameStateAdapter? adapter = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LoadV2Resources(application);
                using VerificationEnvironment environment = new();
                using LauncherOperationCoordinator operations = new();
                BlockingVerificationService service = new();
                GameRuntimeCoordinator coordinator = environment.CreateCoordinator(service, operations);
                GameUiState gameState = LauncherV2RuntimePresentation.CreateGame(environment.LocalState);
                LauncherLocalActionCoordinator localActions = new(
                    environment.Settings,
                    environment.LogPath,
                    new LauncherShellService(new RecordingProcessStarter()),
                    _ => { },
                    new ManualTimeProvider());
                localCommands = LauncherV2RuntimePresentation.ConnectLocalActions(gameState, localActions);
                verifyCommand = new GameVerificationCommand(coordinator);
                gameState.AttachVerifyCommand(verifyCommand.Command);
                adapter = new GameStateAdapter(gameState, coordinator, dispatcher);
                GameViewV2 view = new() { State = gameState };
                host = new Window
                {
                    Width = 1080,
                    Height = 680,
                    Left = -20000,
                    Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Opacity = 0,
                    Content = view
                };
                host.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
                host.Show();
                view.UpdateLayout();

                List<Button> buttons = FindVisualChildren<Button>(view).ToList();
                List<Button> verifyButtons = FindButtons(buttons, "Vérifier le client");
                True(verifyButtons.Count == 0,
                    "La page Jeu immersive ne doit plus dupliquer la vérification disponible dans Paramètres.");
                True(!gameState.VerifyCommand.CanExecute(null),
                    "Sans pipeline de réparation injecté, la commande manuelle doit rester désactivée.");
                True(!FindButtons(buttons, "Jouer").Single().IsEnabled, "Jouer doit rester désactivé.");
                True(FindButtons(buttons, "Options").Count == 0, "Options ne doit plus apparaître sur la page Jeu.");

                int groupedNotifications = 0;
                gameState.PropertyChanged += (_, args) =>
                {
                    if (string.IsNullOrEmpty(args.PropertyName))
                    {
                        groupedNotifications++;
                    }
                };

                Equal(GameVerificationStartStatus.Started, coordinator.TryStartVerification(), "L’analyse automatique doit démarrer hors de la commande manuelle.");
                await service.Started.Task;
                view.UpdateLayout();
                Equal(GamePreviewScenario.Verifying, gameState.Scenario, "La carte doit passer atomiquement à Verifying.");
                Equal("Vérification…", gameState.PrimaryActionLabel, "Le bouton principal ne doit pas rester Jouer.");
                True(gameState.IsProgressIndeterminate, "Sans comptage, la progression doit être indéterminée.");
                True(!gameState.VerifyCommand.CanExecute(null), "Le double clic doit être refusé pendant l'analyse.");
                Equal(GameVerificationStartStatus.Busy, coordinator.TryStartVerification(), "Le refus concurrent doit être immédiat.");
                Equal(1, service.Calls, "Un seul appel de service doit exister.");

                service.Release(ResultUpToDate());
                await coordinator.WaitForIdleAsync();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                view.UpdateLayout();
                Equal(GamePreviewScenario.Ready, gameState.Scenario, "Le succès doit revenir à Ready.");
                Equal("À jour", gameState.InstallBadgeText, "À jour doit apparaître après comparaison seulement.");
                True(!gameState.IsPrimaryActionEnabled, "Jouer doit rester désactivé après 02C.");
                True(groupedNotifications >= 2, "Les snapshots doivent être appliqués par notifications groupées.");

                adapter.Dispose();
                adapter = null;
                long sequenceBefore = coordinator.CurrentSnapshot.Sequence;
                coordinator.RefreshAuthenticationAvailability();
                Equal(sequenceBefore, coordinator.CurrentSnapshot.Sequence, "Aucun changement artificiel n'est attendu.");
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                adapter?.Dispose();
                verifyCommand?.Dispose();
                localCommands?.Dispose();
                host?.Close();
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
    }

    private static void LoadV2Resources(Application application)
    {
        foreach (string resourcePath in new[]
        {
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(resourcePath, UriKind.Relative)
            });
        }
    }

    private static List<Button> FindButtons(IEnumerable<Button> buttons, string automationName)
    {
        return buttons.Where(button => string.Equals(
            AutomationProperties.GetName(button),
            automationName,
            StringComparison.Ordinal)).ToList();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
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

    private static LauncherFile FileEntry(string path, long size, string hash)
    {
        return new LauncherFile { Path = path, Size = size, Sha256 = hash };
    }

    private static GameClientVerificationResult ResultUpToDate()
    {
        return new GameClientVerificationResult(
            GameVerificationOutcome.UpToDate,
            GameAction.Play,
            GameUpdateKnowledge.Known,
            "known-v1",
            0);
    }

    private static void SequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string message)
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

internal sealed class VerificationEnvironment : IDisposable
{
    internal VerificationEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "Atlas Verification 02C", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Settings = new LauncherSettings
        {
            InstallPath = Root,
            ManifestUrl = "https://atlas.test/manifest.json",
            GameLocale = "frFR",
            AutomaticLauncherUpdates = false
        };
        Store = new InstalledManifestStore(_ => true);
        StateReader = new GameClientStateReader(_ => true);
        Verifier = new GameFileVerifier(Store, StateReader, _ => true);
        LocalState = new GameClientLocalState(
            Root,
            "frFR",
            true,
            "installed-v1",
            GameUpdateKnowledge.Unknown);
        LogPath = Path.Combine(Root, "logs", "launcher.log");
    }

    internal string Root { get; }

    internal string LogPath { get; }

    internal LauncherSettings Settings { get; }

    internal InstalledManifestStore Store { get; }

    internal GameClientStateReader StateReader { get; }

    internal GameFileVerifier Verifier { get; }

    internal GameClientLocalState LocalState { get; }

    internal GameClientVerificationService CreateService(IGameManifestClient manifestClient)
    {
        return new GameClientVerificationService(
            manifestClient,
            Verifier,
            Store,
            _ => true,
            _ => false);
    }

    internal GameRuntimeCoordinator CreateCoordinator(
        IGameClientVerificationService service,
        LauncherOperationCoordinator operations,
        Action<string>? writeLog = null,
        TimeProvider? timeProvider = null)
    {
        GameRuntimeCoordinator coordinator = new(
            service,
            operations,
            Settings,
            LocalState,
            () => true,
            writeLog ?? (_ => { }),
            _ => true,
            timeProvider ?? new ManualTimeProvider());
        coordinator.RefreshAuthenticationAvailability();
        return coordinator;
    }

    internal string WriteFile(string relativePath, string content)
    {
        string path = GamePathPolicy.GetSafeTargetPath(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    internal void WriteClientVersion(string version)
    {
        File.WriteAllText(
            Path.Combine(Root, GameInstallServices.ClientMarkerFileName),
            JsonSerializer.Serialize(new { clientVersion = version }));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class StubManifestClient : IGameManifestClient
{
    internal LauncherManifest Manifest { get; set; } = new();

    public Task<LauncherManifest> LoadAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Manifest);
    }
}

internal sealed class BlockingVerificationService : IGameClientVerificationService
{
    private readonly TaskCompletionSource<GameClientVerificationResult> _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Started { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal int Calls { get; private set; }

    internal bool ObservedCancellation { get; private set; }

    public async Task<GameClientVerificationResult> VerifyAsync(
        LauncherSettings settings,
        bool reportFileProgress,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        Calls++;
        reportProgress?.Invoke(new GameVerificationProgress(GameVerificationPhase.LoadingManifest));
        Started.TrySetResult();
        try
        {
            return await _release.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ObservedCancellation = true;
            throw;
        }
    }

    internal void Release(GameClientVerificationResult result)
    {
        _release.TrySetResult(result);
    }
}

internal sealed class CancellationAwareVerificationService : IGameClientVerificationService
{
    internal TaskCompletionSource Started { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool ObservedCancellation { get; private set; }

    public async Task<GameClientVerificationResult> VerifyAsync(
        LauncherSettings settings,
        bool reportFileProgress,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
        catch (OperationCanceledException)
        {
            ObservedCancellation = true;
            throw;
        }
    }
}

internal sealed class ThrowingVerificationService(Exception exception) : IGameClientVerificationService
{
    public Task<GameClientVerificationResult> VerifyAsync(
        LauncherSettings settings,
        bool reportFileProgress,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        return Task.FromException<GameClientVerificationResult>(exception);
    }
}

internal sealed class ImmediateProgressVerificationService : IGameClientVerificationService
{
    public Task<GameClientVerificationResult> VerifyAsync(
        LauncherSettings settings,
        bool reportFileProgress,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        for (int index = 1; index <= 4; index++)
        {
            reportProgress?.Invoke(new GameVerificationProgress(
                GameVerificationPhase.ScanningFiles,
                index,
                4));
        }

        return Task.FromResult(new GameClientVerificationResult(
            GameVerificationOutcome.UpToDate,
            GameAction.Play,
            GameUpdateKnowledge.Known,
            "progress-v1",
            0));
    }
}

internal sealed class EmptyHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
