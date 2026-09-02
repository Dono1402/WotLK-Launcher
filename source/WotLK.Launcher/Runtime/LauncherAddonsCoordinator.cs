using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherAddonsCoordinator : IDisposable
{
    internal static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(80);

    private readonly object _sync = new();
    private readonly IAddonManagementService _service;
    private readonly IAddonsSessionContext _session;
    private readonly LauncherOperationCoordinator _operations;
    private readonly LauncherSettings _settings;
    private readonly Func<string, bool> _hasPlayableClient;
    private readonly Func<string, bool> _isGameRunning;
    private readonly Action<string> _writeLog;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _lifetimeToken;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private readonly HashSet<Task> _inFlightTasks = [];
    private AddonCatalog? _catalog;
    private AddonsRuntimeSnapshot _currentSnapshot;
    private CancellationTokenSource? _catalogLoadCancellation;
    private LauncherOperationLease? _activeLease;
    private long _catalogLoadGeneration;
    private long _sequence;
    private long _lastProgressTimestamp = long.MinValue;
    private long _transferStartedTimestamp;
    private long _previousTransferBytes;
    private string _transferAddonId = string.Empty;
    private bool _isShuttingDown;
    private int _disposeState;

    internal LauncherAddonsCoordinator(
        IAddonManagementService service,
        IAddonsSessionContext session,
        LauncherOperationCoordinator operations,
        LauncherSettings settings,
        Func<string, bool>? hasPlayableClient,
        Func<string, bool>? isGameRunning,
        Action<string> writeLog,
        TimeProvider? timeProvider = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hasPlayableClient = hasPlayableClient ?? GameInstallServices.HasPlayableClient;
        _isGameRunning = isGameRunning ?? GameInstallServices.IsGameRunning;
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetimeToken = operations.ShutdownToken;

        bool authenticated = _session.CurrentSnapshot.IsAuthenticated;
        bool playable = ReadPlayableSafely(_settings.InstallPath);
        _currentSnapshot = AddonsRuntimeSnapshot.Initial with
        {
            Sequence = NextSequenceUnsafe(),
            LoadState = authenticated
                ? AddonsCatalogLoadState.Idle
                : AddonsCatalogLoadState.SignedOut,
            IsClientPlayable = playable,
            IsGameRunning = playable && ReadGameRunningSafely(_settings.InstallPath),
            IsAuthenticated = authenticated
        };
        _session.SnapshotChanged += Session_SnapshotChanged;
        _operations.StateChanged += Operations_StateChanged;
        _lifetimeRegistration = _lifetimeToken.Register(BeginShutdown);
    }

    internal event EventHandler<AddonsRuntimeSnapshotEventArgs>? SnapshotChanged;

    internal AddonsRuntimeSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    internal AddonsCatalogStartResult TryLoadCatalog(bool forceRefresh = false)
    {
        TaskCompletionSource? startGate = null;
        CancellationTokenSource? cancellation = null;
        Task? operation = null;
        AddonsRuntimeSnapshot? snapshot = null;
        long generation = 0;
        bool refreshLocalOnly = false;

        lock (_sync)
        {
            if (IsStoppingUnsafe())
            {
                return AddonsCatalogStartResult.Rejected(AddonsCatalogStartStatus.ShuttingDown);
            }
            if (!_session.CurrentSnapshot.IsAuthenticated)
            {
                return AddonsCatalogStartResult.Rejected(AddonsCatalogStartStatus.NotAuthenticated);
            }
            if (_catalogLoadCancellation is not null)
            {
                return AddonsCatalogStartResult.Rejected(AddonsCatalogStartStatus.Busy);
            }
            if (_activeLease is not null)
            {
                return AddonsCatalogStartResult.Rejected(AddonsCatalogStartStatus.Busy);
            }
            if (_catalog is not null && !forceRefresh)
            {
                refreshLocalOnly = true;
            }
            else if (_operations.HasActiveUserCancellableOperation)
            {
                return AddonsCatalogStartResult.Rejected(AddonsCatalogStartStatus.Busy);
            }
            else
            {
                generation = ++_catalogLoadGeneration;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                _catalogLoadCancellation = cancellation;
                startGate = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                operation = RunCatalogLoadAfterGateAsync(
                    startGate.Task,
                    generation,
                    cancellation);
                TrackUnsafe(operation);
                snapshot = SetSnapshotUnsafe(_currentSnapshot with
                {
                    LoadState = AddonsCatalogLoadState.Loading,
                    CatalogErrorCategory = AddonsErrorCategory.None,
                    Notice = AddonsNoticeKind.None,
                    IsAuthenticated = true,
                    CanMutate = false
                });
            }
        }

        if (refreshLocalOnly)
        {
            RefreshLocalState();
            return new AddonsCatalogStartResult(
                AddonsCatalogStartStatus.AlreadyLoaded,
                null);
        }

        RaiseSnapshotChanged(snapshot);
        startGate!.TrySetResult();
        return new AddonsCatalogStartResult(
            AddonsCatalogStartStatus.Started,
            operation);
    }

    internal bool RefreshLocalState()
    {
        AddonCatalog? catalog;
        lock (_sync)
        {
            if (IsStoppingUnsafe()
                || _catalogLoadCancellation is not null
                || _activeLease is not null)
            {
                return false;
            }

            catalog = _catalog;
        }

        bool playable = ReadPlayableSafely(_settings.InstallPath);
        bool gameRunning = playable && ReadGameRunningSafely(_settings.InstallPath);
        IReadOnlyDictionary<string, AddonInspection>? inspections = catalog is null
            ? null
            : InspectSafely(catalog, _settings.InstallPath, AddonsRequestedAction.None);
        AddonsRuntimeSnapshot? snapshot;
        lock (_sync)
        {
            if (IsStoppingUnsafe() || !ReferenceEquals(catalog, _catalog))
            {
                return false;
            }

            ImmutableArray<AddonRuntimeItem> items = inspections is null || catalog is null
                ? ResetActiveOperations(_currentSnapshot.Items)
                : BuildItems(catalog, inspections, preserveErrors: true);
            snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot with
            {
                Items = items,
                IsClientPlayable = playable,
                IsGameRunning = gameRunning
            }));
        }

        RaiseSnapshotChanged(snapshot);
        return true;
    }

    internal AddonsActionStartResult TryInvokePrimary(string addonId)
    {
        AddonsRequestedAction action;
        lock (_sync)
        {
            AddonRuntimeItem? item = FindItemUnsafe(addonId);
            if (item is null)
            {
                return AddonsActionStartResult.Rejected(AddonsActionStartStatus.AddonNotFound);
            }
            if (item.IsBusy && _currentSnapshot.CanCancel)
            {
                return CancelCurrent()
                    ? AddonsActionStartResult.Rejected(AddonsActionStartStatus.Busy)
                    : AddonsActionStartResult.Rejected(AddonsActionStartStatus.InvalidState);
            }

            action = item.ErrorCategory != AddonsErrorCategory.None
                ? item.RetryAction
                : item.LocalStatus switch
                {
                    AddonLocalStatus.MissingFiles => AddonsRequestedAction.Repair,
                    AddonLocalStatus.UpdateAvailable => AddonsRequestedAction.Update,
                    AddonLocalStatus.NotInstalled or AddonLocalStatus.DetectedUnmanaged =>
                        AddonsRequestedAction.Install,
                    _ => AddonsRequestedAction.None
                };
        }

        return action == AddonsRequestedAction.None
            ? AddonsActionStartResult.Rejected(AddonsActionStartStatus.InvalidState)
            : TryStartAction(action, [addonId]);
    }

    internal AddonsActionStartResult TryRemove(string addonId)
    {
        lock (_sync)
        {
            AddonRuntimeItem? item = FindItemUnsafe(addonId);
            if (item is null)
            {
                return AddonsActionStartResult.Rejected(AddonsActionStartStatus.AddonNotFound);
            }
            if (!item.IsManaged || item.IsBusy)
            {
                return AddonsActionStartResult.Rejected(AddonsActionStartStatus.InvalidState);
            }
        }

        return TryStartAction(AddonsRequestedAction.Remove, [addonId]);
    }

    internal AddonsActionStartResult TryUpdateAll()
    {
        ImmutableArray<string> targets;
        lock (_sync)
        {
            targets = _currentSnapshot.Items
                .Where(item => item.LocalStatus == AddonLocalStatus.UpdateAvailable)
                .Select(item => item.Id)
                .ToImmutableArray();
        }

        return targets.Length == 0
            ? AddonsActionStartResult.Rejected(AddonsActionStartStatus.InvalidState)
            : TryStartAction(AddonsRequestedAction.UpdateAll, targets);
    }

    internal bool CancelCurrent()
    {
        LauncherOperationLease? lease;
        lock (_sync)
        {
            lease = _activeLease;
        }

        return lease?.CancelFromUser() == true;
    }

    internal void BeginShutdown()
    {
        CancellationTokenSource? catalogCancellation;
        LauncherOperationLease? operation;
        lock (_sync)
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            catalogCancellation = _catalogLoadCancellation;
            operation = _activeLease;
        }

        TryCancel(catalogCancellation);
        operation?.CancelForShutdown();
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

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

        _session.SnapshotChanged -= Session_SnapshotChanged;
        _operations.StateChanged -= Operations_StateChanged;
        BeginShutdown();
        _lifetimeRegistration.Dispose();
        lock (_sync)
        {
            SnapshotChanged = null;
        }
    }

    private AddonsActionStartResult TryStartAction(
        AddonsRequestedAction action,
        ImmutableArray<string> targetIds)
    {
        AddonCatalog catalog;
        ImmutableArray<AddonPackage> packages;
        string installRoot;
        bool canUserCancel = action != AddonsRequestedAction.Remove;

        lock (_sync)
        {
            AddonsActionStartStatus validation = ValidateStartUnsafe(action, targetIds);
            if (validation != AddonsActionStartStatus.Started)
            {
                return AddonsActionStartResult.Rejected(validation);
            }

            catalog = _catalog!;
            Dictionary<string, AddonPackage> byId = catalog.Addons.ToDictionary(
                package => package.Id,
                StringComparer.OrdinalIgnoreCase);
            packages = targetIds.Select(id => byId[id]).ToImmutableArray();
            installRoot = _settings.InstallPath;
        }

        LauncherOperationStartResult operationStart = _operations.TryBegin(
            LauncherOperationKind.Addons,
            canUserCancel,
            operationType: ToOperationType(action));
        if (!operationStart.IsStarted)
        {
            return AddonsActionStartResult.Rejected(MapStartStatus(operationStart.Status));
        }

        LauncherOperationLease lease = operationStart.Lease!;
        TaskCompletionSource startGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AddonsActionCompletion>? operation = null;
        AddonsRuntimeSnapshot? snapshot = null;
        AddonsActionStartStatus postLeaseValidation;
        lock (_sync)
        {
            postLeaseValidation = ValidateStartUnsafe(action, targetIds);
            if (postLeaseValidation == AddonsActionStartStatus.Started)
            {
                _activeLease = lease;
                ResetTransferTrackingUnsafe(targetIds[0]);
                AddonsOperationState operationState = ToOperationState(action);
                ImmutableArray<AddonRuntimeItem> items = MarkActiveItem(
                    _currentSnapshot.Items,
                    targetIds[0],
                    action == AddonsRequestedAction.UpdateAll
                        ? AddonsOperationState.Updating
                        : operationState);
                snapshot = SetSnapshotUnsafe(_currentSnapshot with
                {
                    OperationId = lease.OperationId,
                    Items = items,
                    OperationState = operationState,
                    OperationPhase = AddonsOperationPhase.PreparingSession,
                    ActiveAddonId = targetIds[0],
                    PendingAddonIds = targetIds,
                    Progress = new AddonsRuntimeProgress(
                        targetIds[0],
                        AddonsOperationPhase.PreparingSession,
                        null,
                        null,
                        null,
                        null),
                    Error = AddonsRuntimeError.None,
                    Notice = AddonsNoticeKind.None,
                    CanMutate = false,
                    CanCancel = lease.CanUserCancel,
                    TerminalResult = null,
                    ActiveAddonPosition = action == AddonsRequestedAction.UpdateAll ? 1 : null,
                    ActiveAddonTotal = action == AddonsRequestedAction.UpdateAll
                        ? targetIds.Length
                        : null
                });
                OperationPlan plan = new(
                    catalog,
                    packages,
                    action,
                    installRoot);
                operation = RunOperationAfterGateAsync(startGate.Task, lease, plan);
                TrackUnsafe(operation);
            }
        }

        if (postLeaseValidation != AddonsActionStartStatus.Started)
        {
            lease.Complete();
            return AddonsActionStartResult.Rejected(postLeaseValidation);
        }

        RaiseSnapshotChanged(snapshot);
        startGate.TrySetResult();
        return new AddonsActionStartResult(
            AddonsActionStartStatus.Started,
            lease.OperationId,
            operation!);
    }

    private AddonsActionStartStatus ValidateStartUnsafe(
        AddonsRequestedAction action,
        ImmutableArray<string> targetIds)
    {
        if (IsStoppingUnsafe())
        {
            return AddonsActionStartStatus.ShuttingDown;
        }
        if (!_session.CurrentSnapshot.IsAuthenticated)
        {
            return AddonsActionStartStatus.NotAuthenticated;
        }
        if (_catalog is null || _currentSnapshot.LoadState != AddonsCatalogLoadState.Loaded)
        {
            return AddonsActionStartStatus.CatalogUnavailable;
        }
        if (_catalogLoadCancellation is not null || _activeLease is not null)
        {
            return AddonsActionStartStatus.Busy;
        }
        if (!_currentSnapshot.IsClientPlayable)
        {
            return AddonsActionStartStatus.ClientUnavailable;
        }
        if (targetIds.IsDefaultOrEmpty
            || targetIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targetIds.Length
            || targetIds.Any(id => FindItemUnsafe(id) is null))
        {
            return AddonsActionStartStatus.AddonNotFound;
        }

        foreach (string targetId in targetIds)
        {
            AddonRuntimeItem item = FindItemUnsafe(targetId)!;
            bool valid = action switch
            {
                AddonsRequestedAction.Install => item.LocalStatus is
                    AddonLocalStatus.NotInstalled or AddonLocalStatus.DetectedUnmanaged,
                AddonsRequestedAction.Update =>
                    item.LocalStatus == AddonLocalStatus.UpdateAvailable,
                AddonsRequestedAction.Remove => item.IsManaged,
                AddonsRequestedAction.Repair =>
                    item.LocalStatus == AddonLocalStatus.MissingFiles,
                AddonsRequestedAction.UpdateAll =>
                    item.LocalStatus == AddonLocalStatus.UpdateAvailable,
                _ => false
            };
            if (!valid)
            {
                return AddonsActionStartStatus.InvalidState;
            }
        }

        return AddonsActionStartStatus.Started;
    }

    private async Task RunCatalogLoadAfterGateAsync(
        Task gate,
        long generation,
        CancellationTokenSource cancellation)
    {
        await gate.ConfigureAwait(false);
        try
        {
            AtlasRequestPreparationStatus preparation = await _session
                .PrepareAuthenticatedRequestAsync(cancellation.Token)
                .ConfigureAwait(false);
            if (preparation != AtlasRequestPreparationStatus.Ready)
            {
                AddonsErrorCategory category = preparation == AtlasRequestPreparationStatus.AuthenticationRequired
                    ? AddonsErrorCategory.Unauthorized
                    : AddonsErrorCategory.ServiceUnavailable;
                CompleteCatalogFailure(generation, category, cancellation);
                return;
            }

            AddonCatalog catalog = await _service
                .LoadCatalogAsync(cancellation.Token)
                .ConfigureAwait(false);
            IReadOnlyDictionary<string, AddonInspection> inspections = _service.Inspect(
                catalog,
                _settings.InstallPath);
            CompleteCatalogSuccess(generation, catalog, inspections, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CompleteCatalogCancelled(generation, cancellation);
        }
        catch (Exception exception)
        {
            AddonsErrorCategory category = ClassifyFailure(exception);
            if (category == AddonsErrorCategory.Unauthorized)
            {
                _session.NotifyAuthenticatedRequestUnauthorized();
            }
            WriteFailureSafely(string.Empty, AddonsRequestedAction.None, category, exception);
            CompleteCatalogFailure(generation, category, cancellation);
        }
    }

    private async Task<AddonsActionCompletion> RunOperationAfterGateAsync(
        Task gate,
        LauncherOperationLease lease,
        OperationPlan plan)
    {
        await gate.ConfigureAwait(false);
        return await RunOperationAsync(lease, plan).ConfigureAwait(false);
    }

    private async Task<AddonsActionCompletion> RunOperationAsync(
        LauncherOperationLease lease,
        OperationPlan plan)
    {
        string activeAddonId = plan.Packages[0].Id;
        AddonsRequestedAction activeAction = plan.Action == AddonsRequestedAction.UpdateAll
            ? AddonsRequestedAction.Update
            : plan.Action;
        IReadOnlyDictionary<string, AddonInspection>? latestInspections = null;
        try
        {
            AtlasRequestPreparationStatus preparation = await _session
                .PrepareAuthenticatedRequestAsync(lease.CancellationToken)
                .ConfigureAwait(false);
            if (lease.CancellationReason != LauncherOperationCancellationReason.None)
            {
                return CompleteCancelled(lease, plan);
            }

            if (preparation != AtlasRequestPreparationStatus.Ready)
            {
                if (preparation is AtlasRequestPreparationStatus.Cancelled
                    or AtlasRequestPreparationStatus.ShuttingDown)
                {
                    return CompleteCancelled(lease, plan);
                }

                return CompleteFailure(
                    lease,
                    plan,
                    activeAddonId,
                    activeAction,
                    preparation == AtlasRequestPreparationStatus.AuthenticationRequired
                        ? AddonsErrorCategory.Unauthorized
                        : AddonsErrorCategory.ServiceUnavailable,
                    exception: null);
            }

            for (int index = 0; index < plan.Packages.Length; index++)
            {
                lease.CancellationToken.ThrowIfCancellationRequested();
                AddonPackage package = plan.Packages[index];
                activeAddonId = package.Id;
                activeAction = plan.Action == AddonsRequestedAction.UpdateAll
                    ? AddonsRequestedAction.Update
                    : plan.Action;
                PublishTargetStart(lease, plan, index, activeAction);

                AddonCatalog scopedCatalog = CreateScopedCatalog(plan.Catalog, package);
                Dictionary<string, bool> selection = new(StringComparer.OrdinalIgnoreCase)
                {
                    [package.Id] = activeAction != AddonsRequestedAction.Remove
                };
                IProgress<AddonTransferProgress>? progress = activeAction == AddonsRequestedAction.Remove
                    ? null
                    : new InlineProgress<AddonTransferProgress>(value =>
                        ReportProgress(lease, package.Id, value));
                await _service.ApplySelectionAsync(
                    scopedCatalog,
                    plan.InstallRoot,
                    selection,
                    progress,
                    _ => WritePhaseSafely(package, activeAction),
                    lease.CancellationToken).ConfigureAwait(false);

                latestInspections = _service.Inspect(plan.Catalog, plan.InstallRoot);
                if (index + 1 < plan.Packages.Length)
                {
                    PublishCompletedTarget(
                        lease,
                        plan,
                        latestInspections,
                        index,
                        index + 1);
                }
            }

            latestInspections ??= _service.Inspect(plan.Catalog, plan.InstallRoot);
            if (lease.CancellationReason != LauncherOperationCancellationReason.None)
            {
                return CompleteCancelled(lease, plan);
            }

            return CompleteSuccess(lease, plan, latestInspections);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            return CompleteCancelled(lease, plan);
        }
        catch (Exception exception)
        {
            if (lease.CancellationReason != LauncherOperationCancellationReason.None)
            {
                return CompleteCancelled(lease, plan);
            }

            AddonsErrorCategory category = ClassifyFailure(exception);
            if (category == AddonsErrorCategory.Unauthorized)
            {
                _session.NotifyAuthenticatedRequestUnauthorized();
            }
            return CompleteFailure(
                lease,
                plan,
                activeAddonId,
                activeAction,
                category,
                exception);
        }
        finally
        {
            bool release;
            lock (_sync)
            {
                release = ReferenceEquals(_activeLease, lease);
                if (release)
                {
                    _activeLease = null;
                }
            }

            lease.Complete();
        }
    }

    private void PublishTargetStart(
        LauncherOperationLease lease,
        OperationPlan plan,
        int index,
        AddonsRequestedAction action)
    {
        AddonPackage package = plan.Packages[index];
        AddonsRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(lease))
            {
                return;
            }

            ResetTransferTrackingUnsafe(package.Id);
            AddonsOperationPhase phase = action == AddonsRequestedAction.Remove
                ? AddonsOperationPhase.Removing
                : AddonsOperationPhase.Downloading;
            AddonsOperationState itemOperation = ToOperationState(action);
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                Items = MarkActiveItem(_currentSnapshot.Items, package.Id, itemOperation),
                OperationPhase = phase,
                ActiveAddonId = package.Id,
                PendingAddonIds = plan.Packages[index..]
                    .Select(item => item.Id)
                    .ToImmutableArray(),
                Progress = new AddonsRuntimeProgress(
                    package.Id,
                    phase,
                    null,
                    null,
                    null,
                    null),
                CanCancel = lease.CanUserCancel,
                ActiveAddonPosition = plan.Action == AddonsRequestedAction.UpdateAll
                    ? index + 1
                    : null,
                ActiveAddonTotal = plan.Action == AddonsRequestedAction.UpdateAll
                    ? plan.Packages.Length
                    : null
            });
        }

        RaiseSnapshotChanged(snapshot);
    }

    private void PublishCompletedTarget(
        LauncherOperationLease lease,
        OperationPlan plan,
        IReadOnlyDictionary<string, AddonInspection> inspections,
        int completedIndex,
        int nextIndex)
    {
        AddonsRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(lease))
            {
                return;
            }

            ImmutableArray<AddonRuntimeItem> items = BuildItems(
                plan.Catalog,
                inspections,
                preserveErrors: true,
                clearErrorIds: [plan.Packages[completedIndex].Id]);
            AddonPackage next = plan.Packages[nextIndex];
            items = MarkActiveItem(items, next.Id, AddonsOperationState.Updating);
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                Items = items,
                ActiveAddonId = next.Id,
                PendingAddonIds = plan.Packages[nextIndex..]
                    .Select(package => package.Id)
                    .ToImmutableArray(),
                OperationPhase = AddonsOperationPhase.Downloading,
                Progress = new AddonsRuntimeProgress(
                    next.Id,
                    AddonsOperationPhase.Downloading,
                    null,
                    null,
                    null,
                    null),
                ActiveAddonPosition = nextIndex + 1,
                ActiveAddonTotal = plan.Packages.Length
            });
        }

        RaiseSnapshotChanged(snapshot);
    }

    private void ReportProgress(
        LauncherOperationLease lease,
        string addonId,
        AddonTransferProgress progress)
    {
        AddonsRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(lease)
                || !string.Equals(_currentSnapshot.ActiveAddonId, addonId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long now = _timeProvider.GetTimestamp();
            if (!string.Equals(_transferAddonId, addonId, StringComparison.OrdinalIgnoreCase)
                || progress.BytesReceived < _previousTransferBytes)
            {
                _transferAddonId = addonId;
                _transferStartedTimestamp = now;
                _lastProgressTimestamp = long.MinValue;
                _previousTransferBytes = 0;
            }

            bool terminal = progress.TotalBytes > 0
                && progress.BytesReceived >= progress.TotalBytes;
            bool shouldPublish = _lastProgressTimestamp == long.MinValue
                || terminal
                || _timeProvider.GetElapsedTime(_lastProgressTimestamp, now)
                    >= ProgressPublishInterval;
            _previousTransferBytes = progress.BytesReceived;
            if (!shouldPublish)
            {
                return;
            }

            _lastProgressTimestamp = now;
            TimeSpan elapsed = _timeProvider.GetElapsedTime(_transferStartedTimestamp, now);
            double? speed = elapsed.TotalSeconds > 0.05 && progress.BytesReceived > 0
                ? progress.BytesReceived / elapsed.TotalSeconds
                : null;
            TimeSpan? eta = speed is > 0
                && progress.TotalBytes > progress.BytesReceived
                    ? TimeSpan.FromSeconds(
                        (progress.TotalBytes - progress.BytesReceived) / speed.Value)
                    : null;
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                OperationPhase = AddonsOperationPhase.Downloading,
                Progress = new AddonsRuntimeProgress(
                    addonId,
                    AddonsOperationPhase.Downloading,
                    progress.BytesReceived,
                    progress.TotalBytes > 0 ? progress.TotalBytes : null,
                    speed,
                    eta)
            });
        }

        RaiseSnapshotChanged(snapshot);
    }

    private AddonsActionCompletion CompleteSuccess(
        LauncherOperationLease lease,
        OperationPlan plan,
        IReadOnlyDictionary<string, AddonInspection> inspections)
    {
        AddonsRuntimeSnapshot? snapshot = null;
        OperationTerminalResult? terminalResult = null;
        bool publish = false;
        lock (_sync)
        {
            if (OwnsLeaseUnsafe(lease))
            {
                terminalResult = CreateTerminalResult(
                    lease,
                    plan,
                    LauncherOperationOutcome.Succeeded);
                ImmutableArray<string> completedIds = plan.Packages
                    .Select(package => package.Id)
                    .ToImmutableArray();
                snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot with
                {
                    OperationId = null,
                    Items = BuildItems(
                        plan.Catalog,
                        inspections,
                        preserveErrors: true,
                        clearErrorIds: completedIds),
                    OperationState = AddonsOperationState.None,
                    OperationPhase = AddonsOperationPhase.None,
                    ActiveAddonId = string.Empty,
                    PendingAddonIds = ImmutableArray<string>.Empty,
                    Progress = AddonsRuntimeProgress.None,
                    Error = AddonsRuntimeError.None,
                    Notice = ToNotice(plan.Action),
                    IsGameRunning = ReadGameRunningSafely(plan.InstallRoot),
                    CanCancel = false,
                    TerminalResult = terminalResult,
                    ActiveAddonPosition = null,
                    ActiveAddonTotal = null
                }));
                publish = !IsStoppingUnsafe();
            }
        }

        WriteSuccessSafely(plan);
        RaiseSnapshotChanged(publish ? snapshot : null);
        return new AddonsActionCompletion(
            snapshot is null
                ? AddonsActionCompletionStatus.Superseded
                : AddonsActionCompletionStatus.Succeeded,
            snapshot ?? CurrentSnapshot,
            terminalResult);
    }

    private AddonsActionCompletion CompleteCancelled(
        LauncherOperationLease lease,
        OperationPlan plan)
    {
        IReadOnlyDictionary<string, AddonInspection>? inspections = InspectSafely(
            plan.Catalog,
            plan.InstallRoot,
            plan.Action);
        AddonsRuntimeSnapshot? snapshot = null;
        OperationTerminalResult? terminalResult = null;
        bool publish = false;
        lock (_sync)
        {
            if (OwnsLeaseUnsafe(lease))
            {
                terminalResult = CreateTerminalResult(
                    lease,
                    plan,
                    LauncherOperationOutcome.Cancelled,
                    cancellationReason: lease.CancellationReason);
                snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot with
                {
                    OperationId = null,
                    Items = inspections is null
                        ? ResetActiveOperations(_currentSnapshot.Items)
                        : BuildItems(plan.Catalog, inspections, preserveErrors: true),
                    OperationState = AddonsOperationState.None,
                    OperationPhase = AddonsOperationPhase.None,
                    ActiveAddonId = string.Empty,
                    PendingAddonIds = ImmutableArray<string>.Empty,
                    Progress = AddonsRuntimeProgress.None,
                    Error = AddonsRuntimeError.None,
                    Notice = AddonsNoticeKind.Cancelled,
                    IsGameRunning = ReadGameRunningSafely(plan.InstallRoot),
                    CanCancel = false,
                    TerminalResult = terminalResult,
                    ActiveAddonPosition = null,
                    ActiveAddonTotal = null
                }));
                publish = !IsStoppingUnsafe();
            }
        }

        WriteCancellationSafely(plan.Action);
        RaiseSnapshotChanged(publish ? snapshot : null);
        return new AddonsActionCompletion(
            snapshot is null
                ? AddonsActionCompletionStatus.Superseded
                : AddonsActionCompletionStatus.Cancelled,
            snapshot ?? CurrentSnapshot,
            terminalResult);
    }

    private AddonsActionCompletion CompleteFailure(
        LauncherOperationLease lease,
        OperationPlan plan,
        string failedAddonId,
        AddonsRequestedAction failedAction,
        AddonsErrorCategory category,
        Exception? exception)
    {
        IReadOnlyDictionary<string, AddonInspection>? inspections = InspectSafely(
            plan.Catalog,
            plan.InstallRoot,
            plan.Action);
        AddonsRuntimeSnapshot? snapshot = null;
        OperationTerminalResult? terminalResult = null;
        bool publish = false;
        lock (_sync)
        {
            if (OwnsLeaseUnsafe(lease))
            {
                terminalResult = CreateTerminalResult(
                    lease,
                    plan,
                    LauncherOperationOutcome.Failed,
                    failedAddonId,
                    category.ToString());
                ImmutableArray<AddonRuntimeItem> items = inspections is null
                    ? ResetActiveOperations(_currentSnapshot.Items)
                    : BuildItems(plan.Catalog, inspections, preserveErrors: true);
                AddonsRequestedAction retry = failedAction is AddonsRequestedAction.Install
                    or AddonsRequestedAction.Update
                    or AddonsRequestedAction.Repair
                        ? failedAction
                        : AddonsRequestedAction.None;
                items = MarkError(items, failedAddonId, retry, category);
                snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot with
                {
                    OperationId = null,
                    Items = items,
                    OperationState = AddonsOperationState.None,
                    OperationPhase = AddonsOperationPhase.None,
                    ActiveAddonId = string.Empty,
                    PendingAddonIds = ImmutableArray<string>.Empty,
                    Progress = AddonsRuntimeProgress.None,
                    Error = new AddonsRuntimeError(failedAddonId, failedAction, category),
                    Notice = AddonsNoticeKind.None,
                    IsGameRunning = ReadGameRunningSafely(plan.InstallRoot),
                    CanCancel = false,
                    TerminalResult = terminalResult,
                    ActiveAddonPosition = null,
                    ActiveAddonTotal = null
                }));
                publish = !IsStoppingUnsafe();
            }
        }

        if (exception is not null)
        {
            WriteFailureSafely(failedAddonId, failedAction, category, exception);
        }
        RaiseSnapshotChanged(publish ? snapshot : null);
        return new AddonsActionCompletion(
            snapshot is null
                ? AddonsActionCompletionStatus.Superseded
                : AddonsActionCompletionStatus.Failed,
            snapshot ?? CurrentSnapshot,
            terminalResult);
    }

    private void CompleteCatalogSuccess(
        long generation,
        AddonCatalog catalog,
        IReadOnlyDictionary<string, AddonInspection> inspections,
        CancellationTokenSource cancellation)
    {
        AddonsRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (IsCurrentCatalogLoadUnsafe(generation) && !IsStoppingUnsafe())
            {
                _catalog = catalog;
                ReleaseCatalogLoadUnsafe(cancellation);
                snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot with
                {
                    Items = BuildItems(catalog, inspections, preserveErrors: false),
                    LoadState = AddonsCatalogLoadState.Loaded,
                    IsCatalogStale = false,
                    CatalogErrorCategory = AddonsErrorCategory.None,
                    Error = AddonsRuntimeError.None,
                    Notice = AddonsNoticeKind.None,
                    IsClientPlayable = ReadPlayableSafely(_settings.InstallPath),
                    IsGameRunning = ReadGameRunningSafely(_settings.InstallPath)
                }));
            }
            else
            {
                ReleaseCatalogLoadUnsafe(cancellation);
            }
        }

        cancellation.Dispose();
        RaiseSnapshotChanged(snapshot);
    }

    private void CompleteCatalogFailure(
        long generation,
        AddonsErrorCategory category,
        CancellationTokenSource cancellation)
    {
        AddonsRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (IsCurrentCatalogLoadUnsafe(generation) && !IsStoppingUnsafe())
            {
                bool hasKnownCatalog = _catalog is not null;
                ReleaseCatalogLoadUnsafe(cancellation);
                snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot with
                {
                    LoadState = hasKnownCatalog
                        ? AddonsCatalogLoadState.Loaded
                        : AddonsCatalogLoadState.Failed,
                    IsCatalogStale = hasKnownCatalog,
                    CatalogErrorCategory = category,
                    Notice = AddonsNoticeKind.None
                }));
            }
            else
            {
                ReleaseCatalogLoadUnsafe(cancellation);
            }
        }

        cancellation.Dispose();
        RaiseSnapshotChanged(snapshot);
    }

    private void CompleteCatalogCancelled(
        long generation,
        CancellationTokenSource cancellation)
    {
        AddonsRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (IsCurrentCatalogLoadUnsafe(generation) && !IsStoppingUnsafe())
            {
                ReleaseCatalogLoadUnsafe(cancellation);
                snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot with
                {
                    LoadState = _catalog is null
                        ? AddonsCatalogLoadState.Idle
                        : AddonsCatalogLoadState.Loaded,
                    CatalogErrorCategory = AddonsErrorCategory.None
                }));
            }
            else
            {
                ReleaseCatalogLoadUnsafe(cancellation);
            }
        }

        cancellation.Dispose();
        RaiseSnapshotChanged(snapshot);
    }

    private ImmutableArray<AddonRuntimeItem> BuildItems(
        AddonCatalog catalog,
        IReadOnlyDictionary<string, AddonInspection> inspections,
        bool preserveErrors,
        ImmutableArray<string> clearErrorIds = default)
    {
        Dictionary<string, AddonRuntimeItem> previous = _currentSnapshot.Items
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> cleared = clearErrorIds.IsDefault
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : clearErrorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalog.Addons
            .Select(package =>
            {
                AddonInspection inspection = inspections.TryGetValue(package.Id, out AddonInspection? value)
                    ? value
                    : new AddonInspection(AddonLocalStatus.NotInstalled, IsManaged: false);
                AddonSelectionItem legacyItem = new(package);
                legacyItem.ApplyInspection(inspection);
                previous.TryGetValue(package.Id, out AddonRuntimeItem? oldItem);
                bool retainError = preserveErrors
                    && oldItem?.ErrorCategory != AddonsErrorCategory.None
                    && !cleared.Contains(package.Id);
                return new AddonRuntimeItem(
                    legacyItem.Id,
                    legacyItem.Name,
                    legacyItem.Description,
                    legacyItem.Category,
                    package.Version,
                    inspection.InstalledVersion ?? string.Empty,
                    inspection.InstalledSha256 ?? string.Empty,
                    inspection.InstalledAtUtc,
                    package.Interface,
                    package.Author,
                    package.Dependencies.ToImmutableArray(),
                    (inspection.InstalledFolders ?? package.Folders).ToImmutableArray(),
                    inspection.Status,
                    inspection.IsManaged,
                    AddonsOperationState.None,
                    retainError ? oldItem!.RetryAction : AddonsRequestedAction.None,
                    retainError ? oldItem!.ErrorCategory : AddonsErrorCategory.None);
            })
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static ImmutableArray<AddonRuntimeItem> MarkActiveItem(
        ImmutableArray<AddonRuntimeItem> items,
        string addonId,
        AddonsOperationState operation) =>
        items.Select(item => string.Equals(item.Id, addonId, StringComparison.OrdinalIgnoreCase)
            ? item with
            {
                ActiveOperation = operation,
                RetryAction = AddonsRequestedAction.None,
                ErrorCategory = AddonsErrorCategory.None
            }
            : item with { ActiveOperation = AddonsOperationState.None })
        .ToImmutableArray();

    private static ImmutableArray<AddonRuntimeItem> MarkError(
        ImmutableArray<AddonRuntimeItem> items,
        string addonId,
        AddonsRequestedAction retry,
        AddonsErrorCategory category) =>
        items.Select(item => string.Equals(item.Id, addonId, StringComparison.OrdinalIgnoreCase)
            ? item with
            {
                ActiveOperation = AddonsOperationState.None,
                RetryAction = retry,
                ErrorCategory = category
            }
            : item with { ActiveOperation = AddonsOperationState.None })
        .ToImmutableArray();

    private static ImmutableArray<AddonRuntimeItem> ResetActiveOperations(
        ImmutableArray<AddonRuntimeItem> items) =>
        items.Select(item => item with { ActiveOperation = AddonsOperationState.None })
            .ToImmutableArray();

    private AddonsRuntimeSnapshot RecalculateAvailabilityUnsafe(AddonsRuntimeSnapshot snapshot)
    {
        bool authenticated = _session.CurrentSnapshot.IsAuthenticated;
        bool canMutate = authenticated
            && snapshot.IsClientPlayable
            && _catalog is not null
            && snapshot.LoadState == AddonsCatalogLoadState.Loaded
            && _catalogLoadCancellation is null
            && _activeLease is null
            && _operations.CanBegin(LauncherOperationKind.Addons);
        return snapshot with
        {
            IsAuthenticated = authenticated,
            CanMutate = canMutate,
            CanCancel = _activeLease?.CanUserCancel == true
        };
    }

    private void Session_SnapshotChanged(object? sender, AuthSessionSnapshotEventArgs e)
    {
        AddonsRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (IsStoppingUnsafe())
            {
                return;
            }

            snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot with
            {
                IsAuthenticated = e.Snapshot.IsAuthenticated,
                LoadState = e.Snapshot.IsAuthenticated
                    ? _catalog is null
                        ? AddonsCatalogLoadState.Idle
                        : AddonsCatalogLoadState.Loaded
                    : AddonsCatalogLoadState.SignedOut
            }));
        }

        RaiseSnapshotChanged(snapshot);
    }

    private void Operations_StateChanged(object? sender, EventArgs e)
    {
        AddonsRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsStoppingUnsafe())
            {
                snapshot = SetSnapshotUnsafe(RecalculateAvailabilityUnsafe(_currentSnapshot));
            }
        }

        RaiseSnapshotChanged(snapshot);
    }

    private IReadOnlyDictionary<string, AddonInspection>? InspectSafely(
        AddonCatalog catalog,
        string installRoot,
        AddonsRequestedAction action)
    {
        try
        {
            return _service.Inspect(catalog, installRoot);
        }
        catch (Exception exception)
        {
            WriteFailureSafely(string.Empty, action, ClassifyFailure(exception), exception);
            return null;
        }
    }

    private bool ReadPlayableSafely(string installRoot)
    {
        try
        {
            return _hasPlayableClient(installRoot);
        }
        catch (Exception exception)
        {
            WriteFailureSafely(
                string.Empty,
                AddonsRequestedAction.None,
                ClassifyFailure(exception),
                exception);
            return false;
        }
    }

    private bool ReadGameRunningSafely(string installRoot)
    {
        try
        {
            return _isGameRunning(installRoot);
        }
        catch (Exception exception)
        {
            WriteFailureSafely(
                string.Empty,
                AddonsRequestedAction.None,
                ClassifyFailure(exception),
                exception);
            return false;
        }
    }

    private void WritePhaseSafely(AddonPackage package, AddonsRequestedAction action)
    {
        try
        {
            _writeLog(
                $"Addons V2: id={package.Id}; operation={action}; version={package.Version}; phase=apply.");
        }
        catch
        {
        }
    }

    private void WriteSuccessSafely(OperationPlan plan)
    {
        try
        {
            _writeLog(
                $"Addons V2 terminés: operation={plan.Action}; count={plan.Packages.Length}; result=success.");
        }
        catch
        {
        }
    }

    private void WriteCancellationSafely(AddonsRequestedAction action)
    {
        try
        {
            _writeLog($"Addons V2 annulés: operation={action}; result=cancelled.");
        }
        catch
        {
        }
    }

    private void WriteFailureSafely(
        string addonId,
        AddonsRequestedAction action,
        AddonsErrorCategory category,
        Exception exception)
    {
        try
        {
            _writeLog(
                $"Addons V2 indisponibles: id={addonId}; operation={action}; category={category}; "
                + $"type={exception.GetType().Name}.");
        }
        catch
        {
        }
    }

    private void TrackUnsafe(Task task)
    {
        _inFlightTasks.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (_sync)
                {
                    _inFlightTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private AddonRuntimeItem? FindItemUnsafe(string addonId) =>
        _currentSnapshot.Items.FirstOrDefault(item =>
            string.Equals(item.Id, addonId, StringComparison.OrdinalIgnoreCase));

    private bool IsCurrentUnsafe(LauncherOperationLease lease) =>
        OwnsLeaseUnsafe(lease)
        && _currentSnapshot.OperationId == lease.OperationId
        && lease.IsCurrent;

    private bool OwnsLeaseUnsafe(LauncherOperationLease lease) =>
        ReferenceEquals(_activeLease, lease)
        && _currentSnapshot.OperationId == lease.OperationId;

    private bool IsCurrentCatalogLoadUnsafe(long generation) =>
        _catalogLoadCancellation is not null
        && _catalogLoadGeneration == generation;

    private void ReleaseCatalogLoadUnsafe(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_catalogLoadCancellation, cancellation))
        {
            _catalogLoadCancellation = null;
        }
    }

    private bool IsStoppingUnsafe() => _isShuttingDown
        || Volatile.Read(ref _disposeState) != 0
        || _lifetimeToken.IsCancellationRequested;

    private AddonsRuntimeSnapshot SetSnapshotUnsafe(AddonsRuntimeSnapshot snapshot)
    {
        _currentSnapshot = snapshot with { Sequence = NextSequenceUnsafe() };
        return _currentSnapshot;
    }

    private long NextSequenceUnsafe() => ++_sequence;

    private void ResetTransferTrackingUnsafe(string addonId)
    {
        _transferAddonId = addonId;
        _transferStartedTimestamp = _timeProvider.GetTimestamp();
        _lastProgressTimestamp = long.MinValue;
        _previousTransferBytes = 0;
    }

    private void RaiseSnapshotChanged(AddonsRuntimeSnapshot? snapshot)
    {
        if (snapshot is null || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        try
        {
            SnapshotChanged?.Invoke(this, new AddonsRuntimeSnapshotEventArgs(snapshot));
        }
        catch
        {
            // Presentation subscribers cannot interrupt addon ownership.
        }
    }

    private static AddonCatalog CreateScopedCatalog(
        AddonCatalog source,
        AddonPackage package) =>
        new()
        {
            SchemaVersion = source.SchemaVersion,
            ClientInterface = source.ClientInterface,
            Addons = [package]
        };

    private static AddonsOperationState ToOperationState(AddonsRequestedAction action) =>
        action switch
        {
            AddonsRequestedAction.Install => AddonsOperationState.Installing,
            AddonsRequestedAction.Update => AddonsOperationState.Updating,
            AddonsRequestedAction.Remove => AddonsOperationState.Removing,
            AddonsRequestedAction.Repair => AddonsOperationState.Repairing,
            AddonsRequestedAction.UpdateAll => AddonsOperationState.UpdatingAll,
            _ => AddonsOperationState.None
        };

    private static LauncherOperationType ToOperationType(AddonsRequestedAction action) =>
        action switch
        {
            AddonsRequestedAction.Install => LauncherOperationType.AddonInstall,
            AddonsRequestedAction.Update => LauncherOperationType.AddonUpdate,
            AddonsRequestedAction.Repair => LauncherOperationType.AddonRepair,
            AddonsRequestedAction.Remove => LauncherOperationType.AddonRemove,
            AddonsRequestedAction.UpdateAll => LauncherOperationType.AddonBatchUpdate,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

    private OperationTerminalResult CreateTerminalResult(
        LauncherOperationLease lease,
        OperationPlan plan,
        LauncherOperationOutcome outcome,
        string? subjectId = null,
        string? errorCategory = null,
        LauncherOperationCancellationReason cancellationReason =
            LauncherOperationCancellationReason.None)
    {
        AddonPackage? subject = subjectId is null
            ? plan.Packages.Length == 1
                ? plan.Packages[0]
                : null
            : plan.Packages.FirstOrDefault(package =>
                string.Equals(package.Id, subjectId, StringComparison.OrdinalIgnoreCase));
        LauncherOperationDisplayContext context = subject is not null
            ? new LauncherOperationDisplayContext(subject.Id, subject.Name)
            : new LauncherOperationDisplayContext(
                "addon-batch",
                $"{plan.Packages.Length} addons");
        return new OperationTerminalResult(
            lease.OperationId,
            lease.OperationType,
            outcome,
            _timeProvider.GetUtcNow(),
            cancellationReason,
            errorCategory,
            context);
    }

    private static AddonsNoticeKind ToNotice(AddonsRequestedAction action) => action switch
    {
        AddonsRequestedAction.Install => AddonsNoticeKind.Installed,
        AddonsRequestedAction.Update => AddonsNoticeKind.Updated,
        AddonsRequestedAction.Remove => AddonsNoticeKind.Removed,
        AddonsRequestedAction.Repair => AddonsNoticeKind.Repaired,
        AddonsRequestedAction.UpdateAll => AddonsNoticeKind.BatchUpdated,
        _ => AddonsNoticeKind.None
    };

    private static AddonsActionStartStatus MapStartStatus(
        LauncherOperationStartStatus status) => status switch
        {
            LauncherOperationStartStatus.ShuttingDown => AddonsActionStartStatus.ShuttingDown,
            LauncherOperationStartStatus.RejectedByCompatibility =>
                AddonsActionStartStatus.RejectedByCompatibility,
            _ => AddonsActionStartStatus.Busy
        };

    internal static AddonsErrorCategory ClassifyFailure(Exception exception)
    {
        if (exception is TaskCanceledException or TimeoutException)
        {
            return AddonsErrorCategory.Timeout;
        }
        if (exception is LauncherAuthException authException)
        {
            if (authException.StatusCode == HttpStatusCode.Unauthorized)
            {
                return AddonsErrorCategory.Unauthorized;
            }
            return authException.StatusCode is >= HttpStatusCode.InternalServerError
                ? AddonsErrorCategory.ServiceUnavailable
                : AddonsErrorCategory.Network;
        }
        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode switch
            {
                HttpStatusCode.Unauthorized => AddonsErrorCategory.Unauthorized,
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                    AddonsErrorCategory.Timeout,
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable =>
                    AddonsErrorCategory.ServiceUnavailable,
                _ => AddonsErrorCategory.Network
            };
        }
        if (exception is UnauthorizedAccessException)
        {
            return AddonsErrorCategory.AccessDenied;
        }
        if (exception is IOException ioException)
        {
            int errorCode = ioException.HResult & 0xFFFF;
            return errorCode is 32 or 33
                ? AddonsErrorCategory.FilesLocked
                : AddonsErrorCategory.Disk;
        }
        if (exception is InvalidDataException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            return AddonsErrorCategory.InvalidPackage;
        }

        return AddonsErrorCategory.Unknown;
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

    private sealed record OperationPlan(
        AddonCatalog Catalog,
        ImmutableArray<AddonPackage> Packages,
        AddonsRequestedAction Action,
        string InstallRoot);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
