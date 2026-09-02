using System.Collections.Immutable;
using WotLK.Launcher.Game;

namespace WotLK.Launcher.Runtime;

internal interface ILauncherOperationActivitySource
{
    event EventHandler<LauncherOperationActivitySnapshotEventArgs>? SnapshotChanged;

    LauncherOperationActivitySnapshot CurrentSnapshot { get; }
}

internal interface IGameActivitySource
{
    event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;

    GameRuntimeSnapshot CurrentSnapshot { get; }
}

internal interface IAddonsActivitySource
{
    event EventHandler<AddonsRuntimeSnapshotEventArgs>? SnapshotChanged;

    AddonsRuntimeSnapshot CurrentSnapshot { get; }
}

internal sealed class LauncherActivityCoordinator : IDisposable
{
    private const string GameTargetId = "wotlk-classic";
    private const string GameDisplayName = "WotLK Classic";

    private readonly object _sync = new();
    private readonly ILauncherOperationActivitySource _operations;
    private readonly IGameActivitySource _game;
    private readonly IAddonsActivitySource _addons;
    private readonly Dictionary<long, LauncherActivityRecentItem> _recentByOperation = [];
    private LauncherOperationActivitySnapshot _operationSnapshot =
        LauncherOperationActivitySnapshot.Initial;
    private GameRuntimeSnapshot? _gameSnapshot;
    private AddonsRuntimeSnapshot _addonsSnapshot = AddonsRuntimeSnapshot.Initial;
    private LauncherActivitySnapshot _currentSnapshot = LauncherActivitySnapshot.Initial;
    private long _latestOperationSequence = -1;
    private long _latestGameSequence = -1;
    private long _latestAddonsSequence = -1;
    private long _nextSequence;
    private int _disposeState;

    internal LauncherActivityCoordinator(
        LauncherOperationCoordinator operations,
        GameRuntimeCoordinator game,
        LauncherAddonsCoordinator addons)
        : this(
            new OperationActivitySource(operations),
            new GameActivitySource(game),
            new AddonsActivitySource(addons))
    {
    }

    internal LauncherActivityCoordinator(
        ILauncherOperationActivitySource operations,
        IGameActivitySource game,
        IAddonsActivitySource addons)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _addons = addons ?? throw new ArgumentNullException(nameof(addons));

        _operations.SnapshotChanged += Operations_SnapshotChanged;
        _game.SnapshotChanged += Game_SnapshotChanged;
        _addons.SnapshotChanged += Addons_SnapshotChanged;

