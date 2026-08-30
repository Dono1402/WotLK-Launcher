using System.Windows.Input;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class DelegateCommand : ICommand, IDisposable
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;
    private int _disposeState;

    internal DelegateCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
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
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
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
