using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.Updater;

internal static class LauncherUpdateCommandLine
{
    internal const string ApplySwitch = "--atlas-self-update-apply";
    internal const string RecoverSwitch = "--atlas-self-update-recover";
    internal const string BootstrapSwitch = "--atlas-self-update-bootstrap";
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

    internal static bool TryParseBootstrap(
        IReadOnlyList<string> arguments,
        out string transactionPath,
        out int requesterProcessId)
    {
        transactionPath = string.Empty;
        requesterProcessId = 0;
        if (arguments.Count != 3
            || !string.Equals(
                arguments[0],
                BootstrapSwitch,
                StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(
                arguments[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out requesterProcessId)
            || requesterProcessId <= 0)
        {
            return false;
        }

        transactionPath = arguments[1];
        return true;
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

    internal static bool HasPostUpdateMarker(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.StartsWith(
            PostUpdatePrefix,
            StringComparison.OrdinalIgnoreCase));

    internal static string[] ApplicationArguments(IEnumerable<string> arguments) =>
        arguments
            .Where(argument =>
                !argument.StartsWith(PostUpdatePrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
}

internal static class LauncherUpdateBootstrapRunner
{
    internal static int Run(string transactionPath, int requesterProcessId)
    {
        LauncherUpdateRequesterImpersonation? requester = null;
        LauncherUpdateTransactionStore? store = null;
        LauncherUpdateTransaction? transaction = null;
        try
        {
            requester = LauncherUpdateRequesterImpersonation.Capture(requesterProcessId);
            store = new LauncherUpdateTransactionStore(
                requester.TransactionsRoot,
                requester);
            transaction = store.Load(transactionPath);
            string currentExecutable = Path.GetFullPath(
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Executable bootstrap introuvable."));
            if (!SamePath(currentExecutable, transaction.TargetPath)
                || OperatingSystem.IsWindows()
                   && !LauncherUpdateSecurity.IsCurrentProcessElevated())
            {
                throw new UnauthorizedAccessException("Bootstrap de mise à jour non autorisé.");
            }

            requester.DemandMatchesRequester(
                requesterProcessId,
                transaction.TargetPath);
            if (requesterProcessId != transaction.ParentProcessId)
            {
                throw new InvalidDataException(
                    "Le processus demandeur ne correspond pas au parent de la transaction.");
            }

            requester.Run(() =>
                LauncherUpdateElevationSecurity.DemandProtectedTargetForElevation(
                    transaction));
            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(currentExecutable);
            LauncherUpdateAuthenticatedPayload.ValidateProduction(transaction, store);

            string currentHash = LauncherUpdateTransactionStore.ComputeSha256Async(
                    currentExecutable,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!string.Equals(
                    currentHash,
                    transaction.PreviousSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Bootstrap de mise à jour modifié.");
            }

            DemandStrictlyNewerVersion(
                currentExecutable,
                transaction.AuthenticatedManifest!.Version);

            string helperDirectory = Path.GetDirectoryName(transaction.HelperPath)
                ?? throw new InvalidDataException("Dossier du helper protégé absent.");
            Directory.CreateDirectory(helperDirectory);
            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(helperDirectory);
            LauncherUpdateTransactionStore.TryDeleteFile(
                transaction.HelperAcceptedSignalPath);
            File.Copy(currentExecutable, transaction.HelperPath, overwrite: false);
            LauncherUpdateElevationSecurity.DemandNoReparseTransactionPaths(transaction);
            string helperHash = LauncherUpdateTransactionStore.ComputeSha256Async(
                    transaction.HelperPath,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!string.Equals(
                    helperHash,
                    transaction.PreviousSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Copie du helper protégé invalide.");
            }

            requester.Run(() =>
                LauncherUpdateElevationSecurity.DemandProtectedHelperForElevation(
                    transaction));

            ProcessStartInfo startInfo = new()
            {
                FileName = transaction.HelperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = helperDirectory
            };
            startInfo.ArgumentList.Add(LauncherUpdateCommandLine.ApplySwitch);
            startInfo.ArgumentList.Add(transaction.TransactionPath);
            startInfo.ArgumentList.Add(requesterProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Impossible de démarrer le helper protégé de mise à jour.");
            store.AppendJournal(
                transaction,
                $"bootstrap protégé transmis au helper pid={process.Id}");
            return 0;
        }
        catch (Exception ex)
        {
            if (transaction is not null && store is not null)
            {
                store.AppendJournal(
                    transaction,
                    "bootstrap fatal: " + ex.GetType().Name);
            }
            else
            {
                Debug.WriteLine("Launcher update bootstrap fatal: " + ex.GetType().Name);
            }

            return 1;
        }
        finally
        {
            requester?.Dispose();
        }
    }

    internal static void DemandStrictlyNewerVersion(
        string currentExecutable,
        string authenticatedTargetVersion)
    {
        string? currentVersionText = FileVersionInfo
            .GetVersionInfo(currentExecutable)
            .FileVersion;
        if (!Version.TryParse(currentVersionText, out Version? currentVersion)
            || !Version.TryParse(authenticatedTargetVersion, out Version? targetVersion)
            || Normalize(targetVersion) <= Normalize(currentVersion))
        {
            throw new InvalidDataException(
                "La version signée ne constitue pas une mise à niveau stricte.");
        }
    }

    private static Version Normalize(Version version) => new(
        Math.Max(version.Major, 0),
        Math.Max(version.Minor, 0),
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

internal static class LauncherUpdateAuthenticatedPayload
{
    internal static void ValidateProduction(
        LauncherUpdateTransaction transaction,
        LauncherUpdateTransactionStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Validate(
            transaction,
            new LauncherUpdateManifestVerifier(
                LauncherUpdateTrustStore.LoadEmbeddedProduction()),
            store);
    }

    internal static void Validate(
        LauncherUpdateTransaction transaction,
        ILauncherUpdateManifestVerifier verifier,
        LauncherUpdateTransactionStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(verifier);
        LauncherUpdateManifest manifest = transaction.AuthenticatedManifest
            ?? throw new InvalidDataException("Preuve signée de mise à jour absente.");
        verifier.Verify(manifest);
        if (!string.Equals(
                manifest.Version,
                transaction.AuthenticatedTargetVersion,
                StringComparison.Ordinal)
            || manifest.Size != transaction.ExpectedSize
            || !string.Equals(
                manifest.Sha256,
                transaction.CandidateSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Preuve signée de mise à jour incohérente.");
        }

        using FileStream candidate = store is null
            ? new FileStream(
                transaction.CandidatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan)
            : store.OpenUserFileForStableRead(transaction.CandidatePath);
        if (candidate.Length != manifest.Size)
        {
            throw new LauncherUpdatePackageIntegrityException();
        }

        byte[] actual = SHA256.HashData(candidate);
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(manifest.Sha256);
        }
        catch (FormatException)
        {
            throw new LauncherUpdatePackageIntegrityException();
        }

        if (expected.Length != actual.Length
            || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new LauncherUpdatePackageIntegrityException();
        }
    }
}

internal static class LauncherUpdateHelperRunner
{
    internal static int Run(
        bool recovery,
        string transactionPath,
        int requesterProcessId)
    {
        LauncherUpdateRequesterImpersonation? requester = null;
        LauncherUpdateTransactionStore? store = null;
        LauncherUpdateTransaction? transaction = null;
        try
        {
            requester = LauncherUpdateRequesterImpersonation.Capture(requesterProcessId);
            store = new LauncherUpdateTransactionStore(
                requester.TransactionsRoot,
                requester);
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

            requester.DemandMatchesRequester(
                requesterProcessId,
                transaction.TargetPath);
            if (!recovery && requesterProcessId != transaction.ParentProcessId)
            {
                throw new InvalidDataException(
                    "Le processus demandeur ne correspond pas au parent de la transaction.");
            }

            requester.Run(() =>
                LauncherUpdateElevationSecurity.DemandProtectedHelperForElevation(
                    transaction));
            if (!recovery)
            {
                requester.Run(() =>
                    LauncherUpdateElevationSecurity.DemandProtectedTargetForElevation(
                        transaction));
            }
            ValidateAuthenticatedUpgradeProduction(
                transaction,
                currentExecutable,
                store);

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
            store.AppendJournal(
                transaction,
                $"helper accepté par requesterPid={requesterProcessId}");

            LauncherAtomicReplacementService service = new(
                store,
                new WindowsLauncherAtomicFileMover(),
                new LauncherUpdateParentWaiter(),
                new WindowsLauncherUpdateApplicationLauncher(
                    store,
                    requester: requester),
                protectedFileValidator:
                    store.DemandProtectedFileForElevation);
            LauncherUpdateExecutionResult result = recovery
                ? service.RecoverAsync(transaction).GetAwaiter().GetResult()
                : service.ApplyAsync(transaction).GetAwaiter().GetResult();
            store.AppendJournal(
                transaction,
                $"helper terminé: outcome={result.Outcome} category={result.FailureCategory ?? "none"}");

            if (result.Outcome != LauncherUpdateExecutionOutcome.RecoveryRequired)
            {
                LauncherUpdateWorkspaceCleanup.Schedule(transaction, store);
            }

            return result.Outcome is LauncherUpdateExecutionOutcome.Succeeded
                or LauncherUpdateExecutionOutcome.RolledBack
                or LauncherUpdateExecutionOutcome.PreviousVersionIntact
                ? 0
                : 1;
        }
        catch (Exception ex)
        {
            if (transaction is not null && store is not null)
            {
                store.AppendJournal(
                    transaction,
                    "helper fatal: " + ex.GetType().Name);
            }
            else
            {
                Debug.WriteLine("Launcher update helper fatal: " + ex.GetType().Name);
            }

            return 1;
        }
        finally
        {
            requester?.Dispose();
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

    internal static void ValidateAuthenticatedUpgrade(
        LauncherUpdateTransaction transaction,
        string currentExecutable,
        ILauncherUpdateManifestVerifier verifier,
        LauncherUpdateTransactionStore? store = null)
    {
        LauncherUpdateAuthenticatedPayload.Validate(transaction, verifier, store);
        LauncherUpdateBootstrapRunner.DemandStrictlyNewerVersion(
            currentExecutable,
            transaction.AuthenticatedManifest!.Version);
    }

    private static void ValidateAuthenticatedUpgradeProduction(
        LauncherUpdateTransaction transaction,
        string currentExecutable,
        LauncherUpdateTransactionStore store)
    {
        LauncherUpdateAuthenticatedPayload.ValidateProduction(transaction, store);
        LauncherUpdateBootstrapRunner.DemandStrictlyNewerVersion(
            currentExecutable,
            transaction.AuthenticatedManifest!.Version);
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
    private readonly bool _explicitUpdateMarkerPresent;
    private int _completionState;

    internal bool RecoveryOccurred => _interruptedTransactions.Count > 0;

    internal bool HasPendingTransactions => _explicitUpdateMarkerPresent
        || _interruptedTransactions.Count > 0;

    private LauncherUpdateStartupSession(
        LauncherUpdateTransactionStore store,
        ILauncherUpdateHelperLauncher helperLauncher,
        LauncherUpdateTransaction? explicitTransaction,
        IReadOnlyList<LauncherUpdateTransaction> interruptedTransactions,
        bool explicitUpdateMarkerPresent)
    {
        _store = store;
        _helperLauncher = helperLauncher;
        _explicitTransaction = explicitTransaction;
        _interruptedTransactions = interruptedTransactions;
        _explicitUpdateMarkerPresent = explicitUpdateMarkerPresent;
    }

    internal static LauncherUpdateStartupSession Begin(
        IEnumerable<string> arguments,
        bool recoverInterruptedTransactions)
    {
        return BeginCore(
            arguments,
            recoverInterruptedTransactions,
            new LauncherUpdateTransactionStore(),
            Environment.ProcessPath,
            cleanupEmptyTransactionDirectories: true);
    }

    internal static LauncherUpdateStartupSession BeginForTests(
        IEnumerable<string> arguments,
        bool recoverInterruptedTransactions,
        LauncherUpdateTransactionStore store,
        string currentExecutable)
    {
        ArgumentNullException.ThrowIfNull(store);
        return BeginCore(
            arguments,
            recoverInterruptedTransactions,
            store,
            currentExecutable,
            cleanupEmptyTransactionDirectories: false);
    }

    private static LauncherUpdateStartupSession BeginCore(
        IEnumerable<string> arguments,
        bool recoverInterruptedTransactions,
        LauncherUpdateTransactionStore store,
        string? currentExecutable,
        bool cleanupEmptyTransactionDirectories)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] argumentArray = arguments as string[] ?? arguments.ToArray();
        bool explicitMarkerPresent =
            LauncherUpdateCommandLine.HasPostUpdateMarker(argumentArray);
        Guid? explicitId =
            LauncherUpdateCommandLine.FindPostUpdateTransaction(argumentArray);
        if (string.IsNullOrWhiteSpace(currentExecutable))
        {
            return new LauncherUpdateStartupSession(
                store,
                new WindowsLauncherUpdateHelperLauncher(store),
                null,
                [],
                explicitMarkerPresent);
        }

        string target = Path.GetFullPath(currentExecutable);
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
        if (cleanupEmptyTransactionDirectories)
        {
            CleanupEmptyTransactionDirectories(store.TransactionsRoot);
        }
        return new LauncherUpdateStartupSession(
            store,
            new WindowsLauncherUpdateHelperLauncher(store),
            explicitTransaction,
            interrupted,
            explicitMarkerPresent);
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
            _store.AppendJournal(
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
                _store.AppendJournal(
                    transaction,
                    "helper de récupération relancé au démarrage suivant");
            }
            catch (Exception ex)
            {
                _store.AppendJournal(
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
                store.TransactionsRoot,
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
        if (!Directory.Exists(store.TransactionsRoot))
        {
            return [];
        }

        List<LauncherUpdateTransaction> transactions = [];
        foreach (string directory in Directory.EnumerateDirectories(
                     store.TransactionsRoot))
        {
            try
            {
                LauncherUpdateTransaction transaction = store.Load(
                    Path.Combine(directory, "transaction.json"));
                if (transaction.TransactionId != excludedId
                    && SamePath(transaction.TargetPath, targetPath)
                    && !(transaction.Phase == LauncherUpdateTransactionPhase.Failed
                         && string.Equals(
                             transaction.FailureCategory,
                             "UnexpectedTarget",
                             StringComparison.Ordinal)))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        LauncherUpdateElevationSecurity
                            .DemandProtectedHelperForElevation(transaction);
                    }

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

    private static void CleanupEmptyTransactionDirectories(string transactionsRoot)
    {
        try
        {
            if (!Directory.Exists(transactionsRoot))
            {
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(
                         transactionsRoot))
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

    internal static void Schedule(
        LauncherUpdateTransaction transaction,
        LauncherUpdateTransactionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.TryDeleteUserFile(transaction.CandidatePath);
        store.DeleteSignals(transaction);
        store.TryDeleteUserFile(transaction.TransactionPath);
        store.TryDeleteUserFile(
            Path.Combine(transaction.WorkspacePath, "updater.log"));
        if (store.UserFileExists(transaction.TransactionPath))
        {
            // Keep the protected helper and commit proof available for a later
            // recovery if the user-scoped workspace could not be cleaned.
            return;
        }

        LauncherUpdateTransactionStore.TryDeleteFile(
            LauncherUpdateElevationSecurity.GetProtectedCommitSignalPath(
                transaction.TargetPath,
                transaction.TransactionId));

        if (OperatingSystem.IsWindows())
        {
            MoveFileEx(transaction.HelperPath, null, MoveFileDelayUntilReboot);
            string? helperDirectory = Path.GetDirectoryName(transaction.HelperPath);
            if (helperDirectory is not null)
            {
                MoveFileEx(helperDirectory, null, MoveFileDelayUntilReboot);
            }
        }
        else
        {
            LauncherUpdateTransactionStore.TryDeleteFile(transaction.HelperPath);
            string? helperDirectory = Path.GetDirectoryName(transaction.HelperPath);
            if (helperDirectory is not null)
            {
                LauncherUpdateTransactionStore.TryDeleteDirectory(helperDirectory);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string? newFileName,
        uint flags);
}
