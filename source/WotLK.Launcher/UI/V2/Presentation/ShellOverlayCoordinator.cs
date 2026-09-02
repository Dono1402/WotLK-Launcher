namespace WotLK.Launcher.UI.V2.Presentation;

internal enum ShellOverlayKind
{
    None,
    Activity,
    Friends,
    Authentication,
    Profile,
    AvatarCrop
}

internal sealed class ShellOverlayCoordinator
{
    private readonly FriendsUiState _friends;
    private readonly ActivityUiState _activity;
    private readonly AuthUiState _authentication;
    private readonly ProfileUiState _profile;
    private readonly AvatarCropUiState _avatarCrop;

    internal ShellOverlayCoordinator(
        ActivityUiState activity,
        FriendsUiState friends,
        AuthUiState authentication,
        ProfileUiState profile,
        AvatarCropUiState avatarCrop)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _friends = friends ?? throw new ArgumentNullException(nameof(friends));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _avatarCrop = avatarCrop ?? throw new ArgumentNullException(nameof(avatarCrop));
    }

    internal ShellOverlayKind Current => _avatarCrop.IsOpen
        ? ShellOverlayKind.AvatarCrop
        : _authentication.IsOpen
        ? ShellOverlayKind.Authentication
        : _activity.IsOpen
            ? ShellOverlayKind.Activity
            : _friends.IsOpen
            ? ShellOverlayKind.Friends
            : _profile.IsOpen
                ? ShellOverlayKind.Profile
                : ShellOverlayKind.None;

    internal void OpenAuthentication()
    {
        _avatarCrop.IsOpen = false;
        _activity.IsOpen = false;
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
        if (_avatarCrop.IsOpen || _authentication.IsOpen || _profile.Current.IsLoggingOut)
        {
            return false;
        }

        _activity.IsOpen = false;
        _profile.IsOpen = false;
        _friends.IsOpen = !_friends.IsOpen;
        return true;
    }

    internal void CloseFriends()
    {
        _friends.IsOpen = false;
    }

    internal bool TryToggleActivity()
    {
        if (_avatarCrop.IsOpen || _authentication.IsOpen || _profile.Current.IsLoggingOut)
        {
            return false;
        }

        _friends.IsOpen = false;
        _profile.IsOpen = false;
        _activity.IsOpen = !_activity.IsOpen;
        return true;
    }

    internal void CloseActivity()
    {
        _activity.IsOpen = false;
    }

    internal void OpenActivityPreview()
    {
        _avatarCrop.IsOpen = false;
        _authentication.IsOpen = false;
        _friends.IsOpen = false;
        _profile.IsOpen = false;
        _activity.IsOpen = true;
    }

    internal bool TryToggleProfile()
    {
        if (_avatarCrop.IsOpen
            || _authentication.IsOpen
            || !_profile.Current.IsAuthenticated
            || _profile.Current.IsLoggingOut)
        {
            return false;
        }

        _activity.IsOpen = false;
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
        _avatarCrop.IsOpen = false;
        _authentication.IsOpen = false;
        _activity.IsOpen = false;
        _friends.IsOpen = false;
        _profile.IsOpen = true;
    }

    internal bool TryOpenAvatarCrop()
    {
        if (_authentication.IsOpen)
        {
            return false;
        }

        _activity.IsOpen = false;
        _friends.IsOpen = false;
        _profile.IsOpen = false;
        if (_avatarCrop.Current.IsPreview)
        {
            _avatarCrop.Open();
        }
        else if (!_avatarCrop.IsOpen)
        {
            return false;
        }
        return true;
    }

    internal void CloseAvatarCrop()
    {
        if (_avatarCrop.Current.IsPreview
            && _avatarCrop.Current.Status != AvatarCropPreviewStatus.Uploading)
        {
            _avatarCrop.IsOpen = false;
        }
    }
}
