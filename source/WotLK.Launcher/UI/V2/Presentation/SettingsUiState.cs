using System.Windows.Input;
using WotLK.Launcher.UI.V2.Commands;

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
    string ClientVersion,
    string GameLocale = "frFR");

public sealed record UpdateSettingsViewState(
    bool AutomaticLauncherUpdates,
    string ClientUpdateBehavior,
    string ReleaseChannel,
    string LastUpdateCheck,
    string InstalledLauncherVersion,
    string AvailableLauncherVersion,
    bool IsChecking = false,
    bool IsUpdateAvailable = false,
    bool IsUpdating = false,
    bool CanCheck = false,
    bool CanStartUpdate = false,
    string StatusMessage = "Non vérifié");

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
    DiagnosticSettingsViewState Diagnostic,
    bool IsRuntimeConnected = false,
    bool CanChangeInstallPath = false,
    bool CanChangeGameLocale = false,
    bool CanChangeBehavior = false,
    bool CanChangeInstantQuestText = false,
    bool AreDeferredControlsEnabled = true,
    string? SaveStatusDetail = null,
    string? SaveStatusTitle = null,
    string? RuntimeNoticeMessage = null);

public sealed class SettingsUiState : BindableUiState
{
    private SettingsViewState _current;
    private ICommand _browseInstallPathCommand = DisabledCommand.Instance;
    private ICommand _openGameFolderCommand = DisabledCommand.Instance;
    private ICommand _openLogsCommand = DisabledCommand.Instance;
    private ICommand _verifyRepairCommand = DisabledCommand.Instance;
    private ICommand _checkLauncherUpdateCommand = DisabledCommand.Instance;
    private ICommand _startLauncherUpdateCommand = DisabledCommand.Instance;
    private Func<string, bool> _changeGameLocale = static _ => false;
    private Func<bool, bool> _changeCloseAfterLaunch = static _ => false;
    private Func<bool, bool> _changeInstantQuestText = static _ => false;
    private Action _showGameForRepair = static () => { };

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
        _current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public SettingsViewState Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public ICommand BrowseInstallPathCommand
    {
        get => _browseInstallPathCommand;
        private set => SetProperty(ref _browseInstallPathCommand, value);
    }

    public ICommand OpenGameFolderCommand
    {
        get => _openGameFolderCommand;
        private set => SetProperty(ref _openGameFolderCommand, value);
    }

    public ICommand OpenLogsCommand
    {
        get => _openLogsCommand;
        private set => SetProperty(ref _openLogsCommand, value);
    }

    public ICommand VerifyRepairCommand
    {
        get => _verifyRepairCommand;
        private set => SetProperty(ref _verifyRepairCommand, value);
    }

    public ICommand CheckLauncherUpdateCommand
    {
        get => _checkLauncherUpdateCommand;
        private set => SetProperty(ref _checkLauncherUpdateCommand, value);
    }

    public ICommand StartLauncherUpdateCommand
    {
        get => _startLauncherUpdateCommand;
        private set => SetProperty(ref _startLauncherUpdateCommand, value);
    }

    internal void ApplyRuntimeView(SettingsViewState viewState)
    {
        Current = viewState ?? throw new ArgumentNullException(nameof(viewState));
    }

    internal void AttachRuntimeActions(
        ICommand browseInstallPathCommand,
        ICommand openGameFolderCommand,
        ICommand openLogsCommand,
        ICommand verifyRepairCommand,
        ICommand checkLauncherUpdateCommand,
        ICommand startLauncherUpdateCommand,
        Action showGameForRepair,
        Func<string, bool> changeGameLocale,
        Func<bool, bool> changeCloseAfterLaunch,
        Func<bool, bool> changeInstantQuestText)
    {
        BrowseInstallPathCommand = browseInstallPathCommand
            ?? throw new ArgumentNullException(nameof(browseInstallPathCommand));
        OpenGameFolderCommand = openGameFolderCommand
            ?? throw new ArgumentNullException(nameof(openGameFolderCommand));
        OpenLogsCommand = openLogsCommand
            ?? throw new ArgumentNullException(nameof(openLogsCommand));
        VerifyRepairCommand = verifyRepairCommand
            ?? throw new ArgumentNullException(nameof(verifyRepairCommand));
        CheckLauncherUpdateCommand = checkLauncherUpdateCommand
            ?? throw new ArgumentNullException(nameof(checkLauncherUpdateCommand));
        StartLauncherUpdateCommand = startLauncherUpdateCommand
            ?? throw new ArgumentNullException(nameof(startLauncherUpdateCommand));
        _showGameForRepair = showGameForRepair
            ?? throw new ArgumentNullException(nameof(showGameForRepair));
        _changeGameLocale = changeGameLocale
            ?? throw new ArgumentNullException(nameof(changeGameLocale));
        _changeCloseAfterLaunch = changeCloseAfterLaunch
            ?? throw new ArgumentNullException(nameof(changeCloseAfterLaunch));
        _changeInstantQuestText = changeInstantQuestText
            ?? throw new ArgumentNullException(nameof(changeInstantQuestText));
    }

    internal void AttachPreviewActions()
    {
        BrowseInstallPathCommand = PreviewCommand.Instance;
        OpenGameFolderCommand = PreviewCommand.Instance;
        OpenLogsCommand = PreviewCommand.Instance;
        VerifyRepairCommand = PreviewCommand.Instance;
        CheckLauncherUpdateCommand = PreviewCommand.Instance;
        StartLauncherUpdateCommand = PreviewCommand.Instance;
        _showGameForRepair = static () => { };
        _changeGameLocale = static _ => false;
        _changeCloseAfterLaunch = static _ => false;
        _changeInstantQuestText = static _ => false;
    }

    internal bool TryChangeGameLocale(string locale)
    {
        return Current.IsRuntimeConnected
            && Current.CanChangeGameLocale
            && _changeGameLocale(locale);
    }

    internal bool TryChangeCloseAfterLaunch(bool closeAfterLaunch)
    {
        return Current.IsRuntimeConnected
            && Current.CanChangeBehavior
            && _changeCloseAfterLaunch(closeAfterLaunch);
    }

    internal bool TryChangeInstantQuestText(bool enabled)
    {
        return Current.IsRuntimeConnected
            && Current.CanChangeInstantQuestText
            && _changeInstantQuestText(enabled);
    }

    internal void ShowGameForRepair()
    {
        if (Current.IsRuntimeConnected)
        {
            _showGameForRepair();
        }
    }

    internal void ShowRuntimeActionFailure(string message)
    {
        if (!Current.IsRuntimeConnected || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Current = Current with
        {
            RuntimeNoticeMessage = message
        };
    }
}
