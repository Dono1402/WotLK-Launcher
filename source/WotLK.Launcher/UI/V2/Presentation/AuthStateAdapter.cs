using System.Windows.Threading;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class AuthStateAdapter : IDisposable
{
    private readonly AuthUiState _authentication;
    private readonly ShellUiState _shell;
    private readonly GameUiState _game;
    private readonly LauncherSessionCoordinator _runtime;
    private readonly Dispatcher _dispatcher;
    private long _latestSequence = long.MinValue;
    private long _lastEmailWarningSequence = long.MinValue;
    private int _disposeState;

    internal AuthStateAdapter(
        AuthUiState authentication,
        ShellUiState shell,
        GameUiState game,
        LauncherSessionCoordinator runtime,
        Dispatcher dispatcher)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
        ApplyOrQueue(_runtime.CurrentSnapshot);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
        }
    }

    private void Runtime_SnapshotChanged(
        object? sender,
        AuthSessionSnapshotEventArgs eventArgs)
    {
        ApplyOrQueue(eventArgs.Snapshot);
    }

    private void ApplyOrQueue(AuthSessionSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Apply(snapshot);
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => Apply(snapshot)));
    }

    private void Apply(AuthSessionSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0 || snapshot.Sequence <= _latestSequence)
        {
            return;
        }

        _latestSequence = snapshot.Sequence;
        _authentication.ApplySessionSnapshot(snapshot);
        _shell.ApplySessionSnapshot(snapshot);

        if (snapshot.IsAuthenticated
            && !snapshot.IsEmailVerified
            && snapshot.Sequence > _lastEmailWarningSequence)
        {
            _lastEmailWarningSequence = snapshot.Sequence;
            _game.ShowNotification(
                "Adresse e-mail non vérifiée. Tu peux continuer, mais pense à la confirmer.",
                GameSemanticTone.Warning);
        }
    }
}
