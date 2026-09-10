using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static partial class ShellNavigationWpfTests
{
    private static int _checks;
    private static readonly (string Button, LauncherShellPage Page)[] Pages =
    [
        ("SettingsButton", LauncherShellPage.Settings),
        ("PatchNotesNavigationButton", LauncherShellPage.PatchNotes),
        ("AddonsNavigationButton", LauncherShellPage.Addons),
        ("ShopNavigationButton", LauncherShellPage.Shop),
        ("MessagesNavigationButton", LauncherShellPage.Chat),
        ("GameNavigationButton", LauncherShellPage.Game)
    ];
    private static readonly ShellOverlayKind[] Panels =
        [ShellOverlayKind.Friends, ShellOverlayKind.Activity, ShellOverlayKind.Profile, ShellOverlayKind.PatchNote];

    internal static async Task<int> RunAsync(bool optimizationsOnly = false)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            int result = 1;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            completion.TrySetResult(result);
            async Task ExecuteAsync()
            {
                Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                try
                {
                    _checks = 0;
                    foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
                    foreach (Size size in new[] { new Size(1440, 860), new Size(1080, 680) })
                    {
                        long started = Environment.TickCount64;
                        Console.WriteLine($"Navigation fixture: {size.Width}x{size.Height} starting.");
                        await ValidateAsync(size, optimizationsOnly);
                        Console.WriteLine($"Navigation fixture: {size.Width}x{size.Height} complete ({_checks} assertions, {(Environment.TickCount64 - started) / 1000}s).");
                    }
                    Console.WriteLine(optimizationsOnly
                        ? $"Optimization UI PASS: {_checks} assertions; 600 releases, 500 addons, 1000 friends; bounded generated rows, selection, scrolling and refresh persistence at 1440x860 and 1080x680. Inactive offscreen fixtures; no backend or desktop input."
                        : $"Shell navigation WPF PASS: {_checks} assertions; panel/page matrix, direct and pointer activation, header focus routing, rapid switching, outside click, modal guards, virtualized lists and preserved navigation. Inactive offscreen fixtures; no backend, real session or desktop input.");
                    result = 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
                finally { app.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
        }) { IsBackground = true, Name = "AtlasShellNavigationOffscreenFixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Both viewport matrices include real animation settling and software rendering.
        // Keep a bounded deadline without cancelling the second matrix near completion.
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(5));
    }

    private static async Task ValidateAsync(Size size, bool optimizationsOnly)
    {
        ProfileUiState profile = LauncherV2PreviewData.CreateProfile(ProfilePreviewScenario.SignedIn);
        ActivityUiState activity = new(ActivityUiState.EmptyView with
        {
            RecentOperations = [new("Fixture", "Terminé", "Maintenant", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Addons, "", false)]
        });
        LauncherShellV2 shell = new(
            LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready, isAuthenticated: true),
            LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateFriends(), profile,
            new SettingsUiState(SettingsUiState.Empty.Current with { IsRuntimeConnected = true }),
            new AccountUiState(AccountUiState.Empty.Current with { IsRuntimeConnected = true, Username = "NavigationFixture", Initial = "N" }),
            new AvatarCropUiState(AvatarCropUiState.Empty.Current with { IsPreview = true }), activity)
        {
            Width = size.Width, Height = size.Height, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false
        };
        KeyboardFocusChangedEventArgs? focusProbe = null;
        List<DependencyObject> focusAttempts = [];
        shell.PreviewGotKeyboardFocus += (_, args) =>
        {
            // A synthetic routed probe does not acquire OS focus. Suppress every real request.
            if (ReferenceEquals(args, focusProbe)) return;
            if (args.NewFocus is DependencyObject target) focusAttempts.Add(target);
            args.Handled = true;
        };
        shell.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(shell).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
        };
        shell.Show();
        try
        {
            await Settle();
            if (optimizationsOnly) { await ValidatePreservedNavigationAsync(shell); return; }
            // Direct activation covers keyboard and automation; pointer activation also exercises PreviewMouseDown.
            foreach (bool pointer in new[] { false, true })
                foreach (ShellOverlayKind panel in Panels)
                    foreach ((string name, LauncherShellPage page) in Pages)
                    {
                        OpenPanel(panel);
                        await Settle();
                        Button button = Button(name);
                        Check(button.IsEnabled && button.IsVisible, $"{name} remains available with {panel} open.");
                        Check(IsWithin(shell.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), shell)) as DependencyObject, button),
                            $"{panel} must not cover the hit target of {name}.");
                        Invoke(button, pointer);
                        Check(shell.CurrentPage == page, $"{panel} -> {name} must navigate with one activation ({(pointer ? "pointer" : "direct")}).");
                        Check(shell.CurrentOverlay == ShellOverlayKind.None, "Page navigation closes every shell panel immediately.");
                        Check(Panels.All(kind => !View(kind).IsHitTestVisible), "Closing panels immediately release hit testing during animation.");
                        await Settle();
                        Check(Panels.All(kind => View(kind).Visibility == Visibility.Collapsed), "No transparent panel survives its closing animation.");
                    }

            Console.WriteLine($"Navigation fixture: page/panel matrix complete at {size.Width} ({_checks} assertions).");
            foreach (ShellOverlayKind panel in Panels)
            {
                OpenPanel(panel);
                await Settle();
                foreach ((string name, _) in Pages)
                {
                    Button target = Button(name);
                    focusProbe = new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, target)
                    { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent };
                    target.RaiseEvent(focusProbe);
                    Check(!focusProbe.Handled, $"{panel} must allow keyboard focus on {name}.");
                    focusProbe = null;
                }
                ClosePanels();
                await Settle();
            }

            foreach (ShellOverlayKind from in Panels)
                foreach (ShellOverlayKind to in Panels.Where(kind => kind != ShellOverlayKind.PatchNote))
                {
                    OpenPanel(from);
                    await Settle();
                    Invoke(Button(Opener(to)), pointer: true);
                    Check(shell.CurrentOverlay == (from == to ? ShellOverlayKind.None : to), "Panel buttons switch directly or toggle their own panel closed.");
                    await Settle();
                    Check(Panels.Count(kind => View(kind).IsHitTestVisible) == (from == to ? 0 : 1), "Exactly the selected panel accepts input.");
                    ClosePanels();
                    await Settle();
                }

            foreach (ShellOverlayKind panel in Panels)
            {
                // Switch before pending opening focus callbacks or animation completions run.
                OpenPanel(panel);
                Invoke(Button("SettingsButton"), pointer: true);
                focusAttempts.Clear();
                await Settle();
                Check(shell.CurrentPage == LauncherShellPage.Settings && shell.CurrentOverlay == ShellOverlayKind.None,
                    "Rapid navigation cannot reopen a departing panel.");
                Check(!focusAttempts.Any(target => Panels.Any(kind => IsWithin(target, View(kind)))),
                    "Queued focus callbacks cannot focus a panel that has already closed.");
                if (panel != ShellOverlayKind.PatchNote)
                    Check(!focusAttempts.Any(target => IsWithin(target, Button(Opener(panel)))),
                        "The departing panel cannot restore focus to its obsolete opener after navigation.");
            }

            foreach (ShellOverlayKind panel in Panels)
            {
                OpenPanel(panel);
                await Settle();
                ((FrameworkElement)shell.FindName("TitleBar")).RaiseEvent(
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Check(shell.CurrentOverlay == ShellOverlayKind.None, "Clicking blank chrome outside a panel also dismisses it.");
                await Settle();
            }

            OpenPanel(ShellOverlayKind.Friends);
            await Settle();
            Border scrim = (Border)shell.FriendsOverlay.FindName("Scrim");
            scrim.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Check(!shell.FriendsState.IsOpen, "Outside click dismisses the friends drawer.");
            await Settle();

            OpenPanel(ShellOverlayKind.Profile);
            Invoke((Button)shell.ProfileOverlay.FindName("ManageAccountButton"), pointer: false);
            await Settle();
            Check(shell.CurrentPage == LauncherShellPage.Account, "Account management opens from the profile menu.");
            shell.AccountState.ApplyRuntimeView(shell.AccountState.Current with { CanRemoveAvatar = true });
            shell.AccountState.ShowDeleteConfirmation();
            await Settle();
            OpenPanel(ShellOverlayKind.Friends);
            await Settle();
            PresentationSource source = PresentationSource.FromVisual(shell)!;
            shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!shell.FriendsState.IsOpen && shell.AccountState.Current.IsDeleteConfirmationOpen,
                "Escape closes the foreground friends panel before a confirmation behind it.");
            await Settle();
            Invoke(Button("SettingsButton"), pointer: true);
            await Settle();
            Check(shell.CurrentPage == LauncherShellPage.Settings && !shell.AccountState.Current.IsDeleteConfirmationOpen,
                "Leaving Account dismisses its confirmation so a hidden page cannot trap subsequent focus.");

            foreach (ShellOverlayKind modal in new[] { ShellOverlayKind.Authentication, ShellOverlayKind.AvatarCrop })
            {
                if (modal == ShellOverlayKind.Authentication) shell.AuthState.IsOpen = true;
                else shell.AvatarCropState.IsOpen = true;
                await Settle();
                Check(shell.CurrentOverlay == modal, $"Fixture actually opens {modal} before probing its boundary.");
                LauncherShellPage initial = shell.CurrentPage;
                foreach ((string name, _) in Pages) Invoke(Button(name), pointer: true);
                foreach (ShellOverlayKind panel in Panels.Where(kind => kind != ShellOverlayKind.PatchNote)) Invoke(Button(Opener(panel)), pointer: true);
                Check(shell.CurrentPage == initial && shell.CurrentOverlay == modal, $"{modal} retains its modal boundary; actual {shell.CurrentOverlay}/{shell.CurrentPage}.");
                shell.AuthState.IsOpen = false;
                shell.AvatarCropState.IsOpen = false;
                await Settle();
            }
            await ValidatePreservedNavigationAsync(shell);
            Check(!shell.IsActive && !shell.IsKeyboardFocusWithin && shell.Left < -10000 && !shell.ShowInTaskbar,
                "Fixture remains offscreen and never acquires desktop focus.");
        }
        finally { shell.Close(); await Dispatcher.Yield(DispatcherPriority.Background); }

        Button Button(string name) => (Button)shell.FindName(name);
        FrameworkElement View(ShellOverlayKind kind) => kind switch
        {
            ShellOverlayKind.Friends => shell.FriendsOverlay,
            ShellOverlayKind.Activity => shell.ActivityOverlay,
            ShellOverlayKind.Profile => shell.ProfileOverlay,
            _ => shell.PatchNoteReaderOverlay
        };
        void OpenPanel(ShellOverlayKind kind)
        {
            ClosePanels();
            if (kind == ShellOverlayKind.PatchNote) shell.PatchNoteState.IsOpen = true;
            else Invoke(Button(Opener(kind)), pointer: false);
            Check(shell.CurrentOverlay == kind, $"Fixture opens {kind} through its real state/handler.");
        }
        void ClosePanels()
        {
            shell.FriendsState.IsOpen = false;
            shell.ActivityState.IsOpen = false;
            shell.ProfileState.IsOpen = false;
            shell.PatchNoteState.IsOpen = false;
        }
        async Task Settle()
        {
            await Task.Delay(260);
            shell.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    private static string Opener(ShellOverlayKind kind) => kind switch
    {
        ShellOverlayKind.Friends => "FriendsButton",
        ShellOverlayKind.Activity => "ActivityButton",
        _ => "ProfileButton"
    };
    private static void Invoke(Button button, bool pointer)
    {
        if (pointer) button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    private static bool IsWithin(DependencyObject? target, DependencyObject parent)
    {
        for (DependencyObject? current = target; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
