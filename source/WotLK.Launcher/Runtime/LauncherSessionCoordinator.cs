using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

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
    private Task<LauncherSessionRestoreResult>? _restoreTask;

    internal LauncherSessionCoordinator(
        ILauncherAuthService authentication,
        CancellationToken lifetimeToken)
    {
        _authentication = authentication;
        _lifetimeToken = lifetimeToken;
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
            bool restored = await _authentication.RestoreAsync(_lifetimeToken);
            if (!restored)
            {
                return new LauncherSessionRestoreResult(
                    LauncherSessionRestoreStatus.NoSession,
                    null);
            }

            LauncherAuthSession? session = _authentication.Session;
            return session is null
                ? new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Unavailable, null)
                : new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Restored, session);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Cancelled, null);
        }
        catch (ObjectDisposedException) when (_lifetimeToken.IsCancellationRequested)
        {
            return new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Cancelled, null);
        }
        catch (OperationCanceledException)
        {
            return new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Unavailable, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            return new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Unavailable, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.Unavailable, null);
        }
    }
}
