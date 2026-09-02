using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WotLK.Launcher.Updater;

internal static class LauncherUpdateCommandLine
{
    internal const string ApplySwitch = "--atlas-self-update-apply";
    internal const string RecoverSwitch = "--atlas-self-update-recover";
    private const string PostUpdatePrefix = "--atlas-post-update-ready=";

    internal static string BuildPostUpdateArgument(Guid transactionId) =>
        PostUpdatePrefix + transactionId.ToString("N");

    internal static bool TryParseHelper(
        IReadOnlyList<string> arguments,
        out bool recovery,
        out string transactionPath,
        out int requesterProcessId)
    {
        recovery = false;
        transactionPath = string.Empty;
        requesterProcessId = 0;
        if (arguments.Count != 3
            || !int.TryParse(
                arguments[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out requesterProcessId)
            || requesterProcessId <= 0)
        {
            return false;
        }

        if (string.Equals(arguments[0], ApplySwitch, StringComparison.OrdinalIgnoreCase))
        {
            transactionPath = arguments[1];
            return true;
        }

        if (string.Equals(arguments[0], RecoverSwitch, StringComparison.OrdinalIgnoreCase))
        {
            recovery = true;
            transactionPath = arguments[1];
            return true;
        }

        return false;
    }

    internal static Guid? FindPostUpdateTransaction(IEnumerable<string> arguments)
    {
        foreach (string argument in arguments)
        {
            if (argument.StartsWith(PostUpdatePrefix, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(argument[PostUpdatePrefix.Length..], "N", out Guid id))
            {
                return id;
            }
        }

        return null;
    }

    internal static string[] ApplicationArguments(IEnumerable<string> arguments) =>
        arguments
            .Where(argument =>
                !argument.StartsWith(PostUpdatePrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
}

internal static class LauncherUpdateHelperRunner
{
    internal static int Run(
        bool recovery,
        string transactionPath,
        int requesterProcessId)
    {
        LauncherUpdateTransactionStore store = new(LauncherUpdatePaths.TransactionsRoot);
        LauncherUpdateTransaction? transaction = null;
        try
        {
            transaction = store.Load(transactionPath);
            string currentExecutable = Path.GetFullPath(
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Exécutable helper introuvable."));
            if (!SamePath(currentExecutable, transaction.HelperPath))
            {
                throw new InvalidDataException("Helper de mise à jour non autorisé.");
            }

            if (OperatingSystem.IsWindows()
                && !LauncherUpdateSecurity.IsCurrentProcessElevated())
            {
                throw new UnauthorizedAccessException(
                    "Le helper de mise à jour doit être élevé.");
            }

            ValidateRequester(
                recovery,
                transaction,
                requesterProcessId,
                LauncherUpdateParentWaiter.ProcessMatchesPath);

            string helperHash = LauncherUpdateTransactionStore.ComputeSha256Async(
                    currentExecutable,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!string.Equals(
                    helperHash,
                    transaction.PreviousSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Helper de mise à jour invalide.");
            }

            store.WriteHelperAcceptedSignal(
                transaction,
                new LauncherUpdateProcessSignal(
                    transaction.TransactionId,
                    Environment.ProcessId,
                    IsElevated: LauncherUpdateSecurity.IsCurrentProcessElevated(),
                    DateTimeOffset.UtcNow));
            LauncherUpdateJournal.Append(
                transaction,
                $"helper accepté par requesterPid={requesterProcessId}");

            LauncherAtomicReplacementService service = new(
                store,
                new WindowsLauncherAtomicFileMover(),
                new LauncherUpdateParentWaiter(),
                new WindowsLauncherUpdateApplicationLauncher(store));
            LauncherUpdateExecutionResult result = recovery
                ? service.RecoverAsync(transaction).GetAwaiter().GetResult()
                : service.ApplyAsync(transaction).GetAwaiter().GetResult();
            LauncherUpdateJournal.Append(
                transaction,
                $"helper terminé: outcome={result.Outcome} category={result.FailureCategory ?? "none"}");

            if (result.Outcome != LauncherUpdateExecutionOutcome.RecoveryRequired)
            {
                LauncherUpdateWorkspaceCleanup.Schedule(transaction);
            }

            return result.Outcome is LauncherUpdateExecutionOutcome.Succeeded
                or LauncherUpdateExecutionOutcome.RolledBack
                or LauncherUpdateExecutionOutcome.PreviousVersionIntact
                ? 0
                : 1;
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                LauncherUpdateJournal.Append(
                    transaction,
                    "helper fatal: " + ex.GetType().Name);
            }

            return 1;
        }
    }

    internal static void ValidateRequester(
        bool recovery,
        LauncherUpdateTransaction transaction,
        int requesterProcessId,
        Func<int, string, bool> processMatchesPath)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(processMatchesPath);
        if (requesterProcessId <= 0
            || !processMatchesPath(requesterProcessId, transaction.TargetPath)
            || !recovery && requesterProcessId != transaction.ParentProcessId)
        {
            throw new InvalidDataException(
                "Le processus demandeur ne correspond pas au launcher attendu.");
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

internal sealed class LauncherUpdateStartupSession
{
    private static readonly TimeSpan StabilityDelay = TimeSpan.FromSeconds(2);
    private readonly LauncherUpdateTransactionStore _store;
    private readonly ILauncherUpdateHelperLauncher _helperLauncher;
    private readonly LauncherUpdateTransaction? _explicitTransaction;
    private readonly IReadOnlyList<LauncherUpdateTransaction> _interruptedTransactions;
    private int _completionState;

    internal bool RecoveryOccurred => _interruptedTransactions.Count > 0;

    private LauncherUpdateStartupSession(
        LauncherUpdateTransactionStore store,
        ILauncherUpdateHelperLauncher helperLauncher,
        LauncherUpdateTransaction? explicitTransaction,
        IReadOnlyList<LauncherUpdateTransaction> interruptedTransactions)
    {
        _store = store;
        _helperLauncher = helperLauncher;
        _explicitTransaction = explicitTransaction;
        _interruptedTransactions = interruptedTransactions;
    }

    internal static LauncherUpdateStartupSession Begin(
        IEnumerable<string> arguments,
        bool recoverInterruptedTransactions)
    {
        LauncherUpdateTransactionStore store = new();
        string? currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable))
        {
            return new LauncherUpdateStartupSession(
                store,
                new WindowsLauncherUpdateHelperLauncher(store),
                null,
                []);
        }

        string target = Path.GetFullPath(currentExecutable);
        Guid? explicitId = LauncherUpdateCommandLine.FindPostUpdateTransaction(arguments);
        LauncherUpdateTransaction? explicitTransaction = explicitId is Guid id
            ? TryLoadTransaction(store, id, target)
            : null;
        if (explicitTransaction is not null)
        {
            store.WriteStartedSignal(
                explicitTransaction,
                CreateSignal(explicitTransaction.TransactionId));
        }

        IReadOnlyList<LauncherUpdateTransaction> interrupted = recoverInterruptedTransactions
            ? LoadInterruptedTransactions(store, target, explicitId)
            : [];
        CleanupEmptyTransactionDirectories();
        return new LauncherUpdateStartupSession(
            store,
            new WindowsLauncherUpdateHelperLauncher(store),
            explicitTransaction,
            interrupted);
    }

    internal async Task ConfirmReadyAsync(
        Func<bool> isApplicationStillReady,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _completionState, 1) != 0)
        {
            return;
        }

        await Task.Delay(StabilityDelay, cancellationToken).ConfigureAwait(false);
        if (!isApplicationStillReady())
        {
            return;
        }

        LauncherUpdateProcessSignal? signal = null;
        if (_explicitTransaction is not null)
        {
            signal = CreateSignal(_explicitTransaction.TransactionId);
            _store.WriteReadySignal(_explicitTransaction, signal);
            LauncherUpdateJournal.Append(
                _explicitTransaction,
                "handshake Ready émis après stabilisation WPF");
        }

        foreach (LauncherUpdateTransaction transaction in _interruptedTransactions)
        {
            try
            {
                string? currentHash = await TryComputeHashAsync(transaction.TargetPath)
                    .ConfigureAwait(false);
                if (string.Equals(
                        currentHash,
                        transaction.CandidateSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    LauncherUpdateProcessSignal recoverySignal =
                        signal is not null
                        && signal.TransactionId == transaction.TransactionId
                            ? signal
                            : CreateSignal(transaction.TransactionId);
                    _store.WriteStartedSignal(transaction, recoverySignal);
                    _store.WriteReadySignal(transaction, recoverySignal);
                }

                await _helperLauncher.LaunchRecoveryAsync(
                        transaction,
                        Environment.ProcessId,
                        cancellationToken)
                    .ConfigureAwait(false);
                LauncherUpdateJournal.Append(
                    transaction,
                    "helper de récupération relancé au démarrage suivant");
            }
            catch (Exception ex)
            {
                LauncherUpdateJournal.Append(
                    transaction,
                    "récupération différée: " + ex.GetType().Name);
            }
        }
    }

    private static LauncherUpdateTransaction? TryLoadTransaction(
        LauncherUpdateTransactionStore store,
        Guid transactionId,
        string targetPath)
    {
        try
        {
            string transactionPath = Path.Combine(
                LauncherUpdatePaths.TransactionsRoot,
                transactionId.ToString("N"),
                "transaction.json");
            LauncherUpdateTransaction transaction = store.Load(transactionPath);
            return SamePath(transaction.TargetPath, targetPath)
                ? transaction
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<LauncherUpdateTransaction> LoadInterruptedTransactions(
        LauncherUpdateTransactionStore store,
        string targetPath,
        Guid? excludedId)
    {
        if (!Directory.Exists(LauncherUpdatePaths.TransactionsRoot))
        {
            return [];
        }

        List<LauncherUpdateTransaction> transactions = [];
        foreach (string directory in Directory.EnumerateDirectories(
                     LauncherUpdatePaths.TransactionsRoot))
        {
            try
            {
                LauncherUpdateTransaction transaction = store.Load(
                    Path.Combine(directory, "transaction.json"));
                if (transaction.TransactionId != excludedId
                    && SamePath(transaction.TargetPath, targetPath))
                {
                    transactions.Add(transaction);
                }
            }
            catch
            {
            }
        }

        return transactions;
    }

    private static LauncherUpdateProcessSignal CreateSignal(Guid transactionId) => new(
        transactionId,
        Environment.ProcessId,
        LauncherUpdateSecurity.IsCurrentProcessElevated(),
        DateTimeOffset.UtcNow);

    private static async Task<string?> TryComputeHashAsync(string path)
    {
        try
        {
            return await LauncherUpdateTransactionStore.ComputeSha256Async(
                    path,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void CleanupEmptyTransactionDirectories()
    {
        try
        {
            if (!Directory.Exists(LauncherUpdatePaths.TransactionsRoot))
            {
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(
                         LauncherUpdatePaths.TransactionsRoot))
            {
                if (!File.Exists(Path.Combine(directory, "transaction.json")))
                {
                    LauncherUpdateTransactionStore.TryDeleteDirectory(directory);
                }
            }
        }
        catch
        {
        }
    }
}

internal static class LauncherUpdateWorkspaceCleanup
{
    private const uint MoveFileDelayUntilReboot = 0x00000004;

    internal static void Schedule(LauncherUpdateTransaction transaction)
    {
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.CandidatePath);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.StartedSignalPath);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.ReadySignalPath);
        LauncherUpdateTransactionStore.TryDeleteFile(transaction.TransactionPath);
        LauncherUpdateTransactionStore.TryDeleteFile(
            Path.Combine(transaction.WorkspacePath, "updater.log"));

        if (OperatingSystem.IsWindows())
        {
            MoveFileEx(transaction.HelperPath, null, MoveFileDelayUntilReboot);
        }
        else
        {
            LauncherUpdateTransactionStore.TryDeleteFile(transaction.HelperPath);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string? newFileName,
        uint flags);
}
