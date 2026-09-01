using WotLK.Launcher.Account;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherAccountCoordinator : IDisposable
{
    private static readonly TimeSpan ProgressPublicationInterval = TimeSpan.FromMilliseconds(85);
    private readonly object _sync = new();
    private readonly LauncherSessionCoordinator _session;
    private readonly LauncherOperationCoordinator _operations;
    private readonly IAvatarMediaClient _mediaClient;
    private readonly AvatarImageCache _imageCache;
    private readonly Func<LauncherProfile?> _getCurrentProfile;
    private readonly Action<string> _writeLog;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _refreshCancellation;
    private Task _activeRefreshTask = Task.CompletedTask;
    private LauncherOperationLease? _avatarLease;
    private Task _activeMutationTask = Task.CompletedTask;
    private AccountRuntimeSnapshot _currentSnapshot;
    private DateTimeOffset _lastProgressPublication;
    private long _sequence;
    private int _disposeState;

    internal LauncherAccountCoordinator(
        LauncherSessionCoordinator session,
        LauncherOperationCoordinator operations,
        IAvatarMediaClient mediaClient,
        AvatarImageCache imageCache,
        Func<LauncherProfile?> getCurrentProfile,
        Action<string> writeLog,
        TimeProvider? timeProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
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
            if (!_activeRefreshTask.IsCompleted || _avatarLease is not null)
            {
                return AccountActionStartResult.Rejected(AccountActionStartStatus.Busy);
            }

            CancellationTokenSource refreshCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                _operations.ShutdownToken);
            _refreshCancellation = refreshCancellation;
            loading = SetSnapshotUnsafe(
                _currentSnapshot with
                {
                    LoadingState = AccountLoadingState.Loading,
                    ErrorCategory = AccountAvatarErrorCategory.None
                });
            startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = RunAfterGateAsync(
                startGate.Task,
                () => RefreshCoreAsync(refreshCancellation));
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
            if (IsStoppingUnsafe() || _avatarLease is not null)
            {
                lease.CancelForShutdown();
                lease.Dispose();
                return AccountActionStartResult.Rejected(AccountActionStartStatus.ShuttingDown);
            }

            _avatarLease = lease;
            _lastProgressPublication = DateTimeOffset.MinValue;
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                OperationId = lease.OperationId,
                AvatarOperation = AccountAvatarOperationState.Preparing,
                UploadPercentage = 0,
                ErrorCategory = AccountAvatarErrorCategory.None
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
            if (IsStoppingUnsafe() || _avatarLease is not null)
            {
                lease.CancelForShutdown();
                lease.Dispose();
                return AccountActionStartResult.Rejected(AccountActionStartStatus.ShuttingDown);
            }

            _avatarLease = lease;
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                OperationId = lease.OperationId,
                AvatarOperation = AccountAvatarOperationState.Removing,
                UploadPercentage = null,
                ErrorCategory = AccountAvatarErrorCategory.None
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

    internal bool CancelUploadFromUser()
    {
        lock (_sync)
        {
            return _avatarLease is { Kind: LauncherOperationKind.AvatarUpload } lease
                && lease.CancelFromUser();
        }
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
            lease = _avatarLease;
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
        CancellationTokenSource cancellation)
    {
        try
        {
            return await RefreshProfileAsync(cancellation.Token, fromCancellation: false)
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
        bool fromCancellation)
    {
        AtlasRequestPreparationStatus preparation = await _session
            .PrepareAuthenticatedRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        if (preparation != AtlasRequestPreparationStatus.Ready)
        {
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

        try
        {
            AvatarProfileReadResult result = await _mediaClient
                .GetProfileAsync(cancellationToken)
                .ConfigureAwait(false);
            AccountRuntimeSnapshot snapshot;
            lock (_sync)
            {
                if (IsStoppingUnsafe())
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
                    ErrorCategory = AccountAvatarErrorCategory.None
                });
            }

            RaiseSnapshotChanged(snapshot);
            return new AccountActionCompletion(
                result.SupportsProfilePhotos
                    ? AccountActionCompletionStatus.Succeeded
                    : AccountActionCompletionStatus.BackendUnavailable,
                snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AccountRuntimeSnapshot snapshot = PublishStable(
                fromCancellation
                    ? AccountAvatarErrorCategory.CancellationAmbiguous
                    : AccountAvatarErrorCategory.None);
            return new AccountActionCompletion(AccountActionCompletionStatus.Cancelled, snapshot);
        }
        catch (AvatarMediaException exception)
        {
            if (exception.Category == AvatarMediaFailureCategory.Unauthorized)
            {
                _session.NotifyAuthenticatedRequestUnauthorized();
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
            _ = await _imageCache.GetAsync(
                avatar,
                128,
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
                fromCancellation: true).ConfigureAwait(false);
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
            if (!ReferenceEquals(_avatarLease, lease)
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
            if (ReferenceEquals(_avatarLease, lease) && lease.IsCurrent && !IsStoppingUnsafe())
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

    private AccountActionCompletion CompleteMutationSuccess(
        LauncherOperationLease lease,
        AvatarDescriptor? avatar)
    {
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (!ReferenceEquals(_avatarLease, lease) || IsStoppingUnsafe())
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
            snapshot = ReferenceEquals(_avatarLease, lease) && !IsStoppingUnsafe()
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
            snapshot = ReferenceEquals(_avatarLease, lease) && !IsStoppingUnsafe()
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
        if (ReferenceEquals(_avatarLease, lease)
            && _avatarLease.OperationId == lease.OperationId)
        {
            _avatarLease = null;
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
        if (_avatarLease is not null || !_activeRefreshTask.IsCompleted)
        {
            return AccountActionStartResult.Rejected(AccountActionStartStatus.Busy);
        }
        return null;
    }

    private void Session_SnapshotChanged(object? sender, AuthSessionSnapshotEventArgs e)
    {
        AccountRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (IsStoppingUnsafe())
            {
                return;
            }
            snapshot = CreateSessionSnapshot(e.Snapshot, _getCurrentProfile());
            _currentSnapshot = snapshot;
        }

        RaiseSnapshotChanged(snapshot);
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
            ErrorCategory: AccountAvatarErrorCategory.None);
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
