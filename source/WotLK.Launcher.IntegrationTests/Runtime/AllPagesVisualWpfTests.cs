using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

/// <summary>Native preview captures without desktop activation, real services or OS actions.</summary>
internal static class AllPagesVisualWpfTests
{
    internal static async Task<int> RunAsync(string? captureDirectory, bool baseline)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunHarness(completion, captureDirectory, baseline))
        { IsBackground = true, Name = "AtlasAllPagesOffscreenHarness" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(120));
        Console.WriteLine($"All pages visual WPF OK ({(baseline ? "baseline" : "validation")}); isolated offscreen preview, no desktop interaction.");
        return 0;
    }

    private static void RunHarness(TaskCompletionSource completion, string? directory, bool baseline)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        dispatcher.UnhandledException += (_, args) =>
        { failure ??= args.Exception; args.Handled = true; dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
        _ = WorkAsync();
        Dispatcher.Run();
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);

        async Task WorkAsync()
        {
            Application? application = null;
            LauncherShellV2? window = null;
            List<object> captures = [];
            List<string> interactionProbes = [];
            BindingErrorListener bindingErrors = new();
            SourceLevels priorLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
            Stopwatch elapsed = Stopwatch.StartNew();
            string originalLocale = LauncherLocalization.CurrentLocale;
            try
            {
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/WotLK.Launcher;component/{resource}", UriKind.Relative) });
                LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);

                window = CreateWindow(new LauncherShellV2(GamePreviewScenario.Ready, AddonsPreviewScenario.Default));
                window.Show();
                await PumpAsync(window);
                foreach ((string name, LauncherShellPage page, string navigation) in new[]
                {
                    ("addons", LauncherShellPage.Addons, "AddonsNavigationButton"),
                    ("notes", LauncherShellPage.PatchNotes, "PatchNotesNavigationButton"),
                    ("settings", LauncherShellPage.Settings, "SettingsButton")
                })
                {
                    await InvokeAsync(Required<Button>(window, navigation), window);
                    True(window.CurrentPage == page, $"Navigation {name} must reach its page.");
                    foreach ((int width, int height) in new[] { (1672, 941), (1080, 680) })
                        await CaptureAsync(window, name, width, height, directory, captures, baseline);
                }
                if (!baseline)
                {
                    await ValidateSettingsAsync(window, directory, captures);
                    await ValidateAddonsAsync(window, directory, captures);
                    await InvokeAsync(Required<Button>(window, "PatchNotesNavigationButton"), window);
                    ScrollViewer notes = Descendants<ScrollViewer>(window).Single(scroll => scroll.IsVisible && scroll.Name == "PatchNotesScrollViewer");
                    notes.ScrollToBottom();
                    await PumpAsync(window);
                    True(notes.ScrollableHeight <= .5 || notes.VerticalOffset > 0, "Long release notes must remain vertically reachable.");
                    await CaptureAsync(window, "notes-bottom", 1080, 680, directory, captures, false);
                    await ValidateEnglishPagesAsync(window, directory, captures);
                    window.Close(); window = null;

                    foreach ((string name, Func<LauncherShellV2> factory) in new (string, Func<LauncherShellV2>)[]
                    {
                        ("profile-menu", () => new(GamePreviewScenario.Ready, ProfilePreviewScenario.SignedIn)),
                        ("profile-menu-unverified", () => new(GamePreviewScenario.Ready, ProfilePreviewScenario.EmailUnverified)),
                        ("friends", () => new(GamePreviewScenario.Ready, FriendsPreviewScenario.Populated)),
                        ("friends-incoming", () => new(GamePreviewScenario.Ready, FriendsPreviewScenario.IncomingRequests)),
                        ("friends-outgoing", () => new(GamePreviewScenario.Ready, FriendsPreviewScenario.OutgoingRequests)),
                        ("friends-empty", () => new(GamePreviewScenario.Ready, FriendsPreviewScenario.Empty)),
                        ("friends-many", () => new(GamePreviewScenario.Ready, FriendsPreviewScenario.ManyFriends)),
                        ("activity", () => new(GamePreviewScenario.Ready, ActivityPreviewScenario.GameDownload)),
                        ("activity-queue", () => new(GamePreviewScenario.Ready, ActivityPreviewScenario.AddonBatch)),
                        ("activity-error", () => new(GamePreviewScenario.Ready, ActivityPreviewScenario.Error)),
                        ("activity-empty", () => new(GamePreviewScenario.Ready, ActivityPreviewScenario.Idle)),
                        ("account", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.Profile)),
                        ("account-sessions", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.Sessions)),
                        ("account-password", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.PasswordChange)),
                        ("account-password-error", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.PasswordError)),
                        ("account-email", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.EmailChange)),
                        ("account-email-unverified", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.EmailUnverified)),
                        ("account-session-revoke", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.SessionRevoke)),
                        ("account-session-error", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.SessionRevokeError)),
                        ("account-crop", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.Crop)),
                        ("account-crop-uploading", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.Uploading)),
                        ("account-crop-error", () => new(GamePreviewScenario.Ready, AccountPreviewScenario.UploadError)),
                        ("auth-login", () => new(GamePreviewScenario.Ready, AuthPreviewScenario.Login)),
                        ("auth-register", () => new(GamePreviewScenario.Ready, AuthPreviewScenario.Register))
                    })
                    {
                        window = CreateWindow(factory());
                        window.Show(); await Task.Delay(260); await PumpAsync(window);
                        foreach ((int width, int height) in new[] { (1672, 941), (1080, 680) })
                            await CaptureAsync(window, name, width, height, directory, captures, false);
                        if (name == "friends")
                            await ValidateFriendsAsync(window, directory, captures, interactionProbes);
                        else if (name == "friends-many")
                        {
                            window.FriendsOverlay.ScrollHost.ScrollToBottom();
                            await PumpAsync(window);
                            True(window.FriendsOverlay.ScrollHost.VerticalOffset > 0, "The last entries of a long friends list must remain reachable.");
                            await CaptureAsync(window, "friends-many-bottom", 1080, 680, directory, captures, false);
                        }
                        else if (name == "activity")
                            await ValidateActivityAsync(window, directory, captures, interactionProbes);
                        else if (name == "account")
                            await ValidateAccountAsync(window, directory, captures, interactionProbes);
                        else if (name == "profile-menu")
                            await ValidateProfileMenuAsync(window, directory, captures, interactionProbes);
                        window.Close(); window = null;
                        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                }
                True(bindingErrors.Messages.Count == 0, "No WPF binding errors are allowed; see binding-errors.txt.");
            }
            catch (Exception exception) { failure ??= exception; }
            finally
            {
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    File.WriteAllLines(Path.Combine(directory, "binding-errors.txt"), bindingErrors.Messages);
                    File.WriteAllText(Path.Combine(directory, "capture-evidence.json"), JsonSerializer.Serialize(new
                    {
                        CapturedAtUtc = DateTimeOffset.UtcNow, Baseline = baseline,
                        CaptureMethod = "WPF RenderTargetBitmap(window), 96 DPI, no rescaling or retouching",
                        CaptureLimitation = "Alpha RenderTargetBitmap captures validate layout only; they do not prove native RGB ClearType antialiasing.",
                        TypographyRequirement = baseline ? "Record effective text modes without asserting the new requirement"
                            : "Every visible TextBlock, TextBox, PasswordBox and ComboBox must use Ideal and ClearType",
                        SourceOfData = "Existing LauncherShellV2 preview state; no real service attached",
                        InteractionMethod = "WPF AutomationPeer, synthetic routed key events and local recording commands; no OS input",
                        InteractionProbes = interactionProbes,
                        Offscreen = true, ShowActivated = false, ShowInTaskbar = false, KeyboardFocusSuppressed = true,
                        NotExercised = new[] { "Desktop input, taskbar/tray, OS settings, Windows popups/context menus", "Real network, addons installation/deletion, game launch", "WebView armory rendering (separate harness)" },
                        BindingErrors = bindingErrors.Messages, ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                        Failure = failure?.ToString(), Captures = captures
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                window?.Close();
                LauncherLocalization.SetLocale(originalLocale);
                application?.Shutdown();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = priorLevel;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateEnglishPagesAsync(LauncherShellV2 window, string? directory, List<object> captures)
    {
        // This uses the same in-process localization event as production and never
        // writes Windows settings or an account preference.
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
        await PumpAsync(window);
        foreach ((string scenario, string navigation, FrameworkElement page, string title) in new[]
        {
            ("addons-en", "AddonsNavigationButton", (FrameworkElement)window.AddonsPage, "ADDONS"),
            ("notes-en", "PatchNotesNavigationButton", (FrameworkElement)window.PatchNotesPage, "Release notes"),
            ("settings-en", "SettingsButton", (FrameworkElement)window.SettingsPage, "Settings")
        })
        {
            await InvokeAsync(Required<Button>(window, navigation), window);
            True(Required<TextBlock>(page, "PageTitle").Text == title, $"{scenario}: page title must translate through the existing bridge.");
            foreach (ScrollViewer scroll in Descendants<ScrollViewer>(page)) scroll.ScrollToTop();
            foreach ((int width, int height) in new[] { (1672, 941), (1080, 680) })
                await CaptureAsync(window, scenario, width, height, directory, captures, false);
        }
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        await PumpAsync(window);
        True(Required<TextBlock>(window.PatchNotesPage, "PageTitle").Text == "Notes de version"
            && Required<TextBlock>(window.SettingsPage, "PageTitle").Text == "Paramètres",
            "Switching back to French must restore the original native page titles.");
    }

    private static async Task ValidateSettingsAsync(LauncherShellV2 window, string? directory, List<object> captures)
    {
        foreach (SettingsCategory category in Enum.GetValues<SettingsCategory>())
        {
            await InvokeAsync(Required<Button>(window.SettingsPage, $"{category}CategoryButton"), window);
            True(window.SettingsPage.SelectedCategory == category, $"Settings navigation must select {category}.");
            True(Required<FrameworkElement>(window.SettingsPage, $"{category}Panel").IsVisible, $"Settings {category} panel must be visible.");
            foreach ((int width, int height) in new[] { (1672, 941), (1080, 680) })
                await CaptureAsync(window, $"settings-{category.ToString().ToLowerInvariant()}", width, height, directory, captures, false);
        }
        await InvokeAsync(Required<Button>(window.SettingsPage, "GeneralCategoryButton"), window);
        ToggleButton toggle = Required<ToggleButton>(window.SettingsPage, "MinimizeToTrayOnCloseToggle");
        bool? previous = toggle.IsChecked;
        ToggleButtonAutomationPeer peer = new(toggle);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
        await PumpAsync(window);
        True(toggle.IsChecked != previous, "Native settings toggle must respond in isolated preview.");
        await Task.Delay(220);
        await CaptureAsync(window, "settings-toggle", 1080, 680, directory, captures, false);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
        await PumpAsync(window);
        True(toggle.IsChecked == previous, "Native settings toggle must restore its local preview value.");
    }

    private static async Task ValidateAddonsAsync(LauncherShellV2 window, string? directory, List<object> captures)
    {
        await InvokeAsync(Required<Button>(window, "AddonsNavigationButton"), window);
        foreach ((string name, AddonCatalogFilter filter) in new[]
        {
            ("InstalledFilterButton", AddonCatalogFilter.Installed),
            ("UpdatesFilterButton", AddonCatalogFilter.Updates),
            ("AllFilterButton", AddonCatalogFilter.All)
        })
        {
            await InvokeAsync(Required<Button>(window.AddonsPage, name), window);
            True(window.AddonsState.Current.Filter == filter, $"Addon filter {filter} must remain bound.");
            True(window.AddonsPage.ListHost.Items.Count == window.AddonsState.Current.VisibleAddons.Length,
                $"Addon filter {filter} must update the visible list.");
        }
        window.AddonsPage.SearchBox.Text = "AtlasLootClassic";
        await PumpAsync(window);
        True(window.AddonsState.Current.SearchText == "AtlasLootClassic" && window.AddonsPage.ListHost.Items.Count == 1,
            "Typing through the WPF search textbox must filter the preview catalog.");
        await CaptureAsync(window, "addons-search", 1080, 680, directory, captures, false);
        window.AddonsPage.SearchBox.Text = "no-match-all-pages-harness";
        await PumpAsync(window);
        True(window.AddonsPage.ListHost.Items.Count == 0 && Required<FrameworkElement>(window.AddonsPage, "EmptyState").IsVisible,
            "Unmatched search must show the empty state.");
        await CaptureAsync(window, "addons-empty", 1080, 680, directory, captures, false);
        window.AddonsPage.SearchBox.Text = string.Empty;
        await PumpAsync(window);
        Button details = Descendants<Button>(window.AddonsPage.ListHost).Single(button =>
            button.DataContext is AddonUiItem { Id: "atlaslootclassic" }
            && AutomationProperties.GetName(button) == "Voir les détails et les actions de cet addon");
        await InvokeAsync(details, window);
        True(window.AddonsState.Current.SelectedAddon?.Id == "atlaslootclassic", "The native ellipsis action must select its own addon.");
        True(window.AddonsPage.IsDetailOpen, "Addon detail must be rendered.");
        await CaptureAsync(window, "addons-detail", 1080, 680, directory, captures, false);
        await InvokeAsync(Required<Button>(window.AddonsPage, "RemoveSelectedAddonButton"), window);
        True(window.AddonsPage.IsDeleteConfirmationOpen, "Remove must open the isolated confirmation without deleting.");
        await CaptureAsync(window, "addons-confirmation", 1080, 680, directory, captures, false);
        await InvokeAsync(Required<Button>(window.AddonsPage, "CancelDeleteButton"), window);
        True(!window.AddonsPage.IsDeleteConfirmationOpen, "Cancel must dismiss addon removal confirmation.");
        await InvokeAsync(Required<Button>(window.AddonsPage, "CloseDetailButton"), window);
        True(!window.AddonsPage.IsDetailOpen, "Close must dismiss addon detail.");
    }

    private static async Task ValidateFriendsAsync(LauncherShellV2 window, string? directory, List<object> captures, List<string> probes)
    {
        FriendsDrawerV2 drawer = window.FriendsOverlay;
        FriendsUiState state = window.FriendsState;
        FriendsViewState originalView = state.Current;
        ICommand originalRemove = state.RemoveFriendCommand;
        RecordingCommand removal = new();
        state.AttachCommands(state.RefreshCommand, state.SendRequestCommand, state.AcceptRequestCommand,
            state.RejectRequestCommand, state.CancelRequestCommand, removal);
        await PumpAsync(window);
        try
        {
            Button profile = Descendants<Button>(drawer).First(button => button.IsVisible
                && button.DataContext is FriendUiItem item
                && AutomationProperties.GetName(button) == item.Username);
            FriendUiItem friend = (FriendUiItem)profile.DataContext;
            True(profile.Focusable && !profile.IsTabStop && drawer.ContainsKeyboardFocusTarget(profile),
                "Friend profile buttons must remain native pointer targets excluded from Tab navigation.");
            await InvokeAsync(profile, window);
            True(drawer.IsFriendProfileOpen && state.SelectedFriend?.AccountId == friend.AccountId,
                "Invoking a native friend button must open that friend's profile.");
            foreach ((int width, int height) in new[] { (1672, 941), (1080, 680) })
                await CaptureAsync(window, "friends-profile", width, height, directory, captures, false);
            await RaiseKeyAsync(Required<Button>(drawer, "BackToFriendsButton"), window, Key.Escape, preview: true);
            True(!drawer.IsFriendProfileOpen && drawer.IsOpen, "Escape must return from a friend profile to the friends list.");
            probes.Add("Friends: native button is excluded from Tab navigation; native Invoke opens the correct profile; Escape returns to the list.");

            await InvokeAsync(Required<Button>(drawer, "AddFriendToggleButton"), window);
            await Task.Delay(180); await PumpAsync(window);
            True(drawer.IsAddFriendEditorOpen, "The add-friend input must open from its icon button.");
            await CaptureAsync(window, "friends-add", 1080, 680, directory, captures, false);
            await RaiseKeyAsync(drawer.SearchInput, window, Key.Escape, preview: true);
            True(!drawer.IsAddFriendEditorOpen && drawer.IsOpen, "Escape must close only the add-friend editor.");

            foreach ((string locale, string suffix, string title, string cancel, string confirm) in new[]
            {
                (LauncherLocalization.FrenchLocale, "fr", "Retirer cet ami ?", "Annuler", "Retirer"),
                (LauncherLocalization.EnglishLocale, "en", "Remove this friend?", "Cancel", "Remove")
            })
            {
                LauncherLocalization.SetLocale(locale); await PumpAsync(window);
                await OpenFriendRemovalWithoutPopupAsync(window, friend);
                Button cancelButton = Required<Button>(drawer, "CancelRemoveFriendButton");
                Button confirmButton = Required<Button>(drawer, "ConfirmRemoveFriendButton");
                True(drawer.IsRemoveFriendConfirmationOpen
                    && Required<TextBlock>(drawer, "RemoveFriendTitleText").Text == title
                    && Equals(cancelButton.Content, cancel) && Equals(confirmButton.Content, confirm)
                    && Required<TextBlock>(drawer, "RemoveFriendUsernameText").Text == friend.Username,
                    $"Friend removal confirmation must translate in {locale} and identify the exact friend.");
                True(drawer.ContainsKeyboardFocusTarget(cancelButton) && drawer.ContainsKeyboardFocusTarget(confirmButton)
                    && !drawer.ContainsKeyboardFocusTarget(Required<Button>(drawer, "AddFriendToggleButton")),
                    "The modal focus boundary must allow only the confirmation controls.");
                foreach ((int width, int height) in new[] { (1672, 941), (1080, 680) })
                    await CaptureAsync(window, $"friends-remove-{suffix}", width, height, directory, captures, false);
                await RaiseKeyAsync(cancelButton, window, Key.Escape, preview: true);
                True(!drawer.IsRemoveFriendConfirmationOpen && drawer.IsOpen && removal.Arguments.Count == 0,
                    "Escape must cancel friend removal without dispatching any command or closing the drawer.");
                await OpenFriendRemovalWithoutPopupAsync(window, friend);
                await InvokeAsync(confirmButton, window);
                True(!drawer.IsRemoveFriendConfirmationOpen && removal.Arguments.Count == 0,
                    "Preview confirmation must close safely without dispatching removal.");
            }

            // Runtime-shaped data with a recording ICommand is isolated from every backend.
            state.ApplyRuntimeView(originalView with { IsPreview = false, IsRuntimeConnected = true });
            await PumpAsync(window);
            await OpenFriendRemovalWithoutPopupAsync(window, friend);
            await InvokeAsync(Required<Button>(drawer, "CancelRemoveFriendButton"), window);
            True(removal.Arguments.Count == 0, "Cancel must not dispatch removal even with runtime-shaped local data.");
            await OpenFriendRemovalWithoutPopupAsync(window, friend);
            Button confirmRuntime = Required<Button>(drawer, "ConfirmRemoveFriendButton");
            await InvokeAsync(confirmRuntime, window);
            confirmRuntime.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, confirmRuntime));
            True(removal.Arguments.Count == 1 && Equals(removal.Arguments[0], friend.AccountId),
                "A confirmed removal must dispatch the exact friend once, including a repeated click.");
            await OpenFriendRemovalWithoutPopupAsync(window, friend);
            state.ApplyRuntimeView(state.Current with { Friends = state.Current.Friends.Where(item => item.AccountId != friend.AccountId).ToImmutableArray() });
            await PumpAsync(window); await InvokeAsync(confirmRuntime, window);
            True(removal.Arguments.Count == 1, "A friend disappearing before confirmation must not dispatch a stale removal.");
            probes.Add("Friends removal: FR/EN title/actions, modal focus membership, Escape/Cancel, preview guard, single local command for exact ID, stale friend guard. Popup never opened.");
        }
        finally
        {
            state.ApplyRuntimeView(originalView);
            state.AttachCommands(state.RefreshCommand, state.SendRequestCommand, state.AcceptRequestCommand,
                state.RejectRequestCommand, state.CancelRequestCommand, originalRemove);
            LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
            await PumpAsync(window);
        }
    }

    private static async Task OpenFriendRemovalWithoutPopupAsync(LauncherShellV2 window, FriendUiItem friend)
    {
        Button actions = Descendants<Button>(window.FriendsOverlay).First(button => button.Tag is Popup
            && button.DataContext is FriendUiItem item && item.AccountId == friend.AccountId);
        Popup popup = actions.Tag as Popup ?? throw new InvalidOperationException("Missing friend action popup fixture.");
        True(!popup.IsOpen, "Offscreen harness must never open an HWND popup, which Windows can clamp onto the desktop.");
        Button remove = popup.Child as Button ?? throw new InvalidOperationException("Missing native remove-friend action.");
        remove.DataContext = friend;
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, remove));
        await PumpAsync(window); AssertIsolated(window);
        True(!popup.IsOpen, "Opening in-window confirmation must not open the native popup.");
    }

    private static async Task ValidateActivityAsync(LauncherShellV2 window, string? directory, List<object> captures, List<string> probes)
    {
        ActivityCenterPanelV2 panel = window.ActivityOverlay;
        TextBlock transfer = Descendants<TextBlock>(panel).Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == "TransferText");
        TextBlock rate = Descendants<TextBlock>(panel).Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == "RateAndEtaText");
        Point transferPosition = transfer.TranslatePoint(new Point(), panel);
        Point ratePosition = rate.TranslatePoint(new Point(), panel);
        True(ratePosition.Y >= transferPosition.Y + transfer.ActualHeight,
            "Transfer size and rate/ETA must occupy separate, nonoverlapping lines at the compact viewport.");
        int cancelled = 0;
        panel.CancelRequested += (_, _) => cancelled++;
        Button cancel = Descendants<Button>(panel).Single(button => button.DataContext is ActivityOperationUiItem
            && button.GetBindingExpression(Button.ContentProperty)?.ParentBinding.Path?.Path == "CancelActionLabel");
        await InvokeAsync(cancel, window);
        True(cancelled == 1, "The activity cancel button must still emit its cancellation request exactly once in preview.");
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); await PumpAsync(window);
        True(Descendants<TextBlock>(panel).Any(text => text.IsVisible && text.Text == "Activity"), "Activity heading must translate.");
        await CaptureAsync(window, "activity-en", 1080, 680, directory, captures, false);
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale); await PumpAsync(window);
        probes.Add("Activity: transfer and ETA bounds do not overlap; preview cancellation event preserved; English heading verified.");
    }

    private static async Task ValidateAccountAsync(LauncherShellV2 window, string? directory, List<object> captures, List<string> probes)
    {
        AccountViewV2 account = window.AccountPage;
        True(account.SelectedSection == AccountSection.Security && Required<FrameworkElement>(account, "SecurityPanel").IsVisible,
            "The account landing page must default to Security.");
        True(!Required<Button>(account, "ProfileTabButton").IsVisible && !Required<Button>(account, "ProfileTabButton").IsTabStop
            && !Required<FrameworkElement>(account, "ProfilePanel").IsVisible,
            "The account page must expose no duplicate Profile tab or panel.");
        await InvokeAsync(Required<Button>(account, "SessionsTabButton"), window);
        True(account.SelectedSection == AccountSection.Sessions && Required<FrameworkElement>(account, "SessionsPanel").IsVisible,
            "Account Sessions must remain accessible through the native tab.");
        await InvokeAsync(Required<Button>(account, "SecurityTabButton"), window);
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); await PumpAsync(window);
        True(Required<TextBlock>(account, "PageTitle").Text == "My account"
            && Equals(Required<Button>(account, "SecurityTabButton").Content, "Security"),
            "The account heading and Security tab must translate.");
        await CaptureAsync(window, "account-security-en", 1080, 680, directory, captures, false);
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale); await PumpAsync(window);
        probes.Add("Account: Security default; duplicate Profile UI hidden and excluded from tab order; Sessions navigation and English labels preserved.");
    }

    private static async Task ValidateProfileMenuAsync(LauncherShellV2 window, string? directory, List<object> captures, List<string> probes)
    {
        ProfileMenuV2 menu = window.ProfileOverlay;
        foreach (string name in new[] { "ManageProfileButton", "ManageAccountButton", "LogoutButton" })
        {
            Button button = Required<Button>(menu, name);
            True(button.IsVisible && !button.IsTabStop && button.Focusable,
                $"Profile action {name} must remain a visible native pointer target excluded from Tab navigation.");
        }
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); await PumpAsync(window);
        True(Descendants<TextBlock>(menu).Any(text => text.IsVisible && text.Text == "Manage my profile")
            && Descendants<TextBlock>(menu).Any(text => text.IsVisible && text.Text == "Manage my account"),
            "Profile menu actions must translate through the existing localization bridge.");
        await CaptureAsync(window, "profile-menu-en", 1080, 680, directory, captures, false);
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale); await PumpAsync(window);
        probes.Add("Profile menu: all three native actions are excluded from Tab navigation; English action labels verified.");
    }

    private static async Task RaiseKeyAsync(UIElement target, LauncherShellV2 window, Key key, bool preview = false)
    {
        PresentationSource source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Missing isolated WPF presentation source.");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        { RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent });
        await PumpAsync(window); AssertIsolated(window);
    }

    private static LauncherShellV2 CreateWindow(LauncherShellV2 window)
    {
        window.Width = 1672; window.Height = 941;
        window.Left = -20000; window.Top = -20000;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false; window.ShowActivated = false;
        string version = typeof(LauncherShellV2).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        typeof(ShellUiState).GetProperty(nameof(ShellUiState.LauncherVersion))!.SetValue(window.ShellState, $"v{version}-local");
        Required<Border>(window, "LocalBuildBadge").GetBindingExpression(UIElement.VisibilityProperty)?.UpdateTarget();
        window.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
        return window;
    }

    private static async Task CaptureAsync(LauncherShellV2 window, string scenario, int width, int height,
        string? directory, List<object> captures, bool baseline)
    {
        window.Width = width; window.Height = height;
        await PumpAsync(window);
        AssertIsolated(window);
        True(Math.Abs(window.ActualWidth - width) < 0.5 && Math.Abs(window.ActualHeight - height) < 0.5,
            $"{scenario}: unexpected native viewport {window.ActualWidth}x{window.ActualHeight}.");
        foreach (ScrollViewer scroll in Descendants<ScrollViewer>(window).Where(control => control.IsVisible))
            True(scroll.ScrollableWidth <= 0.5, $"{scenario} {width}x{height}: horizontal overflow in {scroll.Name}.");
        if (!baseline && window.CurrentPage == LauncherShellPage.Addons)
        {
            foreach (Grid row in Descendants<Grid>(window.AddonsPage.ListHost).Where(grid => grid.DataContext is AddonUiItem && grid.ColumnDefinitions.Count == 6))
            {
                True(width > 1080 || row.ColumnDefinitions[2].ActualWidth <= .5,
                    $"{scenario}: addon categories must collapse in the compact viewport.");
                True(row.ColumnDefinitions.Sum(column => column.ActualWidth) <= row.ActualWidth + .5,
                    $"{scenario}: addon columns must not overflow their row.");
            }
        }
        if (!baseline && window.CurrentPage != LauncherShellPage.Game)
        {
            FrameworkElement backdrop = Required<FrameworkElement>(window, "SecondaryBackdrop");
            Point origin = backdrop.TranslatePoint(new Point(), window);
            True(backdrop.IsVisible && !backdrop.IsHitTestVisible && Math.Abs(origin.X) < .5 && Math.Abs(origin.Y) < .5
                && Math.Abs(backdrop.ActualWidth - window.ActualWidth) < .5 && Math.Abs(backdrop.ActualHeight - window.ActualHeight) < .5,
                $"{scenario}: decorative citadel backdrop must fill the window and not intercept controls.");
            True(backdrop.Resources["CitadelArtwork"] is BitmapImage artwork
                && artwork.UriSource.OriginalString.EndsWith("Assets/Launcher/visuals/icecrown-citadel.png", StringComparison.OrdinalIgnoreCase),
                $"{scenario}: backdrop must use the embedded citadel image.");
            True(Descendants<UIElement>(backdrop).Any(element => element.Effect is BlurEffect { Radius: > 0 }),
                $"{scenario}: citadel backdrop must retain its frost blur.");
        }
        foreach (string buttonName in new[] { "GameNavigationButton", "AddonsNavigationButton", "PatchNotesNavigationButton", "SettingsButton", "CloseWindowButton" })
        {
            Button button = Required<Button>(window, buttonName);
            Point origin = button.TranslatePoint(new Point(), window);
            True(button.IsVisible && origin.X >= 0 && origin.Y >= 0 && origin.X + button.ActualWidth <= window.ActualWidth + .5
                && origin.Y + button.ActualHeight <= window.ActualHeight + .5, $"{buttonName} must remain inside viewport.");
        }
        List<object> fonts = [];
        foreach (TextBlock text in Descendants<TextBlock>(window).Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text)))
        {
            Typeface typeface = new(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            bool resolved = typeface.TryGetGlyphTypeface(out GlyphTypeface glyph);
            string? physicalFont = resolved ? glyph.FontUri.OriginalString : null;
            fonts.Add(new { text.Name, text.Text, FontFamily = text.FontFamily.Source, text.FontSize, Weight = text.FontWeight.ToString(), PhysicalFont = physicalFont });
            if (!baseline)
            {
                True(resolved && physicalFont!.Contains("Inter-", StringComparison.OrdinalIgnoreCase)
                    && glyph.StyleSimulations == StyleSimulations.None && glyph.Weight == text.FontWeight,
                    $"{scenario}: '{text.Text}' does not use physical embedded Inter without synthetic weight ({physicalFont ?? text.FontFamily.Source}, {text.FontWeight}).");
                if (text.Name is "PageTitle" or "BrandName" || text.Parent is Button)
                    AssertTextFits(text, scenario);
            }
        }
        foreach (Control control in Descendants<Control>(window).Where(control => control.IsVisible && control is TextBox or PasswordBox or ComboBox))
        {
            Typeface typeface = new(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch);
            bool resolved = typeface.TryGetGlyphTypeface(out GlyphTypeface glyph);
            string? physicalFont = resolved ? glyph.FontUri.OriginalString : null;
            fonts.Add(new { control.Name, Control = control.GetType().Name, FontFamily = control.FontFamily.Source,
                control.FontSize, Weight = control.FontWeight.ToString(), PhysicalFont = physicalFont });
            if (!baseline)
                True(resolved && physicalFont!.Contains("Inter-", StringComparison.OrdinalIgnoreCase)
                    && glyph.StyleSimulations == StyleSimulations.None && glyph.Weight == control.FontWeight,
                    $"{scenario}: input {control.Name} does not use physical embedded Inter ({physicalFont ?? control.FontFamily.Source}).");
        }
        List<object> textRendering = [];
        List<string> textRenderingMismatches = [];
        foreach (FrameworkElement element in Descendants<FrameworkElement>(window).Where(element => element.IsVisible
            && element is TextBlock or TextBox or PasswordBox or ComboBox))
        {
            TextFormattingMode formatting = TextOptions.GetTextFormattingMode(element);
            TextRenderingMode rendering = TextOptions.GetTextRenderingMode(element);
            string? text = element is TextBlock textBlock ? textBlock.Text : null;
            bool matches = formatting == TextFormattingMode.Ideal && rendering == TextRenderingMode.ClearType;
            textRendering.Add(new
            {
                element.Name, Type = element.GetType().Name, Text = text,
                TextFormattingMode = formatting.ToString(), TextRenderingMode = rendering.ToString(),
                FormattingValueSource = DependencyPropertyHelper.GetValueSource(element, TextOptions.TextFormattingModeProperty).BaseValueSource.ToString(),
                RenderingValueSource = DependencyPropertyHelper.GetValueSource(element, TextOptions.TextRenderingModeProperty).BaseValueSource.ToString(),
                MatchesRequirement = matches
            });
            if (!matches)
                textRenderingMismatches.Add($"{element.GetType().Name}#{element.Name} '{text}' uses {formatting}/{rendering}");
        }
        string fileName = $"{scenario}-{width}x{height}.png";
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(Path.Combine(directory, fileName)); encoder.Save(stream);
        }
        captures.Add(new { FileName = fileName, Scenario = scenario, Locale = LauncherLocalization.CurrentLocale, window.ActualWidth, window.ActualHeight,
            BitmapDpi = 96, WindowDpi = dpi.PixelsPerInchX, Fonts = fonts, TextRendering = textRendering });
        if (!baseline)
            True(textRenderingMismatches.Count == 0,
                $"{scenario} {width}x{height}: visible text must use Ideal/ClearType: {string.Join("; ", textRenderingMismatches)}.");
        Console.WriteLine($"{fileName}: {window.ActualWidth}x{window.ActualHeight} DIP, physical font entries={fonts.Count}.");
    }

    private static void AssertTextFits(TextBlock text, string scenario)
    {
        FormattedText natural = new(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            text.Foreground, null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
        double availableWidth = Math.Max(1, text.ActualWidth - text.Padding.Left - text.Padding.Right);
        double availableHeight = Math.Max(1, text.ActualHeight - text.Padding.Top - text.Padding.Bottom);
        if (text.TextWrapping != TextWrapping.NoWrap) natural.MaxTextWidth = availableWidth;
        if (!double.IsNaN(text.LineHeight) && text.LineHeight > 0) natural.LineHeight = text.LineHeight;
        True((text.TextTrimming != TextTrimming.None || natural.Width <= availableWidth + 2) && natural.Height <= availableHeight + 2,
            $"{scenario}: '{text.Text}' is clipped ({natural.Width:F1}x{natural.Height:F1} vs {availableWidth:F1}x{availableHeight:F1}).");
    }

    private static void AssertIsolated(LauncherShellV2 window)
    {
        True(window.IsPreviewMode && !window.HasRealAuthenticationAttached && !window.HasRealAddonsAttached && !window.HasRealActivityAttached,
            "Harness must have preview state and no real services.");
        True(window.Left <= -10000 && window.Top <= -10000 && !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar,
            "Harness must remain offscreen, inactive and absent from the taskbar.");
    }

    private static async Task InvokeAsync(Button button, LauncherShellV2 window)
    {
        ButtonAutomationPeer peer = new(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        await PumpAsync(window);
        AssertIsolated(window);
    }
    private static async Task PumpAsync(Window window)
    { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static T Required<T>(FrameworkElement scope, string name) where T : FrameworkElement =>
        scope.FindName(name) as T ?? throw new InvalidOperationException($"Missing WPF element {name}.");
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class RecordingCommand : ICommand
    {
        public List<object?> Arguments { get; } = [];
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => Arguments.Add(parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
    private sealed class BindingErrorListener : TraceListener
    {
        private string pending = string.Empty;
        public List<string> Messages { get; } = [];
        public override void Write(string? message) => pending += message;
        public override void WriteLine(string? message)
        { string line = pending + message; pending = string.Empty; if (!string.IsNullOrWhiteSpace(line) && !Messages.Contains(line)) Messages.Add(line); }
    }
}
