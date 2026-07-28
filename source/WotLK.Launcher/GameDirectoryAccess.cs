using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace WotLK.Launcher;

internal static class GameDirectoryAccess
{
    private const string GrantAccessSwitch = "--grant-game-access";
    private const int OperationCancelledError = 1223;

    internal static bool IsGrantAccessMode(IReadOnlyList<string> args)
    {
        return args.Count > 0 &&
               string.Equals(args[0], GrantAccessSwitch, StringComparison.OrdinalIgnoreCase);
    }

    internal static int RunGrantAccess(IReadOnlyList<string> args)
    {
        if (args.Count != 3)
        {
            return 2;
        }

        try
        {
            var root = GameInstallServices.NormalizeAndValidateGameRoot(args[1]);
            var sid = new SecurityIdentifier(args[2]);
            return GrantAccess(root, sid);
        }
        catch
        {
            return 4;
        }
    }

    internal static void PrepareElevatedSession(string installRoot)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var sid = identity.User;
            if (sid is null ||
                !principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                return;
            }

            var root = GameInstallServices.NormalizeAndValidateGameRoot(installRoot);
            var markerPath = Path.Combine(root, ".wotlk-launcher-user-access-v1");
            if (File.Exists(markerPath))
            {
                return;
            }

            if (GrantAccess(root, sid) == 0)
            {
                File.WriteAllText(markerPath, sid.Value);
            }
        }
        catch
        {
        }
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
        Directory.CreateDirectory(root);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("/grant");
        startInfo.ArgumentList.Add($"*{sid.Value}:(OI)(CI)M");
        startInfo.ArgumentList.Add("/T");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("/Q");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 3;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
