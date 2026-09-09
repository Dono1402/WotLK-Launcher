namespace WotLK.Launcher.Runtime;

internal enum LauncherSettingsSaveStatus
{
    Idle,
    Saving,
    Saved,
    Error
}

internal enum LauncherSettingsChangeKind
{
    InstallPath,
    GameLocale,
    CloseLauncherOnGameStart,
    InstantQuestText,
    InterfaceLocale,
    StartWithWindows,
    MinimizeToTrayOnClose,
    FriendPresenceNotifications
}

internal enum LauncherSettingsChangeStatus
{
    Saved,
    Unchanged,
    Busy,
    ShuttingDown,
    Failed
}

internal readonly record struct LauncherSettingsChangeResult(
    LauncherSettingsChangeStatus Status,
    LauncherSettingsChangeKind ChangeKind)
{
    internal bool IsSaved => Status == LauncherSettingsChangeStatus.Saved;
}

internal sealed record LauncherSettingsSnapshot(
    long Sequence,
    string InstallPath,
    string GameLocale,
    bool AutomaticLauncherUpdates,
    bool CloseLauncherOnGameStart,
    bool InstantQuestText,
    string InterfaceLocale,
    bool StartWithWindows,
    bool MinimizeToTrayOnClose,
    bool FriendPresenceNotifications,
    bool CanChangeInstallPath,
    bool CanChangeGameLocale,
    bool CanChangeBehavior,
    bool CanChangeInstantQuestText,
    LauncherSettingsSaveStatus SaveStatus,
    string? StatusMessage,
    string? RecoveryNotice = null);

internal sealed class LauncherSettingsSnapshotEventArgs(LauncherSettingsSnapshot snapshot) : EventArgs
{
    internal LauncherSettingsSnapshot Snapshot { get; } = snapshot;
}

internal interface ILauncherSettingsRuntime
{
    event EventHandler? AvailabilityChanged;

    event EventHandler<LauncherSettingsSnapshotEventArgs>? SnapshotChanged;

    LauncherSettingsSnapshot CurrentSnapshot { get; }

    Task<LauncherSettingsChangeResult> TrySetInstallPathAsync(string installPath);

    Task<LauncherSettingsChangeResult> TrySetGameLocaleAsync(string gameLocale);

    Task<LauncherSettingsChangeResult> TrySetInterfaceLocaleAsync(string interfaceLocale);

    Task<LauncherSettingsChangeResult> TrySetStartWithWindowsAsync(bool startWithWindows);

    Task<LauncherSettingsChangeResult> TrySetMinimizeToTrayOnCloseAsync(bool minimizeToTrayOnClose);

    Task<LauncherSettingsChangeResult> TrySetFriendPresenceNotificationsAsync(bool enabled);

    Task<LauncherSettingsChangeResult> TrySetCloseLauncherOnGameStartAsync(bool closeAfterLaunch);

    Task<LauncherSettingsChangeResult> TrySetInstantQuestTextAsync(bool enabled);

    void BeginShutdown();
}

internal sealed class LauncherSettingsCoordinator : ILauncherSettingsRuntime, IDisposable
{
    private readonly object _sync = new();
    private readonly LauncherSettings _settings;
    private readonly LauncherOperationCoordinator _operations;
    private readonly Action<LauncherSettings> _saveSettings;
    private readonly Action<LauncherSettingsChangeKind> _settingsChanged;
    private readonly Action<string> _writeLog;
    private readonly Func<string, bool> _readInstantQuestText;
    private readonly Func<string, bool, bool> _writeInstantQuestText;
    private LauncherSettingsSnapshot _currentSnapshot;
    private bool _instantQuestText;
    private long _sequence;
    private bool _isSaving;
    private TaskCompletionSource? _saveCompletion;
    private bool _isShuttingDown;
    private int _disposeState;

