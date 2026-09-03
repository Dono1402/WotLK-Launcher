using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WotLK.Launcher.Installer.Setup;

internal enum InstallerWorkPhase
{
    Preparation,
    CreatingDirectory,
    InstallingFiles,
    CreatingShortcuts,
    RegisteringWindows,
    Finalizing
}

internal sealed record InstallerProgress(
    InstallerWorkPhase Phase,
    double Percent,
    long ProcessedBytes,
    long TotalBytes,
    string Detail);

internal sealed record InstallerRequest(
    string InstallPath,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcut);

internal sealed record InstallerInstallResult(
    string InstallPath,
    string LauncherPath,
    string UninstallerPath,
    long InstalledBytes,
    bool DesktopShortcutCreated,
    bool StartMenuShortcutCreated);

internal sealed class InstallerOperationException : Exception
{
    internal InstallerOperationException(string userMessage, Exception innerException)
        : base(userMessage, innerException)
    {
    }
}

internal interface IInstallerFaultInjector
{
    void AfterPhase(InstallerWorkPhase phase);
}

internal sealed class NoInstallerFaultInjector : IInstallerFaultInjector
{
    internal static readonly NoInstallerFaultInjector Instance = new();

    private NoInstallerFaultInjector()
    {
    }

    public void AfterPhase(InstallerWorkPhase phase)
    {
    }
}

