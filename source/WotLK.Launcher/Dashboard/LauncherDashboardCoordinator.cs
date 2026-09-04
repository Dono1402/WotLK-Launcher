using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.Dashboard;

internal sealed class LauncherDashboardCoordinator : ILauncherDashboardRuntime, IDisposable
{
    private readonly object _sync = new();
    private readonly ILauncherAuthService _authentication;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<string> _writeLog;
    private readonly TimeProvider _timeProvider;
    private DashboardSnapshot _currentSnapshot = DashboardSnapshot.Initial;
    private Task? _activeRefreshTask;
    private Task? _initializationTask;
    private long _sequence;
    private long _requestGeneration;
    private bool _authenticatedRequestsEnabled;
    private bool _isShuttingDown;
    private int _disposeState;

    internal LauncherDashboardCoordinator(
        ILauncherAuthService authentication,
        CancellationToken lifetimeToken,
        Action<string> writeLog,
        TimeProvider? timeProvider = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _lifetimeToken = lifetimeToken;
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _authenticatedRequestsEnabled = authentication.Session is not null;
    }

    public event EventHandler? AvailabilityChanged;

    public event EventHandler<DashboardSnapshotEventArgs>? SnapshotChanged;

    public DashboardSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public bool CanRefresh
    {
        get
        {
            lock (_sync)
            {
                return CanRefreshUnsafe();
            }
        }
    }

    internal bool HasActiveRefresh
    {
        get
        {
            lock (_sync)
            {
                return _activeRefreshTask is not null;
            }
        }
    }

    internal Task InitializeAfterSessionRestoreAsync(LauncherSessionRestoreResult restoreResult)
    {
        ArgumentNullException.ThrowIfNull(restoreResult);
        lock (_sync)
        {
            if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
            {
                return Task.CompletedTask;
            }

            return _initializationTask ??= InitializeCoreAsync(restoreResult);
        }
    }

    internal Task RefreshAfterAuthenticationAsync()
    {
        ResumeAuthenticatedRequests();
        return RefreshFromSessionSignalAsync();
    }

    internal void SuspendAuthenticatedRequests()
    {
        lock (_sync)
        {
            _authenticatedRequestsEnabled = false;
            _requestGeneration++;
        }

        RaiseAvailabilityChanged();
    }

    internal void ResumeAuthenticatedRequests()
    {
        lock (_sync)
        {
            if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            _authenticatedRequestsEnabled = _authentication.Session is not null;
            _requestGeneration++;
        }

        RaiseAvailabilityChanged();
    }

    internal void ApplySignedOutSession()
    {
        SuspendAuthenticatedRequests();
        PublishUnavailable(DashboardFailureCategory.NoSession);
    }

