namespace WotLK.Launcher.Runtime;

internal sealed class LauncherSingleInstanceGate : IDisposable
{
    private const string ProductionIdentity = "AtlasLauncher.Stable.4f4605a6";
    private const string LocalIdentity = "AtlasLauncher.Local.4f4605a6";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly RegisteredWaitHandle _activationRegistration;
    private int _disposeState;

    private LauncherSingleInstanceGate(Mutex mutex, EventWaitHandle activationEvent)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, _) => ((LauncherSingleInstanceGate)state!).OnActivationRequested(),
            this,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    internal event EventHandler? ActivationRequested;

    internal static string CurrentIdentity => LauncherBuildFlavor.IsLocalClient
        ? LocalIdentity
        : ProductionIdentity;

    internal static bool TryAcquire(
        string identity,
        out LauncherSingleInstanceGate? gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        Mutex mutex = new(initiallyOwned: true, MutexName(identity), out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            gate = null;
            return false;
        }

        try
        {
            EventWaitHandle activationEvent = new(
                initialState: false,
                EventResetMode.AutoReset,
                EventName(identity));
            gate = new LauncherSingleInstanceGate(mutex, activationEvent);
            return true;
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    internal static bool SignalExisting(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        try
        {
            using EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(
                EventName(identity));
            return activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _activationRegistration.Unregister(null);
        _activationEvent.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        _mutex.Dispose();
        ActivationRequested = null;
    }

    private void OnActivationRequested()
    {
        if (Volatile.Read(ref _disposeState) == 0)
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string MutexName(string identity) => $"Local\\{identity}.Mutex";

    private static string EventName(string identity) => $"Local\\{identity}.Activate";
}
