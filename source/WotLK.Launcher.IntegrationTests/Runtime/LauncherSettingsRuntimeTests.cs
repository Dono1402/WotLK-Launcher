using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        CharacterizeStartupRegistration();
        CharacterizeLegacyAvailabilityRules();
        CharacterizeGameProjectionRefresh();
        CharacterizeInstantQuestTextConfigFile();
        CharacterizeInstantQuestTextRuntimePersistence();
        await ValidateConnectedWpfSettingsAsync(captureDirectory);
        Console.WriteLine("Settings runtime integration OK (02G.2.1 + 04B.3b).");
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
        LauncherSettingsChangeResult interfaceLocale =
            coordinator.TrySetInterfaceLocale("en-GB");
        LauncherSettingsChangeResult startup = coordinator.TrySetStartWithWindows(true);
        LauncherSettingsChangeResult minimize =
            coordinator.TrySetMinimizeToTrayOnClose(false);
        LauncherSettingsChangeResult notifications =
            coordinator.TrySetFriendPresenceNotifications(false);
        LauncherSettingsChangeResult close =
            coordinator.TrySetCloseLauncherOnGameStart(true);

        Equal(LauncherSettingsChangeStatus.Saved, path.Status, "Le dossier doit être enregistré immédiatement.");
        Equal(LauncherSettingsChangeStatus.Saved, locale.Status, "La langue doit être enregistrée immédiatement.");
        Equal(LauncherSettingsChangeStatus.Saved, interfaceLocale.Status, "La langue d'interface doit être enregistrée immédiatement.");
        Equal(LauncherSettingsChangeStatus.Saved, startup.Status, "Le démarrage Windows doit être enregistré immédiatement.");
        Equal(LauncherSettingsChangeStatus.Saved, minimize.Status, "La fermeture dans la zone de notification doit être enregistrée immédiatement.");
        Equal(LauncherSettingsChangeStatus.Saved, notifications.Status, "Les notifications d'amis doivent être enregistrées immédiatement.");
        Equal(LauncherSettingsChangeStatus.Saved, close.Status, "Le comportement doit être enregistré immédiatement.");
        Equal(7, saveCalls, "Chaque changement doit déclencher une écriture immédiate.");
        Equal(Path.GetFullPath(Path.Combine(root.Root, "client", "..")), settings.InstallPath, "Le dossier doit utiliser la normalisation legacy.");
        Equal("enUS", settings.GameLocale, "La langue doit utiliser la normalisation legacy.");
        Equal("en-US", settings.InterfaceLocale, "La langue d'interface doit être normalisée.");
        True(settings.StartWithWindows, "Le démarrage Windows doit partager l'objet runtime.");
        True(!settings.MinimizeToTrayOnClose, "La préférence de fermeture doit partager l'objet runtime.");
        True(!settings.FriendPresenceNotifications, "La préférence d'amis doit partager l'objet runtime.");
        True(settings.CloseLauncherOnGameStart, "La fermeture après lancement doit partager l'objet runtime.");
        EqualSequence(
            new[]
            {
                LauncherSettingsChangeKind.InstallPath,
                LauncherSettingsChangeKind.GameLocale,
                LauncherSettingsChangeKind.InterfaceLocale,
                LauncherSettingsChangeKind.StartWithWindows,
                LauncherSettingsChangeKind.MinimizeToTrayOnClose,
                LauncherSettingsChangeKind.FriendPresenceNotifications,
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

    private static void CharacterizeStartupRegistration()
    {
        FakeLauncherStartupRegistry registry = new();
        registry.Values["Atlas Launcher Similar"] = "do-not-touch.exe";
        WindowsLauncherStartupRegistration registration = new(
            @"C:\Program Files\Atlas Launcher\AtlasLauncher.exe",
            registry,
            "Atlas Launcher Test");

        True(!registration.IsEnabled, "Une valeur absente ne doit pas être considérée active.");
        True(!registration.IsRegistered, "Une valeur absente ne doit pas être considérée inscrite.");
        LauncherStartupRegistrationResult enabled = registration.TrySetEnabled(true);
        True(enabled.IsApplied, "L'inscription HKCU simulée doit réussir.");
        Equal(
            "\"C:\\Program Files\\Atlas Launcher\\AtlasLauncher.exe\"",
            registry.Values["Atlas Launcher Test"],
            "Le chemin de démarrage doit être absolu et correctement cité.");
        True(registration.IsEnabled, "La valeur exacte doit être reconnue.");
        True(registration.IsRegistered, "La valeur exacte doit être détectée.");

        LauncherStartupRegistrationResult disabled = registration.TrySetEnabled(false);
        True(disabled.IsApplied, "La désactivation simulée doit réussir.");
        True(!registry.Values.ContainsKey("Atlas Launcher Test"),
            "Seule la valeur Atlas exacte doit être supprimée.");
        Equal("do-not-touch.exe", registry.Values["Atlas Launcher Similar"],
            "Une valeur portant un nom similaire ne doit jamais être modifiée.");

        registry.Values["Atlas Launcher Test"] = "\"C:\\Old Atlas\\AtlasLauncher.exe\"";
        True(registration.IsRegistered && !registration.IsEnabled,
            "Une ancienne cible doit être détectée sans être considérée valide.");
        _ = registration.TrySetEnabled(false);
        True(!registration.IsRegistered,
            "La désactivation doit retirer une ancienne cible portant le nom exact.");
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

    private static void CharacterizeInstantQuestTextConfigFile()
    {
        using TemporarySettingsRoot root = new();
        string configPath = Path.Combine(root.Root, "_classic_", "WTF", "Config.wtf");

        True(
            GameInstallServices.ReadInstantQuestText(root.Root),
            "Un Config.wtf absent doit conserver la valeur legacy active par défaut.");
        True(
            GameInstallServices.SetInstantQuestText(root.Root, enabled: false),
            "Désactiver doit créer uniquement la préférence demandée lorsque Config.wtf est absent.");
        True(File.Exists(configPath), "Config.wtf doit être créé au même emplacement legacy.");
        True(
            !GameInstallServices.ReadInstantQuestText(root.Root),
            "La valeur désactivée doit être relue immédiatement.");

        string[] preservedLines =
        [
            "SET gxWindow \"0\"",
            "SET customAtlasSetting \"kept\"",
            "SET instantQuestText \"0\"",
            "SET anotherSetting \"42\""
        ];
        File.WriteAllLines(configPath, preservedLines, new System.Text.UTF8Encoding(false));
        True(
            GameInstallServices.SetInstantQuestText(root.Root, enabled: true),
            "Activer doit remplacer la ligne existante.");
        string[] enabledLines = File.ReadAllLines(configPath);
        Equal("SET gxWindow \"0\"", enabledLines[0], "Le réglage précédent doit garder sa place et sa valeur.");
        Equal("SET customAtlasSetting \"kept\"", enabledLines[1], "Les réglages étrangers doivent être conservés.");
        Equal("SET instantQuestText \"1\"", enabledLines[2], "La valeur doit utiliser le format legacy 0/1.");
        Equal("SET anotherSetting \"42\"", enabledLines[3], "Aucune ligne suivante ne doit être supprimée.");

        string unchanged = File.ReadAllText(configPath);
        True(
            !GameInstallServices.SetInstantQuestText(root.Root, enabled: true),
            "Une valeur identique ne doit pas réécrire Config.wtf.");
        Equal(unchanged, File.ReadAllText(configPath), "Le contenu identique doit rester byte-for-byte stable.");

        _ = GameInstallServices.SetInstantQuestText(root.Root, enabled: false);
        _ = GameInstallServices.EnsureDefaultClientConfig(root.Root, "frFR");
        True(
            !GameInstallServices.ReadInstantQuestText(root.Root),
            "Une réécriture legacy de Config.wtf ne doit plus réactiver le texte instantané.");
    }

    private static void CharacterizeInstantQuestTextRuntimePersistence()
    {
        using TemporarySettingsRoot root = new();
        LauncherSettings settings = root.CreateSettings();
        using LauncherOperationCoordinator operations = new();
        bool storedValue = true;
        int settingsSaveCalls = 0;
        int configWriteCalls = 0;
        List<string> logs = [];
        using LauncherSettingsCoordinator coordinator = new(
            settings,
            operations,
            _ => settingsSaveCalls++,
            static _ => { },
            logs.Add,
            _ => storedValue,
            (_, value) =>
            {
                configWriteCalls++;
                storedValue = value;
                return true;
            });

        LauncherSettingsChangeResult disabled = coordinator.TrySetInstantQuestText(false);
        Equal(LauncherSettingsChangeStatus.Saved, disabled.Status, "Le réglage doit être enregistré immédiatement.");
        True(!coordinator.CurrentSnapshot.InstantQuestText, "Le snapshot doit refléter immédiatement la valeur écrite.");
        Equal(1, configWriteCalls, "Une seule écriture Config.wtf doit être effectuée.");
        Equal(0, settingsSaveCalls, "Le réglage du jeu ne doit pas réécrire les paramètres JSON du launcher.");

        LauncherSettingsChangeResult unchanged = coordinator.TrySetInstantQuestText(false);
        Equal(LauncherSettingsChangeStatus.Unchanged, unchanged.Status, "Une valeur identique doit être ignorée.");
        Equal(1, configWriteCalls, "La valeur identique ne doit pas toucher Config.wtf.");

        using LauncherSettingsCoordinator denied = new(
            settings,
            operations,
            static _ => { },
            static _ => { },
            logs.Add,
            static _ => true,
            static (_, _) => throw new UnauthorizedAccessException("secret-config-content"));
        LauncherSettingsChangeResult failure = denied.TrySetInstantQuestText(false);
        Equal(LauncherSettingsChangeStatus.Failed, failure.Status, "Un accès refusé doit être traduit en échec contrôlé.");
        True(denied.CurrentSnapshot.InstantQuestText, "L'échec doit restaurer la valeur précédente.");
        True(logs.Any(line => line.Contains("UnauthorizedAccessException", StringComparison.Ordinal)), "Seule la catégorie d'accès doit être journalisée.");
        True(logs.All(line => !line.Contains("secret-config-content", StringComparison.Ordinal)), "Le journal ne doit pas exposer le message brut.");
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
        bool instantQuestText = true;
        using LauncherSettingsCoordinator settingsRuntime = new(
            settings,
            operations,
            static _ => { },
            static _ => { },
            static _ => { },
            _ => instantQuestText,
            (_, enabled) =>
            {
                instantQuestText = enabled;
                return true;
            });
        SettingsGameRuntimeStub gameRuntime = new(settings.InstallPath, settings.GameLocale);
        SettingsDashboardRuntimeStub dashboardRuntime = new();
        SettingsSelfUpdateRuntimeStub selfUpdateRuntime = new();
        SettingsUiState settingsState = new(SettingsStateAdapter.CreateInitialView(
            settingsRuntime.CurrentSnapshot,
            gameRuntime.CurrentSnapshot,
            dashboardRuntime.CurrentSnapshot,
            "v1.1.0",
            @"C:\Users\Dono\AppData\Local\WotLK Launcher\launcher.log",
            selfUpdate: selfUpdateRuntime.CurrentSnapshot));
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
        using GameVerificationCommand verificationCommand = new(gameRuntime);
        gameState.AttachVerifyCommand(verificationCommand.Command);
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
        FakeSettingsGameConfigAccess gameConfigAccess = new();
        FakeLauncherStartupRegistration startupRegistration = new();
        using SettingsCommands commands = new(
            settingsState,
            settingsRuntime,
            localActions,
            window,
            static _ => { },
            folderPicker: folderPicker,
            localeApplier: localeApplier,
            gameConfigAccess: gameConfigAccess,
            startupRegistration: startupRegistration,
            verifyRepairCommand: verificationCommand.Command,
            showGameForRepair: window.ShowGamePageForSettingsOperation,
            selfUpdate: selfUpdateRuntime);
        using GameStateAdapter gameAdapter = new(
            gameState,
            gameRuntime,
            window.Dispatcher);
        using SettingsStateAdapter adapter = new(
            settingsState,
            settingsRuntime,
            gameRuntime,
            dashboardRuntime,
            "v1.1.0",
            @"C:\Users\Dono\AppData\Local\WotLK Launcher\launcher.log",
            window.Dispatcher,
            selfUpdateRuntime);

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
            Button repair = Required<Button>(view, "VerifyRepairButton");
            True(repair.IsHitTestVisible && repair.IsEnabled, "Réparer doit partager le CanExecute réel de la page Jeu.");
            True(ReferenceEquals(gameState.VerifyCommand, settingsState.VerifyRepairCommand), "Jeu et Paramètres doivent exposer exactement le même ICommand.");
            True(Required<ComboBox>(view, "InterfaceLanguageComboBox").IsHitTestVisible,
                "La langue du launcher doit être réellement modifiable.");
            True(Required<ToggleButton>(view, "StartWithWindowsToggle").IsHitTestVisible,
                "Le démarrage Windows doit être réellement modifiable.");
            True(Required<ToggleButton>(view, "MinimizeToTrayOnCloseToggle").IsHitTestVisible,
                "La fermeture dans la zone de notification doit être réellement modifiable.");
            True(Required<ToggleButton>(view, "FriendPresenceNotificationsToggle").IsHitTestVisible,
                "Les notifications de connexion doivent être réellement modifiables.");
            True(view.FindName("AppearanceCard") is null,
                "La catégorie Apparence retirée ne doit plus exister dans l'arbre WPF.");
            True(view.FindName("AutomaticUpdatesToggle") is null,
                "L'option de recherche automatique retirée ne doit plus être rendue.");
            True(view.FindName("ReleaseChannelControl") is null,
                "Le canal de publication retiré ne doit plus être rendu.");
            True(view.FindName("ClientUpdateBehaviorControl") is null,
                "Le comportement de mise à jour client retiré ne doit plus être rendu.");
            Equal(Visibility.Collapsed, Required<Border>(view, "SettingsActionBar").Visibility, "La barre de sauvegarde différée doit être absente en mode réel.");

            view.SelectCategory(SettingsCategory.Diagnostic);
            await PumpAsync(DispatcherPriority.DataBind);
            Equal("v1.1.0", Required<TextBlock>(view, "DiagnosticLauncherVersionText").Text,
                "La version complète du launcher doit rester disponible dans Diagnostic.");

            view.SelectCategory(SettingsCategory.Updates);
            await PumpAsync(DispatcherPriority.DataBind);
            Equal("v1.1.0", Required<TextBlock>(view, "LauncherVersionText").Text,
                "Settings doit afficher la version installée du coordinateur.");
            Equal("v1.2.0", Required<TextBlock>(view, "AvailableLauncherVersionText").Text,
                "Settings doit afficher la version réellement disponible.");
            True(view.FindName("CheckLauncherUpdateButton") is null
                 && view.FindName("StartLauncherUpdateButton") is null,
                "Les boutons manuels de mise à jour doivent être retirés de Settings.");
            True(settingsState.CheckLauncherUpdateCommand.CanExecute(null),
                "Le coordinateur de mise à jour doit rester fonctionnel sans bouton Settings.");

            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                SavePng(window, Path.Combine(captureDirectory, "03-settings-runtime-updates-1440x860.png"));
            }

            settingsState.CheckLauncherUpdateCommand.Execute(null);
            await PumpAsync(DispatcherPriority.DataBind);
            Equal(1, selfUpdateRuntime.CheckCalls,
                "Le bouton doit déléguer une seule fois au coordinateur partagé.");
            True(settingsState.Current.Updates.IsChecking,
                "L'état Checking doit être projeté sans créer une opération Activity.");
            True(!settingsState.CheckLauncherUpdateCommand.CanExecute(null),
                "Un deuxième check doit être refusé pendant la requête active.");
            selfUpdateRuntime.CompleteCheck();
            await PumpAsync(DispatcherPriority.DataBind);
            True(settingsState.CheckLauncherUpdateCommand.CanExecute(null),
                "La commande doit redevenir disponible après le check coalescé.");

            selfUpdateRuntime.PublishError(LauncherSelfUpdateErrorCategory.ManifestUnavailable);
            await PumpAsync(DispatcherPriority.DataBind);
            True(!settingsState.Current.Updates.IsUpdateAvailable,
                "Une erreur de manifeste doit retirer l'état de mise à jour disponible.");

            window.Width = 1080;
            window.Height = 680;
            await DelayAndPumpAsync(120);
            view.SelectCategory(SettingsCategory.Updates);
            view.ScrollHost.ScrollToTop();
            await PumpAsync(DispatcherPriority.ApplicationIdle);
            True(view.ScrollHost.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
                "Settings ne doit jamais afficher de barre horizontale à 1080x680.");
            True(view.ScrollHost.ExtentWidth <= view.ScrollHost.ViewportWidth + 1,
                "Le contenu Updates ne doit pas être coupé horizontalement à 1080x680.");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                SavePng(window, Path.Combine(captureDirectory, "04-settings-runtime-updates-1080x680.png"));
            }

            window.Width = 1440;
            window.Height = 860;
            await DelayAndPumpAsync(80);
            view.SelectCategory(SettingsCategory.General);
            await PumpAsync(DispatcherPriority.ApplicationIdle);

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

            settingsState.VerifyRepairCommand.Execute(null);
            RaiseClick(repair);
            await PumpAsync(DispatcherPriority.DataBind);
            Equal(1, gameRuntime.FullRepairCalls, "Un clic Paramètres doit démarrer une seule réparation complète.");
            Equal(LauncherShellPage.Game, window.CurrentPage, "La réparation doit ramener immédiatement vers la page Jeu.");
            Equal("Vérification complète", gameState.ProgressTitle, "La progression réelle doit être projetée sur la page Jeu.");
            Equal(25d, gameState.Progress, "Le comptage réel de la réparation doit rester visible.");
            settingsState.VerifyRepairCommand.Execute(null);
            Equal(1, gameRuntime.FullRepairCalls, "Une deuxième réparation concurrente doit être refusée immédiatement.");

            RaiseClick(settingsButton);
            view.SelectCategory(SettingsCategory.Game);
            ToggleButton instantToggle = Required<ToggleButton>(view, "InstantQuestTextToggle");
            True(instantToggle.IsHitTestVisible && instantToggle.IsEnabled, "Le texte instantané doit être réellement modifiable.");
            instantToggle.IsChecked = false;
            instantToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, instantToggle));
            True(!instantQuestText && !settingsRuntime.CurrentSnapshot.InstantQuestText, "Le toggle désactivé doit être écrit et publié immédiatement.");
            instantToggle.IsChecked = true;
            instantToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, instantToggle));
            True(instantQuestText && settingsRuntime.CurrentSnapshot.InstantQuestText, "Le toggle activé doit être écrit et publié immédiatement.");
            Equal(Visibility.Collapsed, Required<Border>(view, "SettingsActionBar").Visibility, "Aucun bouton Enregistrer ne doit apparaître après une écriture immédiate.");

            gameConfigAccess.Result = new SettingsGameConfigAccessResult(
                SettingsGameConfigAccessStatus.Failed,
                nameof(UnauthorizedAccessException));
            instantToggle.IsChecked = false;
            instantToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, instantToggle));
            await PumpAsync(DispatcherPriority.DataBind);
            True(instantQuestText && instantToggle.IsChecked == true, "Un accès refusé doit conserver et réafficher la valeur enregistrée.");
            Equal(Visibility.Visible, Required<Border>(view, "RuntimeNotice").Visibility, "L'accès refusé doit afficher une notification intégrée courte.");
            True(!Required<TextBlock>(view, "RuntimeNoticeText").Text.Contains("UnauthorizedAccessException", StringComparison.Ordinal), "L'interface ne doit afficher aucune exception brute.");

            commands.BrowseInstallPathCommand.Execute(null);
            Equal(@"C:\Games\WotLK", settings.InstallPath, "Parcourir doit enregistrer le dossier sélectionné.");
            ComboBox language = Required<ComboBox>(view, "GameLanguageComboBox");
            language.SelectedValue = "enUS";
            await PumpAsync(DispatcherPriority.DataBind);
            Equal("enUS", settings.GameLocale, "Le ComboBox doit enregistrer la langue legacy.");
            Equal(1, localeApplier.Calls, "La configuration du jeu doit être appliquée une fois après l'écriture.");

            view.SelectCategory(SettingsCategory.General);
            ComboBox interfaceLanguage = Required<ComboBox>(view, "InterfaceLanguageComboBox");
            interfaceLanguage.SelectedValue = "en-US";
            await PumpAsync(DispatcherPriority.ApplicationIdle);
            Equal("en-US", settings.InterfaceLocale,
                "Le choix anglais doit être persisté immédiatement.");
            Equal("Settings", Required<TextBlock>(view, "PageTitle").Text,
                "La V2 doit être traduite sans redémarrage.");
            True(BindingOperations.IsDataBound(
                    Required<TextBlock>(view, "LauncherVersionText"),
                    TextBlock.TextProperty),
                "La traduction ne doit pas détacher les bindings WPF.");
            interfaceLanguage.SelectedValue = "fr-FR";
            await PumpAsync(DispatcherPriority.ApplicationIdle);
            Equal("Paramètres", Required<TextBlock>(view, "PageTitle").Text,
                "Le retour au français doit restaurer les libellés exacts.");

            ToggleButton startupToggle = Required<ToggleButton>(view, "StartWithWindowsToggle");
            startupToggle.IsChecked = true;
            startupToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, startupToggle));
            True(settings.StartWithWindows && startupRegistration.IsEnabled,
                "Le toggle doit écrire la préférence et l'inscription de démarrage.");
            Equal(1, startupRegistration.SetCalls,
                "Le démarrage Windows ne doit être écrit qu'une fois.");

            ToggleButton trayToggle = Required<ToggleButton>(view, "MinimizeToTrayOnCloseToggle");
            trayToggle.IsChecked = false;
            trayToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, trayToggle));
            True(!settings.MinimizeToTrayOnClose,
                "La préférence de fermeture doit être enregistrée immédiatement.");

            view.SelectCategory(SettingsCategory.Notifications);
            ToggleButton presenceToggle = Required<ToggleButton>(
                view,
                "FriendPresenceNotificationsToggle");
            presenceToggle.IsChecked = false;
            presenceToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, presenceToggle));
            True(!settings.FriendPresenceNotifications,
                "La notification de connexion d'amis doit être désactivable.");

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