    internal LauncherSettingsCoordinator(
        LauncherSettings settings,
        LauncherOperationCoordinator operations,
        Action<LauncherSettings> saveSettings,
        Action<LauncherSettingsChangeKind> settingsChanged,
        Action<string> writeLog,
        Func<string, bool>? readInstantQuestText = null,
        Func<string, bool, bool>? writeInstantQuestText = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _settingsChanged = settingsChanged ?? throw new ArgumentNullException(nameof(settingsChanged));
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _readInstantQuestText = readInstantQuestText ?? GameInstallServices.ReadInstantQuestText;
        _writeInstantQuestText = writeInstantQuestText ?? GameInstallServices.SetInstantQuestText;
        _instantQuestText = ReadInstantQuestTextSafely(_settings.InstallPath);
        _currentSnapshot = CreateSnapshotUnsafe(LauncherSettingsSaveStatus.Idle, settings.RecoveryNotice);
        _operations.StateChanged += Operations_StateChanged;
    }

    public event EventHandler? AvailabilityChanged;

    public event EventHandler<LauncherSettingsSnapshotEventArgs>? SnapshotChanged;

    public LauncherSettingsSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public Task<LauncherSettingsChangeResult> TrySetInstallPathAsync(string installPath)
    {
        string normalized = LauncherSettings.NormalizeInstallPath(installPath);
        return TrySaveAsync(
            LauncherSettingsChangeKind.InstallPath,
            normalized,
            static settings => settings.InstallPath,
            static (settings, value) => settings.InstallPath = value,
            pathRequiresIdleUserOperation: true);
    }

    public Task<LauncherSettingsChangeResult> TrySetGameLocaleAsync(string gameLocale)
    {
        string normalized = LauncherSettings.NormalizeGameLocale(gameLocale);
        return TrySaveAsync(
            LauncherSettingsChangeKind.GameLocale,
            normalized,
            static settings => settings.GameLocale,
            static (settings, value) => settings.GameLocale = value,
            pathRequiresIdleUserOperation: false);
    }

    public Task<LauncherSettingsChangeResult> TrySetInterfaceLocaleAsync(string interfaceLocale)
    {
        string normalized = LauncherSettings.NormalizeInterfaceLocale(interfaceLocale);
        return TrySaveAsync(
            LauncherSettingsChangeKind.InterfaceLocale,
            normalized,
            static settings => settings.InterfaceLocale,
            static (settings, value) => settings.InterfaceLocale = value,
            pathRequiresIdleUserOperation: false);
    }

    public Task<LauncherSettingsChangeResult> TrySetStartWithWindowsAsync(bool startWithWindows)
    {
        return TrySaveAsync(
            LauncherSettingsChangeKind.StartWithWindows,
            startWithWindows,
            static settings => settings.StartWithWindows,
            static (settings, value) => settings.StartWithWindows = value,
            pathRequiresIdleUserOperation: false);
    }

    public Task<LauncherSettingsChangeResult> TrySetMinimizeToTrayOnCloseAsync(bool minimizeToTrayOnClose)
    {
        return TrySaveAsync(
            LauncherSettingsChangeKind.MinimizeToTrayOnClose,
            minimizeToTrayOnClose,
            static settings => settings.MinimizeToTrayOnClose,
            static (settings, value) => settings.MinimizeToTrayOnClose = value,
            pathRequiresIdleUserOperation: false);
    }

    public Task<LauncherSettingsChangeResult> TrySetFriendPresenceNotificationsAsync(bool enabled)
    {
        return TrySaveAsync(
            LauncherSettingsChangeKind.FriendPresenceNotifications,
            enabled,
            static settings => settings.FriendPresenceNotifications,
            static (settings, value) => settings.FriendPresenceNotifications = value,
            pathRequiresIdleUserOperation: false);
    }

    public Task<LauncherSettingsChangeResult> TrySetCloseLauncherOnGameStartAsync(bool closeAfterLaunch)
    {
        return TrySaveAsync(
            LauncherSettingsChangeKind.CloseLauncherOnGameStart,
            closeAfterLaunch,
            static settings => settings.CloseLauncherOnGameStart,
            static (settings, value) => settings.CloseLauncherOnGameStart = value,
            pathRequiresIdleUserOperation: false);
    }

