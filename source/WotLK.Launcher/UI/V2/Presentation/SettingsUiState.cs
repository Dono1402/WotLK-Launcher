namespace WotLK.Launcher.UI.V2.Presentation;

public enum SettingsCategory
{
    General,
    Game,
    Updates,
    Notifications,
    Appearance,
    Diagnostic
}

public enum SettingsSavePreviewState
{
    None,
    Dirty,
    Saving,
    Saved,
    Error
}

public sealed record GeneralSettingsViewState(
    string InterfaceLanguage,
    bool StartWithWindows,
    string WindowCloseBehavior,
    bool CloseLauncherAfterGameStart);

public sealed record GameSettingsViewState(
    string InstallPath,
    string GameLanguage,
    string VideoSettingsLocation,
    bool InstantQuestText,
    string ClientVersion);

public sealed record UpdateSettingsViewState(
    bool AutomaticLauncherUpdates,
    string ClientUpdateBehavior,
    string ReleaseChannel,
    string LastUpdateCheck,
    string InstalledLauncherVersion,
    string AvailableLauncherVersion);

public sealed record NotificationSettingsViewState(
    bool UpdateCompleted,
    bool Errors,
    bool FriendRequests,
    bool FriendPresence,
    bool Sounds);

public sealed record AppearanceSettingsViewState(
    bool ReduceAnimations,
    string InterfaceScale,
    double EffectsIntensity,
    string EffectsIntensityLabel);

public sealed record DiagnosticSettingsViewState(
    string LogLocation,
    string LauncherLocation,
    string LauncherVersion,
    string ClientVersion,
    string LocalState,
    string ServiceState);

public sealed record SettingsViewState(
    SettingsCategory InitialCategory,
    SettingsSavePreviewState SavePreviewState,
    GeneralSettingsViewState General,
    GameSettingsViewState Game,
    UpdateSettingsViewState Updates,
    NotificationSettingsViewState Notifications,
    AppearanceSettingsViewState Appearance,
    DiagnosticSettingsViewState Diagnostic);

public sealed class SettingsUiState
{
    internal static SettingsUiState Empty { get; } = new(new SettingsViewState(
        SettingsCategory.General,
        SettingsSavePreviewState.None,
        new GeneralSettingsViewState(string.Empty, false, string.Empty, false),
        new GameSettingsViewState(string.Empty, string.Empty, string.Empty, false, string.Empty),
        new UpdateSettingsViewState(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
        new NotificationSettingsViewState(false, false, false, false, false),
        new AppearanceSettingsViewState(false, string.Empty, 0, string.Empty),
        new DiagnosticSettingsViewState(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty)));

    public SettingsUiState(SettingsViewState current)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public SettingsViewState Current { get; }
}
