using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;
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
    private int _disposeState;

    internal AddonsCommands(
        LauncherAddonsCoordinator runtime,
        AddonsUiState state,
        Window owner,
        Func<string> getInstallPath,
        Func<Window, string, bool>? ensureWritable = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _getInstallPath = getInstallPath ?? throw new ArgumentNullException(nameof(getInstallPath));
        _ensureWritable = ensureWritable ?? GameDirectoryAccess.EnsureWritable;
        _primary = new AddonParameterCommand(StartPrimary, CanInvokePrimary);
        _remove = new AddonParameterCommand(StartRemove, CanRemove);
        _updateAll = new DelegateCommand(StartUpdateAll, CanUpdateAll);
        _state.AttachCommands(_primary, _updateAll, _remove);
        _state.PropertyChanged += State_PropertyChanged;
    }

    internal AddonsCatalogStartStatus RefreshCatalog(bool forceRefresh = false)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return AddonsCatalogStartStatus.ShuttingDown;
        }

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

    private void StartPrimary(string addonId)
    {
        AddonUiItem? item = _state.Current.Catalog.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, addonId, StringComparison.OrdinalIgnoreCase));
        if (item?.CanCancelOperation == true)
        {
            _runtime.CancelCurrent();
            return;
        }
        if (!EnsureWritable())
        {
            return;
        }

        Start(() => _runtime.TryInvokePrimary(addonId));
    }

    private void StartRemove(string addonId)
    {
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
        if (EnsureWritable())
        {
            Start(_runtime.TryUpdateAll);
        }
    }

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
            ShowStartFailure(result.Status);
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
            _ => string.Empty
        };
        if (message.Length > 0)
        {
            _state.ShowLocalNotification(message);
        }
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _primary.RaiseCanExecuteChanged();
        _remove.RaiseCanExecuteChanged();
        _updateAll.RaiseCanExecuteChanged();
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
