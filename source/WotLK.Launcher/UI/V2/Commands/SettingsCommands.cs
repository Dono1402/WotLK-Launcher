using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Commands;

internal interface ISettingsFolderPicker
{
    string? SelectGameFolder(Window owner, string initialDirectory);
}

internal sealed class SettingsFolderPicker : ISettingsFolderPicker
{
    public string? SelectGameFolder(Window owner, string initialDirectory)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Choisir le dossier du client WotLK",
            InitialDirectory = initialDirectory
        };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
}

internal enum SettingsGameLocaleApplyStatus
{
    Applied,
    ClientNotInstalled,
    PermissionCancelled,
    Failed
}

internal readonly record struct SettingsGameLocaleApplyResult(
    SettingsGameLocaleApplyStatus Status,
    string? FailureCategory = null);

internal interface ISettingsGameLocaleApplier
{
    Task<SettingsGameLocaleApplyResult> ApplyAsync(Window owner, string installPath, string gameLocale);
}

internal sealed class SettingsGameLocaleApplier : ISettingsGameLocaleApplier
{
    private readonly Func<string, bool> _hasPlayableClient;
    private readonly Func<Window, string, Task<bool>> _ensureWritable;
    private readonly Func<string, string, string> _writeConfig;

    internal SettingsGameLocaleApplier(
        Func<string, bool>? hasPlayableClient = null,
        Func<Window, string, Task<bool>>? ensureWritable = null,
        Func<string, string, string>? writeConfig = null)
    {
        _hasPlayableClient = hasPlayableClient ?? GameInstallServices.HasPlayableClient;
        _ensureWritable = ensureWritable ?? GameDirectoryAccess.EnsureWritableAsync;
        _writeConfig = writeConfig ?? GameInstallServices.EnsureDefaultClientConfig;
    }

    public async Task<SettingsGameLocaleApplyResult> ApplyAsync(
        Window owner,
        string installPath,
        string gameLocale)
    {
        try
        {
            if (!await Task.Run(() => _hasPlayableClient(installPath)).ConfigureAwait(false))
                return new SettingsGameLocaleApplyResult(SettingsGameLocaleApplyStatus.ClientNotInstalled);

            if (!await _ensureWritable(owner, installPath).ConfigureAwait(false))
            {
                return new SettingsGameLocaleApplyResult(
                    SettingsGameLocaleApplyStatus.PermissionCancelled);
            }

            _ = await Task.Run(() => _writeConfig(installPath, gameLocale)).ConfigureAwait(false);
            return new SettingsGameLocaleApplyResult(SettingsGameLocaleApplyStatus.Applied);
        }
        catch (Exception exception)
        {
            return new SettingsGameLocaleApplyResult(
                SettingsGameLocaleApplyStatus.Failed,
                exception.GetType().Name);
        }
    }
}

internal enum SettingsGameConfigAccessStatus
{
    Granted,
    PermissionCancelled,
    Failed
}

internal readonly record struct SettingsGameConfigAccessResult(
    SettingsGameConfigAccessStatus Status,
    string? FailureCategory = null);

internal interface ISettingsGameConfigAccess
{
    SettingsGameConfigAccessResult EnsureWritable(Window owner, string installPath);
}

internal sealed class SettingsGameConfigAccess : ISettingsGameConfigAccess
{
    public SettingsGameConfigAccessResult EnsureWritable(Window owner, string installPath)
    {
        try
        {
            return GameDirectoryAccess.EnsureWritable(owner, installPath)
                ? new SettingsGameConfigAccessResult(SettingsGameConfigAccessStatus.Granted)
                : new SettingsGameConfigAccessResult(
                    SettingsGameConfigAccessStatus.PermissionCancelled);
        }
        catch (Exception exception)
        {
            return new SettingsGameConfigAccessResult(
                SettingsGameConfigAccessStatus.Failed,
                exception.GetType().Name);
        }
    }
}

