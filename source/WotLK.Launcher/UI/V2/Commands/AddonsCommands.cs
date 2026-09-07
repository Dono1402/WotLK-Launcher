using System.ComponentModel;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class AddonsCommands : IDisposable
{
    private readonly LauncherAddonsCoordinator _runtime;
    private readonly AddonsUiState _state;
    private readonly Window _owner;
    private readonly Func<string> _getInstallPath;
    private readonly Func<Window, string, bool> _ensureWritable;
    private readonly AddonParameterCommand _primary;
    private readonly AddonParameterCommand _remove;
    private readonly DelegateCommand _updateAll;
    private readonly AddonParameterCommand _verify;
    private readonly AddonParameterCommand _reinstall;
    private readonly DelegateCommand _installSelected;
    private readonly DelegateCommand _retryFailed;
    private readonly DelegateCommand _importProfile;
    private readonly DelegateCommand _exportProfile;
    private readonly IAddonLibraryDialogs _libraryDialogs;
    private readonly IAddonLibraryStore _libraryStore;
    private string _libraryClientRoot = string.Empty;
    private int _disposeState;

    internal AddonsCommands(
        LauncherAddonsCoordinator runtime,
        AddonsUiState state,
        Window owner,
        Func<string> getInstallPath,
        Func<Window, string, bool>? ensureWritable = null,
        IAddonLibraryDialogs? libraryDialogs = null,
        IAddonLibraryStore? libraryStore = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _getInstallPath = getInstallPath ?? throw new ArgumentNullException(nameof(getInstallPath));
        _ensureWritable = ensureWritable ?? GameDirectoryAccess.EnsureWritable;
        _libraryDialogs = libraryDialogs ?? new AddonLibraryDialogs();
        _libraryStore = libraryStore ?? new JsonAddonLibraryStore(Path.Combine(LauncherSettings.SettingsDirectory, "addons-library"));
        _primary = new AddonParameterCommand(StartPrimary, CanInvokePrimary);
        _remove = new AddonParameterCommand(StartRemove, CanRemove);
        _updateAll = new DelegateCommand(StartUpdateAll, CanUpdateAll);
        _verify = new AddonParameterCommand(StartVerify, CanManageInstalled);
        _reinstall = new AddonParameterCommand(StartReinstall, CanManageInstalled);
        _installSelected = new DelegateCommand(StartSelected, () => CanManageLibrary() && !_state.SelectedAddonIds.IsEmpty);
        _retryFailed = new DelegateCommand(StartRetryFailed, () => CanManageLibrary() && _state.Current.CanRetryFailed);
        _importProfile = new DelegateCommand(ImportProfile, () => Volatile.Read(ref _disposeState) == 0 && _state.Current.IsInteractive);
        _exportProfile = new DelegateCommand(ExportProfile, () => Volatile.Read(ref _disposeState) == 0 && !_state.SelectedAddonIds.IsEmpty);
        _state.AttachCommands(_primary, _updateAll, _remove);
        _state.AttachLibraryCommands(_verify, _reinstall, _installSelected, _retryFailed, _importProfile, _exportProfile);
        RefreshLibraryRoot();
        _state.PropertyChanged += State_PropertyChanged;
    }

    internal AddonsCatalogStartStatus RefreshCatalog(bool forceRefresh = false)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return AddonsCatalogStartStatus.ShuttingDown;
        }

        RefreshLibraryRoot();
        AddonsCatalogStartResult result = _runtime.TryLoadCatalog(forceRefresh);
        if (result.Status is AddonsCatalogStartStatus.Busy)
        {
            _state.ShowLocalNotification("Une autre opération est déjà en cours.");
        }
        return result.Status;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _state.PropertyChanged -= State_PropertyChanged;
        _primary.Dispose();
        _remove.Dispose();
        _updateAll.Dispose();
        _verify.Dispose();
        _reinstall.Dispose();
        _installSelected.Dispose();
        _retryFailed.Dispose();
        _importProfile.Dispose();
        _exportProfile.Dispose();
    }

    private bool CanInvokePrimary(string addonId)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        return _state.Current.Catalog.Any(item =>
            string.Equals(item.Id, addonId, StringComparison.OrdinalIgnoreCase)
            && item.CanInvokePrimary);
    }

    private bool CanRemove(string addonId)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        return _state.Current.Catalog.Any(item =>
            string.Equals(item.Id, addonId, StringComparison.OrdinalIgnoreCase)
            && item.CanRemove);
    }

    private bool CanUpdateAll() => Volatile.Read(ref _disposeState) == 0
        && _state.Current.CanUpdateAll;

    private bool CanManageLibrary() => Volatile.Read(ref _disposeState) == 0
        && _state.Current.CanMutate && _state.Current.IsRuntimeConnected;

    private bool CanManageInstalled(string addonId) => CanManageLibrary()
        && _state.Current.Catalog.Any(item => item.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase)
            && item.IsCatalogEntry && item.IsManagedByAtlas && !item.IsBusy);

    private void StartPrimary(string addonId)
    {
        AddonUiItem? item = _state.Current.Catalog.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, addonId, StringComparison.OrdinalIgnoreCase));
        if (item?.CanCancelOperation == true)
        {
            _runtime.CancelCurrent();
            return;
        }
        if (item is null || !item.IsCatalogEntry) return;
        bool force = _runtime.CurrentSnapshot.Items.Any(candidate => candidate.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase)
            && candidate.ErrorCategory != AddonsErrorCategory.None && candidate.RetryAction == AddonsRequestedAction.Reinstall);
        StartWithPlan([addonId], allow => _runtime.TryInvokePrimary(addonId, allow), forceReinstall: force);
    }

    private void StartRemove(string addonId)
    {
        string[] dependents = _runtime.CurrentSnapshot.Items.Where(item => item.IsInstalled
            && item.Dependencies.Contains(addonId, StringComparer.OrdinalIgnoreCase)).Select(item => item.Name).ToArray();
        if (dependents.Length > 0)
        {
            _state.ShowLocalNotification(L("Cet addon est nécessaire à : ", "This addon is required by: ") + string.Join(", ", dependents));
            return;
        }
        if (EnsureWritable())
        {
            Start(() => _runtime.TryRemove(addonId));
        }
    }

    private void StartUpdateAll()
    {
        if (_state.Current.IsBatchOperation && _state.Current.CanCancelCurrent)
        {
            _runtime.CancelCurrent();
            return;
        }
        string[] ids = _state.Current.Catalog.Where(item => item.IsCatalogEntry && item.NeedsUpdate).Select(item => item.Id).ToArray();
        StartWithPlan(ids, allow => _runtime.TryInstallSelection(ids, allow));
    }

    private void StartSelected()
    {
        ImmutableArray<string> ids = _state.SelectedAddonIds;
        StartWithPlan(ids, allow => _runtime.TryInstallSelection(ids, allow));
    }

    private void StartVerify(string addonId) => Start(() => _runtime.TryVerify(addonId));

    private void StartReinstall(string addonId) => StartWithPlan([addonId],
        allow => _runtime.TryReinstall(addonId, allow), forceReinstall: true);

    private void StartRetryFailed()
    {
        AddonsRuntimeSnapshot snapshot = _runtime.CurrentSnapshot;
        string[] ids = snapshot.FailedAddonIds.Concat(snapshot.UnprocessedAddonIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        AddonsRequestedAction retryAction = snapshot.PendingRetryAction != AddonsRequestedAction.None
            ? snapshot.PendingRetryAction : snapshot.Error.Action;
        if (retryAction is AddonsRequestedAction.Verify or AddonsRequestedAction.VerifySelection)
        {
            Start(() => _runtime.TryRetryFailed());
            return;
        }
        if (retryAction == AddonsRequestedAction.Remove)
        {
            if (EnsureWritable()) Start(() => _runtime.TryRetryFailed());
            return;
        }
        StartWithPlan(ids, allow => _runtime.TryRetryFailed(allow), allowEmptyPlan: true, previewPlan: _runtime.PreviewRetry);
    }

    private void StartWithPlan(IEnumerable<string> addonIds, Func<bool, AddonsActionStartResult> startAction,
        bool forceReinstall = false, bool allowEmptyPlan = false, Func<AddonsPlanPreview>? previewPlan = null)
    {
        string[] ids = addonIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        previewPlan ??= () => _runtime.PreviewInstall(ids, forceReinstall);
        AddonsPlanPreview plan = previewPlan();
        if (!plan.IsValid)
        {
            ShowPlanFailure(plan.ErrorCode, plan.RelatedAddonIds);
            return;
        }
        if (plan.Items.IsEmpty && !allowEmptyPlan)
        {
            _state.ShowLocalNotification(L("La sélection est déjà à jour.", "The selection is already up to date."));
            return;
        }
        string clientRoot = _getInstallPath();
        bool confirm = plan.RequiresExternalReplacementConfirmation || plan.Items.Any(item => item.IsDependency) || ids.Length > 1;
        if (confirm && !_libraryDialogs.ConfirmInstall(_owner, plan)) return;
        // A modal plan may outlive a refresh, logout or settings change. Only
        // apply the exact displayed operation to the same selected game folder.
        AddonsPlanPreview current = previewPlan();
        if (!string.Equals(clientRoot, _getInstallPath(), StringComparison.OrdinalIgnoreCase)
            || !current.IsValid || PlanIdentity(plan) != PlanIdentity(current))
        {
            _state.ShowLocalNotification(L("La sélection a changé. Vérifie le nouveau plan avant de relancer l’installation.",
                "The selection has changed. Review the new plan before starting the installation again."));
            return;
        }
        if (EnsureWritable()) Start(() => startAction(plan.RequiresExternalReplacementConfirmation && confirm));
    }

    private static string PlanIdentity(AddonsPlanPreview plan) => JsonSerializer.Serialize(plan.Items);

    private void RefreshLibraryRoot()
    {
        string root = _getInstallPath();
        if (string.Equals(root, _libraryClientRoot, StringComparison.OrdinalIgnoreCase)) return;
        _libraryClientRoot = root;
        _state.ConfigureLibraryStore(_libraryStore, root);
    }

    private void ImportProfile()
    {
        string? path = _libraryDialogs.OpenProfile(_owner);
        if (path is null) return;
        try
        {
            FileInfo file = new(path);
            if (file.Length > 256 * 1024) throw new InvalidDataException("Profile exceeds the size limit.");
            _state.ImportSelectionJson(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _state.ShowLocalNotification(L("Ce fichier ne contient pas une sélection d’addons Atlas valide.", "This file does not contain a valid Atlas addon selection."));
        }
    }

    private void ExportProfile()
    {
        string? path = _libraryDialogs.SaveProfile(_owner);
        if (path is null) return;
        try
        {
            File.WriteAllText(path, _state.ExportSelectionJson(), new UTF8Encoding(false));
            _state.ShowLocalNotification(L("Sélection d’addons exportée.", "Addon selection exported."));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _state.ShowLocalNotification(L("Impossible d’écrire le fichier de sélection.", "Unable to write the selection file."));
        }
    }

    private void ShowPlanFailure(string errorCode, IEnumerable<string> relatedIds)
    {
        string message = errorCode switch
        {
            "dependency-cycle" => L("Le catalogue contient une boucle de dépendances.", "The catalogue contains a dependency cycle."),
            "dependency-missing" => L("Une dépendance nécessaire manque dans le catalogue.", "A required dependency is missing from the catalogue."),
            "dependency-in-use" => L("Cet addon est utilisé par d’autres addons installés.", "This addon is used by other installed addons."),
            "dependency-inspection-failed" => L("Les dépendances locales n’ont pas pu être vérifiées. La suppression est suspendue.", "Local dependencies could not be checked. Removal has been suspended."),
            _ => L("Cette sélection ne peut pas être installée avec le catalogue actuel.", "This selection cannot be installed with the current catalogue.")
        };
        string[] names = relatedIds.Select(id => _state.Current.Catalog.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id).Take(8).ToArray();
        _state.ShowLocalNotification(message + (names.Length == 0 ? string.Empty : " " + string.Join(", ", names)));
    }

    private static string L(string french, string english) => LauncherLocalization.IsEnglish ? english : french;

    private bool EnsureWritable()
    {
        try
        {
            return _ensureWritable(_owner, _getInstallPath());
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _state.ShowLocalNotification("Atlas n’a pas accès au dossier du jeu.");
            return false;
        }
    }

    private void Start(Func<AddonsActionStartResult> startAction)
    {
        AddonsActionStartResult result = startAction();
        if (!result.IsStarted || result.Completion is null)
        {
            if (result.Status is AddonsActionStartStatus.InvalidPlan or AddonsActionStartStatus.DependencyInUse)
                ShowPlanFailure(result.ErrorCode, result.RelatedAddonIds);
            else ShowStartFailure(result.Status);
            return;
        }

        _ = ObserveSilentlyAsync(result.Completion);
    }

    private static async Task ObserveSilentlyAsync(Task<AddonsActionCompletion> completion)
    {
        try
        {
            _ = await completion.ConfigureAwait(false);
        }
        catch
        {
            // The coordinator owns and publishes every terminal failure.
        }
    }

    private void ShowStartFailure(AddonsActionStartStatus status)
    {
        string message = status switch
        {
            AddonsActionStartStatus.Busy => "Une autre opération est déjà en cours.",
            AddonsActionStartStatus.ShuttingDown => "Atlas Launcher est en cours de fermeture.",
            AddonsActionStartStatus.NotAuthenticated => "Reconnecte-toi pour gérer tes addons.",
            AddonsActionStartStatus.CatalogUnavailable => "Le catalogue Atlas n’est pas encore disponible.",
            AddonsActionStartStatus.ClientUnavailable => "Installe d’abord le client WotLK.",
            AddonsActionStartStatus.RejectedByCompatibility =>
                "Cette action attend la fin de l’opération en cours.",
            AddonsActionStartStatus.AddonNotFound => "Cet addon n’est plus présent dans le catalogue.",
            AddonsActionStartStatus.ConfirmationRequired => L("Confirme le remplacement de l’installation manuelle avant de continuer.", "Confirm replacement of the manual installation before continuing."),
            _ => string.Empty
        };
        if (message.Length > 0)
        {
            _state.ShowLocalNotification(message);
        }
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshLibraryRoot();
        _primary.RaiseCanExecuteChanged();
        _remove.RaiseCanExecuteChanged();
        _updateAll.RaiseCanExecuteChanged();
        _verify.RaiseCanExecuteChanged();
        _reinstall.RaiseCanExecuteChanged();
        _installSelected.RaiseCanExecuteChanged();
        _retryFailed.RaiseCanExecuteChanged();
        _importProfile.RaiseCanExecuteChanged();
        _exportProfile.RaiseCanExecuteChanged();
    }

    private sealed class AddonParameterCommand : ICommand, IDisposable
    {
        private readonly Action<string> _execute;
        private readonly Func<string, bool> _canExecute;
        private readonly Dispatcher? _ownerDispatcher;
        private int _disposeState;

        internal AddonParameterCommand(
            Action<string> execute,
            Func<string, bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
            _ownerDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            Volatile.Read(ref _disposeState) == 0
            && parameter is string addonId
            && !string.IsNullOrWhiteSpace(addonId)
            && _canExecute(addonId);

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                _execute((string)parameter!);
            }
        }

        internal void RaiseCanExecuteChanged()
        {
            if (_ownerDispatcher is null || _ownerDispatcher.CheckAccess())
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!_ownerDispatcher.HasShutdownStarted && !_ownerDispatcher.HasShutdownFinished)
            {
                _ownerDispatcher.BeginInvoke(
                    DispatcherPriority.DataBind,
                    new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            {
                RaiseCanExecuteChanged();
            }
        }
    }
}
