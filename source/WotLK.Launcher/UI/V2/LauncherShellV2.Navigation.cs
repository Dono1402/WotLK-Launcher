using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private bool _suppressActivityFocusRestore;

    private void UpdateBackgroundCadence(object? sender, EventArgs e) =>
        _friendsCommands?.SetForeground(IsVisible && IsActive && WindowState != WindowState.Minimized);

    private void ShellVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateBackgroundCadence(sender, EventArgs.Empty);

    private bool IsShellNavigationTarget(DependencyObject? target) =>
        FindAncestor<Button>(target) is { IsEnabled: true, IsVisible: true } button
        && TitleBar.IsAncestorOf(button);

    private Button? CurrentPanelButton => _overlayCoordinator.Current switch
    {
        ShellOverlayKind.Friends => FriendsButton,
        ShellOverlayKind.Activity => ActivityButton,
        ShellOverlayKind.Profile => ProfileButton,
        _ => null
    };

    private bool IsCurrentPanelTarget(DependencyObject? target) => _overlayCoordinator.Current switch
    {
        ShellOverlayKind.Friends => ReferenceEquals(FindAncestor<FriendsDrawerV2>(target), FriendsDrawer)
            || FriendsDrawer.ContainsKeyboardFocusTarget(target),
        ShellOverlayKind.Activity => ReferenceEquals(FindAncestor<ActivityCenterPanelV2>(target), ActivityCenter),
        ShellOverlayKind.PatchNote => ReferenceEquals(FindAncestor<PatchNoteOverlayV2>(target), PatchNoteOverlay),
        ShellOverlayKind.Profile => ProfileMenu.ContainsTarget(target),
        _ => true
    };

    private void PreparePanelTransition(ShellOverlayKind next)
    {
        // Closing animations finish after navigation. They must not restore focus
        // to the old opener, or a queued callback can interrupt the new page/panel.
        if (FriendsState.IsOpen && next != ShellOverlayKind.Friends)
            SuppressFriendsFocusRestore();
        if (ActivityState.IsOpen && next != ShellOverlayKind.Activity)
        {
            _suppressActivityFocusRestore = true;
            ActivityButton.Focusable = true;
        }
        if (ProfileState.IsOpen && next != ShellOverlayKind.Profile)
        {
            _suppressProfileFocusRestore = true;
            ProfileButton.Focusable = true;
        }
        if (PatchNoteState.IsOpen && next != ShellOverlayKind.PatchNote)
            _patchNoteFocusReturnTarget = null;
    }

    private void DismissNavigationPanels()
    {
        if (!_overlayCoordinator.CanNavigate) return;
        PreparePanelTransition(ShellOverlayKind.None);
        _overlayCoordinator.CloseNavigationPanels();
    }
}