    public async Task<LauncherSettingsChangeResult> TrySetInstantQuestTextAsync(bool enabled)
    {
        LauncherSettingsSnapshot savingSnapshot;
        bool previous;
        TaskCompletionSource saveCompletion;
        lock (_sync)
        {
            if (_isShuttingDown
                || Volatile.Read(ref _disposeState) != 0
                || _operations.IsShuttingDown)
            {
                return new LauncherSettingsChangeResult(
                    LauncherSettingsChangeStatus.ShuttingDown,
                    LauncherSettingsChangeKind.InstantQuestText);
            }

            if (_isSaving)
            {
                return new LauncherSettingsChangeResult(
                    LauncherSettingsChangeStatus.Busy,
                    LauncherSettingsChangeKind.InstantQuestText);
            }

            previous = _instantQuestText;
            if (previous == enabled)
            {
                return new LauncherSettingsChangeResult(
                    LauncherSettingsChangeStatus.Unchanged,
                    LauncherSettingsChangeKind.InstantQuestText);
            }

            _isSaving = true;
            saveCompletion = _saveCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _instantQuestText = enabled;
            savingSnapshot = CreateSnapshotUnsafe(
                LauncherSettingsSaveStatus.Saving,
                "Enregistrement immédiat dans Config.wtf.");
            _currentSnapshot = savingSnapshot;
        }

        Publish(savingSnapshot, availabilityChanged: true);

        LauncherSettingsSnapshot finalSnapshot;
        LauncherSettingsChangeStatus status;
        try
        {
            await Task.Run(() => _writeInstantQuestText(_settings.InstallPath, enabled)).ConfigureAwait(false);
            lock (_sync)
            {
                _isSaving = false;
                finalSnapshot = CreateSnapshotUnsafe(
                    LauncherSettingsSaveStatus.Saved,
                    "Texte de quête instantané appliqué au client.");
                _currentSnapshot = finalSnapshot;
            }

            status = LauncherSettingsChangeStatus.Saved;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _instantQuestText = previous;
                _isSaving = false;
                finalSnapshot = CreateSnapshotUnsafe(
                    LauncherSettingsSaveStatus.Error,
                    "Config.wtf n’a pas pu être modifié. La valeur précédente est conservée.");
                _currentSnapshot = finalSnapshot;
            }

            WriteFailureSafely(LauncherSettingsChangeKind.InstantQuestText, exception);
            status = LauncherSettingsChangeStatus.Failed;
        }

        Publish(finalSnapshot, availabilityChanged: true);
        saveCompletion.TrySetResult();
        return new LauncherSettingsChangeResult(
            status,
            LauncherSettingsChangeKind.InstantQuestText);
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task pending;
        lock (_sync) pending = _saveCompletion?.Task ?? Task.CompletedTask;
        try { await pending.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    public void BeginShutdown()
    {
        LauncherSettingsSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            snapshot = CreateSnapshotUnsafe(_currentSnapshot.SaveStatus, _currentSnapshot.StatusMessage);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        BeginShutdown();
        _operations.StateChanged -= Operations_StateChanged;
        AvailabilityChanged = null;
        SnapshotChanged = null;
    }

    private async Task<LauncherSettingsChangeResult> TrySaveAsync<T>(
        LauncherSettingsChangeKind changeKind,
        T value,
        Func<LauncherSettings, T> read,
        Action<LauncherSettings, T> write,
        bool pathRequiresIdleUserOperation)
    {
        T previous;
        TaskCompletionSource saveCompletion;
        LauncherSettingsSnapshot savingSnapshot;
        lock (_sync)
        {
            if (_isShuttingDown
                || Volatile.Read(ref _disposeState) != 0
                || _operations.IsShuttingDown)
            {
                return new LauncherSettingsChangeResult(
                    LauncherSettingsChangeStatus.ShuttingDown,
                    changeKind);
            }

            if (_isSaving
                || pathRequiresIdleUserOperation
                && _operations.HasActiveUserCancellableOperation)
            {
                return new LauncherSettingsChangeResult(
                    LauncherSettingsChangeStatus.Busy,
                    changeKind);
            }

            previous = read(_settings);
            if (EqualityComparer<T>.Default.Equals(previous, value))
            {
                return new LauncherSettingsChangeResult(
                    LauncherSettingsChangeStatus.Unchanged,
                    changeKind);
            }

            _isSaving = true;
            saveCompletion = _saveCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            write(_settings, value);
            savingSnapshot = CreateSnapshotUnsafe(
                LauncherSettingsSaveStatus.Saving,
                "Enregistrement immédiat des préférences locales.");
            _currentSnapshot = savingSnapshot;
        }

        Publish(savingSnapshot, availabilityChanged: true);

        LauncherSettingsSnapshot finalSnapshot;
        LauncherSettingsChangeStatus status;
        try
        {
            await Task.Run(() => _saveSettings(_settings)).ConfigureAwait(false);
            bool? refreshedInstantQuestText = changeKind == LauncherSettingsChangeKind.InstallPath
                ? ReadInstantQuestTextSafely(_settings.InstallPath)
                : null;
            lock (_sync)
            {
                if (refreshedInstantQuestText is bool instantQuestText)
                {
                    _instantQuestText = instantQuestText;
                }

                _isSaving = false;
                finalSnapshot = CreateSnapshotUnsafe(
                    LauncherSettingsSaveStatus.Saved,
                    "Préférence enregistrée sur cet ordinateur.");
                _currentSnapshot = finalSnapshot;
            }

            status = LauncherSettingsChangeStatus.Saved;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                write(_settings, previous);
                _isSaving = false;
                finalSnapshot = CreateSnapshotUnsafe(
                    LauncherSettingsSaveStatus.Error,
                    "La préférence n’a pas pu être enregistrée. La valeur précédente est conservée.");
                _currentSnapshot = finalSnapshot;
            }

            WriteFailureSafely(changeKind, exception);
            status = LauncherSettingsChangeStatus.Failed;
        }

        Publish(finalSnapshot, availabilityChanged: true);
        if (status == LauncherSettingsChangeStatus.Saved)
        {
            try
            {
                _settingsChanged(changeKind);
            }
            catch (Exception exception)
            {
                WriteFailureSafely(changeKind, exception);
            }
        }

        saveCompletion.TrySetResult();
        return new LauncherSettingsChangeResult(status, changeKind);
    }