internal sealed class SettingsGameRuntimeStub : IGamePrimaryActionRuntime, IGameVerificationRuntime
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

    public event EventHandler? AvailabilityChanged;
    public event EventHandler? PlayAuthenticationRequired { add { } remove { } }
    public event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;
    public bool CanExecutePrimaryAction => CurrentSnapshot.CanPrimaryAction;
    public bool CanVerify => CurrentSnapshot.CanVerify;
    public GameRuntimeSnapshot CurrentSnapshot { get; private set; }
    internal int FullRepairCalls { get; private set; }
    public GamePrimaryActionStatus TryExecutePrimaryAction() => GamePrimaryActionStatus.Unsupported;

    public GameVerificationStartStatus TryStartVerification()
    {
        return GameVerificationStartStatus.RejectedByCompatibility;
    }

    public GameVerificationStartStatus TryStartFullRepair()
    {
        if (!CanVerify)
        {
            return GameVerificationStartStatus.Busy;
        }

        FullRepairCalls++;
        CurrentSnapshot = CurrentSnapshot with
        {
            Sequence = CurrentSnapshot.Sequence + 1,
            OperationId = 701,
            OperationKind = LauncherOperationKind.GameRepair,
            MaintenancePhase = GameClientMaintenancePhase.FullVerification,
            ProcessedFileCount = 1,
            TotalFileCount = 4,
            CanVerify = false,
            CanPrimaryAction = false,
            CanUserCancel = true
        };
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        SnapshotChanged?.Invoke(this, new GameRuntimeSnapshotEventArgs(CurrentSnapshot));
        return GameVerificationStartStatus.Started;
    }
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

