using System.IO;
using System.Windows.Threading;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class SettingsStateAdapter : IDisposable
{
    private readonly SettingsUiState _state;
    private readonly ILauncherSettingsRuntime _settings;
    private readonly IGamePrimaryActionRuntime _game;
    private readonly ILauncherDashboardRuntime _dashboard;
    private readonly ILauncherSelfUpdateRuntime? _selfUpdate;
    private readonly string _launcherVersion;
    private readonly string _launcherLogPath;
    private readonly Dispatcher _dispatcher;
    private long _lastSettingsSequence = -1;
    private long _lastGameSequence = -1;
    private long _lastDashboardSequence = -1;
    private long _lastSelfUpdateSequence = -1;
    private int _disposeState;

    internal SettingsStateAdapter(
        SettingsUiState state,
        ILauncherSettingsRuntime settings,
        IGamePrimaryActionRuntime game,
        ILauncherDashboardRuntime dashboard,
        string launcherVersion,
        string launcherLogPath,
        Dispatcher dispatcher,
        ILauncherSelfUpdateRuntime? selfUpdate = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _selfUpdate = selfUpdate;
        _launcherVersion = launcherVersion ?? throw new ArgumentNullException(nameof(launcherVersion));
        _launcherLogPath = launcherLogPath ?? throw new ArgumentNullException(nameof(launcherLogPath));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _settings.SnapshotChanged += Settings_SnapshotChanged;
        _game.SnapshotChanged += Game_SnapshotChanged;
        _dashboard.SnapshotChanged += Dashboard_SnapshotChanged;
        if (_selfUpdate is not null)
        {
            _selfUpdate.SnapshotChanged += SelfUpdate_SnapshotChanged;
        }
        ApplyLatest();
    }

    internal static SettingsViewState CreateInitialView(
        LauncherSettingsSnapshot settings,
        GameRuntimeSnapshot game,
        DashboardSnapshot dashboard,
        string launcherVersion,
        string launcherLogPath,
        SettingsCategory initialCategory = SettingsCategory.General,
        LauncherSelfUpdateSnapshot? selfUpdate = null)
    {
        string installedClientVersion = string.IsNullOrWhiteSpace(game.InstalledVersion)
            ? "Inconnue"
            : game.InstalledVersion;
        string gameLanguage = settings.GameLocale == "enUS" ? "English" : "Français";
        string localState = !game.IsPlayable
            ? "Client non installé"
            : game.Action == GameAction.Update
                ? "Client prêt · mise à jour disponible"
                : game.UpdateKnowledge == GameUpdateKnowledge.Known
                    ? "Client prêt · à jour"
                    : "Client prêt · non vérifié";
        string? runtimeNotice = settings.SaveStatus == LauncherSettingsSaveStatus.Error
            ? settings.StatusMessage
            : null;
        string availableLauncherVersion = selfUpdate is null
            ? "Non vérifiée"
            : selfUpdate.AvailableVersion
                ?? (selfUpdate.IsUpdateAvailable
                    ? "Disponible"
                    : selfUpdate.ErrorCategory is null
                        ? "Aucune"
                        : "Indisponible");

        return new SettingsViewState(
            initialCategory,
            SettingsSavePreviewState.None,
            new GeneralSettingsViewState(
                InterfaceLanguage: settings.InterfaceLocale == "en-US" ? "English" : "Français",
                InterfaceLocale: settings.InterfaceLocale,
                StartWithWindows: settings.StartWithWindows,
                MinimizeToTrayOnClose: settings.MinimizeToTrayOnClose),
            new GameSettingsViewState(
                InstallPath: settings.InstallPath,
                GameLanguage: gameLanguage,
                InstantQuestText: settings.InstantQuestText,
                ClientVersion: installedClientVersion,
                GameLocale: settings.GameLocale),
            new UpdateSettingsViewState(
                InstalledLauncherVersion: selfUpdate?.InstalledVersion ?? launcherVersion,
                AvailableLauncherVersion: availableLauncherVersion,
                IsChecking: selfUpdate?.IsChecking == true,
                IsUpdateAvailable: selfUpdate?.IsUpdateAvailable == true,
                IsUpdating: selfUpdate?.IsUpdating == true,
                CanCheck: selfUpdate is { IsChecking: false, IsUpdating: false },
                CanStartUpdate: selfUpdate is
                    { IsUpdateAvailable: true, IsChecking: false, IsUpdating: false },
                HasChecked: selfUpdate?.LastCheckedAt is not null,
                HasError: selfUpdate?.ErrorCategory is not null,
                ErrorStatusText: selfUpdate?.ErrorCategory is null
                    or LauncherSelfUpdateErrorCategory.ManifestUnavailable
                    or LauncherSelfUpdateErrorCategory.ManifestInvalid
                    or LauncherSelfUpdateErrorCategory.ManifestTransportRejected
                    or LauncherSelfUpdateErrorCategory.ManifestSignatureInvalid
                    or LauncherSelfUpdateErrorCategory.ManifestUnsupported
                        ? "Vérification indisponible"
                        : "Échec de la mise à jour"),
            new NotificationSettingsViewState(settings.FriendPresenceNotifications),
            new DiagnosticSettingsViewState(
                LogLocation: launcherLogPath,
                LauncherLocation: AppContext.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                LauncherVersion: launcherVersion,
                ClientVersion: installedClientVersion,
                LocalState: localState,
                ServiceState: dashboard.RealmStatusLabel),
            IsRuntimeConnected: true,
            CanChangeInstallPath: settings.CanChangeInstallPath,
            CanChangeGameLocale: settings.CanChangeGameLocale,
            CanChangeBehavior: settings.CanChangeBehavior,
            CanChangeInstantQuestText: settings.CanChangeInstantQuestText,
            CanChangeInterfaceLocale: settings.CanChangeBehavior,
            CanChangeStartWithWindows: settings.CanChangeBehavior,
            CanChangeFriendPresenceNotifications: settings.CanChangeBehavior,
            AreDeferredControlsEnabled: false,
            SaveStatusDetail: settings.StatusMessage,
            RuntimeNoticeMessage: runtimeNotice);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _settings.SnapshotChanged -= Settings_SnapshotChanged;
        _game.SnapshotChanged -= Game_SnapshotChanged;
        _dashboard.SnapshotChanged -= Dashboard_SnapshotChanged;
        if (_selfUpdate is not null)
        {
            _selfUpdate.SnapshotChanged -= SelfUpdate_SnapshotChanged;
        }
    }

    private void Settings_SnapshotChanged(object? sender, LauncherSettingsSnapshotEventArgs e)
    {
        QueueApply();
    }

    private void Game_SnapshotChanged(object? sender, GameRuntimeSnapshotEventArgs e)
    {
        QueueApply();
    }

    private void Dashboard_SnapshotChanged(object? sender, DashboardSnapshotEventArgs e)
    {
        QueueApply();
    }

    private void SelfUpdate_SnapshotChanged(
        object? sender,
        LauncherSelfUpdateSnapshotEventArgs e)
    {
        QueueApply();
    }

    private void QueueApply()
    {
        if (Volatile.Read(ref _disposeState) != 0
            || _dispatcher.HasShutdownStarted
            || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            ApplyLatest();
            return;
        }

        _ = _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(ApplyLatest));
    }

    private void ApplyLatest()
    {
        if (Volatile.Read(ref _disposeState) != 0
            || _dispatcher.HasShutdownStarted
            || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        LauncherSettingsSnapshot settings = _settings.CurrentSnapshot;
        GameRuntimeSnapshot game = _game.CurrentSnapshot;
        DashboardSnapshot dashboard = _dashboard.CurrentSnapshot;
        LauncherSelfUpdateSnapshot? selfUpdate = _selfUpdate?.CurrentSnapshot;
        if (settings.Sequence < _lastSettingsSequence
            || game.Sequence < _lastGameSequence
            || dashboard.Sequence < _lastDashboardSequence)
        {
            return;
        }
        if (selfUpdate is not null && selfUpdate.Sequence < _lastSelfUpdateSequence)
        {
            return;
        }

        SettingsCategory category = _state.Current.InitialCategory;
        SettingsViewState view = CreateInitialView(
            settings,
            game,
            dashboard,
            _launcherVersion,
            _launcherLogPath,
            category,
            selfUpdate);
        _lastSettingsSequence = settings.Sequence;
        _lastGameSequence = game.Sequence;
        _lastDashboardSequence = dashboard.Sequence;
        if (selfUpdate is not null)
        {
            _lastSelfUpdateSequence = selfUpdate.Sequence;
        }
        _state.ApplyRuntimeView(view);
    }

}
