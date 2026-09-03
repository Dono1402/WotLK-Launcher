using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using WotLK.Launcher.Game;
using WotLK.Launcher.Updater;

namespace WotLK.Launcher.Runtime;

internal enum LauncherSelfUpdatePhase
{
    None,
    Downloading,
    Validating,
    WaitingForApply,
    Restarting
}

internal enum LauncherSelfUpdateErrorCategory
{
    ManifestUnavailable,
    ManifestInvalid,
    ManifestTransportRejected,
    ManifestSignatureInvalid,
    ManifestUnsupported,
    NoUpdate,
    DownloadFailed,
    CandidateInvalid,
    PackageIntegrityFailed,
    ApplyUnavailable,
    PermissionDenied,
    ReplacementFailed,
    RestartFailed
}

internal enum LauncherSelfUpdateCheckOutcome
{
    Completed,
    NoUpdate,
    Suppressed,
    ShuttingDown,
    Failed
}

internal enum LauncherSelfUpdateStartStatus
{
    Started,
    Busy,
    ShuttingDown,
    NoUpdate,
    RejectedByCompatibility
}

internal sealed record LauncherSelfUpdateSnapshot(
    long Sequence,
    bool IsChecking,
    string InstalledVersion,
    string? AvailableVersion,
    bool IsUpdateAvailable,
    bool IsUpdating,
    LauncherSelfUpdatePhase Phase,
    double? Percent,
    long? BytesProcessed,
    long? BytesTotal,
    double? Speed,
    TimeSpan? Eta,
    bool CanUserCancel,
    LauncherSelfUpdateErrorCategory? ErrorCategory,
    DateTimeOffset? LastCheckedAt);

internal sealed class LauncherSelfUpdateSnapshotEventArgs(
    LauncherSelfUpdateSnapshot snapshot) : EventArgs
{
    internal LauncherSelfUpdateSnapshot Snapshot { get; } = snapshot;
}

internal sealed class LauncherSelfUpdateTerminalEventArgs(
    OperationTerminalResult terminalResult) : EventArgs
{
    internal OperationTerminalResult TerminalResult { get; } = terminalResult;
}

internal readonly record struct LauncherSelfUpdateCheckResult(
    LauncherSelfUpdateCheckOutcome Outcome,
    LauncherSelfUpdateErrorCategory? ErrorCategory = null);

internal sealed record LauncherSelfUpdateCompletion(
    LauncherOperationOutcome Outcome,
    LauncherSelfUpdateErrorCategory? ErrorCategory = null);

internal readonly record struct LauncherSelfUpdateStartResult(
    LauncherSelfUpdateStartStatus Status,
    Task<LauncherSelfUpdateCompletion>? Completion)
{
    internal bool IsStarted => Status == LauncherSelfUpdateStartStatus.Started
        && Completion is not null;
}

internal sealed record LauncherSelfUpdateTransferProgress(
    long BytesProcessed,
    long? BytesTotal,
    double? Percent,
    double? BytesPerSecond,
    TimeSpan? Eta);

internal interface ILauncherSelfUpdateTimer
{
    event EventHandler? Tick;

    TimeSpan Interval { get; }

    bool IsEnabled { get; }

    void Start();

    void Stop();
}

internal sealed class DispatcherLauncherSelfUpdateTimer : ILauncherSelfUpdateTimer
{
    private readonly DispatcherTimer _timer;

    internal DispatcherLauncherSelfUpdateTimer(TimeSpan interval)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval
        };
    }

    public event EventHandler? Tick
    {
        add => _timer.Tick += value;
        remove => _timer.Tick -= value;
    }

    public TimeSpan Interval => _timer.Interval;

    public bool IsEnabled => _timer.IsEnabled;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}

internal interface ILauncherSelfUpdateClient
{
    Task<LauncherUpdateManifest> LoadManifestAsync(CancellationToken cancellationToken);

    Task DownloadAsync(
        Uri uri,
        string targetPath,
        long expectedSize,
        Action<LauncherSelfUpdateTransferProgress> reportProgress,
        CancellationToken cancellationToken);
}

internal sealed class LauncherSelfUpdateHttpClient : ILauncherSelfUpdateClient, IDisposable
{
    internal static readonly Uri ManifestUri = LauncherUpdateSecurityConstants.ManifestUri;
    private const string UpdateRequestHeader = "X-WotLK-Launcher-Update";
    private const string UpdateRequestMarker = "1";
    private readonly HttpClient _httpClient;
    private readonly ILauncherUpdateManifestVerifier _manifestVerifier;
    private readonly bool _ownsHttpClient;
    private int _disposeState;

    internal LauncherSelfUpdateHttpClient(
        HttpClient httpClient,
        ILauncherUpdateManifestVerifier manifestVerifier,
        bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _manifestVerifier = manifestVerifier
            ?? throw new ArgumentNullException(nameof(manifestVerifier));
        _ownsHttpClient = ownsHttpClient;
    }

