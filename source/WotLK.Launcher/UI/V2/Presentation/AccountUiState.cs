namespace WotLK.Launcher.UI.V2.Presentation;

public enum AccountSection
{
    Profile,
    Security,
    Sessions
}

public enum AvatarPreviewOperation
{
    None,
    Removing
}

public sealed record AccountViewState(
    bool IsPreview,
    AccountSection SelectedSection,
    string Username,
    string Email,
    string Initial,
    bool IsEmailVerified,
    bool HasProfileAvatar,
    string AvatarImageUri,
    AvatarPreviewOperation AvatarOperation,
    string AvatarStatusMessage,
    string MemberSince,
    string LastPasswordChange,
    int ActiveSessionCount);

public sealed class AccountUiState : BindableUiState
{
    private AccountViewState _current;

    internal AccountUiState(AccountViewState current)
    {
        _current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public static AccountUiState Empty { get; } = new(new AccountViewState(
        IsPreview: false,
        SelectedSection: AccountSection.Profile,
        Username: string.Empty,
        Email: string.Empty,
        Initial: "?",
        IsEmailVerified: false,
        HasProfileAvatar: false,
        AvatarImageUri: string.Empty,
        AvatarOperation: AvatarPreviewOperation.None,
        AvatarStatusMessage: string.Empty,
        MemberSince: string.Empty,
        LastPasswordChange: string.Empty,
        ActiveSessionCount: 0));

    public AccountViewState Current => _current;

    internal void SelectSection(AccountSection section)
    {
        if (!_current.IsPreview || _current.SelectedSection == section)
        {
            return;
        }

        Apply(_current with { SelectedSection = section });
    }

    internal void StartRemovingPreview()
    {
        if (!_current.IsPreview || !_current.HasProfileAvatar)
        {
            return;
        }

        Apply(_current with
        {
            AvatarOperation = AvatarPreviewOperation.Removing,
            AvatarStatusMessage = "Suppression de la photo en cours…"
        });
    }

    private void Apply(AccountViewState state)
    {
        _current = state;
        RaisePropertyChanged(string.Empty);
    }
}

public enum AvatarCropPreviewStatus
{
    Idle,
    Uploading,
    Error
}

public sealed record AvatarCropViewState(
    bool IsPreview,
    bool IsOpen,
    AvatarCropPreviewStatus Status,
    string AvatarImageUri,
    string ErrorMessage,
    double Zoom,
    double OffsetX,
    double OffsetY);

public sealed class AvatarCropUiState : BindableUiState
{
    private AvatarCropViewState _current;

    internal AvatarCropUiState(AvatarCropViewState current)
    {
        _current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public static AvatarCropUiState Empty { get; } = new(new AvatarCropViewState(
        IsPreview: false,
        IsOpen: false,
        Status: AvatarCropPreviewStatus.Idle,
        AvatarImageUri: string.Empty,
        ErrorMessage: string.Empty,
        Zoom: 1,
        OffsetX: 0,
        OffsetY: 0));

    public AvatarCropViewState Current => _current;

    public bool IsOpen
    {
        get => _current.IsOpen;
        set
        {
            if (!_current.IsPreview || _current.IsOpen == value)
            {
                return;
            }

            Apply(_current with { IsOpen = value });
        }
    }

    internal void Open()
    {
        if (_current.IsPreview)
        {
            Apply(_current with
            {
                IsOpen = true,
                Status = AvatarCropPreviewStatus.Idle,
                ErrorMessage = string.Empty
            });
        }
    }

    internal void SetTransform(double zoom, double offsetX, double offsetY)
    {
        if (_current.IsPreview)
        {
            Apply(_current with
            {
                Zoom = Math.Clamp(zoom, 1, 2.4),
                OffsetX = offsetX,
                OffsetY = offsetY
            });
        }
    }

    internal void StartUploadPreview()
    {
        if (_current.IsPreview && _current.Status != AvatarCropPreviewStatus.Uploading)
        {
            Apply(_current with
            {
                Status = AvatarCropPreviewStatus.Uploading,
                ErrorMessage = string.Empty
            });
        }
    }

    private void Apply(AvatarCropViewState state)
    {
        _current = state;
        RaisePropertyChanged(string.Empty);
    }
}