    private LauncherSettingsSnapshot CreateSnapshotUnsafe(
        LauncherSettingsSaveStatus saveStatus,
        string? statusMessage)
    {
        bool available = !_isShuttingDown
            && Volatile.Read(ref _disposeState) == 0
            && !_operations.IsShuttingDown
            && !_isSaving;
        return new LauncherSettingsSnapshot(
            Sequence: ++_sequence,
            InstallPath: _settings.InstallPath,
            GameLocale: _settings.GameLocale,
            AutomaticLauncherUpdates: _settings.AutomaticLauncherUpdates,
            CloseLauncherOnGameStart: _settings.CloseLauncherOnGameStart,
            InstantQuestText: _instantQuestText,
            InterfaceLocale: _settings.InterfaceLocale,
            StartWithWindows: _settings.StartWithWindows,
            MinimizeToTrayOnClose: _settings.MinimizeToTrayOnClose,
            FriendPresenceNotifications: _settings.FriendPresenceNotifications,
            CanChangeInstallPath: available && !_operations.HasActiveUserCancellableOperation,
            CanChangeGameLocale: available,
            CanChangeBehavior: available,
            CanChangeInstantQuestText: available,
            SaveStatus: saveStatus,
            StatusMessage: statusMessage,
            RecoveryNotice: _settings.RecoveryNotice);
    }

    private void Operations_StateChanged(object? sender, EventArgs e)
    {
        LauncherSettingsSnapshot snapshot;
        lock (_sync)
        {
            if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            snapshot = CreateSnapshotUnsafe(
                _currentSnapshot.SaveStatus,
                _currentSnapshot.StatusMessage);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    private void Publish(LauncherSettingsSnapshot snapshot, bool availabilityChanged)
    {
        if (availabilityChanged)
        {
            try
            {
                AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Settings ownership must not depend on command subscribers.
            }
        }

        try
        {
            SnapshotChanged?.Invoke(this, new LauncherSettingsSnapshotEventArgs(snapshot));
        }
        catch
        {
            // Presentation subscribers cannot interrupt persistence.
        }
    }

    private void WriteFailureSafely(
        LauncherSettingsChangeKind changeKind,
        Exception exception)
    {
        try
        {
            _writeLog(
                $"Paramètre V2 non appliqué: change={changeKind}; "
                + $"category={exception.GetType().Name}.");
        }
        catch
        {
            // A logging failure cannot replace the persistence result.
        }
    }

    private bool ReadInstantQuestTextSafely(string installPath)
    {
        try
        {
            return _readInstantQuestText(installPath);
        }
        catch (Exception exception)
        {
            WriteFailureSafely(LauncherSettingsChangeKind.InstantQuestText, exception);
            return true;
        }
    }
}