internal sealed class SettingsCommands : IDisposable
{
    private readonly SettingsUiState _state;
    private readonly ILauncherSettingsRuntime _settings;
    private readonly ILauncherLocalActions _localActions;
    private readonly Window _owner;
    private readonly ISettingsFolderPicker _folderPicker;
    private readonly ISettingsGameLocaleApplier _localeApplier;
    private readonly ISettingsGameConfigAccess _gameConfigAccess;
    private readonly ILauncherStartupRegistration _startupRegistration;
    private readonly ILauncherSelfUpdateRuntime? _selfUpdate;
    private readonly Action<string> _writeLog;
    private readonly DelegateCommand _browseInstallPath;
    private readonly DelegateCommand _openGameFolder;
    private readonly DelegateCommand _openLogs;
    private readonly DelegateCommand _checkLauncherUpdate;
    private readonly DelegateCommand _startLauncherUpdate;
    private int _disposeState;

    internal SettingsCommands(
        SettingsUiState state,
        ILauncherSettingsRuntime settings,
        ILauncherLocalActions localActions,
        Window owner,
        Action<string> writeLog,
        ISettingsFolderPicker? folderPicker = null,
        ISettingsGameLocaleApplier? localeApplier = null,
        ISettingsGameConfigAccess? gameConfigAccess = null,
        ILauncherStartupRegistration? startupRegistration = null,
        ICommand? verifyRepairCommand = null,
        Action? showGameForRepair = null,
        ILauncherSelfUpdateRuntime? selfUpdate = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localActions = localActions ?? throw new ArgumentNullException(nameof(localActions));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _folderPicker = folderPicker ?? new SettingsFolderPicker();
        _localeApplier = localeApplier ?? new SettingsGameLocaleApplier();
        _gameConfigAccess = gameConfigAccess ?? new SettingsGameConfigAccess();
        _startupRegistration = startupRegistration ?? new WindowsLauncherStartupRegistration();
        _selfUpdate = selfUpdate;
        _browseInstallPath = new DelegateCommand(
            BrowseInstallPath,
            () => _settings.CurrentSnapshot.CanChangeInstallPath);
        _openGameFolder = new DelegateCommand(
            OpenGameFolder,
            () => _localActions.CanOpenGameFolder);
        _openLogs = new DelegateCommand(
            OpenLogs,
            () => _localActions.CanOpenDiagnostic);
        _checkLauncherUpdate = new DelegateCommand(
            CheckLauncherUpdate,
            () => _selfUpdate?.CanCheck == true);
        _startLauncherUpdate = new DelegateCommand(
            StartLauncherUpdate,
            () => _selfUpdate?.CanStartUpdate == true);
        _settings.AvailabilityChanged += Settings_AvailabilityChanged;
        _localActions.AvailabilityChanged += LocalActions_AvailabilityChanged;
        if (_selfUpdate is not null)
        {
            _selfUpdate.AvailabilityChanged += SelfUpdate_AvailabilityChanged;
        }
        _state.AttachRuntimeActions(
            _browseInstallPath,
            _openGameFolder,
            _openLogs,
            verifyRepairCommand ?? DisabledCommand.Instance,
            _checkLauncherUpdate,
            _startLauncherUpdate,
            showGameForRepair ?? (static () => { }),
            ChangeInterfaceLocaleAsync,
            ChangeStartWithWindowsAsync,
            ChangeMinimizeToTrayOnCloseAsync,
            ChangeFriendPresenceNotificationsAsync,
            ChangeGameLocaleAsync,
            ChangeInstantQuestTextAsync);

        bool shouldStartWithWindows = _settings.CurrentSnapshot.StartWithWindows;
        if (_startupRegistration.IsRegistered != shouldStartWithWindows
            || (shouldStartWithWindows && !_startupRegistration.IsEnabled))
        {
            LauncherStartupRegistrationResult repair =
                _startupRegistration.TrySetEnabled(shouldStartWithWindows);
            if (!repair.IsApplied)
            {
                WriteStartupFailureSafely(repair.FailureCategory);
            }
        }
    }

    internal ICommand BrowseInstallPathCommand => _browseInstallPath;

    internal ICommand OpenGameFolderCommand => _openGameFolder;

