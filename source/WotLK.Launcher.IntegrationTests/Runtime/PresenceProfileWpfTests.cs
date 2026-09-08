using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class PresenceProfileWpfTests
{
    private static int _checks;
    private static readonly string[] Statuses = ["online", "away", "dnd", "offline"];
    private static readonly string[] FrenchLabels = ["En ligne", "Absent", "Ne pas déranger", "Apparaître hors ligne"];
    private static readonly string[] Colors = ["#48C78E", "#E9B44C", "#EE6873", "#8995A8"];

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            async Task ExecuteAsync()
            {
                Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                string originalLocale = LauncherLocalization.CurrentLocale;
                try
                {
                    _checks = 0;
                    foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
                    List<object> cases = [];
                    foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
                        foreach (Size size in new[] { new Size(1597.6, 996.8), new Size(1080, 680) })
                            cases.Add(await ValidateAsync(locale, size, captureDirectory));
                    if (!string.IsNullOrWhiteSpace(captureDirectory))
                    {
                        Directory.CreateDirectory(captureDirectory);
                        await File.WriteAllTextAsync(System.IO.Path.Combine(captureDirectory, "verification.json"), JsonSerializer.Serialize(new
                        {
                            status = "PASS", assertions = _checks,
                            method = "Synthetic WPF shell, inactive offscreen WS_EX_NOACTIVATE; injected profile snapshots and routed button events; no authentication, backend or desktop input",
                            cases
                        }, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    Console.WriteLine($"Presence profile WPF PASS: {_checks} assertions; 1597.6x996.8 and 1080x680, FR/EN, four choices, menu/header dots, pending disabled, failure/unavailable, text bounds. Inactive offscreen synthetic fixture; no backend, real authentication or desktop input.");
                    completion.TrySetResult(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); completion.TrySetResult(1); }
                finally { LauncherLocalization.SetLocale(originalLocale); application.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
        }) { IsBackground = true, Name = "AtlasPresenceProfileOffscreenFixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    private static async Task<object> ValidateAsync(string locale, Size size, string? captures)
    {
        LauncherLocalization.SetLocale(locale);
        ProfileUiState profile = LauncherV2PreviewData.CreateProfile(ProfilePreviewScenario.SignedIn);
        profile.ApplyAccountIdentity("PresenceFixture", true);
        LauncherShellV2 shell = new(
            LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready, isAuthenticated: true),
            LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateFriends(), profile,
            new SettingsUiState(SettingsUiState.Empty.Current with { IsRuntimeConnected = true }),
            new AccountUiState(AccountUiState.Empty.Current with { IsRuntimeConnected = true, Username = "PresenceFixture", Initial = "P" }),
            new AvatarCropUiState(AvatarCropUiState.Empty.Current))
        {
            Width = size.Width, Height = size.Height, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false
        };
        shell.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
        shell.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(shell).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
        };
        long sequence = 0;
        List<object> states = [];
        shell.Show();
        try
        {
            await Layout(shell);
            ProfileMenuV2 menu = shell.ProfileOverlay;
            List<string> requests = [];
            menu.PresenceRequested += (_, status) => requests.Add(status);
            ((Button)shell.FindName("ProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(180);
            await Layout(shell);
            Check(menu.IsOpen && menu.IsVisible, "Avatar opens the real profile menu.");
            Check(!menu.IsPresenceExpanded, "Presence choices stay collapsed when the menu opens.");
            Button presenceToggle = (Button)menu.FindName("PresenceToggleButton");
            presenceToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout(shell);
            Check(menu.IsPresenceExpanded, "Current status expands the four choices.");
            Check(!shell.IsActive && !shell.ShowActivated && !shell.ShowInTaskbar && shell.Left < -10000 && shell.Top < -10000,
                "Fixture remains inactive and outside the desktop.");
            Check((GetWindowLong(new WindowInteropHelper(shell).Handle, -20) & 0x08000000) != 0, "Native no-activation flag remains set.");
            Button[] choices = Descendants<Button>(menu).Where(button => button.Tag is string tag && Statuses.Contains(tag)).ToArray();
            Check(choices.Length == 4 && choices.Select(button => (string)button.Tag).Distinct().Count() == 4,
                "Exactly the four canonical presence choices are rendered.");
            FrameworkElement content = (FrameworkElement)shell.Content;
            Ellipse headerDot = Descendants<Ellipse>((FrameworkElement)shell.FindName("ProfileAvatarVisual"))
                .Single(dot => BindingPath(dot, Shape.FillProperty) == "ProfileState.PresenceBrush");
            Ellipse menuDot = Descendants<Ellipse>(menu).Single(dot => BindingPath(dot, Shape.FillProperty) == "PresenceBrush");
            TextBlock statusLabel = Descendants<TextBlock>(menu).Single(text => BindingPath(text, TextBlock.TextProperty) == "PresenceLabel");
            TextBlock progress = Descendants<TextBlock>(menu).Single(text => BindingPath(text, TextBlock.TextProperty) == "PresenceProgress");
            TextBlock errorText = Descendants<TextBlock>(menu).Single(text => BindingPath(text, TextBlock.TextProperty) == "PresenceError");
            ScrollViewer scroll = Descendants<ScrollViewer>(menu).First();

            for (int index = 0; index < Statuses.Length; index++)
            {
                profile.ApplyPresence(new(++sequence, 42, Statuses[index], Statuses[index], false, true, false, null));
                if (!menu.IsPresenceExpanded) presenceToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Layout(shell);
                ValidateVisibleState(Statuses[index], Colors[index], Statuses[index] == "offline" ? "Hors ligne" : FrenchLabels[index], enabled: true);
                Button choice = choices.Single(button => Equals(button.Tag, Statuses[index]));
                int before = requests.Count;
                choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, choice));
                Check(requests.Count == before + 1 && requests[^1] == Statuses[index], "Choice emits the exact canonical status without a backend.");
                Check(!menu.IsPresenceExpanded && menu.IsOpen, "Choosing presence collapses the choices and retains the profile menu.");
                presenceToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Layout(shell);
                states.Add(MeasureState(Statuses[index]));
                if (index == 2) Capture(content, captures, $"presence-{Language()}-{(int)Math.Round(size.Width)}-dnd.png");
            }

            profile.ApplyPresence(new(++sequence, 42, "dnd", "dnd", false, true, true, null));
            await Layout(shell);
            ValidateVisibleState("dnd", Colors[2], FrenchLabels[2], enabled: false);
            Check(progress.Text == LauncherLocalization.Text("Enregistrement…"), "Pending indicator is translated.");
            AssertSingleLineFits(progress);
            Check(Bounds(progress, content).Right <= Bounds(menu, content).Right - 15, "Pending text fits the menu beside the confirmed status.");
            int pendingRequests = requests.Count;
            foreach (Button choice in choices) choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, choice));
            Check(requests.Count == pendingRequests, "Even an injected click cannot emit another request while pending.");
            states.Add(MeasureState("pending"));
            Capture(content, captures, $"presence-{Language()}-{(int)Math.Round(size.Width)}-pending.png");

            profile.ApplyPresence(new(++sequence, 42, "dnd", "dnd", false, true, false, "request-failed"));
            await Layout(shell);
            ValidateVisibleState("dnd", Colors[2], FrenchLabels[2], enabled: true);
            Check(progress.Text.Length == 0 && errorText.Text == LauncherLocalization.Text("Impossible de confirmer le statut. Réessayez."),
                "Failed update retains the confirmed status, removes pending and shows the translated retry message.");
            AssertWrappedFits(errorText);
            states.Add(MeasureState("failure"));
            Capture(content, captures, $"presence-{Language()}-{(int)Math.Round(size.Width)}-failure.png");

            profile.ApplyPresence(new(++sequence, 42, "offline", "dnd", false, false, false, "presence-unavailable"));
            await Layout(shell);
            ValidateVisibleState(null, Colors[3], "Statut indisponible", enabled: false);
            Check(errorText.Text == LauncherLocalization.Text("Le serveur ne permet pas encore de modifier votre statut."), "Unavailable server has an explanatory translated message.");
            AssertWrappedFits(errorText);
            states.Add(MeasureState("unavailable"));
            Capture(content, captures, $"presence-{Language()}-{(int)Math.Round(size.Width)}-unavailable.png");

            profile.ApplyPresence(new(++sequence, 42, "away", "online", true, true, false, null));
            await Layout(shell);
            ValidateVisibleState("online", Colors[1], "Absent · inactivité", enabled: true);
            states.Add(MeasureState("automatic-away"));
            Capture(content, captures, $"presence-{Language()}-{(int)Math.Round(size.Width)}-automatic-away.png");
            menu.IsOpen = false;
            Check(!menu.IsPresenceExpanded, "Closing the profile also collapses the presence choices.");
            return new { locale, width = size.Width, height = size.Height, states };

            string Language() => locale == LauncherLocalization.FrenchLocale ? "fr" : "en";
            object MeasureState(string state) => new
            {
                state, menu = Rectangle(Bounds(menu, content)), viewport = scroll.ViewportHeight, extent = scroll.ExtentHeight,
                label = statusLabel.Text, progress = progress.Text, error = errorText.Text,
                choices = choices.Select(button => new { status = (string)button.Tag, enabled = button.IsEnabled, bounds = Rectangle(Bounds(button, content)) }).ToArray()
            };
            void ValidateVisibleState(string? selected, string color, string label, bool enabled)
            {
                Rect menuBounds = Bounds(menu, content);
                Check(menuBounds.Left >= 0 && menuBounds.Top >= 0 && menuBounds.Right <= content.ActualWidth + .5 && menuBounds.Bottom <= content.ActualHeight + .5,
                    "Profile menu stays inside the shell at this viewport.");
                Check(statusLabel.Text == LauncherLocalization.Text(label), "Confirmed presence label is translated.");
                AssertSingleLineFits(statusLabel);
                Check(((SolidColorBrush)headerDot.Fill).Color == (Color)ColorConverter.ConvertFromString(color)
                    && ((SolidColorBrush)menuDot.Fill).Color == (Color)ColorConverter.ConvertFromString(color), "Header and menu dots show the same expected color.");
                foreach (Button button in choices)
                {
                    string status = (string)button.Tag;
                    int index = Array.IndexOf(Statuses, status);
                    TextBlock text = Descendants<TextBlock>(button).First();
                    Check(text.Text == LauncherLocalization.Text(FrenchLabels[index]), "Each status label is translated.");
                    AssertSingleLineFits(text);
                    Check(button.IsVisible && button.IsEnabled == enabled, "Choice visibility and enabled state match the snapshot.");
                    Rect bounds = Bounds(button, content);
                    Check(bounds.Left >= menuBounds.Left && bounds.Right <= menuBounds.Right && bounds.Top >= menuBounds.Top && bounds.Bottom <= menuBounds.Bottom,
                        "Every choice is fully inside the visible menu.");
                    Rect textBounds = Bounds(text, content);
                    Check(textBounds.Left >= bounds.Left && textBounds.Right <= bounds.Right && textBounds.Top >= bounds.Top && textBounds.Bottom <= bounds.Bottom,
                        "Choice text fits vertically and horizontally inside its button.");
                    TextBlock tick = Descendants<TextBlock>(button).Last();
                    Check(tick.IsVisible == (status == selected), "Only the confirmed manual choice has a checkmark.");
                }
            }
        }
        finally { shell.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    }

    private static void AssertSingleLineFits(TextBlock text)
    {
        FormattedText measured = new(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        Check(text.ActualWidth + 1 >= measured.WidthIncludingTrailingWhitespace && text.ActualHeight + 1 >= measured.Height,
            $"Text is not clipped: {text.Text}; actual {text.ActualWidth:F2}x{text.ActualHeight:F2}; measured {measured.WidthIncludingTrailingWhitespace:F2}x{measured.Height:F2}.");
    }

    private static void AssertWrappedFits(TextBlock text)
    {
        TextBlock probe = new() { Text = text.Text, TextWrapping = text.TextWrapping, FontFamily = text.FontFamily,
            FontStyle = text.FontStyle, FontWeight = text.FontWeight, FontStretch = text.FontStretch, FontSize = text.FontSize };
        probe.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
        Check(text.ActualHeight + 1 >= probe.DesiredSize.Height, "Wrapped explanation has enough height for every line.");
    }

    private static string? BindingPath(DependencyObject value, DependencyProperty property) => BindingOperations.GetBinding(value, property)?.Path?.Path;
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static async Task Layout(Window window)
    {
        window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static object Rectangle(Rect value) => new { x = value.X, y = value.Y, width = value.Width, height = value.Height };
    private static void Capture(FrameworkElement content, string? directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(System.IO.Path.Combine(directory, name)); encoder.Save(stream);
    }
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
