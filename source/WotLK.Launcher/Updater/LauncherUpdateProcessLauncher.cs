using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WotLK.Launcher.Updater;

internal sealed class WindowsLauncherUpdateApplicationLauncher(
    LauncherUpdateTransactionStore store) : ILauncherUpdateApplicationLauncher
{
    private readonly LauncherUpdateTransactionStore _store = store
        ?? throw new ArgumentNullException(nameof(store));

    public async Task<ILauncherUpdateLaunchedProcess> LaunchUpdatedAsync(
        LauncherUpdateTransaction transaction,
        TimeSpan startTimeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        _store.DeleteSignals(transaction);
        LaunchThroughInteractiveShell(
            transaction.TargetPath,
            LauncherUpdateCommandLine.BuildPostUpdateArgument(transaction.TransactionId),
            Path.GetDirectoryName(transaction.TargetPath)!);

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < startTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LauncherUpdateProcessSignal? signal = _store.TryReadStartedSignal(transaction);
            if (signal is not null)
            {
                if (signal.IsElevated)
                {
                    throw new InvalidOperationException(
                        "Le nouveau launcher a hérité d'un jeton administrateur.");
                }

                if (!LauncherUpdateParentWaiter.ProcessMatchesPath(
                        signal.ProcessId,
                        transaction.TargetPath))
                {
                    throw new InvalidDataException(
                        "Le processus démarré ne correspond pas au nouveau launcher.");
                }

                return new LauncherUpdateLaunchedProcess(
                    Process.GetProcessById(signal.ProcessId));
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Le nouveau launcher n'a pas démarré à temps.");
    }

    public Task LaunchRollbackAsync(
        LauncherUpdateTransaction transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LauncherUpdateParentWaiter.ProcessMatchesPath(
                transaction.ParentProcessId,
                transaction.TargetPath))
        {
            return Task.CompletedTask;
        }

        LaunchThroughInteractiveShell(
            transaction.TargetPath,
            arguments: string.Empty,
            Path.GetDirectoryName(transaction.TargetPath)!);
        return Task.CompletedTask;
    }

    private static void LaunchThroughInteractiveShell(
        string executablePath,
        string arguments,
        string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Impossible de relancer Atlas Launcher.");
            return;
        }

        Type shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Shell Windows indisponible.");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Shell Windows indisponible.");
        try
        {
            shellType.InvokeMember(
                "ShellExecute",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [executablePath, arguments, workingDirectory, "open", 1]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException(
                "Windows n'a pas pu relancer Atlas Launcher.",
                ex.InnerException);
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}

internal sealed class LauncherUpdateLaunchedProcess(Process process) : ILauncherUpdateLaunchedProcess
{
    private readonly Process _process = process ?? throw new ArgumentNullException(nameof(process));

    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Kill()
    {
        if (!HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5_000);
        }
    }

    public void Dispose() => _process.Dispose();
}

internal sealed class WindowsLauncherUpdateHelperLauncher(
    LauncherUpdateTransactionStore store) : ILauncherUpdateHelperLauncher
{
    private static readonly TimeSpan AcceptanceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AcceptancePollInterval = TimeSpan.FromMilliseconds(100);
    private readonly LauncherUpdateTransactionStore _store = store
        ?? throw new ArgumentNullException(nameof(store));

    public Task LaunchApplyAsync(
        LauncherUpdateTransaction transaction,
        CancellationToken cancellationToken)
    {
        return LaunchElevatedAsync(
            transaction,
            transaction.HelperPath,
            LauncherUpdateCommandLine.ApplySwitch,
            transaction.TransactionPath,
            transaction.ParentProcessId,
            waitForAcceptance: true,
            cancellationToken);
    }

    public Task LaunchRecoveryAsync(
        LauncherUpdateTransaction transaction,
        int requesterProcessId,
        CancellationToken cancellationToken)
    {
        return LaunchElevatedAsync(
            transaction,
            transaction.HelperPath,
            LauncherUpdateCommandLine.RecoverSwitch,
            transaction.TransactionPath,
            requesterProcessId,
            waitForAcceptance: false,
            cancellationToken);
    }

    private async Task LaunchElevatedAsync(
        LauncherUpdateTransaction transaction,
        string helperPath,
        string modeSwitch,
        string transactionPath,
        int requesterProcessId,
        bool waitForAcceptance,
        CancellationToken cancellationToken)
    {
        if (requesterProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requesterProcessId));
        }

        LauncherUpdateTransactionStore.TryDeleteFile(
            transaction.HelperAcceptedSignalPath);
        ProcessStartInfo startInfo = new()
        {
            FileName = helperPath,
            UseShellExecute = true,
            Verb = OperatingSystem.IsWindows() ? "runas" : string.Empty,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        startInfo.ArgumentList.Add(modeSwitch);
        startInfo.ArgumentList.Add(transactionPath);
        startInfo.ArgumentList.Add(requesterProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "L'autorisation administrateur nécessaire à la mise à jour a été refusée.",
                ex);
        }

        if (process is null)
        {
            throw new InvalidOperationException("Impossible de démarrer le helper de mise à jour.");
        }

        if (!waitForAcceptance)
        {
            process.Dispose();
            return;
        }

        using (process)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < AcceptanceTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LauncherUpdateProcessSignal? accepted =
                    _store.TryReadHelperAcceptedSignal(transaction);
                if (accepted is not null
                    && accepted.ProcessId == process.Id
                    && accepted.IsElevated)
                {
                    return;
                }

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        "Le helper de mise à jour s'est arrêté avant validation.");
                }

                await Task.Delay(AcceptancePollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            "Le helper de mise à jour n'a pas validé la transaction à temps.");
    }
}

internal static class LauncherUpdateSecurity
{
    internal static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
