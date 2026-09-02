using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class AccountStateAdapter : IDisposable
{
    internal const int ChromeAvatarPixelSize = 64;
    internal const int AccountAvatarPixelSize = 256;
    private readonly AccountUiState _target;
    private readonly AvatarCropUiState _crop;
    private readonly ShellUiState _shell;
    private readonly ProfileUiState _profile;
    private readonly LauncherAccountCoordinator _runtime;
    private readonly AvatarImageCache _imageCache;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private long _latestSequence = -1;
    private long _imageLoadGeneration;
    private int _disposeState;

    internal AccountStateAdapter(
        AccountUiState target,
        AvatarCropUiState crop,
        ShellUiState shell,
        ProfileUiState profile,
        LauncherAccountCoordinator runtime,
        AvatarImageCache imageCache,
        Dispatcher dispatcher)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _crop = crop ?? throw new ArgumentNullException(nameof(crop));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
        ApplyOrQueue(_runtime.CurrentSnapshot);
    }

    internal static AccountViewState Project(
        AccountRuntimeSnapshot snapshot,
        BitmapSource? avatarImage)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AvatarPreviewOperation operation = snapshot.AvatarOperation switch
        {
            AccountAvatarOperationState.Preparing => AvatarPreviewOperation.Preparing,
            AccountAvatarOperationState.Uploading => AvatarPreviewOperation.Uploading,
            AccountAvatarOperationState.Processing => AvatarPreviewOperation.Processing,
            AccountAvatarOperationState.Reconciling => AvatarPreviewOperation.Reconciling,
            AccountAvatarOperationState.Removing => AvatarPreviewOperation.Removing,
            _ => AvatarPreviewOperation.None
        };
        bool available = snapshot.AvatarAvailability == AvatarBackendAvailability.Available;
        bool checking = snapshot.AvatarAvailability == AvatarBackendAvailability.Unknown
            || snapshot.LoadingState == AccountLoadingState.Loading;
        string availabilityMessage = snapshot.AvatarAvailability switch
        {
            AvatarBackendAvailability.Unavailable =>
                "Les photos de profil ne sont pas encore disponibles sur ce serveur.",
            AvatarBackendAvailability.Unknown when snapshot.IsAuthenticated =>
                "Vérification de la disponibilité des photos…",
            _ => string.Empty
        };
        string status = snapshot.AvatarOperation switch
        {
            AccountAvatarOperationState.Preparing => "Préparation de la photo…",
            AccountAvatarOperationState.Uploading when snapshot.UploadPercentage is int percentage =>
                $"Envoi de la photo… {percentage} %",
            AccountAvatarOperationState.Uploading => "Envoi de la photo…",
            AccountAvatarOperationState.Processing => "Traitement par Atlas…",
            AccountAvatarOperationState.Reconciling => "Actualisation du profil…",
            AccountAvatarOperationState.Removing => "Suppression de la photo en cours…",
            _ => string.Empty
        };
        bool canMutate = snapshot.IsAuthenticated
            && available
            && snapshot.LoadingState != AccountLoadingState.Loading
            && snapshot.AvatarOperation == AccountAvatarOperationState.None;
        return new AccountViewState(
            IsPreview: false,
            IsRuntimeConnected: true,
            SelectedSection: AccountSection.Profile,
            Username: snapshot.Username,
            Email: snapshot.Email,
            Initial: snapshot.DisplayInitial,
            IsEmailVerified: snapshot.EmailVerified,
            HasProfileAvatar: snapshot.Avatar is not null && avatarImage is not null,
            AvatarImage: avatarImage,
            AvatarOperation: operation,
            AvatarStatusMessage: status,
            AvatarErrorMessage: MapError(snapshot.ErrorCategory),
            IsAvatarBackendAvailable: available,
            IsAvatarBackendChecking: checking,
            AvatarAvailabilityMessage: availabilityMessage,
            CanModifyAvatar: canMutate,
            CanRemoveAvatar: canMutate && snapshot.Avatar is not null,
            IsDeleteConfirmationOpen: false,
            MemberSince: snapshot.IsAuthenticated ? "Profil synchronisé avec Atlas" : string.Empty,
            LastPasswordChange: "À venir",
            ActiveSessionCount: 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
    }

    private void Runtime_SnapshotChanged(object? sender, AccountRuntimeSnapshotEventArgs e)
    {
        ApplyOrQueue(e.Snapshot);
    }

    private void ApplyOrQueue(AccountRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Apply(snapshot);
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => Apply(snapshot)));
    }

    private void Apply(AccountRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0 || snapshot.Sequence <= _latestSequence)
        {
            return;
        }

        _latestSequence = snapshot.Sequence;
        long imageGeneration = ++_imageLoadGeneration;
        BitmapSource? chromeImage = null;
        BitmapSource? accountImage = null;
        if (snapshot.Avatar is not null)
        {
            _imageCache.TryGetMemory(
                snapshot.Avatar,
                ChromeAvatarPixelSize,
                out chromeImage);
            _imageCache.TryGetMemory(
                snapshot.Avatar,
                AccountAvatarPixelSize,
                out accountImage);
        }

        ApplyAvatarProjection(snapshot, chromeImage, accountImage);
        ApplyCropOperation(snapshot);
        if (snapshot.Avatar is not null && chromeImage is null)
        {
            _ = LoadAvatarObservedAsync(
                snapshot,
                imageGeneration,
                ChromeAvatarPixelSize,
                AvatarProjectionTarget.Chrome);
        }
        if (snapshot.Avatar is not null && accountImage is null)
        {
            _ = LoadAvatarObservedAsync(
                snapshot,
                imageGeneration,
                AccountAvatarPixelSize,
                AvatarProjectionTarget.Account);
        }
    }

    private async Task LoadAvatarObservedAsync(
        AccountRuntimeSnapshot snapshot,
        long generation,
        int pixelSize,
        AvatarProjectionTarget target)
    {
        BitmapSource? image;
        try
        {
            image = await _imageCache.GetAsync(
                snapshot.Avatar!,
                pixelSize,
                _disposeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            image = null;
        }

        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentProjection(snapshot, generation))
                {
                    return;
                }

                if (target == AvatarProjectionTarget.Chrome)
                {
                    _shell.ApplyProfileAvatar(image);
                    _profile.ApplyAvatarImage(image);
                }
                else
                {
                    _target.ApplyAvatarImage(image, descriptorPresent: true);
                }
            }, DispatcherPriority.DataBind);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void ApplyAvatarProjection(
        AccountRuntimeSnapshot snapshot,
        BitmapSource? chromeImage,
        BitmapSource? accountImage)
    {
        _shell.ApplyProfileAvatar(chromeImage);
        _profile.ApplyAvatarImage(chromeImage);
        _target.ApplyRuntimeView(Project(snapshot, accountImage));
    }

    private bool IsCurrentProjection(AccountRuntimeSnapshot source, long generation)
    {
        if (Volatile.Read(ref _disposeState) != 0
            || generation != _imageLoadGeneration
            || source.Sequence != _latestSequence
            || source.Avatar is null)
        {
            return false;
        }

        AccountRuntimeSnapshot current = _runtime.CurrentSnapshot;
        return current.IsAuthenticated
            && current.Sequence == source.Sequence
            && string.Equals(current.Username, source.Username, StringComparison.OrdinalIgnoreCase)
            && current.Avatar is { } avatar
            && avatar.AvatarId == source.Avatar.AvatarId
            && avatar.Version == source.Avatar.Version;
    }

    private void ApplyCropOperation(AccountRuntimeSnapshot snapshot)
    {
        AvatarCropPreviewStatus status = snapshot.AvatarOperation switch
        {
            AccountAvatarOperationState.Preparing => AvatarCropPreviewStatus.Preparing,
            AccountAvatarOperationState.Uploading => AvatarCropPreviewStatus.Uploading,
            AccountAvatarOperationState.Processing => AvatarCropPreviewStatus.Processing,
            AccountAvatarOperationState.Reconciling => AvatarCropPreviewStatus.Reconciling,
            _ when snapshot.ErrorCategory != AccountAvatarErrorCategory.None =>
                AvatarCropPreviewStatus.Error,
            _ => AvatarCropPreviewStatus.Idle
        };
        string message = snapshot.AvatarOperation switch
        {
            AccountAvatarOperationState.Preparing => "Préparation de la photo…",
            AccountAvatarOperationState.Uploading when snapshot.UploadPercentage is int percentage =>
                $"Envoi… {percentage} %",
            AccountAvatarOperationState.Uploading => "Envoi…",
            AccountAvatarOperationState.Processing => "Traitement par Atlas…",
            AccountAvatarOperationState.Reconciling => "Actualisation du profil…",
            _ => string.Empty
        };
        bool indeterminate = snapshot.AvatarOperation is AccountAvatarOperationState.Preparing
            or AccountAvatarOperationState.Processing
            or AccountAvatarOperationState.Reconciling;
        _crop.ApplyRuntimeOperation(
            status,
            message,
            snapshot.AvatarOperation == AccountAvatarOperationState.Uploading
                ? snapshot.UploadPercentage
                : null,
            indeterminate,
            MapError(snapshot.ErrorCategory));
    }

    internal static string MapError(AccountAvatarErrorCategory category)
    {
        return category switch
        {
            AccountAvatarErrorCategory.None => string.Empty,
            AccountAvatarErrorCategory.InvalidImage => "Cette image ne peut pas être utilisée.",
            AccountAvatarErrorCategory.AvatarTooLarge => "L’image dépasse la limite de 8 Mo.",
            AccountAvatarErrorCategory.UnsupportedFormat => "Utilise une image JPEG, PNG ou WebP.",
            AccountAvatarErrorCategory.InvalidDimensions => "Les dimensions de cette image ne sont pas compatibles.",
            AccountAvatarErrorCategory.InvalidCrop => "Le recadrage de cette image n’est pas valide.",
            AccountAvatarErrorCategory.RateLimited => "Trop de modifications récentes. Réessaie plus tard.",
            AccountAvatarErrorCategory.UploadInProgress => "Une modification de photo est déjà en cours.",
            AccountAvatarErrorCategory.ProcessingFailed => "Atlas n’a pas pu traiter cette photo.",
            AccountAvatarErrorCategory.StorageFailed => "Le stockage des photos est temporairement indisponible.",
            AccountAvatarErrorCategory.Unauthorized => "Ta session Atlas doit être renouvelée.",
            AccountAvatarErrorCategory.BackendUnavailable =>
                "Les photos de profil ne sont pas encore disponibles sur ce serveur.",
            AccountAvatarErrorCategory.Network => "Atlas est temporairement inaccessible.",
            AccountAvatarErrorCategory.CancellationAmbiguous =>
                "L’état de la photo n’a pas pu être confirmé. Actualise le profil.",
            _ => "La photo n’a pas pu être modifiée. Réessaie."
        };
    }

    private enum AvatarProjectionTarget
    {
        Chrome,
        Account
    }
}
