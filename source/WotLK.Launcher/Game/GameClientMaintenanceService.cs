using System.Diagnostics;
using System.IO;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.Game;

internal interface IGameClientMaintenanceService
{
    Task<GameClientMaintenanceResult> InstallOrUpdateAsync(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        Action<GameClientMaintenanceProgress>? reportProgress);
}

internal sealed class GameClientMaintenanceService : IGameClientMaintenanceService
{
    private readonly IGameManifestClient _manifestClient;
    private readonly IGameFileVerifier _fileVerifier;
    private readonly IInstalledManifestStore _manifestStore;
    private readonly IGameFileTransferService _fileTransfer;
    private readonly IGameFileCleanupService _fileCleanup;
    private readonly IGameInstallPlatform _installPlatform;

    internal GameClientMaintenanceService(
        IGameManifestClient manifestClient,
        IGameFileVerifier fileVerifier,
        IInstalledManifestStore manifestStore,
        IGameFileTransferService fileTransfer,
        IGameFileCleanupService fileCleanup,
        IGameInstallPlatform installPlatform)
    {
        _manifestClient = manifestClient ?? throw new ArgumentNullException(nameof(manifestClient));
        _fileVerifier = fileVerifier ?? throw new ArgumentNullException(nameof(fileVerifier));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        _fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
        _fileCleanup = fileCleanup ?? throw new ArgumentNullException(nameof(fileCleanup));
        _installPlatform = installPlatform ?? throw new ArgumentNullException(nameof(installPlatform));
    }

