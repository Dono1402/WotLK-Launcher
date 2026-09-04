using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

internal static class LauncherLocalActionTests
{
    internal static async Task<int> RunAsync()
    {
        OpenExistingGameFolderWithSpaces();
        RejectInvalidGameFolderPathsWithoutCreatingThem();
        TranslateWindowsFailuresAndSanitizeLogs();
        PreventRapidRepeatedFolderClicks();
        await KeepCommandAvailabilityIndependentAsync();
        SelectExistingDiagnosticLog();
        FallBackToExistingDiagnosticDirectory();
        ReportMissingDiagnosticWithoutCreatingAnything();
        SurviveDiagnosticLoggerFailure();
        RejectActionsDuringRuntimeShutdown();
        KeepPreviewCommandsSideEffectFree();
        await VerifyWpfBindingsAndIntegratedNotificationAsync();
        Console.WriteLine("Launcher local shell actions OK (02B).");
        return 0;
    }

    internal static int RunWindowsSmoke()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This smoke test requires Windows Explorer.");
            return 2;
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "Atlas Launcher 02B Windows Smoke",
            Guid.NewGuid().ToString("N"));
        string gameFolder = Path.Combine(root, "Game Client With Spaces");
        string logDirectory = Path.Combine(root, "Launcher Logs With Spaces");
        string logPath = Path.Combine(logDirectory, "launcher log.txt");
        Directory.CreateDirectory(gameFolder);
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(logPath, "Atlas Launcher 02B Windows smoke");
        ManualTimeProvider clock = new();
        LauncherLocalActionCoordinator coordinator = new(
            new LauncherSettings { InstallPath = gameFolder },
            logPath,
            LauncherShellService.CreateProduction(),
            _ => { },
            clock);

        try
        {
            LauncherLocalActionResult folder = coordinator.OpenGameFolder();
            Console.WriteLine($"STEP 1 Dossier: {folder.Status}; target={gameFolder}");
            Console.WriteLine("Press Enter after checking the Explorer window.");
            Console.ReadLine();

            LauncherLocalActionResult existingLog = coordinator.OpenDiagnostic();
            Console.WriteLine($"STEP 2 Diagnostic existing: {existingLog.Status}; target={logPath}");
            Console.WriteLine("Press Enter after checking that the file is selected.");
            Console.ReadLine();

            File.Delete(logPath);
            clock.Advance(TimeSpan.FromSeconds(1));
            LauncherLocalActionResult missingLog = coordinator.OpenDiagnostic();
            Console.WriteLine($"STEP 3 Diagnostic absent: {missingLog.Status}; target={logDirectory}");
            Console.WriteLine("Press Enter after checking the folder fallback.");
            Console.ReadLine();

            return folder.Status == LauncherLocalActionStatus.Succeeded
                && existingLog.Status == LauncherLocalActionStatus.Succeeded
                && missingLog.Status == LauncherLocalActionStatus.Succeeded
                    ? 0
                    : 1;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void OpenExistingGameFolderWithSpaces()
    {
        using LocalActionEnvironment environment = new();
        Directory.CreateDirectory(environment.GameFolder);
        RecordingProcessStarter starter = new();
        LauncherLocalActionCoordinator coordinator = environment.CreateCoordinator(starter);

        LauncherLocalActionResult result = coordinator.OpenGameFolder();

        Equal(LauncherLocalActionStatus.Succeeded, result.Status, "Le dossier existant doit s'ouvrir.");
        Equal(1, starter.Requests.Count, "Un seul appel Explorer est attendu.");
        ProcessRequest request = starter.Requests.Single();
        Equal("explorer.exe", request.FileName, "Le service local doit utiliser Explorer.");
        True(request.UseShellExecute, "L'ouverture doit utiliser le shell Windows.");
        SequenceEqual([environment.GameFolder], request.Arguments, "Le chemin avec espaces doit rester un argument unique.");
        Equal(environment.GameFolder, environment.Settings.InstallPath, "La commande ne doit pas modifier les paramètres.");
    }

    private static void RejectInvalidGameFolderPathsWithoutCreatingThem()
    {
        RecordingProcessStarter starter = new();

        using (LocalActionEnvironment environment = new())
        {
            environment.Settings.InstallPath = string.Empty;
            LauncherLocalActionResult result = environment.CreateCoordinator(starter).OpenGameFolder();
            Equal(LauncherLocalFailureCategory.EmptyPath, result.FailureCategory, "Un chemin vide doit être explicite.");
        }

        using (LocalActionEnvironment environment = new())
        {
            string missing = Path.Combine(environment.Root, "Missing Client");
            environment.Settings.InstallPath = missing;
            LauncherLocalActionResult result = environment.CreateCoordinator(starter).OpenGameFolder();
            Equal(LauncherLocalFailureCategory.MissingTarget, result.FailureCategory, "Un dossier absent doit être refusé.");
            True(!Directory.Exists(missing), "Dossier ne doit jamais créer la cible absente.");
        }

        using (LocalActionEnvironment environment = new())
        {
            environment.Settings.InstallPath = "invalid\0path";
            LauncherLocalActionResult result = environment.CreateCoordinator(starter).OpenGameFolder();
            Equal(LauncherLocalFailureCategory.InvalidPath, result.FailureCategory, "Un chemin invalide doit être traduit.");
        }

        Equal(0, starter.Requests.Count, "Aucun chemin refusé ne doit lancer Explorer.");
    }

    private static void TranslateWindowsFailuresAndSanitizeLogs()
    {
        using LocalActionEnvironment environment = new();
        Directory.CreateDirectory(environment.GameFolder);
        RecordingProcessStarter starter = new()
        {
            ExceptionToThrow = new UnauthorizedAccessException("sensitive failure")
        };
        List<string> logs = [];
        LauncherLocalActionCoordinator coordinator = environment.CreateCoordinator(starter, logs.Add);

        LauncherLocalActionResult denied = coordinator.OpenGameFolder();

        Equal(LauncherLocalFailureCategory.AccessDenied, denied.FailureCategory, "Le refus d'acces doit être structuré.");
        Equal("UnauthorizedAccessException", denied.ExceptionType, "Seul le type d'exception est exposé.");
        True(!denied.UserMessage!.Contains("sensitive", StringComparison.Ordinal), "La notification ne doit pas exposer l'exception.");
        Equal(1, logs.Count, "L'échec local doit être journalisé une fois.");
        True(logs[0].Contains("operation=OpenGameFolder", StringComparison.Ordinal), "Le journal doit contenir l'opération.");
        True(logs[0].Contains("category=AccessDenied", StringComparison.Ordinal), "Le journal doit contenir la catégorie.");
        True(!logs[0].Contains(environment.Root, StringComparison.OrdinalIgnoreCase), "Le chemin utilisateur complet ne doit pas être journalisé.");

        starter.ExceptionToThrow = new Win32Exception(123, "raw explorer command");
        environment.Clock.Advance(TimeSpan.FromSeconds(1));
        LauncherLocalActionResult failed = coordinator.OpenGameFolder();
        Equal(LauncherLocalFailureCategory.ShellLaunchFailed, failed.FailureCategory, "Un autre échec Windows doit rester générique.");
        True(!failed.UserMessage!.Contains("raw", StringComparison.Ordinal), "La commande Explorer ne doit pas apparaître.");
    }

    private static void PreventRapidRepeatedFolderClicks()
    {
        using LocalActionEnvironment environment = new();
        Directory.CreateDirectory(environment.GameFolder);
        RecordingProcessStarter starter = new();
        LauncherLocalActionCoordinator coordinator = environment.CreateCoordinator(starter);

        LauncherLocalActionResult first = coordinator.OpenGameFolder();
        LauncherLocalActionResult second = coordinator.OpenGameFolder();

        Equal(LauncherLocalActionStatus.Succeeded, first.Status, "Le premier clic doit réussir.");
        Equal(LauncherLocalActionStatus.Busy, second.Status, "Le clic répété doit être refusé immédiatement.");
        Equal(1, starter.Requests.Count, "Le double clic ne doit ouvrir qu'une fenêtre.");

        environment.Clock.Advance(TimeSpan.FromSeconds(1));
        Equal(LauncherLocalActionStatus.Succeeded, coordinator.OpenGameFolder().Status, "Une action ultérieure doit rester possible.");
        Equal(2, starter.Requests.Count, "La protection courte ne doit pas bloquer définitivement l'action.");
    }

    private static async Task KeepCommandAvailabilityIndependentAsync()
    {
        using LocalActionEnvironment environment = new();
        Directory.CreateDirectory(environment.GameFolder);
        BlockingProcessStarter starter = new();
        LauncherLocalActionCoordinator coordinator = environment.CreateCoordinator(starter);

        Task<LauncherLocalActionResult> opening = Task.Run(coordinator.OpenGameFolder);
        True(starter.WaitUntilEntered(TimeSpan.FromSeconds(2)), "L'ouverture témoin n'a pas démarré.");
        True(!coordinator.CanOpenGameFolder, "Dossier doit être single-flight pendant son exécution.");
        True(coordinator.CanOpenDiagnostic, "Diagnostic doit conserver son propre CanExecute.");

        starter.Release();
        Equal(
            LauncherLocalActionStatus.Succeeded,
            (await opening).Status,
            "L'ouverture bloquée doit finir normalement après libération.");
        True(coordinator.CanOpenGameFolder, "Dossier doit redevenir disponible après l'exécution.");
    }

    private static void SelectExistingDiagnosticLog()
    {
        using LocalActionEnvironment environment = new();
        Directory.CreateDirectory(environment.LogDirectory);
        File.WriteAllText(environment.LogPath, "diagnostic");
        RecordingProcessStarter starter = new();

        LauncherLocalActionResult result = environment.CreateCoordinator(starter).OpenDiagnostic();

        Equal(LauncherLocalActionStatus.Succeeded, result.Status, "Le journal existant doit être sélectionné.");
        ProcessRequest request = starter.Requests.Single();
        SequenceEqual(
            ["/select,", environment.LogPath],
            request.Arguments,
            "Explorer doit recevoir le fichier avec espaces comme argument séparé.");
    }

    private static void FallBackToExistingDiagnosticDirectory()
    {
        using LocalActionEnvironment environment = new();
        Directory.CreateDirectory(environment.LogDirectory);
        RecordingProcessStarter starter = new();

        LauncherLocalActionResult result = environment.CreateCoordinator(starter).OpenDiagnostic();

        Equal(LauncherLocalActionStatus.Succeeded, result.Status, "Le dossier de logs existant doit servir de repli.");
        SequenceEqual(
            [environment.LogDirectory],
            starter.Requests.Single().Arguments,
            "Le repli doit ouvrir le dossier sans inventer de fichier.");
        True(!File.Exists(environment.LogPath), "Diagnostic ne doit pas créer le journal absent.");
    }

    private static void ReportMissingDiagnosticWithoutCreatingAnything()
    {
        using LocalActionEnvironment environment = new();
        RecordingProcessStarter starter = new();
        List<string> logs = [];

        LauncherLocalActionResult result = environment.CreateCoordinator(starter, logs.Add).OpenDiagnostic();

        Equal(LauncherLocalFailureCategory.NoJournal, result.FailureCategory, "L'absence totale de journal doit être distinguée.");
        Equal("Aucun journal n'est encore disponible.", result.UserMessage, "La notification demandée est incorrecte.");
        Equal(0, starter.Requests.Count, "Aucun Explorer ne doit être lancé sans cible.");
        Equal(0, logs.Count, "L'absence de journal ne doit pas créer son propre journal.");
        True(!Directory.Exists(environment.LogDirectory), "Diagnostic ne doit pas créer le dossier de logs.");
        True(!File.Exists(environment.LogPath), "Diagnostic ne doit pas créer de fichier.");
    }

    private static void SurviveDiagnosticLoggerFailure()
    {
        using LocalActionEnvironment environment = new();
        Directory.CreateDirectory(environment.LogDirectory);
        File.WriteAllText(environment.LogPath, "diagnostic");
        RecordingProcessStarter starter = new()
        {
            ExceptionToThrow = new UnauthorizedAccessException("denied")
        };
        LauncherLocalActionCoordinator coordinator = environment.CreateCoordinator(
            starter,
            _ => throw new IOException("logger failure"));

        LauncherLocalActionResult result = coordinator.OpenDiagnostic();

        Equal(LauncherLocalFailureCategory.AccessDenied, result.FailureCategory, "L'échec du logger ne doit pas remplacer le refus Windows.");
        True(File.Exists(environment.LogPath), "Le journal existant ne doit pas être supprimé.");
    }

    private static void RejectActionsDuringRuntimeShutdown()
    {
        using LocalActionEnvironment environment = new();
        Directory.CreateDirectory(environment.GameFolder);
        RecordingProcessStarter starter = new();
        FakeLauncherAuthService authentication = new();
        LauncherRuntime runtime = new(new LauncherRuntimeDependencies
        {
            LoadSettings = () => environment.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = new GameClientStateReader(_ => false),
            GetLauncherVersion = () => "v1.1.0-test",
            LocalShellService = new LauncherShellService(starter),
            GetLauncherLogPath = () => environment.LogPath,
            LocalActionTimeProvider = environment.Clock
        });

        True(runtime.LocalActions.CanOpenGameFolder, "Dossier doit être disponible avant l'arrêt.");
        True(runtime.LocalActions.CanOpenDiagnostic, "Diagnostic doit être disponible avant l'arrêt.");
        runtime.Dispose();
        runtime.Dispose();

        True(!runtime.LocalActions.CanOpenGameFolder, "Dossier doit refuser pendant l'arrêt.");
        True(!runtime.LocalActions.CanOpenDiagnostic, "Diagnostic doit refuser pendant l'arrêt.");
        Equal(LauncherLocalActionStatus.ShuttingDown, runtime.LocalActions.OpenGameFolder().Status, "Le refus Dossier doit être immédiat.");
        Equal(LauncherLocalActionStatus.ShuttingDown, runtime.LocalActions.OpenDiagnostic().Status, "Le refus Diagnostic doit être immédiat.");
        Equal(0, starter.Requests.Count, "L'arrêt ne doit lancer aucun processus.");
        Equal(1, authentication.DisposeCalls, "La fermeture du runtime doit rester idempotente.");
    }

    private static void KeepPreviewCommandsSideEffectFree()
    {
        RecordingProcessStarter starter = new();
        foreach (GamePreviewScenario scenario in Enum.GetValues<GamePreviewScenario>())
        {
            GameUiState preview = LauncherV2PreviewData.CreateGame(scenario);
            True(!preview.OpenGameFolderCommand.CanExecute(null), $"Dossier doit être désactivé en preview {scenario}.");
            True(!preview.OpenDiagnosticCommand.CanExecute(null), $"Diagnostic doit être désactivé en preview {scenario}.");
            preview.OpenGameFolderCommand.Execute(null);
            preview.OpenDiagnosticCommand.Execute(null);
        }

        Equal(0, starter.Requests.Count, "Le preview ne doit atteindre aucun service shell.");
        Equal(
            LauncherStartupMode.UiV2Preview,
            App.ResolveStartupMode(["--ui-v2", "--preview-state=Ready"]),
            "La branche preview doit court-circuiter la composition réelle.");
    }

    private static Task VerifyWpfBindingsAndIntegratedNotificationAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfBindings(completion))
        {
            IsBackground = true,
            Name = "Atlas V2 local action WPF bindings"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunWpfBindings(TaskCompletionSource completion)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(() =>
        {
            Application? application = null;
            Window? host = null;
            GameCommands? commands = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LoadV2Resources(application);
                using LocalActionEnvironment environment = new();
                GameClientLocalState localClient = new(
                    environment.GameFolder,
                    "frFR",
                    true,
                    "3.4.3-test",
                    GameUpdateKnowledge.Unknown);
                GameUiState gameState = LauncherV2RuntimePresentation.CreateGame(localClient);
                LauncherLocalActionCoordinator localActions = environment.CreateCoordinator(new RecordingProcessStarter());
                commands = LauncherV2RuntimePresentation.ConnectLocalActions(gameState, localActions);
                GameViewV2 view = new() { State = gameState };
                host = new Window
                {
                    Width = 1080,
                    Height = 680,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Content = view
                };
                host.Show();
                view.UpdateLayout();

                List<Button> buttons = FindVisualChildren<Button>(view).ToList();
                List<Button> folderButtons = FindButtons(buttons, "Ouvrir le dossier du client");
                List<Button> diagnosticButtons = FindButtons(buttons, "Ouvrir le diagnostic");
                True(folderButtons.Count == 0 && diagnosticButtons.Count == 0,
                    "La page Jeu immersive ne doit plus dupliquer Dossier et Diagnostic.");
                True(gameState.OpenGameFolderCommand.CanExecute(null),
                    "La commande Dossier doit rester disponible pour Paramètres.");
                True(gameState.OpenDiagnosticCommand.CanExecute(null),
                    "La commande Diagnostic doit rester disponible pour Paramètres.");

                List<Button> verifyButtons = FindButtons(buttons, "Vérifier le client");
                True(verifyButtons.Count == 0,
                    "La page Jeu ne doit plus dupliquer Vérifier depuis la carte Installation retirée.");
                True(!gameState.VerifyCommand.CanExecute(null),
                    "Sans coordinateur 02C, Vérifier doit conserver sa commande désactivée.");
                Button primary = FindButtons(buttons, "Jouer").Single();
                True(
                    !primary.IsEnabled
                    && ReferenceEquals(primary.Command, gameState.PrimaryActionCommand)
                    && !gameState.PrimaryActionCommand.CanExecute(null),
                    "Jouer doit rester désactivé tant qu'aucun runtime de jeu n'est raccordé.");
                True(FindButtons(buttons, "Options").Count == 0,
                    "Le bouton Options retiré ne doit plus apparaître dans la page Jeu.");
                True(!application.Windows.OfType<MainWindow>().Any(), "La V2 locale ne doit pas instancier MainWindow legacy.");

                gameState.OpenGameFolderCommand.Execute(null);
                view.UpdateLayout();
                Border notification = (Border)view.FindName("LocalNotification");
                True(gameState.ShowsNotification, "Un dossier absent doit publier une notification intégrée.");
                Equal(Visibility.Visible, notification.Visibility, "La notification WPF doit devenir visible.");
                True(!notification.IsHitTestVisible, "La notification ne doit pas bloquer la fenêtre.");
                True(!gameState.NotificationMessage.Contains("Exception", StringComparison.Ordinal), "La notification ne doit pas contenir d'exception brute.");

                gameState.ClearNotification();
                view.UpdateLayout();
                Equal(Visibility.Collapsed, notification.Visibility, "La notification doit pouvoir être retirée sans timer.");
                commands.Dispose();
                commands = null;
                True(!gameState.OpenGameFolderCommand.CanExecute(null), "Une commande libérée doit refuser l'exécution.");
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                commands?.Dispose();
                host?.Close();
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
    }

    private static void LoadV2Resources(Application application)
    {
        string[] resourcePaths =
        [
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        ];

        foreach (string resourcePath in resourcePaths)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(resourcePath, UriKind.Relative)
            });
        }
    }

    private static List<Button> FindButtons(IEnumerable<Button> buttons, string automationName)
    {
        return buttons
            .Where(button => string.Equals(
                AutomationProperties.GetName(button),
                automationName,
                StringComparison.Ordinal))
            .ToList();
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

    private static void SequenceEqual(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message} Attendu=[{string.Join(", ", expected)}]; actuel=[{string.Join(", ", actual)}].");
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

internal sealed class LocalActionEnvironment : IDisposable
{
    internal LocalActionEnvironment()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "Atlas Local Actions 02B",
            Guid.NewGuid().ToString("N"));
        GameFolder = Path.Combine(Root, "Game Client With Spaces");
        LogDirectory = Path.Combine(Root, "Launcher Logs With Spaces");
        LogPath = Path.Combine(LogDirectory, "launcher log.txt");
        Settings = new LauncherSettings
        {
            InstallPath = GameFolder,
            AutomaticLauncherUpdates = false
        };
    }

    internal string Root { get; }

    internal string GameFolder { get; }

    internal string LogDirectory { get; }

    internal string LogPath { get; }

    internal LauncherSettings Settings { get; }

    internal ManualTimeProvider Clock { get; } = new();

    internal LauncherLocalActionCoordinator CreateCoordinator(
        ILauncherProcessStarter starter,
        Action<string>? writeLog = null)
    {
        return new LauncherLocalActionCoordinator(
            Settings,
            LogPath,
            new LauncherShellService(starter),
            writeLog ?? (_ => { }),
            Clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class RecordingProcessStarter : ILauncherProcessStarter
{
    internal List<ProcessRequest> Requests { get; } = [];

    internal Exception? ExceptionToThrow { get; set; }

    public void Start(ProcessStartInfo startInfo)
    {
        Requests.Add(new ProcessRequest(
            startInfo.FileName,
            startInfo.UseShellExecute,
            startInfo.ArgumentList.ToArray()));
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }
    }
}

internal sealed class BlockingProcessStarter : ILauncherProcessStarter
{
    private readonly ManualResetEventSlim _entered = new(initialState: false);
    private readonly ManualResetEventSlim _release = new(initialState: false);

    public void Start(ProcessStartInfo startInfo)
    {
        _entered.Set();
        _release.Wait(TimeSpan.FromSeconds(5));
    }

    internal bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

    internal void Release() => _release.Set();
}

internal sealed record ProcessRequest(
    string FileName,
    bool UseShellExecute,
    IReadOnlyList<string> Arguments);

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _timestamp);

    internal void Advance(TimeSpan elapsed)
    {
        Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
