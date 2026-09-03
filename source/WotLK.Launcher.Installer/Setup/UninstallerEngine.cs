using System.Text.Json;
using System.IO;

namespace WotLK.Launcher.Installer.Setup;

internal enum UninstallStatus
{
    Completed,
    LauncherRunning
}

internal sealed record UninstallResult(
    UninstallStatus Status,
    string Message,
    IReadOnlyList<int> RunningProcessIds);

internal sealed class UninstallerEngine
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly InstallerEnvironment _environment;
    private readonly IInstallerRegistry _registry;
    private readonly IInstallerShortcutService _shortcuts;
    private readonly IInstallerProcessInspector _processes;
    private readonly IInstallerSystemActions _systemActions;
    private readonly InstallerLog _log;
    private int _running;

    internal UninstallerEngine(
        InstallerEnvironment environment,
        IInstallerRegistry registry,
        IInstallerShortcutService shortcuts,
        IInstallerProcessInspector processes,
        IInstallerSystemActions systemActions,
        InstallerLog log)
    {
        _environment = environment;
        _registry = registry;
        _shortcuts = shortcuts;
        _processes = processes;
        _systemActions = systemActions;
        _log = log;
    }

    internal async Task<UninstallResult> UninstallAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("Une désinstallation est déjà en cours.");
        }

        try
        {
            string root = InstallerEnvironment.Normalize(installRoot);
            _environment.DemandAllowedDestination(root);
            AtlasInstallState state = await LoadAndValidateStateAsync(root, cancellationToken);
            IReadOnlyList<int> running = _processes.FindByExactPath(state.LauncherPath);
            if (running.Count > 0)
            {
                return new UninstallResult(
                    UninstallStatus.LauncherRunning,
                    "Atlas Launcher est encore ouvert. Ferme-le puis réessaie.",
                    running);
            }

            _log.Info($"Désinstallation démarrée depuis {root}.");
            cancellationToken.ThrowIfCancellationRequested();
            if (state.DesktopShortcutCreated)
            {
                _shortcuts.DeleteIfOwned(state.DesktopShortcutPath, state.LauncherPath);
            }

            if (state.StartMenuShortcutCreated)
            {
                _shortcuts.DeleteIfOwned(state.StartMenuShortcutPath, state.LauncherPath);
            }

            _registry.Unregister(state.RegistrySubKey);
            DeleteFileIfPresent(state.LauncherPath);
            DeleteFileIfPresent(Path.Combine(root, InstallerProduct.InstallStateFileName));

            string? currentProcess = Environment.ProcessPath;
            bool runningFromInstalledUninstaller = !string.IsNullOrWhiteSpace(currentProcess)
                && InstallerEnvironment.SamePath(currentProcess, state.UninstallerPath);
            if (runningFromInstalledUninstaller)
            {
                _systemActions.ScheduleSelfDelete(
                    state.UninstallerPath,
                    root,
                    Environment.ProcessId);
            }
            else
            {
                DeleteFileIfPresent(state.UninstallerPath);
                DeleteDirectoryIfEmpty(root);
            }

            _log.Info("Désinstallation terminée. Les données LocalAppData et le client WoW ont été conservés.");
            return new UninstallResult(
                UninstallStatus.Completed,
                "Atlas Launcher a été désinstallé.",
                Array.Empty<int>());
        }
        catch (Exception exception)
        {
            _log.Error("Échec de la désinstallation", exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    internal static async Task<AtlasInstallState> ReadStateAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        string statePath = Path.Combine(
            InstallerEnvironment.Normalize(installRoot),
            InstallerProduct.InstallStateFileName);
        await using FileStream stream = new(
            statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<AtlasInstallState>(
                stream,
                StateJsonOptions,
                cancellationToken)
            ?? throw new InvalidDataException("Les informations de désinstallation sont incomplètes.");
    }

    internal static AtlasInstallState ReadState(string installRoot)
    {
        string statePath = Path.Combine(
            InstallerEnvironment.Normalize(installRoot),
            InstallerProduct.InstallStateFileName);
        using FileStream stream = new(
            statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        return JsonSerializer.Deserialize<AtlasInstallState>(stream, StateJsonOptions)
            ?? throw new InvalidDataException("Les informations de désinstallation sont incomplètes.");
    }

    private async Task<AtlasInstallState> LoadAndValidateStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        AtlasInstallState state = await ReadStateAsync(root, cancellationToken);
        if (state.SchemaVersion != 1
            || !string.Equals(state.ProductVersion, InstallerProduct.Version, StringComparison.Ordinal)
            || !InstallerEnvironment.SamePath(state.InstallLocation, root)
            || !InstallerEnvironment.SamePath(
                state.LauncherPath,
                Path.Combine(root, InstallerProduct.LauncherFileName))
            || !InstallerEnvironment.SamePath(
                state.UninstallerPath,
                Path.Combine(root, InstallerProduct.UninstallerFileName))
            || !string.Equals(
                state.RegistrySubKey,
                _environment.RegistrySubKey,
                StringComparison.OrdinalIgnoreCase)
            || !InstallerEnvironment.SamePath(
                state.DesktopShortcutPath,
                _environment.DesktopShortcutPath)
            || !InstallerEnvironment.SamePath(
                state.StartMenuShortcutPath,
                _environment.StartMenuShortcutPath)
            || !InstallerEnvironment.SamePath(state.InstallerLogPath, _environment.LogPath))
        {
            throw new InvalidDataException(
                "Les informations de désinstallation Atlas Launcher ne sont pas valides.");
        }

        return state;
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }
}
