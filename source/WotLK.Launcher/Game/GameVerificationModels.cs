using WotLK.Launcher.Runtime;

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
    ShuttingDown,
    RejectedByCompatibility
}

internal enum GamePrimaryActionStatus
{
    Started,
    CancelRequested,
    Busy,
    Unauthenticated,
    ShuttingDown,
    Unsupported
}

internal enum GameRuntimeErrorCategory
{
    Network,
    Unauthorized,
    Disk,
    Permission,
    LockedFile,
    Integrity,
    Platform,
    Unknown
}

internal enum GameViewMode
{
    NotInstalled,
    UpdateAvailable,
    Ready,
    Verifying,
    Downloading,
    Installing,
    Error
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
    string? FailureCategory,
    string GameLocale = "frFR",
    LauncherOperationKind? OperationKind = null,
    GameClientMaintenancePhase? MaintenancePhase = null,
    bool CanPrimaryAction = false,
    bool CanUserCancel = false,
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    TimeSpan? Remaining = null,
    string? CurrentFile = null,
    GameRuntimeErrorCategory? ErrorCategory = null,
    string? ErrorTitle = null,
    string? ErrorSummary = null,
    GameAction? RetryAction = null,
    LauncherOperationKind? RetryOperationKind = null,
    string? PrimaryActionUnavailableReason = null)
{
    internal GameViewMode ViewMode
    {
        get
        {
            if (ErrorCategory is not null)
            {
                return GameViewMode.Error;
            }

            if (IsVerifying || OperationKind == LauncherOperationKind.Verify)
            {
                return GameViewMode.Verifying;
            }

            if (OperationKind == LauncherOperationKind.GameRepair)
            {
                return MaintenancePhase switch
                {
                    GameClientMaintenancePhase.LoadingManifest
                        or GameClientMaintenancePhase.ManifestLoaded
                        or GameClientMaintenancePhase.FullVerification
                        or GameClientMaintenancePhase.ComparisonCompleted => GameViewMode.Verifying,
                    GameClientMaintenancePhase.RepairDownloading => GameViewMode.Downloading,
                    _ => GameViewMode.Installing
                };
            }

            if (OperationKind is LauncherOperationKind.GameInstall
                or LauncherOperationKind.GameUpdate)
            {
                return MaintenancePhase switch
                {
                    GameClientMaintenancePhase.Cleaning
                        or GameClientMaintenancePhase.CleanupCompleted
                        or GameClientMaintenancePhase.CacheSaved
                        or GameClientMaintenancePhase.Registering
                        or GameClientMaintenancePhase.RegistrationCompleted
                        or GameClientMaintenancePhase.Completed => GameViewMode.Installing,
                    _ => GameViewMode.Downloading
                };
            }

            return Action switch
            {
                GameAction.Install => GameViewMode.NotInstalled,
                GameAction.Update => GameViewMode.UpdateAvailable,
                _ => GameViewMode.Ready
            };
        }
    }

    internal bool IsMaintenanceActive => OperationKind is LauncherOperationKind.GameInstall
        or LauncherOperationKind.GameUpdate
        or LauncherOperationKind.GameRepair;

    internal bool IsFinalizing => IsMaintenanceActive
        && MaintenancePhase is GameClientMaintenancePhase.CacheSaved
            or GameClientMaintenancePhase.Registering
            or GameClientMaintenancePhase.RegistrationCompleted
            or GameClientMaintenancePhase.Completed;
}

internal sealed class GameRuntimeSnapshotEventArgs(GameRuntimeSnapshot snapshot) : EventArgs
{
    internal GameRuntimeSnapshot Snapshot { get; } = snapshot;
}
