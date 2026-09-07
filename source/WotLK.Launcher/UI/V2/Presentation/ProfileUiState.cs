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
    private Runtime.LauncherPresenceSnapshot? _presence;

    public string PresenceLabel => Localization.LauncherLocalization.Text(_presence?.OwnerAccountId is null ? "Hors ligne" : !_presence.IsAvailable
        ? "Statut indisponible" : _presence.IsAutomaticAway ? "Absent · inactivité" : _presence.Status switch
        { "online" => "En ligne", "away" => "Absent", "dnd" => "Ne pas déranger", _ => "Hors ligne" });
    public string PresenceBrush => _presence?.Status switch
        { "online" when _presence.IsAvailable => "#48C78E", "away" when _presence.IsAvailable => "#E9B44C", "dnd" when _presence.IsAvailable => "#EE6873", _ => "#8995A8" };
    public string PresenceError => Localization.LauncherLocalization.Text(_presence?.ErrorCode switch
        { "presence-unavailable" => "Le serveur ne permet pas encore de modifier votre statut.",
          not null => "Impossible de confirmer le statut. Réessayez.", _ => "" });
    public bool CanChangePresence => Current.IsAuthenticated && _presence?.OwnerAccountId is not null && _presence.IsAvailable && !_presence.IsUpdating;
    public string PresenceProgress => _presence?.IsUpdating == true ? Localization.LauncherLocalization.Text("Enregistrement…") : "";
    public bool IsPresenceOnline => _presence?.ManualStatus == "online" && _presence.IsAvailable;
    public bool IsPresenceAway => _presence?.ManualStatus == "away" && _presence.IsAvailable;
    public bool IsPresenceDnd => _presence?.ManualStatus == "dnd" && _presence.IsAvailable;
    public bool IsPresenceOffline => _presence?.ManualStatus == "offline" && _presence.IsAvailable;

    internal void ApplyPresence(Runtime.LauncherPresenceSnapshot snapshot)
    {
        if(_presence is not null && snapshot.Sequence < _presence.Sequence)return;
        _presence=snapshot;RaisePropertyChanged(string.Empty);
    }

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
            _presence = null;
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
