namespace WotLK.Launcher.Dashboard;

public enum DashboardRealmState
{
    Unknown,
    Loading,
    Online,
    Degraded,
    Offline,
    Unavailable
}

internal enum DashboardFailureCategory
{
    None,
    NoSession,
    Network,
    Timeout,
    Unauthorized,
    InvalidResponse,
    ServiceUnavailable,
    Unexpected
}

internal enum DashboardRefreshStartStatus
{
    Started,
    Busy,
    NoSession,
    ShuttingDown
}

internal sealed record DashboardSnapshot(
    long Sequence,
    bool IsLoading,
    DashboardRealmState RealmState,
    string RealmStatusLabel,
    DateTimeOffset? LastSuccessfulRefreshAt,
    DashboardFailureCategory FailureCategory,
    string? LatestPatchNoteId,
    string? LatestPatchNoteCategory,
    string LatestPatchNoteTitle,
    string LatestPatchNoteSummary,
    string LatestPatchNoteVersion,
    DateTimeOffset? LatestPatchNoteDate,
    bool HasPatchNote,
    bool IsStale,
    bool HasRetainedDataAfterFailure,
    DashboardRealmState? LastKnownRealmState,
    string? LastKnownRealmStatusLabel)
{
    internal static DashboardSnapshot Initial { get; } = new(
        Sequence: 0,
        IsLoading: false,
        RealmState: DashboardRealmState.Unknown,
        RealmStatusLabel: "Non vérifié",
        LastSuccessfulRefreshAt: null,
        FailureCategory: DashboardFailureCategory.None,
        LatestPatchNoteId: null,
        LatestPatchNoteCategory: null,
        LatestPatchNoteTitle: string.Empty,
        LatestPatchNoteSummary: string.Empty,
        LatestPatchNoteVersion: string.Empty,
        LatestPatchNoteDate: null,
        HasPatchNote: false,
        IsStale: false,
        HasRetainedDataAfterFailure: false,
        LastKnownRealmState: null,
        LastKnownRealmStatusLabel: null);
}

internal sealed class DashboardSnapshotEventArgs(DashboardSnapshot snapshot) : EventArgs
{
    internal DashboardSnapshot Snapshot { get; } = snapshot;
}

internal interface ILauncherDashboardRuntime
{
    event EventHandler? AvailabilityChanged;

    event EventHandler<DashboardSnapshotEventArgs>? SnapshotChanged;

    DashboardSnapshot CurrentSnapshot { get; }

    bool CanRefresh { get; }

    DashboardRefreshStartStatus TryRefresh();
}
