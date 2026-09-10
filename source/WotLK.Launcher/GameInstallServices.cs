using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WotLK.Launcher.Updater;

namespace WotLK.Launcher;

internal static partial class GameInstallServices
{
    internal const string AppDisplayName = "WotLK Client";
    internal const string UninstallerFileName = "WotLK Uninstaller.exe";
    internal const string GameUninstallCleanupArgument = "--game-uninstall-cleanup";
    internal const int MaximumInstallMarkerBytes = 64 * 1024;
    internal const string GameLauncherFileName = "Arctium Game Launcher Atlas.exe";
    internal const string GameExecutableRelativePath = @"_classic_\WowClassic.exe";
    internal const string ClassicDirectoryName = "_classic_";
    internal const string PortalAddress = "animeclub.fr";

    private const string Publisher = "WotLK";
    private const string RegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WotLK.Client";
    private const string TransientCleanupHelperFileName = "WotLK Game Uninstall Helper.exe";
    internal const string ClientMarkerFileName = "client-install.json";
    private const string VideoDefaultsMarkerFileName = "launcher-video-defaults.json";
    private const int MaximumClientConfigBytes = 4 * 1024 * 1024;
    private const int SystemMetricPrimaryScreenWidth = 0;
    private const int SystemMetricPrimaryScreenHeight = 1;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint GenericReadAccess = 0x80000000;
    private const uint FileWriteAttributesAccess = 0x00000100;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const int FileDispositionInfoClass = 4;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private static readonly JsonSerializerOptions InstallMarkerJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string GetGameExecutablePath(string installRoot)
    {
        return Path.Combine(installRoot, GameExecutableRelativePath);
    }

    internal static string GetGameLauncherPath(string installRoot)
    {
        return Path.Combine(installRoot, GameLauncherFileName);
    }

    internal static string GetClassicDirectoryPath(string installRoot)
    {
        return Path.Combine(installRoot, ClassicDirectoryName);
    }

    internal static bool HasPlayableClient(string installRoot)
    {
        return File.Exists(GetGameExecutablePath(installRoot)) &&
               File.Exists(GetGameLauncherPath(installRoot));
    }

