using System.Net;
using System.Net.Http;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherSessionCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly ILauncherAuthService _authentication;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<string> _writeLog;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private readonly Dictionary<long, CancellationTokenSource> _attemptCancellations = [];
    private readonly HashSet<Task> _inFlightTasks = [];
    private Task<LauncherSessionRestoreResult>? _restoreTask;
    private AuthSessionSnapshot _currentSnapshot = AuthSessionSnapshot.Initial;
    private long _sequence;
    private long _nextAttemptId;
    private long? _activeAttemptId;
    private LauncherSessionOperationKind? _activeOperationKind;
    private bool _isShuttingDown;
    private int _disposeState;

    internal LauncherSessionCoordinator(
        ILauncherAuthService authentication,
        CancellationToken lifetimeToken,
        Action<string> writeLog)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _lifetimeToken = lifetimeToken;
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _lifetimeRegistration = lifetimeToken.Register(BeginShutdown);
    }

    internal event EventHandler<AuthSessionSnapshotEventArgs>? SnapshotChanged;

    internal AuthSessionSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    internal Task<LauncherSessionRestoreResult> RestoreOnceAsync()
    {
        TaskCompletionSource<LauncherSessionRestoreResult>? completion = null;
        AuthSessionSnapshot? snapshot = null;
        long attemptId = 0;
        CancellationTokenSource? cancellation = null;

        lock (_sync)
        {
            if (_restoreTask is not null)
            {
                return _restoreTask;
            }

            if (IsStoppingUnsafe())
            {
                return Task.FromResult(CancelledRestore());
            }

            if (_activeAttemptId is not null)
            {
                return Task.FromResult(new LauncherSessionRestoreResult(
                    LauncherSessionRestoreStatus.Unavailable,
                    null));
            }

            attemptId = ++_nextAttemptId;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            completion = new TaskCompletionSource<LauncherSessionRestoreResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _restoreTask = completion.Task;
            _inFlightTasks.Add(completion.Task);
            _attemptCancellations.Add(attemptId, cancellation);
            _activeAttemptId = attemptId;
            _activeOperationKind = LauncherSessionOperationKind.Restore;
            snapshot = SetSnapshotUnsafe(
                attemptId,
                LauncherSessionState.Restoring,
                LauncherSessionOperationKind.Restore,
                string.Empty,
                isEmailVerified: true,
                LauncherSessionFailureCategory.None);
        }

        RaiseSnapshotChanged(snapshot);
        _ = RunRestoreAsync(attemptId, cancellation, completion);
        return completion.Task;
    }

    internal LauncherSessionStartResult TryLogin(string username, string password)
    {
        LauncherAuthInputValidation validation = LauncherAuthenticationValidator.Login(
            username,
            !string.IsNullOrEmpty(password));
        if (!validation.IsValid)
        {
            return LauncherSessionStartResult.Rejected(
                LauncherSessionStartStatus.RejectedByValidation,
                CurrentSnapshot);
        }

        string normalizedUsername = username.Trim();
        return TryStartInteractive(
            LauncherSessionOperationKind.Login,
            normalizedUsername,
            cancellationToken => _authentication.PrepareLoginAsync(
                normalizedUsername,
                password,
                cancellationToken));
    }

    internal LauncherSessionStartResult TryRegister(
        string username,
        string email,
        string password,
        string passwordConfirmation)
    {
        LauncherAuthInputValidation validation = LauncherAuthenticationValidator.Register(
            username,
            email,
            password.Length,
            !string.IsNullOrEmpty(passwordConfirmation),
            string.Equals(password, passwordConfirmation, StringComparison.Ordinal));
        if (!validation.IsValid)
        {
            return LauncherSessionStartResult.Rejected(
                LauncherSessionStartStatus.RejectedByValidation,
                CurrentSnapshot);
        }

        string normalizedUsername = username.Trim();
        string normalizedEmail = email.Trim();
        return TryStartInteractive(
            LauncherSessionOperationKind.Register,
            normalizedUsername,
            cancellationToken => _authentication.PrepareRegistrationAsync(
                normalizedUsername,
                normalizedEmail,
                password,
                cancellationToken));
    }

    internal bool CancelInteractiveAttempt()
    {
        CancellationTokenSource? cancellation = null;
        AuthSessionSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_activeAttemptId is not long attemptId
                || _activeOperationKind is not (
                    LauncherSessionOperationKind.Login
                    or LauncherSessionOperationKind.Register))
            {
                return false;
            }

            _attemptCancellations.TryGetValue(attemptId, out cancellation);
            _activeAttemptId = null;
            _activeOperationKind = null;
            snapshot = SetSnapshotUnsafe(
                attemptId,
                LauncherSessionState.SignedOut,
                null,
                _currentSnapshot.Username,
                isEmailVerified: true,
                LauncherSessionFailureCategory.None);
        }

        TryCancel(cancellation);
        RaiseSnapshotChanged(snapshot);
        return true;
    }

    internal void BeginShutdown()
    {
        CancellationTokenSource[] cancellations;
        lock (_sync)
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            _activeAttemptId = null;
            _activeOperationKind = null;
            cancellations = _attemptCancellations.Values.ToArray();
        }

        foreach (CancellationTokenSource cancellation in cancellations)
        {
            TryCancel(cancellation);
        }
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_sync)
        {
            tasks = _inFlightTasks.ToArray();
        }

        if (tasks.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
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

        BeginShutdown();
        _lifetimeRegistration.Dispose();
        lock (_sync)
        {
            SnapshotChanged = null;
        }
    }

    private LauncherSessionStartResult TryStartInteractive(
        LauncherSessionOperationKind operationKind,
        string username,
        Func<CancellationToken, Task<LauncherAuthSession>> execute)
    {
        TaskCompletionSource<LauncherSessionCompletion> completion;
        CancellationTokenSource cancellation;
        AuthSessionSnapshot snapshot;
        long attemptId;

        lock (_sync)
        {
            if (IsStoppingUnsafe())
            {
                return LauncherSessionStartResult.Rejected(
                    LauncherSessionStartStatus.ShuttingDown,
                    _currentSnapshot);
            }

            if (_activeAttemptId is not null)
            {
                return LauncherSessionStartResult.Rejected(
                    LauncherSessionStartStatus.Busy,
                    _currentSnapshot);
            }

            if (_currentSnapshot.IsAuthenticated)
            {
                return LauncherSessionStartResult.Rejected(
                    LauncherSessionStartStatus.AlreadyAuthenticated,
                    _currentSnapshot);
            }

            attemptId = ++_nextAttemptId;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            completion = new TaskCompletionSource<LauncherSessionCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightTasks.Add(completion.Task);
            _attemptCancellations.Add(attemptId, cancellation);
            _activeAttemptId = attemptId;
            _activeOperationKind = operationKind;
            snapshot = SetSnapshotUnsafe(
                attemptId,
                operationKind == LauncherSessionOperationKind.Login
                    ? LauncherSessionState.Authenticating
                    : LauncherSessionState.Registering,
                operationKind,
                username,
                isEmailVerified: true,
                LauncherSessionFailureCategory.None);
        }

        RaiseSnapshotChanged(snapshot);
        _ = RunInteractiveAsync(
            attemptId,
            operationKind,
            username,
            execute,
            cancellation,
            completion);
        return new LauncherSessionStartResult(
            LauncherSessionStartStatus.Started,
            attemptId,
            completion.Task);
    }

    private async Task RunRestoreAsync(
        long attemptId,
        CancellationTokenSource cancellation,
        TaskCompletionSource<LauncherSessionRestoreResult> completion)
    {
        LauncherSessionRestoreResult result;
        try
        {
            LauncherAuthRestoreAttempt attempt = await _authentication
                .PrepareRestoreAsync(cancellation.Token)
                .ConfigureAwait(false);
            if (IsCancelledOrSuperseded(attemptId, cancellation.Token))
            {
                result = CancelledRestore();
            }
            else
            {
                result = attempt.Outcome switch
                {
                    LauncherAuthRestoreOutcome.Restored when attempt.Session is not null =>
                        CompleteRestoreSuccess(attemptId, attempt.Session),
                    LauncherAuthRestoreOutcome.Rejected => CompleteRestoreWithoutSession(
                        attemptId,
                        LauncherSessionRestoreStatus.Rejected,
                        LauncherSessionFailureCategory.SessionExpired),
                    _ => CompleteRestoreWithoutSession(
                        attemptId,
                        LauncherSessionRestoreStatus.NoSession,
                        LauncherSessionFailureCategory.NoStoredSession)
                };
            }
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested || _lifetimeToken.IsCancellationRequested)
        {
            result = CancelledRestore();
        }
        catch (Exception exception)
        {
            LauncherSessionFailureCategory category = ClassifyFailure(
                exception,
                LauncherSessionOperationKind.Restore);
            WriteFailureSafely(LauncherSessionOperationKind.Restore, category, exception);
            result = category == LauncherSessionFailureCategory.Unauthorized
                ? CompleteRestoreWithoutSession(
                    attemptId,
                    LauncherSessionRestoreStatus.Rejected,
                    LauncherSessionFailureCategory.SessionExpired)
                : CompleteRestoreFailure(attemptId, category);
        }
        completion.TrySetResult(result);
        FinishAttempt(attemptId, cancellation, completion.Task);
    }

    private async Task RunInteractiveAsync(
        long attemptId,
        LauncherSessionOperationKind operationKind,
        string username,
        Func<CancellationToken, Task<LauncherAuthSession>> execute,
        CancellationTokenSource cancellation,
        TaskCompletionSource<LauncherSessionCompletion> completion)
    {
        LauncherSessionCompletion result;
        try
        {
            LauncherAuthSession session = await execute(cancellation.Token).ConfigureAwait(false);
            if (IsCancelledOrSuperseded(attemptId, cancellation.Token))
            {
                result = new LauncherSessionCompletion(
                    LauncherSessionCompletionStatus.Superseded,
                    CurrentSnapshot);
            }
            else if (session is null)
            {
                LauncherSessionFailureCategory category = operationKind
                    == LauncherSessionOperationKind.Register
                        ? LauncherSessionFailureCategory.AccountCreatedSignInRequired
                        : LauncherSessionFailureCategory.Unknown;
                result = CompleteInteractiveFailure(attemptId, operationKind, username, category);
            }
            else
            {
                result = CompleteInteractiveSuccess(attemptId, session);
            }
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested || _lifetimeToken.IsCancellationRequested)
        {
            result = new LauncherSessionCompletion(
                IsCurrentAttempt(attemptId)
                    ? LauncherSessionCompletionStatus.Cancelled
                    : LauncherSessionCompletionStatus.Superseded,
                CurrentSnapshot);
        }
        catch (Exception exception)
        {
            LauncherSessionFailureCategory category = ClassifyFailure(exception, operationKind);
            WriteFailureSafely(operationKind, category, exception);
            result = CompleteInteractiveFailure(
                attemptId,
                operationKind,
                username,
                category);
        }
        completion.TrySetResult(result);
        FinishAttempt(attemptId, cancellation, completion.Task);
    }

    private LauncherSessionRestoreResult CompleteRestoreSuccess(
        long attemptId,
        LauncherAuthSession session)
    {
        AuthSessionSnapshot? snapshot = null;
        try
        {
            lock (_sync)
            {
                if (!IsCurrentAttemptUnsafe(attemptId))
                {
                    return CancelledRestore();
                }

                _authentication.CommitSession(session, clearGameSingleSignOn: false);
                snapshot = SetSnapshotUnsafe(
                    attemptId,
                    LauncherSessionState.Authenticated,
                    LauncherSessionOperationKind.Restore,
                    session.Profile.Username,
                    session.Profile.EmailVerified,
                    LauncherSessionFailureCategory.None);
                ClearActiveAttemptUnsafe(attemptId);
            }
        }
        catch (Exception exception)
        {
            LauncherSessionFailureCategory category = ClassifyFailure(
                exception,
                LauncherSessionOperationKind.Restore);
            WriteFailureSafely(LauncherSessionOperationKind.Restore, category, exception);
            return CompleteRestoreFailure(attemptId, category);
        }

        RaiseSnapshotChanged(snapshot);
        return new LauncherSessionRestoreResult(
            LauncherSessionRestoreStatus.Restored,
            session);
    }

    private LauncherSessionRestoreResult CompleteRestoreWithoutSession(
        long attemptId,
        LauncherSessionRestoreStatus status,
        LauncherSessionFailureCategory category)
    {
        AuthSessionSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentAttemptUnsafe(attemptId))
            {
                return CancelledRestore();
            }

            snapshot = SetSnapshotUnsafe(
                attemptId,
                LauncherSessionState.SignedOut,
                LauncherSessionOperationKind.Restore,
                string.Empty,
                isEmailVerified: true,
                category);
            ClearActiveAttemptUnsafe(attemptId);
        }

        RaiseSnapshotChanged(snapshot);
        return new LauncherSessionRestoreResult(status, null);
    }

    private LauncherSessionRestoreResult CompleteRestoreFailure(
        long attemptId,
        LauncherSessionFailureCategory category)
    {
        AuthSessionSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentAttemptUnsafe(attemptId))
            {
                return CancelledRestore();
            }

            snapshot = SetSnapshotUnsafe(
                attemptId,
                LauncherSessionState.Unavailable,
                LauncherSessionOperationKind.Restore,
                string.Empty,
                isEmailVerified: true,
                category);
            ClearActiveAttemptUnsafe(attemptId);
        }

        RaiseSnapshotChanged(snapshot);
        return new LauncherSessionRestoreResult(
            LauncherSessionRestoreStatus.Unavailable,
            null);
    }

    private LauncherSessionCompletion CompleteInteractiveSuccess(
        long attemptId,
        LauncherAuthSession session)
    {
        AuthSessionSnapshot? snapshot = null;
        LauncherSessionOperationKind operationKind;
        try
        {
            lock (_sync)
            {
                if (!IsCurrentAttemptUnsafe(attemptId))
                {
                    return new LauncherSessionCompletion(
                        LauncherSessionCompletionStatus.Superseded,
                        _currentSnapshot);
                }

                operationKind = _activeOperationKind ?? LauncherSessionOperationKind.Login;
                _authentication.CommitSession(session, clearGameSingleSignOn: true);
                snapshot = SetSnapshotUnsafe(
                    attemptId,
                    LauncherSessionState.Authenticated,
                    operationKind,
                    session.Profile.Username,
                    session.Profile.EmailVerified,
                    LauncherSessionFailureCategory.None);
                ClearActiveAttemptUnsafe(attemptId);
            }
        }
        catch (Exception exception)
        {
            operationKind = _activeOperationKind ?? LauncherSessionOperationKind.Login;
            LauncherSessionFailureCategory category = ClassifyFailure(exception, operationKind);
            WriteFailureSafely(operationKind, category, exception);
            return CompleteInteractiveFailure(
                attemptId,
                operationKind,
                session.Profile.Username,
                category);
        }

        RaiseSnapshotChanged(snapshot);
        return new LauncherSessionCompletion(
            LauncherSessionCompletionStatus.Succeeded,
            snapshot);
    }

    private LauncherSessionCompletion CompleteInteractiveFailure(
        long attemptId,
        LauncherSessionOperationKind operationKind,
        string username,
        LauncherSessionFailureCategory category)
    {
        AuthSessionSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentAttemptUnsafe(attemptId))
            {
                return new LauncherSessionCompletion(
                    LauncherSessionCompletionStatus.Superseded,
                    _currentSnapshot);
            }

            snapshot = SetSnapshotUnsafe(
                attemptId,
                category is LauncherSessionFailureCategory.Network
                    or LauncherSessionFailureCategory.Timeout
                    or LauncherSessionFailureCategory.ServiceUnavailable
                    or LauncherSessionFailureCategory.Unknown
                        ? LauncherSessionState.Unavailable
                        : LauncherSessionState.SignedOut,
                operationKind,
                username,
                isEmailVerified: true,
                category);
            ClearActiveAttemptUnsafe(attemptId);
        }

        RaiseSnapshotChanged(snapshot);
        return new LauncherSessionCompletion(
            LauncherSessionCompletionStatus.Failed,
            snapshot);
    }

    private void FinishAttempt(
        long attemptId,
        CancellationTokenSource cancellation,
        Task completionTask)
    {
        lock (_sync)
        {
            _attemptCancellations.Remove(attemptId);
            _inFlightTasks.Remove(completionTask);
            ClearActiveAttemptUnsafe(attemptId);
        }

        cancellation.Dispose();
    }

    private AuthSessionSnapshot SetSnapshotUnsafe(
        long? attemptId,
        LauncherSessionState state,
        LauncherSessionOperationKind? operationKind,
        string username,
        bool isEmailVerified,
        LauncherSessionFailureCategory failureCategory)
    {
        _currentSnapshot = new AuthSessionSnapshot(
            Sequence: ++_sequence,
            AttemptId: attemptId,
            State: state,
            OperationKind: operationKind,
            Username: username,
            IsEmailVerified: isEmailVerified,
            FailureCategory: failureCategory);
        return _currentSnapshot;
    }

    private bool IsCancelledOrSuperseded(long attemptId, CancellationToken token)
    {
        return token.IsCancellationRequested
            || _lifetimeToken.IsCancellationRequested
            || !IsCurrentAttempt(attemptId);
    }

    private bool IsCurrentAttempt(long attemptId)
    {
        lock (_sync)
        {
            return IsCurrentAttemptUnsafe(attemptId);
        }
    }

    private bool IsCurrentAttemptUnsafe(long attemptId)
    {
        return !IsStoppingUnsafe() && _activeAttemptId == attemptId;
    }

    private bool IsStoppingUnsafe()
    {
        return _isShuttingDown
            || Volatile.Read(ref _disposeState) != 0
            || _lifetimeToken.IsCancellationRequested;
    }

    private void ClearActiveAttemptUnsafe(long attemptId)
    {
        if (_activeAttemptId != attemptId)
        {
            return;
        }

        _activeAttemptId = null;
        _activeOperationKind = null;
    }

    private void RaiseSnapshotChanged(AuthSessionSnapshot? snapshot)
    {
        if (snapshot is not null && Volatile.Read(ref _disposeState) == 0)
        {
            SnapshotChanged?.Invoke(this, new AuthSessionSnapshotEventArgs(snapshot));
        }
    }

    private void WriteFailureSafely(
        LauncherSessionOperationKind operationKind,
        LauncherSessionFailureCategory category,
        Exception exception)
    {
        try
        {
            _writeLog(
                $"Authentification V2 indisponible: operation={operationKind}; "
                + $"category={category}; exception={exception.GetType().Name}.");
        }
        catch
        {
            // A logging failure must not fault an observed authentication task.
        }
    }

    internal static LauncherSessionFailureCategory ClassifyFailure(
        Exception exception,
        LauncherSessionOperationKind operationKind)
    {
        if (exception is TaskCanceledException or TimeoutException)
        {
            return LauncherSessionFailureCategory.Timeout;
        }

        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.GatewayTimeout
                    ? LauncherSessionFailureCategory.Timeout
                    : httpException.StatusCode is >= HttpStatusCode.InternalServerError
                        ? LauncherSessionFailureCategory.ServiceUnavailable
                        : LauncherSessionFailureCategory.Network;
        }

        if (exception is LauncherAuthException authException)
        {
            if (authException.StatusCode == HttpStatusCode.Unauthorized)
            {
                return operationKind == LauncherSessionOperationKind.Login
                    ? LauncherSessionFailureCategory.InvalidCredentials
                    : LauncherSessionFailureCategory.Unauthorized;
            }

            if (authException.StatusCode == HttpStatusCode.Conflict)
            {
                bool mentionsEmail = authException.Message.Contains(
                    "mail",
                    StringComparison.OrdinalIgnoreCase);
                bool mentionsUsername = authException.Message.Contains(
                        "utilisateur",
                        StringComparison.OrdinalIgnoreCase)
                    || authException.Message.Contains(
                        "nom",
                        StringComparison.OrdinalIgnoreCase);
                if (mentionsEmail && !mentionsUsername)
                {
                    return LauncherSessionFailureCategory.EmailAlreadyExists;
                }

                return mentionsUsername && !mentionsEmail
                    ? LauncherSessionFailureCategory.UsernameAlreadyExists
                    : LauncherSessionFailureCategory.Validation;
            }

            if (authException.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.GatewayTimeout)
            {
                return LauncherSessionFailureCategory.Timeout;
            }

            return authException.StatusCode is >= HttpStatusCode.InternalServerError
                || authException.StatusCode == HttpStatusCode.TooManyRequests
                    ? LauncherSessionFailureCategory.ServiceUnavailable
                    : LauncherSessionFailureCategory.Unknown;
        }

        return LauncherSessionFailureCategory.Unknown;
    }

    private static LauncherSessionRestoreResult CancelledRestore()
    {
        return new LauncherSessionRestoreResult(
            LauncherSessionRestoreStatus.Cancelled,
            null);
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
