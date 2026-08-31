namespace WotLK.Launcher.UI.V2.Presentation;

internal enum ShellOverlayKind
{
    None,
    Friends,
    Authentication
}

internal sealed class ShellOverlayCoordinator
{
    private readonly FriendsUiState _friends;
    private readonly AuthUiState _authentication;

    internal ShellOverlayCoordinator(FriendsUiState friends, AuthUiState authentication)
    {
        _friends = friends ?? throw new ArgumentNullException(nameof(friends));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    internal ShellOverlayKind Current => _authentication.IsOpen
        ? ShellOverlayKind.Authentication
        : _friends.IsOpen
            ? ShellOverlayKind.Friends
            : ShellOverlayKind.None;

    internal void OpenAuthentication()
    {
        _friends.IsOpen = false;
        _authentication.IsOpen = true;
    }

    internal void CloseAuthentication()
    {
        _authentication.IsOpen = false;
    }

    internal bool TryToggleFriends()
    {
        if (_authentication.IsOpen)
        {
            return false;
        }

        _friends.IsOpen = !_friends.IsOpen;
        return true;
    }

    internal void CloseFriends()
    {
        _friends.IsOpen = false;
    }
}
