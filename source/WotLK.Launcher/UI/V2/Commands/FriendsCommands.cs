using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class FriendsCommands : IDisposable
{
    private readonly LauncherFriendsCoordinator _runtime;
    private readonly FriendsUiState _state;
    private readonly Dispatcher _dispatcher;
    private readonly FriendParameterCommand _accept;
    private readonly FriendParameterCommand _reject;
    private readonly FriendParameterCommand _cancel;
    private readonly FriendParameterCommand _remove;
    private readonly DelegateCommand _refresh;
    private readonly DelegateCommand _send;
    private int _disposeState;

    internal FriendsCommands(
        LauncherFriendsCoordinator runtime,
        FriendsUiState state,
        Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _refresh = new DelegateCommand(Refresh, CanRefresh);
        _send = new DelegateCommand(SendRequest, CanSendRequest);
        _accept = new FriendParameterCommand(
            accountId => Start(() => _runtime.TryAcceptRequest(accountId)),
            accountId => CanActOn(_state.Current.IncomingRequests, accountId, item => item.CanAccept));
        _reject = new FriendParameterCommand(
            accountId => Start(() => _runtime.TryRejectRequest(accountId)),
            accountId => CanActOn(_state.Current.IncomingRequests, accountId, item => item.CanReject));
        _cancel = new FriendParameterCommand(
            accountId => Start(() => _runtime.TryCancelRequest(accountId)),
            accountId => CanActOn(_state.Current.OutgoingRequests, accountId, item => item.CanCancel));
        _remove = new FriendParameterCommand(
            accountId => Start(() => _runtime.TryRemoveFriend(accountId)),
            accountId => CanActOn(_state.Current.Friends, accountId, item => item.CanRemove));
        _state.AttachCommands(_refresh, _send, _accept, _reject, _cancel, _remove);
        _state.PropertyChanged += State_PropertyChanged;
    }

    internal void Refresh()
    {
        if (Volatile.Read(ref _disposeState) == 0)
        {
            Start(_runtime.TryRefresh);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _state.PropertyChanged -= State_PropertyChanged;
        _refresh.Dispose();
        _send.Dispose();
        _accept.Dispose();
        _reject.Dispose();
        _cancel.Dispose();
        _remove.Dispose();
    }

    private bool CanRefresh()
    {
        return Volatile.Read(ref _disposeState) == 0 && _state.Current.CanRefresh;
    }

    private bool CanSendRequest()
    {
        int length = _state.SearchText.Trim().Length;
        return Volatile.Read(ref _disposeState) == 0
            && _state.Current.CanSendRequest
            && length is >= 2 and <= 32;
    }

    private void SendRequest()
    {
        string username = _state.SearchText.Trim();
        if (username.Length is < 2 or > 32)
        {
            _state.ShowLocalSearchError("Saisis un nom d’utilisateur Atlas valide.");
            return;
        }

        FriendsActionStartResult start = _runtime.TrySendRequest(username);
        if (!start.IsStarted || start.Completion is null)
        {
            ShowStartFailure(start.Status);
            return;
        }

        _ = ObserveSendAsync(start.Completion);
    }

    private void Start(Func<FriendsActionStartResult> startAction)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        FriendsActionStartResult start = startAction();
        if (!start.IsStarted || start.Completion is null)
        {
            ShowStartFailure(start.Status);
            return;
        }

        _ = ObserveSilentlyAsync(start.Completion);
    }

    private async Task ObserveSendAsync(Task<FriendsActionCompletion> completion)
    {
        FriendsActionCompletion result;
        try
        {
            result = await completion.ConfigureAwait(false);
        }
        catch
        {
            return;
        }
        if (result.Status != FriendsActionCompletionStatus.Succeeded
            || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(
                _state.ClearSearchText,
                DispatcherPriority.DataBind);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static async Task ObserveSilentlyAsync(Task<FriendsActionCompletion> completion)
    {
        try
        {
            _ = await completion.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void ShowStartFailure(FriendsActionStartStatus status)
    {
        string message = status switch
        {
            FriendsActionStartStatus.InvalidRequest => "Saisis un nom d’utilisateur Atlas valide.",
            FriendsActionStartStatus.NotAuthenticated => "Reconnecte-toi pour gérer tes amis.",
            FriendsActionStartStatus.ShuttingDown => "Atlas Launcher est en cours de fermeture.",
            FriendsActionStartStatus.Busy => string.Empty,
            _ => "Cette action n’est pas disponible pour le moment."
        };
        if (message.Length > 0)
        {
            _state.ShowLocalSearchError(message);
        }
    }

    private bool CanActOn(
        IEnumerable<FriendUiItem> source,
        uint accountId,
        Func<FriendUiItem, bool> predicate)
    {
        return Volatile.Read(ref _disposeState) == 0
            && source.Any(item => item.AccountId == accountId && predicate(item));
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _refresh.RaiseCanExecuteChanged();
        _send.RaiseCanExecuteChanged();
        _accept.RaiseCanExecuteChanged();
        _reject.RaiseCanExecuteChanged();
        _cancel.RaiseCanExecuteChanged();
        _remove.RaiseCanExecuteChanged();
    }

    private sealed class FriendParameterCommand : ICommand, IDisposable
    {
        private readonly Action<uint> _execute;
        private readonly Func<uint, bool> _canExecute;
        private readonly Dispatcher? _ownerDispatcher;
        private int _disposeState;

        internal FriendParameterCommand(Action<uint> execute, Func<uint, bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
            _ownerDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return Volatile.Read(ref _disposeState) == 0
                && TryGetAccountId(parameter, out uint accountId)
                && _canExecute(accountId);
        }

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter) && TryGetAccountId(parameter, out uint accountId))
            {
                _execute(accountId);
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

        private static bool TryGetAccountId(object? value, out uint accountId)
        {
            switch (value)
            {
                case uint id:
                    accountId = id;
                    return id > 0;
                case int id when id > 0:
                    accountId = (uint)id;
                    return true;
                case long id when id is > 0 and <= uint.MaxValue:
                    accountId = (uint)id;
                    return true;
                default:
                    accountId = 0;
                    return false;
            }
        }
    }
}
