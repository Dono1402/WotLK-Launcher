using System.Windows.Media.Imaging;
using WotLK.Launcher.Account;

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
    Preparing,
    Uploading,
    Processing,
    Reconciling,
    Removing
}

public sealed record AccountViewState(
    bool IsPreview,
    bool IsRuntimeConnected,
    AccountSection SelectedSection,
    string Username,
    string Email,
    string Initial,
    bool IsEmailVerified,
    bool HasProfileAvatar,
    BitmapSource? AvatarImage,
    AvatarPreviewOperation AvatarOperation,
    string AvatarStatusMessage,
    string AvatarErrorMessage,
    bool IsAvatarBackendAvailable,
    bool IsAvatarBackendChecking,
    string AvatarAvailabilityMessage,
    bool CanModifyAvatar,
    bool CanRemoveAvatar,
    bool IsDeleteConfirmationOpen,
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
        IsRuntimeConnected: false,
        SelectedSection: AccountSection.Profile,
        Username: string.Empty,
        Email: string.Empty,
        Initial: "?",
        IsEmailVerified: false,
        HasProfileAvatar: false,
        AvatarImage: null,
        AvatarOperation: AvatarPreviewOperation.None,
        AvatarStatusMessage: string.Empty,
        AvatarErrorMessage: string.Empty,
        IsAvatarBackendAvailable: false,
        IsAvatarBackendChecking: false,
        AvatarAvailabilityMessage: string.Empty,
        CanModifyAvatar: false,
        CanRemoveAvatar: false,
        IsDeleteConfirmationOpen: false,
        MemberSince: string.Empty,
        LastPasswordChange: "À venir",
        ActiveSessionCount: 0));

    public AccountViewState Current => _current;

    internal void SelectSection(AccountSection section)
    {
        if (_current.SelectedSection != section)
        {
            Apply(_current with { SelectedSection = section });
        }
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
            AvatarStatusMessage = "Suppression de la photo en cours…",
            CanModifyAvatar = false,
            CanRemoveAvatar = false,
            IsDeleteConfirmationOpen = false
        });
    }

    internal void ShowDeleteConfirmation()
    {
        if (_current.CanRemoveAvatar && !_current.IsDeleteConfirmationOpen)
        {
            Apply(_current with { IsDeleteConfirmationOpen = true });
        }
    }

    internal void CloseDeleteConfirmation()
    {
        if (_current.IsDeleteConfirmationOpen)
        {
            Apply(_current with { IsDeleteConfirmationOpen = false });
        }
    }

    internal void ApplyRuntimeView(AccountViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Apply(state with
        {
            SelectedSection = _current.SelectedSection,
            IsDeleteConfirmationOpen = _current.IsDeleteConfirmationOpen
                && state.CanRemoveAvatar
        });
    }

    internal void ApplyAvatarImage(BitmapSource? image, bool descriptorPresent)
    {
        Apply(_current with
        {
            AvatarImage = image,
            HasProfileAvatar = descriptorPresent && image is not null,
            CanRemoveAvatar = descriptorPresent
                && _current.IsAvatarBackendAvailable
                && _current.AvatarOperation == AvatarPreviewOperation.None
        });
    }

    internal void ShowLocalError(string message)
    {
        Apply(_current with { AvatarErrorMessage = message });
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
    Preparing,
    Uploading,
    Processing,
    Cancelling,
    Reconciling,
    Error
}

public sealed record AvatarCropViewState(
    bool IsPreview,
    bool IsOpen,
    AvatarCropPreviewStatus Status,
    BitmapSource? AvatarImage,
    string ErrorMessage,
    string StatusMessage,
    int? UploadPercentage,
    bool IsProgressIndeterminate,
    double Zoom,
    double OffsetX,
    double OffsetY,
    int OrientedPixelWidth,
    int OrientedPixelHeight,
    double MaximumZoom)
{
    internal AvatarCropLayout Layout => AvatarCropGeometry.Calculate(
        Math.Max(1, OrientedPixelWidth),
        Math.Max(1, OrientedPixelHeight),
        Zoom,
        OffsetX,
        OffsetY);
}

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
        AvatarImage: null,
        ErrorMessage: string.Empty,
        StatusMessage: string.Empty,
        UploadPercentage: null,
        IsProgressIndeterminate: false,
        Zoom: 1,
        OffsetX: 0,
        OffsetY: 0,
        OrientedPixelWidth: 1,
        OrientedPixelHeight: 1,
        MaximumZoom: 1));

    public AvatarCropViewState Current => _current;

    public bool IsOpen
    {
        get => _current.IsOpen;
        set
        {
            if (_current.IsPreview && _current.IsOpen != value)
            {
                Apply(_current with { IsOpen = value });
            }
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

    internal void OpenReal(AvatarPreviewImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Apply(new AvatarCropViewState(
            IsPreview: false,
            IsOpen: true,
            Status: AvatarCropPreviewStatus.Idle,
            AvatarImage: image.OrientedImage,
            ErrorMessage: string.Empty,
            StatusMessage: string.Empty,
            UploadPercentage: null,
            IsProgressIndeterminate: false,
            Zoom: 1,
            OffsetX: 0,
            OffsetY: 0,
            OrientedPixelWidth: image.OrientedPixelWidth,
            OrientedPixelHeight: image.OrientedPixelHeight,
            MaximumZoom: AvatarCropGeometry.GetMaximumZoom(
                image.OrientedPixelWidth,
                image.OrientedPixelHeight)));
    }

    internal void CloseReal()
    {
        if (!_current.IsPreview)
        {
            Apply(Empty.Current);
        }
    }

    internal void SetTransform(double zoom, double offsetX, double offsetY)
    {
        AvatarCropLayout layout = AvatarCropGeometry.Calculate(
            Math.Max(1, _current.OrientedPixelWidth),
            Math.Max(1, _current.OrientedPixelHeight),
            zoom,
            offsetX,
            offsetY);
        Apply(_current with
        {
            Zoom = layout.Zoom,
            OffsetX = layout.OffsetX,
            OffsetY = layout.OffsetY
        });
    }

    internal void StartUploadPreview()
    {
        if (_current.IsPreview && _current.Status != AvatarCropPreviewStatus.Uploading)
        {
            Apply(_current with
            {
                Status = AvatarCropPreviewStatus.Uploading,
                ErrorMessage = string.Empty,
                StatusMessage = "Envoi…",
                UploadPercentage = 48,
                IsProgressIndeterminate = false
            });
        }
    }

    internal void ApplyRuntimeOperation(
        AvatarCropPreviewStatus status,
        string statusMessage,
        int? uploadPercentage,
        bool isIndeterminate,
        string errorMessage)
    {
        if (_current.IsPreview || !_current.IsOpen)
        {
            return;
        }

        Apply(_current with
        {
            Status = status,
            StatusMessage = statusMessage,
            UploadPercentage = uploadPercentage,
            IsProgressIndeterminate = isIndeterminate,
            ErrorMessage = errorMessage
        });
    }

    internal void ShowSelectionError(string message)
    {
        if (!_current.IsPreview)
        {
            Apply(_current with
            {
                Status = AvatarCropPreviewStatus.Error,
                ErrorMessage = message,
                StatusMessage = string.Empty,
                UploadPercentage = null,
                IsProgressIndeterminate = false
            });
        }
    }

    private void Apply(AvatarCropViewState state)
    {
        _current = state;
        RaisePropertyChanged(string.Empty);
    }
}
