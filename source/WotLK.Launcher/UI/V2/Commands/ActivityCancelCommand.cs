using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class ActivityCancelCommand : ICommand, IDisposable
{
    private readonly ActivityUiState _state;
    private readonly Func<bool> _cancelCurrent;
    private readonly Dispatcher? _ownerDispatcher;
    private int _disposeState;

    internal ActivityCancelCommand(
        LauncherOperationCoordinator operations,
        ActivityUiState state)
        : this(
            state,
            (operations ?? throw new ArgumentNullException(nameof(operations))).CancelFromUser)
    {
    }

    internal ActivityCancelCommand(ActivityUiState state, Func<bool> cancelCurrent)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _cancelCurrent = cancelCurrent ?? throw new ArgumentNullException(nameof(cancelCurrent));
        _ownerDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        _state.PropertyChanged += State_PropertyChanged;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _disposeState) == 0
        && _state.Current.ActiveOperation is
        {
            CanUserCancel: true,
            IsCancellationRequested: false
        };

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _cancelCurrent();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _state.PropertyChanged -= State_PropertyChanged;
        RaiseCanExecuteChanged();
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RaiseCanExecuteChanged();

    private void RaiseCanExecuteChanged()
    {
        if (_ownerDispatcher is null || _ownerDispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_ownerDispatcher.HasShutdownStarted && !_ownerDispatcher.HasShutdownFinished)
        {
            _ = _ownerDispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
        }
    }
}
