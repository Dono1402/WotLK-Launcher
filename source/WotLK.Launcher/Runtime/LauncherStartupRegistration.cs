using System.IO;
using Microsoft.Win32;

namespace WotLK.Launcher.Runtime;

internal enum LauncherStartupRegistrationStatus
{
    Applied,
    Failed
}

internal readonly record struct LauncherStartupRegistrationResult(
    LauncherStartupRegistrationStatus Status,
    string? FailureCategory = null)
{
    internal bool IsApplied => Status == LauncherStartupRegistrationStatus.Applied;
}

internal interface ILauncherStartupRegistration
{
    bool IsRegistered { get; }

    bool IsEnabled { get; }

    LauncherStartupRegistrationResult TrySetEnabled(bool enabled);
}

internal interface ILauncherStartupRegistry
{
    string? Read(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);
}

internal sealed class WindowsLauncherStartupRegistry : ILauncherStartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
    }

    public void Write(string valueName, string command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("La clé de démarrage Windows est indisponible.");
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

internal sealed class WindowsLauncherStartupRegistration : ILauncherStartupRegistration
{
    internal const string AutoStartArgument = "--autostart";

    private readonly ILauncherStartupRegistry _registry;
    private readonly string _valueName;
    private readonly string _command;

    internal WindowsLauncherStartupRegistration(
        string? executablePath = null,
        ILauncherStartupRegistry? registry = null,
        string? valueName = null)
    {
        string path = executablePath ?? Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Le chemin du launcher est indisponible.");
        }

        _registry = registry ?? new WindowsLauncherStartupRegistry();
        _valueName = valueName ?? (LauncherBuildFlavor.IsLocalClient
            ? "Atlas Launcher Local"
            : "Atlas Launcher");
        _command = $"{QuoteExecutable(path)} {AutoStartArgument}";
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                return string.Equals(
                    _registry.Read(_valueName),
                    _command,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsRegistered
    {
        get
        {
            try
            {
                return _registry.Read(_valueName) is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public LauncherStartupRegistrationResult TrySetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                _registry.Write(_valueName, _command);
            }
            else
            {
                _registry.Delete(_valueName);
            }

            return new LauncherStartupRegistrationResult(
                LauncherStartupRegistrationStatus.Applied);
        }
        catch (Exception exception)
        {
            return new LauncherStartupRegistrationResult(
                LauncherStartupRegistrationStatus.Failed,
                exception.GetType().Name);
        }
    }

    internal static string QuoteExecutable(string executablePath) =>
        $"\"{Path.GetFullPath(executablePath)}\"";
}