    public async Task<GameClientMaintenanceResult> InstallOrUpdateAsync(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        Action<GameClientMaintenanceProgress>? reportProgress)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Kind is not (LauncherOperationKind.GameInstall or LauncherOperationKind.GameUpdate))
        {
            throw new InvalidOperationException(
                "Le pipeline client requiert un bail GameInstall ou GameUpdate.");
        }

        CancellationToken cancellationToken = operation.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.InstallPath);

        Report(GameClientMaintenancePhase.LoadingManifest);
        LauncherManifest manifest = await _manifestClient.LoadAsync(
            request.ManifestUrl,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Report(
            GameClientMaintenancePhase.ManifestLoaded,
            availableVersion: manifest.Version);

        if (manifest.Files.Count == 0)
        {
            throw new InvalidOperationException("Le manifeste ne contient aucun fichier.");
        }

        _installPlatform.StopRunningGameProcesses(request.InstallPath);
        Report(GameClientMaintenancePhase.GameProcessesStopped);

        Report(GameClientMaintenancePhase.ComparingManifest);
        GameFileComparisonResult comparison = await _fileVerifier
            .FindMissingOrChangedFilesAsync(
                request.InstallPath,
                manifest,
                progress => Report(
                    GameClientMaintenancePhase.ScanningFiles,
                    processedFileCount: progress.ProcessedFileCount,
                    totalFileCount: progress.TotalFileCount),
                cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<LauncherFile> missingOrChanged = comparison.MissingOrChangedFiles;
        IReadOnlyList<string> removedFiles = _fileCleanup.FindRemovedFiles(
            request.InstallPath,
            manifest);
        long totalBytes = missingOrChanged.Sum(file => Math.Max(file.Size, 0));
        Report(
            GameClientMaintenancePhase.ComparisonCompleted,
            missingOrChangedFileCount: missingOrChanged.Count,
            removedFileCount: removedFiles.Count,
            comparisonSource: comparison.Source,
            totalBytes: totalBytes);

        if (missingOrChanged.Count == 0 && removedFiles.Count == 0)
        {
            return FinalizeInstallation(
                request,
                operation,
                manifest,
                GameClientMaintenanceOutcome.AlreadyCurrent,
                downloadedFileCount: 0,
                deletedFileCount: 0,
                reportProgress);
        }

        int deletedCount = 0;
        if (removedFiles.Count > 0)
        {
            Report(
                GameClientMaintenancePhase.Cleaning,
                removedFileCount: removedFiles.Count);
            deletedCount = _fileCleanup.DeleteRemovedFiles(
                request.InstallPath,
                removedFiles,
                cancellationToken);
            Report(
                GameClientMaintenancePhase.CleanupCompleted,
                removedFileCount: removedFiles.Count,
                deletedFileCount: deletedCount);
        }

        if (missingOrChanged.Count == 0)
        {
            return FinalizeInstallation(
                request,
                operation,
                manifest,
                GameClientMaintenanceOutcome.CleanupOnly,
                downloadedFileCount: 0,
                deletedFileCount: deletedCount,
                reportProgress);
        }

        Report(GameClientMaintenancePhase.DownloadingStarted);
        Stopwatch downloadStopwatch = Stopwatch.StartNew();
        long downloadedBytes = 0;
        for (int index = 0; index < missingOrChanged.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LauncherFile file = missingOrChanged[index];
            string target = GamePathPolicy.GetSafeTargetPath(
                request.InstallPath,
                file.Path);
            Uri uri = _fileTransfer.BuildFileUri(manifest, file);

            Report(
                GameClientMaintenancePhase.DownloadingFile,
                currentFile: file.Path,
                processedFileCount: index + 1,
                totalFileCount: missingOrChanged.Count,
                downloadedBytes: downloadedBytes,
                totalBytes: totalBytes);
            await _fileTransfer.DownloadAsync(
                operation.OperationId,
                uri,
                target,
                file.Size,
                file.Sha256,
                transfer =>
                {
                    long currentBytes = downloadedBytes + transfer.DownloadedBytes;
                    (double? speed, TimeSpan? remaining) = CalculateRate(
                        currentBytes,
                        totalBytes,
                        downloadStopwatch.Elapsed);
                    Report(
                        GameClientMaintenancePhase.Downloading,
                        currentFile: file.Path,
                        processedFileCount: index + 1,
                        totalFileCount: missingOrChanged.Count,
                        downloadedBytes: currentBytes,
                        totalBytes: totalBytes,
                        bytesPerSecond: speed,
                        remaining: remaining);
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            downloadedBytes += Math.Max(file.Size, 0);
        }

        return FinalizeInstallation(
            request,
            operation,
            manifest,
            GameClientMaintenanceOutcome.Downloaded,
            missingOrChanged.Count,
            deletedCount,
            reportProgress);

        void Report(
            GameClientMaintenancePhase phase,
            string? availableVersion = null,
            string? currentFile = null,
            int? processedFileCount = null,
            int? totalFileCount = null,
            int? missingOrChangedFileCount = null,
            int? removedFileCount = null,
            int? deletedFileCount = null,
            GameFileComparisonSource? comparisonSource = null,
            long? downloadedBytes = null,
            long? totalBytes = null,
            double? bytesPerSecond = null,
            TimeSpan? remaining = null,
            string? configPath = null,
            string? uninstallerPath = null)
        {
            reportProgress?.Invoke(new GameClientMaintenanceProgress(
                operation.OperationId,
                phase,
                availableVersion,
                currentFile,
                processedFileCount,
                totalFileCount,
                missingOrChangedFileCount,
                removedFileCount,
                deletedFileCount,
                comparisonSource,
                downloadedBytes,
                totalBytes,
                bytesPerSecond,
                remaining,
                configPath,
                uninstallerPath));
        }
    }

    private GameClientMaintenanceResult FinalizeInstallation(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        LauncherManifest manifest,
        GameClientMaintenanceOutcome outcome,
        int downloadedFileCount,
        int deletedFileCount,
        Action<GameClientMaintenanceProgress>? reportProgress)
    {
        operation.CancellationToken.ThrowIfCancellationRequested();
        _manifestStore.Save(request.InstallPath, manifest);
        reportProgress?.Invoke(new GameClientMaintenanceProgress(
            operation.OperationId,
            GameClientMaintenancePhase.CacheSaved,
            AvailableVersion: manifest.Version));

        reportProgress?.Invoke(new GameClientMaintenanceProgress(
            operation.OperationId,
            GameClientMaintenancePhase.Registering,
            AvailableVersion: manifest.Version));
        GameApplicationRegistration? registration = _installPlatform.RegisterGameApplication(
            request.InstallPath,
            manifest.Version,
            request.GameLocale);
        reportProgress?.Invoke(new GameClientMaintenanceProgress(
            operation.OperationId,
            GameClientMaintenancePhase.RegistrationCompleted,
            AvailableVersion: manifest.Version,
            ConfigPath: registration?.ConfigPath,
            UninstallerPath: registration?.UninstallerPath));
        reportProgress?.Invoke(new GameClientMaintenanceProgress(
            operation.OperationId,
            GameClientMaintenancePhase.Completed,
            AvailableVersion: manifest.Version));

        return new GameClientMaintenanceResult(
            operation.OperationId,
            outcome,
            manifest.Version,
            downloadedFileCount,
            deletedFileCount,
            registration?.ConfigPath,
            registration?.UninstallerPath);
    }

    private static (double? BytesPerSecond, TimeSpan? Remaining) CalculateRate(
        long received,
        long total,
        TimeSpan elapsed)
    {
        if (received <= 0 || elapsed.TotalSeconds < 0.5)
        {
            return (null, null);
        }

        double bytesPerSecond = received / elapsed.TotalSeconds;
        if (bytesPerSecond <= 0)
        {
            return (null, null);
        }

        TimeSpan? remaining = total > received
            ? TimeSpan.FromSeconds((total - received) / bytesPerSecond)
            : null;
        return (bytesPerSecond, remaining);
    }
}
