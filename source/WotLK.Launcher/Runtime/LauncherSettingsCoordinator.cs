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
    InstantQuestText
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
    bool CanChangeInstallPath,
    bool CanChangeGameLocale,
    bool CanChangeBehavior,
    bool CanChangeInstantQuestText,
    LauncherSettingsSaveStatus SaveStatus,
    string? StatusMessage);

internal sealed class LauncherSettingsSnapshotEventArgs(LauncherSettingsSnapshot snapshot) : EventArgs
{
    internal LauncherSettingsSnapshot Snapshot { get; } = snapshot;
}

internal interface ILauncherSettingsRuntime
{
    event EventHandler? AvailabilityChanged;

    event EventHandler<LauncherSettingsSnapshotEventArgs>? SnapshotChanged;

    LauncherSettingsSnapshot CurrentSnapshot { get; }

    LauncherSettingsChangeResult TrySetInstallPath(string installPath);

    LauncherSettingsChangeResult TrySetGameLocale(string gameLocale);

    LauncherSettingsChangeResult TrySetCloseLauncherOnGameStart(bool closeAfterLaunch);

    LauncherSettingsChangeResult TrySetInstantQuestText(bool enabled);

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
        _currentSnapshot = CreateSnapshotUnsafe(LauncherSettingsSaveStatus.Idle, null);
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

    public LauncherSettingsChangeResult TrySetInstallPath(string installPath)
    {
        string normalized = LauncherSettings.NormalizeInstallPath(installPath);
        return TrySave(
            LauncherSettingsChangeKind.InstallPath,
            normalized,
            static settings => settings.InstallPath,
            static (settings, value) => settings.InstallPath = value,
            pathRequiresIdleUserOperation: true);
    }

    public LauncherSettingsChangeResult TrySetGameLocale(string gameLocale)
    {
        string normalized = LauncherSettings.NormalizeGameLocale(gameLocale);
        return TrySave(
            LauncherSettingsChangeKind.GameLocale,
            normalized,
            static settings => settings.GameLocale,
            static (settings, value) => settings.GameLocale = value,
            pathRequiresIdleUserOperation: false);
    }

    public LauncherSettingsChangeResult TrySetCloseLauncherOnGameStart(bool closeAfterLaunch)
    {
        return TrySave(
            LauncherSettingsChangeKind.CloseLauncherOnGameStart,
            closeAfterLaunch,
            static settings => settings.CloseLauncherOnGameStart,
            static (settings, value) => settings.CloseLauncherOnGameStart = value,
            pathRequiresIdleUserOperation: false);
    }

    public LauncherSettingsChangeResult TrySetInstantQuestText(bool enabled)
    {
        LauncherSettingsSnapshot savingSnapshot;
        bool previous;
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
            _ = _writeInstantQuestText(_settings.InstallPath, enabled);
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
        return new LauncherSettingsChangeResult(
            status,
            LauncherSettingsChangeKind.InstantQuestText);
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

    private LauncherSettingsChangeResult TrySave<T>(
        LauncherSettingsChangeKind changeKind,
        T value,
        Func<LauncherSettings, T> read,
        Action<LauncherSettings, T> write,
        bool pathRequiresIdleUserOperation)
    {
        T previous;
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
            _saveSettings(_settings);
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
            CanChangeInstallPath: available && !_operations.HasActiveUserCancellableOperation,
            CanChangeGameLocale: available,
            CanChangeBehavior: available,
            CanChangeInstantQuestText: available,
            SaveStatus: saveStatus,
            StatusMessage: statusMessage);
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
