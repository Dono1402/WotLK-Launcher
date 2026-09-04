using System.Windows.Input;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class PrimaryActionCommand : IDisposable
{
    private readonly IGamePrimaryActionRuntime _runtime;
    private readonly Action? _requestAuthentication;
    private readonly Func<bool> _ensureWritable;
    private readonly DelegateCommand _command;
    private int _disposeState;

    internal PrimaryActionCommand(
        IGamePrimaryActionRuntime runtime,
        Action? requestAuthentication = null,
        Func<bool>? ensureWritable = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _requestAuthentication = requestAuthentication;
        _ensureWritable = ensureWritable ?? (static () => true);
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
        GameRuntimeSnapshot snapshot = _runtime.CurrentSnapshot;
        if (RequiresWritableDirectory(snapshot) && !_ensureWritable())
        {
            return;
        }

        _runtime.TryExecutePrimaryAction();
    }

    private static bool RequiresWritableDirectory(GameRuntimeSnapshot snapshot)
    {
        if (snapshot.IsMaintenanceActive)
        {
            return false;
        }

        return snapshot.RetryOperationKind == LauncherOperationKind.GameRepair
            || (snapshot.RetryAction ?? snapshot.Action) is GameAction.Install or GameAction.Update;
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
