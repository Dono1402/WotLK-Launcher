using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class AuthCommands : IDisposable
{
    private readonly LauncherRuntime _runtime;
    private int _disposeState;

    internal AuthCommands(LauncherRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    internal LauncherSessionStartStatus TrySubmit(AuthSubmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return LauncherSessionStartStatus.ShuttingDown;
        }

        LauncherSessionStartResult start = request.Mode == AuthMode.Login
            ? _runtime.TryLogin(request.Username, request.Password)
            : _runtime.TryRegister(
                request.Username,
                request.Email,
                request.Password,
                request.PasswordConfirmation);
        if (start.IsStarted && start.Completion is not null)
        {
            _ = ObserveCompletionAsync(start.Completion);
        }

        return start.Status;
    }

    internal bool CancelCurrent()
    {
        return Volatile.Read(ref _disposeState) == 0
            && _runtime.CancelInteractiveAuthentication();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _runtime.CancelInteractiveAuthentication();
        }
    }

    private static async Task ObserveCompletionAsync(
        Task<LauncherSessionCompletion> completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // The coordinator converts failures; this guard prevents an unforeseen
            // presentation observer failure from becoming unobserved.
        }
    }
}