internal sealed class SettingsSelfUpdateRuntimeStub : ILauncherSelfUpdateRuntime
{
    private TaskCompletionSource<LauncherSelfUpdateCheckResult>? _activeCheck;

    internal SettingsSelfUpdateRuntimeStub()
    {
        CurrentSnapshot = new LauncherSelfUpdateSnapshot(
            Sequence: 1,
            IsChecking: false,
            InstalledVersion: "v1.1.0",
            AvailableVersion: "v1.2.0",
            IsUpdateAvailable: true,
            IsUpdating: false,
            Phase: LauncherSelfUpdatePhase.None,
            Percent: null,
            BytesProcessed: null,
            BytesTotal: null,
            Speed: null,
            Eta: null,
            CanUserCancel: false,
            ErrorCategory: null,
            LastCheckedAt: new DateTimeOffset(2026, 9, 2, 12, 30, 0, TimeSpan.Zero));
    }

    public event EventHandler<LauncherSelfUpdateSnapshotEventArgs>? SnapshotChanged;
    public event EventHandler? AvailabilityChanged;
    public LauncherSelfUpdateSnapshot CurrentSnapshot { get; private set; }
    public long? CurrentOperationId => null;
    public bool CanCheck => _activeCheck is null && !CurrentSnapshot.IsUpdating;
    public bool CanStartUpdate => _activeCheck is null
        && CurrentSnapshot.IsUpdateAvailable
        && !CurrentSnapshot.IsUpdating;
    internal int CheckCalls { get; private set; }

