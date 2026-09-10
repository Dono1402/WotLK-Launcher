using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using WotLK.Launcher.Updater;

namespace WotLK.Launcher;

internal static class GameDirectoryAccess
{
    private const string GrantAccessSwitch = "--grant-game-access";
    private const int OperationCancelledError = 1223;
    private const uint TokenQuery = 0x0008;
    private const uint FileAddFile = 0x0002;
    private const uint FileAddSubdirectory = 0x0004;
    private const uint FileDeleteChild = 0x0040;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    internal static bool IsGrantAccessMode(IReadOnlyList<string> args)
    {
        return args.Count > 0 &&
               string.Equals(args[0], GrantAccessSwitch, StringComparison.OrdinalIgnoreCase);
    }

    internal static int RunGrantAccess(IReadOnlyList<string> args)
    {
        if (args.Count != 4
            || !int.TryParse(
                args[3],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int requesterProcessId)
            || requesterProcessId <= 0)
        {
            return 2;
        }

        try
        {
            var root = GameInstallServices.NormalizeAndValidateGameRoot(args[1]);
            var sid = new SecurityIdentifier(args[2]);
            string currentExecutable = Path.GetFullPath(
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Executable launcher introuvable."));
            if (!LauncherUpdateSecurity.IsCurrentProcessElevated()
                || !ValidateRequesterAndGrantRoot(
                    requesterProcessId,
                    currentExecutable,
                    sid,
                    root))
            {
                return 3;
            }

            return GrantAccess(root, sid);
        }
        catch
        {
            return 4;
        }
    }

    internal static void PrepareElevatedSession(string installRoot)
    {
        // ACL changes are only allowed through the requester-bound helper mode.
        _ = installRoot;
    }

    internal static bool EnsureWritable(Window owner, string installRoot)
    {
        var root = GameInstallServices.NormalizeAndValidateGameRoot(installRoot);
        if (CanWrite(root))
        {
            return true;
        }

        var currentExe = Environment.ProcessPath;
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(currentExe) ||
            !File.Exists(currentExe) ||
            string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("Impossible de preparer les droits du dossier WotLK.");
        }

        DemandStableGrantRootForCurrentUser(root);
        DemandAdmissibleElevatedGrantTarget(root);

        var startInfo = new ProcessStartInfo
        {
            FileName = currentExe,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(GrantAccessSwitch);
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add(sid);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Impossible de preparer les droits du dossier WotLK.");
            }

            process.WaitForExit();
            if (process.ExitCode == 0 && CanWrite(root))
            {
                return true;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == OperationCancelledError)
        {
            return false;
        }

        MessageBox.Show(
            owner,
            "Windows n'a pas pu autoriser l'acces au dossier du client WotLK.",
            "Autorisation requise",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    internal static async Task<bool> EnsureWritableAsync(Window owner, string installRoot)
    {
        string root = GameInstallServices.NormalizeAndValidateGameRoot(installRoot);
        if (await Task.Run(() => CanWrite(root)).ConfigureAwait(false)) return true;

        string? currentExe = Environment.ProcessPath;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string? sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe) || string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("Impossible de preparer les droits du dossier WotLK.");

        DemandStableGrantRootForCurrentUser(root);
        DemandAdmissibleElevatedGrantTarget(root);

        ProcessStartInfo start = new()
        {
            FileName = currentExe, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(GrantAccessSwitch);
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(sid);
        start.ArgumentList.Add(Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            using Process? process = await Task.Run(() => Process.Start(start)).ConfigureAwait(false);
            if (process is null) throw new InvalidOperationException("Impossible de preparer les droits du dossier WotLK.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode == 0 && await Task.Run(() => CanWrite(root)).ConfigureAwait(false)) return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == OperationCancelledError) { return false; }

        if (!owner.Dispatcher.HasShutdownStarted)
        {
            try
            {
                await owner.Dispatcher.InvokeAsync(() => MessageBox.Show(owner,
                    "Windows n'a pas pu autoriser l'acces au dossier du client WotLK.",
                    "Autorisation requise", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            catch (TaskCanceledException) when (owner.Dispatcher.HasShutdownStarted) { }
        }
        return false;
    }

    internal static bool CanWrite(string installRoot)
    {
        var candidate = GameInstallServices.NormalizeAndValidateGameRoot(installRoot);
        while (!Directory.Exists(candidate))
        {
            var parent = Directory.GetParent(candidate);
            if (parent is null)
            {
                return false;
            }

            candidate = parent.FullName;
        }

        var probePath = Path.Combine(candidate, ".wotlk-launcher-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteProbe(probePath);
            return false;
        }
    }

    private static void TryDeleteProbe(string probePath)
    {
        try
        {
            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static int GrantAccess(string root, SecurityIdentifier sid)
    {
        ValidateGrantRoot(root);
        DemandAdmissibleElevatedGrantTarget(root);
        Directory.CreateDirectory(root);
        ValidateGrantRoot(root);
        DemandAdmissibleElevatedGrantTarget(root);
        ProcessStartInfo startInfo = BuildIcaclsStartInfo(root, sid);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 3;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    internal static ProcessStartInfo BuildIcaclsStartInfo(
        string root,
        SecurityIdentifier sid)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("/grant");
        startInfo.ArgumentList.Add($"*{sid.Value}:(OI)(CI)M");
        startInfo.ArgumentList.Add("/L");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("/Q");
        return startInfo;
    }

    internal static void ValidateGrantRoot(string root)
    {
        string current = Path.GetFullPath(root);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Le dossier WotLK traverse un lien ou un point de jonction.");
            }

            string? parent = Path.GetDirectoryName(
                current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent) || SamePath(parent, current))
            {
                break;
            }

            current = parent;
        }
    }

    internal static bool ValidateRequester(
        int requesterProcessId,
        string expectedExecutablePath,
        SecurityIdentifier expectedSid)
    {
        try
        {
            using Process requester = Process.GetProcessById(requesterProcessId);
            if (requester.HasExited
                || !ProcessMatchesPath(requester, expectedExecutablePath)
                || !OpenProcessToken(
                    requester.SafeHandle,
                    TokenQuery,
                    out SafeAccessTokenHandle token))
            {
                return false;
            }

            using (token)
            using (WindowsIdentity identity = new(token.DangerousGetHandle()))
            {
                return identity.User is not null && identity.User.Equals(expectedSid);
            }
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or Win32Exception
                                   or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void DemandStableGrantRootForCurrentUser(string root)
    {
        ValidateGrantRoot(root);
        string existing = Path.GetFullPath(root);
        while (!Directory.Exists(existing))
        {
            if (File.Exists(existing))
            {
                throw new InvalidDataException("Le chemin WotLK désigne un fichier.");
            }

            existing = Path.GetDirectoryName(existing)
                ?? throw new InvalidDataException("Dossier parent WotLK absent.");
        }

        LauncherUpdateElevationSecurity.DemandProtectedDirectoryForElevation(existing);

        if (HasDirectoryAccess(existing, FileAddFile)
            || HasDirectoryAccess(existing, FileAddSubdirectory)
            || HasDirectoryAccess(existing, FileDeleteChild))
        {
            throw new UnauthorizedAccessException(
                "Le dossier WotLK a changé de droits pendant la demande élevée.");
        }

        string child = existing;
        while (true)
        {
            string? parent = Path.GetDirectoryName(child.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent) || SamePath(parent, child))
            {
                break;
            }

            if (HasDirectoryAccess(parent, FileAddSubdirectory)
                && (HasDirectoryAccess(parent, FileDeleteChild)
                    || HasDirectoryAccess(child, DeleteAccess)))
            {
                throw new UnauthorizedAccessException(
                    "Un ancêtre du dossier WotLK peut remplacer la cible pendant l'élévation.");
            }

            child = parent;
        }

        ValidateGrantRoot(root);
    }

    internal static void DemandAdmissibleElevatedGrantTarget(string root)
    {
        string canonicalRoot = GameInstallServices.NormalizeAndValidateGameRoot(root);
        ValidateGrantRoot(canonicalRoot);
        if (!Directory.Exists(canonicalRoot))
        {
            if (!SamePath(canonicalRoot, LauncherSettings.GetDefaultInstallPath()))
            {
                throw new UnauthorizedAccessException(
                    "Un nouveau dossier protégé ne peut être créé que pour le chemin WotLK par défaut.");
            }

            return;
        }

        try
        {
            using IEnumerator<string> entries = Directory
                .EnumerateFileSystemEntries(canonicalRoot)
                .GetEnumerator();
            if (!entries.MoveNext())
            {
                if (SamePath(canonicalRoot, LauncherSettings.GetDefaultInstallPath()))
                {
                    return;
                }

                throw new UnauthorizedAccessException(
                    "Un dossier protégé vide n'est accepté que pour le chemin WotLK par défaut.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                "Le contenu du dossier protégé WotLK ne peut pas être vérifié.",
                ex);
        }

        if (HasManagedInstallMarker(canonicalRoot)
            || HasExpectedPlayableClient(canonicalRoot))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            "Le dossier protégé choisi contient des fichiers qui ne prouvent pas une installation WotLK Atlas.");
    }

    private static bool ValidateRequesterAndGrantRoot(
        int requesterProcessId,
        string expectedExecutablePath,
        SecurityIdentifier expectedSid,
        string root)
    {
        try
        {
            using Process requester = Process.GetProcessById(requesterProcessId);
            if (requester.HasExited
                || !ProcessMatchesPath(requester, expectedExecutablePath)
                || !OpenProcessToken(
                    requester.SafeHandle,
                    TokenQuery,
                    out SafeAccessTokenHandle token))
            {
                return false;
            }

            using (token)
            using (WindowsIdentity identity = new(token.DangerousGetHandle()))
            {
                if (identity.User is null || !identity.User.Equals(expectedSid))
                {
                    return false;
                }

                WindowsIdentity.RunImpersonated(
                    token,
                    () =>
                    {
                        DemandStableGrantRootForCurrentUser(root);
                        DemandAdmissibleElevatedGrantTarget(root);
                    });
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or Win32Exception
                                   or UnauthorizedAccessException
                                   or IOException)
        {
            return false;
        }
    }

    private static bool HasDirectoryAccess(string directory, uint desiredAccess)
    {
        using SafeFileHandle handle = CreateFile(
            directory,
            desiredAccess,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return true;
        }

        const int accessDenied = 5;
        return Marshal.GetLastWin32Error() != accessDenied;
    }

    private static bool ProcessMatchesPath(Process process, string expectedPath)
    {
        try
        {
            string? actualPath = process.MainModule?.FileName;
            return actualPath is not null && SamePath(actualPath, expectedPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasManagedInstallMarker(string root)
    {
        string marker = Path.Combine(root, GameInstallServices.ClientMarkerFileName);
        if (!File.Exists(marker))
        {
            return false;
        }

        try
        {
            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(marker);
            using FileStream stream = new(
                marker,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > 16 * 1024)
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            JsonElement rootElement = document.RootElement;
            return rootElement.ValueKind == JsonValueKind.Object
                   && rootElement.TryGetProperty("registeredApp", out JsonElement app)
                   && string.Equals(
                       app.GetString(),
                       GameInstallServices.AppDisplayName,
                       StringComparison.Ordinal)
                   && rootElement.TryGetProperty("installRoot", out JsonElement installRoot)
                   && installRoot.ValueKind == JsonValueKind.String
                   && SamePath(installRoot.GetString()!, root);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or InvalidDataException
                                   or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasExpectedPlayableClient(string root)
    {
        string gameExecutable = GameInstallServices.GetGameExecutablePath(root);
        string gameLauncher = GameInstallServices.GetGameLauncherPath(root);
        if (!File.Exists(gameExecutable) || !File.Exists(gameLauncher))
        {
            return false;
        }

        try
        {
            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(gameExecutable);
            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(gameLauncher);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
