using System.Windows.Threading;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class AccountCommands : IDisposable
{
    private readonly LauncherAccountCoordinator _runtime;
    private readonly AccountUiState _accountState;
    private readonly AvatarCropUiState _cropState;
    private readonly AvatarFileSelectionService _selectionService;
    private readonly Dispatcher _dispatcher;
    private AvatarPreviewImage? _selection;
    private Task _activeTask = Task.CompletedTask;
    private int _disposeState;

    internal AccountCommands(
        LauncherAccountCoordinator runtime,
        AccountUiState accountState,
        AvatarCropUiState cropState,
        AvatarFileSelectionService selectionService,
        Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _accountState = accountState ?? throw new ArgumentNullException(nameof(accountState));
        _cropState = cropState ?? throw new ArgumentNullException(nameof(cropState));
        _selectionService = selectionService ?? throw new ArgumentNullException(nameof(selectionService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    internal void RefreshProfile()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        AccountActionStartResult start = _runtime.TryRefresh();
        if (start.IsStarted && start.Completion is not null)
        {
            _activeTask = ObserveSilentlyAsync(start.Completion);
        }
    }

    internal bool TryChangeEmail(string email)
    {
        return StartAccountAction(
            () => _runtime.TryChangeEmail(email),
            "L’adresse e-mail ne peut pas être modifiée pour le moment.");
    }

    internal bool TryResendVerification()
    {
        return StartAccountAction(
            _runtime.TryResendVerification,
            "Le message de validation ne peut pas être envoyé pour le moment.");
    }

    internal bool TryChangePassword(
        string currentPassword,
        string newPassword,
        string confirmation)
    {
        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            _accountState.ShowAccountLocalError("Les deux nouveaux mots de passe ne correspondent pas.");
            return false;
        }

        return StartAccountAction(
            () => _runtime.TryChangePassword(currentPassword, newPassword),
            "Le mot de passe ne peut pas être modifié pour le moment.");
    }

    internal bool TryRevokeSession(string sessionId)
    {
        return StartAccountAction(
            () => _runtime.TryRevokeSession(sessionId),
            "Cette session ne peut pas être déconnectée pour le moment.");
    }

    internal async Task<bool> SelectAvatarAsync()
    {
        if (Volatile.Read(ref _disposeState) != 0
            || !_accountState.Current.CanModifyAvatar)
        {
            return false;
        }

        try
        {
            AvatarPreviewImage? selection = await _selectionService
                .PickAndLoadAsync(CancellationToken.None);
            if (selection is null || Volatile.Read(ref _disposeState) != 0)
            {
                return false;
            }

            _selection = selection;
            _cropState.OpenReal(selection);
            return true;
        }
        catch (AvatarSelectionException exception)
        {
            _accountState.ShowLocalError(MapSelectionFailure(exception.Category));
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal bool TryStartUpload()
    {
        if (Volatile.Read(ref _disposeState) != 0
            || _selection is null)
        {
            _cropState.ShowSelectionError("Sélectionne de nouveau la photo avant de l’utiliser.");
            return false;
        }

        AvatarNormalizedCrop crop = _cropState.Current.Layout.Crop;
        AccountActionStartResult start = _runtime.TryUpload(new AvatarUploadRequest(
            _selection.OriginalBytes,
            _selection.ContentType,
            crop));
        if (!start.IsStarted || start.Completion is null)
        {
            _cropState.ShowSelectionError(MapStartFailure(start.Status));
            return false;
        }

        _activeTask = ObserveUploadAsync(start.Completion);
        return true;
    }

    internal bool CancelUploadOrCloseCrop()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        AccountAvatarOperationState operation = _runtime.CurrentSnapshot.AvatarOperation;
        if (operation is AccountAvatarOperationState.Preparing
            or AccountAvatarOperationState.Uploading
            or AccountAvatarOperationState.Processing)
        {
            if (_runtime.CancelUploadFromUser())
            {
                _cropState.ApplyRuntimeOperation(
                    AvatarCropPreviewStatus.Cancelling,
                    "Annulation de l’envoi…",
                    null,
                    isIndeterminate: true,
                    errorMessage: string.Empty);
            }
            return false;
        }
        if (operation == AccountAvatarOperationState.Reconciling)
        {
            return false;
        }

        ClearSelectionAndClose();
        return true;
    }

    internal void ShowDeleteConfirmation()
    {
        _accountState.ShowDeleteConfirmation();
    }

    internal void CancelDeleteConfirmation()
    {
        _accountState.CloseDeleteConfirmation();
    }

    internal bool ConfirmDelete()
    {
        if (Volatile.Read(ref _disposeState) != 0 || !_activeTask.IsCompleted)
        {
            return false;
        }

        AccountActionStartResult start = _runtime.TryDelete();
        if (!start.IsStarted || start.Completion is null)
        {
            _accountState.ShowLocalError(MapStartFailure(start.Status));
            return false;
        }

        _accountState.CloseDeleteConfirmation();
        _activeTask = ObserveDeleteAsync(start.Completion);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _selection = null;
        }
    }

    private bool StartAccountAction(
        Func<AccountActionStartResult> startAction,
        string fallbackMessage)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        AccountActionStartResult start = startAction();
        if (!start.IsStarted || start.Completion is null)
        {
            if (start.Status != AccountActionStartStatus.InvalidRequest)
            {
                _accountState.ShowAccountLocalError(MapAccountStartFailure(
                    start.Status,
                    fallbackMessage));
            }
            return false;
        }

        _activeTask = ObserveSilentlyAsync(start.Completion);
        return true;
    }

    private async Task ObserveUploadAsync(Task<AccountActionCompletion> completion)
    {
        AccountActionCompletion result;
        try
        {
            result = await completion.ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }
        if (result.Status is AccountActionCompletionStatus.Succeeded
            or AccountActionCompletionStatus.Cancelled)
        {
            try
            {
                await _dispatcher.InvokeAsync(
                    ClearSelectionAndClose,
                    DispatcherPriority.DataBind);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    private async Task ObserveDeleteAsync(Task<AccountActionCompletion> completion)
    {
        try
        {
            _ = await completion.ConfigureAwait(false);
        }
        catch
        {
            // Runtime completion is reflected by AccountStateAdapter.
        }
    }

    private static async Task ObserveSilentlyAsync(Task<AccountActionCompletion> completion)
    {
        try
        {
            _ = await completion.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void ClearSelectionAndClose()
    {
        _selection = null;
        _cropState.CloseReal();
    }

    private static string MapSelectionFailure(AvatarSelectionFailureCategory category)
    {
        return category switch
        {
            AvatarSelectionFailureCategory.TooLarge => "L’image dépasse la limite de 8 Mo.",
            AvatarSelectionFailureCategory.UnsupportedFormat => "Utilise une image JPEG, PNG ou WebP.",
            AvatarSelectionFailureCategory.AccessDenied => "Atlas Launcher ne peut pas lire ce fichier.",
            AvatarSelectionFailureCategory.FileUnavailable => "Le fichier sélectionné n’est plus disponible.",
            _ => "Cette image ne peut pas être utilisée."
        };
    }

    private static string MapStartFailure(AccountActionStartStatus status)
    {
        return status switch
        {
            AccountActionStartStatus.Busy => "Une opération est déjà en cours.",
            AccountActionStartStatus.BackendUnavailable =>
                "Les photos de profil ne sont pas encore disponibles sur ce serveur.",
            AccountActionStartStatus.NotAuthenticated => "Ta session Atlas doit être renouvelée.",
            AccountActionStartStatus.ShuttingDown => "Atlas Launcher est en cours de fermeture.",
            AccountActionStartStatus.RejectedByCompatibility =>
                "Termine l’opération en cours avant de modifier ta photo.",
            _ => "La photo ne peut pas être modifiée pour le moment."
        };
    }

    private static string MapAccountStartFailure(
        AccountActionStartStatus status,
        string fallbackMessage)
    {
        return status switch
        {
            AccountActionStartStatus.Busy => "Une modification du compte est déjà en cours.",
            AccountActionStartStatus.NotAuthenticated => "Ta session Atlas doit être renouvelée.",
            AccountActionStartStatus.ShuttingDown => "Atlas Launcher est en cours de fermeture.",
            AccountActionStartStatus.RejectedByCompatibility =>
                "Termine l’opération en cours avant de modifier ton compte.",
            _ => fallbackMessage
        };
    }
}
