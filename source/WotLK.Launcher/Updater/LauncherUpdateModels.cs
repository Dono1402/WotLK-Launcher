namespace WotLK.Launcher.Updater;

internal enum LauncherUpdateTransactionPhase
{
    Prepared,
    CandidateStaged,
    BackupReady,
    SwappedAwaitingStart,
    StartedAwaitingReady,
    Committed,
    RollingBack,
    RolledBack,
    Failed
}

internal enum LauncherUpdateFaultPoint
{
    BeforeCandidateValidation,
    AfterCandidateValidation,
    AfterCandidateStaged,
    AfterBackupCreated,
    AfterAtomicSwap,
    BeforeNewLauncherStart,
    AfterNewLauncherStart,
    AfterReadyConfirmation,
    AfterCommitPersisted
}

internal enum LauncherUpdateExecutionOutcome
{
    Succeeded,
    RolledBack,
    PreviousVersionIntact,
    RecoveryRequired
}

internal sealed record LauncherUpdateTransaction(
    int SchemaVersion,
    Guid TransactionId,
    int ParentProcessId,
    string TargetPath,
    string WorkspacePath,
    string CandidatePath,
    string HelperPath,
    string StagedPath,
    string BackupPath,
    string TransactionPath,
    string HelperAcceptedSignalPath,
    string StartedSignalPath,
    string ReadySignalPath,
    long ExpectedSize,
    string PreviousSha256,
    string CandidateSha256,
    LauncherUpdateTransactionPhase Phase,
    DateTimeOffset UpdatedAt,
    int? NewProcessId = null,
    string? FailureCategory = null)
{
    internal const int CurrentSchemaVersion = 1;
}

internal sealed record LauncherUpdateProcessSignal(
    Guid TransactionId,
    int ProcessId,
    bool IsElevated,
    DateTimeOffset CreatedAt);

internal sealed record LauncherUpdateExecutionResult(
    Guid TransactionId,
    LauncherUpdateExecutionOutcome Outcome,
    LauncherUpdateTransactionPhase FinalPhase,
    string? FailureCategory = null);

internal sealed record LauncherUpdateRetryPolicy(
    int FileAttempts,
    TimeSpan FileRetryDelay,
    TimeSpan ParentExitTimeout,
    TimeSpan ProcessStartTimeout,
    TimeSpan ReadyTimeout,
    TimeSpan SignalPollInterval)
{
    internal static LauncherUpdateRetryPolicy Production { get; } = new(
        FileAttempts: 20,
        FileRetryDelay: TimeSpan.FromMilliseconds(300),
        ParentExitTimeout: TimeSpan.FromSeconds(45),
        ProcessStartTimeout: TimeSpan.FromSeconds(10),
        ReadyTimeout: TimeSpan.FromSeconds(25),
        SignalPollInterval: TimeSpan.FromMilliseconds(100));
}

internal sealed class LauncherUpdateSimulatedCrashException(
    LauncherUpdateFaultPoint faultPoint) : Exception(
        $"Simulated launcher update crash at {faultPoint}.")
{
    internal LauncherUpdateFaultPoint FaultPoint { get; } = faultPoint;
}

internal interface ILauncherUpdateFaultInjector
{
    void Hit(LauncherUpdateFaultPoint point, LauncherUpdateTransaction transaction);
}

internal sealed class NullLauncherUpdateFaultInjector : ILauncherUpdateFaultInjector
{
    internal static NullLauncherUpdateFaultInjector Instance { get; } = new();

    private NullLauncherUpdateFaultInjector()
    {
    }

    public void Hit(LauncherUpdateFaultPoint point, LauncherUpdateTransaction transaction)
    {
    }
}

internal interface ILauncherUpdateParentWaiter
{
    Task<bool> WaitForExitAsync(
        int processId,
        string expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface ILauncherUpdateLaunchedProcess : IDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    void Kill();
}

internal interface ILauncherUpdateApplicationLauncher
{
    Task<ILauncherUpdateLaunchedProcess> LaunchUpdatedAsync(
        LauncherUpdateTransaction transaction,
        TimeSpan startTimeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken);

    Task LaunchRollbackAsync(
        LauncherUpdateTransaction transaction,
        CancellationToken cancellationToken);
}

internal interface ILauncherAtomicFileMover
{
    void Replace(string sourcePath, string destinationPath);
}

internal interface ILauncherSelfUpdateFinalizer
{
    Task<LauncherUpdateTransaction> PrepareAndLaunchAsync(
        string targetPath,
        string downloadedCandidatePath,
        long expectedSize,
        string expectedSha256,
        int parentProcessId,
        CancellationToken cancellationToken);
}

internal interface ILauncherUpdateHelperLauncher
{
    Task LaunchApplyAsync(
        LauncherUpdateTransaction transaction,
        CancellationToken cancellationToken);

    Task LaunchRecoveryAsync(
        LauncherUpdateTransaction transaction,
        int requesterProcessId,
        CancellationToken cancellationToken);
}