internal sealed class InstallerEngine
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly InstallerEnvironment _environment;
    private readonly IInstallerPayloadSource _payload;
    private readonly InstallerPathValidator _pathValidator;
    private readonly IInstallerRegistry _registry;
    private readonly IInstallerShortcutService _shortcuts;
    private readonly IInstallerProcessInspector _processes;
    private readonly InstallerLog _log;
    private readonly IInstallerFaultInjector _faults;
    private int _running;

    internal InstallerEngine(
        InstallerEnvironment environment,
        IInstallerPayloadSource payload,
        InstallerPathValidator pathValidator,
        IInstallerRegistry registry,
        IInstallerShortcutService shortcuts,
        IInstallerProcessInspector processes,
        InstallerLog log,
        IInstallerFaultInjector? faults = null)
    {
        _environment = environment;
        _payload = payload;
        _pathValidator = pathValidator;
        _registry = registry;
        _shortcuts = shortcuts;
        _processes = processes;
        _log = log;
        _faults = faults ?? NoInstallerFaultInjector.Instance;
    }

    internal long RequiredBytes
    {
        get
        {
            long setupBytes = new FileInfo(_environment.SetupExecutablePath).Length;
            return checked(_payload.Length + setupBytes + InstallerProduct.FreeSpaceMargin);
        }
    }

    internal InstallerPathValidationResult ValidatePath(string? path) =>
        _pathValidator.Validate(path, RequiredBytes);

    internal ExistingInstallation DetectExistingInstallation() => _registry.Detect(
        _environment.DetectionRegistrySubKeys,
        GetFallbackInstallPaths());

    internal async Task<InstallerInstallResult> InstallAsync(
        InstallerRequest request,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("Une installation Atlas Launcher est déjà en cours.");
        }

        string? destination = null;
        string? staging = null;
        string? firstExistingParent = null;
        bool destinationExistedEmpty = false;
        bool destinationCommitted = false;
        bool desktopShortcutAttempted = false;
        bool startMenuShortcutAttempted = false;
        bool registryWriteAttempted = false;
        try
        {
            if (!Environment.Is64BitProcess)
            {
                throw new PlatformNotSupportedException(
                    "Cette distribution d'Atlas Launcher exige un processus x64.");
            }

            if (!_environment.IsTest && !WindowsInstallerSystemActions.IsCurrentProcessElevated())
            {
                throw new UnauthorizedAccessException(
                    "L'installation per-machine doit être exécutée avec les droits administrateur.");
            }

            ExistingInstallation existing = DetectExistingInstallation();
            if (existing.Status != ExistingInstallationStatus.None)
            {
                throw new InvalidOperationException(existing.Message);
            }

            InstallerPathValidationResult validation = ValidatePath(request.InstallPath);
            if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.FullPath))
            {
                throw new InvalidOperationException(validation.Message);
            }

            destination = validation.FullPath;
            _environment.DemandAllowedDestination(destination);
            string launcherPath = Path.Combine(destination, InstallerProduct.LauncherFileName);
            IReadOnlyList<int> running = _processes.FindByExactPath(launcherPath);
            if (running.Count > 0)
            {
                throw new InvalidOperationException(
                    "Atlas Launcher est encore ouvert dans ce dossier. Ferme-le puis réessaie.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, InstallerWorkPhase.Preparation, 1, 0, 0, "Validation de l'installation");
            _log.Info($"Installation {InstallerProduct.Version} démarrée vers {destination}.");
            _faults.AfterPhase(InstallerWorkPhase.Preparation);

            string parent = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("Le dossier parent de l'installation est invalide.");
            firstExistingParent = FindFirstExistingParent(parent);
            Directory.CreateDirectory(parent);
            destinationExistedEmpty = Directory.Exists(destination);
            if (destinationExistedEmpty && Directory.EnumerateFileSystemEntries(destination).Any())
            {
                throw new IOException("Le dossier d'installation n'est plus vide.");
            }

            staging = Path.Combine(parent, $".atlas-launcher-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            Report(progress, InstallerWorkPhase.CreatingDirectory, 5, 0, 0, "Dossier de préparation créé");
            _faults.AfterPhase(InstallerWorkPhase.CreatingDirectory);

            string stagedLauncher = Path.Combine(staging, InstallerProduct.LauncherFileName);
            string stagedUninstaller = Path.Combine(staging, InstallerProduct.UninstallerFileName);
            long setupLength = new FileInfo(_environment.SetupExecutablePath).Length;
            long totalCopyBytes = checked(_payload.Length + setupLength);
            long copiedBytes = 0;

            using (Stream payload = _payload.OpenRead())
            {
                copiedBytes = await CopyAndVerifyPayloadAsync(
                    payload,
                    stagedLauncher,
                    _payload.Length,
                    _payload.Sha256,
                    copiedBytes,
                    totalCopyBytes,
                    progress,
                    cancellationToken);
            }

            copiedBytes = await CopyFileAsync(
                _environment.SetupExecutablePath,
                stagedUninstaller,
                copiedBytes,
                totalCopyBytes,
                progress,
                cancellationToken);
            _faults.AfterPhase(InstallerWorkPhase.InstallingFiles);

            string finalLauncher = Path.Combine(destination, InstallerProduct.LauncherFileName);
            string finalUninstaller = Path.Combine(destination, InstallerProduct.UninstallerFileName);
            AtlasInstallState state = new(
                SchemaVersion: 1,
                InstallerProduct.Version,
                destination,
                finalLauncher,
                finalUninstaller,
                request.CreateDesktopShortcut,
                _environment.DesktopShortcutPath,
                request.CreateStartMenuShortcut,
                _environment.StartMenuShortcutPath,
                _environment.RegistrySubKey,
                DateTimeOffset.UtcNow,
                _environment.LogPath,
                _environment.IsTest);
            string statePath = Path.Combine(staging, InstallerProduct.InstallStateFileName);
            await File.WriteAllTextAsync(
                statePath,
                JsonSerializer.Serialize(state, StateJsonOptions) + Environment.NewLine,
                cancellationToken);

            if (destinationExistedEmpty)
            {
                Directory.Delete(destination);
            }

            Directory.Move(staging, destination);
            staging = null;
            destinationCommitted = true;

            Report(progress, InstallerWorkPhase.CreatingShortcuts, 84, copiedBytes, totalCopyBytes, "Création des raccourcis");
            if (request.CreateDesktopShortcut)
            {
                desktopShortcutAttempted = true;
                _shortcuts.Create(_environment.DesktopShortcutPath, finalLauncher, destination);
            }

            if (request.CreateStartMenuShortcut)
            {
                startMenuShortcutAttempted = true;
                _shortcuts.Create(_environment.StartMenuShortcutPath, finalLauncher, destination);
            }

            _faults.AfterPhase(InstallerWorkPhase.CreatingShortcuts);
            cancellationToken.ThrowIfCancellationRequested();

            long installedBytes = CalculateDirectorySize(destination);
            long estimatedKiB = Math.Max(1, (installedBytes + 1023) / 1024);
            Report(progress, InstallerWorkPhase.RegisteringWindows, 94, copiedBytes, totalCopyBytes, "Enregistrement dans Windows");
            registryWriteAttempted = true;
            _registry.Register(new InstalledApplicationRegistration(
                _environment.RegistrySubKey,
                destination,
                finalLauncher,
                finalUninstaller,
                estimatedKiB));
            _faults.AfterPhase(InstallerWorkPhase.RegisteringWindows);

            Report(progress, InstallerWorkPhase.Finalizing, 100, totalCopyBytes, totalCopyBytes, "Installation terminée");
            _faults.AfterPhase(InstallerWorkPhase.Finalizing);
            _log.Info(
                $"Installation terminée. Taille installée : {installedBytes} octets; "
                + $"Bureau={desktopShortcutAttempted}; Démarrer={startMenuShortcutAttempted}.");

            return new InstallerInstallResult(
                destination,
                finalLauncher,
                finalUninstaller,
                installedBytes,
                desktopShortcutAttempted,
                startMenuShortcutAttempted);
        }
        catch (Exception exception)
        {
            _log.Error("Échec de l'installation; démarrage du rollback", exception);
            Rollback(
                destination,
                staging,
                firstExistingParent,
                destinationExistedEmpty,
                destinationCommitted,
                desktopShortcutAttempted,
                startMenuShortcutAttempted,
                registryWriteAttempted);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            throw new InstallerOperationException(
                "L'installation n'a pas pu être terminée. Les changements incomplets ont été retirés.",
                exception);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<long> CopyAndVerifyPayloadAsync(
        Stream source,
        string targetPath,
        long expectedLength,
        string expectedSha256,
        long alreadyCopied,
        long totalCopyBytes,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        string partial = targetPath + ".partial";
        byte[] buffer = new byte[1024 * 1024];
        long payloadBytes = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (FileStream output = new(
            partial,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                payloadBytes += read;
                ReportCopy(progress, alreadyCopied + payloadBytes, totalCopyBytes);
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (payloadBytes != expectedLength
            || !string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Le payload Atlas Launcher embarqué n'a pas passé la validation SHA-256.");
        }

        File.Move(partial, targetPath);
        _log.Info($"Payload validé : {payloadBytes} octets, SHA-256 {actualHash}.");
        return alreadyCopied + payloadBytes;
    }

    private static async Task<long> CopyFileAsync(
        string sourcePath,
        string targetPath,
        long alreadyCopied,
        long totalCopyBytes,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        string partial = targetPath + ".partial";
        byte[] buffer = new byte[1024 * 1024];
        long fileBytes = 0;
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (FileStream output = new(
            partial,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                fileBytes += read;
                ReportCopy(progress, alreadyCopied + fileBytes, totalCopyBytes);
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        if (fileBytes != source.Length)
        {
            throw new InvalidDataException("La copie du désinstalleur est incomplète.");
        }

        File.Move(partial, targetPath);
        return alreadyCopied + fileBytes;
    }

    private void Rollback(
        string? destination,
        string? staging,
        string? firstExistingParent,
        bool destinationExistedEmpty,
        bool destinationCommitted,
        bool desktopShortcutAttempted,
        bool startMenuShortcutAttempted,
        bool registryWriteAttempted)
    {
        List<Exception> rollbackErrors = [];
        TryRollback(() =>
        {
            if (registryWriteAttempted)
            {
                _registry.Unregister(_environment.RegistrySubKey);
            }
        }, rollbackErrors);
        TryRollback(() =>
        {
            if (desktopShortcutAttempted && destination is not null)
            {
                _shortcuts.DeleteIfOwned(
                    _environment.DesktopShortcutPath,
                    Path.Combine(destination, InstallerProduct.LauncherFileName));
            }
        }, rollbackErrors);
        TryRollback(() =>
        {
            if (startMenuShortcutAttempted && destination is not null)
            {
                _shortcuts.DeleteIfOwned(
                    _environment.StartMenuShortcutPath,
                    Path.Combine(destination, InstallerProduct.LauncherFileName));
            }
        }, rollbackErrors);
        TryRollback(() => DeleteDirectoryTree(staging), rollbackErrors);
        TryRollback(() =>
        {
            if (destinationCommitted)
            {
                DeleteDirectoryTree(destination);
            }
        }, rollbackErrors);
        TryRollback(() =>
        {
            if (destinationExistedEmpty && destination is not null && !Directory.Exists(destination))
            {
                Directory.CreateDirectory(destination);
            }
        }, rollbackErrors);
        TryRollback(() => RemoveCreatedEmptyParents(destination, firstExistingParent), rollbackErrors);

        foreach (Exception rollbackError in rollbackErrors)
        {
            _log.Error("Une étape du rollback a échoué", rollbackError);
        }

        _log.Info("Rollback terminé.");
    }

    private static void ReportCopy(
        IProgress<InstallerProgress>? progress,
        long copiedBytes,
        long totalCopyBytes)
    {
        double percent = totalCopyBytes <= 0
            ? 8
            : 8 + (copiedBytes / (double)totalCopyBytes * 72);
        Report(
            progress,
            InstallerWorkPhase.InstallingFiles,
            percent,
            copiedBytes,
            totalCopyBytes,
            $"{InstallerPathValidator.FormatBytes(copiedBytes)} sur {InstallerPathValidator.FormatBytes(totalCopyBytes)}");
    }

    private static void Report(
        IProgress<InstallerProgress>? progress,
        InstallerWorkPhase phase,
        double percent,
        long processedBytes,
        long totalBytes,
        string detail) =>
        progress?.Report(new InstallerProgress(
            phase,
            Math.Clamp(percent, 0, 100),
            processedBytes,
            totalBytes,
            detail));

    private static string FindFirstExistingParent(string path)
    {
        string candidate = Path.GetFullPath(path);
        while (!Directory.Exists(candidate))
        {
            candidate = Path.GetDirectoryName(candidate)
                ?? throw new DirectoryNotFoundException("Aucun dossier parent accessible n'a été trouvé.");
        }

        return candidate;
    }

    private static void RemoveCreatedEmptyParents(string? destination, string? firstExistingParent)
    {
        if (destination is null || firstExistingParent is null)
        {
            return;
        }

        string? candidate = Path.GetDirectoryName(destination);
        while (!string.IsNullOrWhiteSpace(candidate)
            && !InstallerEnvironment.SamePath(candidate, firstExistingParent)
            && Directory.Exists(candidate)
            && !Directory.EnumerateFileSystemEntries(candidate).Any())
        {
            Directory.Delete(candidate);
            candidate = Path.GetDirectoryName(candidate);
        }
    }

    private static void DeleteDirectoryTree(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static void TryRollback(Action action, ICollection<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static long CalculateDirectorySize(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

    private IEnumerable<string> GetFallbackInstallPaths()
    {
        yield return _environment.DefaultInstallPath;
        if (!_environment.IsTest)
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "WotLK Launcher");
            }
        }
    }
}
