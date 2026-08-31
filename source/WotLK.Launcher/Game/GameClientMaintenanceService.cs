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

    Task<GameClientMaintenanceResult> VerifyAndRepairAsync(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        Action<GameClientMaintenanceProgress>? reportProgress);
}

internal sealed class GameClientMaintenanceService : IGameClientMaintenanceService
{
    private readonly IGameManifestClient _manifestClient;
    private readonly IGameFileVerifier _fileVerifier;
    private readonly IGameFullFileVerifier _fullFileVerifier;
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
        IGameInstallPlatform installPlatform,
        IGameFullFileVerifier? fullFileVerifier = null)
    {
        _manifestClient = manifestClient ?? throw new ArgumentNullException(nameof(manifestClient));
        _fileVerifier = fileVerifier ?? throw new ArgumentNullException(nameof(fileVerifier));
        _fullFileVerifier = fullFileVerifier ?? new GameFullFileVerifier();
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

        return await ExecutePlanAsync(
            request,
            operation,
            manifest,
            missingOrChanged,
            removedFiles,
            isRepair: false,
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

    public async Task<GameClientMaintenanceResult> VerifyAndRepairAsync(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        Action<GameClientMaintenanceProgress>? reportProgress)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Kind != LauncherOperationKind.GameRepair)
        {
            throw new InvalidOperationException(
                "La réparation complète requiert un bail GameRepair.");
        }

        CancellationToken cancellationToken = operation.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(request.InstallPath))
        {
            throw new DirectoryNotFoundException(
                "Le dossier du client n’existe pas.");
        }

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
            throw new InvalidDataException("Le manifeste ne contient aucun fichier.");
        }

        Report(
            GameClientMaintenancePhase.FullVerification,
            availableVersion: manifest.Version,
            processedFileCount: 0,
            totalFileCount: manifest.Files.Count);
        GameFullVerificationResult verification = await _fullFileVerifier.VerifyAllAsync(
            request.InstallPath,
            manifest,
            progress => Report(
                GameClientMaintenancePhase.FullVerification,
                availableVersion: manifest.Version,
                currentFile: progress.CurrentFile,
                processedFileCount: progress.ProcessedFileCount,
                totalFileCount: progress.TotalFileCount),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFullVerificationIsBlocked(verification);

        IReadOnlyList<LauncherFile> repairFiles = verification.RepairFiles;
        IReadOnlyList<string> removedFiles = _fileCleanup.FindRemovedFiles(
            request.InstallPath,
            manifest);
        long totalBytes = repairFiles.Sum(file => Math.Max(file.Size, 0));
        Report(
            GameClientMaintenancePhase.ComparisonCompleted,
            availableVersion: manifest.Version,
            missingOrChangedFileCount: repairFiles.Count,
            removedFileCount: removedFiles.Count,
            comparisonSource: GameFileComparisonSource.FileSystem,
            totalBytes: totalBytes);

        if (repairFiles.Count > 0 || removedFiles.Count > 0)
        {
            _installPlatform.StopRunningGameProcesses(request.InstallPath);
            Report(
                GameClientMaintenancePhase.GameProcessesStopped,
                availableVersion: manifest.Version);
        }

        return await ExecutePlanAsync(
            request,
            operation,
            manifest,
            repairFiles,
            removedFiles,
            isRepair: true,
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
            TimeSpan? remaining = null)
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
                remaining));
        }
    }

    private async Task<GameClientMaintenanceResult> ExecutePlanAsync(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        LauncherManifest manifest,
        IReadOnlyList<LauncherFile> missingOrChanged,
        IReadOnlyList<string> removedFiles,
        bool isRepair,
        Action<GameClientMaintenanceProgress>? reportProgress)
    {
        CancellationToken cancellationToken = operation.CancellationToken;
        if (missingOrChanged.Count == 0 && removedFiles.Count == 0)
        {
            return Finalize(
                request,
                operation,
                manifest,
                GameClientMaintenanceOutcome.AlreadyCurrent,
                downloadedFileCount: 0,
                deletedFileCount: 0,
                isRepair,
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
            return Finalize(
                request,
                operation,
                manifest,
                GameClientMaintenanceOutcome.CleanupOnly,
                downloadedFileCount: 0,
                deletedFileCount: deletedCount,
                isRepair,
                reportProgress);
        }

        long totalBytes = missingOrChanged.Sum(file => Math.Max(file.Size, 0));
        Report(
            isRepair
                ? GameClientMaintenancePhase.RepairDownloading
                : GameClientMaintenancePhase.DownloadingStarted,
            processedFileCount: 0,
            totalFileCount: missingOrChanged.Count,
            downloadedBytes: 0,
            totalBytes: totalBytes);
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
                isRepair
                    ? GameClientMaintenancePhase.RepairDownloading
                    : GameClientMaintenancePhase.DownloadingFile,
                currentFile: file.Path,
                processedFileCount: isRepair ? index : index + 1,
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
                    if (!isRepair && transfer.Stage != GameFileTransferStage.Downloading)
                    {
                        return;
                    }

                    long currentBytes = downloadedBytes + Math.Max(transfer.DownloadedBytes, 0);
                    (double? speed, TimeSpan? remaining) = CalculateRate(
                        currentBytes,
                        totalBytes,
                        downloadStopwatch.Elapsed);
                    GameClientMaintenancePhase phase = isRepair
                        ? transfer.Stage == GameFileTransferStage.Downloading
                            ? GameClientMaintenancePhase.RepairDownloading
                            : GameClientMaintenancePhase.RepairApplying
                        : GameClientMaintenancePhase.Downloading;
                    int processedCount = isRepair
                        ? transfer.Stage == GameFileTransferStage.Completed
                            ? index + 1
                            : index
                        : index + 1;
                    Report(
                        phase,
                        currentFile: file.Path,
                        processedFileCount: processedCount,
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

        return Finalize(
            request,
            operation,
            manifest,
            GameClientMaintenanceOutcome.Downloaded,
            missingOrChanged.Count,
            deletedCount,
            isRepair,
            reportProgress);

        void Report(
            GameClientMaintenancePhase phase,
            string? currentFile = null,
            int? processedFileCount = null,
            int? totalFileCount = null,
            int? removedFileCount = null,
            int? deletedFileCount = null,
            long? downloadedBytes = null,
            long? totalBytes = null,
            double? bytesPerSecond = null,
            TimeSpan? remaining = null)
        {
            reportProgress?.Invoke(new GameClientMaintenanceProgress(
                operation.OperationId,
                phase,
                AvailableVersion: manifest.Version,
                CurrentFile: currentFile,
                ProcessedFileCount: processedFileCount,
                TotalFileCount: totalFileCount,
                RemovedFileCount: removedFileCount,
                DeletedFileCount: deletedFileCount,
                DownloadedBytes: downloadedBytes,
                TotalBytes: totalBytes,
                BytesPerSecond: bytesPerSecond,
                Remaining: remaining));
        }
    }

    private GameClientMaintenanceResult Finalize(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        LauncherManifest manifest,
        GameClientMaintenanceOutcome outcome,
        int downloadedFileCount,
        int deletedFileCount,
        bool isRepair,
        Action<GameClientMaintenanceProgress>? reportProgress)
    {
        return isRepair
            ? FinalizeRepair(
                request,
                operation,
                manifest,
                outcome,
                downloadedFileCount,
                deletedFileCount,
                reportProgress)
            : FinalizeInstallation(
                request,
                operation,
                manifest,
                outcome,
                downloadedFileCount,
                deletedFileCount,
                reportProgress);
    }

    private static void ThrowIfFullVerificationIsBlocked(
        GameFullVerificationResult verification)
    {
        GameManagedFileVerification? blocked = verification.BlockingFailures.FirstOrDefault();
        if (blocked is null)
        {
            return;
        }

        if (blocked.Status == GameManagedFileStatus.InvalidPath)
        {
            throw new InvalidDataException(
                "Le manifeste contient un chemin de fichier invalide.");
        }

        if (blocked.ReadFailure == GameManagedFileReadFailure.Permission)
        {
            throw new UnauthorizedAccessException(
                "Un fichier géré ne peut pas être lu avec les autorisations actuelles.");
        }

        throw new IOException(
            "Un fichier géré est verrouillé ou illisible.");
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

    private GameClientMaintenanceResult FinalizeRepair(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        LauncherManifest manifest,
        GameClientMaintenanceOutcome outcome,
        int downloadedFileCount,
        int deletedFileCount,
        Action<GameClientMaintenanceProgress>? reportProgress)
    {
        operation.CancellationToken.ThrowIfCancellationRequested();
        operation.DisableUserCancellation();
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

        operation.CancellationToken.ThrowIfCancellationRequested();
        _manifestStore.Save(request.InstallPath, manifest);
        reportProgress?.Invoke(new GameClientMaintenanceProgress(
            operation.OperationId,
            GameClientMaintenancePhase.CacheSaved,
            AvailableVersion: manifest.Version));
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
