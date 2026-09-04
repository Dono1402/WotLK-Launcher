using System.Windows.Input;
using WotLK.Launcher.UI.V2.Commands;

namespace WotLK.Launcher.UI.V2.Presentation;

public enum SettingsCategory
{
    General,
    Game,
    Updates,
    Notifications,
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
    string InterfaceLocale,
    bool StartWithWindows,
    bool MinimizeToTrayOnClose);

public sealed record GameSettingsViewState(
    string InstallPath,
    string GameLanguage,
    bool InstantQuestText,
    string ClientVersion,
    string GameLocale = "frFR");

public sealed record UpdateSettingsViewState(
    string InstalledLauncherVersion,
    string AvailableLauncherVersion,
    bool IsChecking = false,
    bool IsUpdateAvailable = false,
    bool IsUpdating = false,
    bool CanCheck = false,
    bool CanStartUpdate = false);

public sealed record NotificationSettingsViewState(
    bool FriendPresence);

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
    DiagnosticSettingsViewState Diagnostic,
    bool IsRuntimeConnected = false,
    bool CanChangeInstallPath = false,
    bool CanChangeGameLocale = false,
    bool CanChangeBehavior = false,
    bool CanChangeInstantQuestText = false,
    bool CanChangeInterfaceLocale = false,
    bool CanChangeStartWithWindows = false,
    bool CanChangeFriendPresenceNotifications = false,
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
    private Func<string, bool> _changeInterfaceLocale = static _ => false;
    private Func<bool, bool> _changeStartWithWindows = static _ => false;
    private Func<bool, bool> _changeMinimizeToTrayOnClose = static _ => false;
    private Func<bool, bool> _changeFriendPresenceNotifications = static _ => false;
    private Func<string, bool> _changeGameLocale = static _ => false;
    private Func<bool, bool> _changeInstantQuestText = static _ => false;
    private Action _showGameForRepair = static () => { };

    internal static SettingsUiState Empty { get; } = new(new SettingsViewState(
        SettingsCategory.General,
        SettingsSavePreviewState.None,
        new GeneralSettingsViewState(string.Empty, "fr-FR", false, true),
        new GameSettingsViewState(string.Empty, string.Empty, false, string.Empty),
        new UpdateSettingsViewState(string.Empty, string.Empty),
        new NotificationSettingsViewState(true),
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
        Func<string, bool> changeInterfaceLocale,
        Func<bool, bool> changeStartWithWindows,
        Func<bool, bool> changeMinimizeToTrayOnClose,
        Func<bool, bool> changeFriendPresenceNotifications,
        Func<string, bool> changeGameLocale,
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
        _changeInterfaceLocale = changeInterfaceLocale
            ?? throw new ArgumentNullException(nameof(changeInterfaceLocale));
        _changeStartWithWindows = changeStartWithWindows
            ?? throw new ArgumentNullException(nameof(changeStartWithWindows));
        _changeMinimizeToTrayOnClose = changeMinimizeToTrayOnClose
            ?? throw new ArgumentNullException(nameof(changeMinimizeToTrayOnClose));
        _changeFriendPresenceNotifications = changeFriendPresenceNotifications
            ?? throw new ArgumentNullException(nameof(changeFriendPresenceNotifications));
        _changeGameLocale = changeGameLocale
            ?? throw new ArgumentNullException(nameof(changeGameLocale));
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
        _changeInterfaceLocale = static _ => false;
        _changeStartWithWindows = static _ => false;
        _changeMinimizeToTrayOnClose = static _ => false;
        _changeFriendPresenceNotifications = static _ => false;
        _changeGameLocale = static _ => false;
        _changeInstantQuestText = static _ => false;
    }

    internal bool TryChangeInterfaceLocale(string locale)
    {
        return Current.IsRuntimeConnected
            && Current.CanChangeInterfaceLocale
            && _changeInterfaceLocale(locale);
    }

    internal bool TryChangeStartWithWindows(bool enabled)
    {
        return Current.IsRuntimeConnected
            && Current.CanChangeStartWithWindows
            && _changeStartWithWindows(enabled);
    }

    internal bool TryChangeMinimizeToTrayOnClose(bool enabled)
    {
        return Current.IsRuntimeConnected
            && Current.CanChangeBehavior
            && _changeMinimizeToTrayOnClose(enabled);
    }

    internal bool TryChangeFriendPresenceNotifications(bool enabled)
    {
        return Current.IsRuntimeConnected
            && Current.CanChangeFriendPresenceNotifications
            && _changeFriendPresenceNotifications(enabled);
    }

    internal bool TryChangeGameLocale(string locale)
    {
        return Current.IsRuntimeConnected
            && Current.CanChangeGameLocale
            && _changeGameLocale(locale);
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
