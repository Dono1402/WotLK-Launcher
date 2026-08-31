namespace WotLK.Launcher.Game;

internal enum GameClientMaintenancePhase
{
    LoadingManifest,
    ManifestLoaded,
    GameProcessesStopped,
    ComparingManifest,
    ScanningFiles,
    ComparisonCompleted,
    Cleaning,
    CleanupCompleted,
    DownloadingStarted,
    DownloadingFile,
    Downloading,
    CacheSaved,
    Registering,
    RegistrationCompleted,
    Completed
}

internal enum GameClientMaintenanceOutcome
{
    AlreadyCurrent,
    CleanupOnly,
    Downloaded
}

internal sealed record GameClientMaintenanceRequest(
    string InstallPath,
    string ManifestUrl,
    string GameLocale);

internal sealed record GameClientMaintenanceProgress(
    long OperationId,
    GameClientMaintenancePhase Phase,
    string? AvailableVersion = null,
    string? CurrentFile = null,
    int? ProcessedFileCount = null,
    int? TotalFileCount = null,
    int? MissingOrChangedFileCount = null,
    int? RemovedFileCount = null,
    int? DeletedFileCount = null,
    GameFileComparisonSource? ComparisonSource = null,
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    TimeSpan? Remaining = null,
    string? ConfigPath = null,
    string? UninstallerPath = null);

internal sealed record GameClientMaintenanceResult(
    long OperationId,
    GameClientMaintenanceOutcome Outcome,
    string AvailableVersion,
    int DownloadedFileCount,
    int DeletedFileCount,
    string? ConfigPath,
    string? UninstallerPath);

internal sealed record GameFileTransferProgress(
    long OperationId,
    long DownloadedBytes,
    long? TotalBytes);
