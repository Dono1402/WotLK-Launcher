using System.Windows.Threading;
using WotLK.Launcher.Game;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed record GameViewState(
    GamePreviewScenario Scenario,
    GameSemanticTone SemanticTone,
    string ClientStatus,
    string PrimaryActionLabel,
    bool IsPrimaryActionEnabled,
    bool IsVerifyEnabled,
    string InstallBadgeText,
    string AvailableClientVersion,
    bool IsClientReady,
    double Progress,
    bool IsProgressIndeterminate,
    string ProgressTitle,
    string ProgressPercentText,
    string ProgressPrimaryDetail,
    string ProgressSecondaryDetail);

internal sealed class GameStateAdapter : IDisposable
{
    private readonly GameUiState _target;
    private readonly IGameVerificationRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private long _latestSequence;
    private long _latestOperationId;
    private int _disposeState;

    internal GameStateAdapter(
        GameUiState target,
        IGameVerificationRuntime runtime,
        Dispatcher dispatcher)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
        ApplyOrQueue(_runtime.CurrentSnapshot);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
        }
    }

    internal static GameViewState Project(GameRuntimeSnapshot snapshot)
    {
        if (snapshot.IsVerifying)
        {
            bool hasCount = snapshot.Phase == GameVerificationPhase.ScanningFiles
                && snapshot.ProcessedFileCount is > 0
                && snapshot.TotalFileCount is > 0;
            double progress = hasCount
                ? Math.Clamp(
                    snapshot.ProcessedFileCount!.Value * 100d
                        / snapshot.TotalFileCount!.Value,
                    0,
                    100)
                : 0;
            string primaryDetail = snapshot.Phase switch
            {
                GameVerificationPhase.LoadingManifest => "Chargement du manifeste",
                GameVerificationPhase.ComparingManifest => "Comparaison avec le cache local",
                GameVerificationPhase.ScanningFiles => "Analyse des fichiers locaux",
                _ => "Analyse du client"
            };

            return new GameViewState(
                GamePreviewScenario.Verifying,
                GameSemanticTone.Accent,
                "Vérification en cours",
                "Vérification…",
                IsPrimaryActionEnabled: false,
                IsVerifyEnabled: false,
                "Analyse",
                snapshot.AvailableVersion ?? string.Empty,
                snapshot.IsPlayable,
                progress,
                IsProgressIndeterminate: !hasCount,
                "Vérification des fichiers",
                hasCount
                    ? $"{snapshot.ProcessedFileCount}/{snapshot.TotalFileCount}"
                    : string.Empty,
                primaryDetail,
                hasCount
                    ? "Comptage des fichiers effectivement parcourus"
                    : "Progression indéterminée");
        }

        if (snapshot.Action == GameAction.Install)
        {
            return Stable(
                GamePreviewScenario.NotInstalled,
                GameSemanticTone.Warning,
                "Client non installé",
                "Installer",
                snapshot.CanVerify,
                "Non installé",
                snapshot,
                isClientReady: false);
        }

        if (snapshot.Action == GameAction.Update)
        {
            return Stable(
                GamePreviewScenario.UpdateAvailable,
                GameSemanticTone.Warning,
                "Mise à jour disponible",
                "Mettre à jour",
                snapshot.CanVerify,
                "Mise à jour",
                snapshot,
                isClientReady: true);
        }

        string badge = snapshot.UpdateKnowledge switch
        {
            GameUpdateKnowledge.Known => "À jour",
            GameUpdateKnowledge.Unavailable => "Vérification indisponible",
            _ => "Non vérifié"
        };
        GameSemanticTone tone = snapshot.UpdateKnowledge == GameUpdateKnowledge.Known
            ? GameSemanticTone.Success
            : GameSemanticTone.Neutral;
        return Stable(
            GamePreviewScenario.Ready,
            tone,
            "Client prêt",
            "Jouer",
            snapshot.CanVerify,
            badge,
            snapshot,
            isClientReady: true);
    }

    private static GameViewState Stable(
        GamePreviewScenario scenario,
        GameSemanticTone tone,
        string status,
        string primaryLabel,
        bool canVerify,
        string badge,
        GameRuntimeSnapshot snapshot,
        bool isClientReady)
    {
        return new GameViewState(
            scenario,
            tone,
            status,
            primaryLabel,
            IsPrimaryActionEnabled: false,
            IsVerifyEnabled: canVerify,
            badge,
            snapshot.AvailableVersion ?? string.Empty,
            isClientReady,
            Progress: 0,
            IsProgressIndeterminate: false,
            ProgressTitle: string.Empty,
            ProgressPercentText: string.Empty,
            ProgressPrimaryDetail: string.Empty,
            ProgressSecondaryDetail: string.Empty);
    }

    private void Runtime_SnapshotChanged(
        object? sender,
        GameRuntimeSnapshotEventArgs eventArgs)
    {
        ApplyOrQueue(eventArgs.Snapshot);
    }

    private void ApplyOrQueue(GameRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Apply(snapshot);
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => Apply(snapshot)));
    }

    private void Apply(GameRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0
            || snapshot.Sequence <= _latestSequence
            || snapshot.OperationId is long operationId
                && operationId < _latestOperationId)
        {
            return;
        }

        if (snapshot.OperationId is long currentOperationId)
        {
            _latestOperationId = Math.Max(_latestOperationId, currentOperationId);
        }

        GameViewState viewState = Project(snapshot);
        _latestSequence = snapshot.Sequence;
        _target.ApplyRuntimeView(viewState);
    }
}
