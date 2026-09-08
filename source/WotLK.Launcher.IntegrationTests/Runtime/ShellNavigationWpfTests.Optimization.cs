using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static partial class ShellNavigationWpfTests
{
    private static async Task ValidatePreservedNavigationAsync(LauncherShellV2 shell)
    {
        shell.AuthState.IsOpen = false;
        shell.AvatarCropState.IsOpen = false;
        shell.ProfileState.IsOpen = false;
        shell.FriendsState.IsOpen = false;
        shell.PatchNoteState.IsOpen = false;
        shell.ActivityState.IsOpen = false;
        PatchNoteEntryViewState release = new("test", "1.0", "Fixture release", "08/09/2026", "Fixture", true, false, false,
            [new("Changes", ["First change", "Second change", "Third change"])]);
        shell.DashboardState.ApplyView(shell.DashboardState.Current with
        {
            PatchNotes = Enumerable.Range(1, 600).Select(index => release with { Id = index.ToString(), Version = "1." + index }).ToImmutableArray()
        });
        await Navigate("PatchNotesNavigationButton");
        ItemsControl patchList = shell.PatchNotesPage.ListHost;
        Check(Realized(patchList) is > 0 and < 30, $"600 releases only generate viewport containers; actual {Realized(patchList)}.");
        ScrollViewer patchScroll = shell.PatchNotesPage.ScrollHost;
        patchScroll.ScrollToVerticalOffset(2000);
        await Layout();
        double patchOffset = patchScroll.VerticalOffset;
        Check(patchOffset > 500, "The patch-note fixture really scrolls.");
        await Navigate("SettingsButton");
        await Navigate("PatchNotesNavigationButton");
        Check(Math.Abs(patchScroll.VerticalOffset - patchOffset) < 2,
            $"Returning to notes preserves reading position: expected {patchOffset}, actual {patchScroll.VerticalOffset}.");
        patchScroll.ScrollToEnd();
        await Layout();
        Check(Realized(patchList) < 30 && patchList.ItemContainerGenerator.ContainerFromIndex(599) is not null,
            "The last release is reachable without materializing the entire history.");

        AddonsViewState addons = AddonsPreviewData.Create(AddonsPreviewScenario.Default).Current;
        AddonUiItem addon = addons.Catalog[0];
        shell.AddonsState.ApplyRuntimeView(addons with
        {
            Catalog = Enumerable.Range(1, 500).Select(index => addon with { Id = "fixture-" + index, Name = "Addon " + index.ToString("D4") }).ToImmutableArray()
        });
        await Navigate("AddonsNavigationButton");
        ListBox addonList = shell.AddonsPage.ListHost;
        AddonUiItem chosen = (AddonUiItem)addonList.Items[200];
        addonList.SelectedItem = chosen;
        addonList.ScrollIntoView(chosen);
        await Layout();
        ScrollViewer addonScroll = Visuals<ScrollViewer>(addonList).First();
        double addonOffset = addonScroll.VerticalOffset;
        Check(addonOffset > 0, "The addon fixture really scrolls.");
        await Navigate("GameNavigationButton");
        await Navigate("AddonsNavigationButton");
        Check(addonList.SelectedItem is AddonUiItem selected && selected.Id == chosen.Id, "The focused addon survives a page round-trip.");
        Check(Math.Abs(addonScroll.VerticalOffset - addonOffset) < 2, $"Addon scroll survives navigation: expected {addonOffset}, actual {addonScroll.VerticalOffset}.");
        Check(!shell.AddonsState.Current.IsDetailOpen && !shell.AddonsState.IsLibraryOpen, "Navigation still dismisses transient addon panels.");
        shell.AddonsState.ApplyRuntimeView(shell.AddonsState.Current with { NotificationMessage = "Fixture refresh" });
        await Layout();
        Check(addonList.SelectedItem is AddonUiItem refreshed && refreshed.Id == chosen.Id && Math.Abs(addonScroll.VerticalOffset - addonOffset) < 2,
            "A catalog refresh preserves the useful addon selection and position.");

        FriendsUiState friends = shell.FriendsState;
        FriendUiItem friend = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.Populated).Current.Friends[0];
        friends.ApplyRuntimeView(friends.Current with
        {
            Friends = Enumerable.Range(1, 1000).Select(index => friend with { AccountId = (uint)index, Username = "Friend " + index.ToString("D4"), IsOnline = true }).ToImmutableArray(),
            IncomingRequests = [], OutgoingRequests = [], IsRuntimeConnected = true, LoadState = FriendsViewLoadState.Loaded
        });
        friends.IsOpen = true;
        await Layout();
        ItemsControl friendList = shell.FriendsOverlay.ListHost;
        Check(shell.FriendsOverlay.VisibleRows.Count == 1001 && Realized(friendList) is > 0 and < 100,
            $"1000 friends retain data while only generating viewport rows; actual {Realized(friendList)}.");
        ScrollViewer friendScroll = shell.FriendsOverlay.ScrollHost;
        friendScroll.ScrollToVerticalOffset(10000);
        await Layout();
        double friendOffset = friendScroll.VerticalOffset;
        friends.ApplyRuntimeView(friends.Current with { NoticeMessage = "Fixture background refresh" });
        await Layout();
        Check(Math.Abs(friendScroll.VerticalOffset - friendOffset) < 2, "Refreshing the friend snapshot does not jump the list.");
        friends.IsOpen = false;
        await Layout();

        Check(!shell.IsActive && !shell.IsKeyboardFocusWithin && shell.Left < -10000 && !shell.ShowInTaskbar,
            "Optimization fixtures stay inactive and outside the desktop.");
        friends.IsOpen = true;
        await Layout();
        Check(Math.Abs(friendScroll.VerticalOffset - friendOffset) < 2, "Closing/reopening friends preserves the list position.");
        friendScroll.ScrollToEnd();
        await Layout();
        Check(Realized(friendList) < 100 && friendList.ItemContainerGenerator.ContainerFromIndex(1000) is not null,
            "The last friend remains reachable with a bounded visual tree.");
        friends.IsOpen = false;
        await Layout();

        async Task Navigate(string name) { Invoke((Button)shell.FindName(name), pointer: false); await Layout(); }
        async Task Layout() { await Task.Delay(220); shell.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); shell.UpdateLayout(); }
    }

    private static int Realized(ItemsControl items) => Enumerable.Range(0, items.Items.Count).Count(index => items.ItemContainerGenerator.ContainerFromIndex(index) is not null);
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (T descendant in Visuals<T>(child)) yield return descendant;
        }
    }
}
