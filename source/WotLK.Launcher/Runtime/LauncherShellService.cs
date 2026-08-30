using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace WotLK.Launcher.Runtime;

internal enum LauncherLocalAction
{
    OpenGameFolder,
    OpenDiagnostic
}

internal enum LauncherLocalActionStatus
{
    Succeeded,
    Unavailable,
    Failed,
    Busy,
    ShuttingDown
}

internal enum LauncherLocalFailureCategory
{
    None,
    EmptyPath,
    MissingTarget,
    InvalidPath,
    AccessDenied,
    ShellLaunchFailed,
    NoJournal
}

internal sealed record LauncherLocalActionResult(
    LauncherLocalAction Action,
    LauncherLocalActionStatus Status,
    LauncherLocalFailureCategory FailureCategory,
    string? UserMessage = null,
    string? ExceptionType = null)
{
    internal static LauncherLocalActionResult Success(LauncherLocalAction action)
    {
        return new LauncherLocalActionResult(
            action,
            LauncherLocalActionStatus.Succeeded,
            LauncherLocalFailureCategory.None);
    }

    internal static LauncherLocalActionResult Busy(LauncherLocalAction action)
    {
        return new LauncherLocalActionResult(
            action,
            LauncherLocalActionStatus.Busy,
            LauncherLocalFailureCategory.None);
    }

    internal static LauncherLocalActionResult ShuttingDown(LauncherLocalAction action)
    {
        return new LauncherLocalActionResult(
            action,
            LauncherLocalActionStatus.ShuttingDown,
            LauncherLocalFailureCategory.None);
    }
}

internal interface ILauncherProcessStarter
{
    void Start(ProcessStartInfo startInfo);
}

internal interface ILauncherShellService
{
    LauncherLocalActionResult OpenFolder(LauncherLocalAction action, string? folderPath);

    LauncherLocalActionResult SelectFile(LauncherLocalAction action, string? filePath);
}

internal sealed class LauncherProcessStarter : ILauncherProcessStarter
{
    public void Start(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows n'a pas créé de processus Explorateur.");
        }
    }
}

internal sealed class LauncherShellService : ILauncherShellService
{
    private readonly ILauncherProcessStarter _processStarter;

    internal LauncherShellService(ILauncherProcessStarter processStarter)
    {
        _processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
    }

    internal static LauncherShellService CreateProduction()
    {
        return new LauncherShellService(new LauncherProcessStarter());
    }

    public LauncherLocalActionResult OpenFolder(LauncherLocalAction action, string? folderPath)
    {
        LauncherLocalActionResult? validation = ValidatePath(action, folderPath, isFile: false);
        if (validation is not null)
        {
            return validation;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(folderPath!);
        return StartExplorer(action, startInfo);
    }

    public LauncherLocalActionResult SelectFile(LauncherLocalAction action, string? filePath)
    {
        LauncherLocalActionResult? validation = ValidatePath(action, filePath, isFile: true);
        if (validation is not null)
        {
            return validation;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(filePath!);
        return StartExplorer(action, startInfo);
    }

    private static LauncherLocalActionResult? ValidatePath(
        LauncherLocalAction action,
        string? path,
        bool isFile)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Unavailable(
                action,
                LauncherLocalFailureCategory.EmptyPath,
                action == LauncherLocalAction.OpenGameFolder
                    ? "Le chemin du jeu n'est pas configuré."
                    : "Aucun journal n'est encore disponible.");
        }

        try
        {
            _ = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return Failure(
                action,
                LauncherLocalFailureCategory.InvalidPath,
                action == LauncherLocalAction.OpenGameFolder
                    ? "Le chemin du jeu n'est pas valide."
                    : "Le chemin du journal n'est pas valide.",
                ex);
        }

        bool exists = isFile ? File.Exists(path) : Directory.Exists(path);
        if (!exists)
        {
            return Unavailable(
                action,
                LauncherLocalFailureCategory.MissingTarget,
                action == LauncherLocalAction.OpenGameFolder
                    ? "Le dossier du jeu est introuvable."
                    : "Le journal du launcher est introuvable.");
        }

        return null;
    }

    private LauncherLocalActionResult StartExplorer(
        LauncherLocalAction action,
        ProcessStartInfo startInfo)
    {
        try
        {
            _processStarter.Start(startInfo);
            return LauncherLocalActionResult.Success(action);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException
            || ex is Win32Exception { NativeErrorCode: 5 })
        {
            return Failure(
                action,
                LauncherLocalFailureCategory.AccessDenied,
                action == LauncherLocalAction.OpenGameFolder
                    ? "Windows refuse l'ouverture du dossier."
                    : "Windows refuse l'ouverture du journal.",
                ex);
        }
        catch (Exception ex)
        {
            return Failure(
                action,
                LauncherLocalFailureCategory.ShellLaunchFailed,
                action == LauncherLocalAction.OpenGameFolder
                    ? "Impossible d'ouvrir le dossier du jeu."
                    : "Impossible d'ouvrir le journal du launcher.",
                ex);
        }
    }

    private static LauncherLocalActionResult Unavailable(
        LauncherLocalAction action,
        LauncherLocalFailureCategory category,
        string message)
    {
        return new LauncherLocalActionResult(
            action,
            LauncherLocalActionStatus.Unavailable,
            category,
            message);
    }

    private static LauncherLocalActionResult Failure(
        LauncherLocalAction action,
        LauncherLocalFailureCategory category,
        string message,
        Exception exception)
    {
        return new LauncherLocalActionResult(
            action,
            LauncherLocalActionStatus.Failed,
            category,
            message,
            exception.GetType().Name);
    }
}