    internal static LauncherSelfUpdateHttpClient CreateProduction()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli
        };
        HttpClient client = new(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        return new LauncherSelfUpdateHttpClient(
            client,
            new LauncherUpdateManifestVerifier(
                LauncherUpdateTrustStore.LoadEmbeddedProduction()),
            ownsHttpClient: true);
    }

    public async Task<LauncherUpdateManifest> LoadManifestAsync(
        CancellationToken cancellationToken)
    {
        LauncherUpdateUriPolicy.RequireManifestUri(ManifestUri);
        using HttpRequestMessage request = new(HttpMethod.Get, ManifestUri);
        using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        RequireExpectedResponse(response, ManifestUri, manifest: true);
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > LauncherUpdateSecurityConstants.MaximumManifestBytes)
        {
            throw new LauncherUpdateManifestUnsupportedException();
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] payload = await ReadBoundedAsync(
                stream,
                LauncherUpdateSecurityConstants.MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        LauncherUpdateManifest manifest = LauncherUpdateManifestJson.ParseStrict(payload);
        _manifestVerifier.Verify(manifest);
        return manifest;
    }

    public async Task DownloadAsync(
        Uri uri,
        string targetPath,
        long expectedSize,
        Action<LauncherSelfUpdateTransferProgress> reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(reportProgress);
        LauncherUpdateUriPolicy.RequirePackageUri(uri);
        if (expectedSize <= 0
            || expectedSize > LauncherUpdateSecurityConstants.MaximumPackageBytes)
        {
            throw new LauncherUpdatePackageIntegrityException();
        }

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(UpdateRequestHeader, UpdateRequestMarker);
        using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        RequireExpectedResponse(response, uri, manifest: false);

        long? responseSize = response.Content.Headers.ContentLength;
        if (responseSize is long declaredSize && declaredSize != expectedSize)
        {
            throw new LauncherUpdatePackageIntegrityException();
        }
        long? totalSize = expectedSize;
        await using Stream remote = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using FileStream local = new(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        byte[] buffer = new byte[128 * 1024];
        long written = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (true)
        {
            int read = await remote.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (written > expectedSize - read)
            {
                throw new LauncherUpdatePackageIntegrityException();
            }

            await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            written += read;
            reportProgress(CreateProgress(written, totalSize, stopwatch.Elapsed));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (written != expectedSize)
        {
            throw new LauncherUpdatePackageIntegrityException();
        }
        reportProgress(CreateProgress(written, totalSize, stopwatch.Elapsed, completed: true));
    }

    internal static Uri BuildDownloadUri(string url, string version)
    {
        return LauncherUpdateUriPolicy.RequirePackageUri(url, version);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0 && _ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static void RequireExpectedResponse(
        HttpResponseMessage response,
        Uri expectedUri,
        bool manifest)
    {
        int statusCode = (int)response.StatusCode;
        if (statusCode is >= 300 and <= 399)
        {
            throw new LauncherUpdateManifestTransportException();
        }

        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri != expectedUri)
        {
            throw new LauncherUpdateManifestTransportException();
        }

        if (manifest)
        {
            LauncherUpdateUriPolicy.RequireManifestUri(finalUri);
        }
        else
        {
            LauncherUpdateUriPolicy.RequirePackageUri(finalUri);
        }
        response.EnsureSuccessStatusCode();
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new(capacity: maximumBytes);
        byte[] chunk = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length > maximumBytes - read)
            {
                throw new LauncherUpdateManifestUnsupportedException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static LauncherSelfUpdateTransferProgress CreateProgress(
        long received,
        long? total,
        TimeSpan elapsed,
        bool completed = false)
    {
        double? percent = total is > 0
            ? Math.Clamp((double)received / total.Value * 100, 0, 100)
            : null;
        if (completed && total is > 0)
        {
            percent = 100;
        }

        double? speed = received > 0 && elapsed.TotalSeconds >= 0.5
            ? received / elapsed.TotalSeconds
            : null;
        TimeSpan? eta = speed is > 0 && total is > 0 && total.Value > received
            ? TimeSpan.FromSeconds((total.Value - received) / speed.Value)
            : null;
        return new LauncherSelfUpdateTransferProgress(
            received,
            total,
            percent,
            speed,
            eta);
    }
}

internal interface ILauncherSelfUpdateRuntime
{
    event EventHandler<LauncherSelfUpdateSnapshotEventArgs>? SnapshotChanged;

    event EventHandler? AvailabilityChanged;

    LauncherSelfUpdateSnapshot CurrentSnapshot { get; }

    long? CurrentOperationId { get; }

    bool CanCheck { get; }

    bool CanStartUpdate { get; }

    Task<LauncherSelfUpdateCheckResult> CheckAsync();

    LauncherSelfUpdateStartResult TryStartUpdate();
}

internal sealed class LauncherSelfUpdateCoordinator : ILauncherSelfUpdateRuntime, IDisposable
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(80);

    private readonly object _sync = new();
    private readonly LauncherOperationCoordinator _operations;
    private readonly ILauncherSelfUpdateClient _client;
    private readonly ILauncherSelfUpdateFinalizer _finalizer;
    private readonly ILauncherSelfUpdateTimer _timer;
    private readonly Func<string?> _getExecutablePath;
    private readonly Func<string> _createDownloadDirectory;
    private readonly Func<int> _getProcessId;
    private readonly Func<DateTimeOffset> _getNow;
    private readonly Func<string, CancellationToken, Task<string>> _computeSha256;
    private readonly Action<string> _writeLog;
    private readonly Action _requestShutdown;
    private readonly CancellationTokenSource _lifetimeCancellation;
    private readonly bool _automaticRetrySuppressedForCurrentLaunch;
    private LauncherSelfUpdateSnapshot _currentSnapshot;
    private LauncherUpdateManifest? _availableManifest;
    private Task<LauncherSelfUpdateCheckResult>? _activeCheck;
    private Task<LauncherSelfUpdateCompletion>? _activeUpdate;
    private LauncherOperationLease? _activeOperationLease;
    private long? _currentOperationId;
    private string? _announcedUpdateHash;
    private DateTimeOffset _lastProgressPublishedAt = DateTimeOffset.MinValue;
    private long _sequence;
    private bool _automaticChecksEnabled;
    private bool _timerRunning;
    private bool _updateStarting;
    private bool _isShuttingDown;
    private int _disposeState;

    internal LauncherSelfUpdateCoordinator(
        LauncherOperationCoordinator operations,
        ILauncherSelfUpdateClient client,
        ILauncherSelfUpdateFinalizer finalizer,
        ILauncherSelfUpdateTimer timer,
        bool automaticChecksEnabled,
        string installedVersion,
        bool selfUpdateRecoveryOccurred,
        Func<string?>? getExecutablePath = null,
        Func<string>? createDownloadDirectory = null,
        Func<int>? getProcessId = null,
        Func<DateTimeOffset>? getNow = null,
        Func<string, CancellationToken, Task<string>>? computeSha256 = null,
        Action<string>? writeLog = null,
        Action? requestShutdown = null)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _finalizer = finalizer ?? throw new ArgumentNullException(nameof(finalizer));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        if (_timer.Interval != CheckInterval)
        {
            throw new ArgumentException("Le timer self-update doit utiliser exactement 30 secondes.", nameof(timer));
        }

        _automaticChecksEnabled = automaticChecksEnabled;
        _automaticRetrySuppressedForCurrentLaunch = selfUpdateRecoveryOccurred;
        _getExecutablePath = getExecutablePath ?? (() => Environment.ProcessPath);
        _createDownloadDirectory = createDownloadDirectory ?? CreateProductionDownloadDirectory;
        _getProcessId = getProcessId ?? (() => Environment.ProcessId);
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
        _computeSha256 = computeSha256 ?? GameFileVerifier.ComputeSha256Async;
        _writeLog = writeLog ?? (static _ => { });
        _requestShutdown = requestShutdown ?? (static () => { });
        _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _operations.ShutdownToken);
        _currentSnapshot = new LauncherSelfUpdateSnapshot(
            Sequence: ++_sequence,
            IsChecking: false,
            InstalledVersion: installedVersion ?? throw new ArgumentNullException(nameof(installedVersion)),
            AvailableVersion: null,
            IsUpdateAvailable: false,
            IsUpdating: false,
            Phase: LauncherSelfUpdatePhase.None,
            Percent: null,
            BytesProcessed: null,
            BytesTotal: null,
            Speed: null,
            Eta: null,
            CanUserCancel: false,
            ErrorCategory: null,
            LastCheckedAt: null);
        _timer.Tick += Timer_Tick;
        _operations.StateChanged += Operations_StateChanged;
        _operations.ActivityChanged += Operations_ActivityChanged;
    }

    internal event EventHandler? PeriodicTickStarted;

    internal event EventHandler? PeriodicCheckCompleted;

    internal event EventHandler<LauncherSelfUpdateTerminalEventArgs>? OperationTerminated;

    public event EventHandler<LauncherSelfUpdateSnapshotEventArgs>? SnapshotChanged;

    public event EventHandler? AvailabilityChanged;

    public LauncherSelfUpdateSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public long? CurrentOperationId
    {
        get
        {
            lock (_sync)
            {
                return _currentOperationId;
            }
        }
    }

    internal bool AutomaticChecksEnabled
    {
        get
        {
            lock (_sync)
            {
                return _automaticChecksEnabled;
            }
        }
    }

    internal bool IsAutomaticRetrySuppressed => _automaticRetrySuppressedForCurrentLaunch;

    internal bool IsIdle
    {
        get
        {
            lock (_sync)
            {
                return _activeCheck is null && _activeUpdate is null && !_updateStarting;
            }
        }
    }

    public bool CanCheck
    {
        get
        {
            lock (_sync)
            {
                return !_isShuttingDown
                    && Volatile.Read(ref _disposeState) == 0
                    && _activeCheck is null
                    && _activeUpdate is null
                    && !_updateStarting;
            }
        }
    }

    public bool CanStartUpdate
    {
        get
        {
            lock (_sync)
            {
                return !_isShuttingDown
                    && Volatile.Read(ref _disposeState) == 0
                    && _availableManifest is not null
                    && _activeCheck is null
                    && _activeUpdate is null
                    && !_updateStarting
                    && _operations.CanBegin(LauncherOperationKind.LauncherAutoUpdate);
            }
        }
    }

    internal void ScheduleInitialCheck()
    {
        lock (_sync)
        {
            if (!_automaticChecksEnabled
                || _automaticRetrySuppressedForCurrentLaunch
                || _isShuttingDown
                || Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }
        }

        _ = CheckAsync();
    }

    internal void StartPeriodicChecks()
    {
        bool start;
        lock (_sync)
        {
            start = _automaticChecksEnabled
                && !_automaticRetrySuppressedForCurrentLaunch
                && !_isShuttingDown
                && Volatile.Read(ref _disposeState) == 0
                && !_timerRunning;
            if (start)
            {
                _timerRunning = true;
            }
        }

        if (start)
        {
            _timer.Start();
        }
    }

    internal void SetAutomaticChecksEnabled(bool enabled)
    {
        bool stop = false;
        bool start = false;
        lock (_sync)
        {
            _automaticChecksEnabled = enabled;
            if (!enabled && _timerRunning)
            {
                _timerRunning = false;
                stop = true;
            }
            else if (enabled
                     && !_automaticRetrySuppressedForCurrentLaunch
                     && !_timerRunning
                     && !_isShuttingDown
                     && Volatile.Read(ref _disposeState) == 0)
            {
                _timerRunning = true;
                start = true;
            }
        }

        if (stop)
        {
            _timer.Stop();
        }
        if (start)
        {
            _timer.Start();
            _ = CheckAsync();
        }

        RaiseAvailabilityChanged();
    }

    public Task<LauncherSelfUpdateCheckResult> CheckAsync()
    {
        TaskCompletionSource<LauncherSelfUpdateCheckResult>? completion = null;
        LauncherSelfUpdateSnapshot? checkingSnapshot = null;
        lock (_sync)
        {
            if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
            {
                return Task.FromResult(new LauncherSelfUpdateCheckResult(
                    LauncherSelfUpdateCheckOutcome.ShuttingDown));
            }

            if (_activeUpdate is not null || _updateStarting)
            {
                return Task.FromResult(new LauncherSelfUpdateCheckResult(
                    LauncherSelfUpdateCheckOutcome.Suppressed));
            }

            if (_activeCheck is not null)
            {
                return _activeCheck;
            }

            completion = new TaskCompletionSource<LauncherSelfUpdateCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activeCheck = completion.Task;
            checkingSnapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
            {
                IsChecking = true,
                ErrorCategory = null
            });
        }

        Publish(checkingSnapshot);
        _ = RunCheckAsync(completion);
        return completion.Task;
    }

    public LauncherSelfUpdateStartResult TryStartUpdate()
    {
        LauncherUpdateManifest? manifest;
        lock (_sync)
        {
            if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
            {
                return new LauncherSelfUpdateStartResult(
                    LauncherSelfUpdateStartStatus.ShuttingDown,
                    null);
            }
            if (_activeCheck is not null || _activeUpdate is not null || _updateStarting)
            {
                return new LauncherSelfUpdateStartResult(
                    LauncherSelfUpdateStartStatus.Busy,
                    null);
            }
            if (_availableManifest is null)
            {
                return new LauncherSelfUpdateStartResult(
                    LauncherSelfUpdateStartStatus.NoUpdate,
                    null);
            }

            manifest = _availableManifest;
            _updateStarting = true;
        }

        LauncherOperationStartResult operationStart = _operations.TryBegin(
            LauncherOperationKind.LauncherAutoUpdate,
            canUserCancel: true,
            operationType: LauncherOperationType.LauncherAutoUpdate);
        if (!operationStart.IsStarted)
        {
            lock (_sync)
            {
                _updateStarting = false;
            }
            RaiseAvailabilityChanged();
            return new LauncherSelfUpdateStartResult(
                operationStart.Status == LauncherOperationStartStatus.ShuttingDown
                    ? LauncherSelfUpdateStartStatus.ShuttingDown
                    : operationStart.Status == LauncherOperationStartStatus.RejectedByCompatibility
                        ? LauncherSelfUpdateStartStatus.RejectedByCompatibility
                        : LauncherSelfUpdateStartStatus.Busy,
                null);
        }

        LauncherOperationLease lease = operationStart.Lease!;
        TaskCompletionSource<LauncherSelfUpdateCompletion> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        LauncherSelfUpdateSnapshot startedSnapshot;
        lock (_sync)
        {
            if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
            {
                _updateStarting = false;
                lease.CancelForShutdown();
                lease.Complete();
                return new LauncherSelfUpdateStartResult(
                    LauncherSelfUpdateStartStatus.ShuttingDown,
                    null);
            }

            _updateStarting = false;
            _activeUpdate = completion.Task;
            _activeOperationLease = lease;
            _currentOperationId = lease.OperationId;
            _lastProgressPublishedAt = DateTimeOffset.MinValue;
            startedSnapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
            {
                IsUpdating = true,
                Phase = LauncherSelfUpdatePhase.Downloading,
                Percent = 0,
                BytesProcessed = 0,
                BytesTotal = manifest.Size > 0 ? manifest.Size : null,
                Speed = null,
                Eta = null,
                CanUserCancel = lease.CanUserCancel,
                ErrorCategory = null
            });
        }

        Publish(startedSnapshot);
        WriteLogSafely("Téléchargement de la mise à jour launcher...");
        _ = RunUpdateAsync(manifest, lease, completion);
        return new LauncherSelfUpdateStartResult(
            LauncherSelfUpdateStartStatus.Started,
            completion.Task);
    }

    internal bool CancelFromUser()
    {
        LauncherOperationLease activeOperation;
        lock (_sync)
        {
            LauncherOperationLease? candidate = _activeOperationLease;
            if (_currentOperationId is not long operationId
                || candidate is null
                || candidate.OperationId != operationId)
            {
                return false;
            }
            activeOperation = candidate;
        }

        bool cancelled = activeOperation.CancelFromUser();
        return cancelled;
    }

    internal void BeginShutdown()
    {
        LauncherOperationLease? activeOperation;
        lock (_sync)
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            _timerRunning = false;
            activeOperation = _activeOperationLease;
        }

        _timer.Stop();
        activeOperation?.CancelForShutdown();
        try
        {
            _lifetimeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        RaiseAvailabilityChanged();
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        DateTimeOffset deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            Task[] tasks;
            lock (_sync)
            {
                tasks = new Task?[] { _activeCheck, _activeUpdate }
                    .Where(task => task is not null)
                    .Cast<Task>()
                    .ToArray();
            }
            if (tasks.Length == 0)
            {
                return true;
            }

            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            try
            {
                await Task.WhenAll(tasks).WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        BeginShutdown();
        _timer.Tick -= Timer_Tick;
        _operations.StateChanged -= Operations_StateChanged;
        _operations.ActivityChanged -= Operations_ActivityChanged;
        SnapshotChanged = null;
        AvailabilityChanged = null;
        PeriodicTickStarted = null;
        PeriodicCheckCompleted = null;
        OperationTerminated = null;
        _lifetimeCancellation.Dispose();
        if (_client is IDisposable disposableClient)
        {
            disposableClient.Dispose();
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        bool shouldRun;
        lock (_sync)
        {
            shouldRun = _automaticChecksEnabled
                && !_automaticRetrySuppressedForCurrentLaunch
                && !_isShuttingDown
                && Volatile.Read(ref _disposeState) == 0;
        }
        if (!shouldRun)
        {
            return;
        }

        RaiseSimpleEvent(PeriodicTickStarted);
        try
        {
            await CheckAsync().ConfigureAwait(true);
        }
        catch
        {
            // CheckAsync always converts failures to a controlled result.
        }
        RaiseSimpleEvent(PeriodicCheckCompleted);
    }

    private void Operations_StateChanged(object? sender, EventArgs e)
    {
        RaiseAvailabilityChanged();
    }

    private void Operations_ActivityChanged(
        object? sender,
        LauncherOperationActivitySnapshotEventArgs eventArgs)
    {
        LauncherSelfUpdateSnapshot? snapshot = null;
        lock (_sync)
        {
            LauncherOperationActivitySnapshot activity = eventArgs.Snapshot;
            if (_currentOperationId is long operationId
                && activity.OperationId == operationId
                && _currentSnapshot.IsUpdating)
            {
                bool canUserCancel = activity.CanUserCancel
                    && activity.CancellationReason == LauncherOperationCancellationReason.None;
                if (_currentSnapshot.CanUserCancel != canUserCancel)
                {
                    snapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
                    {
                        CanUserCancel = canUserCancel
                    });
                }
            }
        }

        Publish(snapshot);
    }

    private async Task RunCheckAsync(
        TaskCompletionSource<LauncherSelfUpdateCheckResult> completion)
    {
        LauncherSelfUpdateCheckResult result;
        LauncherSelfUpdateSnapshot finalSnapshot;
        try
        {
            CancellationToken token = _lifetimeCancellation.Token;
            LauncherUpdateManifest manifest = await _client.LoadManifestAsync(token)
                .ConfigureAwait(false);
            ValidateManifestForComparison(manifest);
            string executable = RequireExecutablePath();
            string currentHash = await _computeSha256(executable, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            bool available = !string.Equals(
                    currentHash,
                    manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                && IsManifestVersionEligible(manifest.Version, _currentSnapshot.InstalledVersion);
            lock (_sync)
            {
                if (!ReferenceEquals(_activeCheck, completion.Task))
                {
                    completion.TrySetResult(new LauncherSelfUpdateCheckResult(
                        LauncherSelfUpdateCheckOutcome.Suppressed));
                    return;
                }

                _availableManifest = available ? manifest : null;
                if (available)
                {
                    if (!string.Equals(
                            _announcedUpdateHash,
                            manifest.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _announcedUpdateHash = manifest.Sha256;
                        WriteLogSafely(string.IsNullOrWhiteSpace(manifest.Version)
                            ? "Mise a jour launcher disponible."
                            : "Mise a jour launcher disponible: " + manifest.Version);
                    }
                }
                else
                {
                    _announcedUpdateHash = null;
                }

                finalSnapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
                {
                    IsChecking = false,
                    AvailableVersion = available && !string.IsNullOrWhiteSpace(manifest.Version)
                        ? manifest.Version
                        : null,
                    IsUpdateAvailable = available,
                    ErrorCategory = null,
                    LastCheckedAt = _getNow()
                });
                _activeCheck = null;
            }

            result = new LauncherSelfUpdateCheckResult(
                available
                    ? LauncherSelfUpdateCheckOutcome.Completed
                    : LauncherSelfUpdateCheckOutcome.NoUpdate,
                available ? null : LauncherSelfUpdateErrorCategory.NoUpdate);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            lock (_sync)
            {
                _activeCheck = null;
                finalSnapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
                {
                    IsChecking = false
                });
            }
            result = new LauncherSelfUpdateCheckResult(
                LauncherSelfUpdateCheckOutcome.ShuttingDown);
        }
        catch (Exception exception)
        {
            LauncherSelfUpdateErrorCategory category = ClassifyCheckFailure(exception);
            lock (_sync)
            {
                _activeCheck = null;
                finalSnapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
                {
                    IsChecking = false,
                    ErrorCategory = category
                });
            }
            WriteLogSafely("Verification launcher ignoree: " + category + ".");
            result = new LauncherSelfUpdateCheckResult(
                LauncherSelfUpdateCheckOutcome.Failed,
                category);
        }

        Publish(finalSnapshot);
        completion.TrySetResult(result);
    }

    private async Task RunUpdateAsync(
        LauncherUpdateManifest manifest,
        LauncherOperationLease operation,
        TaskCompletionSource<LauncherSelfUpdateCompletion> completion)
    {
        LauncherSelfUpdateCompletion result;
        LauncherSelfUpdateErrorCategory? failureCategory = null;
        LauncherOperationOutcome outcome = LauncherOperationOutcome.Failed;
        string? updateDirectory = null;
        LauncherSelfUpdatePhase failurePhase = LauncherSelfUpdatePhase.Downloading;
        try
        {
            CancellationToken token = operation.CancellationToken;
            string executable = RequireExecutablePath();
            ValidateManifestForDownload(manifest);
            updateDirectory = _createDownloadDirectory();
            Directory.CreateDirectory(updateDirectory);
            string downloadedExecutable = Path.Combine(
                updateDirectory,
                Path.GetFileName(executable));
            Uri downloadUri = LauncherSelfUpdateHttpClient.BuildDownloadUri(
                manifest.Url,
                manifest.Version);

            await _client.DownloadAsync(
                    downloadUri,
                    downloadedExecutable,
                    manifest.Size,
                    progress => ApplyProgress(operation.OperationId, progress),
                    token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            failurePhase = LauncherSelfUpdatePhase.Validating;
            PublishPhase(operation, LauncherSelfUpdatePhase.Validating, canUserCancel: true);
            await LauncherUpdatePackageIntegrity.ValidateAsync(
                    downloadedExecutable,
                    manifest,
                    _computeSha256,
                    token)
                .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            operation.DisableUserCancellation();
            failurePhase = LauncherSelfUpdatePhase.WaitingForApply;
            PublishPhase(operation, LauncherSelfUpdatePhase.WaitingForApply, canUserCancel: false);
            await _finalizer.PrepareAndLaunchAsync(
                    executable,
                    downloadedExecutable,
                    manifest.Size,
                    manifest.Sha256,
                    _getProcessId(),
                    token)
                .ConfigureAwait(false);

            failurePhase = LauncherSelfUpdatePhase.Restarting;
            PublishPhase(operation, LauncherSelfUpdatePhase.Restarting, canUserCancel: false);
            WriteLogSafely(
                "Application de la mise à jour. Une validation administrateur peut être demandée.");
            try
            {
                _requestShutdown();
            }
            catch (Exception)
            {
                throw new LauncherSelfUpdateRestartException();
            }

            outcome = LauncherOperationOutcome.Succeeded;
            result = new LauncherSelfUpdateCompletion(outcome);
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            outcome = LauncherOperationOutcome.Cancelled;
            result = new LauncherSelfUpdateCompletion(outcome);
            WriteLogSafely("Mise à jour du launcher annulée.");
        }
        catch (Exception exception)
        {
            failureCategory = ClassifyUpdateFailure(exception, failurePhase);
            result = new LauncherSelfUpdateCompletion(
                LauncherOperationOutcome.Failed,
                failureCategory);
            WriteLogSafely("Erreur mise à jour launcher: " + failureCategory + ".");
        }
        finally
        {
            if (outcome != LauncherOperationOutcome.Succeeded && updateDirectory is not null)
            {
                LauncherUpdateTransactionStore.TryDeleteDirectory(updateDirectory);
            }

            OperationTerminalResult terminal = new(
                operation.OperationId,
                LauncherOperationType.LauncherAutoUpdate,
                outcome,
                _getNow(),
                operation.CancellationReason,
                failureCategory?.ToString(),
                new LauncherOperationDisplayContext(
                    "atlas-launcher",
                    "Atlas Launcher"));
            RaiseTerminal(terminal);
            operation.Complete();

            LauncherSelfUpdateSnapshot stableSnapshot;
            lock (_sync)
            {
                _activeUpdate = null;
                if (_activeOperationLease?.OperationId == operation.OperationId)
                {
                    _activeOperationLease = null;
                }
                _currentOperationId = null;
                stableSnapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
                {
                    IsUpdating = false,
                    Phase = LauncherSelfUpdatePhase.None,
                    Percent = null,
                    BytesProcessed = null,
                    BytesTotal = null,
                    Speed = null,
                    Eta = null,
                    CanUserCancel = false,
                    ErrorCategory = failureCategory
                });
            }
            Publish(stableSnapshot);
        }

        completion.TrySetResult(result);
    }

    private void ApplyProgress(long operationId, LauncherSelfUpdateTransferProgress progress)
    {
        LauncherSelfUpdateSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_currentOperationId != operationId
                || !_currentSnapshot.IsUpdating
                || _currentSnapshot.Phase != LauncherSelfUpdatePhase.Downloading)
            {
                return;
            }

            DateTimeOffset now = _getNow();
            bool completed = progress.Percent is >= 100
                || progress.BytesTotal is > 0
                && progress.BytesProcessed >= progress.BytesTotal.Value;
            if (!completed && now - _lastProgressPublishedAt < ProgressPublishInterval)
            {
                return;
            }

            _lastProgressPublishedAt = now;
            snapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
            {
                Percent = progress.Percent,
                BytesProcessed = progress.BytesProcessed,
                BytesTotal = progress.BytesTotal,
                Speed = progress.BytesPerSecond,
                Eta = progress.Eta,
                CanUserCancel = _operations.CurrentActivitySnapshot.CanUserCancel
            });
        }
        Publish(snapshot);
    }

    private void PublishPhase(
        LauncherOperationLease operation,
        LauncherSelfUpdatePhase phase,
        bool canUserCancel)
    {
        LauncherSelfUpdateSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_currentOperationId != operation.OperationId || !_currentSnapshot.IsUpdating)
            {
                return;
            }
            snapshot = ReplaceSnapshotUnsafe(_currentSnapshot with
            {
                Phase = phase,
                Percent = phase == LauncherSelfUpdatePhase.Downloading
                    ? _currentSnapshot.Percent
                    : null,
                CanUserCancel = canUserCancel,
                Speed = phase == LauncherSelfUpdatePhase.Downloading
                    ? _currentSnapshot.Speed
                    : null,
                Eta = phase == LauncherSelfUpdatePhase.Downloading
                    ? _currentSnapshot.Eta
                    : null
            });
        }
        Publish(snapshot);
    }

    private LauncherSelfUpdateSnapshot ReplaceSnapshotUnsafe(
        LauncherSelfUpdateSnapshot snapshot)
    {
        _currentSnapshot = snapshot with
        {
            Sequence = ++_sequence
        };
        return _currentSnapshot;
    }

    private void Publish(LauncherSelfUpdateSnapshot? snapshot)
    {
        if (snapshot is null || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }
        lock (_sync)
        {
            if (snapshot.Sequence < _currentSnapshot.Sequence)
            {
                return;
            }
        }
        try
        {
            SnapshotChanged?.Invoke(this, new LauncherSelfUpdateSnapshotEventArgs(snapshot));
        }
        catch
        {
            // Presentation observers cannot alter updater ownership.
        }
        RaiseAvailabilityChanged();
    }

    private void RaiseAvailabilityChanged()
    {
        try
        {
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Commands cannot alter updater ownership.
        }
    }

    private void RaiseTerminal(OperationTerminalResult terminal)
    {
        try
        {
            OperationTerminated?.Invoke(this, new LauncherSelfUpdateTerminalEventArgs(terminal));
        }
        catch
        {
            // Activity projection cannot alter updater ownership.
        }
    }

    private static void RaiseSimpleEvent(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(null, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private string RequireExecutablePath()
    {
        string? executable = _getExecutablePath();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new LauncherSelfUpdateApplyUnavailableException();
        }
        return Path.GetFullPath(executable);
    }

    private static void ValidateManifestForComparison(LauncherUpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.Sha256.Length != 64
            || !manifest.Sha256.All(Uri.IsHexDigit))
        {
            throw new JsonException("ManifestSha256Invalid");
        }
    }

    private static void ValidateManifestForDownload(LauncherUpdateManifest manifest)
    {
        ValidateManifestForComparison(manifest);
        if (manifest.Size <= 0 || string.IsNullOrWhiteSpace(manifest.Url))
        {
            throw new LauncherSelfUpdateApplyUnavailableException();
        }
    }

    private static bool IsManifestVersionEligible(
        string manifestVersion,
        string installedVersion)
    {
        if (!Version.TryParse(manifestVersion, out Version? remoteVersion))
        {
            return false;
        }
        string currentText = installedVersion.Trim().TrimStart('v', 'V');
        Version currentVersion = Version.TryParse(currentText, out Version? parsed)
            ? parsed
            : new Version(0, 0, 0, 0);
        return NormalizeVersion(remoteVersion) > NormalizeVersion(currentVersion);
    }

    private static Version NormalizeVersion(Version version) => new(
        Math.Max(version.Major, 0),
        Math.Max(version.Minor, 0),
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private static LauncherSelfUpdateErrorCategory ClassifyCheckFailure(Exception exception) =>
        exception switch
        {
            LauncherUpdateManifestFormatException => LauncherSelfUpdateErrorCategory.ManifestInvalid,
            JsonException or InvalidDataException => LauncherSelfUpdateErrorCategory.ManifestInvalid,
            LauncherUpdateManifestTransportException =>
                LauncherSelfUpdateErrorCategory.ManifestTransportRejected,
            LauncherUpdateManifestSignatureException =>
                LauncherSelfUpdateErrorCategory.ManifestSignatureInvalid,
            LauncherUpdateManifestUnsupportedException =>
                LauncherSelfUpdateErrorCategory.ManifestUnsupported,
            LauncherSelfUpdateApplyUnavailableException => LauncherSelfUpdateErrorCategory.ApplyUnavailable,
            UnauthorizedAccessException => LauncherSelfUpdateErrorCategory.PermissionDenied,
            HttpRequestException or TaskCanceledException => LauncherSelfUpdateErrorCategory.ManifestUnavailable,
            _ => LauncherSelfUpdateErrorCategory.ManifestUnavailable
        };

    private static LauncherSelfUpdateErrorCategory ClassifyUpdateFailure(
        Exception exception,
        LauncherSelfUpdatePhase phase) => exception switch
        {
            LauncherSelfUpdateRestartException => LauncherSelfUpdateErrorCategory.RestartFailed,
            LauncherUpdateManifestTransportException =>
                LauncherSelfUpdateErrorCategory.ManifestTransportRejected,
            LauncherUpdatePackageIntegrityException =>
                LauncherSelfUpdateErrorCategory.PackageIntegrityFailed,
            LauncherSelfUpdateApplyUnavailableException => LauncherSelfUpdateErrorCategory.ApplyUnavailable,
            UnauthorizedAccessException => LauncherSelfUpdateErrorCategory.PermissionDenied,
            InvalidDataException => LauncherSelfUpdateErrorCategory.CandidateInvalid,
            HttpRequestException => LauncherSelfUpdateErrorCategory.DownloadFailed,
            IOException when phase == LauncherSelfUpdatePhase.Downloading =>
                LauncherSelfUpdateErrorCategory.DownloadFailed,
            _ when phase is LauncherSelfUpdatePhase.WaitingForApply
                or LauncherSelfUpdatePhase.Restarting =>
                LauncherSelfUpdateErrorCategory.ReplacementFailed,
            _ => LauncherSelfUpdateErrorCategory.DownloadFailed
        };

    internal static string GetUserMessage(LauncherSelfUpdateErrorCategory category) => category switch
    {
        LauncherSelfUpdateErrorCategory.ManifestUnavailable =>
            "La recherche de mise à jour est temporairement indisponible.",
        LauncherSelfUpdateErrorCategory.ManifestInvalid =>
            "Les informations de mise à jour reçues sont invalides.",
        LauncherSelfUpdateErrorCategory.ManifestTransportRejected =>
            "Le canal de mise à jour n’a pas pu être vérifié.",
        LauncherSelfUpdateErrorCategory.ManifestSignatureInvalid =>
            "La mise à jour n’a pas pu être vérifiée.",
        LauncherSelfUpdateErrorCategory.ManifestUnsupported =>
            "Cette version du launcher ne reconnaît pas le manifeste de mise à jour.",
        LauncherSelfUpdateErrorCategory.DownloadFailed =>
            "La mise à jour du launcher n’a pas pu être téléchargée.",
        LauncherSelfUpdateErrorCategory.CandidateInvalid =>
            "Le fichier téléchargé n’a pas pu être validé.",
        LauncherSelfUpdateErrorCategory.PackageIntegrityFailed =>
            "Le fichier téléchargé ne correspond pas à la mise à jour vérifiée.",
        LauncherSelfUpdateErrorCategory.ApplyUnavailable =>
            "La mise à jour ne peut pas être appliquée depuis cette installation.",
        LauncherSelfUpdateErrorCategory.PermissionDenied =>
            "Windows a refusé l’autorisation nécessaire à la mise à jour.",
        LauncherSelfUpdateErrorCategory.RestartFailed =>
            "La mise à jour est prête, mais le redémarrage a échoué.",
        LauncherSelfUpdateErrorCategory.ReplacementFailed =>
            "Le programme de mise à jour n’a pas pu démarrer.",
        _ => "Aucune mise à jour n’est disponible."
    };

    private void WriteLogSafely(string message)
    {
        try
        {
            _writeLog(message);
        }
        catch
        {
        }
    }

    private static string CreateProductionDownloadDirectory() => Path.Combine(
        Path.GetTempPath(),
        "WotLKLauncherUpdate",
        Guid.NewGuid().ToString("N"));

    private sealed class LauncherSelfUpdateApplyUnavailableException : Exception
    {
    }

    private sealed class LauncherSelfUpdateRestartException : Exception
    {
    }
}
