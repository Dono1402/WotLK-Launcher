namespace WotLK.Launcher.Game;

internal enum GameVerificationPhase
{
    Stable,
    CheckingLocalClient,
    LoadingManifest,
    ComparingManifest,
    ScanningFiles
}

internal enum GameVerificationOutcome
{
    NotInstalled,
    GameRunning,
    EmptyManifest,
    UpToDate,
    UpdateAvailable
}

internal enum GameVerificationStartStatus
{
    Started,
    Busy,
    Unauthenticated,
    ShuttingDown
}

internal enum GameFileComparisonSource
{
    ManifestHistory,
    InstalledVersion,
    FileSystem
}

internal sealed record GameVerificationProgress(
    GameVerificationPhase Phase,
    int? ProcessedFileCount = null,
    int? TotalFileCount = null);

internal sealed record GameFileComparisonResult(
    IReadOnlyList<LauncherFile> MissingOrChangedFiles,
    GameFileComparisonSource Source,
    int ProcessedFileCount,
    int TotalFileCount);

internal sealed record GameClientVerificationResult(
    GameVerificationOutcome Outcome,
    GameAction Action,
    GameUpdateKnowledge UpdateKnowledge,
    string AvailableVersion,
    int ChangeCount);

internal sealed record GameRuntimeSnapshot(
    long Sequence,
    long? OperationId,
    GameAction Action,
    GameUpdateKnowledge UpdateKnowledge,
    GameVerificationPhase Phase,
    bool IsVerifying,
    bool CanVerify,
    bool IsPlayable,
    string InstallPath,
    string? InstalledVersion,
    string? AvailableVersion,
    int? ProcessedFileCount,
    int? TotalFileCount,
    string? FailureCategory);

internal sealed class GameRuntimeSnapshotEventArgs(GameRuntimeSnapshot snapshot) : EventArgs
{
    internal GameRuntimeSnapshot Snapshot { get; } = snapshot;
}
