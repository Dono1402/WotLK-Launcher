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
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class SettingsPreviewTests
{
    private static uint _observedWindowDpi;

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizePreviewStartupIsolation();
        CharacterizeReadOnlyPreviewState();
        await ValidateWpfLayoutsNavigationAndCapturesAsync(captureDirectory);
        Console.WriteLine($"Settings WPF preview OK (02G.1, window DPI={_observedWindowDpi}).");
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
            App.ResolveStartupMode(["--ui-v2", "--preview-settings"]),
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
            LauncherStartupMode.Legacy,
            App.ResolveStartupMode([]),
            "Le lancement sans argument doit rester legacy.");
        Equal(
            LauncherStartupMode.UiV2,
            App.ResolveStartupMode(["--ui-v2"]),
            "La V2 réelle doit conserver sa branche distincte.");
    }

    private static void CharacterizeReadOnlyPreviewState()
    {
        SettingsUiState state = LauncherV2PreviewData.CreateSettings();
        Equal(@"C:\Program Files (x86)\WotLK", state.Current.InstallPath, "Le chemin fictif est incorrect.");
        Equal("Français", state.Current.GameLanguage, "La langue fictive est incorrecte.");
        True(state.Current.AutomaticLauncherUpdates, "Le preview doit montrer l'auto-update actif.");
        True(!state.Current.CloseLauncherAfterGameStart, "Le preview doit montrer la fermeture désactivée.");
        Equal("v1.1.0", state.Current.LauncherVersion, "La version launcher doit rester cohérente.");
        True(
            typeof(SettingsUiState).GetProperties().All(property =>
                !typeof(System.Windows.Input.ICommand).IsAssignableFrom(property.PropertyType)),
            "SettingsUiState ne doit exposer aucune commande métier en 02G.1.");
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
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
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
                await ValidateRequestedLayoutsAsync(captureDirectory);
                await ValidateLocalNavigationAsync();
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

    private static async Task ValidateRequestedLayoutsAsync(string? captureDirectory)
    {
        (string FileName, double Width, double Height, AdaptiveLayoutMode Mode)[] layouts =
        [
            ("01-settings-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide),
            ("02-settings-1080x680.png", 1080, 680, AdaptiveLayoutMode.Stacked),
            ("03-settings-1920x1080.png", 1920, 1080, AdaptiveLayoutMode.Wide)
        ];

        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
        }

        foreach ((string fileName, double width, double height, AdaptiveLayoutMode expectedMode) in layouts)
        {
            LauncherShellV2 window = CreateSettingsWindow(width, height);
            window.Show();
            try
            {
                await DelayAndPumpAsync(220);
                RecordDpi(window);
                ValidateCommonVisualContract(window);
                Equal(expectedMode, window.ShellState.LayoutMode, $"Layout inattendu à {width}x{height}.");

                SettingsViewV2 settings = window.SettingsPage;
                Grid secondary = Required<Grid>(settings, "SettingsColumns");
                StackPanel secondaryColumn = Required<StackPanel>(settings, "SecondarySettingsColumn");
                if (expectedMode == AdaptiveLayoutMode.Stacked)
                {
                    Equal(1, Grid.GetRow(secondaryColumn), "Les sections secondaires doivent être empilées à 1080 DIPs.");
                    Equal(0, Grid.GetColumn(secondaryColumn), "La pile compacte doit rester dans la colonne principale.");
                    True(
                        settings.ScrollHost.ExtentHeight > settings.ScrollHost.ViewportHeight,
                        "Le contenu 1080x680 doit défiler plutôt que se compresser.");
                }
                else
                {
                    Equal(0, Grid.GetRow(secondaryColumn), "Le mode Wide doit conserver deux colonnes.");
                    Equal(2, Grid.GetColumn(secondaryColumn), "Le panneau Comportement doit rester à droite.");
                }

                True(secondary.ActualWidth <= 1220.5, "Le contenu Wide ne doit pas être étiré excessivement.");
                Equal(
                    ScrollBarVisibility.Disabled,
                    settings.ScrollHost.HorizontalScrollBarVisibility,
                    "Aucune barre horizontale n'est autorisée.");
                True(settings.ScrollHost.ScrollableWidth <= 0.5, "Le contenu ne doit pas déborder horizontalement.");

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

    private static LauncherShellV2 CreateSettingsWindow(double width, double height)
    {
        return new LauncherShellV2(GamePreviewScenario.Ready, SettingsPreviewScenario.Default)
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

    private static void ValidateCommonVisualContract(LauncherShellV2 window)
    {
        True(window.IsPreviewMode, "SettingsViewV2 doit rester dans une fenêtre preview.");
        True(!window.HasRealAuthenticationAttached, "Le preview ne doit attacher aucun service réel.");
        Equal(LauncherShellPage.Settings, window.CurrentPage, "Le preview Settings doit ouvrir sa page directement.");
        Equal(Visibility.Collapsed, Required<GameViewV2>(window, "GameView").Visibility, "GameView ne doit pas être instancié visuellement derrière Settings.");
        Equal(Visibility.Visible, window.SettingsPage.Visibility, "SettingsViewV2 doit être visible.");

        Button settingsButton = Required<Button>(window, "SettingsButton");
        Equal("Active", settingsButton.Tag as string, "Le bouton Paramètres doit montrer l'état actif.");
        True(settingsButton.IsEnabled, "La navigation Paramètres doit être active dans le preview uniquement.");

        SettingsViewV2 settings = window.SettingsPage;
        Equal(@"C:\Program Files (x86)\WotLK", Required<TextBlock>(settings, "InstallPathText").Text, "Le dossier fictif doit être lisible.");
        Equal("Français", Required<TextBlock>(settings, "GameLanguageText").Text, "La langue doit être visible.");
        True(Required<ToggleButton>(settings, "AutomaticUpdatesToggle").IsChecked == true, "L'auto-update doit être visuellement actif.");
        True(Required<ToggleButton>(settings, "CloseAfterLaunchToggle").IsChecked == false, "La fermeture doit être visuellement inactive.");
        True(!Required<Button>(settings, "BrowseInstallPathButton").IsHitTestVisible, "Parcourir ne doit lancer aucun dialogue en 02G.1.");
        True(!Required<Button>(settings, "OpenLogsButton").IsHitTestVisible, "Ouvrir les logs ne doit lancer aucun processus en 02G.1.");
        Equal("v1.1.0", Required<TextBlock>(settings, "LauncherVersionText").Text, "La version launcher est absente.");
        Equal("3.4.3.54261", Required<TextBlock>(settings, "ClientVersionText").Text, "La version client est absente.");
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
        where T : FrameworkElement
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
