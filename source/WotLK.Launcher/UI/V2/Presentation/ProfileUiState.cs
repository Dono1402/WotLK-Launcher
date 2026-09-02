using System.Windows.Input;
using System.Windows.Media.Imaging;
using WotLK.Launcher.UI.V2.Commands;

namespace WotLK.Launcher.UI.V2.Presentation;

public sealed record ProfileViewState(
    bool IsAuthenticated,
    bool IsLoggingOut,
    string Username,
    string Initial,
    bool IsEmailVerified,
    string EmailStatusText,
    bool CanLogout,
    string LogoutLabel,
    string LogoutToolTip,
    string ErrorMessage)
{
    internal static ProfileViewState SignedOut { get; } = new(
        IsAuthenticated: false,
        IsLoggingOut: false,
        Username: string.Empty,
        Initial: "?",
        IsEmailVerified: true,
        EmailStatusText: string.Empty,
        CanLogout: false,
        LogoutLabel: "Déconnexion",
        LogoutToolTip: "Aucune session active.",
        ErrorMessage: string.Empty);
}

public sealed class ProfileUiState : BindableUiState
{
    private ProfileViewState _current = ProfileViewState.SignedOut;
    private bool _isOpen;
    private BitmapSource? _avatarImage;

    public ProfileViewState Current => _current;

    public BitmapSource? AvatarImage => _avatarImage;

    public bool HasAvatar => _avatarImage is not null;

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public ICommand LogoutCommand { get; private set; } = DisabledCommand.Instance;

    internal void ApplyView(ProfileViewState state)
    {
        _current = state ?? throw new ArgumentNullException(nameof(state));
        if (!state.IsAuthenticated)
        {
            _isOpen = false;
            _avatarImage = null;
        }

        RaisePropertyChanged(string.Empty);
    }

    internal void ApplyAvatarImage(BitmapSource? image)
    {
        if (ReferenceEquals(_avatarImage, image))
        {
            return;
        }

        _avatarImage = image;
        RaisePropertyChanged(nameof(AvatarImage));
        RaisePropertyChanged(nameof(HasAvatar));
    }

    internal void ApplyAccountIdentity(string username, bool isEmailVerified)
    {
        if (!_current.IsAuthenticated)
        {
            return;
        }

        _current = _current with
        {
            Username = username,
            Initial = string.IsNullOrWhiteSpace(username)
                ? "?"
                : username[..1].ToUpperInvariant(),
            IsEmailVerified = isEmailVerified,
            EmailStatusText = isEmailVerified
                ? "Adresse e-mail vérifiée"
                : "Adresse e-mail non vérifiée"
        };
        RaisePropertyChanged(string.Empty);
    }

    internal void AttachLogoutCommand(ICommand command)
    {
        LogoutCommand = command ?? throw new ArgumentNullException(nameof(command));
        RaisePropertyChanged(nameof(LogoutCommand));
    }
}
