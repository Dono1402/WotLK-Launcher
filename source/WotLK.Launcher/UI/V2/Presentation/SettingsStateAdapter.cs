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
    private readonly string _launcherVersion;
    private readonly string _launcherLogPath;
    private readonly Dispatcher _dispatcher;
    private long _lastSettingsSequence = -1;
    private long _lastGameSequence = -1;
    private long _lastDashboardSequence = -1;
    private int _disposeState;

    internal SettingsStateAdapter(
        SettingsUiState state,
        ILauncherSettingsRuntime settings,
        IGamePrimaryActionRuntime game,
        ILauncherDashboardRuntime dashboard,
        string launcherVersion,
        string launcherLogPath,
        Dispatcher dispatcher)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _launcherVersion = launcherVersion ?? throw new ArgumentNullException(nameof(launcherVersion));
        _launcherLogPath = launcherLogPath ?? throw new ArgumentNullException(nameof(launcherLogPath));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _settings.SnapshotChanged += Settings_SnapshotChanged;
        _game.SnapshotChanged += Game_SnapshotChanged;
        _dashboard.SnapshotChanged += Dashboard_SnapshotChanged;
        ApplyLatest();
    }

    internal static SettingsViewState CreateInitialView(
        LauncherSettingsSnapshot settings,
        GameRuntimeSnapshot game,
        DashboardSnapshot dashboard,
        string launcherVersion,
        string launcherLogPath,
        SettingsCategory initialCategory = SettingsCategory.General)
    {
        string installedClientVersion = string.IsNullOrWhiteSpace(game.InstalledVersion)
            ? "Inconnue"
            : game.InstalledVersion;
        string gameLanguage = settings.GameLocale == "enUS" ? "English" : "Français";
        string updateStatus = game.UpdateKnowledge switch
        {
            GameUpdateKnowledge.Checking => "Vérification en cours",
            GameUpdateKnowledge.Known => game.Action == GameAction.Update
                ? "Mise à jour disponible"
                : "Vérifié",
            GameUpdateKnowledge.Unavailable => "Indisponible",
            _ => "Non vérifié"
        };
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

        return new SettingsViewState(
            initialCategory,
            SettingsSavePreviewState.None,
            new GeneralSettingsViewState(
                InterfaceLanguage: "Français",
                StartWithWindows: false,
                WindowCloseBehavior: "Quitter Atlas Launcher",
                CloseLauncherAfterGameStart: settings.CloseLauncherOnGameStart),
            new GameSettingsViewState(
                InstallPath: settings.InstallPath,
                GameLanguage: gameLanguage,
                VideoSettingsLocation: @"WTF\Config.wtf",
                InstantQuestText: settings.InstantQuestText,
                ClientVersion: installedClientVersion,
                GameLocale: settings.GameLocale),
            new UpdateSettingsViewState(
                AutomaticLauncherUpdates: settings.AutomaticLauncherUpdates,
                ClientUpdateBehavior: "Depuis la page Jeu",
                ReleaseChannel: "Stable",
                LastUpdateCheck: updateStatus,
                InstalledLauncherVersion: launcherVersion,
                AvailableLauncherVersion: "Non vérifiée"),
            new NotificationSettingsViewState(
                UpdateCompleted: true,
                Errors: true,
                FriendRequests: true,
                FriendPresence: false,
                Sounds: true),
            new AppearanceSettingsViewState(
                ReduceAnimations: false,
                InterfaceScale: "100 %",
                EffectsIntensity: 68,
                EffectsIntensityLabel: "Équilibrée"),
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
        if (settings.Sequence < _lastSettingsSequence
            || game.Sequence < _lastGameSequence
            || dashboard.Sequence < _lastDashboardSequence)
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
            category);
        _lastSettingsSequence = settings.Sequence;
        _lastGameSequence = game.Sequence;
        _lastDashboardSequence = dashboard.Sequence;
        _state.ApplyRuntimeView(view);
    }
}
