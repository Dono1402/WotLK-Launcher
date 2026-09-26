using System.Windows;
using WotLK.Launcher.UI.V2.Views;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private void FriendsDrawer_PublicProfileRequested(object? sender, FriendPublicProfileRequestedEventArgs e)
    {
        if (!ArmoryView.IsConfigured || !IsAccountNavigationEnabled) return;
        FriendUiItem? friend = FriendsState.Current.Friends.FirstOrDefault(item => item.AccountId == e.AccountId);
        if (friend is null) return;
        if (CurrentPage != LauncherShellPage.Armory || ArmoryView.FriendAccountId is null)
            _friendProfileOrigin = CaptureNavigationOrigin();
        SuppressFriendsFocusRestore();
        _overlayCoordinator.CloseFriends();
        ArmoryView.ShowFriendProfile(friend);
        NavigateTo(LauncherShellPage.Armory);
    }

    private void RefreshOpenFriendProfile()
    {
        if (FriendsState.Current.LoadState is FriendsViewLoadState.Loaded or FriendsViewLoadState.SignedOut)
            ArmoryView.RetainFriendCaches(FriendsState.Current.Friends.Select(friend => friend.AccountId).ToHashSet());
        if (ArmoryView.FriendAccountId is not uint accountId) return;
        FriendUiItem? friend = FriendsState.Current.Friends.FirstOrDefault(item => item.AccountId == accountId);
        if (friend is not null) ArmoryView.UpdateFriendProfile(friend);
        else if (FriendsState.Current.LoadState is FriendsViewLoadState.Loaded or FriendsViewLoadState.SignedOut)
        {
            ArmoryView.ForgetFriendCache(accountId);
            if (CurrentPage == LauncherShellPage.Armory) ReturnFromFriendProfile();
            else ArmoryView.ShowOwnProfile();
        }
    }

    private void ArmoryView_FriendsBackRequested(object? sender, EventArgs e)
    {
        ReturnFromFriendProfile();
        if (!FriendsState.IsOpen) FriendsButton_Click(FriendsButton, new RoutedEventArgs());
    }

    private void ReturnFromFriendProfile()
    {
        NavigationOrigin origin = _friendProfileOrigin ?? new(ShellState.SelectedService, LauncherShellPage.Game, false);
        _friendProfileOrigin = null;
        // Clear the friend context only after navigating away: a WotLK personal
        // profile must not be exposed under the Minecraft selection.
        RestoreNavigationOrigin(origin);
        ArmoryView.ShowOwnProfile();
    }

    private void ArmoryView_BackRequested(object? sender, EventArgs e)
    {
        bool friend = ArmoryView.FriendAccountId is not null;
        ReturnFromFriendProfile();
        if (friend && !FriendsState.IsOpen) FriendsButton_Click(FriendsButton, new RoutedEventArgs());
    }

    private void ArmoryView_FriendMessageRequested(object? sender, ChatConversationRequestedEventArgs e)
        => FriendsDrawer_MessageRequested(sender, new FriendPublicProfileRequestedEventArgs(e.AccountId, e.Username));
}
