using WotLK.Launcher.Dashboard;

namespace WotLK.Launcher.Game;

// A missing first observation must still allow the normal authentication/ticket path.
// Once Offline is confirmed, a refresh/error cannot silently turn it into permission to play.
internal sealed class GameLaunchAvailability
{
    private readonly object _sync = new();
    private long _latestSequence = -1;
    private long _generation;
    private bool _isUnavailable;

    internal bool IsUnavailable
    {
        get { lock (_sync) return _isUnavailable; }
    }

    internal bool Update(DashboardSnapshot snapshot)
    {
        lock (_sync)
        {
            if (snapshot.Sequence <= _latestSequence) return false;
            _latestSequence = snapshot.Sequence;
            bool unavailable = snapshot.RealmState switch
            {
                DashboardRealmState.Offline => true,
                DashboardRealmState.Online or DashboardRealmState.Degraded => false,
                _ => _isUnavailable || snapshot.LastKnownRealmState == DashboardRealmState.Offline
            };
            if (unavailable == _isUnavailable) return false;
            _isUnavailable = unavailable;
            if (unavailable) _generation++;
            return true;
        }
    }

    internal GameLaunchPermit CreatePermit()
    {
        lock (_sync) return new GameLaunchPermit(this, _generation);
    }

    internal bool IsPermitAvailable(long generation)
    {
        lock (_sync) return !_isUnavailable && generation == _generation;
    }

    internal GameLaunchOutcome TryStartProcess(long generation, Func<bool> startProcess)
    {
        lock (_sync)
        {
            if (_isUnavailable || generation != _generation) return GameLaunchOutcome.ServerUnavailable;
            // Serialize the last availability check with the actual start. An offline
            // observation cannot slip between a successful check and Process.Start.
            return startProcess() ? GameLaunchOutcome.Started : GameLaunchOutcome.StartFailed;
        }
    }
}

internal sealed class GameLaunchPermit(GameLaunchAvailability availability, long generation)
{
    internal bool IsAvailable => availability.IsPermitAvailable(generation);

    internal GameLaunchOutcome TryStartProcess(Func<bool> startProcess) =>
        availability.TryStartProcess(generation, startProcess);
}