    public Task<LauncherSelfUpdateCheckResult> CheckAsync()
    {
        if (_activeCheck is not null)
        {
            return _activeCheck.Task;
        }

        CheckCalls++;
        _activeCheck = new TaskCompletionSource<LauncherSelfUpdateCheckResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Publish(CurrentSnapshot with
        {
            Sequence = CurrentSnapshot.Sequence + 1,
            IsChecking = true,
            ErrorCategory = null
        });
        return _activeCheck.Task;
    }

    public LauncherSelfUpdateStartResult TryStartUpdate() =>
        new(LauncherSelfUpdateStartStatus.Busy, null);

    internal void CompleteCheck()
    {
        TaskCompletionSource<LauncherSelfUpdateCheckResult>? completion = _activeCheck;
        _activeCheck = null;
        Publish(CurrentSnapshot with
        {
            Sequence = CurrentSnapshot.Sequence + 1,
            IsChecking = false,
            LastCheckedAt = CurrentSnapshot.LastCheckedAt?.AddMinutes(1)
        });
        completion?.TrySetResult(new LauncherSelfUpdateCheckResult(
            LauncherSelfUpdateCheckOutcome.Completed));
    }

    internal void PublishError(LauncherSelfUpdateErrorCategory errorCategory)
    {
        Publish(CurrentSnapshot with
        {
            Sequence = CurrentSnapshot.Sequence + 1,
            IsChecking = false,
            AvailableVersion = null,
            IsUpdateAvailable = false,
            ErrorCategory = errorCategory
        });
    }

