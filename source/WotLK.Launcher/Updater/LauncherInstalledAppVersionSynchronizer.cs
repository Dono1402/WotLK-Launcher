using Microsoft.Win32;
using System.IO;

namespace WotLK.Launcher.Updater;

internal enum LauncherInstalledAppVersionSyncStatus
{
    Updated,
    NeedsUpdate,
    AlreadyCurrent,
    EntryMissing,
    EntryNotOfficial,
    InstallLocationMismatch,
    InvalidAuthenticatedVersion,
    UnsupportedPlatform,
    Failed
}

internal readonly record struct LauncherInstalledAppVersionSyncResult(
    LauncherInstalledAppVersionSyncStatus Status,
    string? FailureCategory = null);

internal interface ILauncherInstalledAppVersionSynchronizer
{
    LauncherInstalledAppVersionSyncResult Synchronize(
        LauncherUpdateTransaction transaction);
}

internal interface ILauncherInstalledAppVersionRegistry
{
    LauncherInstalledAppVersionSyncResult TrySetDisplayVersion(
        string expectedInstallLocation,
        string expectedLauncherPath,
        string displayVersion);
}

internal sealed class LauncherInstalledAppVersionSynchronizer(
    ILauncherInstalledAppVersionRegistry registry)
    : ILauncherInstalledAppVersionSynchronizer
{
    private readonly ILauncherInstalledAppVersionRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    internal static LauncherInstalledAppVersionSynchronizer CreateProduction() =>
        new(WindowsLauncherInstalledAppVersionRegistry.CreateProduction());

    public LauncherInstalledAppVersionSyncResult Synchronize(
        LauncherUpdateTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!LauncherUpdateVersionPolicy.IsValid(
                transaction.AuthenticatedTargetVersion))
        {
            return new LauncherInstalledAppVersionSyncResult(
                LauncherInstalledAppVersionSyncStatus.InvalidAuthenticatedVersion);
        }

        try
        {
            string targetPath = Path.GetFullPath(transaction.TargetPath);
            string? installLocation = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(installLocation))
            {
                return new LauncherInstalledAppVersionSyncResult(
                    LauncherInstalledAppVersionSyncStatus.InstallLocationMismatch);
            }

            return _registry.TrySetDisplayVersion(
                installLocation,
                targetPath,
                transaction.AuthenticatedTargetVersion!);
        }
        catch (Exception exception)
        {
            return new LauncherInstalledAppVersionSyncResult(
                LauncherInstalledAppVersionSyncStatus.Failed,
                exception.GetType().Name);
        }
    }
}

