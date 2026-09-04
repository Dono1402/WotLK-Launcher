using System.Windows.Input;
using WotLK.Launcher.Game;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class GameVerificationCommand : IDisposable
{
    private readonly IGameVerificationRuntime _runtime;
    private readonly Func<bool> _ensureWritable;
    private readonly DelegateCommand _command;
    private int _disposeState;

    internal GameVerificationCommand(
        IGameVerificationRuntime runtime,
        Func<bool>? ensureWritable = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _ensureWritable = ensureWritable ?? (static () => true);
        _command = new DelegateCommand(
            Execute,
            () => _runtime.CanVerify);
        _runtime.AvailabilityChanged += Runtime_AvailabilityChanged;
    }

    internal ICommand Command => _command;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _runtime.AvailabilityChanged -= Runtime_AvailabilityChanged;
        _command.Dispose();
    }

    private void Execute()
    {
        if (_ensureWritable())
        {
            _ = _runtime.TryStartFullRepair();
        }
    }

    private void Runtime_AvailabilityChanged(object? sender, EventArgs e)
    {
        _command.RaiseCanExecuteChanged();
    }
}
