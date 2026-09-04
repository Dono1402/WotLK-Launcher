namespace WotLK.Launcher.Game;

internal interface IGameProcessMonitor : IDisposable
{
    Task<bool> WaitForRunningAsync(string installRoot, CancellationToken cancellationToken);

    Task WaitForExitAsync(string installRoot, CancellationToken cancellationToken);

    Task StopAsync(string installRoot);
}

internal sealed class ProductionGameProcessMonitor : IGameProcessMonitor
{
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<string, bool> _isGameRunning;
    private readonly Action<string> _stopGame;
    private readonly TimeProvider _timeProvider;
    private int _disposeState;

    internal ProductionGameProcessMonitor(
        Func<string, bool>? isGameRunning = null,
        Action<string>? stopGame = null,
        TimeProvider? timeProvider = null)
    {
        _isGameRunning = isGameRunning ?? GameInstallServices.IsGameRunning;
        _stopGame = stopGame ?? GameInstallServices.StopRunningGameProcesses;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> WaitForRunningAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        long startedAt = _timeProvider.GetTimestamp();
        while (_timeProvider.GetElapsedTime(startedAt) < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isGameRunning(installRoot))
            {
                return true;
            }

            await Task.Delay(PollInterval, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        return _isGameRunning(installRoot);
    }

    public async Task WaitForExitAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        while (_isGameRunning(installRoot))
        {
            await Task.Delay(PollInterval, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task StopAsync(string installRoot)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return Task.Run(() => _stopGame(installRoot));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeState, 1);
    }
}
