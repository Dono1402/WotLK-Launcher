using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using WotLK.Launcher.Account;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherAccountCoordinator : IDisposable
{
    private static readonly TimeSpan ProgressPublicationInterval = TimeSpan.FromMilliseconds(85);
    private readonly object _sync = new();
    private readonly LauncherSessionCoordinator _session;
    private readonly ILauncherAuthService _authentication;
    private readonly LauncherOperationCoordinator _operations;
    private readonly IAvatarMediaClient _mediaClient;
    private readonly AvatarImageCache _imageCache;
    private readonly Func<LauncherProfile?> _getCurrentProfile;
    private readonly Action<string> _writeLog;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _refreshCancellation;
    private Task _activeRefreshTask = Task.CompletedTask;
    private LauncherOperationLease? _mutationLease;
    private Task _activeMutationTask = Task.CompletedTask;
    private AccountRuntimeSnapshot _currentSnapshot;
    private DateTimeOffset _lastProgressPublication;
    private long _sessionGeneration;
    private long _sequence;
    private int _disposeState;

    internal LauncherAccountCoordinator(
        LauncherSessionCoordinator session,
        ILauncherAuthService authentication,
        LauncherOperationCoordinator operations,
        IAvatarMediaClient mediaClient,
        AvatarImageCache imageCache,
        Func<LauncherProfile?> getCurrentProfile,
        Action<string> writeLog,
        TimeProvider? timeProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _mediaClient = mediaClient ?? throw new ArgumentNullException(nameof(mediaClient));
        _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
        _getCurrentProfile = getCurrentProfile ?? throw new ArgumentNullException(nameof(getCurrentProfile));
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentSnapshot = CreateSessionSnapshot(_session.CurrentSnapshot, _getCurrentProfile());
        _session.SnapshotChanged += Session_SnapshotChanged;
    }

    internal event EventHandler<AccountRuntimeSnapshotEventArgs>? SnapshotChanged;

    internal AccountRuntimeSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    internal AccountActionStartResult TryRefresh()
    {
        AccountRuntimeSnapshot loading;
        TaskCompletionSource startGate;
        Task<AccountActionCompletion> completion;
        long sessionGeneration;
        lock (_sync)
        {
            if (IsStoppingUnsafe())
            {
                return AccountActionStartResult.Rejected(AccountActionStartStatus.ShuttingDown);
            }
            if (!_session.CurrentSnapshot.IsAuthenticated)
            {
                return AccountActionStartResult.Rejected(AccountActionStartStatus.NotAuthenticated);
            }
            if (!_activeRefreshTask.IsCompleted || _mutationLease is not null)
            {
                return AccountActionStartResult.Rejected(AccountActionStartStatus.Busy);
            }

            CancellationTokenSource refreshCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                _operations.ShutdownToken);
            _refreshCancellation = refreshCancellation;
            sessionGeneration = _sessionGeneration;
            loading = SetSnapshotUnsafe(
                _currentSnapshot with
                {
                    LoadingState = AccountLoadingState.Loading,
                    SessionsState = AccountSessionsState.Loading,
                    ErrorCategory = AccountAvatarErrorCategory.None,
                    AccountError = AccountRuntimeError.None,
                    Notice = AccountNoticeKind.None
                });
            startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = RunAfterGateAsync(
                startGate.Task,
                () => RefreshCoreAsync(refreshCancellation, sessionGeneration));
            _activeRefreshTask = completion;
        }

        RaiseSnapshotChanged(loading);
        startGate.TrySetResult();
        return new AccountActionStartResult(
            AccountActionStartStatus.Started,
            null,
            completion);
    }

    internal AccountActionStartResult TryUpload(AvatarUploadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Crop.IsValid || request.OriginalBytes.IsEmpty)
        {
            return AccountActionStartResult.Rejected(AccountActionStartStatus.InvalidRequest);
        }

        lock (_sync)
        {
            AccountActionStartResult? rejection = GetMutationRejectionUnsafe();
            if (rejection is not null)
            {
                return rejection;
            }
            if (_currentSnapshot.AvatarAvailability != AvatarBackendAvailability.Available)
            {
                return AccountActionStartResult.Rejected(AccountActionStartStatus.BackendUnavailable);
            }
        }

        LauncherOperationStartResult operation = _operations.TryBegin(
            LauncherOperationKind.AvatarUpload,
            canUserCancel: true,
            clientIsPlayable: false);
        if (!operation.IsStarted || operation.Lease is null)
        {
            return AccountActionStartResult.Rejected(MapOperationStatus(operation.Status));
        }

        LauncherOperationLease lease = operation.Lease;
        AccountRuntimeSnapshot snapshot;
        TaskCompletionSource startGate;
        Task<AccountActionCompletion> completion;
        lock (_sync)
        {
            if (IsStoppingUnsafe() || _mutationLease is not null)
            {
                lease.CancelForShutdown();
                lease.Dispose();
                return AccountActionStartResult.Rejected(AccountActionStartStatus.ShuttingDown);
            }

            _mutationLease = lease;
            _lastProgressPublication = DateTimeOffset.MinValue;
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                OperationId = lease.OperationId,
                AvatarOperation = AccountAvatarOperationState.Preparing,
                UploadPercentage = 0,
                ErrorCategory = AccountAvatarErrorCategory.None,
                AccountError = AccountRuntimeError.None,
                Notice = AccountNoticeKind.None
            });
            startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = RunAfterGateAsync(
                startGate.Task,
                () => RunUploadAsync(lease, request));
            _activeMutationTask = completion;
        }

        RaiseSnapshotChanged(snapshot);
        startGate.TrySetResult();
        return new AccountActionStartResult(
            AccountActionStartStatus.Started,
            lease.OperationId,
            completion);
    }

    internal AccountActionStartResult TryDelete()
    {
        lock (_sync)
        {
            AccountActionStartResult? rejection = GetMutationRejectionUnsafe();
            if (rejection is not null)
            {
                return rejection;
            }
            if (_currentSnapshot.AvatarAvailability != AvatarBackendAvailability.Available)
            {
                return AccountActionStartResult.Rejected(AccountActionStartStatus.BackendUnavailable);
            }
        }

        LauncherOperationStartResult operation = _operations.TryBegin(
            LauncherOperationKind.AvatarDelete,
            canUserCancel: false,
            clientIsPlayable: false);
        if (!operation.IsStarted || operation.Lease is null)
        {
            return AccountActionStartResult.Rejected(MapOperationStatus(operation.Status));
        }

        LauncherOperationLease lease = operation.Lease;
        AccountRuntimeSnapshot snapshot;
        TaskCompletionSource startGate;
        Task<AccountActionCompletion> completion;
        lock (_sync)
        {
            if (IsStoppingUnsafe() || _mutationLease is not null)
            {
                lease.CancelForShutdown();
                lease.Dispose();
                return AccountActionStartResult.Rejected(AccountActionStartStatus.ShuttingDown);
            }

            _mutationLease = lease;
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                OperationId = lease.OperationId,
                AvatarOperation = AccountAvatarOperationState.Removing,
                UploadPercentage = null,
                ErrorCategory = AccountAvatarErrorCategory.None,
                AccountError = AccountRuntimeError.None,
                Notice = AccountNoticeKind.None
            });
            startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = RunAfterGateAsync(
                startGate.Task,
                () => RunDeleteAsync(lease));
            _activeMutationTask = completion;
        }

        RaiseSnapshotChanged(snapshot);
        startGate.TrySetResult();
        return new AccountActionStartResult(
            AccountActionStartStatus.Started,
            lease.OperationId,
            completion);
    }

    internal AccountActionStartResult TryChangeEmail(string email)
    {
        string normalizedEmail = email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return RejectAccountValidation(
                AccountOperationState.ChangingEmail,
                AccountErrorCategory.InvalidEmail);
        }

        return TryStartAccountMutation(
            LauncherOperationKind.AccountEmailChange,
            AccountOperationState.ChangingEmail,
            targetSessionId: null,
            lease => RunChangeEmailAsync(lease, normalizedEmail));
    }

    internal AccountActionStartResult TryResendVerification()
    {
        lock (_sync)
        {
            if (_currentSnapshot.EmailVerified)
            {
                return AccountActionStartResult.Rejected(AccountActionStartStatus.InvalidRequest);
            }
        }

        return TryStartAccountMutation(
            LauncherOperationKind.AccountEmailVerification,
            AccountOperationState.ResendingVerification,
            targetSessionId: null,
            RunResendVerificationAsync);
    }

    internal AccountActionStartResult TryChangePassword(
        string currentPassword,
        string newPassword)
    {
        if (string.IsNullOrEmpty(currentPassword)
            || newPassword.Length is < 10 or > 128)
        {
            return RejectAccountValidation(
                AccountOperationState.ChangingPassword,
                AccountErrorCategory.InvalidPassword);
        }

        return TryStartAccountMutation(
            LauncherOperationKind.AccountPasswordChange,
            AccountOperationState.ChangingPassword,
            targetSessionId: null,
            lease => RunChangePasswordAsync(lease, currentPassword, newPassword));
    }

    internal AccountActionStartResult TryRevokeSession(string sessionId)
    {
        string normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        lock (_sync)
        {
            AccountDeviceSessionSnapshot? session = _currentSnapshot.Sessions
                .FirstOrDefault(item => string.Equals(
                    item.Id,
                    normalizedSessionId,
                    StringComparison.OrdinalIgnoreCase));
            if (session is null || session.IsCurrent)
            {
                return AccountActionStartResult.Rejected(AccountActionStartStatus.InvalidRequest);
            }
        }

        return TryStartAccountMutation(
            LauncherOperationKind.AccountSessionRevoke,
            AccountOperationState.RevokingSession,
            normalizedSessionId,
            lease => RunRevokeSessionAsync(lease, normalizedSessionId));
    }

    internal bool CancelUploadFromUser()
    {
        lock (_sync)
        {
            return _mutationLease is { Kind: LauncherOperationKind.AvatarUpload } lease
                && lease.CancelFromUser();
        }
    }

    private AccountActionStartResult TryStartAccountMutation(
        LauncherOperationKind kind,
        AccountOperationState operationState,
        string? targetSessionId,
        Func<LauncherOperationLease, Task<AccountActionCompletion>> action)
    {
        lock (_sync)
        {
            AccountActionStartResult? rejection = GetMutationRejectionUnsafe();
            if (rejection is not null)
            {
                return rejection;
            }
        }

        LauncherOperationStartResult operation = _operations.TryBegin(
            kind,
            canUserCancel: false,
            clientIsPlayable: false);
        if (!operation.IsStarted || operation.Lease is null)
        {
            return AccountActionStartResult.Rejected(MapOperationStatus(operation.Status));
        }

        LauncherOperationLease lease = operation.Lease;
        AccountRuntimeSnapshot snapshot;
        TaskCompletionSource startGate;
        Task<AccountActionCompletion> completion;
        lock (_sync)
        {
            if (IsStoppingUnsafe() || _mutationLease is not null)
            {
                lease.CancelForShutdown();
                lease.Dispose();
                return AccountActionStartResult.Rejected(AccountActionStartStatus.ShuttingDown);
            }

            _mutationLease = lease;
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                OperationId = lease.OperationId,
                AccountOperation = operationState,
                TargetSessionId = targetSessionId,
                AccountError = AccountRuntimeError.None,
                Notice = AccountNoticeKind.None
            });
            startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = RunAfterGateAsync(startGate.Task, () => action(lease));
            _activeMutationTask = completion;
        }

        RaiseSnapshotChanged(snapshot);
        startGate.TrySetResult();
        return new AccountActionStartResult(
            AccountActionStartStatus.Started,
            lease.OperationId,
            completion);
    }

    private AccountActionStartResult RejectAccountValidation(
        AccountOperationState operation,
        AccountErrorCategory error)
    {
        AccountRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsStoppingUnsafe() && _session.CurrentSnapshot.IsAuthenticated)
            {
                snapshot = SetSnapshotUnsafe(_currentSnapshot with
                {
                    AccountError = new AccountRuntimeError(operation, error),
                    Notice = AccountNoticeKind.None
                });
            }
        }
        RaiseSnapshotChanged(snapshot);
        return AccountActionStartResult.Rejected(AccountActionStartStatus.InvalidRequest);
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task[] active;
        lock (_sync)
        {
            active = [_activeRefreshTask, _activeMutationTask];
        }

        try
        {
            await Task.WhenAll(active).WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    internal void BeginShutdown()
    {
        CancellationTokenSource? refresh;
        LauncherOperationLease? lease;
        lock (_sync)
        {
            refresh = _refreshCancellation;
            lease = _mutationLease;
        }

        TryCancel(refresh);
        lease?.CancelForShutdown();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _session.SnapshotChanged -= Session_SnapshotChanged;
        BeginShutdown();
        lock (_sync)
        {
            SnapshotChanged = null;
        }
    }

    private async Task<AccountActionCompletion> RefreshCoreAsync(
        CancellationTokenSource cancellation,
        long sessionGeneration)
    {
        try
        {
            return await RefreshProfileAsync(
                    cancellation.Token,
                    fromCancellation: false,
                    sessionGeneration)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_refreshCancellation, cancellation))
                {
                    _refreshCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task<AccountActionCompletion> RefreshProfileAsync(
        CancellationToken cancellationToken,
        bool fromCancellation,
        long sessionGeneration)
    {
        AtlasRequestPreparationStatus preparation = await _session
            .PrepareAuthenticatedRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        if (preparation != AtlasRequestPreparationStatus.Ready)
        {
            if (!IsRefreshSessionCurrent(sessionGeneration))
            {
                return CancelledRefresh();
            }
            AccountAvatarErrorCategory error = preparation switch
            {
                AtlasRequestPreparationStatus.AuthenticationRequired =>
                    AccountAvatarErrorCategory.Unauthorized,
                AtlasRequestPreparationStatus.Unavailable =>
                    fromCancellation
                        ? AccountAvatarErrorCategory.CancellationAmbiguous
                        : AccountAvatarErrorCategory.Network,
                _ => AccountAvatarErrorCategory.None
            };
            AccountRuntimeSnapshot rejected = PublishStable(error);
            return new AccountActionCompletion(
                preparation is AtlasRequestPreparationStatus.Cancelled
                    or AtlasRequestPreparationStatus.ShuttingDown
                        ? AccountActionCompletionStatus.Cancelled
                        : AccountActionCompletionStatus.Failed,
                rejected);
        }

        if (!IsRefreshSessionCurrent(sessionGeneration))
        {
            return CancelledRefresh();
        }

        try
        {
            AvatarProfileReadResult result = await _mediaClient
                .GetProfileAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!IsRefreshSessionCurrent(sessionGeneration))
            {
                return CancelledRefresh();
            }
            ImmutableArray<AccountDeviceSessionSnapshot> sessions = [];
            AccountSessionsState sessionsState = AccountSessionsState.Loaded;
            AccountRuntimeError accountError = AccountRuntimeError.None;
            try
            {
                IReadOnlyList<LauncherDeviceSession> response = await _authentication
                    .GetSessionsAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!IsRefreshSessionCurrent(sessionGeneration))
                {
                    return CancelledRefresh();
                }
                sessions = response.Select(ToSessionSnapshot).ToImmutableArray();
            }
            catch (LauncherAuthException exception)
            {
                if (!IsRefreshSessionCurrent(sessionGeneration))
                {
                    return CancelledRefresh();
                }
                AccountErrorCategory category = MapAccountFailure(
                    AccountOperationState.None,
                    exception);
                if (category == AccountErrorCategory.SessionExpired)
                {
                    HandleUnauthorized(category);
                    return new AccountActionCompletion(
                        AccountActionCompletionStatus.Failed,
                        CurrentSnapshot);
                }

                sessionsState = AccountSessionsState.Failed;
                accountError = new AccountRuntimeError(AccountOperationState.None, category);
                WriteAccountFailureSafely(AccountOperationState.None, category, exception);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                if (!IsRefreshSessionCurrent(sessionGeneration))
                {
                    return CancelledRefresh();
                }
                sessionsState = AccountSessionsState.Failed;
                accountError = new AccountRuntimeError(
                    AccountOperationState.None,
                    AccountErrorCategory.Timeout);
                WriteAccountFailureSafely(
                    AccountOperationState.None,
                    AccountErrorCategory.Timeout,
                    exception);
            }
            catch (HttpRequestException exception)
            {
                if (!IsRefreshSessionCurrent(sessionGeneration))
                {
                    return CancelledRefresh();
                }
                sessionsState = AccountSessionsState.Failed;
                accountError = new AccountRuntimeError(
                    AccountOperationState.None,
                    AccountErrorCategory.Network);
                WriteAccountFailureSafely(
                    AccountOperationState.None,
                    AccountErrorCategory.Network,
                    exception);
            }
            catch (Exception exception)
            {
                if (!IsRefreshSessionCurrent(sessionGeneration))
                {
                    return CancelledRefresh();
                }
                sessionsState = AccountSessionsState.Failed;
                accountError = new AccountRuntimeError(
                    AccountOperationState.None,
                    AccountErrorCategory.Unknown);
                WriteAccountFailureSafely(
                    AccountOperationState.None,
                    AccountErrorCategory.Unknown,
                    exception);
            }

            AccountRuntimeSnapshot snapshot;
            lock (_sync)
            {
                if (IsStoppingUnsafe()
                    || sessionGeneration != _sessionGeneration
                    || !_session.CurrentSnapshot.IsAuthenticated)
                {
                    return new AccountActionCompletion(
                        AccountActionCompletionStatus.Cancelled,
                        _currentSnapshot);
                }

                snapshot = SetSnapshotUnsafe(_currentSnapshot with
                {
                    IsAuthenticated = true,
                    Username = result.Profile.Username,
                    Email = result.Profile.Email,
                    EmailVerified = result.Profile.EmailVerified,
                    Avatar = result.Profile.Avatar,
                    AvatarAvailability = result.SupportsProfilePhotos
                        ? AvatarBackendAvailability.Available
                        : AvatarBackendAvailability.Unavailable,
                    LoadingState = AccountLoadingState.Loaded,
                    AvatarOperation = AccountAvatarOperationState.None,
                    OperationId = null,
                    UploadPercentage = null,
                    ErrorCategory = AccountAvatarErrorCategory.None,
                    SecurityState = AccountSecurityState.Ready,
                    SessionsState = sessionsState,
                    Sessions = sessions,
                    CurrentSessionId = sessions.FirstOrDefault(item => item.IsCurrent)?.Id,
                    AccountOperation = AccountOperationState.None,
                    TargetSessionId = null,
                    AccountError = accountError,
                    Notice = AccountNoticeKind.None
                });
            }

            RaiseSnapshotChanged(snapshot);
            return new AccountActionCompletion(
                result.SupportsProfilePhotos && sessionsState == AccountSessionsState.Loaded
                    ? AccountActionCompletionStatus.Succeeded
                    : result.SupportsProfilePhotos
                        ? AccountActionCompletionStatus.Failed
                        : AccountActionCompletionStatus.BackendUnavailable,
                snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsRefreshSessionCurrent(sessionGeneration))
            {
                return CancelledRefresh();
            }
            AccountRuntimeSnapshot snapshot = PublishStable(
                fromCancellation
                    ? AccountAvatarErrorCategory.CancellationAmbiguous
                    : AccountAvatarErrorCategory.None);
            return new AccountActionCompletion(AccountActionCompletionStatus.Cancelled, snapshot);
        }
        catch (AvatarMediaException exception)
        {
            if (!IsRefreshSessionCurrent(sessionGeneration))
            {
                return CancelledRefresh();
            }
            if (exception.Category == AvatarMediaFailureCategory.Unauthorized)
            {
                _session.NotifyAuthenticatedRequestUnauthorized();
                AccountAvatarErrorCategory unauthorizedError = fromCancellation
                    ? AccountAvatarErrorCategory.CancellationAmbiguous
                    : AccountAvatarErrorCategory.Unauthorized;
                WriteFailureSafely("refresh", unauthorizedError, exception);
                return new AccountActionCompletion(
                    AccountActionCompletionStatus.Failed,
                    CurrentSnapshot);
            }
            AccountAvatarErrorCategory error = fromCancellation
                ? AccountAvatarErrorCategory.CancellationAmbiguous
                : MapFailure(exception.Category);
            AccountRuntimeSnapshot snapshot = PublishStable(
                error,
                exception.Category == AvatarMediaFailureCategory.BackendUnavailable
                    ? AvatarBackendAvailability.Unavailable
                    : null);
            WriteFailureSafely("refresh", error, exception);
            return new AccountActionCompletion(
                exception.Category == AvatarMediaFailureCategory.BackendUnavailable
                    ? AccountActionCompletionStatus.BackendUnavailable
                    : AccountActionCompletionStatus.Failed,
                snapshot);
        }
        catch (Exception exception)
        {
            if (!IsRefreshSessionCurrent(sessionGeneration))
            {
                return CancelledRefresh();
            }
            AccountRuntimeSnapshot snapshot = PublishStable(AccountAvatarErrorCategory.Unknown);
            WriteFailureSafely("refresh", AccountAvatarErrorCategory.Unknown, exception);
            return new AccountActionCompletion(AccountActionCompletionStatus.Failed, snapshot);
        }
    }

    private static async Task<AccountActionCompletion> RunAfterGateAsync(
        Task gate,
        Func<Task<AccountActionCompletion>> action)
    {
        await gate.ConfigureAwait(false);
        return await action().ConfigureAwait(false);
    }

    private async Task<AccountActionCompletion> RunChangeEmailAsync(
        LauncherOperationLease lease,
        string email)
    {
        try
        {
            AccountActionCompletion? preparationFailure = await PrepareAccountMutationAsync(
                lease,
                AccountOperationState.ChangingEmail).ConfigureAwait(false);
            if (preparationFailure is not null)
            {
                return preparationFailure;
            }

            EmailChangeResult result = await _authentication
                .ChangeEmailAsync(email, lease.CancellationToken)
                .ConfigureAwait(false);
            return CompleteAccountMutationSuccess(
                lease,
                snapshot => snapshot with
                {
                    Username = result.Profile.Username,
                    Email = result.Profile.Email,
                    EmailVerified = result.Profile.EmailVerified,
                    SecurityState = AccountSecurityState.Ready,
                    Notice = result.VerificationEmailSent
                        ? AccountNoticeKind.EmailChangedVerificationSent
                        : AccountNoticeKind.EmailChangedVerificationUnavailable
                });
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            return CompleteAccountMutationCancelled(lease);
        }
        catch (OperationCanceledException exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ChangingEmail,
                AccountErrorCategory.Timeout,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingEmail,
                AccountErrorCategory.Timeout);
        }
        catch (LauncherAuthException exception)
        {
            AccountErrorCategory category = MapAccountFailure(
                AccountOperationState.ChangingEmail,
                exception);
            HandleUnauthorized(category);
            WriteAccountFailureSafely(AccountOperationState.ChangingEmail, category, exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingEmail,
                category);
        }
        catch (HttpRequestException exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ChangingEmail,
                AccountErrorCategory.Network,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingEmail,
                AccountErrorCategory.Network);
        }
        catch (Exception exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ChangingEmail,
                AccountErrorCategory.Unknown,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingEmail,
                AccountErrorCategory.Unknown);
        }
    }

    private async Task<AccountActionCompletion> RunResendVerificationAsync(
        LauncherOperationLease lease)
    {
        try
        {
            AccountActionCompletion? preparationFailure = await PrepareAccountMutationAsync(
                lease,
                AccountOperationState.ResendingVerification).ConfigureAwait(false);
            if (preparationFailure is not null)
            {
                return preparationFailure;
            }

            _ = await _authentication
                .ResendVerificationAsync(lease.CancellationToken)
                .ConfigureAwait(false);
            return CompleteAccountMutationSuccess(
                lease,
                snapshot => snapshot with
                {
                    Notice = AccountNoticeKind.VerificationEmailSent
                });
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            return CompleteAccountMutationCancelled(lease);
        }
        catch (OperationCanceledException exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ResendingVerification,
                AccountErrorCategory.Timeout,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ResendingVerification,
                AccountErrorCategory.Timeout);
        }
        catch (LauncherAuthException exception)
        {
            AccountErrorCategory category = MapAccountFailure(
                AccountOperationState.ResendingVerification,
                exception);
            HandleUnauthorized(category);
            WriteAccountFailureSafely(
                AccountOperationState.ResendingVerification,
                category,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ResendingVerification,
                category);
        }
        catch (HttpRequestException exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ResendingVerification,
                AccountErrorCategory.Network,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ResendingVerification,
                AccountErrorCategory.Network);
        }
        catch (Exception exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ResendingVerification,
                AccountErrorCategory.Unknown,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ResendingVerification,
                AccountErrorCategory.Unknown);
        }
    }

    private async Task<AccountActionCompletion> RunChangePasswordAsync(
        LauncherOperationLease lease,
        string currentPassword,
        string newPassword)
    {
        try
        {
            AccountActionCompletion? preparationFailure = await PrepareAccountMutationAsync(
                lease,
                AccountOperationState.ChangingPassword).ConfigureAwait(false);
            if (preparationFailure is not null)
            {
                return preparationFailure;
            }

            await _authentication.ChangePasswordAsync(
                currentPassword,
                newPassword,
                lease.CancellationToken).ConfigureAwait(false);
            return CompleteAccountMutationSuccess(
                lease,
                snapshot => snapshot with { Notice = AccountNoticeKind.PasswordChanged });
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            return CompleteAccountMutationCancelled(lease);
        }
        catch (OperationCanceledException exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ChangingPassword,
                AccountErrorCategory.Timeout,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingPassword,
                AccountErrorCategory.Timeout);
        }
        catch (LauncherAuthException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            AccountErrorCategory category = await ResolvePasswordUnauthorizedAsync(
                lease.CancellationToken).ConfigureAwait(false);
            if (category == AccountErrorCategory.None
                && lease.CancellationToken.IsCancellationRequested)
            {
                return CompleteAccountMutationCancelled(lease);
            }
            HandleUnauthorized(category);
            WriteAccountFailureSafely(AccountOperationState.ChangingPassword, category, exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingPassword,
                category);
        }
        catch (LauncherAuthException exception)
        {
            AccountErrorCategory category = MapAccountFailure(
                AccountOperationState.ChangingPassword,
                exception);
            HandleUnauthorized(category);
            WriteAccountFailureSafely(AccountOperationState.ChangingPassword, category, exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingPassword,
                category);
        }
        catch (HttpRequestException exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ChangingPassword,
                AccountErrorCategory.Network,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingPassword,
                AccountErrorCategory.Network);
        }
        catch (Exception exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.ChangingPassword,
                AccountErrorCategory.Unknown,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.ChangingPassword,
                AccountErrorCategory.Unknown);
        }
    }

    private async Task<AccountActionCompletion> RunRevokeSessionAsync(
        LauncherOperationLease lease,
        string sessionId)
    {
        try
        {
            AccountActionCompletion? preparationFailure = await PrepareAccountMutationAsync(
                lease,
                AccountOperationState.RevokingSession).ConfigureAwait(false);
            if (preparationFailure is not null)
            {
                return preparationFailure;
            }

            await _authentication
                .RevokeSessionAsync(sessionId, lease.CancellationToken)
                .ConfigureAwait(false);
            return CompleteAccountMutationSuccess(
                lease,
                snapshot => snapshot with
                {
                    Sessions = snapshot.Sessions
                        .Where(item => !string.Equals(
                            item.Id,
                            sessionId,
                            StringComparison.OrdinalIgnoreCase))
                        .ToImmutableArray(),
                    Notice = AccountNoticeKind.SessionRevoked
                });
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            return CompleteAccountMutationCancelled(lease);
        }
        catch (OperationCanceledException exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.RevokingSession,
                AccountErrorCategory.Timeout,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.RevokingSession,
                AccountErrorCategory.Timeout);
        }
        catch (LauncherAuthException exception)
        {
            AccountErrorCategory category = MapAccountFailure(
                AccountOperationState.RevokingSession,
                exception);
            HandleUnauthorized(category);
            WriteAccountFailureSafely(AccountOperationState.RevokingSession, category, exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.RevokingSession,
                category);
        }
        catch (HttpRequestException exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.RevokingSession,
                AccountErrorCategory.Network,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.RevokingSession,
                AccountErrorCategory.Network);
        }
        catch (Exception exception)
        {
            WriteAccountFailureSafely(
                AccountOperationState.RevokingSession,
                AccountErrorCategory.Unknown,
                exception);
            return CompleteAccountMutationFailure(
                lease,
                AccountOperationState.RevokingSession,
                AccountErrorCategory.Unknown);
        }
    }

    private async Task<AccountActionCompletion?> PrepareAccountMutationAsync(
        LauncherOperationLease lease,
        AccountOperationState operation)
    {
        AtlasRequestPreparationStatus preparation = await _session
            .PrepareAuthenticatedRequestAsync(lease.CancellationToken)
            .ConfigureAwait(false);
        if (preparation == AtlasRequestPreparationStatus.Ready)
        {
            return null;
        }

        AccountErrorCategory error = preparation switch
        {
            AtlasRequestPreparationStatus.AuthenticationRequired =>
                AccountErrorCategory.SessionExpired,
            AtlasRequestPreparationStatus.Unavailable => AccountErrorCategory.Network,
            _ => AccountErrorCategory.None
        };
        return preparation is AtlasRequestPreparationStatus.Cancelled
            or AtlasRequestPreparationStatus.ShuttingDown
                ? CompleteAccountMutationCancelled(lease)
                : CompleteAccountMutationFailure(lease, operation, error);
    }

    private async Task<AccountErrorCategory> ResolvePasswordUnauthorizedAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _authentication.RefreshProfileAsync(cancellationToken).ConfigureAwait(false);
            return AccountErrorCategory.CurrentPasswordIncorrect;
        }
        catch (LauncherAuthException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            return AccountErrorCategory.SessionExpired;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AccountErrorCategory.None;
        }
        catch (OperationCanceledException)
        {
            return AccountErrorCategory.Timeout;
        }
        catch (HttpRequestException)
        {
            return AccountErrorCategory.Network;
        }
        catch
        {
            return AccountErrorCategory.Unknown;
        }
    }

    private async Task<AccountActionCompletion> RunUploadAsync(
        LauncherOperationLease lease,
        AvatarUploadRequest request)
    {
        try
        {
            AtlasRequestPreparationStatus preparation = await _session
                .PrepareAuthenticatedRequestAsync(lease.CancellationToken)
                .ConfigureAwait(false);
            if (preparation != AtlasRequestPreparationStatus.Ready)
            {
                return CompleteMutationFailure(
                    lease,
                    preparation == AtlasRequestPreparationStatus.AuthenticationRequired
                        ? AccountAvatarErrorCategory.Unauthorized
                        : AccountAvatarErrorCategory.Network);
            }

            Progress<AvatarUploadTransferProgress> progress = new(value =>
                PublishUploadProgress(lease, value));
            AvatarDescriptor avatar = await _mediaClient.UploadAvatarAsync(
                request,
                progress,
                lease.CancellationToken).ConfigureAwait(false);
            return CompleteMutationSuccess(lease, avatar);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            if (_operations.IsShuttingDown)
            {
                return CompleteMutationCancelled(lease, reconcile: false);
            }

            PublishMutationPhase(lease, AccountAvatarOperationState.Reconciling, null);
            AccountActionCompletion reconciled = await RefreshProfileAsync(
                _operations.ShutdownToken,
                fromCancellation: true,
                CaptureSessionGeneration()).ConfigureAwait(false);
            CompleteLease(lease);
            return reconciled with { Status = AccountActionCompletionStatus.Cancelled };
        }
        catch (AvatarMediaException exception)
        {
            if (exception.Category == AvatarMediaFailureCategory.Unauthorized)
            {
                _session.NotifyAuthenticatedRequestUnauthorized();
            }
            WriteFailureSafely("upload", MapFailure(exception.Category), exception);
            return CompleteMutationFailure(lease, MapFailure(exception.Category));
        }
        catch (Exception exception)
        {
            WriteFailureSafely("upload", AccountAvatarErrorCategory.Unknown, exception);
            return CompleteMutationFailure(lease, AccountAvatarErrorCategory.Unknown);
        }
    }

    private async Task<AccountActionCompletion> RunDeleteAsync(LauncherOperationLease lease)
    {
        AvatarDescriptor? previous = CurrentSnapshot.Avatar;
        try
        {
            AtlasRequestPreparationStatus preparation = await _session
                .PrepareAuthenticatedRequestAsync(lease.CancellationToken)
                .ConfigureAwait(false);
            if (preparation != AtlasRequestPreparationStatus.Ready)
            {
                return CompleteMutationFailure(
                    lease,
                    preparation == AtlasRequestPreparationStatus.AuthenticationRequired
                        ? AccountAvatarErrorCategory.Unauthorized
                        : AccountAvatarErrorCategory.Network);
            }

            await _mediaClient.DeleteAvatarAsync(lease.CancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                _imageCache.Evict(previous);
            }
            return CompleteMutationSuccess(lease, avatar: null);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            return CompleteMutationCancelled(lease, reconcile: false);
        }
        catch (AvatarMediaException exception)
        {
            if (exception.Category == AvatarMediaFailureCategory.Unauthorized)
            {
                _session.NotifyAuthenticatedRequestUnauthorized();
            }
            WriteFailureSafely("delete", MapFailure(exception.Category), exception);
            return CompleteMutationFailure(lease, MapFailure(exception.Category));
        }
        catch (Exception exception)
        {
            WriteFailureSafely("delete", AccountAvatarErrorCategory.Unknown, exception);
            return CompleteMutationFailure(lease, AccountAvatarErrorCategory.Unknown);
        }
    }

    private void PublishUploadProgress(
        LauncherOperationLease lease,
        AvatarUploadTransferProgress progress)
    {
        AccountAvatarOperationState phase = progress.Phase switch
        {
            AvatarUploadPhase.Preparing => AccountAvatarOperationState.Preparing,
            AvatarUploadPhase.Sending => AccountAvatarOperationState.Uploading,
            AvatarUploadPhase.Processing => AccountAvatarOperationState.Processing,
            _ => AccountAvatarOperationState.Uploading
        };
        DateTimeOffset now = _timeProvider.GetUtcNow();
        AccountRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!ReferenceEquals(_mutationLease, lease)
                || !lease.IsCurrent
                || IsStoppingUnsafe())
            {
                return;
            }

            bool phaseChanged = _currentSnapshot.AvatarOperation != phase;
            bool finalProgress = progress.Percentage == 100;
            if (!phaseChanged
                && !finalProgress
                && now - _lastProgressPublication < ProgressPublicationInterval)
            {
                return;
            }

            _lastProgressPublication = now;
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                AvatarOperation = phase,
                UploadPercentage = phase == AccountAvatarOperationState.Uploading
                    ? progress.Percentage
                    : null
            });
        }

        RaiseSnapshotChanged(snapshot);
    }

    private void PublishMutationPhase(
        LauncherOperationLease lease,
        AccountAvatarOperationState phase,
        int? percentage)
    {
        AccountRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (ReferenceEquals(_mutationLease, lease) && lease.IsCurrent && !IsStoppingUnsafe())
            {
                snapshot = SetSnapshotUnsafe(_currentSnapshot with
                {
                    AvatarOperation = phase,
                    UploadPercentage = percentage
                });
            }
        }
        RaiseSnapshotChanged(snapshot);
    }

    private AccountActionCompletion CompleteAccountMutationSuccess(
        LauncherOperationLease lease,
        Func<AccountRuntimeSnapshot, AccountRuntimeSnapshot> update)
    {
        AccountRuntimeSnapshot snapshot;
        bool succeeded;
        lock (_sync)
        {
            succeeded = ReferenceEquals(_mutationLease, lease)
                && lease.IsCurrent
                && !IsStoppingUnsafe()
                && _currentSnapshot.IsAuthenticated;
            if (!succeeded)
            {
                snapshot = _currentSnapshot;
            }
            else
            {
                snapshot = SetSnapshotUnsafe(update(_currentSnapshot) with
                {
                    OperationId = null,
                    AccountOperation = AccountOperationState.None,
                    TargetSessionId = null,
                    AccountError = AccountRuntimeError.None
                });
            }
            CompleteLeaseUnsafe(lease);
        }

        if (succeeded)
        {
            RaiseSnapshotChanged(snapshot);
        }
        lease.Dispose();
        return new AccountActionCompletion(
            succeeded
                ? AccountActionCompletionStatus.Succeeded
                : AccountActionCompletionStatus.Cancelled,
            snapshot);
    }

    private AccountActionCompletion CompleteAccountMutationFailure(
        LauncherOperationLease lease,
        AccountOperationState operation,
        AccountErrorCategory error)
    {
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            snapshot = ReferenceEquals(_mutationLease, lease)
                && lease.IsCurrent
                && !IsStoppingUnsafe()
                && _currentSnapshot.IsAuthenticated
                    ? SetSnapshotUnsafe(_currentSnapshot with
                    {
                        OperationId = null,
                        AccountOperation = AccountOperationState.None,
                        TargetSessionId = null,
                        AccountError = new AccountRuntimeError(operation, error),
                        Notice = AccountNoticeKind.None
                    })
                    : _currentSnapshot;
            CompleteLeaseUnsafe(lease);
        }

        RaiseSnapshotChanged(snapshot);
        lease.Dispose();
        return new AccountActionCompletion(AccountActionCompletionStatus.Failed, snapshot);
    }

    private AccountActionCompletion CompleteAccountMutationCancelled(
        LauncherOperationLease lease)
    {
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            snapshot = ReferenceEquals(_mutationLease, lease)
                && lease.IsCurrent
                && !IsStoppingUnsafe()
                && _currentSnapshot.IsAuthenticated
                    ? SetSnapshotUnsafe(_currentSnapshot with
                    {
                        OperationId = null,
                        AccountOperation = AccountOperationState.None,
                        TargetSessionId = null,
                        AccountError = AccountRuntimeError.None,
                        Notice = AccountNoticeKind.None
                    })
                    : _currentSnapshot;
            CompleteLeaseUnsafe(lease);
        }

        RaiseSnapshotChanged(snapshot);
        lease.Dispose();
        return new AccountActionCompletion(AccountActionCompletionStatus.Cancelled, snapshot);
    }

    private AccountActionCompletion CompleteMutationSuccess(
        LauncherOperationLease lease,
        AvatarDescriptor? avatar)
    {
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (!ReferenceEquals(_mutationLease, lease) || IsStoppingUnsafe())
            {
                CompleteLeaseUnsafe(lease);
                return new AccountActionCompletion(
                    AccountActionCompletionStatus.Cancelled,
                    _currentSnapshot);
            }

            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                OperationId = null,
                Avatar = avatar,
                AvatarOperation = AccountAvatarOperationState.None,
                UploadPercentage = null,
                ErrorCategory = AccountAvatarErrorCategory.None,
                LoadingState = AccountLoadingState.Loaded
            });
            CompleteLeaseUnsafe(lease);
        }

        RaiseSnapshotChanged(snapshot);
        lease.Dispose();
        return new AccountActionCompletion(AccountActionCompletionStatus.Succeeded, snapshot);
    }

    private AccountActionCompletion CompleteMutationFailure(
        LauncherOperationLease lease,
        AccountAvatarErrorCategory error)
    {
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            snapshot = ReferenceEquals(_mutationLease, lease) && !IsStoppingUnsafe()
                ? SetSnapshotUnsafe(_currentSnapshot with
                {
                    OperationId = null,
                    AvatarOperation = AccountAvatarOperationState.None,
                    UploadPercentage = null,
                    ErrorCategory = error,
                    AvatarAvailability = error == AccountAvatarErrorCategory.BackendUnavailable
                        ? AvatarBackendAvailability.Unavailable
                        : _currentSnapshot.AvatarAvailability
                })
                : _currentSnapshot;
            CompleteLeaseUnsafe(lease);
        }

        RaiseSnapshotChanged(snapshot);
        lease.Dispose();
        return new AccountActionCompletion(
            error == AccountAvatarErrorCategory.BackendUnavailable
                ? AccountActionCompletionStatus.BackendUnavailable
                : AccountActionCompletionStatus.Failed,
            snapshot);
    }

    private AccountActionCompletion CompleteMutationCancelled(
        LauncherOperationLease lease,
        bool reconcile)
    {
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            snapshot = ReferenceEquals(_mutationLease, lease) && !IsStoppingUnsafe()
                ? SetSnapshotUnsafe(_currentSnapshot with
                {
                    OperationId = null,
                    AvatarOperation = AccountAvatarOperationState.None,
                    UploadPercentage = null,
                    ErrorCategory = reconcile
                        ? AccountAvatarErrorCategory.CancellationAmbiguous
                        : AccountAvatarErrorCategory.None
                })
                : _currentSnapshot;
            CompleteLeaseUnsafe(lease);
        }

        RaiseSnapshotChanged(snapshot);
        lease.Dispose();
        return new AccountActionCompletion(AccountActionCompletionStatus.Cancelled, snapshot);
    }

    private void CompleteLease(LauncherOperationLease lease)
    {
        lock (_sync)
        {
            CompleteLeaseUnsafe(lease);
        }
        lease.Dispose();
    }

    private void CompleteLeaseUnsafe(LauncherOperationLease lease)
    {
        if (ReferenceEquals(_mutationLease, lease)
            && _mutationLease.OperationId == lease.OperationId)
        {
            _mutationLease = null;
        }
    }

    private AccountRuntimeSnapshot PublishStable(
        AccountAvatarErrorCategory error,
        AvatarBackendAvailability? availability = null)
    {
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            snapshot = IsStoppingUnsafe()
                ? _currentSnapshot
                : SetSnapshotUnsafe(_currentSnapshot with
                {
                    OperationId = null,
                    LoadingState = _currentSnapshot.IsAuthenticated
                        ? AccountLoadingState.Loaded
                        : AccountLoadingState.SignedOut,
                    AvatarOperation = AccountAvatarOperationState.None,
                    UploadPercentage = null,
                    ErrorCategory = error,
                    SessionsState = _currentSnapshot.SessionsState == AccountSessionsState.Loading
                        ? _currentSnapshot.Sessions.IsDefaultOrEmpty
                            ? AccountSessionsState.NotLoaded
                            : AccountSessionsState.Loaded
                        : _currentSnapshot.SessionsState,
                    AvatarAvailability = availability ?? _currentSnapshot.AvatarAvailability
                });
        }

        RaiseSnapshotChanged(snapshot);
        return snapshot;
    }

    private AccountActionStartResult? GetMutationRejectionUnsafe()
    {
        if (IsStoppingUnsafe())
        {
            return AccountActionStartResult.Rejected(AccountActionStartStatus.ShuttingDown);
        }
        if (!_session.CurrentSnapshot.IsAuthenticated)
        {
            return AccountActionStartResult.Rejected(AccountActionStartStatus.NotAuthenticated);
        }
        if (_mutationLease is not null || !_activeRefreshTask.IsCompleted)
        {
            return AccountActionStartResult.Rejected(AccountActionStartStatus.Busy);
        }
        return null;
    }

    private void Session_SnapshotChanged(object? sender, AuthSessionSnapshotEventArgs e)
    {
        CancellationTokenSource? refresh;
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (IsStoppingUnsafe())
            {
                return;
            }
            _sessionGeneration++;
            refresh = _refreshCancellation;
            snapshot = CreateSessionSnapshot(e.Snapshot, _getCurrentProfile());
            _currentSnapshot = snapshot;
        }

        TryCancel(refresh);
        RaiseSnapshotChanged(snapshot);
    }

    private long CaptureSessionGeneration()
    {
        lock (_sync)
        {
            return _sessionGeneration;
        }
    }

    private bool IsRefreshSessionCurrent(long sessionGeneration)
    {
        lock (_sync)
        {
            return !IsStoppingUnsafe()
                && sessionGeneration == _sessionGeneration
                && _session.CurrentSnapshot.IsAuthenticated;
        }
    }

    private AccountActionCompletion CancelledRefresh()
    {
        lock (_sync)
        {
            return new AccountActionCompletion(
                AccountActionCompletionStatus.Cancelled,
                _currentSnapshot);
        }
    }

    private AccountRuntimeSnapshot CreateSessionSnapshot(
        AuthSessionSnapshot session,
        LauncherProfile? profile)
    {
        if (!session.IsAuthenticated)
        {
            return AccountRuntimeSnapshot.SignedOut with { Sequence = ++_sequence };
        }

        AvatarDescriptor? avatar = profile?.Avatar;
        return new AccountRuntimeSnapshot(
            Sequence: ++_sequence,
            OperationId: null,
            IsAuthenticated: true,
            Username: profile?.Username ?? session.Username,
            Email: profile?.Email ?? string.Empty,
            EmailVerified: profile?.EmailVerified ?? session.IsEmailVerified,
            Avatar: avatar,
            AvatarAvailability: avatar is null
                ? AvatarBackendAvailability.Unknown
                : AvatarBackendAvailability.Available,
            LoadingState: AccountLoadingState.Idle,
            AvatarOperation: AccountAvatarOperationState.None,
            UploadPercentage: null,
            ErrorCategory: AccountAvatarErrorCategory.None,
            SecurityState: AccountSecurityState.Ready,
            SessionsState: AccountSessionsState.NotLoaded,
            Sessions: ImmutableArray<AccountDeviceSessionSnapshot>.Empty,
            CurrentSessionId: null,
            AccountOperation: AccountOperationState.None,
            TargetSessionId: null,
            AccountError: AccountRuntimeError.None,
            Notice: AccountNoticeKind.None);
    }

    private AccountRuntimeSnapshot SetSnapshotUnsafe(AccountRuntimeSnapshot snapshot)
    {
        _currentSnapshot = snapshot with { Sequence = ++_sequence };
        return _currentSnapshot;
    }

    private bool IsStoppingUnsafe()
    {
        return Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown;
    }

    private void RaiseSnapshotChanged(AccountRuntimeSnapshot? snapshot)
    {
        if (snapshot is null || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }
        try
        {
            SnapshotChanged?.Invoke(this, new AccountRuntimeSnapshotEventArgs(snapshot));
        }
        catch
        {
            // Presentation subscribers cannot interrupt account lifecycle cleanup.
        }
    }

    private void WriteFailureSafely(
        string phase,
        AccountAvatarErrorCategory category,
        Exception exception)
    {
        try
        {
            _writeLog(
                $"Avatar V2 indisponible: phase={phase}; category={category}; "
                + $"type={exception.GetType().Name}.");
        }
        catch
        {
        }
    }

    private void WriteAccountFailureSafely(
        AccountOperationState operation,
        AccountErrorCategory category,
        Exception exception)
    {
        try
        {
            _writeLog(
                $"Compte V2 indisponible: operation={operation}; category={category}; "
                + $"type={exception.GetType().Name}.");
        }
        catch
        {
        }
    }

    private void HandleUnauthorized(AccountErrorCategory category)
    {
        if (category == AccountErrorCategory.SessionExpired)
        {
            _session.NotifyAuthenticatedRequestUnauthorized();
        }
    }

    private static AccountDeviceSessionSnapshot ToSessionSnapshot(
        LauncherDeviceSession session)
    {
        return new AccountDeviceSessionSnapshot(
            session.Id,
            string.IsNullOrWhiteSpace(session.DeviceName)
                ? "Appareil inconnu"
                : session.DeviceName,
            session.CreatedAt,
            session.LastSeenAt,
            session.ExpiresAt,
            session.Current);
    }

    private static AccountErrorCategory MapAccountFailure(
        AccountOperationState operation,
        LauncherAuthException exception)
    {
        return exception.StatusCode switch
        {
            HttpStatusCode.BadRequest when operation == AccountOperationState.ChangingEmail =>
                AccountErrorCategory.InvalidEmail,
            HttpStatusCode.BadRequest when operation == AccountOperationState.ChangingPassword =>
                AccountErrorCategory.InvalidPassword,
            HttpStatusCode.Conflict when operation == AccountOperationState.ChangingEmail =>
                AccountErrorCategory.EmailAlreadyUsed,
            HttpStatusCode.Unauthorized => AccountErrorCategory.SessionExpired,
            HttpStatusCode.NotFound when operation == AccountOperationState.RevokingSession =>
                AccountErrorCategory.SessionNotFound,
            HttpStatusCode.TooManyRequests => AccountErrorCategory.RateLimited,
            HttpStatusCode.RequestTimeout => AccountErrorCategory.Timeout,
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout => AccountErrorCategory.ServiceUnavailable,
            _ when exception.StatusCode is HttpStatusCode statusCode
                   && (int)statusCode >= 500 => AccountErrorCategory.ServiceUnavailable,
            _ when exception.StatusCode is not null => AccountErrorCategory.ServerRejected,
            _ => AccountErrorCategory.Unknown
        };
    }

    private static AccountAvatarErrorCategory MapFailure(AvatarMediaFailureCategory category)
    {
        return category switch
        {
            AvatarMediaFailureCategory.AvatarTooLarge => AccountAvatarErrorCategory.AvatarTooLarge,
            AvatarMediaFailureCategory.InvalidImage => AccountAvatarErrorCategory.InvalidImage,
            AvatarMediaFailureCategory.UnsupportedFormat => AccountAvatarErrorCategory.UnsupportedFormat,
            AvatarMediaFailureCategory.InvalidDimensions => AccountAvatarErrorCategory.InvalidDimensions,
            AvatarMediaFailureCategory.InvalidCrop => AccountAvatarErrorCategory.InvalidCrop,
            AvatarMediaFailureCategory.UploadInProgress => AccountAvatarErrorCategory.UploadInProgress,
            AvatarMediaFailureCategory.RateLimited => AccountAvatarErrorCategory.RateLimited,
            AvatarMediaFailureCategory.ProcessingFailed => AccountAvatarErrorCategory.ProcessingFailed,
            AvatarMediaFailureCategory.StorageFailed => AccountAvatarErrorCategory.StorageFailed,
            AvatarMediaFailureCategory.Unauthorized => AccountAvatarErrorCategory.Unauthorized,
            AvatarMediaFailureCategory.BackendUnavailable => AccountAvatarErrorCategory.BackendUnavailable,
            AvatarMediaFailureCategory.Network => AccountAvatarErrorCategory.Network,
            _ => AccountAvatarErrorCategory.Unknown
        };
    }

    private static AccountActionStartStatus MapOperationStatus(
        LauncherOperationStartStatus status)
    {
        return status switch
        {
            LauncherOperationStartStatus.ShuttingDown => AccountActionStartStatus.ShuttingDown,
            LauncherOperationStartStatus.Busy => AccountActionStartStatus.Busy,
            _ => AccountActionStartStatus.RejectedByCompatibility
        };
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
