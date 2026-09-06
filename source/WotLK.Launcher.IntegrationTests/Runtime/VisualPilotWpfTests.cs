using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

/// <summary>
/// Uses the existing WPF preview construction and RenderTargetBitmap capture approach.
/// Only this executable contains the fixture data and the recording command.
/// The window never occupies the desktop, taskbar or keyboard focus.
/// </summary>
internal static class VisualPilotWpfTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasVisualPilotWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        Console.WriteLine("Visual pilot WPF OK: all nine Game preview scenarios, disabled command, native button invocation, navigation and offscreen captures. Desktop focus, tray and maximize interactions were not exercised.");
        return 0;
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
            completion.TrySetResult();
        else
            completion.TrySetException(failure);

        async Task RunAsync()
        {
            Application? application = null;
            LauncherShellV2? window = null;
            List<CaptureEvidence> captures = [];
            Stopwatch elapsed = Stopwatch.StartNew();
            BindingErrorListener bindingErrors = new();
            SourceLevels priorBindingLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
            try
            {
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LoadV2Resources(application);
                foreach (GamePreviewScenario scenario in Enum.GetValues<GamePreviewScenario>())
                {
                    window = CreateOffscreenWindow(scenario);
                    window.Show();
                    await PumpAsync(window);
                    AssertIsolated(window);
                    GameViewV2 game = Required<GameViewV2>(window, "GameView");
                    Button primary = Required<Button>(game, "PrimaryActionButton");
                    TextBlock label = Required<TextBlock>(game, "PrimaryActionLabelText");
                    Equal(window.GameState.PrimaryActionLabel, label.Text, $"{scenario}: le libellé doit conserver le binding d’état.");
                    True(ReferenceEquals(window.GameState.PrimaryActionCommand, primary.Command), $"{scenario}: le bouton doit utiliser la commande de son état.");
                    Equal(window.GameState.IsPrimaryActionEnabled, primary.IsEnabled, $"{scenario}: la disponibilité visuelle doit suivre l’état.");
                    True(primary.Focusable && primary.IsTabStop, "Le bouton doit rester accessible au clavier en production.");

                    await ResizeAndCaptureAsync(window, 1672, 941, scenario.ToString(), captureDirectory, captures);
                    await ResizeAndCaptureAsync(window, 1080, 680, scenario.ToString(), captureDirectory, captures);
                    if (scenario == GamePreviewScenario.Ready)
                    {
                        await ResizeAndCaptureAsync(window, 1920, 1080, scenario.ToString(), captureDirectory, captures);
                        await ValidateNativeCommandDispatchAsync(window, primary);
                        await ResizeAndCaptureAsync(window, 1672, 941, "Disabled", captureDirectory, captures);
                        await ResizeAndCaptureAsync(window, 1080, 680, "Disabled", captureDirectory, captures);
                        await ValidateNavigationAsync(window);
                        window.SettingsState.ApplyRuntimeView(window.SettingsState.Current with
                        {
                            Updates = window.SettingsState.Current.Updates with { IsUpdateAvailable = true, CanStartUpdate = true }
                        });
                        window.ActivityState.ApplyRuntimeView(ActivityPreviewData.Create(ActivityPreviewScenario.GameDownload).Current);
                        await PumpAsync(window);
                        True(Required<Button>(window, "LauncherUpdateButton").IsVisible && window.ActivityState.Current.TopBarShowsPercent,
                            "Le stress compact doit inclure le raccourci de mise à jour du launcher et le pourcentage d’activité.");
                        await ResizeAndCaptureAsync(window, 1080, 680, "ShellBusy", captureDirectory, captures);
                    }
                    else if (primary.IsEnabled)
                    {
                        RecordingCommand command = new();
                        window.GameState.AttachPrimaryActionCommand(command);
                        await PumpAsync(window);
                        await InvokeNativeButtonAsync(primary, window);
                        Equal(1, command.ExecutionCount, $"{scenario}: le bouton natif doit transmettre exactement une commande au double de test.");
                    }

                    AssertIsolated(window);
                    window.Close();
                    window = null;
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }

                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    Directory.CreateDirectory(captureDirectory);
                    File.WriteAllText(Path.Combine(captureDirectory, "capture-evidence.json"), JsonSerializer.Serialize(new
                    {
                        CapturedAtUtc = DateTimeOffset.UtcNow,
                        CaptureMethod = "WPF RenderTargetBitmap(window), full window visual, no bitmap rescaling or retouching",
                        SourceOfData = "Existing LauncherV2PreviewData scenarios; recording ICommand; isolated to IntegrationTests",
                        Offscreen = true,
                        ShowActivated = false,
                        KeyboardAcquisitionSuppressedByHarness = true,
                        MainComparisonExportDpi = 96,
                        AdditionalReadyCaptureUsesActualWindowDpi = true,
                        ControlCoordinates = "DIPs relative to the full window visual",
                        ActualWindowDpiIsRecordedSeparately = true,
                        NotExercised = new[] { "Desktop keyboard focus acquisition/restoration", "Tray restore", "Interactive maximize or resize", "Real services or game launch" },
                        BindingErrors = bindingErrors.Messages,
                        ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                        Captures = captures
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                True(bindingErrors.Messages.Count == 0, "Le pilote ne doit produire aucune erreur de binding WPF. Voir binding-errors.txt.");
                Console.WriteLine($"Visual pilot captures={captures.Count}; elapsed={elapsed.Elapsed.TotalSeconds:F1}s; managedMemory={GC.GetTotalMemory(false) / 1024d / 1024d:F1}MiB.");
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                window?.Close();
                application?.Shutdown();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = priorBindingLevel;
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    Directory.CreateDirectory(captureDirectory);
                    File.WriteAllLines(Path.Combine(captureDirectory, "binding-errors.txt"), bindingErrors.Messages);
                }
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static LauncherShellV2 CreateOffscreenWindow(GamePreviewScenario scenario)
    {
        LauncherShellV2 window = new(scenario)
        {
            Width = 1672,
            Height = 941,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        // LauncherVersion is init-only and the existing public preview constructor
        // does not accept a ShellUiState fixture. Initialize its test value before
        // showing the visual, then refresh its unchanged production binding.
        string version = typeof(LauncherShellV2).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        typeof(ShellUiState).GetProperty(nameof(ShellUiState.LauncherVersion))!
            .SetValue(window.ShellState, $"v{version}-local");
        Required<Border>(window, "LocalBuildBadge").GetBindingExpression(UIElement.VisibilityProperty)?.UpdateTarget();
        // The real navigation handlers may request focus. Cancel that acquisition only
        // in the harness; focusable controls and their production handlers are preserved.
        window.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
        return window;
    }

    private static void AssertIsolated(LauncherShellV2 window)
    {
        True(window.IsPreviewMode, "Les captures doivent rester en preview.");
        True(!window.HasRealAuthenticationAttached && !window.HasRealAddonsAttached && !window.HasRealActivityAttached,
            "Aucun service réel ne doit être attaché au pilote.");
        True(window.ShellState.IsLocalBuild && Required<Border>(window, "LocalBuildBadge").Visibility == Visibility.Visible,
            "Le badge LOCAL doit découler de la version locale du fixture via son binding de production.");
        True(!window.IsActive && !window.IsKeyboardFocusWithin, "Le harnais ne doit pas prendre le focus du bureau.");
        True(!window.ShowInTaskbar && !window.ShowActivated && window.Left <= -10000 && window.Top <= -10000,
            "La fenêtre doit rester hors écran et sans activation.");
    }

    private static async Task ValidateNativeCommandDispatchAsync(LauncherShellV2 window, Button primary)
    {
        RecordingCommand command = new();
        window.GameState.AttachPrimaryActionCommand(command);
        await PumpAsync(window);
        True(primary.IsEnabled, "La commande disponible doit activer Jouer.");
        await InvokeNativeButtonAsync(primary, window);
        Equal(1, command.ExecutionCount, "Jouer doit transmettre exactement une invocation au double de commande.");
        command.SetEnabled(false);
        await PumpAsync(window);
        True(!primary.IsEnabled, "CanExecute=false doit désactiver le bouton même si l’état du jeu est prêt.");
        bool refused = false;
        try
        {
            await InvokeNativeButtonAsync(primary, window);
        }
        catch (ElementNotEnabledException)
        {
            refused = true;
        }
        True(refused, "Le pair natif WPF doit refuser l’invocation du bouton désactivé.");
        Equal(1, command.ExecutionCount, "Une invocation désactivée ne doit pas atteindre la commande.");
    }

    private static async Task ValidateNavigationAsync(LauncherShellV2 window)
    {
        ShellUiState originalShell = window.ShellState;
        GameUiState originalGame = window.GameState;
        foreach ((string name, LauncherShellPage page) in new[]
        {
            ("AddonsNavigationButton", LauncherShellPage.Addons),
            ("PatchNotesNavigationButton", LauncherShellPage.PatchNotes),
            ("SettingsButton", LauncherShellPage.Settings),
            ("GameNavigationButton", LauncherShellPage.Game)
        })
        {
            Button button = Required<Button>(window, name);
            True(button.IsEnabled && button.Focusable, $"{name} doit rester disponible et focusable.");
            await InvokeNativeButtonAsync(button, window);
            Equal(page, window.CurrentPage, $"{name} doit conserver sa destination.");
            AssertIsolated(window);
        }
        True(ReferenceEquals(originalShell, window.ShellState) && ReferenceEquals(originalGame, window.GameState),
            "La navigation doit conserver les états existants.");
        foreach (string name in new[] { "ActivityButton", "FriendsButton", "ProfileButton", "CloseWindowButton", "MinimizeWindowButton", "MaximizeWindowButton" })
        {
            Button button = Required<Button>(window, name);
            True(button.IsVisible && button.IsEnabled && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)),
                $"{name} doit rester visible, disponible et nommé pour l’accessibilité.");
            if (!name.EndsWith("WindowButton", StringComparison.Ordinal))
                True(button.Focusable, $"{name} doit conserver son accès clavier.");
            AssertInsideWindow(button, window, name);
        }
    }

    private static async Task InvokeNativeButtonAsync(Button button, LauncherShellV2 window)
    {
        ButtonAutomationPeer peer = new(button);
        IInvokeProvider invoker = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
        invoker.Invoke();
        await PumpAsync(window);
    }

    private static async Task ResizeAndCaptureAsync(LauncherShellV2 window, int width, int height,
        string scenario, string? directory, List<CaptureEvidence> captures)
    {
        window.Width = width;
        window.Height = height;
        await PumpAsync(window);
        AssertIsolated(window);
        GameViewV2 game = Required<GameViewV2>(window, "GameView");
        ScrollViewer scroll = Required<ScrollViewer>(game, "GameScrollViewer");
        True(scroll.ScrollableWidth <= 0.5, $"{scenario} {width}×{height}: aucun défilement horizontal.");
        foreach (string name in new[] { "PrimaryActionButton", "LatestPatchNoteAction" })
            AssertInsideWindow(Required<Button>(game, name), window, name);
        AssertInsideWindow(Required<Button>(window, "CloseWindowButton"), window, "Fermer");
        foreach (string name in new[] { "HeroTitle", "HeroTitleSecond", "HeroChips", "RealmStatusCard" })
            AssertInsideWindow(Required<FrameworkElement>(game, name), window, name);
        TextBlock primaryLabel = Required<TextBlock>(game, "PrimaryActionLabelText");
        TextBlock noteLabel = Required<TextBlock>(game, "LatestPatchNoteLabel");
        AssertInsideElement(primaryLabel, Required<Button>(game, "PrimaryActionButton"), "Libellé de l’action principale");
        AssertInsideElement(noteLabel, Required<Button>(game, "LatestPatchNoteAction"), "Libellé Notes de version");
        foreach (TextBlock text in new[] { primaryLabel, noteLabel,
            Required<TextBlock>(game, "HeroTitle"), Required<TextBlock>(game, "HeroTitleSecond") })
            AssertNaturalTextFits(text);
        foreach (Border chip in Required<StackPanel>(game, "HeroChips").Children)
        {
            TextBlock text = Descendants<TextBlock>(chip).Single();
            AssertNaturalTextFits(text);
            AssertInsideElement(text, chip, "Libellé du cartouche");
        }
        AssertNonIntersecting(window, [Required<Button>(game, "PrimaryActionButton"), Required<Button>(game, "LatestPatchNoteAction"),
            Required<Border>(game, "RealmStatusCard")], "Les actions et la carte d’état");
        AssertNonIntersecting(window,
            new[] { "GameNavigationButton", "AddonsNavigationButton", "PatchNotesNavigationButton", "LauncherUpdateButton", "ActivityButton",
                "FriendsButton", "SettingsButton", "ProfileButton", "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" }
                .Select(name => Required<Button>(window, name)).Where(button => button.IsVisible).ToArray(),
            "Les commandes de la barre supérieure");
        if (window.GameState.ShowsProgress)
        {
            TextBlock title = Descendants<TextBlock>(game).FirstOrDefault(text => text.IsVisible && text.Text == window.GameState.ProgressTitle)
                ?? throw new InvalidOperationException($"{scenario}: le titre de progression doit rester visible.");
            ProgressBar progress = Descendants<ProgressBar>(game).Single(bar => bar.IsVisible);
            Equal(window.GameState.Progress, progress.Value, "La valeur de progression doit conserver son binding.");
            Equal(window.GameState.IsProgressIndeterminate, progress.IsIndeterminate, "La progression indéterminée doit rester liée à l’état.");
            AssertInsideWindow(title, window, "Titre de progression");
            AssertInsideWindow(progress, window, "Progression");
        }
        if (window.GameState.ShowsError)
        {
            foreach (string value in new[] { window.GameState.ErrorTitle, window.GameState.ErrorSummary })
            {
                TextBlock text = Descendants<TextBlock>(game).FirstOrDefault(text => text.IsVisible && text.Text == value)
                    ?? throw new InvalidOperationException("Le détail d’erreur doit rester visible et lié à l’état.");
                AssertInsideWindow(text, window, "Détail d’erreur");
            }
        }

        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);
        string fileName = $"game-{scenario.ToLowerInvariant()}-{width}x{height}.png";
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        DpiScale actualDpi = VisualTreeHelper.GetDpi(window);
        Matrix deviceTransform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        RenderTargetBitmap bitmap = new(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(Path.Combine(directory, fileName)))
            encoder.Save(stream);

        string[] shellNames = ["TitleBar", "BrandName", "ProductGameName", "GameNavigationButton", "AddonsNavigationButton", "PatchNotesNavigationButton", "CloseWindowButton"];
        string[] gameNames = ["HeroCopyContent", "RealmEyebrow", "HeroTitle", "HeroTitleSecond", "HeroSubtitle", "HeroChips",
            "RealmStatusCard", "RealmStatusText", "PrimaryActionButton", "PrimaryActionLabelText", "LatestPatchNoteAction", "LatestPatchNoteLabel", "GameServerStatus"];
        List<ControlEvidence> controls = [];
        foreach (string name in shellNames)
            if (window.FindName(name) is FrameworkElement element) controls.Add(Measure(name, element, window));
        foreach (string name in gameNames)
            if (game.FindName(name) is FrameworkElement element) controls.Add(Measure(name, element, window));

        CaptureEvidence evidence = new(fileName, scenario, window.ActualWidth, window.ActualHeight,
            bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY,
            actualDpi.PixelsPerInchX, actualDpi.PixelsPerInchY, actualDpi.PixelsPerDip,
            deviceTransform.M11, deviceTransform.M22, controls);
        captures.Add(evidence);
        Console.WriteLine($"{fileName}: logical={window.ActualWidth:F2}×{window.ActualHeight:F2}DIP; bitmap={bitmap.PixelWidth}×{bitmap.PixelHeight}@{bitmap.DpiX:F2}DPI; window={actualDpi.PixelsPerInchX:F2}×{actualDpi.PixelsPerInchY:F2}DPI.");
        if (scenario == nameof(GamePreviewScenario.Ready) && width == 1672 && height == 941)
        {
            int nativeWidth = Math.Max(1, (int)Math.Round(window.ActualWidth * actualDpi.DpiScaleX));
            int nativeHeight = Math.Max(1, (int)Math.Round(window.ActualHeight * actualDpi.DpiScaleY));
            RenderTargetBitmap deviceBitmap = new(nativeWidth, nativeHeight,
                actualDpi.PixelsPerInchX, actualDpi.PixelsPerInchY, PixelFormats.Pbgra32);
            deviceBitmap.Render(window);
            PngBitmapEncoder deviceEncoder = new();
            deviceEncoder.Frames.Add(BitmapFrame.Create(deviceBitmap));
            string deviceName = $"game-ready-device-{nativeWidth}x{nativeHeight}-{actualDpi.PixelsPerInchX:F0}dpi.png";
            using (FileStream stream = File.Create(Path.Combine(directory, deviceName)))
                deviceEncoder.Save(stream);
            captures.Add(evidence with { FileName = deviceName, BitmapWidthPixels = nativeWidth, BitmapHeightPixels = nativeHeight,
                BitmapDpiX = deviceBitmap.DpiX, BitmapDpiY = deviceBitmap.DpiY });
        }
    }

    private static ControlEvidence Measure(string name, FrameworkElement element, Window window)
    {
        Point origin = element.TransformToAncestor(window).Transform(new Point());
        TextBlock? text = element as TextBlock;
        Control? control = element as Control;
        FontFamily? family = text?.FontFamily ?? control?.FontFamily;
        FontWeight? weight = text?.FontWeight ?? control?.FontWeight;
        double? size = text?.FontSize ?? control?.FontSize;
        FontEvidence? resolvedFont = null;
        if (family is not null && weight is not null)
        {
            Typeface typeface = new(family, text?.FontStyle ?? control!.FontStyle, weight.Value,
                text?.FontStretch ?? control!.FontStretch);
            if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyph))
                resolvedFont = new(glyph.FontUri.OriginalString,
                    string.Join(" / ", glyph.FamilyNames.Values.Distinct()),
                    string.Join(" / ", glyph.FaceNames.Values.Distinct()), glyph.Weight.ToString(), glyph.Style.ToString(),
                    (text?.Text ?? string.Empty).All(character => char.IsWhiteSpace(character) || glyph.CharacterToGlyphMap.ContainsKey(character)));
            string? expectedFont = name switch
            {
                "HeroTitle" or "HeroTitleSecond" => "Inter-ExtraBold.ttf",
                "BrandName" or "RealmStatusText" or "PrimaryActionLabelText" => "Inter-SemiBold.ttf",
                "GameNavigationButton" or "AddonsNavigationButton" or "PatchNotesNavigationButton" or "LatestPatchNoteLabel" => "Inter-Medium.ttf",
                "HeroSubtitle" or "ProductGameName" => "Inter-Regular.ttf",
                _ => null
            };
            if (expectedFont is not null)
                True(resolvedFont is { CoversDisplayedCharacters: true }
                    && resolvedFont.GlyphTypefaceUri.EndsWith(expectedFont, StringComparison.OrdinalIgnoreCase)
                    && glyph.Weight == weight.Value && glyph.StyleSimulations == StyleSimulations.None,
                    $"{name}: la vraie fonte embarquée {expectedFont} doit fournir les glyphes, sans fallback ni graisse simulée.");
        }
        return new(name, origin.X, origin.Y, element.ActualWidth, element.ActualHeight,
            element.IsVisible, family?.Source, size, weight?.ToString(), resolvedFont);
    }

    private static void AssertInsideWindow(FrameworkElement element, Window window, string label)
    {
        Point origin = element.TransformToAncestor(window).Transform(new Point());
        True(element.IsVisible && origin.X >= -0.5 && origin.Y >= -0.5
            && origin.X + element.ActualWidth <= window.ActualWidth + 0.5
            && origin.Y + element.ActualHeight <= window.ActualHeight + 0.5,
            $"{label} doit rester entièrement visible dans {window.ActualWidth}×{window.ActualHeight}DIP.");
    }

    private static void AssertInsideElement(FrameworkElement element, FrameworkElement container, string label)
    {
        Point origin = element.TransformToAncestor(container).Transform(new Point());
        True(element.IsVisible && origin.X >= -0.5 && origin.Y >= -0.5
            && origin.X + element.ActualWidth <= container.ActualWidth + 0.5
            && origin.Y + element.ActualHeight <= container.ActualHeight + 0.5,
            $"{label} dépasse son bouton: origine={origin}, texte={element.ActualWidth:F1}×{element.ActualHeight:F1}, bouton={container.ActualWidth:F1}×{container.ActualHeight:F1}.");
    }

    private static void AssertNaturalTextFits(TextBlock text)
    {
        FormattedText natural = new(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            text.Foreground, null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
        double availableWidth = Math.Max(1, text.ActualWidth - text.Padding.Left - text.Padding.Right);
        double availableHeight = Math.Max(1, text.ActualHeight - text.Padding.Top - text.Padding.Bottom);
        if (text.TextWrapping != TextWrapping.NoWrap)
            natural.MaxTextWidth = availableWidth;
        if (!double.IsNaN(text.LineHeight) && text.LineHeight > 0)
            natural.LineHeight = text.LineHeight;
        // A wrapped line may end with a separating space outside its ink width.
        // Check the visible glyph width, while still checking the full multiline height.
        double measuredWidth = text.TextWrapping == TextWrapping.NoWrap
            ? natural.WidthIncludingTrailingWhitespace : natural.Width;
        True(measuredWidth <= availableWidth + 2 && natural.Height <= availableHeight + 2,
            $"Le texte natif '{text.Text}' est rogné: mesure={measuredWidth:F1}×{natural.Height:F1}, espace={availableWidth:F1}×{availableHeight:F1}, retour={text.TextWrapping}.");
    }

    private static void AssertNonIntersecting(Window window, IReadOnlyList<FrameworkElement> elements, string label)
    {
        for (int first = 0; first < elements.Count; first++)
        {
            FrameworkElement a = elements[first];
            Rect aBounds = new(a.TranslatePoint(new Point(), window), new Size(a.ActualWidth, a.ActualHeight));
            for (int second = first + 1; second < elements.Count; second++)
            {
                FrameworkElement b = elements[second];
                Rect overlap = Rect.Intersect(aBounds, new Rect(b.TranslatePoint(new Point(), window), new Size(b.ActualWidth, b.ActualHeight)));
                True(overlap.IsEmpty || overlap.Width <= 0.5 || overlap.Height <= 0.5,
                    $"{label} ne doivent pas se chevaucher: {a.Name} / {b.Name}, intersection={overlap}.");
            }
        }
    }

    private static async Task PumpAsync(LauncherShellV2 window)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static T Required<T>(FrameworkElement scope, string name) where T : FrameworkElement =>
        scope.FindName(name) as T ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Attendu={expected}; actuel={actual}.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void LoadV2Resources(Application application)
    {
        foreach (string resourcePath in new[]
        {
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(resourcePath, UriKind.Relative) });
    }

    private sealed class RecordingCommand : ICommand
    {
        private bool _enabled = true;
        public int ExecutionCount { get; private set; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _enabled;
        public void Execute(object? parameter) => ExecutionCount++;
        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private string _pending = string.Empty;
        public List<string> Messages { get; } = [];
        public override void Write(string? message) => _pending += message;
        public override void WriteLine(string? message)
        {
            string line = _pending + message;
            _pending = string.Empty;
            if (!string.IsNullOrWhiteSpace(line) && !Messages.Contains(line, StringComparer.Ordinal))
                Messages.Add(line);
        }
    }

    private sealed record CaptureEvidence(string FileName, string Scenario,
        double LogicalWidthDip, double LogicalHeightDip, int BitmapWidthPixels, int BitmapHeightPixels,
        double BitmapDpiX, double BitmapDpiY, double WindowDpiX, double WindowDpiY, double WindowPixelsPerDip,
        double DeviceScaleX, double DeviceScaleY, IReadOnlyList<ControlEvidence> Controls);

    private sealed record ControlEvidence(string Name, double X, double Y, double Width, double Height,
        bool IsVisible, string? FontFamily, double? FontSize, string? FontWeight, FontEvidence? ResolvedFont);

    private sealed record FontEvidence(string GlyphTypefaceUri, string PhysicalFamily, string PhysicalFace,
        string PhysicalWeight, string PhysicalStyle, bool CoversDisplayedCharacters);
}
