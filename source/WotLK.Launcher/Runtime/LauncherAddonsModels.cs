using System.Collections.Immutable;

namespace WotLK.Launcher.Runtime;

internal enum AddonsCatalogLoadState
{
    SignedOut,
    Idle,
    Loading,
    Loaded,
    Failed
}

internal enum AddonsOperationState
{
    None,
    Installing,
    Updating,
    Removing,
    Repairing,
    UpdatingAll
}

internal enum AddonsOperationPhase
{
    None,
    PreparingSession,
    Downloading,
    Removing
}

internal enum AddonsRequestedAction
{
    None,
    Install,
    Update,
    Remove,
    Repair,
    UpdateAll
}

internal enum AddonsErrorCategory
{
    None,
    Unauthorized,
    Network,
    Timeout,
    ServiceUnavailable,
    ClientUnavailable,
    AccessDenied,
    FilesLocked,
    Disk,
    InvalidPackage,
    Unknown
}

internal enum AddonsNoticeKind
{
    None,
    Installed,
    Updated,
    Removed,
    Repaired,
    BatchUpdated,
    Cancelled
}

internal sealed record AddonRuntimeItem(
    string Id,
    string Name,
    string Description,
    string Category,
    string AvailableVersion,
    string InstalledVersion,
    string InstalledSha256,
    DateTimeOffset? InstalledAtUtc,
    string InterfaceVersion,
    string Author,
    ImmutableArray<string> Dependencies,
    ImmutableArray<string> ManagedFolders,
    AddonLocalStatus LocalStatus,
    bool IsManaged,
    AddonsOperationState ActiveOperation,
    AddonsRequestedAction RetryAction,
    AddonsErrorCategory ErrorCategory)
{
    internal bool IsInstalled => IsManaged;

    internal bool NeedsUpdate => LocalStatus is AddonLocalStatus.UpdateAvailable
        or AddonLocalStatus.MissingFiles;

    internal bool NeedsRepair => LocalStatus == AddonLocalStatus.MissingFiles;

    internal bool IsDetectedUnmanaged => LocalStatus == AddonLocalStatus.DetectedUnmanaged;

    internal bool IsBusy => ActiveOperation != AddonsOperationState.None;
}

internal sealed record AddonsRuntimeProgress(
    string AddonId,
    AddonsOperationPhase Phase,
    long? BytesReceived,
    long? TotalBytes,
    double? BytesPerSecond,
    TimeSpan? EstimatedRemaining)
{
    internal double? Percent => BytesReceived is long received
        && TotalBytes is long total
        && total > 0
            ? Math.Clamp((double)received / total * 100d, 0d, 100d)
            : null;

    internal bool IsIndeterminate => Percent is null;

    internal static AddonsRuntimeProgress None { get; } = new(
        string.Empty,
        AddonsOperationPhase.None,
        null,
        null,
        null,
        null);
}

internal sealed record AddonsRuntimeError(
    string AddonId,
    AddonsRequestedAction Action,
    AddonsErrorCategory Category)
{
    internal static AddonsRuntimeError None { get; } = new(
        string.Empty,
        AddonsRequestedAction.None,
        AddonsErrorCategory.None);
}

internal sealed record AddonsRuntimeSnapshot(
    long Sequence,
    long? OperationId,
    ImmutableArray<AddonRuntimeItem> Items,
    AddonsCatalogLoadState LoadState,
    bool IsCatalogStale,
    AddonsErrorCategory CatalogErrorCategory,
    AddonsOperationState OperationState,
    AddonsOperationPhase OperationPhase,
    string ActiveAddonId,
    ImmutableArray<string> PendingAddonIds,
    AddonsRuntimeProgress Progress,
    AddonsRuntimeError Error,
    AddonsNoticeKind Notice,
    bool IsGameRunning,
    bool IsClientPlayable,
    bool IsAuthenticated,
    bool CanMutate,
    bool CanCancel,
    OperationTerminalResult? TerminalResult = null,
    int? ActiveAddonPosition = null,
    int? ActiveAddonTotal = null)
{
    internal static AddonsRuntimeSnapshot Initial { get; } = new(
        Sequence: 0,
        OperationId: null,
        Items: ImmutableArray<AddonRuntimeItem>.Empty,
        LoadState: AddonsCatalogLoadState.SignedOut,
        IsCatalogStale: false,
        CatalogErrorCategory: AddonsErrorCategory.None,
        OperationState: AddonsOperationState.None,
        OperationPhase: AddonsOperationPhase.None,
        ActiveAddonId: string.Empty,
        PendingAddonIds: ImmutableArray<string>.Empty,
        Progress: AddonsRuntimeProgress.None,
        Error: AddonsRuntimeError.None,
        Notice: AddonsNoticeKind.None,
        IsGameRunning: false,
        IsClientPlayable: false,
        IsAuthenticated: false,
        CanMutate: false,
        CanCancel: false);

    internal bool HasCatalog => LoadState == AddonsCatalogLoadState.Loaded
        || !Items.IsDefaultOrEmpty;
}

internal sealed class AddonsRuntimeSnapshotEventArgs : EventArgs
{
    internal AddonsRuntimeSnapshotEventArgs(AddonsRuntimeSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    internal AddonsRuntimeSnapshot Snapshot { get; }
}

internal enum AddonsCatalogStartStatus
{
    Started,
    AlreadyLoaded,
    Busy,
    ShuttingDown,
    NotAuthenticated
}

internal sealed record AddonsCatalogStartResult(
    AddonsCatalogStartStatus Status,
    Task? Completion)
{
    internal bool IsStarted => Status == AddonsCatalogStartStatus.Started
        && Completion is not null;

    internal static AddonsCatalogStartResult Rejected(AddonsCatalogStartStatus status) =>
        new(status, null);
}

internal enum AddonsActionStartStatus
{
    Started,
    Busy,
    ShuttingDown,
    NotAuthenticated,
    CatalogUnavailable,
    ClientUnavailable,
    AddonNotFound,
    InvalidState,
    RejectedByCompatibility
}

internal enum AddonsActionCompletionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Superseded
}

internal sealed record AddonsActionCompletion(
    AddonsActionCompletionStatus Status,
    AddonsRuntimeSnapshot Snapshot,
    OperationTerminalResult? TerminalResult = null);

internal sealed record AddonsActionStartResult(
    AddonsActionStartStatus Status,
    long? OperationId,
    Task<AddonsActionCompletion>? Completion)
{
    internal bool IsStarted => Status == AddonsActionStartStatus.Started
        && OperationId is not null
        && Completion is not null;

    internal static AddonsActionStartResult Rejected(AddonsActionStartStatus status) =>
        new(status, null, null);
}
