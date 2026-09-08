namespace WotLK.Launcher.Runtime;

// FIFO disk work. Invalidation is ordered with reads even across session changes.
internal sealed class LauncherBackgroundWorkQueue
{
    private readonly object _sync = new();
    private Task _pending = Task.CompletedTask;

    internal Task Pending { get { lock (_sync) return _pending; } }

    internal Task<T> RunAsync<T>(Func<T> action, Task? barrier = null)
    {
        lock (_sync)
        {
            Task previous = _pending;
            Task<T> work = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                if (barrier is not null) await barrier.ConfigureAwait(false);
                return action();
            });
            _pending = ObserveAsync(work);
            return work;
        }
    }

    internal Task RunAsync(Action action, Task? barrier = null)
        => RunAsync(() => { action(); return true; }, barrier);

    private static async Task ObserveAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { } // Returned work reports errors; the queue remains usable for cleanup/retry.
    }
}
