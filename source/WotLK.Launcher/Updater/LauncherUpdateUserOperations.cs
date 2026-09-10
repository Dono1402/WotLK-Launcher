using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WotLK.Launcher.Updater;

internal interface ILauncherUpdateUserOperationRunner
{
    void Run(Action operation);

    T Run<T>(Func<T> operation);
}

internal sealed class LauncherUpdateDirectUserOperationRunner
    : ILauncherUpdateUserOperationRunner
{
    internal static LauncherUpdateDirectUserOperationRunner Instance { get; } = new();

    private LauncherUpdateDirectUserOperationRunner()
    {
    }

    public void Run(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation();
    }

    public T Run<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation();
    }
}

/// <summary>
/// Keeps a duplicated handle to the unelevated requester's token. The handle remains
/// valid after the requester exits, so every later operation against LocalAppData can
/// run with the requester's rights while target-directory operations stay elevated.
/// </summary>
internal sealed class LauncherUpdateRequesterImpersonation
    : ILauncherUpdateUserOperationRunner, IDisposable
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint KnownFolderFlagDontVerify = 0x00004000;
    private const uint LogonWithProfile = 0x00000001;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private static readonly Guid LocalAppDataFolderId = new(
        "F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private readonly Process _requesterProcess;
    private readonly string _requesterExecutablePath;
    private readonly SafeAccessTokenHandle _token;
    private readonly string _transactionsRoot;
    private bool _disposed;

    private LauncherUpdateRequesterImpersonation(
        Process requesterProcess,
        string requesterExecutablePath,
        SafeAccessTokenHandle token,
        string transactionsRoot)
    {
        _requesterProcess = requesterProcess;
        _requesterExecutablePath = requesterExecutablePath;
        _token = token;
        _transactionsRoot = transactionsRoot;
    }

    /// <summary>
    /// Transaction root resolved for the captured requester token. This must be used
    /// instead of Environment.SpecialFolder while an elevated OTS account is active.
    /// </summary>
    internal string TransactionsRoot
    {
        get
        {
            ThrowIfDisposed();
            return _transactionsRoot;
        }
    }

    internal static LauncherUpdateRequesterImpersonation Capture(int requesterProcessId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "L'impersonation du demandeur est uniquement disponible sous Windows.");
        }

        if (requesterProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requesterProcessId));
        }

        Process requester = Process.GetProcessById(requesterProcessId);
        SafeAccessTokenHandle? token = null;
        try
        {
            if (requester.HasExited
                || !OpenProcessToken(
                    requester.SafeHandle,
                    TokenQuery | TokenDuplicate | TokenImpersonate | TokenAssignPrimary,
                    out token))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Impossible de capturer le jeton du launcher demandeur.");
            }

            if (IsElevated(token))
            {
                throw new UnauthorizedAccessException(
                    "Le launcher demandeur doit rester non élevé pendant la mise à jour.");
            }

            string requesterExecutablePath = Path.GetFullPath(
                requester.MainModule?.FileName
                ?? throw new InvalidOperationException(
                    "L'exécutable du launcher demandeur est introuvable."));
            string transactionsRoot = GetTransactionsRoot(token);
            LauncherUpdateRequesterImpersonation captured = new(
                requester,
                requesterExecutablePath,
                token,
                transactionsRoot);
            requester = null!;
            token = null;
            return captured;
        }
        catch
        {
            token?.Dispose();
            throw;
        }
        finally
        {
            requester?.Dispose();
        }
    }

    /// <summary>
    /// Validates the transaction against the same process object whose token was
    /// captured. Retaining its kernel handle prevents a recycled PID from changing
    /// the requester identity between token capture and transaction validation.
    /// </summary>
    internal void DemandMatchesRequester(int requesterProcessId, string expectedExecutablePath)
    {
        ThrowIfDisposed();
        if (requesterProcessId != _requesterProcess.Id
            || _requesterProcess.HasExited
            || !SamePath(_requesterExecutablePath, expectedExecutablePath))
        {
            throw new UnauthorizedAccessException(
                "Le demandeur ne correspond pas au launcher attendu.");
        }
    }

    /// <summary>
    /// Starts the replacement launcher with the captured requester's primary token.
    /// This preserves the interactive user across an over-the-shoulder UAC prompt,
    /// where the elevated helper can belong to a different administrator account.
    /// </summary>
    internal void LaunchProcess(
        string executablePath,
        string arguments,
        string workingDirectory)
    {
        ThrowIfDisposed();
        string executable = Path.GetFullPath(executablePath);
        string directory = Path.GetFullPath(workingDirectory);
        if (executable.Contains('"', StringComparison.Ordinal)
            || directory.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Chemin de relance du launcher invalide.");
        }

        StringBuilder commandLine = new();
        commandLine.Append('"').Append(executable).Append('"');
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            commandLine.Append(' ').Append(arguments);
        }

        IntPtr environment = IntPtr.Zero;
        ProcessInformation processInformation = default;
        try
        {
            if (!CreateEnvironmentBlock(out environment, _token, inherit: false))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Impossible de préparer l'environnement du launcher demandeur.");
            }

            StartupInfo startupInfo = new()
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = "winsta0\\default"
            };
            if (!CreateProcessWithTokenW(
                    _token,
                    LogonWithProfile,
                    executable,
                    commandLine,
                    CreateUnicodeEnvironment,
                    environment,
                    directory,
                    ref startupInfo,
                    out processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Impossible de relancer Atlas Launcher avec le jeton du demandeur.");
            }
        }
        finally
        {
            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ProcessHandle);
            }

            if (processInformation.ThreadHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ThreadHandle);
            }

            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }
        }
    }

    public void Run(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        WindowsIdentity.RunImpersonated(_token, operation);
    }

    public T Run<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        return WindowsIdentity.RunImpersonated(_token, operation);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _token.Dispose();
        _requesterProcess.Dispose();
    }

    private static bool IsElevated(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(
                token,
                TokenInformationClass.TokenElevation,
                out TokenElevation elevation,
                (uint)Marshal.SizeOf<TokenElevation>(),
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Impossible de vérifier le niveau du launcher demandeur.");
        }

        return elevation.TokenIsElevated != 0;
    }

    private static string GetTransactionsRoot(SafeAccessTokenHandle token)
    {
        IntPtr pathPointer = IntPtr.Zero;
        int result = SHGetKnownFolderPath(
            in LocalAppDataFolderId,
            KnownFolderFlagDontVerify,
            token,
            out pathPointer);
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        try
        {
            string localAppData = Marshal.PtrToStringUni(pathPointer)
                ?? throw new InvalidOperationException(
                    "Le dossier LocalAppData du launcher demandeur est introuvable.");
            string fullPath = Path.GetFullPath(localAppData);
            if (!Path.IsPathFullyQualified(fullPath)
                || fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Le dossier LocalAppData du launcher demandeur doit être local.");
            }

            return Path.Combine(
                fullPath,
                LauncherBuildFlavor.SettingsDirectoryName,
                "SelfUpdate",
                "Transactions");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenElevation
    {
        internal readonly int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        private string? Reserved;
        internal string? Desktop;
        private string? Title;
        private int X;
        private int Y;
        private int XSize;
        private int YSize;
        private int XCountChars;
        private int YCountChars;
        private int FillAttribute;
        private int Flags;
        private short ShowWindow;
        private short Reserved2;
        private IntPtr Reserved2Pointer;
        private IntPtr StandardInput;
        private IntPtr StandardOutput;
        private IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr ProcessHandle;
        internal IntPtr ThreadHandle;
        private int ProcessId;
        private int ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        out TokenElevation tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        in Guid knownFolderId,
        uint flags,
        SafeAccessTokenHandle tokenHandle,
        out IntPtr path);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle tokenHandle,
        uint logonFlags,
        string? applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