    internal ICommand OpenLogsCommand => _openLogs;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _settings.AvailabilityChanged -= Settings_AvailabilityChanged;
        _localActions.AvailabilityChanged -= LocalActions_AvailabilityChanged;
        if (_selfUpdate is not null)
        {
            _selfUpdate.AvailabilityChanged -= SelfUpdate_AvailabilityChanged;
        }
        _browseInstallPath.Dispose();
        _openGameFolder.Dispose();
        _openLogs.Dispose();
        _checkLauncherUpdate.Dispose();
        _startLauncherUpdate.Dispose();
    }

    private async void BrowseInstallPath()
    {
        LauncherSettingsSnapshot current = _settings.CurrentSnapshot;
        string initialDirectory = Directory.Exists(current.InstallPath)
            ? current.InstallPath
            : LauncherSettings.GetDefaultInstallPath();
        string? selectedPath = _folderPicker.SelectGameFolder(_owner, initialDirectory);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        _ = await _settings.TrySetInstallPathAsync(selectedPath);
    }

    private async Task<bool> ChangeGameLocaleAsync(string gameLocale)
    {
        LauncherSettingsChangeResult change = await _settings.TrySetGameLocaleAsync(gameLocale, async (path, locale) =>
        {
            SettingsGameLocaleApplyResult apply;
            try { apply = await _localeApplier.ApplyAsync(_owner, path, locale).ConfigureAwait(false); }
            catch (Exception exception) { apply = new(SettingsGameLocaleApplyStatus.Failed, exception.GetType().Name); }
            if (apply.Status != SettingsGameLocaleApplyStatus.Failed) return null;
            WriteLocaleFailureSafely(apply.FailureCategory);
            return "La langue est enregistrée, mais le client n’a pas pu être modifié maintenant.";
        });
        if (change.Status == LauncherSettingsChangeStatus.Unchanged)
        {
            return true;
        }

        if (!change.IsSaved)
        {
            return false;
        }

        return true;
    }

    private async Task<bool> ChangeInterfaceLocaleAsync(string interfaceLocale)
    {
        LauncherSettingsChangeResult result = await _settings.TrySetInterfaceLocaleAsync(interfaceLocale);
        if (result.Status is not (LauncherSettingsChangeStatus.Saved
            or LauncherSettingsChangeStatus.Unchanged))
        {
            return false;
        }

        LauncherLocalization.SetLocale(interfaceLocale);
        return true;
    }

    private async Task<bool> ChangeStartWithWindowsAsync(bool enabled)
    {
        bool previous = _settings.CurrentSnapshot.StartWithWindows;
        if (previous == enabled)
        {
            return true;
        }

        LauncherStartupRegistrationResult registration =
            _startupRegistration.TrySetEnabled(enabled);
        if (!registration.IsApplied)
        {
            WriteStartupFailureSafely(registration.FailureCategory);
            _state.ShowRuntimeActionFailure(
                "Le démarrage avec Windows n’a pas pu être modifié.");
            return false;
        }

        LauncherSettingsChangeResult result = await _settings.TrySetStartWithWindowsAsync(enabled);
        if (result.Status is LauncherSettingsChangeStatus.Saved
            or LauncherSettingsChangeStatus.Unchanged)
        {
            return true;
        }

        _ = _startupRegistration.TrySetEnabled(previous);
        return false;
    }

    private async Task<bool> ChangeMinimizeToTrayOnCloseAsync(bool enabled)
    {
        LauncherSettingsChangeResult result =
            await _settings.TrySetMinimizeToTrayOnCloseAsync(enabled);
        return result.Status is LauncherSettingsChangeStatus.Saved
            or LauncherSettingsChangeStatus.Unchanged;
    }

    private async Task<bool> ChangeFriendPresenceNotificationsAsync(bool enabled)
    {
        LauncherSettingsChangeResult result =
            await _settings.TrySetFriendPresenceNotificationsAsync(enabled);
        return result.Status is LauncherSettingsChangeStatus.Saved
            or LauncherSettingsChangeStatus.Unchanged;
    }

    private async Task<bool> ChangeInstantQuestTextAsync(bool enabled)
    {
        LauncherSettingsSnapshot current = _settings.CurrentSnapshot;
        if (current.InstantQuestText == enabled)
        {
            return true;
        }

        SettingsGameConfigAccessResult access = _gameConfigAccess.EnsureWritable(
            _owner,
            current.InstallPath);
        if (access.Status != SettingsGameConfigAccessStatus.Granted)
        {
            WriteGameConfigFailureSafely(access.FailureCategory);
            _state.ShowRuntimeActionFailure(
                access.Status == SettingsGameConfigAccessStatus.PermissionCancelled
                    ? "La modification de Config.wtf a été annulée."
                    : "Config.wtf n’est pas accessible pour le moment.");
            return false;
        }

        LauncherSettingsChangeResult result = await _settings.TrySetInstantQuestTextAsync(enabled);
        return result.Status is LauncherSettingsChangeStatus.Saved
            or LauncherSettingsChangeStatus.Unchanged;
    }

    private void OpenGameFolder()
    {
        PublishLocalActionResult(_localActions.OpenGameFolder());
    }

    private void OpenLogs()
    {
        PublishLocalActionResult(_localActions.OpenDiagnostic());
    }

    private void CheckLauncherUpdate()
    {
        if (_selfUpdate is not null)
        {
            _ = ObserveLauncherUpdateCheckAsync(_selfUpdate.CheckAsync());
        }
    }

    private async Task ObserveLauncherUpdateCheckAsync(
        Task<LauncherSelfUpdateCheckResult> check)
    {
        LauncherSelfUpdateCheckResult result = await check.ConfigureAwait(true);
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }
        if (result.Outcome == LauncherSelfUpdateCheckOutcome.Failed
            && result.ErrorCategory is LauncherSelfUpdateErrorCategory category)
        {
            _state.ShowRuntimeActionFailure(
                LauncherSelfUpdateCoordinator.GetUserMessage(category));
        }
    }

    private void StartLauncherUpdate()
    {
        if (_selfUpdate is null)
        {
            return;
        }

        LauncherSelfUpdateStartResult start = _selfUpdate.TryStartUpdate();
        if (!start.IsStarted)
        {
            if (start.Status is LauncherSelfUpdateStartStatus.Busy
                or LauncherSelfUpdateStartStatus.RejectedByCompatibility)
            {
                _state.ShowRuntimeActionFailure(
                    "Une autre opération Atlas est déjà en cours.");
            }
            return;
        }

        _ = ObserveLauncherUpdateAsync(start.Completion!);
    }

    private async Task ObserveLauncherUpdateAsync(
        Task<LauncherSelfUpdateCompletion> completion)
    {
        LauncherSelfUpdateCompletion result = await completion.ConfigureAwait(true);
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }
        if (result.Outcome == LauncherOperationOutcome.Failed
            && result.ErrorCategory is LauncherSelfUpdateErrorCategory category)
        {
            _state.ShowRuntimeActionFailure(
                LauncherSelfUpdateCoordinator.GetUserMessage(category));
        }
    }

    private void PublishLocalActionResult(LauncherLocalActionResult result)
    {
        if (result.Status is (LauncherLocalActionStatus.Failed
                or LauncherLocalActionStatus.Unavailable)
            && !string.IsNullOrWhiteSpace(result.UserMessage))
        {
            _state.ShowRuntimeActionFailure(result.UserMessage!);
        }
    }

    private void Settings_AvailabilityChanged(object? sender, EventArgs e)
    {
        _browseInstallPath.RaiseCanExecuteChanged();
    }

    private void LocalActions_AvailabilityChanged(object? sender, EventArgs e)
    {
        _openGameFolder.RaiseCanExecuteChanged();
        _openLogs.RaiseCanExecuteChanged();
    }

    private void SelfUpdate_AvailabilityChanged(object? sender, EventArgs e)
    {
        _checkLauncherUpdate.RaiseCanExecuteChanged();
        _startLauncherUpdate.RaiseCanExecuteChanged();
    }

    private void WriteLocaleFailureSafely(string? failureCategory)
    {
        try
        {
            _writeLog(
                "Langue du jeu V2 non appliquée au client: category="
                + (string.IsNullOrWhiteSpace(failureCategory)
                    ? "Unknown"
                    : failureCategory)
                + ".");
        }
        catch
        {
            // A diagnostic failure cannot replace the persisted preference.
        }
    }

    private void WriteGameConfigFailureSafely(string? failureCategory)
    {
        try
        {
            _writeLog(
                "Config.wtf V2 non modifié: category="
                + (string.IsNullOrWhiteSpace(failureCategory)
                    ? "PermissionCancelled"
                    : failureCategory)
                + ".");
        }
        catch
        {
            // A diagnostic failure cannot replace the user-facing result.
        }
    }

    private void WriteStartupFailureSafely(string? failureCategory)
    {
        try
        {
            _writeLog(
                "Démarrage Windows V2 non modifié: category="
                + (string.IsNullOrWhiteSpace(failureCategory)
                    ? "Unknown"
                    : failureCategory)
                + ".");
        }
        catch
        {
            // A diagnostic failure cannot replace the user-facing result.
        }
    }
}
