using System.Runtime.InteropServices;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal interface ILauncherIdleTimeSource
{
    TimeSpan GetIdleTime();
}

internal sealed class WindowsLauncherIdleTimeSource : ILauncherIdleTimeSource
{
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { internal uint Size; internal uint Tick; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo input);
    public TimeSpan GetIdleTime()
    {
        LastInputInfo input = new() { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref input) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - input.Tick)) : TimeSpan.Zero;
    }
}

internal sealed record LauncherPresenceSnapshot(long Sequence, uint? OwnerAccountId, string Status, string ManualStatus,
    bool IsAutomaticAway, bool IsAvailable, bool IsUpdating, string? ErrorCode)
{
    internal bool DoNotDisturb => OwnerAccountId is not null && ManualStatus == "dnd";
}

internal sealed class LauncherPresenceSnapshotEventArgs(LauncherPresenceSnapshot snapshot) : EventArgs
{
    internal LauncherPresenceSnapshot Snapshot { get; } = snapshot;
}

internal sealed class LauncherPresenceCoordinator : IDisposable
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private readonly object _sync = new();
    private readonly LauncherSessionCoordinator _session;
    private readonly ILauncherAuthService _authentication;
    private readonly ILauncherPresenceApiClient _api;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<string> _writeLog;
    private readonly ILauncherIdleTimeSource _idleSource;
    private readonly ITimer _timer;
    private readonly CancellationTokenRegistration _registration;
    private CancellationTokenSource? _accountCancellation;
    private readonly HashSet<Task> _tasks = [];
    private LauncherPresenceSnapshot _snapshot = new(0, null, "offline", "online", false, false, false, null);
    private long _generation;
    private bool _started, _stopping, _busy;
    private int _disposed;

    internal LauncherPresenceCoordinator(LauncherSessionCoordinator session, ILauncherAuthService authentication,
        ILauncherPresenceApiClient api, CancellationToken lifetimeToken, Action<string> writeLog,
        TimeProvider? timeProvider = null, ILauncherIdleTimeSource? idleSource = null)
    {
        _session = session; _authentication = authentication; _api = api; _lifetimeToken = lifetimeToken;
        _writeLog = writeLog; _idleSource = idleSource ?? new WindowsLauncherIdleTimeSource();
        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(_ => { _ = RefreshAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _session.SnapshotChanged += SessionChanged;
        _registration = lifetimeToken.Register(BeginShutdown);
    }
    internal event EventHandler<LauncherPresenceSnapshotEventArgs>? SnapshotChanged;
    internal LauncherPresenceSnapshot CurrentSnapshot { get { lock (_sync) return _snapshot; } }

    internal void Start()
    {
        lock (_sync) { if (_started || _stopping) return; _started = true; }
        ResetAccount();
        _timer.Change(RefreshInterval, RefreshInterval);
    }
    private void SessionChanged(object? sender, AuthSessionSnapshotEventArgs args) => ResetAccount();
    private void ResetAccount()
    {
        CancellationTokenSource? previous;
        lock (_sync)
        {
            if (!_started || _stopping) return;
            uint? account = _session.CurrentSnapshot.IsAuthenticated ? _authentication.Session?.Profile.AccountId : null;
            if (account == _snapshot.OwnerAccountId && _accountCancellation is not null) return;
            previous = _accountCancellation;
            _accountCancellation = account is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            ++_generation; _busy = false;
            _snapshot = new(_snapshot.Sequence + 1, account, "offline", "online", false, false, false, null);
        }
        previous?.Cancel(); previous?.Dispose(); Raise(); _ = RefreshAsync();
    }
    internal Task<bool> SetStatusAsync(string status)
    {
        if (!LauncherPresenceStatus.IsValid(status)) throw new ArgumentException("Invalid presence status.", nameof(status));
        return StartRequest(status);
    }
    internal Task<bool> RefreshAsync() => StartRequest(null);
    private Task<bool> StartRequest(string? status)
    {
        lock (_sync)
        {
            if (!_started || _stopping || _busy || _snapshot.OwnerAccountId is null || _accountCancellation is null) return Task.FromResult(false);
            _busy = true;
            long generation = _generation;
            _snapshot = _snapshot with { Sequence = _snapshot.Sequence + 1, IsUpdating = true, ErrorCode = null };
            Task<bool> task = RunAsync(status, _snapshot.OwnerAccountId.Value, generation, _accountCancellation.Token);
            _tasks.Add(task);
            _ = task.ContinueWith(completed => { lock (_sync) _tasks.Remove(completed); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Raise(); return task;
        }
    }
    private async Task<bool> RunAsync(string? status, uint account, long generation, CancellationToken token)
    {
        // Yield so observers always see the pending state before a synchronous fake transport completes.
        await Task.Yield();
        bool succeeded = false;
        long sessionSequence = _session.CurrentSnapshot.Sequence;
        try
        {
            if (await _session.PrepareAuthenticatedRequestAsync(token).ConfigureAwait(false) != AtlasRequestPreparationStatus.Ready) return false;
            lock (_sync) if (_stopping || generation != _generation || _authentication.Session?.Profile.AccountId != account) return false;
            int idle = (int)Math.Clamp(_idleSource.GetIdleTime().TotalSeconds, 0, int.MaxValue);
            LauncherPresenceStateDto result = await _api.UpdateAsync(new(status, idle), token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_stopping || generation != _generation || result.AccountId != account || _authentication.Session?.Profile.AccountId != account) return false;
                _snapshot = new(_snapshot.Sequence + 1, account, result.Status, result.ManualStatus, result.IsAutomaticAway, true, false, null);
                succeeded = true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (LauncherPresenceApiException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            lock (_sync) if (!_stopping && generation == _generation)
                _session.NotifyAuthenticatedRequestUnauthorized(sessionSequence, token);
        }
        catch (Exception error)
        {
            lock (_sync) if (!_stopping && generation == _generation)
                _snapshot = _snapshot with { Sequence = _snapshot.Sequence + 1, IsAvailable = error is not LauncherPresenceApiException { Code: "presence-unavailable" } && _snapshot.IsAvailable,
                    ErrorCode = error is LauncherPresenceApiException exception ? exception.Code : "presence-request-failed" };
            _writeLog("Presence request failed: " + error.GetType().Name);
        }
        finally
        {
            lock (_sync) if (generation == _generation) { _busy = false; _snapshot = _snapshot with { Sequence = _snapshot.Sequence + 1, IsUpdating = false }; }
            Raise();
        }
        return succeeded;
    }
    private void Raise() => SnapshotChanged?.Invoke(this, new(CurrentSnapshot));
    internal void BeginShutdown()
    {
        CancellationTokenSource? cancellation;
        lock (_sync) { if (_stopping) return; _stopping = true; ++_generation; cancellation = _accountCancellation; }
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); cancellation?.Cancel();
    }
    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task[] tasks; lock (_sync) tasks = _tasks.ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false); return true; } catch (TimeoutException) { return false; }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        BeginShutdown(); _session.SnapshotChanged -= SessionChanged; _timer.Dispose(); _registration.Dispose(); _accountCancellation?.Dispose();
    }
}
