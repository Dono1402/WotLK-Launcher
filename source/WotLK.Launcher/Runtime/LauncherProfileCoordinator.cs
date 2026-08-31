using WotLK.Launcher.Dashboard;
using WotLK.Launcher.Game;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherProfileCoordinator : ILauncherProfileRuntime, IDisposable
{
    private readonly object _sync = new();
    private readonly LauncherSessionCoordinator _session;
    private readonly LauncherOperationCoordinator _operations;
    private readonly GameRuntimeCoordinator _game;
    private readonly LauncherDashboardCoordinator _dashboard;
    private LauncherOperationLease? _logoutLease;
    private Task _activeLogoutTask = Task.CompletedTask;
    private ProfileRuntimeSnapshot _currentSnapshot;
    private long _sequence;
    private int _disposeState;

    internal LauncherProfileCoordinator(
        LauncherSessionCoordinator session,
        LauncherOperationCoordinator operations,
        GameRuntimeCoordinator game,
        LauncherDashboardCoordinator dashboard)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _currentSnapshot = CreateSnapshot(_session.CurrentSnapshot);
        _session.SnapshotChanged += Session_SnapshotChanged;
        _operations.StateChanged += Operations_StateChanged;
        _game.SnapshotChanged += Game_SnapshotChanged;
    }

    public event EventHandler<ProfileRuntimeSnapshotEventArgs>? SnapshotChanged;

    public ProfileRuntimeSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public ProfileLogoutStartResult TryLogout()
    {
        if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
        {
            return Rejected(ProfileLogoutStartStatus.ShuttingDown);
        }

        AuthSessionSnapshot session = _session.CurrentSnapshot;
        if (!session.IsAuthenticated)
        {
            return Rejected(session.IsLoggingOut
                ? ProfileLogoutStartStatus.Busy
                : ProfileLogoutStartStatus.NotAuthenticated);
        }

        lock (_sync)
        {
            if (_logoutLease is not null)
            {
                return RejectedUnsafe(ProfileLogoutStartStatus.Busy);
            }
        }

        bool pendingPlay = _game.CurrentSnapshot.IsPlayPendingAuthentication;
        if (!_operations.IsIdle
            && (!pendingPlay || _operations.ActiveMaintenanceKind is not null))
        {
            return Rejected(ProfileLogoutStartStatus.RejectedByCompatibility);
        }

        if (pendingPlay && !_game.CancelPendingPlayAuthentication())
        {
            return Rejected(ProfileLogoutStartStatus.Busy);
        }

        LauncherOperationStartResult operation = _operations.TryBegin(
            LauncherOperationKind.Logout,
            canUserCancel: false,
            clientIsPlayable: _game.CurrentSnapshot.IsPlayable);
        if (!operation.IsStarted || operation.Lease is null)
        {
            return Rejected(MapOperationStatus(operation.Status));
        }

        LauncherOperationLease lease = operation.Lease;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                lease.CancelForShutdown();
                lease.Dispose();
                return RejectedUnsafe(ProfileLogoutStartStatus.ShuttingDown);
            }

            _logoutLease = lease;
        }

        _dashboard.SuspendAuthenticatedRequests();
        LauncherSessionStartResult sessionStart = _session.TryLogout(lease.CancellationToken);
        if (!sessionStart.IsStarted || sessionStart.Completion is null)
        {
            _dashboard.ResumeAuthenticatedRequests();
            lock (_sync)
            {
                if (ReferenceEquals(_logoutLease, lease))
                {
                    _logoutLease = null;
                }
            }

            lease.Dispose();
            PublishCurrent();
            return Rejected(MapSessionStatus(sessionStart.Status));
        }

        Task<LauncherSessionCompletion> observed = ObserveLogoutAsync(
            lease,
            sessionStart.Completion);
        lock (_sync)
        {
            _activeLogoutTask = observed;
        }

        PublishCurrent();
        return new ProfileLogoutStartResult(
            ProfileLogoutStartStatus.Started,
            sessionStart.AttemptId,
            observed);
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task active;
        lock (_sync)
        {
            active = _activeLogoutTask;
        }

        try
        {
            await active.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<LauncherSessionCompletion> ObserveLogoutAsync(
        LauncherOperationLease lease,
        Task<LauncherSessionCompletion> completion)
    {
        LauncherSessionCompletion result;
        try
        {
            result = await completion.ConfigureAwait(false);
        }
        catch
        {
            result = new LauncherSessionCompletion(
                LauncherSessionCompletionStatus.Failed,
                _session.CurrentSnapshot);
        }

        bool signedOut = result.Snapshot.State == LauncherSessionState.SignedOut;
        if (signedOut)
        {
            _dashboard.ApplySignedOutSession();
        }
        else
        {
            _dashboard.ResumeAuthenticatedRequests();
        }

        lock (_sync)
        {
            if (ReferenceEquals(_logoutLease, lease))
            {
                _logoutLease = null;
            }
        }

        lease.Dispose();
        _game.RefreshAuthenticationAvailability();
        PublishCurrent();
        return result;
    }

    private void Session_SnapshotChanged(object? sender, AuthSessionSnapshotEventArgs e)
    {
        Publish(e.Snapshot);
    }

    private void Operations_StateChanged(object? sender, EventArgs e)
    {
        PublishCurrent();
    }

    private void Game_SnapshotChanged(object? sender, GameRuntimeSnapshotEventArgs e)
    {
        PublishCurrent();
    }

    private void PublishCurrent()
    {
        Publish(_session.CurrentSnapshot);
    }

    private void Publish(AuthSessionSnapshot session)
    {
        ProfileRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            snapshot = CreateSnapshot(session);
            _currentSnapshot = snapshot;
        }

        try
        {
            SnapshotChanged?.Invoke(this, new ProfileRuntimeSnapshotEventArgs(snapshot));
        }
        catch
        {
            // Presentation subscribers cannot interrupt session cleanup.
        }
    }

    private ProfileRuntimeSnapshot CreateSnapshot(AuthSessionSnapshot session)
    {
        bool pendingPlay = _game.CurrentSnapshot.IsPlayPendingAuthentication;
        bool hasActiveLogout;
        lock (_sync)
        {
            hasActiveLogout = _logoutLease is not null;
        }

        bool operationsCompatible = _operations.IsIdle
            || (pendingPlay && _operations.ActiveMaintenanceKind is null);
        bool canLogout = Volatile.Read(ref _disposeState) == 0
            && !_operations.IsShuttingDown
            && session.IsAuthenticated
            && !session.IsLoggingOut
            && !hasActiveLogout
            && operationsCompatible;
        string unavailableReason = canLogout
            ? string.Empty
            : session.IsLoggingOut || hasActiveLogout
                ? "Déconnexion en cours."
                : !session.IsAuthenticated
                    ? "Aucune session active."
                    : !operationsCompatible
                        ? "Une opération est en cours."
                        : "Déconnexion indisponible.";
        LauncherSessionFailureCategory failure = session.OperationKind
            == LauncherSessionOperationKind.Logout
                ? session.FailureCategory
                : LauncherSessionFailureCategory.None;
        return new ProfileRuntimeSnapshot(
            Sequence: ++_sequence,
            SessionSequence: session.Sequence,
            LogoutAttemptId: session.OperationKind == LauncherSessionOperationKind.Logout
                ? session.AttemptId
                : null,
            SessionState: session.State,
            Username: session.Username,
            IsEmailVerified: session.IsEmailVerified,
            CanLogout: canLogout,
            LogoutUnavailableReason: unavailableReason,
            FailureCategory: failure);
    }

    private ProfileLogoutStartResult Rejected(ProfileLogoutStartStatus status)
    {
        lock (_sync)
        {
            return RejectedUnsafe(status);
        }
    }

    private ProfileLogoutStartResult RejectedUnsafe(ProfileLogoutStartStatus status)
    {
        return new ProfileLogoutStartResult(status, null, null);
    }

    private static ProfileLogoutStartStatus MapOperationStatus(
        LauncherOperationStartStatus status)
    {
        return status switch
        {
            LauncherOperationStartStatus.ShuttingDown => ProfileLogoutStartStatus.ShuttingDown,
            LauncherOperationStartStatus.Busy => ProfileLogoutStartStatus.Busy,
            _ => ProfileLogoutStartStatus.RejectedByCompatibility
        };
    }

    private static ProfileLogoutStartStatus MapSessionStatus(
        LauncherSessionStartStatus status)
    {
        return status switch
        {
            LauncherSessionStartStatus.ShuttingDown => ProfileLogoutStartStatus.ShuttingDown,
            LauncherSessionStartStatus.NotAuthenticated => ProfileLogoutStartStatus.NotAuthenticated,
            _ => ProfileLogoutStartStatus.Busy
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _session.SnapshotChanged -= Session_SnapshotChanged;
        _operations.StateChanged -= Operations_StateChanged;
        _game.SnapshotChanged -= Game_SnapshotChanged;
        LauncherOperationLease? lease;
        lock (_sync)
        {
            lease = _logoutLease;
            SnapshotChanged = null;
        }

        lease?.CancelForShutdown();
    }
}