internal sealed class WindowsLauncherInstalledAppVersionRegistry
    : ILauncherInstalledAppVersionRegistry
{
    internal const string StableRegistrySubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AtlasLauncher";
    internal const string StableDisplayName = "Atlas Launcher";
    internal const string StablePublisher = "Atlas";
    internal const string LegacyPublisher = "AnimeClub";
    private const string IsolatedTestKeyPrefix =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AtlasLauncher.04D3.Test.";

    private readonly string _registrySubKey;
    private readonly string _expectedDisplayName;
    private readonly string _expectedPublisher;
    private readonly string _legacyPublisher;
    private readonly RegistryHive _registryHive;

    private WindowsLauncherInstalledAppVersionRegistry(
        string registrySubKey,
        string expectedDisplayName,
        string expectedPublisher,
        string legacyPublisher,
        RegistryHive registryHive)
    {
        _registrySubKey = registrySubKey;
        _expectedDisplayName = expectedDisplayName;
        _expectedPublisher = expectedPublisher;
        _legacyPublisher = legacyPublisher;
        _registryHive = registryHive;
    }

    internal string RegistrySubKey => _registrySubKey;

    internal string ExpectedDisplayName => _expectedDisplayName;

    internal string ExpectedPublisher => _expectedPublisher;

    internal string AcceptedLegacyPublisher => _legacyPublisher;

    internal RegistryHive RegistryHive => _registryHive;

    internal static WindowsLauncherInstalledAppVersionRegistry CreateProduction() =>
        new(
            StableRegistrySubKey,
            StableDisplayName,
            StablePublisher,
            LegacyPublisher,
            RegistryHive.LocalMachine);

    internal static WindowsLauncherInstalledAppVersionRegistry CreateIsolatedTest(
        Guid testId,
        bool machineWide = false)
    {
        if (testId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(testId));
        }

        string suffix = testId.ToString("N");
        return new WindowsLauncherInstalledAppVersionRegistry(
            IsolatedTestKeyPrefix + suffix,
            "Atlas Launcher 04D.3 Test " + suffix,
            "Atlas Test",
            "AnimeClub Test",
            machineWide ? RegistryHive.LocalMachine : RegistryHive.CurrentUser);
    }

    public LauncherInstalledAppVersionSyncResult TrySetDisplayVersion(
        string expectedInstallLocation,
        string expectedLauncherPath,
        string displayVersion) => SynchronizeRegistration(
            expectedInstallLocation, expectedLauncherPath, displayVersion, writable: true);

    internal LauncherInstalledAppVersionSyncResult SynchronizePublisher(
        string expectedInstallLocation,
        string expectedLauncherPath,
        bool writable) => SynchronizeRegistration(
            expectedInstallLocation, expectedLauncherPath, displayVersion: null, writable);

    private LauncherInstalledAppVersionSyncResult SynchronizeRegistration(
        string expectedInstallLocation,
        string expectedLauncherPath,
        string? displayVersion,
        bool writable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new LauncherInstalledAppVersionSyncResult(
                LauncherInstalledAppVersionSyncStatus.UnsupportedPlatform);
        }

        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(
                _registryHive,
                RegistryView.Registry64);
            using RegistryKey? key = machine.OpenSubKey(
                _registrySubKey,
                writable: writable);
            if (key is null)
            {
                return new LauncherInstalledAppVersionSyncResult(
                    LauncherInstalledAppVersionSyncStatus.EntryMissing);
            }

            string? registeredPublisher = key.GetValue("Publisher") as string;
            bool publisherCurrent = string.Equals(
                registeredPublisher, _expectedPublisher, StringComparison.Ordinal);
            if (!string.Equals(
                    key.GetValue("DisplayName") as string,
                    _expectedDisplayName,
                    StringComparison.Ordinal)
                || (!publisherCurrent && !string.Equals(
                    registeredPublisher, _legacyPublisher, StringComparison.Ordinal)))
            {
                return new LauncherInstalledAppVersionSyncResult(
                    LauncherInstalledAppVersionSyncStatus.EntryNotOfficial);
            }

            string? registeredLocation = key.GetValue("InstallLocation") as string;
            string? registeredLauncher = key.GetValue("DisplayIcon") as string;
            if (!SamePath(registeredLocation, expectedInstallLocation)
                || !SamePath(registeredLauncher, expectedLauncherPath))
            {
                return new LauncherInstalledAppVersionSyncResult(
                    LauncherInstalledAppVersionSyncStatus.InstallLocationMismatch);
            }

            bool versionCurrent = displayVersion is null || string.Equals(
                    key.GetValue("DisplayVersion") as string,
                    displayVersion,
                    StringComparison.Ordinal);
            if (versionCurrent && publisherCurrent)
            {
                return new LauncherInstalledAppVersionSyncResult(
                    LauncherInstalledAppVersionSyncStatus.AlreadyCurrent);
            }

            if (!writable)
            {
                return new LauncherInstalledAppVersionSyncResult(
                    LauncherInstalledAppVersionSyncStatus.NeedsUpdate);
            }

            if (!versionCurrent)
            {
                key.SetValue("DisplayVersion", displayVersion!, RegistryValueKind.String);
            }
            if (!publisherCurrent)
            {
                key.SetValue("Publisher", _expectedPublisher, RegistryValueKind.String);
            }
            return new LauncherInstalledAppVersionSyncResult(
                LauncherInstalledAppVersionSyncStatus.Updated);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or System.Security.SecurityException
                                          or IOException)
        {
            return new LauncherInstalledAppVersionSyncResult(
                LauncherInstalledAppVersionSyncStatus.Failed,
                exception.GetType().Name);
        }
    }

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Normalize(left),
                Normalize(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