    internal static bool IsGameUninstallMode(IEnumerable<string> args)
    {
        return args.Any(arg =>
            string.Equals(arg, "/uninstall-game", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--uninstall-game", StringComparison.OrdinalIgnoreCase));
    }

    internal static int RunGameUninstall(string[] args)
    {
        bool quiet = args.Any(arg =>
            string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase));

        try
        {
            GameUninstallIdentity identity = ValidateGameUninstallPreparation(
                Environment.ProcessPath,
                LauncherUpdateSecurity.IsCurrentProcessElevated(),
                ReadGameUninstallRegistration);

            if (!quiet)
            {
                MessageBoxResult confirmation = MessageBox.Show(
                    "Desinstaller WotLK Client de cet ordinateur ?",
                    "Desinstaller WotLK Client",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirmation != MessageBoxResult.Yes)
                {
                    return 0;
                }
            }

            StartGameUninstallCleanup(identity);
            return 0;
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                MessageBox.Show(ex.Message, "Erreur de desinstallation", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return 1;
        }
    }

    internal static bool TryParseGameUninstallCleanupArguments(
        IReadOnlyList<string> args,
        out GameUninstallCleanupRequest request)
    {
        request = default!;
        if (args.Count != 10
            || !string.Equals(
                args[0],
                GameUninstallCleanupArgument,
                StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(
                args[5],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parentProcessId)
            || parentProcessId <= 0
            || !long.TryParse(
                args[6],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parentStartTimeUtcTicks)
            || parentStartTimeUtcTicks <= 0
            || !TryNormalizeSecurityIdentifier(args[7], out string requesterSid)
            || !TryNormalizeCleanupEventName(args[8], out string cleanupEventName)
            || !TryNormalizeSha256(args[9], out string expectedHelperSha256))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(args[1])
                || !Path.IsPathFullyQualified(args[2])
                || !Path.IsPathFullyQualified(args[3])
                || !Path.IsPathFullyQualified(args[4]))
            {
                return false;
            }

            request = new GameUninstallCleanupRequest(
                WorkspacePath: Path.GetFullPath(args[1]),
                HelperPath: Path.GetFullPath(args[2]),
                InstallRoot: Path.GetFullPath(args[3]),
                UninstallerPath: Path.GetFullPath(args[4]),
                parentProcessId,
                parentStartTimeUtcTicks,
                requesterSid,
                cleanupEventName,
                expectedHelperSha256);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    internal static int RunGameUninstallCleanup(string[] args)
    {
        try
        {
            if (!TryParseGameUninstallCleanupArguments(args, out GameUninstallCleanupRequest request))
            {
                throw new InvalidOperationException(
                    "Les arguments internes de desinstallation WotLK sont invalides.");
            }

            DemandMediumIntegrity(
                LauncherUpdateSecurity.IsCurrentProcessElevated());
            string currentSid = GetCurrentUserSid();
            using TransientGameUninstallValidation transient =
                ValidateTransientGameUninstallCleanup(
                    request,
                    Environment.ProcessPath,
                    currentSid);

            GameUninstallIdentity requestedIdentity = DeriveGameUninstallIdentity(
                request.UninstallerPath);
            if (!SamePath(requestedIdentity.InstallRoot, request.InstallRoot))
            {
                throw new InvalidDataException(
                    "La racine transmise au nettoyage WotLK ne correspond pas au desinstalleur.");
            }

            using Process parent = Process.GetProcessById(request.ParentProcessId);
            if (!ParentProcessMatchesExpected(
                    parent,
                    request.ParentStartTimeUtcTicks,
                    () => parent.MainModule?.FileName,
                    requestedIdentity.UninstallerPath))
            {
                throw new InvalidDataException(
                    "Le processus parent du nettoyage WotLK n'est pas le desinstalleur attendu.");
            }

            GameUninstallIdentity identity = ValidateGameUninstallPreparation(
                requestedIdentity.UninstallerPath,
                isElevated: false,
                ReadGameUninstallRegistration);
            if (!SamePath(identity.InstallRoot, request.InstallRoot))
            {
                throw new InvalidDataException(
                    "La racine WotLK a change avant le nettoyage.");
            }

            using EventWaitHandle accepted = EventWaitHandle.OpenExisting(
                request.CleanupEventName);
            accepted.Set();

            if (!parent.WaitForExit(60_000))
            {
                throw new TimeoutException(
                    "Le desinstalleur WotLK ne s'est pas ferme a temps.");
            }

            transient.ReleaseGameSourceLocks();

            identity = ValidateGameUninstallPreparation(
                request.UninstallerPath,
                isElevated: false,
                ReadGameUninstallRegistration);
            if (!SamePath(identity.InstallRoot, request.InstallRoot))
            {
                throw new InvalidDataException(
                    "La racine WotLK a change apres la fermeture du desinstalleur.");
            }

            StopRunningWow(identity.InstallRoot);
            identity = ValidateGameUninstallPreparation(
                request.UninstallerPath,
                isElevated: false,
                ReadGameUninstallRegistration);
            DeleteDirectoryTreeWithRetry(
                identity.InstallRoot,
                validateUnderLock: () =>
                {
                    GameUninstallIdentity lockedIdentity = ValidateGameUninstallPreparation(
                        request.UninstallerPath,
                        isElevated: false,
                        ReadGameUninstallRegistration);
                    if (!SamePath(lockedIdentity.InstallRoot, request.InstallRoot))
                    {
                        throw new InvalidDataException(
                            "La racine WotLK a change sous le verrou de suppression.");
                    }

                    identity = lockedIdentity;
                });
            UnregisterInstalledApp(identity);
            ScheduleTransientGameUninstallSelfDelete(
                request,
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    internal static string RegisterInstalledGame(string installRoot, string clientVersion)
    {
        string root = NormalizeAndValidateGameRoot(installRoot);
        using IGameInstallRootLease rootLease = AcquireGameInstallRootLease(
            root,
            GameInstallRootLeaseMode.ExistingClient);
        return RegisterInstalledGame(root, clientVersion, rootLease);
    }

    internal static string RegisterInstalledGame(
        string installRoot,
        string clientVersion,
        IGameInstallRootLease rootLease)
    {
        ArgumentNullException.ThrowIfNull(rootLease);
        string root = DemandLeaseMatchesGameRoot(installRoot, rootLease);
        rootLease.Revalidate();
        string uninstallerExe = CopySelfAsGameUninstaller(root, rootLease);
        WriteInstallMarker(
            root,
            clientVersion,
            uninstallerExe,
            rootLease.Ownership,
            rootLease);
        rootLease.Revalidate();
        RegisterInstalledApp(root, clientVersion, uninstallerExe, rootLease);
        return uninstallerExe;
    }

    private static string CopySelfAsGameUninstaller(
        string installRoot,
        IGameInstallRootLease rootLease)
    {
        string? sourceExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourceExe) || !File.Exists(sourceExe))
        {
            throw new InvalidOperationException("Impossible de retrouver l'executable du launcher.");
        }

        string uninstallerExe = Path.Combine(installRoot, UninstallerFileName);
        if (SamePath(sourceExe, uninstallerExe))
        {
            using IGameInstallReadLease readLease = rootLease.OpenFileForRead(
                uninstallerExe);
            return uninstallerExe;
        }

        using FileStream source = new(
            sourceExe,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (source.Length <= 0)
        {
            throw new InvalidDataException(
                "L'executable du launcher est vide et ne peut pas devenir le desinstalleur.");
        }

        rootLease.WriteFileAtomically(
            uninstallerExe,
            destination => source.CopyTo(destination));
        return uninstallerExe;
    }

    private static void RegisterInstalledApp(
        string installRoot,
        string clientVersion,
        string uninstallerExe,
        IGameInstallRootLease rootLease)
    {
        rootLease.Revalidate();
        using var baseKey = OpenUninstallBaseKey();
        using var key = baseKey.CreateSubKey(RegistrySubKey) ?? throw new InvalidOperationException("Impossible de creer l'entree Windows de desinstallation WotLK.");
        var wowExe = GetGameExecutablePath(installRoot);
        var uninstallCommand = Quote(uninstallerExe) + " /uninstall-game";
        var quietUninstallCommand = Quote(uninstallerExe) + " /uninstall-game /quiet";

        key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", string.IsNullOrWhiteSpace(clientVersion) ? GetProductVersion() : clientVersion, RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
        key.SetValue("DisplayIcon", File.Exists(wowExe) ? wowExe : uninstallerExe, RegistryValueKind.String);
        key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", quietUninstallCommand, RegistryValueKind.String);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
        key.SetValue(
            "EstimatedSize",
            EstimateDirectorySizeKb(installRoot, rootLease),
            RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void WriteInstallMarker(
        string installRoot,
        string clientVersion,
        string uninstallerExe,
        GameInstallRootOwnership ownership,
        IGameInstallRootLease rootLease)
    {
        var marker = Path.Combine(installRoot, ClientMarkerFileName);
        var json = $$"""
        {
          "installedAt": "{{DateTimeOffset.Now:O}}",
          "clientVersion": "{{EscapeJson(clientVersion)}}",
          "installRoot": "{{EscapeJson(installRoot)}}",
          "uninstaller": "{{EscapeJson(uninstallerExe)}}",
          "registeredApp": "{{AppDisplayName}}",
          "ownershipId": "{{ownership.OwnershipId:N}}"
        }
        """;
        byte[] payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(json + Environment.NewLine);
        rootLease.WriteFileAtomically(marker, stream => stream.Write(payload));
    }

    internal static GameUninstallIdentity ValidateGameUninstallPreparation(
        string? currentExecutablePath,
        bool isElevated,
        Func<GameUninstallRegistryBinding> readRegistration)
    {
        ArgumentNullException.ThrowIfNull(readRegistration);
        DemandMediumIntegrity(isElevated);

        GameUninstallIdentity identity = DeriveGameUninstallIdentity(
            currentExecutablePath);
        GameUninstallRegistryBinding registration = readRegistration();
        ValidateGameUninstallRegistration(identity, registration);

        string markerPath = Path.Combine(
            identity.InstallRoot,
            ClientMarkerFileName);
        _ = ValidateGameInstallMarker(markerPath, identity);
        ValidateUninstallTreeNoReparse(identity.InstallRoot);
        return identity;
    }

    internal static void DemandMediumIntegrity(bool isElevated)
    {
        if (isElevated)
        {
            throw new UnauthorizedAccessException(
                "La desinstallation du client WotLK refuse de s'executer avec des droits administrateur.");
        }
    }

    internal static GameUninstallIdentity DeriveGameUninstallIdentity(
        string? currentExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(currentExecutablePath))
        {
            throw new InvalidOperationException(
                "Impossible de retrouver le desinstalleur WotLK en cours d'execution.");
        }

        string uninstallerPath = Path.GetFullPath(currentExecutablePath);
        if (!string.Equals(
                Path.GetFileName(uninstallerPath),
                UninstallerFileName,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(uninstallerPath))
        {
            throw new InvalidDataException(
                "La desinstallation doit etre lancee depuis WotLK Uninstaller.exe.");
        }

        string installRoot = Path.GetDirectoryName(uninstallerPath)
            ?? throw new InvalidDataException(
                "Le dossier du desinstalleur WotLK est invalide.");
        installRoot = NormalizeAndValidateGameRoot(installRoot);
        DemandNoReparsePoints(uninstallerPath, requireLeaf: true);
        return new GameUninstallIdentity(installRoot, uninstallerPath);
    }

    internal static void ValidateGameUninstallRegistration(
        GameUninstallIdentity identity,
        GameUninstallRegistryBinding registration)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(registration);

        string expectedUninstall = Quote(identity.UninstallerPath)
            + " /uninstall-game";
        string expectedQuietUninstall = expectedUninstall + " /quiet";
        if (!registration.UsesOnlyLiteralStrings
            || !string.Equals(
                registration.DisplayName,
                AppDisplayName,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(registration.InstallLocation)
            || !IsCanonicalAbsolutePath(
                registration.InstallLocation,
                isDirectory: true)
            || !SamePath(registration.InstallLocation, identity.InstallRoot)
            || !string.Equals(
                registration.UninstallString,
                expectedUninstall,
                StringComparison.Ordinal)
            || !string.Equals(
                registration.QuietUninstallString,
                expectedQuietUninstall,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "L'inscription Windows du client WotLK ne correspond pas au desinstalleur en cours.");
        }
    }

    internal static GameInstallMarker ValidateGameInstallMarker(
        string markerPath,
        GameUninstallIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string fullMarkerPath = Path.GetFullPath(markerPath);
        if (!SamePath(
                fullMarkerPath,
                Path.Combine(identity.InstallRoot, ClientMarkerFileName)))
        {
            throw new InvalidDataException(
                "Le marqueur WotLK doit se trouver dans la racine d'installation.");
        }

        DemandNoReparsePoints(fullMarkerPath, requireLeaf: true);
        byte[] bytes;
        using (FileStream markerStream = new(
                   fullMarkerPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            if (markerStream.Length <= 0
                || markerStream.Length > MaximumInstallMarkerBytes)
            {
                throw new InvalidDataException(
                    "Le marqueur d'installation WotLK a une taille invalide.");
            }

            bytes = new byte[checked((int)markerStream.Length)];
            markerStream.ReadExactly(bytes);
        }

        GameInstallMarker marker;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Le marqueur d'installation WotLK doit etre un objet JSON.");
            }

            HashSet<string> propertyNames = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "Le marqueur d'installation WotLK contient une propriete dupliquee.");
                }
            }

            marker = document.RootElement.Deserialize<GameInstallMarker>(
                    InstallMarkerJsonOptions)
                ?? throw new InvalidDataException(
                    "Le marqueur d'installation WotLK est vide.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Le marqueur d'installation WotLK est invalide.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(marker.InstallRoot)
            || string.IsNullOrWhiteSpace(marker.Uninstaller)
            || !IsCanonicalAbsolutePath(marker.InstallRoot, isDirectory: true)
            || !IsCanonicalAbsolutePath(marker.Uninstaller, isDirectory: false)
            || !SamePath(marker.InstallRoot, identity.InstallRoot)
            || !SamePath(marker.Uninstaller, identity.UninstallerPath)
            || !string.Equals(
                marker.RegisteredApp,
                AppDisplayName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Le marqueur d'installation WotLK ne correspond pas au desinstalleur en cours.");
        }

        ValidateGameRootOwnershipForUninstall(identity, marker);
        return marker;
    }

    private static GameUninstallRegistryBinding ReadGameUninstallRegistration()
    {
        using RegistryKey baseKey = OpenUninstallBaseKey();
        using RegistryKey key = baseKey.OpenSubKey(RegistrySubKey, writable: false)
            ?? throw new InvalidDataException(
                "L'inscription Windows du client WotLK est absente.");

        string[] requiredNames =
        [
            "DisplayName",
            "InstallLocation",
            "UninstallString",
            "QuietUninstallString"
        ];
        bool literalStrings = requiredNames.All(name =>
        {
            try
            {
                return key.GetValueKind(name) == RegistryValueKind.String;
            }
            catch (IOException)
            {
                return false;
            }
        });

        string? ReadLiteralString(string name) => key.GetValue(
            name,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

        return new GameUninstallRegistryBinding(
            ReadLiteralString("DisplayName"),
            ReadLiteralString("InstallLocation"),
            ReadLiteralString("UninstallString"),
            ReadLiteralString("QuietUninstallString"),
            literalStrings);
    }

    private static void UnregisterInstalledApp(GameUninstallIdentity identity)
    {
        GameUninstallRegistryBinding registration = ReadGameUninstallRegistration();
        ValidateGameUninstallRegistration(identity, registration);
        using RegistryKey baseKey = OpenUninstallBaseKey();
        baseKey.DeleteSubKeyTree(RegistrySubKey, throwOnMissingSubKey: false);
    }

    private static RegistryKey OpenUninstallBaseKey()
    {
        return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
    }

    private static bool TryNormalizeCleanupEventName(
        string value,
        out string cleanupEventName)
    {
        const string prefix = @"Local\Atlas.GameUninstall.";
        cleanupEventName = string.Empty;
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(value[prefix.Length..], "N", out Guid eventId))
        {
            return false;
        }

        cleanupEventName = prefix + eventId.ToString("N", CultureInfo.InvariantCulture);
        return string.Equals(value, cleanupEventName, StringComparison.Ordinal);
    }

    private static void StartGameUninstallCleanup(GameUninstallIdentity identity)
    {
        using TransientGameUninstallWorkspace workspace =
            CreateTransientGameUninstallWorkspace(identity);
        using EventWaitHandle accepted = new(
            initialState: false,
            EventResetMode.ManualReset,
            workspace.CleanupEventName,
            out bool createdNew);
        if (!createdNew)
        {
            throw new InvalidOperationException(
                "Impossible de creer le canal de validation du nettoyage WotLK.");
        }

        ProcessStartInfo startInfo = CreateGameUninstallCleanupStartInfo(
            workspace,
            identity,
            Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
        using Process cleanup = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Impossible de demarrer le nettoyage WotLK.");
        if (accepted.WaitOne(TimeSpan.FromSeconds(60)))
        {
            workspace.MarkHandedOff();
            return;
        }

        try
        {
            if (!cleanup.HasExited)
            {
                cleanup.Kill(entireProcessTree: true);
                cleanup.WaitForExit(10_000);
            }
        }
        catch
        {
        }

        throw new TimeoutException(
            "Le processus de nettoyage WotLK n'a pas valide la demande a temps.");
    }

    internal static ProcessStartInfo CreateGameUninstallCleanupStartInfo(
        TransientGameUninstallWorkspace workspace,
        GameUninstallIdentity identity,
        int parentProcessId,
        long parentStartTimeUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(identity);
        if (parentProcessId <= 0
            || parentStartTimeUtcTicks <= 0
            || !TryNormalizeCleanupEventName(
                workspace.CleanupEventName,
                out string normalizedEventName))
        {
            throw new ArgumentException(
                "La demande de nettoyage WotLK est invalide.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = workspace.HelperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(GameUninstallCleanupArgument);
        startInfo.ArgumentList.Add(workspace.WorkspacePath);
        startInfo.ArgumentList.Add(workspace.HelperPath);
        startInfo.ArgumentList.Add(identity.InstallRoot);
        startInfo.ArgumentList.Add(identity.UninstallerPath);
        startInfo.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(parentStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(workspace.RequesterSid);
        startInfo.ArgumentList.Add(normalizedEventName);
        startInfo.ArgumentList.Add(workspace.HelperSha256);
        return startInfo;
    }

    private static bool HasLiteralRegistryString(RegistryKey key, string name)
    {
        try
        {
            return key.GetValueKind(name) == RegistryValueKind.String;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ReadLiteralRegistryString(RegistryKey key, string name)
    {
        return key.GetValue(
            name,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    internal static void DemandNoReparsePoints(
        string path,
        bool requireLeaf = true)
    {
        string fullPath = Path.GetFullPath(path);
        string? current = fullPath;
        bool leaf = true;
        while (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "La desinstallation WotLK refuse les liens et points de jonction: "
                        + current);
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException
                or DirectoryNotFoundException)
            {
                if (leaf && requireLeaf)
                {
                    throw new InvalidDataException(
                        "Un element requis pour la desinstallation WotLK est absent: "
                        + current,
                        exception);
                }
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
            leaf = false;
        }
    }

    internal static void ValidateUninstallTreeNoReparse(string installRoot)
    {
        string root = NormalizeAndValidateGameRoot(installRoot);
        DemandNoReparsePoints(root, requireLeaf: true);
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            DemandNoReparsePoints(directory, requireLeaf: true);
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "La desinstallation WotLK refuse les liens et points de jonction: "
                        + entry);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    DemandSingleHardLink(entry);
                }
            }
        }
    }

    internal static void DeleteDirectoryTreeWithRetry(
        string installRoot,
        Action validateUnderLock,
        Action? afterPreflight = null)
    {
        ArgumentNullException.ThrowIfNull(validateUnderLock);
        string root = NormalizeAndValidateGameRoot(installRoot);
        ValidateUninstallTreeNoReparse(root);
        afterPreflight?.Invoke();
        List<SafeFileHandle> ancestorHandles = OpenAncestorDirectoryHandles(root);
        SafeFileHandle? rootHandle = null;
        try
        {
            rootHandle = OpenValidatedFileSystemEntry(
                root,
                expectDirectory: true,
                denyDeleteSharing: true,
                desiredAccess: DeleteAccess);
            validateUnderLock();
            ValidateUninstallTreeNoReparse(root);
            DeleteDirectoryContentsTopLevel(root);
            MarkDirectoryForDeletion(rootHandle, root);
            rootHandle.Dispose();
            rootHandle = null;
            if (Directory.Exists(root))
            {
                throw new IOException(
                    "Le dossier WotLK est encore présent après sa suppression verrouillée: "
                    + root);
            }
        }
        finally
        {
            rootHandle?.Dispose();
            for (int index = ancestorHandles.Count - 1; index >= 0; index--)
            {
                ancestorHandles[index].Dispose();
            }
        }
    }

    private static void DeleteDirectoryContentsTopLevel(string directory)
    {
        using SafeFileHandle directoryHandle = OpenValidatedFileSystemEntry(
            directory,
            expectDirectory: true,
            denyDeleteSharing: true);
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "La desinstallation WotLK refuse les liens et points de jonction: "
                    + entry);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryContentsTopLevel(entry);
                DeleteEmptyDirectoryWithRetry(entry);
                continue;
            }

            DeleteFileIfExistsWithRetry(entry);
        }
    }

    private static void DeleteEmptyDirectoryWithRetry(string directory)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                DemandNoReparsePoints(directory, requireLeaf: true);
                Directory.Delete(directory, recursive: false);
                return;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                lastError = exception;
                Thread.Sleep(250);
            }
        }

        throw new IOException(
            "Impossible de supprimer le dossier WotLK apres plusieurs essais: "
            + directory,
            lastError);
    }

    private static List<SafeFileHandle> OpenAncestorDirectoryHandles(
        string leafDirectory)
    {
        List<string> paths = new();
        DirectoryInfo? current = Directory.GetParent(
            Path.GetFullPath(leafDirectory));
        while (current is not null)
        {
            paths.Add(current.FullName);
            current = current.Parent;
        }

        paths.Reverse();
        List<SafeFileHandle> handles = new(paths.Count);
        try
        {
            foreach (string path in paths)
            {
                handles.Add(OpenValidatedFileSystemEntry(
                    path,
                    expectDirectory: true,
                    denyDeleteSharing: true));
            }

            return handles;
        }
        catch
        {
            for (int index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }

            throw;
        }
    }

    private static void DemandSingleHardLink(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using SafeFileHandle handle = OpenValidatedFileSystemEntry(
            path,
            expectDirectory: false,
            denyDeleteSharing: false);
        ByHandleFileInformation information = ReadFileInformation(handle, path);
        if (information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                "La desinstallation WotLK refuse un fichier lie a un autre emplacement: "
                + path);
        }
    }

    private static SafeFileHandle OpenValidatedFileSystemEntry(
        string path,
        bool expectDirectory,
        bool denyDeleteSharing,
        uint desiredAccess = 0,
        uint? shareModeOverride = null)
    {
        uint shareMode = shareModeOverride ?? (FileShareRead | FileShareWrite);
        if (shareModeOverride is null && !denyDeleteSharing)
        {
            shareMode |= FileShareDelete;
        }

        uint flags = FileFlagOpenReparsePoint;
        if (expectDirectory)
        {
            flags |= FileFlagBackupSemantics;
        }

        SafeFileHandle handle = CreateFileW(
            Path.GetFullPath(path),
            desiredAccess,
            shareMode,
            securityAttributes: IntPtr.Zero,
            OpenExisting,
            flags,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Impossible de verrouiller un element de la desinstallation WotLK: "
                + path,
                new Win32Exception(error));
        }

        try
        {
            ByHandleFileInformation information = ReadFileInformation(handle, path);
            bool isDirectory = (information.FileAttributes
                & (uint)FileAttributes.Directory) != 0;
            bool isReparsePoint = (information.FileAttributes
                & (uint)FileAttributes.ReparsePoint) != 0;
            if (isReparsePoint || isDirectory != expectDirectory)
            {
                throw new InvalidDataException(
                    "L'identite d'un element WotLK a change pendant la desinstallation: "
                    + path);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static ByHandleFileInformation ReadFileInformation(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new IOException(
                "Impossible de verifier l'identite d'un element WotLK: " + path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return information;
    }

    private static void MarkDirectoryForDeletion(
        SafeFileHandle directoryHandle,
        string path)
    {
        FileDispositionInformation disposition = new()
        {
            DeleteFile = true
        };
        if (!SetFileInformationByHandle(
                directoryHandle,
                FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new IOException(
                "Impossible de supprimer le dossier WotLK verrouillé: " + path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    internal static string NormalizeAndValidateGameRoot(string installRoot)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var driveRoot = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) ||
            string.IsNullOrWhiteSpace(driveRoot) ||
            SamePath(root, driveRoot))
        {
            throw new InvalidOperationException("Dossier WotLK refuse pour securite: " + installRoot);
        }

        DemandGameRootIsNotSensitive(root, installRoot);

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows) &&
            (SamePath(root, windows) || IsPathInside(windows, root)))
        {
            throw new InvalidOperationException("Dossier WotLK refuse pour securite: " + installRoot);
        }

        foreach (var protectedRoot in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                 })
        {
            if (!string.IsNullOrWhiteSpace(protectedRoot) && SamePath(root, protectedRoot))
            {
                throw new InvalidOperationException("Dossier WotLK refuse pour securite: " + installRoot);
            }
        }

        return root;
    }

    internal static string EnsureDefaultClientConfig(string installRoot, string locale)
    {
        string root = NormalizeAndValidateGameRoot(installRoot);
        using IGameInstallRootLease rootLease = AcquireGameInstallRootLease(
            root,
            GameInstallRootLeaseMode.ExistingClient);
        return EnsureDefaultClientConfig(root, locale, rootLease);
    }

    internal static string EnsureDefaultClientConfig(
        string installRoot,
        string locale,
        IGameInstallRootLease rootLease)
    {
        string root = DemandLeaseMatchesGameRoot(installRoot, rootLease);
        string gameLocale = LauncherSettings.NormalizeGameLocale(locale);

        string wtfDirectory = Path.Combine(GetClassicDirectoryPath(root), "WTF");
        using IGameInstallDirectoryLease wtfLease = rootLease.AcquireDirectory(
            wtfDirectory,
            createIfMissing: true);

        string configPath = Path.Combine(wtfDirectory, "Config.wtf");
        string videoDefaultsMarkerPath = Path.Combine(
            wtfDirectory,
            VideoDefaultsMarkerFileName);
        bool configExists = SafeChildFileExists(wtfLease, configPath);
        bool videoDefaultsMarkerExists = SafeChildFileExists(
            wtfLease,
            videoDefaultsMarkerPath);
        bool applyDesktopResolution = !configExists || !videoDefaultsMarkerExists;
        List<string> keptLines = [];
        bool instantQuestText = true;
        if (configExists)
        {
            IReadOnlyList<string> existingLines = ReadBoundedTextLines(
                rootLease,
                configPath,
                MaximumClientConfigBytes,
                "La configuration du client WotLK");
            instantQuestText = ReadInstantQuestTextValue(existingLines);
            foreach (string line in existingLines)
            {
                string? key = TryReadConfigKey(line);
                if (key is not null && IsManagedClientConfigKey(key, applyDesktopResolution))
                {
                    continue;
                }

                keptLines.Add(line);
            }
        }

        if (keptLines.Count > 0 && !string.IsNullOrWhiteSpace(keptLines[^1]))
        {
            keptLines.Add(string.Empty);
        }

        keptLines.Add($"SET locale \"{gameLocale}\"");
        keptLines.Add($"SET installLocale \"{gameLocale}\"");
        keptLines.Add($"SET textLocale \"{gameLocale}\"");
        keptLines.Add($"SET audioLocale \"{gameLocale}\"");
        keptLines.Add($"SET portal \"{PortalAddress}\"");

        string? desktopResolution = applyDesktopResolution
            ? TryGetPrimaryDesktopResolution()
            : null;
        if (!string.IsNullOrWhiteSpace(desktopResolution))
        {
            keptLines.Add($"SET gxResolution \"{desktopResolution}\"");
        }

        keptLines.Add("SET gxWindow \"1\"");
        keptLines.Add("SET gxMaximize \"1\"");
        keptLines.Add("SET gxVSync \"0\"");
        keptLines.Add("SET miniWorldMap \"1\"");
        keptLines.Add($"SET instantQuestText \"{(instantQuestText ? "1" : "0")}\"");

        WriteTextLinesAtomically(rootLease, configPath, keptLines);
        if (applyDesktopResolution)
        {
            WriteVideoDefaultsMarker(
                videoDefaultsMarkerPath,
                desktopResolution,
                rootLease);
        }

        return configPath;
    }

    internal static bool ReadInstantQuestText(string installRoot)
    {
        string root = NormalizeAndValidateGameRoot(installRoot);
        using IGameInstallRootLease rootLease = AcquireGameInstallRootLease(
            root,
            GameInstallRootLeaseMode.ExistingClient);
        return ReadInstantQuestText(root, rootLease);
    }

    internal static bool ReadInstantQuestText(
        string installRoot,
        IGameInstallRootLease rootLease)
    {
        string root = DemandLeaseMatchesGameRoot(installRoot, rootLease);
        string wtfDirectory = Path.Combine(GetClassicDirectoryPath(root), "WTF");
        try
        {
            using IGameInstallDirectoryLease wtfLease = rootLease.AcquireDirectory(
                wtfDirectory,
                createIfMissing: false);
            string configPath = Path.Combine(wtfDirectory, "Config.wtf");
            return SafeChildFileExists(wtfLease, configPath)
                ? ReadInstantQuestTextValue(ReadBoundedTextLines(
                    rootLease,
                    configPath,
                    MaximumClientConfigBytes,
                    "La configuration du client WotLK"))
                : true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    internal static bool SetInstantQuestText(string installRoot, bool enabled)
    {
        string root = NormalizeAndValidateGameRoot(installRoot);
        using IGameInstallRootLease rootLease = AcquireGameInstallRootLease(
            root,
            GameInstallRootLeaseMode.ExistingClient);
        return SetInstantQuestText(root, enabled, rootLease);
    }

    internal static bool SetInstantQuestText(
        string installRoot,
        bool enabled,
        IGameInstallRootLease rootLease)
    {
        string root = DemandLeaseMatchesGameRoot(installRoot, rootLease);
        string wtfDirectory = Path.Combine(GetClassicDirectoryPath(root), "WTF");
        using IGameInstallDirectoryLease wtfLease = rootLease.AcquireDirectory(
            wtfDirectory,
            createIfMissing: true);
        string configPath = Path.Combine(wtfDirectory, "Config.wtf");
        List<string> existingLines = SafeChildFileExists(wtfLease, configPath)
            ? ReadBoundedTextLines(
                rootLease,
                configPath,
                MaximumClientConfigBytes,
                "La configuration du client WotLK").ToList()
            : [];

        if (ReadInstantQuestTextValue(existingLines) == enabled
            && existingLines.Any(IsInstantQuestTextLine))
        {
            return false;
        }

        var updatedLines = new List<string>(existingLines.Count + 1);
        var insertionIndex = -1;
        foreach (var line in existingLines)
        {
            if (IsInstantQuestTextLine(line))
            {
                insertionIndex = insertionIndex < 0 ? updatedLines.Count : insertionIndex;
                continue;
            }

            updatedLines.Add(line);
        }

        var setting = $"SET instantQuestText \"{(enabled ? "1" : "0")}\"";
        if (insertionIndex >= 0)
        {
            updatedLines.Insert(insertionIndex, setting);
        }
        else
        {
            updatedLines.Add(setting);
        }

        WriteTextLinesAtomically(rootLease, configPath, updatedLines);
        return true;
    }

    private static bool SafeChildFileExists(
        IGameInstallDirectoryLease parentLease,
        string path)
    {
        parentLease.DemandChildFileSafe(path, allowMissing: true);
        return File.Exists(path);
    }

    private static IReadOnlyList<string> ReadBoundedTextLines(
        IGameInstallRootLease rootLease,
        string path,
        int maximumBytes,
        string description)
    {
        using IGameInstallReadLease readLease = rootLease.OpenFileForRead(path);
        FileStream stream = readLease.Stream;
        if (stream.Length < 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException(
                description + " dépasse la taille maximale autorisée.");
        }

        List<string> lines = [];
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static void WriteTextLinesAtomically(
        IGameInstallRootLease rootLease,
        string path,
        IEnumerable<string> lines)
    {
        rootLease.WriteFileAtomically(path, stream =>
        {
            using StreamWriter writer = new(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16 * 1024,
                leaveOpen: true);
            foreach (string line in lines)
            {
                writer.WriteLine(line);
            }

            writer.Flush();
            if (stream.Length > MaximumClientConfigBytes)
            {
                throw new InvalidDataException(
                    "La configuration du client WotLK générée est trop volumineuse.");
            }
        });
    }

    private static bool ReadInstantQuestTextValue(IEnumerable<string> lines)
    {
        var enabled = true;
        foreach (var line in lines)
        {
            if (!TryReadConfigValue(line, "instantQuestText", out var value))
            {
                continue;
            }

            if (string.Equals(value, "0", StringComparison.Ordinal))
            {
                enabled = false;
            }
            else if (string.Equals(value, "1", StringComparison.Ordinal))
            {
                enabled = true;
            }
        }

        return enabled;
    }

    private static bool IsInstantQuestTextLine(string line)
    {
        return string.Equals(
            TryReadConfigKey(line),
            "instantQuestText",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadConfigValue(string line, string expectedKey, out string value)
    {
        value = string.Empty;
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[4..].TrimStart();
        var keyEnd = rest.IndexOfAny([' ', '\t']);
        if (keyEnd < 0
            || !string.Equals(rest[..keyEnd], expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawValue = rest[keyEnd..].Trim();
        if (rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[^1] == '"')
        {
            value = rawValue[1..^1];
            return true;
        }

        value = rawValue;
        return true;
    }

    private static string? TryReadConfigKey(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = trimmed[4..].TrimStart();
        if (rest.Length == 0)
        {
            return null;
        }

        var end = rest.IndexOfAny([' ', '\t']);
        return end < 0 ? rest : rest[..end];
    }

    private static bool IsManagedClientConfigKey(string key, bool applyDesktopResolution)
    {
        return string.Equals(key, "locale", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "installLocale", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "textLocale", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "audioLocale", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "portal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "gxWindow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "gxMaximize", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "gxVSync", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "miniWorldMap", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "instantQuestText", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "gxRefresh", StringComparison.OrdinalIgnoreCase) ||
               (applyDesktopResolution && string.Equals(key, "gxResolution", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteVideoDefaultsMarker(
        string markerPath,
        string? desktopResolution,
        IGameInstallRootLease rootLease)
    {
        var json = $$"""
        {
          "appliedAt": "{{DateTimeOffset.Now:O}}",
          "desktopResolution": "{{EscapeJson(desktopResolution ?? string.Empty)}}"
        }
        """;
        byte[] payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(json + Environment.NewLine);
        rootLease.WriteFileAtomically(markerPath, stream => stream.Write(payload));
    }

    private static string? TryGetPrimaryDesktopResolution()
    {
        try
        {
            var width = GetSystemMetrics(SystemMetricPrimaryScreenWidth);
            var height = GetSystemMetrics(SystemMetricPrimaryScreenHeight);
            return width > 0 && height > 0 ? $"{width}x{height}" : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    internal static void StopRunningGameProcesses(string installRoot)
    {
        var root = NormalizeAndValidateGameRoot(installRoot);
        StopRunningWow(root);
    }

    internal static bool IsGameRunning(string installRoot)
    {
        var root = NormalizeAndValidateGameRoot(installRoot);
        foreach (var processName in new[] { "Wow", "WowClassic" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId && ProcessMatchesInstallRoot(process, root))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void StopRunningWow(string installRoot)
    {
        foreach (var processName in new[] { "Wow", "WowClassic", "Arctium Game Launcher Atlas" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId || !ProcessMatchesInstallRoot(process, installRoot))
                        {
                            continue;
                        }
                        if (process.CloseMainWindow() && process.WaitForExit(5000))
                        {
                            continue;
                        }
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(10000);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private static bool ProcessMatchesInstallRoot(Process process, string installRoot)
    {
        return ProcessPathMatchesInstallRoot(
            installRoot,
            () => process.MainModule?.FileName);
    }

    internal static bool ProcessPathMatchesInstallRoot(
        string installRoot,
        Func<string?> readProcessPath)
    {
        ArgumentNullException.ThrowIfNull(readProcessPath);
        try
        {
            string? processPath = readProcessPath();
            return !string.IsNullOrWhiteSpace(processPath)
                && IsPathInside(installRoot, processPath);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ProcessPathMatchesExpected(
        Func<string?> readProcessPath,
        string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(readProcessPath);
        try
        {
            string? processPath = readProcessPath();
            return !string.IsNullOrWhiteSpace(processPath)
                && SamePath(processPath, expectedPath);
        }
        catch
        {
            return false;
        }
    }

    private static void TrySetNormalAttributes(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
        }
    }

    private static void DeleteFileIfExistsWithRetry(string path)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (SafeFileHandle handle = OpenValidatedFileSystemEntry(
                               path,
                               expectDirectory: false,
                               denyDeleteSharing: true))
                    {
                        ByHandleFileInformation information = ReadFileInformation(
                            handle,
                            path);
                        if (information.NumberOfLinks != 1)
                        {
                            throw new InvalidDataException(
                                "La desinstallation WotLK refuse un fichier lie a un autre emplacement: "
                                + path);
                        }

                        File.SetAttributes(path, FileAttributes.Normal);
                    }

                    File.Delete(path);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(500);
            }
        }
        throw new IOException("Impossible de supprimer le fichier apres plusieurs essais: " + path, lastError);
    }

    private static int EstimateDirectorySizeKb(
        string installRoot,
        IGameInstallRootLease rootLease)
    {
        if (!Directory.Exists(installRoot))
        {
            return 0;
        }

        long bytes = EstimateDirectorySizeBytesNoFollow(
            installRoot,
            rootLease);
        return (int)Math.Min(int.MaxValue, Math.Max(1, bytes / 1024));
    }

    private static long EstimateDirectorySizeBytesNoFollow(
        string directory,
        IGameInstallRootLease rootLease)
    {
        using IGameInstallDirectoryLease directoryLease = rootLease.AcquireDirectory(
            directory,
            createIfMissing: false);
        directoryLease.Revalidate();

        long total = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception exception) when (exception is FileNotFoundException
                                               or DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Le calcul de taille WotLK refuse un lien de système de fichiers: "
                    + entry);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                directoryLease.DemandChildDirectorySafe(entry, allowMissing: false);
                total = checked(total + EstimateDirectorySizeBytesNoFollow(
                    entry,
                    rootLease));
                continue;
            }

            directoryLease.DemandChildFileSafe(entry, allowMissing: false);
            using IGameInstallReadLease readLease = rootLease.OpenFileForRead(entry);
            total = checked(total + readLease.Stream.Length);
        }

        return total;
    }

    private static bool IsPathInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProductVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        private uint _creationTimeLow;
        private uint _creationTimeHigh;
        private uint _lastAccessTimeLow;
        private uint _lastAccessTimeHigh;
        private uint _lastWriteTimeLow;
        private uint _lastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        private uint _fileSizeHigh;
        private uint _fileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool DeleteFile;
    }

    private static string Quote(string value) => "\"" + value + "\"";
    private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

internal sealed record GameUninstallIdentity(
    string InstallRoot,
    string UninstallerPath);

internal sealed record GameUninstallRegistryBinding(
    string? DisplayName,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    bool UsesOnlyLiteralStrings);

internal sealed record GameUninstallCleanupRequest(
    string WorkspacePath,
    string HelperPath,
    string InstallRoot,
    string UninstallerPath,
    int ParentProcessId,
    long ParentStartTimeUtcTicks,
    string RequesterSid,
    string CleanupEventName,
    string ExpectedHelperSha256);

internal sealed class GameInstallMarker
{
    [JsonRequired]
    public DateTimeOffset InstalledAt { get; init; }

    [JsonRequired]
    public string ClientVersion { get; init; } = string.Empty;

    [JsonRequired]
    public string InstallRoot { get; init; } = string.Empty;

    [JsonRequired]
    public string Uninstaller { get; init; } = string.Empty;

    [JsonRequired]
    public string RegisteredApp { get; init; } = string.Empty;

    public string? OwnershipId { get; init; }
}
