using System.Windows.Input;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class LogoutCommand : IDisposable
{
    private readonly ILauncherProfileRuntime _runtime;
    private readonly DelegateCommand _command;
    private int _disposeState;

    internal LogoutCommand(ILauncherProfileRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _command = new DelegateCommand(
            Execute,
            () => _runtime.CurrentSnapshot.CanLogout);
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
    }

    internal ICommand Command => _command;

    private void Execute()
    {
        ProfileLogoutStartResult start = _runtime.TryLogout();
        if (start.IsStarted && start.Completion is not null)
        {
            _ = ObserveCompletionAsync(start.Completion);
        }
    }

    private void Runtime_SnapshotChanged(object? sender, ProfileRuntimeSnapshotEventArgs e)
    {
        _command.RaiseCanExecuteChanged();
    }

    private static async Task ObserveCompletionAsync(Task<LauncherSessionCompletion> completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // Runtime results are projected through snapshots; observation is defensive.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
        _command.Dispose();
    }
}