    public DashboardRefreshStartStatus TryRefresh()
    {
        TaskCompletionSource completion;
        long requestGeneration;
        lock (_sync)
        {
            if (_isShuttingDown
                || Volatile.Read(ref _disposeState) != 0
                || _lifetimeToken.IsCancellationRequested)
            {
                return DashboardRefreshStartStatus.ShuttingDown;
            }

            if (!_authenticatedRequestsEnabled || _authentication.Session is null)
            {
                return DashboardRefreshStartStatus.NoSession;
            }

            if (_activeRefreshTask is not null)
            {
                return DashboardRefreshStartStatus.Busy;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeRefreshTask = completion.Task;
            requestGeneration = _requestGeneration;
        }

        PublishLoading();
        RaiseAvailabilityChanged();
        _ = ExecuteRefreshAsync(completion, requestGeneration);
        return DashboardRefreshStartStatus.Started;
    }

    internal void BeginShutdown()
    {
        bool changed;
        lock (_sync)
        {
            changed = !_isShuttingDown;
            _isShuttingDown = true;
        }

        if (changed)
        {
            RaiseAvailabilityChanged();
        }
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task? active;
        lock (_sync)
        {
            active = _activeRefreshTask;
        }

        if (active is null)
        {
            return true;
        }

        try
        {
            await active.WaitAsync(timeout).ConfigureAwait(false);
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
        lock (_sync)
        {
            SnapshotChanged = null;
            AvailabilityChanged = null;
        }
    }

    internal static DashboardRealmState MapRealmState(LauncherServerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (string.IsNullOrWhiteSpace(status.Realm) || status.CheckedAt == default)
        {
            return DashboardRealmState.Unavailable;
        }

        if (!status.RealmGateway || !status.WorldGateway || !status.WorldServer)
        {
            return DashboardRealmState.Offline;
        }

        return status.Api && status.Authentication
            ? DashboardRealmState.Online
            : DashboardRealmState.Degraded;
    }

    internal static LauncherNews? SelectLatestPatchNote(IReadOnlyList<LauncherNews> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        return notes.OrderByDescending(note => note.PublishedAt).FirstOrDefault();
    }

    internal static string ExtractPatchNoteVersion(LauncherNews note)
    {
        ArgumentNullException.ThrowIfNull(note);
        const string prefix = "atlas-launcher-";
        if (!note.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string[] parts = note.Id[prefix.Length..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts.All(part => int.TryParse(part, out _))
            ? $"v{parts[0]}.{parts[1]}.{parts[2]}"
            : string.Empty;
    }

    private async Task InitializeCoreAsync(LauncherSessionRestoreResult restoreResult)
    {
        if (restoreResult.Status != LauncherSessionRestoreStatus.Restored
            || restoreResult.Session is null)
        {
            PublishUnavailable(DashboardFailureCategory.NoSession);
            return;
        }

        ResumeAuthenticatedRequests();
        await RefreshFromSessionSignalAsync().ConfigureAwait(false);
    }

    private async Task RefreshFromSessionSignalAsync()
    {
        DashboardRefreshStartStatus status = TryRefresh();
        if (status == DashboardRefreshStartStatus.NoSession)
        {
            PublishUnavailable(DashboardFailureCategory.NoSession);
            return;
        }

        if (status is DashboardRefreshStartStatus.Started or DashboardRefreshStartStatus.Busy)
        {
            Task? active;
            lock (_sync)
            {
                active = _activeRefreshTask;
            }

            if (active is not null)
            {
                await active.ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteRefreshAsync(
        TaskCompletionSource completion,
        long requestGeneration)
    {
        try
        {
            await RefreshCoreAsync(requestGeneration).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _lifetimeToken.IsCancellationRequested || IsStopping())
        {
            // Shutdown owns the cancellation and no terminal dashboard state is published.
        }
        catch (Exception ex)
        {
            DashboardFailureCategory category = ClassifyFailure(ex);
            WriteFailureSafely(category, ex);
            PublishRefreshResult(
                DashboardFetchResult<LauncherServerStatus>.Failure(
                    category),
                DashboardFetchResult<IReadOnlyList<LauncherNews>>.Failure(
                    category),
                requestGeneration);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeRefreshTask, completion.Task))
                {
                    _activeRefreshTask = null;
                }
            }

            completion.TrySetResult();
            RaiseAvailabilityChanged();
        }
    }

    private async Task RefreshCoreAsync(long requestGeneration)
    {
        if (_authentication.Session is null)
        {
            PublishUnavailable(DashboardFailureCategory.NoSession, requestGeneration);
            return;
        }

        try
        {
            bool fresh = await _authentication
                .EnsureFreshAsync(_lifetimeToken)
                .ConfigureAwait(false);
            if (!fresh || _authentication.Session is null)
            {
                PublishUnavailable(DashboardFailureCategory.Unauthorized, requestGeneration);
                return;
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DashboardFailureCategory category = ClassifyFailure(ex);
            WriteFailureSafely(category, ex);
            PublishRefreshResult(
                DashboardFetchResult<LauncherServerStatus>.Failure(category),
                DashboardFetchResult<IReadOnlyList<LauncherNews>>.Failure(category),
                requestGeneration);
            return;
        }

        if (!CanUseRequestGeneration(requestGeneration))
        {
            return;
        }

        Task<DashboardFetchResult<LauncherServerStatus>> statusTask = FetchAsync(
            _authentication.GetStatusAsync,
            "statut du royaume");
        Task<DashboardFetchResult<IReadOnlyList<LauncherNews>>> notesTask = FetchAsync(
            _authentication.GetNewsAsync,
            "notes de mise à jour");
        await Task.WhenAll(statusTask, notesTask).ConfigureAwait(false);
        PublishRefreshResult(await statusTask, await notesTask, requestGeneration);
    }

    private async Task<DashboardFetchResult<T>> FetchAsync<T>(
        Func<CancellationToken, Task<T>> fetch,
        string operationName)
    {
        try
        {
            T value = await fetch(_lifetimeToken).ConfigureAwait(false);
            return DashboardFetchResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DashboardFailureCategory category = ClassifyFailure(ex);
            WriteFailureSafely(category, ex, operationName);
            return DashboardFetchResult<T>.Failure(category);
        }
    }

    private void PublishLoading()
    {
        Publish(previous => previous with
        {
            Sequence = NextSequence(),
            IsLoading = true,
            RealmState = DashboardRealmState.Loading,
            RealmStatusLabel = "Actualisation…",
            FailureCategory = DashboardFailureCategory.None,
            IsStale = false,
            HasRetainedDataAfterFailure = false
        });
    }

    private void PublishUnavailable(
        DashboardFailureCategory category,
        long? requestGeneration = null)
    {
        Publish(previous => previous with
        {
            Sequence = NextSequence(),
            IsLoading = false,
            RealmState = DashboardRealmState.Unavailable,
            RealmStatusLabel = "Statut indisponible",
            FailureCategory = category,
            IsStale = previous.HasPatchNote || previous.LastKnownRealmState is not null,
            HasRetainedDataAfterFailure = previous.HasPatchNote
                || previous.LastKnownRealmState is not null
        }, requestGeneration);
    }

    private void PublishRefreshResult(
        DashboardFetchResult<LauncherServerStatus> statusResult,
        DashboardFetchResult<IReadOnlyList<LauncherNews>> notesResult,
        long requestGeneration)
    {
        if (IsStopping())
        {
            return;
        }

        Publish(previous =>
        {
            bool statusSucceeded = statusResult.IsSuccess && statusResult.Value is not null;
            DashboardRealmState realmState = statusSucceeded
                ? MapRealmState(statusResult.Value!)
                : DashboardRealmState.Unavailable;
            if (realmState == DashboardRealmState.Unavailable)
            {
                statusSucceeded = false;
            }

            string realmLabel = statusSucceeded
                ? GetRealmLabel(realmState)
                : "Statut indisponible";

            bool notesSucceeded = notesResult.IsSuccess && notesResult.Value is not null;
            ImmutableArray<LauncherNews> patchNotes = notesSucceeded
                ? notesResult.Value!
                    .OrderByDescending(note => note.PublishedAt)
                    .ToImmutableArray()
                : previous.PatchNotes;
            LauncherNews? latest = notesSucceeded ? patchNotes.FirstOrDefault() : null;
            DashboardFailureCategory failure = FirstFailure(statusResult, notesResult);
            if (!statusSucceeded && failure == DashboardFailureCategory.None)
            {
                failure = DashboardFailureCategory.InvalidResponse;
            }

            bool retainedRealm = !statusSucceeded && previous.LastKnownRealmState is not null;
            bool retainedNote = !notesSucceeded && previous.HasPatchNote;
            bool completeSuccess = statusSucceeded && notesSucceeded;

            return previous with
            {
                Sequence = NextSequence(),
                IsLoading = false,
                RealmState = realmState,
                RealmStatusLabel = realmLabel,
                LastSuccessfulRefreshAt = completeSuccess
                    ? _timeProvider.GetUtcNow()
                    : previous.LastSuccessfulRefreshAt,
                FailureCategory = completeSuccess
                    ? DashboardFailureCategory.None
                    : failure,
                PatchNotes = patchNotes,
                LatestPatchNoteId = notesSucceeded ? latest?.Id : previous.LatestPatchNoteId,
                LatestPatchNoteCategory = notesSucceeded ? latest?.Category : previous.LatestPatchNoteCategory,
                LatestPatchNoteTitle = notesSucceeded ? latest?.Title ?? string.Empty : previous.LatestPatchNoteTitle,
                LatestPatchNoteSummary = notesSucceeded ? latest?.Summary ?? string.Empty : previous.LatestPatchNoteSummary,
                LatestPatchNoteVersion = notesSucceeded && latest is not null
                    ? ExtractPatchNoteVersion(latest)
                    : notesSucceeded
                        ? string.Empty
                        : previous.LatestPatchNoteVersion,
                LatestPatchNoteDate = notesSucceeded ? latest?.PublishedAt : previous.LatestPatchNoteDate,
                HasPatchNote = notesSucceeded ? latest is not null : previous.HasPatchNote,
                IsStale = retainedRealm || retainedNote,
                HasRetainedDataAfterFailure = retainedRealm || retainedNote,
                LastKnownRealmState = statusSucceeded ? realmState : previous.LastKnownRealmState,
                LastKnownRealmStatusLabel = statusSucceeded ? realmLabel : previous.LastKnownRealmStatusLabel
            };
        }, requestGeneration);
    }

    private void Publish(
        Func<DashboardSnapshot, DashboardSnapshot> update,
        long? requestGeneration = null)
    {
        lock (_sync)
        {
            if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            if (requestGeneration is long expected
                && (!_authenticatedRequestsEnabled || expected != _requestGeneration))
            {
                return;
            }

            _currentSnapshot = update(_currentSnapshot);
            SnapshotChanged?.Invoke(this, new DashboardSnapshotEventArgs(_currentSnapshot));
        }
    }

    private bool CanRefreshUnsafe()
    {
        return !_isShuttingDown
            && Volatile.Read(ref _disposeState) == 0
            && !_lifetimeToken.IsCancellationRequested
            && _authenticatedRequestsEnabled
            && _authentication.Session is not null
            && _activeRefreshTask is null;
    }

    private bool IsStopping()
    {
        lock (_sync)
        {
            return _isShuttingDown
                || Volatile.Read(ref _disposeState) != 0
                || _lifetimeToken.IsCancellationRequested;
        }
    }

    private bool CanUseRequestGeneration(long requestGeneration)
    {
        lock (_sync)
        {
            return !_isShuttingDown
                && Volatile.Read(ref _disposeState) == 0
                && _authenticatedRequestsEnabled
                && _authentication.Session is not null
                && requestGeneration == _requestGeneration;
        }
    }

    private long NextSequence()
    {
        return ++_sequence;
    }

    private void RaiseAvailabilityChanged()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) == 0)
            {
                AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void WriteFailureSafely(
        DashboardFailureCategory category,
        Exception exception,
        string operationName = "dashboard")
    {
        try
        {
            _writeLog(
                $"Actualisation V2 indisponible: operation={operationName}; "
                + $"category={category}; exception={exception.GetType().Name}.");
        }
        catch
        {
            // Diagnostics must never fault the observed refresh task.
        }
    }

    private static DashboardFailureCategory ClassifyFailure(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => DashboardFailureCategory.Timeout,
            TimeoutException => DashboardFailureCategory.Timeout,
            HttpRequestException => DashboardFailureCategory.Network,
            LauncherAuthException { StatusCode: HttpStatusCode.Unauthorized } =>
                DashboardFailureCategory.Unauthorized,
            LauncherAuthException authException when authException.Message.Contains(
                "invalide",
                StringComparison.OrdinalIgnoreCase) => DashboardFailureCategory.InvalidResponse,
            LauncherAuthException => DashboardFailureCategory.ServiceUnavailable,
            JsonException or NotSupportedException => DashboardFailureCategory.InvalidResponse,
            _ => DashboardFailureCategory.Unexpected
        };
    }

    private static DashboardFailureCategory FirstFailure(
        DashboardFetchResult<LauncherServerStatus> status,
        DashboardFetchResult<IReadOnlyList<LauncherNews>> notes)
    {
        return !status.IsSuccess
            ? status.FailureCategory
            : !notes.IsSuccess
                ? notes.FailureCategory
                : DashboardFailureCategory.None;
    }

    private static string GetRealmLabel(DashboardRealmState state)
    {
        return state switch
        {
            DashboardRealmState.Online => "En ligne",
            DashboardRealmState.Degraded => "Services dégradés",
            DashboardRealmState.Offline => "Hors ligne",
            DashboardRealmState.Loading => "Actualisation…",
            DashboardRealmState.Unavailable => "Statut indisponible",
            _ => "Non vérifié"
        };
    }

    private readonly record struct DashboardFetchResult<T>(
        bool IsSuccess,
        T? Value,
        DashboardFailureCategory FailureCategory)
    {
        internal static DashboardFetchResult<T> Success(T value) =>
            new(true, value, DashboardFailureCategory.None);

        internal static DashboardFetchResult<T> Failure(DashboardFailureCategory category) =>
            new(false, default, category);
    }
}
