using System.Windows.Threading;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class ProfileStateAdapter : IDisposable
{
    private readonly ProfileUiState _target;
    private readonly GameUiState _game;
    private readonly ILauncherProfileRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private long _latestSequence = -1;
    private long _lastFailureSessionSequence = -1;
    private int _disposeState;

    internal ProfileStateAdapter(
        ProfileUiState target,
        GameUiState game,
        ILauncherProfileRuntime runtime,
        Dispatcher dispatcher)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
        ApplyOrQueue(_runtime.CurrentSnapshot);
    }

    internal static ProfileViewState Project(ProfileRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string error = snapshot.FailureCategory == LauncherSessionFailureCategory.None
            || !snapshot.IsAuthenticated
            ? string.Empty
            : MapFailure(snapshot.FailureCategory);
        return new ProfileViewState(
            IsAuthenticated: snapshot.IsAuthenticated,
            IsLoggingOut: snapshot.IsLoggingOut,
            Username: snapshot.Username,
            Initial: snapshot.DisplayInitial,
            IsEmailVerified: snapshot.IsEmailVerified,
            EmailStatusText: snapshot.IsEmailVerified
                ? "Adresse e-mail vérifiée"
                : "Adresse e-mail non vérifiée",
            CanLogout: snapshot.CanLogout,
            LogoutLabel: snapshot.IsLoggingOut ? "Déconnexion…" : "Déconnexion",
            LogoutToolTip: snapshot.LogoutUnavailableReason,
            ErrorMessage: error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
        }
    }

    private void Runtime_SnapshotChanged(object? sender, ProfileRuntimeSnapshotEventArgs e)
    {
        ApplyOrQueue(e.Snapshot);
    }

    private void ApplyOrQueue(ProfileRuntimeSnapshot snapshot)
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

    private void Apply(ProfileRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0 || snapshot.Sequence <= _latestSequence)
        {
            return;
        }

        _latestSequence = snapshot.Sequence;
        _target.ApplyView(Project(snapshot));
        if (snapshot.FailureCategory != LauncherSessionFailureCategory.None
            && !snapshot.IsAuthenticated
            && snapshot.SessionSequence > _lastFailureSessionSequence)
        {
            _lastFailureSessionSequence = snapshot.SessionSequence;
            _game.ShowNotification(
                "La session locale a été fermée, mais la déconnexion n’a pas pu être confirmée.",
                GameSemanticTone.Warning);
        }
    }

    private static string MapFailure(LauncherSessionFailureCategory category)
    {
        return category switch
        {
            LauncherSessionFailureCategory.Network =>
                "Atlas est indisponible. Ta session reste active.",
            LauncherSessionFailureCategory.Timeout =>
                "La déconnexion a expiré. Réessaie dans quelques instants.",
            LauncherSessionFailureCategory.ServerRejected =>
                "Atlas a refusé la déconnexion. Ta session reste active.",
            LauncherSessionFailureCategory.SecureStorage =>
                "La session locale n’a pas pu être supprimée.",
            _ => "La déconnexion n’a pas pu être terminée. Réessaie."
        };
    }
}
