using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static class LauncherSettingsRuntimeTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizeImmediatePersistence();
        CharacterizePersistenceRollback();
        CharacterizeLegacyAvailabilityRules();
        CharacterizeGameProjectionRefresh();
        await ValidateConnectedWpfSettingsAsync(captureDirectory);
        Console.WriteLine("Settings runtime integration OK (02G.2).");
        return 0;
    }

    private static void CharacterizeImmediatePersistence()
    {
        using TemporarySettingsRoot root = new();
        LauncherSettings settings = root.CreateSettings();
        using LauncherOperationCoordinator operations = new();
        List<LauncherSettingsChangeKind> changes = [];
        int saveCalls = 0;
        using LauncherSettingsCoordinator coordinator = new(
            settings,
            operations,
            saved =>
            {
                saveCalls++;
                Equal(settings, saved, "Le même objet de réglages doit rester autoritaire.");
            },
            changes.Add,
            static _ => { });

        string selectedPath = Path.Combine(root.Root, "client", "..");
        LauncherSettingsChangeResult path = coordinator.TrySetInstallPath(selectedPath);
        LauncherSettingsChangeResult locale = coordinator.TrySetGameLocale("ENus");
        LauncherSettingsChangeResult close =
            coordinator.TrySetCloseLauncherOnGameStart(true);

        Equal(LauncherSettingsChangeStatus.Saved, path.Status, "Le dossier doit être enregistré immédiatement.");
        Equal(LauncherSettingsChangeStatus.Saved, locale.Status, "La langue doit être enregistrée immédiatement.");
        Equal(LauncherSettingsChangeStatus.Saved, close.Status, "Le comportement doit être enregistré immédiatement.");
        Equal(3, saveCalls, "Chaque changement legacy doit déclencher une écriture immédiate.");
        Equal(Path.GetFullPath(Path.Combine(root.Root, "client", "..")), settings.InstallPath, "Le dossier doit utiliser la normalisation legacy.");
        Equal("enUS", settings.GameLocale, "La langue doit utiliser la normalisation legacy.");
        True(settings.CloseLauncherOnGameStart, "La fermeture après lancement doit partager l'objet runtime.");
        EqualSequence(
            new[]
            {
                LauncherSettingsChangeKind.InstallPath,
                LauncherSettingsChangeKind.GameLocale,
                LauncherSettingsChangeKind.CloseLauncherOnGameStart
            },
            changes.ToArray(),
            "Les projections doivent être rafraîchies après chaque écriture réussie.");
        Equal(LauncherSettingsSaveStatus.Saved, coordinator.CurrentSnapshot.SaveStatus, "Le dernier état doit confirmer l'écriture.");

        int beforeUnchanged = saveCalls;
        LauncherSettingsChangeResult unchanged =
            coordinator.TrySetCloseLauncherOnGameStart(true);
        Equal(LauncherSettingsChangeStatus.Unchanged, unchanged.Status, "Une valeur identique ne doit pas être réécrite.");
        Equal(beforeUnchanged, saveCalls, "Une valeur identique ne doit toucher aucun fichier.");
    }

    private static void CharacterizePersistenceRollback()
    {
        using TemporarySettingsRoot root = new();
        LauncherSettings settings = root.CreateSettings();
        string previousPath = settings.InstallPath;
        List<string> logs = [];
        using LauncherOperationCoordinator operations = new();
        using LauncherSettingsCoordinator coordinator = new(
            settings,
            operations,
            _ => throw new IOException("secret-value-must-not-be-logged"),
            static _ => { },
            logs.Add);

        LauncherSettingsChangeResult result = coordinator.TrySetInstallPath(
            Path.Combine(root.Root, "private-client-token"));

        Equal(LauncherSettingsChangeStatus.Failed, result.Status, "Une erreur disque doit être signalée.");
        Equal(previousPath, settings.InstallPath, "L'échec doit restaurer la valeur précédente.");
        Equal(previousPath, coordinator.CurrentSnapshot.InstallPath, "Le snapshot doit publier la valeur restaurée.");
        Equal(LauncherSettingsSaveStatus.Error, coordinator.CurrentSnapshot.SaveStatus, "L'UI doit recevoir un état d'erreur.");
        True(logs.Count == 1 && logs[0].Contains("IOException", StringComparison.Ordinal), "La catégorie technique doit être journalisée.");
        True(!logs[0].Contains("secret-value", StringComparison.Ordinal), "Le journal ne doit contenir ni message brut ni valeur sensible.");
        True(!logs[0].Contains("private-client-token", StringComparison.Ordinal), "Le chemin choisi ne doit pas être journalisé.");
    }

    private static void CharacterizeLegacyAvailabilityRules()
    {
        using TemporarySettingsRoot root = new();
        LauncherSettings settings = root.CreateSettings();
        using LauncherOperationCoordinator operations = new();
        using LauncherSettingsCoordinator coordinator = new(
            settings,
            operations,
            static _ => { },
            static _ => { },
            static _ => { });

        LauncherOperationStartResult start = operations.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true,
            clientIsPlayable: true);
        True(start.IsStarted, "Le test doit posséder un bail de maintenance.");
        Equal(
            LauncherSettingsChangeStatus.Busy,
            coordinator.TrySetInstallPath(Path.Combine(root.Root, "other")).Status,
            "Parcourir doit conserver le refus legacy pendant une opération annulable.");
        Equal(
            LauncherSettingsChangeStatus.Saved,
            coordinator.TrySetGameLocale("enUS").Status,
            "La langue reste enregistrable comme dans le legacy.");
        Equal(
            LauncherSettingsChangeStatus.Saved,
            coordinator.TrySetCloseLauncherOnGameStart(true).Status,
            "Le comportement reste enregistrable comme dans le legacy.");

        start.Lease!.Complete();
        Equal(
            LauncherSettingsChangeStatus.Saved,
            coordinator.TrySetInstallPath(Path.Combine(root.Root, "other")).Status,
            "Le dossier doit redevenir modifiable dès la libération du bail.");
        coordinator.BeginShutdown();
        Equal(
            LauncherSettingsChangeStatus.ShuttingDown,
            coordinator.TrySetGameLocale("frFR").Status,
            "Aucune écriture ne doit commencer pendant la fermeture.");
    }

    private static void CharacterizeGameProjectionRefresh()
    {
        using TemporarySettingsRoot root = new();
        string playablePath = Path.Combine(root.Root, "playable");
        LauncherSettings settings = root.CreateSettings();
        using LauncherOperationCoordinator operations = new();
        GameClientStateReader reader = new(path =>
            string.Equals(path, playablePath, StringComparison.OrdinalIgnoreCase));
        GameClientLocalState initial = reader.Read(settings);
        using GameRuntimeCoordinator game = new(
            new RuntimeVerificationStub(),
            operations,
            settings,
            initial,
            static () => false,
            static _ => { },
            path => string.Equals(path, playablePath, StringComparison.OrdinalIgnoreCase),
            readLocalState: () => reader.Read(settings));
        using LauncherSettingsCoordinator coordinator = new(
            settings,
            operations,
            static _ => { },
            change => game.RefreshLocalSettings(
                change == LauncherSettingsChangeKind.InstallPath),
            static _ => { });

        Equal(GameAction.Install, game.CurrentSnapshot.Action, "Le client initial doit être absent.");
        Equal(
            LauncherSettingsChangeStatus.Saved,
            coordinator.TrySetInstallPath(playablePath).Status,
            "Le nouveau dossier doit être accepté.");
        Equal(playablePath, game.CurrentSnapshot.InstallPath, "La page Jeu doit recevoir le nouveau dossier.");
        Equal(GameAction.Play, game.CurrentSnapshot.Action, "Le nouvel état local jouable doit être publié.");
        Equal(GameUpdateKnowledge.Unknown, game.CurrentSnapshot.UpdateKnowledge, "Un changement de dossier doit invalider uniquement la connaissance distante.");
    }

    private static async Task ValidateConnectedWpfSettingsAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasSettingsRuntimeWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static void RunWpfHarness(TaskCompletionSource completion, string? captureDirectory)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        dispatcher.UnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        };

        _ = RunAsync();
        Dispatcher.Run();
        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }

        async Task RunAsync()
        {
            Application? application = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                await ValidateWindowAsync(captureDirectory);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateWindowAsync(string? captureDirectory)
    {
        LauncherSettings settings = new()
        {
            InstallPath = @"C:\Program Files (x86)\WotLK",
            GameLocale = "frFR",
            AutomaticLauncherUpdates = true,
            CloseLauncherOnGameStart = false
        };
        using LauncherOperationCoordinator operations = new();
        using LauncherSettingsCoordinator settingsRuntime = new(
            settings,
            operations,
            static _ => { },
            static _ => { },
            static _ => { });
        SettingsGameRuntimeStub gameRuntime = new(settings.InstallPath, settings.GameLocale);
        SettingsDashboardRuntimeStub dashboardRuntime = new();
        SettingsUiState settingsState = new(SettingsStateAdapter.CreateInitialView(
            settingsRuntime.CurrentSnapshot,
            gameRuntime.CurrentSnapshot,
            dashboardRuntime.CurrentSnapshot,
            "v1.1.0",
            @"C:\Users\Dono\AppData\Local\WotLK Launcher\launcher.log"));
        ShellUiState shellState = new()
        {
            LauncherVersion = "v1.1.0",
            Username = "Dono1402",
            IsAuthenticated = true,
            IsGameNavigationEnabled = true,
            IsNavigationEnabled = true
        };
        GameUiState gameState = LauncherV2RuntimePresentation.CreateGame(
            new GameClientLocalState(
                settings.InstallPath,
                settings.GameLocale,
                true,
                "3.4.3.54261",
                GameUpdateKnowledge.Unknown));
        DashboardUiState dashboardState = LauncherV2RuntimePresentation.CreateDashboard();
        LauncherShellV2 window = new(
            shellState,
            gameState,
            dashboardState,
            LauncherV2RuntimePresentation.CreateFriends(),
            new ProfileUiState(),
            settingsState)
        {
            Width = 1440,
            Height = 860,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        FakeSettingsLocalActions localActions = new();
        FakeSettingsFolderPicker folderPicker = new(@"C:\Games\WotLK");
        FakeSettingsLocaleApplier localeApplier = new();
        using SettingsCommands commands = new(
            settingsState,
            settingsRuntime,
            localActions,
            window,
            static _ => { },
            folderPicker,
            localeApplier);
        using SettingsStateAdapter adapter = new(
            settingsState,
            settingsRuntime,
            gameRuntime,
            dashboardRuntime,
            "v1.1.0",
            @"C:\Users\Dono\AppData\Local\WotLK Launcher\launcher.log",
            window.Dispatcher);

        window.Show();
        try
        {
            await DelayAndPumpAsync(180);
            Button settingsButton = Required<Button>(window, "SettingsButton");
            True(settingsButton.IsEnabled, "La navigation Paramètres doit être active dans la V2 réelle.");
            RaiseClick(settingsButton);
            await PumpAsync(DispatcherPriority.ApplicationIdle);
            Equal(LauncherShellPage.Settings, window.CurrentPage, "Le bouton Paramètres doit ouvrir SettingsViewV2.");

            SettingsViewV2 view = window.SettingsPage;
            True(Required<Button>(view, "BrowseInstallPathButton").IsHitTestVisible, "Parcourir doit être interactif en V2 réelle.");
            True(Required<Button>(view, "OpenGameFolderButton").IsHitTestVisible, "Le dossier du jeu doit être interactif.");
            True(Required<Button>(view, "OpenLogsButton").IsHitTestVisible, "Les journaux doivent être interactifs.");
            True(!Required<Button>(view, "VerifyRepairButton").IsHitTestVisible, "Réparer reste hors du périmètre Settings 02G.2.");
            True(!Required<ToggleButton>(view, "AutomaticUpdatesToggle").IsHitTestVisible, "L'auto-update reste en lecture seule jusqu'à 02H.2.");

            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                SavePng(window, Path.Combine(captureDirectory, "01-settings-runtime-general-1440x860.png"));
            }

            view.SelectCategory(SettingsCategory.Game);
            await PumpAsync(DispatcherPriority.ApplicationIdle);
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                SavePng(window, Path.Combine(captureDirectory, "02-settings-runtime-game-1440x860.png"));
            }

            commands.BrowseInstallPathCommand.Execute(null);
            Equal(@"C:\Games\WotLK", settings.InstallPath, "Parcourir doit enregistrer le dossier sélectionné.");
            ComboBox language = Required<ComboBox>(view, "GameLanguageComboBox");
            language.SelectedValue = "enUS";
            await PumpAsync(DispatcherPriority.DataBind);
            Equal("enUS", settings.GameLocale, "Le ComboBox doit enregistrer la langue legacy.");
            Equal(1, localeApplier.Calls, "La configuration du jeu doit être appliquée une fois après l'écriture.");

            view.SelectCategory(SettingsCategory.General);
            ToggleButton closeToggle = Required<ToggleButton>(view, "CloseAfterLaunchToggle");
            closeToggle.IsChecked = true;
            closeToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, closeToggle));
            Equal(true, settings.CloseLauncherOnGameStart, "Le toggle doit modifier l'objet partagé par le lancement du jeu.");

            commands.OpenGameFolderCommand.Execute(null);
            commands.OpenLogsCommand.Execute(null);
            Equal(1, localActions.OpenGameFolderCalls, "Le dossier doit réutiliser l'action locale existante.");
            Equal(1, localActions.OpenDiagnosticCalls, "Les logs doivent réutiliser l'action locale existante.");

            Button gameButton = Required<Button>(window, "GameNavigationButton");
            RaiseClick(gameButton);
            Equal(LauncherShellPage.Game, window.CurrentPage, "Le retour vers Jeu doit fonctionner en V2 réelle.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void LoadV2Resources(Application application)
    {
        if (application.Resources.MergedDictionaries.Count > 0)
        {
            return;
        }

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

    private static void SavePng(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T Required<T>(FrameworkElement root, string name)
        where T : class
    {
        return root.FindName(name) as T
            ?? throw new InvalidOperationException($"Contrôle WPF absent : {name}.");
    }

    private static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; obtenu={actual}.");
        }
    }

    private static void EqualSequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{message} Attendu={string.Join(',', expected)}; obtenu={string.Join(',', actual)}.");
        }
    }

    private sealed class TemporarySettingsRoot : IDisposable
    {
        internal TemporarySettingsRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "AtlasSettingsRuntime",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal LauncherSettings CreateSettings()
        {
            return new LauncherSettings
            {
                InstallPath = Path.Combine(Root, "client-initial"),
                GameLocale = "frFR",
                AutomaticLauncherUpdates = true,
                CloseLauncherOnGameStart = false
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

internal sealed class SettingsGameRuntimeStub : IGamePrimaryActionRuntime
{
    internal SettingsGameRuntimeStub(string installPath, string gameLocale)
    {
        CurrentSnapshot = new GameRuntimeSnapshot(
            Sequence: 1,
            OperationId: null,
            Action: GameAction.Play,
            UpdateKnowledge: GameUpdateKnowledge.Unknown,
            Phase: GameVerificationPhase.Stable,
            IsVerifying: false,
            CanVerify: true,
            IsPlayable: true,
            InstallPath: installPath,
            InstalledVersion: "3.4.3.54261",
            AvailableVersion: null,
            ProcessedFileCount: null,
            TotalFileCount: null,
            FailureCategory: null,
            GameLocale: gameLocale,
            CanPrimaryAction: true);
    }

    public event EventHandler? AvailabilityChanged { add { } remove { } }
    public event EventHandler? PlayAuthenticationRequired { add { } remove { } }
    public event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged { add { } remove { } }
    public bool CanExecutePrimaryAction => true;
    public GameRuntimeSnapshot CurrentSnapshot { get; }
    public GamePrimaryActionStatus TryExecutePrimaryAction() => GamePrimaryActionStatus.Unsupported;
}

internal sealed class SettingsDashboardRuntimeStub : ILauncherDashboardRuntime
{
    public event EventHandler? AvailabilityChanged { add { } remove { } }
    public event EventHandler<DashboardSnapshotEventArgs>? SnapshotChanged { add { } remove { } }
    public DashboardSnapshot CurrentSnapshot { get; } = DashboardSnapshot.Initial with
    {
        Sequence = 1,
        RealmState = DashboardRealmState.Online,
        RealmStatusLabel = "En ligne"
    };
    public bool CanRefresh => false;
    public DashboardRefreshStartStatus TryRefresh() => DashboardRefreshStartStatus.Busy;
}

internal sealed class FakeSettingsLocalActions : ILauncherLocalActions
{
    public event EventHandler? AvailabilityChanged { add { } remove { } }
    public bool CanOpenGameFolder => true;
    public bool CanOpenDiagnostic => true;
    internal int OpenGameFolderCalls { get; private set; }
    internal int OpenDiagnosticCalls { get; private set; }

    public LauncherLocalActionResult OpenGameFolder()
    {
        OpenGameFolderCalls++;
        return new LauncherLocalActionResult(
            LauncherLocalAction.OpenGameFolder,
            LauncherLocalActionStatus.Succeeded,
            LauncherLocalFailureCategory.None,
            null);
    }

    public LauncherLocalActionResult OpenDiagnostic()
    {
        OpenDiagnosticCalls++;
        return new LauncherLocalActionResult(
            LauncherLocalAction.OpenDiagnostic,
            LauncherLocalActionStatus.Succeeded,
            LauncherLocalFailureCategory.None,
            null);
    }

    public void BeginShutdown()
    {
    }
}

internal sealed class FakeSettingsFolderPicker(string selectedPath) : ISettingsFolderPicker
{
    public string? SelectGameFolder(Window owner, string initialDirectory) => selectedPath;
}

internal sealed class FakeSettingsLocaleApplier : ISettingsGameLocaleApplier
{
    internal int Calls { get; private set; }

    public SettingsGameLocaleApplyResult Apply(
        Window owner,
        string installPath,
        string gameLocale)
    {
        Calls++;
        return new SettingsGameLocaleApplyResult(SettingsGameLocaleApplyStatus.Applied);
    }
}
