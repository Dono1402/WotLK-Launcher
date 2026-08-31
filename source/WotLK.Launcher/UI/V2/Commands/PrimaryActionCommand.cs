using System.Windows.Input;
using WotLK.Launcher.Game;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class PrimaryActionCommand : IDisposable
{
    private readonly IGamePrimaryActionRuntime _runtime;
    private readonly Action? _requestAuthentication;
    private readonly DelegateCommand _command;
    private int _disposeState;

    internal PrimaryActionCommand(
        IGamePrimaryActionRuntime runtime,
        Action? requestAuthentication = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _requestAuthentication = requestAuthentication;
        _command = new DelegateCommand(
            Execute,
            () => _runtime.CanExecutePrimaryAction);
        _runtime.AvailabilityChanged += Runtime_AvailabilityChanged;
        _runtime.PlayAuthenticationRequired += Runtime_PlayAuthenticationRequired;
    }

    internal ICommand Command => _command;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _runtime.AvailabilityChanged -= Runtime_AvailabilityChanged;
        _runtime.PlayAuthenticationRequired -= Runtime_PlayAuthenticationRequired;
        _command.Dispose();
    }

    private void Execute()
    {
        _runtime.TryExecutePrimaryAction();
    }

    private void Runtime_AvailabilityChanged(object? sender, EventArgs e)
    {
        _command.RaiseCanExecuteChanged();
    }

    private void Runtime_PlayAuthenticationRequired(object? sender, EventArgs e)
    {
        _requestAuthentication?.Invoke();
    }
}
