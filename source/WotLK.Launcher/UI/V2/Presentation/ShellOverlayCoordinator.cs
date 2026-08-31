namespace WotLK.Launcher.UI.V2.Presentation;

internal enum ShellOverlayKind
{
    None,
    Friends,
    Authentication,
    Profile
}

internal sealed class ShellOverlayCoordinator
{
    private readonly FriendsUiState _friends;
    private readonly AuthUiState _authentication;
    private readonly ProfileUiState _profile;

    internal ShellOverlayCoordinator(
        FriendsUiState friends,
        AuthUiState authentication,
        ProfileUiState profile)
    {
        _friends = friends ?? throw new ArgumentNullException(nameof(friends));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    internal ShellOverlayKind Current => _authentication.IsOpen
        ? ShellOverlayKind.Authentication
        : _friends.IsOpen
            ? ShellOverlayKind.Friends
            : _profile.IsOpen
                ? ShellOverlayKind.Profile
                : ShellOverlayKind.None;

    internal void OpenAuthentication()
    {
        _friends.IsOpen = false;
        _profile.IsOpen = false;
        _authentication.IsOpen = true;
    }

    internal void CloseAuthentication()
    {
        _authentication.IsOpen = false;
    }

    internal bool TryToggleFriends()
    {
        if (_authentication.IsOpen || _profile.Current.IsLoggingOut)
        {
            return false;
        }

        _profile.IsOpen = false;
        _friends.IsOpen = !_friends.IsOpen;
        return true;
    }

    internal void CloseFriends()
    {
        _friends.IsOpen = false;
    }

    internal bool TryToggleProfile()
    {
        if (_authentication.IsOpen
            || !_profile.Current.IsAuthenticated
            || _profile.Current.IsLoggingOut)
        {
            return false;
        }

        _friends.IsOpen = false;
        _profile.IsOpen = !_profile.IsOpen;
        return true;
    }

    internal void CloseProfile()
    {
        if (!_profile.Current.IsLoggingOut)
        {
            _profile.IsOpen = false;
        }
    }

    internal void OpenProfilePreview()
    {
        _authentication.IsOpen = false;
        _friends.IsOpen = false;
        _profile.IsOpen = true;
    }
}
