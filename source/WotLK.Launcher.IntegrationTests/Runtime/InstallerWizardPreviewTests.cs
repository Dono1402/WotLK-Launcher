using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.Installer.Setup;

internal static class InstallerWizardPreviewTests
{
    private static uint _observedWindowDpi;

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizePreviewStates();
        await RunWpfHarnessAsync(captureDirectory);
        Console.WriteLine(
            $"Atlas installer WPF preview OK (no system effects, window DPI={_observedWindowDpi}).");
        return 0;
    }

    internal static async Task<int> ShowAsync(string? scenarioName)
    {
        if (!TryParseScenario(scenarioName, out InstallerPreviewScenario scenario))
        {
            Console.Error.WriteLine(
                "Scénario inconnu. Valeurs : welcome, destination, options, ready, installing, completed, invalid-path, insufficient-space, existing-installation, install-error.");
            return 2;
        }

        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                Application application = new()
                {
                    ShutdownMode = ShutdownMode.OnMainWindowClose
                };
                InstallerWizardWindow window = new(scenario)
                {
                    Width = 1180,
                    Height = 740,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                window.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != Key.F12)
                    {
                        return;
                    }

                    e.Handled = true;
                    window.CloseForTest();
                };
                Console.WriteLine($"Preview installateur : {scenario}. F12 ferme le harnais de test.");
                application.Run(window);
                completion.TrySetResult(0);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "AtlasInstallerManualPreview"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task;
    }

    private static void CharacterizePreviewStates()
    {
        InstallerWizardViewState destination = InstallerWizardPreviewData.Create(
            InstallerPreviewScenario.Destination);
        Equal(
            @"C:\Program Files\Atlas Launcher",
            destination.InstallPath,
            "La distribution x64 doit proposer Program Files.");
        True(destination.CreateDesktopShortcut, "Le raccourci Bureau doit être coché par défaut.");
        True(destination.CreateStartMenuShortcut, "Le raccourci Démarrer doit être coché par défaut.");
        Equal("Oui", destination.DesktopShortcutSummary, "Le résumé Bureau par défaut doit être affirmatif.");
        Equal("Oui", destination.StartMenuShortcutSummary, "Le résumé Démarrer par défaut doit être affirmatif.");

        InstallerWizardViewState invalid = InstallerWizardPreviewData.Create(
            InstallerPreviewScenario.InvalidPath);
        Equal(InstallerNoticeKind.InvalidPath, invalid.Notice, "Le chemin invalide doit être explicite.");
        True(!invalid.CanPrimaryAction, "Un chemin invalide doit bloquer Suivant.");

        InstallerWizardViewState space = InstallerWizardPreviewData.Create(
            InstallerPreviewScenario.InsufficientSpace);
        True(!space.CanPrimaryAction, "Un disque trop petit doit bloquer Suivant.");

        InstallerWizardViewState existing = InstallerWizardPreviewData.Create(
            InstallerPreviewScenario.ExistingInstallation);
        True(existing.ShowExistingInstallation, "L’ancienne installation doit avoir un écran bloquant dédié.");
        Equal("Réessayer", existing.PrimaryActionLabel, "Aucune migration automatique ne doit être proposée.");
        True(existing.CanCancel && existing.CanCloseWindow && !existing.ShowBack,
            "L’ancienne installation doit proposer Réessayer ou Annuler, sans parcours de migration.");

        InstallerWizardViewState installing = InstallerWizardPreviewData.Create(
            InstallerPreviewScenario.Installing);
        True(!installing.CanCancel && !installing.CanCloseWindow && !installing.CanPrimaryAction,
            "Les commandes doivent être bloquées pendant la phase critique.");

        InstallerWizardViewState completed = InstallerWizardPreviewData.Create(
            InstallerPreviewScenario.Completed);
        True(completed.LaunchAfterInstall, "Le lancement final doit être coché par défaut.");
        Equal("Terminer", completed.PrimaryActionLabel, "Le bouton final doit être Terminer.");

        InstallerWizardViewState installError = InstallerWizardPreviewData.Create(
            InstallerPreviewScenario.InstallError);
        True(installError.CanPrimaryAction && installError.CanCancel
            && installError.CanCloseWindow && !installError.ShowBack,
            "Une erreur après rollback doit pouvoir être fermée ou relancée.");

        True(TryParseScenario("invalid-path", out InstallerPreviewScenario parsed)
            && parsed == InstallerPreviewScenario.InvalidPath,
            "Le mode manuel doit résoudre explicitement les scénarios de preview.");
        True(!TryParseScenario("production", out _),
            "Le mode manuel doit refuser tout scénario inconnu.");
    }

    private static async Task RunWpfHarnessAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasInstallerPreviewWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static void RunWpfHarness(
        TaskCompletionSource completion,
        string? captureDirectory)
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
                await ValidateLayoutsAndCapturesAsync(captureDirectory);
                await ValidateCompactLayoutsAsync();
                await ValidateLocalNavigationAsync();
                await ValidateKeyboardAndCriticalStateAsync();
                await ValidateLongPathLayoutAsync();
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

    private static async Task ValidateLayoutsAndCapturesAsync(string? captureDirectory)
    {
        (string FileName, InstallerPreviewScenario Scenario, double Width, double Height)[] layouts =
        [
            ("01-bienvenue-1440x860.png", InstallerPreviewScenario.Welcome, 1440, 860),
            ("02-dossier-1440x860.png", InstallerPreviewScenario.Destination, 1440, 860),
            ("03-options-1440x860.png", InstallerPreviewScenario.Options, 1440, 860),
            ("04-pret-a-installer-1440x860.png", InstallerPreviewScenario.Ready, 1440, 860),
            ("05-installation-1440x860.png", InstallerPreviewScenario.Installing, 1440, 860),
            ("06-termine-1440x860.png", InstallerPreviewScenario.Completed, 1440, 860),
            ("07-chemin-invalide-1440x860.png", InstallerPreviewScenario.InvalidPath, 1440, 860),
            ("08-espace-insuffisant-1440x860.png", InstallerPreviewScenario.InsufficientSpace, 1440, 860),
            ("09-ancienne-installation-1440x860.png", InstallerPreviewScenario.ExistingInstallation, 1440, 860),
            ("10-erreur-installation-1440x860.png", InstallerPreviewScenario.InstallError, 1440, 860),
            ("11-dossier-1080x680.png", InstallerPreviewScenario.Destination, 1080, 680)
        ];

        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
        }

        foreach ((string fileName, InstallerPreviewScenario scenario, double width, double height) in layouts)
        {
            InstallerWizardWindow window = CreateWindow(scenario, width, height);
            window.Show();
            try
            {
                await DelayAndPumpAsync(180);
                RecordDpi(window);
                ValidateVisualContract(window, scenario, width);
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    SavePng(window, Path.Combine(captureDirectory, fileName));
                }
            }
            finally
            {
                window.CloseForTest();
                await PumpAsync(DispatcherPriority.Background);
            }
        }
    }

    private static void ValidateVisualContract(
        InstallerWizardWindow window,
        InstallerPreviewScenario scenario,
        double width)
    {
        True(window.IsPreviewMode, "Le wizard de ce checkpoint doit rester une preview.");
        Equal(0, window.SystemEffectCount, "La preview ne doit produire aucun effet système.");
        True(
            window.FontFamily.Source.Contains("Manrope", StringComparison.OrdinalIgnoreCase),
            "La fenêtre doit utiliser Manrope embarqué.");
        True(window.ActualWidth >= width - 1, "La largeur demandée n’a pas été appliquée.");
        True(window.WizardScrollHost.ScrollableWidth <= 0.5, "Aucun débordement horizontal n’est permis.");
        True(window.ContentFrame.ActualWidth <= 900.5, "Le contenu ne doit pas s’étirer excessivement.");
        Near(width < 1120 ? 250 : 292, window.SidebarColumn.ActualWidth, 0.6,
            "La largeur adaptative de la progression est incorrecte.");
        True(
            window.PrimaryButton.ActualWidth >= window.PrimaryButton.MinWidth - 0.5,
            $"Le bouton principal doit rester lisible (ActualWidth={window.PrimaryButton.ActualWidth:0.##}, MinWidth={window.PrimaryButton.MinWidth:0.##}).");
        True(window.CloseButton.ActualWidth >= 44, "La commande Fermer doit rester accessible.");

        FrameworkElement expectedPanel = scenario switch
        {
            InstallerPreviewScenario.Welcome => window.WelcomePanel,
            InstallerPreviewScenario.Destination => window.DestinationPanel,
            InstallerPreviewScenario.Options => window.OptionsPanel,
            InstallerPreviewScenario.Ready => window.ReadyPanel,
            InstallerPreviewScenario.Installing => window.InstallingPanel,
            InstallerPreviewScenario.Completed => window.CompletedPanel,
            InstallerPreviewScenario.InvalidPath => window.DestinationPanel,
            InstallerPreviewScenario.InsufficientSpace => window.DestinationPanel,
            InstallerPreviewScenario.ExistingInstallation => window.ExistingInstallationPanel,
            InstallerPreviewScenario.InstallError => window.InstallErrorPanel,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        Equal(Visibility.Visible, expectedPanel.Visibility, $"Le panneau {scenario} doit être visible.");

        if (scenario is InstallerPreviewScenario.InvalidPath
            or InstallerPreviewScenario.InsufficientSpace)
        {
            Equal(Visibility.Visible, window.DestinationNotice.Visibility,
                "L’erreur de destination doit être visible.");
            True(!window.PrimaryButton.IsEnabled, "Suivant doit être désactivé pour une destination refusée.");
        }

        if (scenario == InstallerPreviewScenario.Installing)
        {
            True(!window.BackButton.IsEnabled && !window.CancelButton.IsEnabled
                && !window.PrimaryButton.IsEnabled && !window.CloseButton.IsEnabled,
                "La phase critique doit désactiver toutes les sorties du wizard.");
        }
    }

    private static async Task ValidateCompactLayoutsAsync()
    {
        foreach (InstallerPreviewScenario scenario in Enum.GetValues<InstallerPreviewScenario>())
        {
            InstallerWizardWindow window = CreateWindow(scenario, 1080, 680);
            window.Show();
            try
            {
                await DelayAndPumpAsync(80);
                ValidateVisualContract(window, scenario, 1080);
                True(window.WizardScrollHost.ViewportHeight > 0,
                    $"Le contenu compact de {scenario} doit rester accessible verticalement.");
            }
            finally
            {
                window.CloseForTest();
                await PumpAsync(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateLocalNavigationAsync()
    {
        InstallerWizardWindow window = CreateWindow(
            InstallerPreviewScenario.Welcome,
            1180,
            740,
            showActivated: true);
        window.Show();
        try
        {
            await DelayAndPumpAsync(120);
            Equal(InstallerWizardStep.Welcome, window.State.Current.Step, "Le départ doit être Bienvenue.");
            RaiseClick(window.PrimaryButton);
            await PumpAsync(DispatcherPriority.Input);
            Equal(InstallerWizardStep.Destination, window.State.Current.Step, "Suivant doit ouvrir Dossier.");

            RaiseClick(window.BrowseButton);
            await PumpAsync(DispatcherPriority.Input);
            Equal(@"D:\Applications\Atlas Launcher", window.State.Current.InstallPath,
                "Parcourir en preview doit rester une transition locale.");

            RaiseClick(window.PrimaryButton);
            await PumpAsync(DispatcherPriority.Input);
            Equal(InstallerWizardStep.Options, window.State.Current.Step, "Dossier doit mener aux options.");
            RaiseClick(window.DesktopShortcutCheckBox);
            await PumpAsync(DispatcherPriority.Input);
            True(!window.State.Current.CreateDesktopShortcut,
                "L’option Bureau doit être modifiable localement en preview.");
            Equal("Non", window.State.Current.DesktopShortcutSummary,
                "Le résumé doit refléter une option Bureau décochée.");
            RaiseClick(window.StartMenuShortcutCheckBox);
            await PumpAsync(DispatcherPriority.Input);
            True(!window.State.Current.CreateStartMenuShortcut,
                "L’option Démarrer doit être modifiable localement en preview.");

            RaiseClick(window.PrimaryButton);
            await PumpAsync(DispatcherPriority.Input);
            Equal(InstallerWizardStep.Ready, window.State.Current.Step, "Options doit mener au résumé.");
            Equal("Non", window.State.Current.DesktopShortcutSummary,
                "La valeur du raccourci Bureau doit être conservée jusqu’au résumé.");
            Equal("Non", window.State.Current.StartMenuShortcutSummary,
                "La valeur du raccourci Démarrer doit être conservée jusqu’au résumé.");
            RaiseClick(window.BackButton);
            await PumpAsync(DispatcherPriority.Input);
            Equal(InstallerWizardStep.Options, window.State.Current.Step, "Retour doit revenir aux options.");
            Equal(0, window.SystemEffectCount, "La navigation ne doit appeler aucun service système.");
        }
        finally
        {
            window.CloseForTest();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateKeyboardAndCriticalStateAsync()
    {
        InstallerWizardWindow welcome = CreateWindow(
            InstallerPreviewScenario.Welcome,
            1080,
            680,
            showActivated: true);
        welcome.Show();
        await DelayAndPumpAsync(120);
        True(welcome.PrimaryButton.IsDefault, "Entrée doit activer l’action principale.");
        True(welcome.CancelButton.IsTabStop && welcome.PrimaryButton.IsTabStop,
            "Les commandes doivent participer à la navigation Tab et Shift+Tab.");
        welcome.PrimaryButton.Focus();
        True(welcome.PrimaryButton.IsKeyboardFocusWithin,
            "Le focus initial doit être placé sur l’action principale.");
        True(
            welcome.PrimaryButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),
            "Tab doit déplacer le focus vers la commande suivante.");
        True(!welcome.PrimaryButton.IsKeyboardFocusWithin,
            "Tab ne doit pas laisser le focus sur l’action principale.");
        welcome.PrimaryButton.Focus();
        True(
            welcome.PrimaryButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)),
            "Shift+Tab doit déplacer le focus vers la commande précédente.");

        RaisePreviewKey(welcome, Key.Escape);
        await PumpAsync(DispatcherPriority.Background);
        True(!welcome.IsVisible, "Échap doit fermer le wizard avant l’installation.");

        InstallerWizardWindow cancelled = CreateWindow(
            InstallerPreviewScenario.Welcome,
            1080,
            680,
            showActivated: true);
        cancelled.Show();
        await DelayAndPumpAsync(80);
        RaiseClick(cancelled.CancelButton);
        await PumpAsync(DispatcherPriority.Background);
        True(!cancelled.IsVisible, "Annuler doit fermer le wizard avant l’installation.");

        InstallerWizardWindow installing = CreateWindow(
            InstallerPreviewScenario.Installing,
            1080,
            680,
            showActivated: true);
        installing.Show();
        try
        {
            await DelayAndPumpAsync(100);
            True(!installing.State.Current.CanCancel,
                "Échap doit être neutralisé pendant l’installation critique.");
            True(!installing.PrimaryButton.IsEnabled && !installing.BackButton.IsEnabled,
                "Entrée et Retour doivent être neutralisés pendant l’installation critique.");
            RaisePreviewKey(installing, Key.Escape);
            await PumpAsync(DispatcherPriority.Background);
            True(installing.IsVisible, "Échap ne doit pas interrompre une phase critique non annulable.");
            installing.Close();
            await PumpAsync(DispatcherPriority.Background);
            True(installing.IsVisible, "La fermeture Windows doit être bloquée pendant la phase critique.");
        }
        finally
        {
            installing.CloseForTest();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateLongPathLayoutAsync()
    {
        InstallerWizardWindow window = CreateWindow(
            InstallerPreviewScenario.Destination,
            1080,
            680);
        const string longPath = @"D:\Applications de Dono\Atlas Launcher édition spéciale\Versions locales";
        window.State.SetPreviewPath(longPath);
        window.Show();
        try
        {
            await DelayAndPumpAsync(100);
            Equal(longPath, window.InstallPathTextBox.Text,
                "Un chemin local long, accentué et contenant des espaces doit rester représentable.");
            True(window.WizardScrollHost.ScrollableWidth <= 0.5,
                "Un chemin long ne doit pas créer de défilement horizontal dans le wizard.");
        }
        finally
        {
            window.CloseForTest();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static InstallerWizardWindow CreateWindow(
        InstallerPreviewScenario scenario,
        double width,
        double height,
        bool showActivated = false) =>
        new(scenario)
        {
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = showActivated
        };

    private static bool TryParseScenario(
        string? value,
        out InstallerPreviewScenario scenario)
    {
        scenario = (value ?? "welcome").ToLowerInvariant() switch
        {
            "welcome" => InstallerPreviewScenario.Welcome,
            "destination" => InstallerPreviewScenario.Destination,
            "options" => InstallerPreviewScenario.Options,
            "ready" => InstallerPreviewScenario.Ready,
            "installing" => InstallerPreviewScenario.Installing,
            "completed" => InstallerPreviewScenario.Completed,
            "invalid-path" => InstallerPreviewScenario.InvalidPath,
            "insufficient-space" => InstallerPreviewScenario.InsufficientSpace,
            "existing-installation" => InstallerPreviewScenario.ExistingInstallation,
            "install-error" => InstallerPreviewScenario.InstallError,
            _ => (InstallerPreviewScenario)(-1)
        };
        return Enum.IsDefined(scenario);
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
            Equal(_observedWindowDpi, dpi, "Toutes les captures doivent utiliser la même session DPI.");
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

    private static void RaiseClick(ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static void RaisePreviewKey(Window window, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("La source WPF de la fenêtre est absente.");
        window.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority) =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);

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
