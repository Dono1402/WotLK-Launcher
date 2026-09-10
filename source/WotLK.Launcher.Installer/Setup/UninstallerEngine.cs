using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const int MaximumStateBytes = 64 * 1024;
    private static readonly string[] RequiredStatePropertyNames =
    [
        "schemaVersion",
        "productVersion",
        "installLocation",
        "launcherPath",
        "uninstallerPath",
        "desktopShortcutCreated",
        "desktopShortcutPath",
        "startMenuShortcutCreated",
        "startMenuShortcutPath",
        "registrySubKey",
        "installedAtUtc",
        "isTestInstallation"
    ];

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
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
            DemandInstalledBoundary(root, state, requireLauncher: true, requireState: true);
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
                DemandInstalledBoundary(root, state, requireLauncher: true, requireState: true);
                DemandShortcutBoundary(state.DesktopShortcutPath);
                _shortcuts.DeleteIfOwned(state.DesktopShortcutPath, state.LauncherPath);
            }

            if (state.StartMenuShortcutCreated)
            {
                DemandInstalledBoundary(root, state, requireLauncher: true, requireState: true);
                DemandShortcutBoundary(state.StartMenuShortcutPath);
                _shortcuts.DeleteIfOwned(state.StartMenuShortcutPath, state.LauncherPath);
            }

            DemandInstalledBoundary(root, state, requireLauncher: true, requireState: true);
            _registry.Unregister(state.RegistrySubKey);
            DemandInstalledBoundary(root, state, requireLauncher: true, requireState: true);
            DeleteFileIfPresent(state.LauncherPath);
            DemandInstalledBoundary(root, state, requireLauncher: false, requireState: true);
            DeleteFileIfPresent(Path.Combine(root, InstallerProduct.InstallStateFileName));

            string? currentProcess = Environment.ProcessPath;
            bool runningFromInstalledUninstaller = !string.IsNullOrWhiteSpace(currentProcess)
                && InstallerEnvironment.SamePath(currentProcess, state.UninstallerPath);
            if (runningFromInstalledUninstaller)
            {
                DemandInstalledBoundary(root, state, requireLauncher: false, requireState: false);
                _systemActions.ScheduleSelfDelete(
                    state.UninstallerPath,
                    root,
                    Environment.ProcessId);
            }
            else
            {
                DemandInstalledBoundary(root, state, requireLauncher: false, requireState: false);
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
        InstallerPathValidator.DemandNoReparsePoints(statePath);
        await using FileStream stream = new(
            statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] document = await ReadBoundedStateAsync(stream, cancellationToken);
        return ParseState(document);
    }

    internal static AtlasInstallState ReadState(string installRoot)
    {
        string statePath = Path.Combine(
            InstallerEnvironment.Normalize(installRoot),
            InstallerProduct.InstallStateFileName);
        InstallerPathValidator.DemandNoReparsePoints(statePath);
        using FileStream stream = new(
            statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        byte[] document = ReadBoundedState(stream);
        return ParseState(document);
    }

    private async Task<AtlasInstallState> LoadAndValidateStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        DemandRootBoundary(root, requireState: true);
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
            || state.IsTestInstallation != _environment.IsTest)
        {
            throw new InvalidDataException(
                "Les informations de désinstallation Atlas Launcher ne sont pas valides.");
        }

        DemandInstalledBoundary(root, state, requireLauncher: true, requireState: true);
        return state;
    }

    private static async Task<byte[]> ReadBoundedStateAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        int length = ValidateStateLength(stream.Length);
        byte[] document = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(document.AsMemory(), cancellationToken);
        return document;
    }

    private static byte[] ReadBoundedState(FileStream stream)
    {
        int length = ValidateStateLength(stream.Length);
        byte[] document = GC.AllocateUninitializedArray<byte>(length);
        stream.ReadExactly(document);
        return document;
    }

    private static int ValidateStateLength(long length)
    {
        if (length <= 0 || length > MaximumStateBytes)
        {
            throw new InvalidDataException(
                "La taille des informations de désinstallation n'est pas valide.");
        }

        return checked((int)length);
    }

    private static AtlasInstallState ParseState(ReadOnlySpan<byte> document)
    {
        HashSet<string> propertyNames = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            Utf8JsonReader reader = new(document, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException(
                    "Les informations de désinstallation doivent être un objet JSON.");
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    string name = reader.GetString()
                        ?? throw new InvalidDataException("Un nom de propriété JSON est invalide.");
                    if (!propertyNames.Add(name))
                    {
                        throw new InvalidDataException(
                            "Les informations de désinstallation contiennent une propriété dupliquée.");
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Les informations de désinstallation ne sont pas valides.",
                exception);
        }

        if (propertyNames.Count != RequiredStatePropertyNames.Length
            || RequiredStatePropertyNames.Any(name => !propertyNames.Contains(name)))
        {
            throw new InvalidDataException(
                "Les informations de désinstallation ne contiennent pas l'ensemble exact des propriétés attendues.");
        }

        try
        {
            return JsonSerializer.Deserialize<AtlasInstallState>(document, StateJsonOptions)
                ?? throw new InvalidDataException(
                    "Les informations de désinstallation sont incomplètes.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Les informations de désinstallation ne sont pas valides.",
                exception);
        }
    }

    private void DemandRootBoundary(string root, bool requireState)
    {
        _environment.DemandAllowedDestination(root);
        InstallerPathValidator.DemandNoReparsePoints(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Le dossier Atlas Launcher installé est absent.");
        }

        if (!_environment.IsTest)
        {
            InstallerProtectedPathSecurity.DemandTrustedDirectory(root);
        }

        string statePath = Path.Combine(root, InstallerProduct.InstallStateFileName);
        InstallerPathValidator.DemandNoReparsePoints(statePath);
        if (requireState && !File.Exists(statePath))
        {
            throw new FileNotFoundException(
                "Les informations de désinstallation sont absentes.",
                statePath);
        }

        if (!_environment.IsTest && File.Exists(statePath))
        {
            InstallerProtectedPathSecurity.DemandTrustedFile(statePath);
        }
    }

    private void DemandInstalledBoundary(
        string root,
        AtlasInstallState state,
        bool requireLauncher,
        bool requireState)
    {
        DemandRootBoundary(root, requireState);
        foreach ((string path, bool required) in new[]
                 {
                     (state.LauncherPath, requireLauncher),
                     (state.UninstallerPath, true)
                 })
        {
            InstallerPathValidator.DemandNoReparsePoints(path);
            if (required && !File.Exists(path))
            {
                throw new FileNotFoundException("Un fichier installé attendu est absent.", path);
            }

            if (!_environment.IsTest && File.Exists(path))
            {
                InstallerProtectedPathSecurity.DemandTrustedFile(path);
            }
        }
    }

    private void DemandShortcutBoundary(string shortcutPath)
    {
        InstallerPathValidator.DemandNoReparsePoints(shortcutPath);
        if (_environment.IsTest)
        {
            return;
        }

        string parent = Path.GetDirectoryName(shortcutPath)
            ?? throw new InvalidDataException("Le dossier du raccourci est absent.");
        while (!Directory.Exists(parent))
        {
            parent = Path.GetDirectoryName(parent)
                ?? throw new DirectoryNotFoundException(
                    "Le dossier protégé du raccourci est absent.");
        }

        InstallerProtectedPathSecurity.DemandTrustedDirectory(parent);
        if (File.Exists(shortcutPath))
        {
            InstallerProtectedPathSecurity.DemandTrustedFile(shortcutPath);
        }
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        InstallerPathValidator.DemandNoReparsePoints(path);
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
