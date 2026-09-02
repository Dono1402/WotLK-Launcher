using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WotLK.Launcher.Updater;

internal sealed class WindowsLauncherAtomicFileMover : ILauncherAtomicFileMover
{
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;

    public void Replace(string sourcePath, string destinationPath)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(
                Path.GetPathRoot(source),
                Path.GetPathRoot(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Le remplacement atomique exige un volume unique.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        if (!MoveFileEx(
                source,
                destination,
                MoveFileReplaceExisting | MoveFileWriteThrough))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                "Le remplacement atomique Windows a échoué.",
                new Win32Exception(error));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}

internal sealed class LauncherUpdateParentWaiter : ILauncherUpdateParentWaiter
{
    public async Task<bool> WaitForExitAsync(
        int processId,
        string expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            if (!process.HasExited)
            {
                string? actualPath = TryGetProcessPath(process);
                if (!string.IsNullOrWhiteSpace(actualPath)
                    && !SamePath(actualPath, expectedExecutablePath))
                {
                    throw new InvalidDataException(
                        "Le PID parent ne correspond pas au launcher attendu.");
                }
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(timeout, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    internal static bool ProcessMatchesPath(int processId, string expectedPath)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            string? actualPath = TryGetProcessPath(process);
            return actualPath is not null && SamePath(actualPath, expectedPath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or Win32Exception)
        {
            return false;
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException)
        {
            return null;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

internal static class LauncherUpdateProcessTerminator
{
    internal static void StopIfMatches(int? processId, string expectedPath)
    {
        if (processId is not > 0
            || !LauncherUpdateParentWaiter.ProcessMatchesPath(processId.Value, expectedPath))
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException)
        {
        }
    }
}

internal sealed class LauncherAtomicReplacementService
{
    private readonly LauncherUpdateTransactionStore _store;
    private readonly ILauncherAtomicFileMover _atomicMover;
    private readonly ILauncherUpdateParentWaiter _parentWaiter;
    private readonly ILauncherUpdateApplicationLauncher _applicationLauncher;
    private readonly LauncherUpdateRetryPolicy _retryPolicy;
    private readonly ILauncherUpdateFaultInjector _faultInjector;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<int, string, bool> _processMatchesPath;
    private readonly Action<int?, string> _stopProcess;

    internal LauncherAtomicReplacementService(
        LauncherUpdateTransactionStore store,
        ILauncherAtomicFileMover atomicMover,
        ILauncherUpdateParentWaiter parentWaiter,
        ILauncherUpdateApplicationLauncher applicationLauncher,
        LauncherUpdateRetryPolicy? retryPolicy = null,
        ILauncherUpdateFaultInjector? faultInjector = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<int, string, bool>? processMatchesPath = null,
        Action<int?, string>? stopProcess = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _atomicMover = atomicMover ?? throw new ArgumentNullException(nameof(atomicMover));
        _parentWaiter = parentWaiter ?? throw new ArgumentNullException(nameof(parentWaiter));
        _applicationLauncher = applicationLauncher
            ?? throw new ArgumentNullException(nameof(applicationLauncher));
        _retryPolicy = retryPolicy ?? LauncherUpdateRetryPolicy.Production;
        _faultInjector = faultInjector ?? NullLauncherUpdateFaultInjector.Instance;
        _delayAsync = delayAsync ?? Task.Delay;
        _processMatchesPath = processMatchesPath
            ?? LauncherUpdateParentWaiter.ProcessMatchesPath;
        _stopProcess = stopProcess ?? LauncherUpdateProcessTerminator.StopIfMatches;

        if (_retryPolicy.FileAttempts <= 0
            || _retryPolicy.FileRetryDelay < TimeSpan.Zero
            || _retryPolicy.ParentExitTimeout <= TimeSpan.Zero
            || _retryPolicy.ProcessStartTimeout <= TimeSpan.Zero
            || _retryPolicy.ReadyTimeout <= TimeSpan.Zero
            || _retryPolicy.SignalPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryPolicy));
        }
    }

    internal async Task<LauncherUpdateExecutionResult> ApplyAsync(
        LauncherUpdateTransaction initialTransaction,
        CancellationToken cancellationToken = default)
    {
        LauncherUpdateTransaction transaction = initialTransaction;
        bool parentExited = false;
        bool swapObserved = false;
        ILauncherUpdateLaunchedProcess? launchedProcess = null;

        try
        {
            Log(transaction, "validation du candidat et de la release active");
            _faultInjector.Hit(LauncherUpdateFaultPoint.BeforeCandidateValidation, transaction);
            await ValidateCandidateAndTargetAsync(transaction, cancellationToken)
                .ConfigureAwait(false);
            _faultInjector.Hit(LauncherUpdateFaultPoint.AfterCandidateValidation, transaction);

            await CopyAndValidateWithRetryAsync(
                    transaction,
                    transaction.CandidatePath,
                    transaction.StagedPath,
                    transaction.CandidateSha256,
                    transaction.ExpectedSize,
                    cancellationToken)
                .ConfigureAwait(false);
            transaction = SavePhase(transaction, LauncherUpdateTransactionPhase.CandidateStaged);
            _faultInjector.Hit(LauncherUpdateFaultPoint.AfterCandidateStaged, transaction);

            Log(transaction, $"attente de la fermeture du PID {transaction.ParentProcessId}");
            parentExited = await _parentWaiter.WaitForExitAsync(
                    transaction.ParentProcessId,
                    transaction.TargetPath,
                    _retryPolicy.ParentExitTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!parentExited)
            {
                Log(transaction, "abandon: le processus parent ne s'est pas fermé");
                CleanupBeforeSwap(transaction);
                return new LauncherUpdateExecutionResult(
                    transaction.TransactionId,
                    LauncherUpdateExecutionOutcome.PreviousVersionIntact,
                    LauncherUpdateTransactionPhase.Failed,
                    "ParentDidNotExit");
            }

            await CopyAndValidateWithRetryAsync(
                    transaction,
                    transaction.TargetPath,
                    transaction.BackupPath,
                    transaction.PreviousSha256,
                    expectedSize: null,
                    cancellationToken)
                .ConfigureAwait(false);
            transaction = SavePhase(transaction, LauncherUpdateTransactionPhase.BackupReady);
            _faultInjector.Hit(LauncherUpdateFaultPoint.AfterBackupCreated, transaction);

            Log(transaction, "swap atomique MoveFileEx(REPLACE_EXISTING|WRITE_THROUGH)");
            await ReplaceWithRetryAsync(
                    transaction,
                    transaction.StagedPath,
                    transaction.TargetPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            swapObserved = true;
            await ValidateHashWithRetryAsync(
                    transaction,
                    transaction.TargetPath,
                    transaction.CandidateSha256,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await ValidateHashWithRetryAsync(
                    transaction,
                    transaction.BackupPath,
                    transaction.PreviousSha256,
                    CancellationToken.None)
                .ConfigureAwait(false);
            transaction = SavePhase(
                transaction,
                LauncherUpdateTransactionPhase.SwappedAwaitingStart);
            _faultInjector.Hit(LauncherUpdateFaultPoint.AfterAtomicSwap, transaction);

            _store.DeleteSignals(transaction);
            _faultInjector.Hit(LauncherUpdateFaultPoint.BeforeNewLauncherStart, transaction);
            Log(transaction, "lancement non élevé du nouveau launcher");
            launchedProcess = await _applicationLauncher.LaunchUpdatedAsync(
                    transaction,
                    _retryPolicy.ProcessStartTimeout,
                    _retryPolicy.SignalPollInterval,
                    CancellationToken.None)
                .ConfigureAwait(false);
            transaction = transaction with
            {
                NewProcessId = launchedProcess.ProcessId,
                Phase = LauncherUpdateTransactionPhase.StartedAwaitingReady,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _store.Save(transaction);
            _faultInjector.Hit(LauncherUpdateFaultPoint.AfterNewLauncherStart, transaction);

            bool ready = await WaitForReadyAsync(
                    transaction,
                    launchedProcess,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!ready)
            {
                throw new InvalidOperationException(
                    launchedProcess.HasExited
                        ? "Le nouveau launcher s'est fermé avant sa confirmation."
                        : "Le nouveau launcher n'a pas confirmé son démarrage.");
            }

            _faultInjector.Hit(LauncherUpdateFaultPoint.AfterReadyConfirmation, transaction);
            transaction = SavePhase(transaction, LauncherUpdateTransactionPhase.Committed);
            _faultInjector.Hit(LauncherUpdateFaultPoint.AfterCommitPersisted, transaction);
            Log(transaction, "transaction confirmée");
            CleanupAfterSuccess(transaction);
            return new LauncherUpdateExecutionResult(
                transaction.TransactionId,
                LauncherUpdateExecutionOutcome.Succeeded,
                LauncherUpdateTransactionPhase.Committed);
        }
        catch (LauncherUpdateSimulatedCrashException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(transaction, "échec: " + SafeErrorCategory(ex));
            if (launchedProcess is { HasExited: false })
            {
                TryKill(launchedProcess);
            }

            bool targetIsCandidate = swapObserved
                || await HasHashAsync(transaction.TargetPath, transaction.CandidateSha256)
                    .ConfigureAwait(false);
            if (targetIsCandidate)
            {
                return await RollbackAsync(transaction, ex, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            CleanupBeforeSwap(transaction);
            if (parentExited)
            {
                await TryRelaunchPreviousAsync(transaction).ConfigureAwait(false);
            }

            return new LauncherUpdateExecutionResult(
                transaction.TransactionId,
                LauncherUpdateExecutionOutcome.PreviousVersionIntact,
                LauncherUpdateTransactionPhase.Failed,
                SafeErrorCategory(ex));
        }
        finally
        {
            launchedProcess?.Dispose();
        }
    }

    internal async Task<LauncherUpdateExecutionResult> RecoverAsync(
        LauncherUpdateTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        Log(transaction, "inspection d'une transaction interrompue");
        bool targetIsPrevious = await HasHashAsync(
                transaction.TargetPath,
                transaction.PreviousSha256)
            .ConfigureAwait(false);
        bool targetIsCandidate = await HasHashAsync(
                transaction.TargetPath,
                transaction.CandidateSha256)
            .ConfigureAwait(false);

        if (targetIsPrevious)
        {
            CleanupBeforeSwap(transaction);
            Log(transaction, "récupération: ancienne version déjà intacte");
            return new LauncherUpdateExecutionResult(
                transaction.TransactionId,
                LauncherUpdateExecutionOutcome.PreviousVersionIntact,
                LauncherUpdateTransactionPhase.RolledBack);
        }

        if (targetIsCandidate)
        {
            if (transaction.Phase == LauncherUpdateTransactionPhase.Committed)
            {
                CleanupAfterSuccess(transaction);
                Log(transaction, "récupération: commit déjà confirmé, nettoyage terminé");
                return new LauncherUpdateExecutionResult(
                    transaction.TransactionId,
                    LauncherUpdateExecutionOutcome.Succeeded,
                    LauncherUpdateTransactionPhase.Committed);
            }

            LauncherUpdateProcessSignal? ready = _store.TryReadReadySignal(transaction);
            if (ready is not null
                && _processMatchesPath(
                    ready.ProcessId,
                    transaction.TargetPath))
            {
                LauncherUpdateTransaction committed = SavePhase(
                    transaction,
                    LauncherUpdateTransactionPhase.Committed);
                CleanupAfterSuccess(committed);
                Log(committed, "récupération: confirmation valide retrouvée");
                return new LauncherUpdateExecutionResult(
                    transaction.TransactionId,
                    LauncherUpdateExecutionOutcome.Succeeded,
                    LauncherUpdateTransactionPhase.Committed);
            }

            _stopProcess(transaction.NewProcessId, transaction.TargetPath);
            return await RollbackAsync(
                    transaction,
                    new InvalidOperationException(
                        "Transaction interrompue avant confirmation du nouveau launcher."),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (await HasHashAsync(transaction.BackupPath, transaction.PreviousSha256)
                .ConfigureAwait(false))
        {
            return await RollbackAsync(
                    transaction,
                    new InvalidDataException("La cible ne correspond à aucune version attendue."),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        LauncherUpdateTransaction failed = SaveFailure(transaction, "NoValidRelease");
        Log(failed, "récupération impossible: aucun binaire valide");
        return new LauncherUpdateExecutionResult(
            transaction.TransactionId,
            LauncherUpdateExecutionOutcome.RecoveryRequired,
            LauncherUpdateTransactionPhase.Failed,
            "NoValidRelease");
    }

    private async Task<LauncherUpdateExecutionResult> RollbackAsync(
        LauncherUpdateTransaction transaction,
        Exception cause,
        CancellationToken cancellationToken)
    {
        LauncherUpdateTransaction rollingBack = SavePhase(
            transaction,
            LauncherUpdateTransactionPhase.RollingBack,
            SafeErrorCategory(cause));
        Log(rollingBack, "rollback vers la version précédente");

        try
        {
            await ValidateHashWithRetryAsync(
                    rollingBack,
                    rollingBack.BackupPath,
                    rollingBack.PreviousSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            await ReplaceWithRetryAsync(
                    rollingBack,
                    rollingBack.BackupPath,
                    rollingBack.TargetPath,
                    cancellationToken)
                .ConfigureAwait(false);
            await ValidateHashWithRetryAsync(
                    rollingBack,
                    rollingBack.TargetPath,
                    rollingBack.PreviousSha256,
                    cancellationToken)
                .ConfigureAwait(false);

            LauncherUpdateTransaction rolledBack = SavePhase(
                rollingBack,
                LauncherUpdateTransactionPhase.RolledBack,
                SafeErrorCategory(cause));
            CleanupAfterRollback(rolledBack);
            Log(rolledBack, "rollback confirmé");
            await TryRelaunchPreviousAsync(rolledBack).ConfigureAwait(false);
            return new LauncherUpdateExecutionResult(
                transaction.TransactionId,
                LauncherUpdateExecutionOutcome.RolledBack,
                LauncherUpdateTransactionPhase.RolledBack,
                SafeErrorCategory(cause));
        }
        catch (Exception rollbackError)
        {
            LauncherUpdateTransaction failed = SaveFailure(
                rollingBack,
                "RollbackFailed");
            Log(failed, "rollback impossible: " + SafeErrorCategory(rollbackError));
            return new LauncherUpdateExecutionResult(
                transaction.TransactionId,
                LauncherUpdateExecutionOutcome.RecoveryRequired,
                LauncherUpdateTransactionPhase.Failed,
                "RollbackFailed");
        }
    }

    private async Task ValidateCandidateAndTargetAsync(
        LauncherUpdateTransaction transaction,
        CancellationToken cancellationToken)
    {
        LauncherUpdateTransaction validated = _store.Load(transaction.TransactionPath);
        if (validated != transaction)
        {
            throw new InvalidDataException("La transaction a changé avant son application.");
        }

        FileInfo candidate = new(transaction.CandidatePath);
        if (!candidate.Exists || candidate.Length != transaction.ExpectedSize)
        {
            throw new InvalidDataException("Taille du candidat launcher invalide.");
        }

        await ValidateHashWithRetryAsync(
                transaction,
                transaction.CandidatePath,
                transaction.CandidateSha256,
                cancellationToken)
            .ConfigureAwait(false);
        await ValidateHashWithRetryAsync(
                transaction,
                transaction.TargetPath,
                transaction.PreviousSha256,
                cancellationToken)
            .ConfigureAwait(false);
        await ValidateHashWithRetryAsync(
                transaction,
                transaction.HelperPath,
                transaction.PreviousSha256,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task CopyAndValidateWithRetryAsync(
        LauncherUpdateTransaction transaction,
        string sourcePath,
        string destinationPath,
        string expectedSha256,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= _retryPolicy.FileAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                LauncherUpdateTransactionStore.TryDeleteFile(destinationPath);
                await CopyFileDurablyAsync(sourcePath, destinationPath, cancellationToken)
                    .ConfigureAwait(false);
                if (expectedSize is > 0
                    && new FileInfo(destinationPath).Length != expectedSize.Value)
                {
                    throw new InvalidDataException("Taille copiée invalide.");
                }

                await ValidateHashAsync(destinationPath, expectedSha256, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException)
            {
                lastError = ex;
                LogRetry(transaction, destinationPath, attempt, ex);
                LauncherUpdateTransactionStore.TryDeleteFile(destinationPath);
                if (attempt < _retryPolicy.FileAttempts)
                {
                    await _delayAsync(_retryPolicy.FileRetryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            "Impossible de préparer un fichier de mise à jour après plusieurs tentatives.",
            lastError);
    }

    private async Task ReplaceWithRetryAsync(
        LauncherUpdateTransaction transaction,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= _retryPolicy.FileAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _atomicMover.Replace(sourcePath, destinationPath);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                LogRetry(transaction, destinationPath, attempt, ex);
                if (attempt < _retryPolicy.FileAttempts)
                {
                    await _delayAsync(_retryPolicy.FileRetryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            "Impossible d'effectuer le remplacement atomique après plusieurs tentatives.",
            lastError);
    }

    private async Task<bool> WaitForReadyAsync(
        LauncherUpdateTransaction transaction,
        ILauncherUpdateLaunchedProcess process,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < _retryPolicy.ReadyTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LauncherUpdateProcessSignal? ready = _store.TryReadReadySignal(transaction);
            if (ready is not null
                && ready.ProcessId == process.ProcessId
                && !ready.IsElevated
                && !process.HasExited)
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            await _delayAsync(_retryPolicy.SignalPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private static async Task CopyFileDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken)
            .ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task ValidateHashAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Fichier de mise à jour absent.", path);
        }

        string actual = await LauncherUpdateTransactionStore.ComputeSha256Async(
                path,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "L'empreinte d'un fichier de mise à jour est invalide.");
        }
    }

    private async Task ValidateHashWithRetryAsync(
        LauncherUpdateTransaction transaction,
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= _retryPolicy.FileAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ValidateHashAsync(path, expectedSha256, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException)
            {
                lastError = ex;
                LogRetry(transaction, path, attempt, ex);
                if (attempt < _retryPolicy.FileAttempts)
                {
                    await _delayAsync(_retryPolicy.FileRetryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            "Impossible de valider un fichier de mise à jour après plusieurs tentatives.",
            lastError);
    }

    private static async Task<bool> HasHashAsync(string path, string expectedSha256)
    {
        try
        {
            return File.Exists(path)
                && string.Equals(
                    await LauncherUpdateTransactionStore.ComputeSha256Async(
                            path,
                            CancellationToken.None)
                        .ConfigureAwait(false),
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private LauncherUpdateTransaction SavePhase(
        LauncherUpdateTransaction transaction,
        LauncherUpdateTransactionPhase phase,
        string? failureCategory = null)
    {
        LauncherUpdateTransaction updated = transaction with
        {
            Phase = phase,
            FailureCategory = failureCategory,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _store.Save(updated);
        return updated;
    }

    private LauncherUpdateTransaction SaveFailure(
        LauncherUpdateTransaction transaction,
        string category) =>
        SavePhase(transaction, LauncherUpdateTransactionPhase.Failed, category);

    private void CleanupBeforeSwap(LauncherUpdateTransaction transaction)
    {
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.StagedPath);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.BackupPath);
        _store.DeleteSignals(transaction);
        SaveFailure(transaction, transaction.FailureCategory ?? "BeforeSwap");
    }

    private void CleanupAfterSuccess(LauncherUpdateTransaction transaction)
    {
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.StagedPath);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.BackupPath);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.CandidatePath);
        _store.DeleteSignals(transaction);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.TransactionPath);
    }

    private void CleanupAfterRollback(LauncherUpdateTransaction transaction)
    {
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.StagedPath);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.BackupPath);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.CandidatePath);
        _store.DeleteSignals(transaction);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.TransactionPath);
    }

    private async Task TryRelaunchPreviousAsync(LauncherUpdateTransaction transaction)
    {
        try
        {
            await _applicationLauncher.LaunchRollbackAsync(
                    transaction,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Log(transaction, "ancienne version relancée");
        }
        catch (Exception ex)
        {
            Log(transaction, "relance de l'ancienne version impossible: " + SafeErrorCategory(ex));
        }
    }

    private static void TryKill(ILauncherUpdateLaunchedProcess process)
    {
        try
        {
            process.Kill();
        }
        catch
        {
        }
    }

    private static string SafeErrorCategory(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "PermissionDenied",
        FileNotFoundException => "FileMissing",
        InvalidDataException => "ValidationFailed",
        IOException => "FileLockedOrIoFailure",
        OperationCanceledException => "Cancelled",
        _ => exception.GetType().Name
    };

    private static void Log(LauncherUpdateTransaction transaction, string message)
    {
        LauncherUpdateJournal.Append(transaction, message);
    }

    private static void LogRetry(
        LauncherUpdateTransaction transaction,
        string path,
        int attempt,
        Exception exception)
    {
        string message =
            $"retry={attempt} file={Path.GetFileName(path)} category={exception.GetType().Name}";
        Debug.WriteLine("Launcher update " + message);
        LauncherUpdateJournal.Append(transaction, message);
    }
}

internal static class LauncherUpdateJournal
{
    private static readonly object Sync = new();

    internal static void Append(LauncherUpdateTransaction transaction, string message)
    {
        string line =
            $"[{DateTimeOffset.Now:O}] transaction={transaction.TransactionId:N} " +
            $"phase={transaction.Phase} {message}{Environment.NewLine}";
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(transaction.WorkspacePath);
                File.AppendAllText(
                    Path.Combine(transaction.WorkspacePath, "updater.log"),
                    line,
                    new System.Text.UTF8Encoding(false));

                string? transactionsDirectory = Path.GetDirectoryName(transaction.WorkspacePath);
                string? selfUpdateDirectory = transactionsDirectory is null
                    ? null
                    : Path.GetDirectoryName(transactionsDirectory);
                if (selfUpdateDirectory is not null)
                {
                    Directory.CreateDirectory(selfUpdateDirectory);
                    File.AppendAllText(
                        Path.Combine(selfUpdateDirectory, "updater.log"),
                        line,
                        new System.Text.UTF8Encoding(false));
                }
            }
        }
        catch
        {
            Debug.WriteLine(line);
        }
    }
}