        ApplyOperationSnapshot(_operations.CurrentSnapshot);
        ApplyGameSnapshot(_game.CurrentSnapshot);
        ApplyAddonsSnapshot(_addons.CurrentSnapshot);
    }

    internal event EventHandler<LauncherActivitySnapshotEventArgs>? SnapshotChanged;

    internal LauncherActivitySnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _operations.SnapshotChanged -= Operations_SnapshotChanged;
        _game.SnapshotChanged -= Game_SnapshotChanged;
        _addons.SnapshotChanged -= Addons_SnapshotChanged;
        lock (_sync)
        {
            SnapshotChanged = null;
        }
    }

    private void Operations_SnapshotChanged(
        object? sender,
        LauncherOperationActivitySnapshotEventArgs eventArgs) =>
        ApplyOperationSnapshot(eventArgs.Snapshot);

    private void Game_SnapshotChanged(
        object? sender,
        GameRuntimeSnapshotEventArgs eventArgs) =>
        ApplyGameSnapshot(eventArgs.Snapshot);

    private void Addons_SnapshotChanged(
        object? sender,
        AddonsRuntimeSnapshotEventArgs eventArgs) =>
        ApplyAddonsSnapshot(eventArgs.Snapshot);

    private void ApplyOperationSnapshot(LauncherOperationActivitySnapshot snapshot)
    {
        LauncherActivitySnapshot? published = null;
        lock (_sync)
        {
            if (IsDisposed || snapshot.Sequence <= _latestOperationSequence)
            {
                return;
            }

            _latestOperationSequence = snapshot.Sequence;
            _operationSnapshot = snapshot;
            published = RebuildUnsafe();
        }

        Publish(published);
    }

    private void ApplyGameSnapshot(GameRuntimeSnapshot snapshot)
    {
        LauncherActivitySnapshot? published = null;
        lock (_sync)
        {
            if (IsDisposed || snapshot.Sequence <= _latestGameSequence)
            {
                return;
            }

            _latestGameSequence = snapshot.Sequence;
            AddTerminalUnsafe(snapshot.TerminalResult);
            if (!IsObsoleteForActiveOperationUnsafe(snapshot.OperationId))
            {
                _gameSnapshot = snapshot;
            }
            published = RebuildUnsafe();
        }

        Publish(published);
    }

    private void ApplyAddonsSnapshot(AddonsRuntimeSnapshot snapshot)
    {
        LauncherActivitySnapshot? published = null;
        lock (_sync)
        {
            if (IsDisposed || snapshot.Sequence <= _latestAddonsSequence)
            {
                return;
            }

            _latestAddonsSequence = snapshot.Sequence;
            AddTerminalUnsafe(snapshot.TerminalResult);
            if (!IsObsoleteForActiveOperationUnsafe(snapshot.OperationId))
            {
                _addonsSnapshot = snapshot;
            }
            published = RebuildUnsafe();
        }

        Publish(published);
    }

    private bool IsObsoleteForActiveOperationUnsafe(long? operationId) =>
        _operationSnapshot is { IsActive: true, OperationId: long activeOperationId }
        && operationId is long sourceOperationId
        && sourceOperationId < activeOperationId;

    private void AddTerminalUnsafe(OperationTerminalResult? terminal)
    {
        if (terminal is null
            || !IsConnectedOperation(terminal.OperationType)
            || _recentByOperation.ContainsKey(terminal.OperationId))
        {
            return;
        }

        LauncherOperationDisplayContext? context = terminal.DisplayContext;
        LauncherActivityNavigationTarget navigation = GetNavigationTarget(
            terminal.OperationType);
        string targetId = context?.SubjectId ?? GetDefaultTargetId(terminal.OperationType);
        string targetName = context?.DisplayName ?? GetDefaultDisplayName(terminal.OperationType);
        _recentByOperation.Add(
            terminal.OperationId,
            new LauncherActivityRecentItem(
                terminal.OperationId,
                terminal.OperationType,
                terminal.Outcome,
                terminal.CompletedAt,
                targetId,
                targetName,
                terminal.ErrorCategory,
                navigation));

        while (_recentByOperation.Count > LauncherOperationActivityPolicy.RecentHistoryLimit)
        {
            long oldest = _recentByOperation.Values
                .OrderBy(item => item.CompletedAt)
                .ThenBy(item => item.OperationId)
                .First()
                .OperationId;
            _recentByOperation.Remove(oldest);
        }
    }

    private LauncherActivitySnapshot RebuildUnsafe()
    {
        LauncherActivityOperationSnapshot? active = ProjectActiveUnsafe();
        ImmutableArray<LauncherActivityPendingItem> pending = ProjectPendingUnsafe(active);
        ImmutableArray<LauncherActivityRecentItem> recent = _recentByOperation.Values
            .OrderByDescending(item => item.CompletedAt)
            .ThenByDescending(item => item.OperationId)
            .ToImmutableArray();
        _currentSnapshot = new LauncherActivitySnapshot(
            Sequence: ++_nextSequence,
            ActiveOperation: active,
            PendingItems: pending,
            RecentItems: recent);
        return _currentSnapshot;
    }

    private LauncherActivityOperationSnapshot? ProjectActiveUnsafe()
    {
        if (_operationSnapshot is not
            {
                IsActive: true,
                OperationId: long operationId,
                OperationType: LauncherOperationType operationType
            }
            || !IsConnectedOperation(operationType))
        {
            return null;
        }

        bool cancellationRequested =
            _operationSnapshot.CancellationReason == LauncherOperationCancellationReason.User;
        if (IsGameOperation(operationType)
            && _gameSnapshot?.OperationId == operationId)
        {
            return ProjectGameOperation(
                operationId,
                operationType,
                _gameSnapshot,
                cancellationRequested);
        }

        if (IsAddonOperation(operationType)
            && _addonsSnapshot.OperationId == operationId)
        {
            return ProjectAddonOperation(
                operationId,
                operationType,
                _addonsSnapshot,
                cancellationRequested);
        }

        LauncherActivityNavigationTarget navigation = GetNavigationTarget(operationType);
        return new LauncherActivityOperationSnapshot(
            operationId,
            operationType,
            GetDefaultTargetId(operationType),
            GetDefaultDisplayName(operationType),
            GetDefaultDisplayName(operationType),
            cancellationRequested
                ? LauncherActivityPhase.Cancelling
                : LauncherActivityPhase.Preparing,
            LauncherActivityProgressMode.Indeterminate,
            Percent: null,
            BytesProcessed: null,
            BytesTotal: null,
            BytesPerSecond: null,
            Eta: null,
            FilesProcessed: null,
            FilesTotal: null,
            _operationSnapshot.CanUserCancel,
            cancellationRequested,
            AddonPosition: null,
            AddonTotal: null,
            ErrorCategory: null,
            navigation);
    }

    private LauncherActivityOperationSnapshot ProjectGameOperation(
        long operationId,
        LauncherOperationType operationType,
        GameRuntimeSnapshot game,
        bool cancellationRequested)
    {
        double? percent = game.ProgressPercent;
        return new LauncherActivityOperationSnapshot(
            operationId,
            operationType,
            GameTargetId,
            GameDisplayName,
            GameDisplayName,
            cancellationRequested
                ? LauncherActivityPhase.Cancelling
                : MapGamePhase(operationType, game),
            percent is null
                ? LauncherActivityProgressMode.Indeterminate
                : LauncherActivityProgressMode.Determinate,
            percent,
            game.DownloadedBytes,
            game.TotalBytes,
            game.BytesPerSecond,
            game.Remaining,
            game.ProcessedFileCount,
            game.TotalFileCount,
            _operationSnapshot.CanUserCancel,
            cancellationRequested,
            AddonPosition: null,
            AddonTotal: null,
            game.ErrorCategory?.ToString() ?? game.FailureCategory,
            LauncherActivityNavigationTarget.Game);
    }

    private LauncherActivityOperationSnapshot ProjectAddonOperation(
        long operationId,
        LauncherOperationType operationType,
        AddonsRuntimeSnapshot addons,
        bool cancellationRequested)
    {
        string targetId = addons.ActiveAddonId;
        AddonRuntimeItem? item = addons.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, targetId, StringComparison.OrdinalIgnoreCase));
        string targetName = item?.Name
            ?? (operationType == LauncherOperationType.AddonBatchUpdate
                ? "Addons"
                : "Addon");
        bool progressMatches = string.Equals(
            addons.Progress.AddonId,
            targetId,
            StringComparison.OrdinalIgnoreCase);
        double? percent = operationType == LauncherOperationType.AddonRemove
            || !progressMatches
                ? null
                : addons.Progress.Percent;
        return new LauncherActivityOperationSnapshot(
            operationId,
            operationType,
            targetId,
            targetName,
            targetName,
            cancellationRequested
                ? LauncherActivityPhase.Cancelling
                : MapAddonPhase(addons),
            percent is null
                ? LauncherActivityProgressMode.Indeterminate
                : LauncherActivityProgressMode.Determinate,
            percent,
            progressMatches ? addons.Progress.BytesReceived : null,
            progressMatches ? addons.Progress.TotalBytes : null,
            progressMatches ? addons.Progress.BytesPerSecond : null,
            progressMatches ? addons.Progress.EstimatedRemaining : null,
            FilesProcessed: null,
            FilesTotal: null,
            _operationSnapshot.CanUserCancel,
            cancellationRequested,
            operationType == LauncherOperationType.AddonBatchUpdate
                ? addons.ActiveAddonPosition
                : null,
            operationType == LauncherOperationType.AddonBatchUpdate
                ? addons.ActiveAddonTotal
                : null,
            addons.Error.Category == AddonsErrorCategory.None
                ? null
                : addons.Error.Category.ToString(),
            LauncherActivityNavigationTarget.Addons);
    }

    private ImmutableArray<LauncherActivityPendingItem> ProjectPendingUnsafe(
        LauncherActivityOperationSnapshot? active)
    {
        if (active?.OperationType != LauncherOperationType.AddonBatchUpdate
            || _addonsSnapshot.OperationId != active.OperationId)
        {
            return ImmutableArray<LauncherActivityPendingItem>.Empty;
        }

        return _addonsSnapshot.PendingAddonIds
            .Where(id => !string.Equals(
                id,
                _addonsSnapshot.ActiveAddonId,
                StringComparison.OrdinalIgnoreCase))
            .Select(id =>
            {
                string name = _addonsSnapshot.Items.FirstOrDefault(item =>
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;
                return new LauncherActivityPendingItem(
                    id,
                    name,
                    LauncherOperationType.AddonBatchUpdate,
                    LauncherActivityNavigationTarget.Addons);
            })
            .ToImmutableArray();
    }

    private static LauncherActivityPhase MapGamePhase(
        LauncherOperationType operationType,
        GameRuntimeSnapshot game)
    {
        if (operationType == LauncherOperationType.GameVerify)
        {
            return game.Phase switch
            {
                GameVerificationPhase.CheckingLocalClient =>
                    LauncherActivityPhase.CheckingLocalClient,
                GameVerificationPhase.LoadingManifest => LauncherActivityPhase.LoadingManifest,
                GameVerificationPhase.ComparingManifest => LauncherActivityPhase.ComparingManifest,
                GameVerificationPhase.ScanningFiles => LauncherActivityPhase.ScanningFiles,
                _ => LauncherActivityPhase.Preparing
            };
        }

        return game.MaintenancePhase switch
        {
            GameClientMaintenancePhase.LoadingManifest => LauncherActivityPhase.LoadingManifest,
            GameClientMaintenancePhase.ManifestLoaded
                or GameClientMaintenancePhase.GameProcessesStopped =>
                LauncherActivityPhase.Preparing,
            GameClientMaintenancePhase.ComparingManifest
                or GameClientMaintenancePhase.ComparisonCompleted =>
                LauncherActivityPhase.ComparingManifest,
            GameClientMaintenancePhase.ScanningFiles
                or GameClientMaintenancePhase.FullVerification =>
                LauncherActivityPhase.ScanningFiles,
            GameClientMaintenancePhase.Cleaning
                or GameClientMaintenancePhase.CleanupCompleted =>
                LauncherActivityPhase.Cleaning,
            GameClientMaintenancePhase.DownloadingStarted
                or GameClientMaintenancePhase.DownloadingFile
                or GameClientMaintenancePhase.Downloading
                or GameClientMaintenancePhase.RepairDownloading =>
                LauncherActivityPhase.Downloading,
            GameClientMaintenancePhase.RepairApplying => LauncherActivityPhase.Applying,
            GameClientMaintenancePhase.CacheSaved
                or GameClientMaintenancePhase.Registering
                or GameClientMaintenancePhase.RegistrationCompleted
                or GameClientMaintenancePhase.Completed =>
                LauncherActivityPhase.Finalizing,
            _ => LauncherActivityPhase.Preparing
        };
    }

    private static LauncherActivityPhase MapAddonPhase(AddonsRuntimeSnapshot addons) =>
        addons.OperationPhase switch
        {
            AddonsOperationPhase.Downloading => LauncherActivityPhase.Downloading,
            AddonsOperationPhase.Removing => LauncherActivityPhase.Removing,
            _ => LauncherActivityPhase.Preparing
        };

    private static bool IsGameOperation(LauncherOperationType operationType) =>
        operationType is LauncherOperationType.GameInstall
            or LauncherOperationType.GameUpdate
            or LauncherOperationType.GameVerify
            or LauncherOperationType.GameRepair;

    private static bool IsAddonOperation(LauncherOperationType operationType) =>
        operationType is LauncherOperationType.AddonInstall
            or LauncherOperationType.AddonUpdate
            or LauncherOperationType.AddonRepair
            or LauncherOperationType.AddonRemove
            or LauncherOperationType.AddonBatchUpdate;

    private static bool IsConnectedOperation(LauncherOperationType operationType) =>
        IsGameOperation(operationType) || IsAddonOperation(operationType);

    private static LauncherActivityNavigationTarget GetNavigationTarget(
        LauncherOperationType operationType) => IsGameOperation(operationType)
            ? LauncherActivityNavigationTarget.Game
            : IsAddonOperation(operationType)
                ? LauncherActivityNavigationTarget.Addons
                : LauncherActivityNavigationTarget.None;

    private static string GetDefaultTargetId(LauncherOperationType operationType) =>
        IsGameOperation(operationType)
            ? GameTargetId
            : operationType == LauncherOperationType.LauncherAutoUpdate
                ? "atlas-launcher"
                : "addons";

    private static string GetDefaultDisplayName(LauncherOperationType operationType) =>
        IsGameOperation(operationType)
            ? GameDisplayName
            : operationType == LauncherOperationType.LauncherAutoUpdate
                ? "Atlas Launcher"
                : "Addons";

    private bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    private void Publish(LauncherActivitySnapshot snapshot)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            SnapshotChanged?.Invoke(this, new LauncherActivitySnapshotEventArgs(snapshot));
        }
        catch
        {
            // Presentation observers cannot affect runtime operation ownership.
        }
    }

    private sealed class OperationActivitySource(
        LauncherOperationCoordinator source) : ILauncherOperationActivitySource
    {
        public event EventHandler<LauncherOperationActivitySnapshotEventArgs>? SnapshotChanged
        {
            add => source.ActivityChanged += value;
            remove => source.ActivityChanged -= value;
        }

        public LauncherOperationActivitySnapshot CurrentSnapshot =>
            source.CurrentActivitySnapshot;
    }

    private sealed class GameActivitySource(
        GameRuntimeCoordinator source) : IGameActivitySource
    {
        public event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged
        {
            add => source.SnapshotChanged += value;
            remove => source.SnapshotChanged -= value;
        }

        public GameRuntimeSnapshot CurrentSnapshot => source.CurrentSnapshot;
    }

    private sealed class AddonsActivitySource(
        LauncherAddonsCoordinator source) : IAddonsActivitySource
    {
        public event EventHandler<AddonsRuntimeSnapshotEventArgs>? SnapshotChanged
        {
            add => source.SnapshotChanged += value;
            remove => source.SnapshotChanged -= value;
        }

        public AddonsRuntimeSnapshot CurrentSnapshot => source.CurrentSnapshot;
    }
}