    private void Publish(LauncherSelfUpdateSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;
        SnapshotChanged?.Invoke(this, new LauncherSelfUpdateSnapshotEventArgs(snapshot));
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
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

internal sealed class FakeSettingsGameConfigAccess : ISettingsGameConfigAccess
{
    internal SettingsGameConfigAccessResult Result { get; set; } =
        new(SettingsGameConfigAccessStatus.Granted);

    public SettingsGameConfigAccessResult EnsureWritable(Window owner, string installPath)
    {
        return Result;
    }
}

internal sealed class FakeLauncherStartupRegistration : ILauncherStartupRegistration
{
    public bool IsRegistered { get; private set; }

    public bool IsEnabled { get; private set; }

    internal int SetCalls { get; private set; }

    internal bool FailChanges { get; set; }

    public LauncherStartupRegistrationResult TrySetEnabled(bool enabled)
    {
        SetCalls++;
        if (FailChanges)
        {
            return new LauncherStartupRegistrationResult(
                LauncherStartupRegistrationStatus.Failed,
                nameof(UnauthorizedAccessException));
        }

        IsEnabled = enabled;
        IsRegistered = enabled;
        return new LauncherStartupRegistrationResult(
            LauncherStartupRegistrationStatus.Applied);
    }
}

internal sealed class FakeLauncherStartupRegistry : ILauncherStartupRegistry
{
    internal Dictionary<string, string> Values { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string? Read(string valueName) =>
        Values.TryGetValue(valueName, out string? value) ? value : null;

    public void Write(string valueName, string command) =>
        Values[valueName] = command;

    public void Delete(string valueName) => Values.Remove(valueName);
}
