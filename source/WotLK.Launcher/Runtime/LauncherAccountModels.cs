using WotLK.Launcher.Account;

namespace WotLK.Launcher.Runtime;

internal enum AvatarBackendAvailability
{
    Unknown,
    Available,
    Unavailable
}

internal enum AccountLoadingState
{
    SignedOut,
    Idle,
    Loading,
    Loaded
}

internal enum AccountAvatarOperationState
{
    None,
    Preparing,
    Uploading,
    Processing,
    Reconciling,
    Removing
}

internal enum AccountAvatarErrorCategory
{
    None,
    AvatarTooLarge,
    InvalidImage,
    UnsupportedFormat,
    InvalidDimensions,
    InvalidCrop,
    UploadInProgress,
    RateLimited,
    ProcessingFailed,
    StorageFailed,
    Unauthorized,
    BackendUnavailable,
    Network,
    CancellationAmbiguous,
    Unknown
}

internal sealed record AccountRuntimeSnapshot(
    long Sequence,
    long? OperationId,
    bool IsAuthenticated,
    string Username,
    string Email,
    bool EmailVerified,
    AvatarDescriptor? Avatar,
    AvatarBackendAvailability AvatarAvailability,
    AccountLoadingState LoadingState,
    AccountAvatarOperationState AvatarOperation,
    int? UploadPercentage,
    AccountAvatarErrorCategory ErrorCategory)
{
    internal static AccountRuntimeSnapshot SignedOut { get; } = new(
        Sequence: 0,
        OperationId: null,
        IsAuthenticated: false,
        Username: string.Empty,
        Email: string.Empty,
        EmailVerified: false,
        Avatar: null,
        AvatarAvailability: AvatarBackendAvailability.Unknown,
        LoadingState: AccountLoadingState.SignedOut,
        AvatarOperation: AccountAvatarOperationState.None,
        UploadPercentage: null,
        ErrorCategory: AccountAvatarErrorCategory.None);

    internal string DisplayInitial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();

    internal bool IsBusy => AvatarOperation != AccountAvatarOperationState.None;
}

internal sealed class AccountRuntimeSnapshotEventArgs : EventArgs
{
    internal AccountRuntimeSnapshotEventArgs(AccountRuntimeSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    internal AccountRuntimeSnapshot Snapshot { get; }
}

internal enum AccountActionStartStatus
{
    Started,
    Busy,
    ShuttingDown,
    NotAuthenticated,
    BackendUnavailable,
    RejectedByCompatibility,
    InvalidRequest
}

internal enum AccountActionCompletionStatus
{
    Succeeded,
    Cancelled,
    Failed,
    BackendUnavailable
}

internal sealed record AccountActionCompletion(
    AccountActionCompletionStatus Status,
    AccountRuntimeSnapshot Snapshot);

internal sealed record AccountActionStartResult(
    AccountActionStartStatus Status,
    long? OperationId,
    Task<AccountActionCompletion>? Completion)
{
    internal bool IsStarted => Status == AccountActionStartStatus.Started
        && Completion is not null;

    internal static AccountActionStartResult Rejected(AccountActionStartStatus status) =>
        new(status, null, null);
}
