namespace WotLK.Launcher.Runtime;

internal enum LauncherSessionRestoreStatus
{
    Restored,
    NoSession,
    Unavailable,
    Cancelled
}

internal sealed record LauncherSessionRestoreResult(
    LauncherSessionRestoreStatus Status,
    LauncherAuthSession? Session);

internal sealed class LauncherSessionCoordinator
{
    private readonly object _sync = new();
    private readonly ILauncherAuthService _authentication;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<string> _writeLog;
    private Task<LauncherSessionRestoreResult>? _restoreTask;

    internal LauncherSessionCoordinator(
        ILauncherAuthService authentication,
        CancellationToken lifetimeToken,
        Action<string> writeLog)
    {
        _authentication = authentication;
        _lifetimeToken = lifetimeToken;
        _writeLog = writeLog;
    }

    internal Task<LauncherSessionRestoreResult> RestoreOnceAsync()
    {
        lock (_sync)
        {
            return _restoreTask ??= RestoreCoreAsync();
        }
    }

    private async Task<LauncherSessionRestoreResult> RestoreCoreAsync()
    {
        try
        {
            bool restored = await _authentication
                .RestoreAsync(_lifetimeToken)
                .ConfigureAwait(false);
            if (_lifetimeToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            if (!restored)
            {
                return new LauncherSessionRestoreResult(
                    LauncherSessionRestoreStatus.NoSession,
                    null);
            }

            LauncherAuthSession? session = _authentication.Session;
            if (_lifetimeToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            return session is null
                ? new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Unavailable, null)
                : new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Restored, session);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception ex)
        {
            WriteFailureSafely(ex);
            return _lifetimeToken.IsCancellationRequested
                ? Cancelled()
                : new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Unavailable, null);
        }
    }

    private static LauncherSessionRestoreResult Cancelled()
    {
        return new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Cancelled, null);
    }

    private void WriteFailureSafely(Exception exception)
    {
        try
        {
            _writeLog($"Restauration de session V2 indisponible ({exception.GetType().Name}).");
        }
        catch
        {
            // A logging failure must not fault the observed restoration task.
        }
    }
}
