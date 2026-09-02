namespace WotLK.Launcher.Runtime;

internal enum LauncherOperationType
{
    GameInstall,
    GameUpdate,
    GameVerify,
    GameRepair,
    AddonInstall,
    AddonUpdate,
    AddonRepair,
    AddonRemove,
    AddonBatchUpdate,
    AddonSynchronization,
    LauncherAutoUpdate,
    AvatarUpload,
    AvatarDelete,
    AccountEmailChange,
    AccountEmailVerification,
    AccountPasswordChange,
    AccountSessionRevoke,
    Logout,
    Play
}

internal enum LauncherOperationOutcome
{
    Succeeded,
    Cancelled,
    Failed
}

internal enum LauncherOperationCancellationReason
{
    None,
    User,
    Shutdown
}

internal sealed record LauncherOperationDisplayContext(
    string SubjectId,
    string DisplayName,
    string? Message = null);

internal sealed record OperationTerminalResult(
    long OperationId,
    LauncherOperationType OperationType,
    LauncherOperationOutcome Outcome,
    DateTimeOffset CompletedAt,
    LauncherOperationCancellationReason CancellationReason = LauncherOperationCancellationReason.None,
    string? ErrorCategory = null,
    LauncherOperationDisplayContext? DisplayContext = null);

internal sealed record LauncherOperationActivitySnapshot(
    long Sequence,
    long? OperationId,
    LauncherOperationType? OperationType,
    bool IsActive,
    bool CanUserCancel,
    bool IsShuttingDown)
{
    internal static LauncherOperationActivitySnapshot Initial { get; } = new(
        Sequence: 0,
        OperationId: null,
        OperationType: null,
        IsActive: false,
        CanUserCancel: false,
        IsShuttingDown: false);
}

internal sealed class LauncherOperationActivitySnapshotEventArgs(
    LauncherOperationActivitySnapshot snapshot) : EventArgs
{
    internal LauncherOperationActivitySnapshot Snapshot { get; } = snapshot;
}

internal static class LauncherOperationActivityPolicy
{
    internal const int RecentHistoryLimit = 10;

    internal static bool IsTracked(LauncherOperationType operationType) =>
        operationType is LauncherOperationType.GameInstall
            or LauncherOperationType.GameUpdate
            or LauncherOperationType.GameVerify
            or LauncherOperationType.GameRepair
            or LauncherOperationType.AddonInstall
            or LauncherOperationType.AddonUpdate
            or LauncherOperationType.AddonRepair
            or LauncherOperationType.AddonRemove
            or LauncherOperationType.AddonBatchUpdate
            or LauncherOperationType.LauncherAutoUpdate;
}
