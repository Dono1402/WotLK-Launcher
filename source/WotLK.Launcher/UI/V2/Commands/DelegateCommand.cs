using System.Windows.Input;
using System.Windows.Threading;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class DelegateCommand : ICommand, IDisposable
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;
    private readonly Dispatcher? _ownerDispatcher;
    private int _disposeState;

    internal DelegateCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _ownerDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return Volatile.Read(ref _disposeState) == 0 && _canExecute();
    }

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    internal void RaiseCanExecuteChanged()
    {
        if (_ownerDispatcher is null || _ownerDispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_ownerDispatcher.HasShutdownStarted || _ownerDispatcher.HasShutdownFinished)
        {
            return;
        }

        _ownerDispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            RaiseCanExecuteChanged();
        }
    }
}

internal sealed class DisabledCommand : ICommand
{
    internal static DisabledCommand Instance { get; } = new();

    private DisabledCommand()
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => false;

    public void Execute(object? parameter)
    {
    }
}

internal sealed class PreviewCommand : ICommand
{
    internal static PreviewCommand Instance { get; } = new();

    private PreviewCommand()
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
    }
}
