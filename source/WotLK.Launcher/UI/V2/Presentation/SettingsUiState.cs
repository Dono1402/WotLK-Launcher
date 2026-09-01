namespace WotLK.Launcher.UI.V2.Presentation;

public sealed record SettingsViewState(
    string InstallPath,
    string GameLanguage,
    bool AutomaticLauncherUpdates,
    string AutomaticLauncherUpdatesStatus,
    bool CloseLauncherAfterGameStart,
    string CloseLauncherAfterGameStartStatus,
    string LauncherVersion,
    string ClientVersion,
    string ReleaseChannel,
    string LogLocation);

public sealed class SettingsUiState
{
    internal static SettingsUiState Empty { get; } = new(new SettingsViewState(
        InstallPath: string.Empty,
        GameLanguage: string.Empty,
        AutomaticLauncherUpdates: false,
        AutomaticLauncherUpdatesStatus: string.Empty,
        CloseLauncherAfterGameStart: false,
        CloseLauncherAfterGameStartStatus: string.Empty,
        LauncherVersion: string.Empty,
        ClientVersion: string.Empty,
        ReleaseChannel: string.Empty,
        LogLocation: string.Empty));

    public SettingsUiState(SettingsViewState current)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public SettingsViewState Current { get; }
}
