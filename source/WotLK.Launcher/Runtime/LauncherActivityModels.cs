using System.Collections.Immutable;

namespace WotLK.Launcher.Runtime;

internal enum LauncherActivityProgressMode
{
    None,
    Indeterminate,
    Determinate
}

internal enum LauncherActivityPhase
{
    None,
    Preparing,
    CheckingLocalClient,
    LoadingManifest,
    ComparingManifest,
    ScanningFiles,
    Cleaning,
    Downloading,
    Applying,
    Finalizing,
    Removing,
    Cancelling
}

internal enum LauncherActivityNavigationTarget
{
    None,
    Game,
    Addons
}

internal sealed record LauncherActivityOperationSnapshot(
    long OperationId,
    LauncherOperationType OperationType,
    string TargetId,
    string TargetName,
    string DisplayName,
    LauncherActivityPhase Phase,
    LauncherActivityProgressMode ProgressMode,
    double? Percent,
    long? BytesProcessed,
    long? BytesTotal,
    double? BytesPerSecond,
    TimeSpan? Eta,
    int? FilesProcessed,
    int? FilesTotal,
    bool CanUserCancel,
    bool IsCancellationRequested,
    int? AddonPosition,
    int? AddonTotal,
    string? ErrorCategory,
    LauncherActivityNavigationTarget NavigationTarget);

internal sealed record LauncherActivityPendingItem(
    string TargetId,
    string TargetName,
    LauncherOperationType OperationType,
    LauncherActivityNavigationTarget NavigationTarget);

internal sealed record LauncherActivityRecentItem(
    long OperationId,
    LauncherOperationType OperationType,
    LauncherOperationOutcome Outcome,
    DateTimeOffset CompletedAt,
    string TargetId,
    string TargetName,
    string? ErrorCategory,
    LauncherActivityNavigationTarget NavigationTarget);

internal sealed record LauncherActivitySnapshot(
    long Sequence,
    LauncherActivityOperationSnapshot? ActiveOperation,
    ImmutableArray<LauncherActivityPendingItem> PendingItems,
    ImmutableArray<LauncherActivityRecentItem> RecentItems)
{
    internal static LauncherActivitySnapshot Initial { get; } = new(
        Sequence: 0,
        ActiveOperation: null,
        PendingItems: ImmutableArray<LauncherActivityPendingItem>.Empty,
        RecentItems: ImmutableArray<LauncherActivityRecentItem>.Empty);

    internal bool HasActiveOperation => ActiveOperation is not null;

    internal bool TopBarProgressKnown => ActiveOperation is
    {
        ProgressMode: LauncherActivityProgressMode.Determinate,
        Percent: not null,
        OperationType: not LauncherOperationType.AddonBatchUpdate
    };

    internal double? TopBarProgress => TopBarProgressKnown
        ? ActiveOperation!.Percent
        : null;

    internal bool HasRecentFailure => RecentItems.Any(item =>
        item.Outcome == LauncherOperationOutcome.Failed);
}

internal sealed class LauncherActivitySnapshotEventArgs(
    LauncherActivitySnapshot snapshot) : EventArgs
{
    internal LauncherActivitySnapshot Snapshot { get; } = snapshot;
}
