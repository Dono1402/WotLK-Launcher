using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;
using Ellipse = System.Windows.Shapes.Ellipse;

internal static class SettingsPreviewTests
{
    private static readonly (SettingsCategory Category, string ButtonName, string PanelName)[] CategoryControls =
    [
        (SettingsCategory.General, "GeneralCategoryButton", "GeneralPanel"),
        (SettingsCategory.Game, "GameCategoryButton", "GamePanel"),
        (SettingsCategory.Updates, "UpdatesCategoryButton", "UpdatesPanel"),
        (SettingsCategory.Notifications, "NotificationsCategoryButton", "NotificationsPanel"),
        (SettingsCategory.Diagnostic, "DiagnosticCategoryButton", "DiagnosticPanel")
    ];

    private static uint _observedWindowDpi;

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizePreviewStartupIsolation();
        CharacterizeReadOnlyPreviewState();
        CharacterizePreviewScenarios();
        await ValidateWpfLayoutsNavigationAndCapturesAsync(captureDirectory);
        Console.WriteLine($"Settings WPF preview OK (02G.2 isolation, window DPI={_observedWindowDpi}).");
        return 0;
    }

    private static void CharacterizePreviewStartupIsolation()
    {
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-settings"]),
            "preview-settings sans --ui-v2 doit être refusé avant composition.");
        Equal(
            LauncherStartupMode.UiV2SettingsPreview,
            App.ResolveStartupMode(["--ui-v2", "--preview-settings=game"]),
            "preview-settings doit utiliser sa branche isolée.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-settings", "--preview-auth=login"]),
            "Settings et Auth preview ne doivent pas composer deux expériences simultanées.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-settings", "--preview-profile=signed-in"]),
            "Settings et Profile preview ne doivent pas être combinés.");
        Equal(
            LauncherStartupMode.UiV2,
            App.ResolveStartupMode([]),
            "Le lancement sans argument doit ouvrir la V2 réelle.");
        Equal(
            LauncherStartupMode.UiV2,
            App.ResolveStartupMode(["--ui-v2"]),
            "La V2 réelle doit conserver sa branche distincte.");
    }

    private static void CharacterizeReadOnlyPreviewState()
    {
        SettingsUiState state = LauncherV2PreviewData.CreateSettings();
        Equal(@"C:\Program Files (x86)\WotLK", state.Current.Game.InstallPath, "Le chemin fictif est incorrect.");
        Equal("Français", state.Current.Game.GameLanguage, "La langue du jeu fictive est incorrecte.");
        Equal("Français", state.Current.General.InterfaceLanguage, "La langue du launcher fictive est incorrecte.");
        Equal("fr-FR", state.Current.General.InterfaceLocale, "La locale du launcher fictive est incorrecte.");
        True(!state.Current.General.StartWithWindows, "Le preview doit montrer le démarrage Windows inactif.");
        True(state.Current.General.MinimizeToTrayOnClose, "Le preview doit montrer la réduction à la fermeture active.");
        True(state.Current.Notifications.FriendPresence, "Les connexions d'amis doivent être visibles dans le preview.");
        Equal("v1.1.0", state.Current.Updates.InstalledLauncherVersion, "La version launcher doit rester cohérente.");

        True(!state.Current.IsRuntimeConnected, "Le preview ne doit pas se déclarer connecté au runtime.");
        SettingsViewState before = state.Current;
        state.BrowseInstallPathCommand.Execute(null);
        state.OpenGameFolderCommand.Execute(null);
        state.OpenLogsCommand.Execute(null);
        True(ReferenceEquals(before, state.Current), "Les commandes preview ne doivent modifier aucun état.");
        True(!state.TryChangeInterfaceLocale("en-US"), "Le preview ne doit pas persister une langue réelle.");
        True(!state.TryChangeStartWithWindows(true), "Le preview ne doit pas toucher au démarrage Windows.");
        True(!state.TryChangeMinimizeToTrayOnClose(false), "Le preview ne doit pas modifier la fermeture réelle.");
        True(!state.TryChangeFriendPresenceNotifications(false), "Le preview ne doit pas modifier les notifications réelles.");
        True(!state.TryChangeGameLocale("enUS"), "Le preview ne doit pas accepter une langue réelle.");
    }

    private static void CharacterizePreviewScenarios()
    {
        Equal(SettingsPreviewScenario.General, Resolve("--preview-settings"), "Le scénario par défaut doit être Général.");
        Equal(SettingsPreviewScenario.Game, Resolve("--preview-settings=game"), "Le scénario Jeu est absent.");
        Equal(SettingsPreviewScenario.Updates, Resolve("--preview-settings=updates"), "Le scénario Mises à jour est absent.");
        Equal(SettingsPreviewScenario.Notifications, Resolve("--preview-settings=notifications"), "Le scénario Notifications est absent.");
        Equal(SettingsPreviewScenario.General, Resolve("--preview-settings=appearance"), "L'ancien scénario Apparence doit revenir sur Général.");
        Equal(SettingsPreviewScenario.Diagnostic, Resolve("--preview-settings=diagnostic"), "Le scénario Diagnostic est absent.");
        Equal(SettingsPreviewScenario.Dirty, Resolve("--preview-settings=dirty"), "Le scénario de modifications est absent.");
        Equal(SettingsPreviewScenario.Saving, Resolve("--preview-settings=saving"), "Le scénario d'enregistrement est absent.");
        Equal(SettingsPreviewScenario.Saved, Resolve("--preview-settings=saved"), "Le scénario enregistré est absent.");
        Equal(SettingsPreviewScenario.SaveError, Resolve("--preview-settings=save-error"), "Le scénario d'erreur est absent.");

        Equal(SettingsCategory.Game, LauncherV2PreviewData.CreateSettings(SettingsPreviewScenario.Game).Current.InitialCategory, "Game doit cibler Jeu.");
        Equal(SettingsCategory.Updates, LauncherV2PreviewData.CreateSettings(SettingsPreviewScenario.Updates).Current.InitialCategory, "Updates doit cibler Mises à jour.");
        Equal(SettingsSavePreviewState.Dirty, LauncherV2PreviewData.CreateSettings(SettingsPreviewScenario.Dirty).Current.SavePreviewState, "Dirty doit exposer la barre d'action.");
        Equal(SettingsSavePreviewState.Saving, LauncherV2PreviewData.CreateSettings(SettingsPreviewScenario.Saving).Current.SavePreviewState, "Saving doit rester purement visuel.");
        Equal(SettingsSavePreviewState.Saved, LauncherV2PreviewData.CreateSettings(SettingsPreviewScenario.Saved).Current.SavePreviewState, "Saved doit rester purement visuel.");
        Equal(SettingsSavePreviewState.Error, LauncherV2PreviewData.CreateSettings(SettingsPreviewScenario.SaveError).Current.SavePreviewState, "SaveError doit rester purement visuel.");

        static SettingsPreviewScenario Resolve(string argument)
        {
            return SettingsPreviewArguments.ResolveScenario(["--ui-v2", argument]);
        }
    }

    private static async Task ValidateWpfLayoutsNavigationAndCapturesAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasSettingsPreviewWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static void RunWpfHarness(TaskCompletionSource completion, string? captureDirectory)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
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
                await ValidateToggleAnimationAsync(application);
                await ValidateRequestedLayoutsAsync(captureDirectory);
                await ValidateLocalNavigationAsync();
                await ValidateSavePreviewStatesAsync();
                await ValidateEnglishInterfaceAsync(captureDirectory);
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

    private static async Task ValidateToggleAnimationAsync(Application application)
    {
        ToggleButton toggle = new()
        {
            Width = 44,
            Height = 24,
            Style = (Style)application.FindResource("AtlasV2.Toggle")
        };
        Window host = new()
        {
            Width = 100,
            Height = 80,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.ToolWindow,
            Content = toggle
        };

        host.Show();
        try
        {
            await PumpAsync(DispatcherPriority.Loaded);
            toggle.ApplyTemplate();
            Ellipse thumb = toggle.Template.FindName("Thumb", toggle) as Ellipse
                ?? throw new InvalidOperationException("Le curseur de l'interrupteur est absent.");
            TranslateTransform translation = thumb.RenderTransform as TranslateTransform
                ?? throw new InvalidOperationException("La translation animée de l'interrupteur est absente.");

            Near(0, translation.X, 0.5,
                "L'interrupteur désactivé doit commencer à gauche.");
            toggle.IsChecked = true;
            await DelayAndPumpAsync(230);
            Near(20, translation.X, 0.5,
                "L'interrupteur activé doit animer son curseur vers la droite.");

            toggle.IsChecked = false;
            await DelayAndPumpAsync(210);
            Near(0, translation.X, 0.5,
                "L'interrupteur désactivé doit animer son curseur vers la gauche.");
        }
        finally
        {
            host.Close();
        }
    }

    private static async Task ValidateEnglishInterfaceAsync(string? captureDirectory)
    {
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        LauncherShellV2 window = CreateSettingsWindow(
            1440,
            860,
            SettingsPreviewScenario.General,
            GamePreviewScenario.Launching);
        window.Show();
        try
        {
            await DelayAndPumpAsync(100);
            LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
            await DelayAndPumpAsync(100);

            SettingsViewV2 settings = window.SettingsPage;
            Equal("Settings", Required<TextBlock>(settings, "PageTitle").Text,
                "Le titre Settings doit basculer en anglais immédiatement.");
            Equal("General", System.Windows.Automation.AutomationProperties.GetName(
                    Required<Button>(settings, "GeneralCategoryButton")),
                "La navigation des paramètres doit être traduite.");
            Equal("Help and diagnostics", System.Windows.Automation.AutomationProperties.GetName(
                    Required<Button>(settings, "DiagnosticCategoryButton")),
                "Le diagnostic doit être traduit.");
            Equal("Browse", Required<Button>(settings, "BrowseInstallPathButton").Content as string,
                "Parcourir doit être traduit en anglais sans remplacer son libellé français d'origine.");
            GameViewV2 game = Required<GameViewV2>(window, "GameView");
            Equal("Launching game", Required<TextBlock>(game, "PrimaryActionLabelText").Text,
                "Le bouton de lancement dynamique doit basculer réellement en anglais.");
            Equal("Game server online", Required<TextBlock>(game, "RealmStatusText").Text,
                "Le statut déplacé du serveur doit lui aussi suivre la langue de l'interface.");

            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                SavePng(window, Path.Combine(
                    captureDirectory,
                    "08-settings-general-en-1440x860.png"));
            }

            LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
            await DelayAndPumpAsync(80);
            Equal("Paramètres", Required<TextBlock>(settings, "PageTitle").Text,
                "Le retour au français doit restaurer le libellé d'origine.");
            Equal("Parcourir", Required<Button>(settings, "BrowseInstallPathButton").Content as string,
                "Le retour au français doit restaurer Parcourir après un passage en anglais.");
            Equal("En cours de lancement", Required<TextBlock>(game, "PrimaryActionLabelText").Text,
                "Le retour au français doit restaurer le libellé dynamique du lancement.");
            Equal("Serveur de jeu en ligne", Required<TextBlock>(game, "RealmStatusText").Text,
                "Le retour au français doit restaurer le statut du serveur.");
        }
        finally
        {
            window.Close();
            LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateRequestedLayoutsAsync(string? captureDirectory)
    {
        (string FileName, double Width, double Height, AdaptiveLayoutMode Mode, SettingsPreviewScenario Scenario, SettingsCategory Category, bool ShowsActionBar)[] layouts =
        [
            ("01-settings-general-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, SettingsPreviewScenario.General, SettingsCategory.General, false),
            ("02-settings-game-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, SettingsPreviewScenario.Game, SettingsCategory.Game, false),
            ("03-settings-updates-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, SettingsPreviewScenario.Updates, SettingsCategory.Updates, false),
            ("04-settings-notifications-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, SettingsPreviewScenario.Notifications, SettingsCategory.Notifications, false),
            ("04-settings-diagnostic-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, SettingsPreviewScenario.Diagnostic, SettingsCategory.Diagnostic, false),
            ("05-settings-game-1080x680.png", 1080, 680, AdaptiveLayoutMode.Stacked, SettingsPreviewScenario.Game, SettingsCategory.Game, false),
            ("06-settings-general-1920x1080.png", 1920, 1080, AdaptiveLayoutMode.Wide, SettingsPreviewScenario.General, SettingsCategory.General, false),
            ("07-settings-unsaved-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, SettingsPreviewScenario.Dirty, SettingsCategory.General, true)
        ];

        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
        }

        foreach ((string fileName, double width, double height, AdaptiveLayoutMode expectedMode, SettingsPreviewScenario scenario, SettingsCategory expectedCategory, bool showsActionBar) in layouts)
        {
            LauncherShellV2 window = CreateSettingsWindow(width, height, scenario);
            window.Show();
            try
            {
                await DelayAndPumpAsync(240);
                RecordDpi(window);
                ValidateCommonVisualContract(window, expectedCategory, showsActionBar);
                Equal(expectedMode, window.ShellState.LayoutMode, $"Layout inattendu à {width}x{height}.");

                SettingsViewV2 settings = window.SettingsPage;
                ColumnDefinition navigationColumn = Required<ColumnDefinition>(settings, "NavigationColumn");
                double expectedNavigationWidth = expectedMode switch
                {
                    AdaptiveLayoutMode.Wide => 224,
                    AdaptiveLayoutMode.Compact => 212,
                    _ => 176
                };
                Near(expectedNavigationWidth, navigationColumn.ActualWidth, 0.6, "La navigation secondaire n'a pas la largeur attendue.");

                Grid contentFrame = Required<Grid>(settings, "ContentFrame");
                True(contentFrame.ActualWidth <= 1280.5, "Le contenu Wide ne doit pas être étiré excessivement.");
                Equal(
                    ScrollBarVisibility.Disabled,
                    settings.ScrollHost.HorizontalScrollBarVisibility,
                    "Aucune barre horizontale n'est autorisée.");
                True(settings.ScrollHost.ScrollableWidth <= 0.5, "Le contenu ne doit pas déborder horizontalement.");

                if (width >= 1900)
                {
                    True(contentFrame.ActualWidth >= 1270, "La largeur maximale doit être utilisée sur grand écran.");
                    Rect contentBounds = BoundsInAncestor(contentFrame, window);
                    True(contentBounds.Left > 250, "Le contenu 1920 doit être visiblement centré.");
                    True(window.ActualWidth - contentBounds.Right > 250, "Le vide latéral 1920 doit être équilibré.");
                }

                if (showsActionBar)
                {
                    FrameworkElement actionBar = Required<Border>(settings, "SettingsActionBar");
                    Rect bounds = BoundsInAncestor(actionBar, window);
                    True(bounds.Bottom <= window.ActualHeight + 0.5, "La barre Enregistrer/Annuler doit rester accessible.");
                }

                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    SavePng(window, Path.Combine(captureDirectory, fileName));
                }
            }
            finally
            {
                window.Close();
                await PumpAsync(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateLocalNavigationAsync()
    {
        LauncherShellV2 window = new(GamePreviewScenario.Ready)
        {
            Width = 1440,
            Height = 860,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        window.Show();
        try
        {
            await DelayAndPumpAsync(180);
            Equal(LauncherShellPage.Game, window.CurrentPage, "Le preview standard doit rester sur Jeu.");
            Button settingsButton = Required<Button>(window, "SettingsButton");
            Button gameButton = Required<Button>(window, "GameNavigationButton");
            RaiseClick(settingsButton);
            await PumpAsync(DispatcherPriority.Input);
            Equal(LauncherShellPage.Settings, window.CurrentPage, "Le bouton engrenage doit ouvrir la maquette locale.");

            SettingsViewV2 settings = window.SettingsPage;
            foreach ((SettingsCategory category, string buttonName, _) in CategoryControls)
            {
                RaiseClick(Required<Button>(settings, buttonName));
                await PumpAsync(DispatcherPriority.Input);
                ValidateSelectedCategory(settings, category);
            }

            RaiseClick(gameButton);
            await PumpAsync(DispatcherPriority.Input);
            Equal(LauncherShellPage.Game, window.CurrentPage, "Jeu doit ramener au tableau de bord preview.");
            Equal(Visibility.Visible, Required<GameViewV2>(window, "GameView").Visibility, "GameView doit redevenir visible.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateSavePreviewStatesAsync()
    {
        (SettingsPreviewScenario Scenario, string Label, bool ProgressVisible, bool ButtonsVisible)[] states =
        [
            (SettingsPreviewScenario.Dirty, "Modifications non enregistrées", false, true),
            (SettingsPreviewScenario.Saving, "Enregistrement…", true, true),
            (SettingsPreviewScenario.Saved, "Enregistré", false, false),
            (SettingsPreviewScenario.SaveError, "Erreur d’enregistrement", false, true)
        ];

        foreach ((SettingsPreviewScenario scenario, string expectedLabel, bool progressVisible, bool buttonsVisible) in states)
        {
            LauncherShellV2 window = CreateSettingsWindow(1080, 680, scenario);
            window.Show();
            try
            {
                await DelayAndPumpAsync(100);
                SettingsViewV2 settings = window.SettingsPage;
                Equal(Visibility.Visible, Required<Border>(settings, "SettingsActionBar").Visibility, "L'état de sauvegarde doit afficher sa barre.");
                Equal(expectedLabel, Required<TextBlock>(settings, "SettingsActionStatusText").Text, "Libellé d'enregistrement incohérent.");
                Equal(progressVisible ? Visibility.Visible : Visibility.Collapsed, Required<ProgressBar>(settings, "SettingsActionProgress").Visibility, "Progression de sauvegarde incohérente.");
                Equal(buttonsVisible ? Visibility.Visible : Visibility.Collapsed, Required<StackPanel>(settings, "SettingsActionButtons").Visibility, "Actions de sauvegarde incohérentes.");
                True(settings.ScrollHost.ScrollableWidth <= 0.5, "La barre d'action ne doit pas provoquer de débordement horizontal.");
                Rect bounds = BoundsInAncestor(Required<Border>(settings, "SettingsActionBar"), window);
                True(bounds.Bottom <= window.ActualHeight + 0.5, "La barre d'action doit rester dans la fenêtre compacte.");
            }
            finally
            {
                window.Close();
                await PumpAsync(DispatcherPriority.Background);
            }
        }
    }

    private static LauncherShellV2 CreateSettingsWindow(
        double width,
        double height,
        SettingsPreviewScenario scenario,
        GamePreviewScenario gameScenario = GamePreviewScenario.Ready)
    {
        return new LauncherShellV2(gameScenario, scenario)
        {
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
    }

    private static void ValidateCommonVisualContract(
        LauncherShellV2 window,
        SettingsCategory expectedCategory,
        bool showsActionBar)
    {
        True(window.IsPreviewMode, "SettingsViewV2 doit rester dans une fenêtre preview.");
        True(!window.HasRealAuthenticationAttached, "Le preview ne doit attacher aucun service réel.");
        Equal(LauncherShellPage.Settings, window.CurrentPage, "Le preview Settings doit ouvrir sa page directement.");
        Equal(Visibility.Collapsed, Required<GameViewV2>(window, "GameView").Visibility, "GameView ne doit pas être visible derrière Settings.");
        Equal(Visibility.Visible, window.SettingsPage.Visibility, "SettingsViewV2 doit être visible.");

        Button settingsButton = Required<Button>(window, "SettingsButton");
        Equal("Active", settingsButton.Tag as string, "Le bouton Paramètres doit montrer l'état actif.");
        True(settingsButton.IsEnabled, "La navigation Paramètres doit être active dans le preview uniquement.");

        SettingsViewV2 settings = window.SettingsPage;
        ValidateSelectedCategory(settings, expectedCategory);
        Equal(showsActionBar ? Visibility.Visible : Visibility.Collapsed, Required<Border>(settings, "SettingsActionBar").Visibility, "Visibilité inattendue de la barre d'action.");
        True(!Required<Button>(settings, "BrowseInstallPathButton").IsHitTestVisible, "Parcourir ne doit lancer aucun dialogue en preview.");
        True(!Required<Button>(settings, "OpenGameFolderButton").IsHitTestVisible, "Ouvrir le jeu ne doit lancer aucun processus en preview.");
        True(!Required<Button>(settings, "VerifyRepairButton").IsHitTestVisible, "Vérifier ne doit lancer aucun pipeline en preview.");
        True(!Required<Button>(settings, "OpenLogsButton").IsHitTestVisible, "Ouvrir les journaux ne doit lancer aucun processus en preview.");
        Equal(@"C:\Program Files (x86)\WotLK", Required<TextBlock>(settings, "InstallPathText").Text, "Le dossier fictif doit être lisible.");
        Equal("frFR", Required<ComboBox>(settings, "GameLanguageComboBox").SelectedValue as string, "La langue du jeu doit être visible.");
        Equal("fr-FR", Required<ComboBox>(settings, "InterfaceLanguageComboBox").SelectedValue as string, "La langue du launcher doit être visible.");
        True(Required<ToggleButton>(settings, "MinimizeToTrayOnCloseToggle").IsChecked == true, "La réduction à la fermeture doit être visuellement active.");
        True(Required<ToggleButton>(settings, "FriendPresenceNotificationsToggle").IsChecked == true, "La notification des connexions doit être visible.");
        Equal(System.Windows.Input.Cursors.Hand, Required<ToggleButton>(settings, "MinimizeToTrayOnCloseToggle").Cursor,
            "Les interrupteurs interactifs doivent afficher le curseur main.");
        True(settings.FindName("CheckLauncherUpdateButton") is null
             && settings.FindName("StartLauncherUpdateButton") is null,
            "Les actions manuelles de mise à jour ne doivent plus être affichées.");
        True(!ContainsText(settings, "Nouvelles demandes d’amis")
             && !ContainsText(settings, "Toujours activées"),
            "La notification automatique des demandes d'ami ne doit plus occuper une option dédiée.");
        Equal("v1.1.0", Required<TextBlock>(settings, "LauncherVersionText").Text, "La version launcher est absente.");
        Equal("3.4.3.54261", Required<TextBlock>(settings, "ClientVersionText").Text, "La version client est absente.");
    }

    private static void ValidateSelectedCategory(SettingsViewV2 settings, SettingsCategory expectedCategory)
    {
        Equal(expectedCategory, settings.SelectedCategory, "La catégorie sélectionnée est incohérente.");
        foreach ((SettingsCategory category, string buttonName, string panelName) in CategoryControls)
        {
            bool expected = category == expectedCategory;
            Equal(expected ? "Active" : null, Required<Button>(settings, buttonName).Tag as string, $"Sélection visuelle incorrecte pour {category}.");
            Equal(expected ? Visibility.Visible : Visibility.Collapsed, Required<StackPanel>(settings, panelName).Visibility, $"Une seule catégorie doit être rendue : {category}.");
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

    private static Rect BoundsInAncestor(FrameworkElement element, Visual ancestor)
    {
        return element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private static void RecordDpi(Window window)
    {
        uint dpi = GetDpiForWindow(new WindowInteropHelper(window).Handle);
        if (_observedWindowDpi == 0)
        {
            _observedWindowDpi = dpi;
        }
        else
        {
            Equal(_observedWindowDpi, dpi, "Toutes les fenêtres doivent utiliser la même session DPI réelle.");
        }
    }

    private static T Required<T>(FrameworkElement root, string name)
        where T : class
    {
        return root.FindName(name) as T
            ?? throw new InvalidOperationException($"Contrôle WPF absent : {name}.");
    }

    private static bool ContainsText(DependencyObject root, string expected)
    {
        if (root is TextBlock textBlock
            && string.Equals(textBlock.Text, expected, StringComparison.Ordinal))
        {
            return true;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            if (ContainsText(VisualTreeHelper.GetChild(root, index), expected))
            {
                return true;
            }
        }

        return false;
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

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; obtenu={actual}.");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
