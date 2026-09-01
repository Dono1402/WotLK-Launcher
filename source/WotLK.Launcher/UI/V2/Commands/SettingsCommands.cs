using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using WotLK.Launcher.Runtime;
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
    SettingsGameLocaleApplyResult Apply(Window owner, string installPath, string gameLocale);
}

internal sealed class SettingsGameLocaleApplier : ISettingsGameLocaleApplier
{
    public SettingsGameLocaleApplyResult Apply(
        Window owner,
        string installPath,
        string gameLocale)
    {
        if (!GameInstallServices.HasPlayableClient(installPath))
        {
            return new SettingsGameLocaleApplyResult(
                SettingsGameLocaleApplyStatus.ClientNotInstalled);
        }

        try
        {
            if (!GameDirectoryAccess.EnsureWritable(owner, installPath))
            {
                return new SettingsGameLocaleApplyResult(
                    SettingsGameLocaleApplyStatus.PermissionCancelled);
            }

            _ = GameInstallServices.EnsureDefaultClientConfig(installPath, gameLocale);
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

internal sealed class SettingsCommands : IDisposable
{
    private readonly SettingsUiState _state;
    private readonly ILauncherSettingsRuntime _settings;
    private readonly ILauncherLocalActions _localActions;
    private readonly Window _owner;
    private readonly ISettingsFolderPicker _folderPicker;
    private readonly ISettingsGameLocaleApplier _localeApplier;
    private readonly Action<string> _writeLog;
    private readonly DelegateCommand _browseInstallPath;
    private readonly DelegateCommand _openGameFolder;
    private readonly DelegateCommand _openLogs;
    private int _disposeState;

    internal SettingsCommands(
        SettingsUiState state,
        ILauncherSettingsRuntime settings,
        ILauncherLocalActions localActions,
        Window owner,
        Action<string> writeLog,
        ISettingsFolderPicker? folderPicker = null,
        ISettingsGameLocaleApplier? localeApplier = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localActions = localActions ?? throw new ArgumentNullException(nameof(localActions));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _folderPicker = folderPicker ?? new SettingsFolderPicker();
        _localeApplier = localeApplier ?? new SettingsGameLocaleApplier();
        _browseInstallPath = new DelegateCommand(
            BrowseInstallPath,
            () => _settings.CurrentSnapshot.CanChangeInstallPath);
        _openGameFolder = new DelegateCommand(
            OpenGameFolder,
            () => _localActions.CanOpenGameFolder);
        _openLogs = new DelegateCommand(
            OpenLogs,
            () => _localActions.CanOpenDiagnostic);
        _settings.AvailabilityChanged += Settings_AvailabilityChanged;
        _localActions.AvailabilityChanged += LocalActions_AvailabilityChanged;
        _state.AttachRuntimeActions(
            _browseInstallPath,
            _openGameFolder,
            _openLogs,
            ChangeGameLocale,
            ChangeCloseAfterLaunch);
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
        _browseInstallPath.Dispose();
        _openGameFolder.Dispose();
        _openLogs.Dispose();
    }

    private void BrowseInstallPath()
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

        _ = _settings.TrySetInstallPath(selectedPath);
    }

    private bool ChangeGameLocale(string gameLocale)
    {
        LauncherSettingsChangeResult change = _settings.TrySetGameLocale(gameLocale);
        if (change.Status == LauncherSettingsChangeStatus.Unchanged)
        {
            return true;
        }

        if (!change.IsSaved)
        {
            return false;
        }

        LauncherSettingsSnapshot current = _settings.CurrentSnapshot;
        SettingsGameLocaleApplyResult apply = _localeApplier.Apply(
            _owner,
            current.InstallPath,
            current.GameLocale);
        if (apply.Status == SettingsGameLocaleApplyStatus.Failed)
        {
            WriteLocaleFailureSafely(apply.FailureCategory);
            _state.ShowRuntimeActionFailure(
                "La langue est enregistrée, mais le client n’a pas pu être modifié maintenant.");
        }

        return true;
    }

    private bool ChangeCloseAfterLaunch(bool closeAfterLaunch)
    {
        LauncherSettingsChangeResult result =
            _settings.TrySetCloseLauncherOnGameStart(closeAfterLaunch);
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
}
