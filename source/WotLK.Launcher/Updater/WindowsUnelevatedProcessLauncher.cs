using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WotLK.Launcher.Updater;

internal static class WindowsUnelevatedProcessLauncher
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint LogonWithProfile = 0x00000001;

    internal static void Launch(
        string executablePath,
        string arguments,
        string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        string executable = Path.GetFullPath(executablePath);
        string directory = Path.GetFullPath(workingDirectory);
        nint shellWindow = GetShellWindow();
        if (shellWindow == 0
            || GetWindowThreadProcessId(shellWindow, out uint shellProcessId) == 0
            || shellProcessId == 0)
        {
            throw new InvalidOperationException("Shell Windows interactif indisponible.");
        }

        ValidateShellProcess(shellProcessId);
        using SafeProcessHandle shellProcess = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            shellProcessId);
        if (shellProcess.IsInvalid)
        {
            throw LastWin32Error("Impossible d'ouvrir le processus du shell Windows.");
        }

        uint tokenAccess = TokenAssignPrimary | TokenDuplicate | TokenQuery;
        if (!OpenProcessToken(shellProcess, tokenAccess, out SafeAccessTokenHandle shellToken))
        {
            throw LastWin32Error("Impossible d'ouvrir le jeton du shell Windows.");
        }

        using (shellToken)
        {
            if (IsTokenElevated(shellToken))
            {
                throw new InvalidOperationException(
                    "Le shell Windows ne fournit pas de jeton utilisateur non élevé.");
            }

            StartupInfo startupInfo = new()
            {
                Size = Marshal.SizeOf<StartupInfo>()
            };
            StringBuilder commandLine = new(BuildCommandLine(executable, arguments));
            if (!CreateProcessWithTokenW(
                    shellToken,
                    LogonWithProfile,
                    executable,
                    commandLine,
                    creationFlags: 0,
                    environment: 0,
                    directory,
                    ref startupInfo,
                    out ProcessInformation processInformation))
            {
                throw LastWin32Error("Windows n'a pas pu relancer Atlas Launcher.");
            }

            CloseHandle(processInformation.ThreadHandle);
            CloseHandle(processInformation.ProcessHandle);
        }
    }

    internal static string BuildCommandLine(string executablePath, string arguments)
    {
        string executable = Path.GetFullPath(executablePath);
        if (executable.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Chemin du launcher invalide.");
        }

        string commandLine = $"\"{executable}\"";
        return string.IsNullOrEmpty(arguments)
            ? commandLine
            : commandLine + " " + arguments;
    }

    private static void ValidateShellProcess(uint shellProcessId)
    {
        using Process shell = Process.GetProcessById(checked((int)shellProcessId));
        using Process current = Process.GetCurrentProcess();
        string windowsDirectory = Environment.GetEnvironmentVariable("WINDIR")
            ?? throw new InvalidOperationException("Dossier Windows introuvable.");
        string expectedPath = Path.Combine(windowsDirectory, "explorer.exe");
        string? actualPath = shell.MainModule?.FileName;
        if (shell.SessionId != current.SessionId
            || actualPath is null
            || !string.Equals(
                Path.GetFullPath(actualPath),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Le shell Windows interactif est invalide.");
        }
    }

    private static bool IsTokenElevated(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(
                token,
                TokenInformationClass.TokenElevation,
                out TokenElevation elevation,
                Marshal.SizeOf<TokenElevation>(),
                out _))
        {
            throw LastWin32Error("Impossible de vérifier le jeton du shell Windows.");
        }

        return elevation.IsElevated != 0;
    }

    private static Win32Exception LastWin32Error(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        internal int IsElevated;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort ReservedSize;
        internal nint ReservedPointer;
        internal nint StandardInput;
        internal nint StandardOutput;
        internal nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint ProcessHandle;
        internal nint ThreadHandle;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

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
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle token,
        uint logonFlags,
        string applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
