using System.Globalization;
using System.Windows.Threading;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed record GameViewState(
    GamePreviewScenario Scenario,
    GameSemanticTone SemanticTone,
    string ClientStatus,
    string PrimaryActionLabel,
    bool IsPrimaryActionEnabled,
    bool IsOptionsEnabled,
    bool IsVerifyEnabled,
    bool IsRetryEnabled,
    string InstallBadgeText,
    string ClientVersion,
    string AvailableClientVersion,
    string InstallPath,
    string Language,
    bool IsClientReady,
    double Progress,
    bool IsProgressIndeterminate,
    string ProgressTitle,
    string ProgressPercentText,
    string ProgressPrimaryDetail,
    string ProgressSecondaryDetail,
    string ErrorTitle,
    string ErrorSummary,
    string PrimaryActionUnavailableReason,
    bool IsLaunchInProgress = false,
    bool IsGameRunning = false);

internal sealed class GameStateAdapter : IDisposable
{
    private readonly GameUiState _target;
    private readonly IGameVerificationRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private long _latestSequence;
    private long _latestOperationId;
    private long _latestPlayAttemptId;
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
        if (snapshot.PlayLaunchPhase != GameLaunchPhase.Idle)
        {
            return ProjectLaunch(snapshot);
        }

        return snapshot.ViewMode switch
        {
            GameViewMode.Verifying => ProjectVerifying(snapshot),
            GameViewMode.Downloading or GameViewMode.Installing => ProjectMaintenance(snapshot),
            GameViewMode.Error => ProjectError(snapshot),
            GameViewMode.NotInstalled => Stable(
                GamePreviewScenario.NotInstalled,
                GameSemanticTone.Warning,
                "Client non installé",
                "Installer",
                "Non installé",
                snapshot,
                isClientReady: false),
            GameViewMode.UpdateAvailable => Stable(
                GamePreviewScenario.UpdateAvailable,
                GameSemanticTone.Warning,
                "Mise à jour disponible",
                "Mettre à jour",
                "Mise à jour",
                snapshot,
                isClientReady: true),
            _ => ProjectReady(snapshot)
        };
    }

    private static GameViewState ProjectVerifying(GameRuntimeSnapshot snapshot)
    {
        bool fullRepair = snapshot.OperationKind == LauncherOperationKind.GameRepair;
        bool hasCount = (snapshot.Phase == GameVerificationPhase.ScanningFiles
                || snapshot.MaintenancePhase == GameClientMaintenancePhase.FullVerification)
            && snapshot.ProcessedFileCount is >= 0
            && snapshot.TotalFileCount is > 0;
        double progress = hasCount
            ? Math.Clamp(
                snapshot.ProcessedFileCount!.Value * 100d
                    / snapshot.TotalFileCount!.Value,
                0,
                100)
            : 0;
        string primaryDetail = fullRepair
            ? snapshot.MaintenancePhase switch
            {
                GameClientMaintenancePhase.LoadingManifest => "Chargement du manifeste",
                GameClientMaintenancePhase.ManifestLoaded => "Manifeste reçu",
                GameClientMaintenancePhase.FullVerification => "Rehachage des fichiers gérés",
                GameClientMaintenancePhase.ComparisonCompleted => "Plan de réparation prêt",
                _ => "Analyse complète du client"
            }
            : snapshot.Phase switch
        {
            GameVerificationPhase.LoadingManifest => "Chargement du manifeste",
            GameVerificationPhase.ComparingManifest => "Comparaison avec le cache local",
            GameVerificationPhase.ScanningFiles => "Analyse des fichiers locaux",
            _ => "Analyse du client"
        };

        bool canPlayDuringAutomaticVerification = !fullRepair
            && snapshot.Action == GameAction.Play
            && snapshot.IsPlayable
            && snapshot.CanPrimaryAction;
        return Create(
            GamePreviewScenario.Verifying,
            GameSemanticTone.Accent,
            fullRepair ? "Vérification complète" : "Vérification en cours",
            fullRepair && snapshot.CanUserCancel
                ? "Annuler"
                : canPlayDuringAutomaticVerification
                    ? "Jouer"
                    : "Vérification…",
            isPrimaryActionEnabled: fullRepair
                ? snapshot.CanPrimaryAction
                : canPlayDuringAutomaticVerification,
            snapshot,
            "Analyse",
            progress,
            isProgressIndeterminate: !hasCount,
            fullRepair ? "Vérification complète" : "Vérification des fichiers",
            hasCount ? $"{Math.Round(progress):0} %" : string.Empty,
            primaryDetail,
            hasCount
                ? $"{snapshot.ProcessedFileCount}/{snapshot.TotalFileCount} fichiers parcourus"
                : "Progression indéterminée");
    }

    private static GameViewState ProjectLaunch(GameRuntimeSnapshot snapshot)
    {
        string badge = snapshot.UpdateKnowledge switch
        {
            GameUpdateKnowledge.Known => "À jour",
            GameUpdateKnowledge.Unavailable => "Vérification indisponible",
            _ => "Non vérifié"
        };
        if (snapshot.PlayLaunchPhase == GameLaunchPhase.WaitingForAuthentication)
        {
            return Stable(
                GamePreviewScenario.Ready,
                GameSemanticTone.Neutral,
                "Connexion requise",
                "Jouer",
                badge,
                snapshot,
                isClientReady: true);
        }

        if (snapshot.PlayLaunchPhase is GameLaunchPhase.RequestingTicket
            or GameLaunchPhase.PreparingSso
            or GameLaunchPhase.StartingProcess
            or GameLaunchPhase.Started)
        {
            return Stable(
                    GamePreviewScenario.Ready,
                    GameSemanticTone.Accent,
                    "En cours de lancement",
                    "En cours de lancement",
                    badge,
                    snapshot,
                    isClientReady: true)
                with
                {
                    IsPrimaryActionEnabled = false,
                    IsLaunchInProgress = true
                };
        }

        if (snapshot.PlayLaunchPhase == GameLaunchPhase.Running)
        {
            return Stable(
                    GamePreviewScenario.Ready,
                    GameSemanticTone.Success,
                    "Jeu en cours d’utilisation",
                    "Jeu en cours d’utilisation",
                    badge,
                    snapshot,
                    isClientReady: true)
                with
                {
                    IsPrimaryActionEnabled = false,
                    IsGameRunning = true
                };
        }

        string failureStatus = snapshot.LastPlayOutcome switch
        {
            GameLaunchOutcome.NetworkUnavailable
                or GameLaunchOutcome.ServiceUnavailable => "Service Atlas indisponible",
            GameLaunchOutcome.AuthenticationRequired
                or GameLaunchOutcome.TicketFailed => "Connexion requise",
            GameLaunchOutcome.ExecutableMissing => "Lanceur Arctium introuvable",
            GameLaunchOutcome.AccessDenied => "Accès au lancement refusé",
            GameLaunchOutcome.SsoFailed => "Connexion locale impossible",
            _ => "Lancement impossible"
        };
        return Stable(
            GamePreviewScenario.Ready,
            GameSemanticTone.Error,
            failureStatus,
            "Jouer",
            badge,
            snapshot,
            isClientReady: true);
    }

    private static GameViewState ProjectMaintenance(GameRuntimeSnapshot snapshot)
    {
        bool repairing = snapshot.OperationKind == LauncherOperationKind.GameRepair;
        bool finalizing = snapshot.IsFinalizing;
        bool downloading = snapshot.ViewMode == GameViewMode.Downloading;
        bool hasByteProgress = downloading
            && snapshot.DownloadedBytes is >= 0
            && snapshot.TotalBytes is > 0;
        bool hasFileProgress = !finalizing
            && !hasByteProgress
            && snapshot.ProcessedFileCount is >= 0
            && snapshot.TotalFileCount is > 0;
        double progress = hasByteProgress
            ? Math.Clamp(
                snapshot.DownloadedBytes!.Value * 100d / snapshot.TotalBytes!.Value,
                0,
                100)
            : hasFileProgress
                ? Math.Clamp(
                    snapshot.ProcessedFileCount!.Value * 100d
                        / snapshot.TotalFileCount!.Value,
                    0,
                    100)
                : 0;
        string status = finalizing
            ? "Finalisation…"
            : repairing
                ? "Réparation en cours"
                : downloading
                    ? IsDownloadTransfer(snapshot.MaintenancePhase)
                        ? "Téléchargement en cours"
                        : "Préparation…"
                    : "Installation en cours";
        string primaryLabel = snapshot.CanUserCancel ? "Annuler" : status;
        string title = finalizing
            ? "Finalisation du client"
            : repairing && snapshot.MaintenancePhase == GameClientMaintenancePhase.RepairApplying
                ? "Application de la réparation"
                : repairing && snapshot.MaintenancePhase is (
                    GameClientMaintenancePhase.Cleaning
                    or GameClientMaintenancePhase.CleanupCompleted)
                    ? "Nettoyage des anciens fichiers"
                    : repairing
                        ? "Téléchargement des fichiers à réparer"
                        : downloading
                            ? IsDownloadTransfer(snapshot.MaintenancePhase)
                                ? "Téléchargement du client"
                                : "Préparation du client"
                            : snapshot.Action == GameAction.Update
                                ? "Application de la mise à jour"
                                : "Installation du client";
        string primaryDetail = hasByteProgress
            ? $"{FormatBytes(snapshot.DownloadedBytes!.Value)} / {FormatBytes(snapshot.TotalBytes!.Value)}"
            : hasFileProgress
                ? $"{snapshot.ProcessedFileCount}/{snapshot.TotalFileCount} fichiers"
                : GetMaintenanceDetail(snapshot.MaintenancePhase);
        string secondaryDetail = BuildTransferDetail(snapshot);
        if (string.IsNullOrWhiteSpace(secondaryDetail))
        {
            secondaryDetail = finalizing
                ? "Enregistrement de l’installation"
                : downloading
                    ? snapshot.CurrentFile ?? "Analyse des fichiers nécessaires"
                    : "Écriture des fichiers du client";
        }

        return Create(
            downloading ? GamePreviewScenario.Downloading : GamePreviewScenario.Installing,
            GameSemanticTone.Accent,
            status,
            primaryLabel,
            snapshot.CanPrimaryAction,
            snapshot,
            hasByteProgress || hasFileProgress ? $"{Math.Round(progress):0} %" : "En cours",
            progress,
            isProgressIndeterminate: !hasByteProgress && !hasFileProgress,
            title,
            hasByteProgress || hasFileProgress ? $"{Math.Round(progress):0} %" : string.Empty,
            primaryDetail,
            secondaryDetail);
    }

    private static GameViewState ProjectError(GameRuntimeSnapshot snapshot)
    {
        return Create(
            GamePreviewScenario.Error,
            GameSemanticTone.Error,
            "Une erreur est survenue",
            "Réessayer",
            snapshot.CanPrimaryAction,
            snapshot,
            "Erreur",
            progress: 0,
            isProgressIndeterminate: false,
            progressTitle: string.Empty,
            progressPercentText: string.Empty,
            progressPrimaryDetail: string.Empty,
            progressSecondaryDetail: string.Empty,
            errorTitle: snapshot.ErrorTitle ?? "Opération interrompue",
            errorSummary: snapshot.ErrorSummary
                ?? "L’opération n’a pas pu être terminée. Consulte le diagnostic.");
    }

    private static GameViewState ProjectReady(GameRuntimeSnapshot snapshot)
    {
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
            badge,
            snapshot,
            isClientReady: true);
    }

    private static GameViewState Stable(
        GamePreviewScenario scenario,
        GameSemanticTone tone,
        string status,
        string primaryLabel,
        string badge,
        GameRuntimeSnapshot snapshot,
        bool isClientReady)
    {
        return new GameViewState(
            scenario,
            tone,
            status,
            primaryLabel,
            snapshot.CanPrimaryAction,
            IsOptionsEnabled: false,
            snapshot.CanVerify,
            IsRetryEnabled: false,
            badge,
            FormatInstalledVersion(snapshot.InstalledVersion),
            snapshot.AvailableVersion ?? string.Empty,
            snapshot.InstallPath,
            FormatLanguage(snapshot.GameLocale),
            isClientReady,
            Progress: 0,
            IsProgressIndeterminate: false,
            ProgressTitle: string.Empty,
            ProgressPercentText: string.Empty,
            ProgressPrimaryDetail: string.Empty,
            ProgressSecondaryDetail: string.Empty,
            ErrorTitle: string.Empty,
            ErrorSummary: string.Empty,
            snapshot.PrimaryActionUnavailableReason ?? string.Empty);
    }

    private static GameViewState Create(
        GamePreviewScenario scenario,
        GameSemanticTone tone,
        string status,
        string primaryLabel,
        bool isPrimaryActionEnabled,
        GameRuntimeSnapshot snapshot,
        string badge,
        double progress,
        bool isProgressIndeterminate,
        string progressTitle,
        string progressPercentText,
        string progressPrimaryDetail,
        string progressSecondaryDetail,
        string errorTitle = "",
        string errorSummary = "")
    {
        return new GameViewState(
            scenario,
            tone,
            status,
            primaryLabel,
            isPrimaryActionEnabled,
            IsOptionsEnabled: false,
            snapshot.CanVerify,
            IsRetryEnabled: scenario == GamePreviewScenario.Error && isPrimaryActionEnabled,
            badge,
            FormatInstalledVersion(snapshot.InstalledVersion),
            snapshot.AvailableVersion ?? string.Empty,
            snapshot.InstallPath,
            FormatLanguage(snapshot.GameLocale),
            snapshot.IsPlayable,
            progress,
            isProgressIndeterminate,
            progressTitle,
            progressPercentText,
            progressPrimaryDetail,
            progressSecondaryDetail,
            errorTitle,
            errorSummary,
            snapshot.PrimaryActionUnavailableReason ?? string.Empty);
    }

    private static bool IsDownloadTransfer(GameClientMaintenancePhase? phase)
    {
        return phase is GameClientMaintenancePhase.DownloadingStarted
            or GameClientMaintenancePhase.DownloadingFile
            or GameClientMaintenancePhase.Downloading
            or GameClientMaintenancePhase.RepairDownloading;
    }

    private static string GetMaintenanceDetail(GameClientMaintenancePhase? phase)
    {
        return phase switch
        {
            GameClientMaintenancePhase.LoadingManifest => "Chargement du manifeste",
            GameClientMaintenancePhase.ManifestLoaded => "Manifeste reçu",
            GameClientMaintenancePhase.GameProcessesStopped => "Préparation des fichiers",
            GameClientMaintenancePhase.ComparingManifest => "Comparaison du client local",
            GameClientMaintenancePhase.ScanningFiles => "Analyse des fichiers locaux",
            GameClientMaintenancePhase.FullVerification => "Rehachage des fichiers gérés",
            GameClientMaintenancePhase.ComparisonCompleted => "Analyse terminée",
            GameClientMaintenancePhase.Cleaning => "Nettoyage des anciens fichiers",
            GameClientMaintenancePhase.CleanupCompleted => "Nettoyage terminé",
            GameClientMaintenancePhase.RepairDownloading => "Téléchargement de la réparation",
            GameClientMaintenancePhase.RepairApplying => "Application des fichiers réparés",
            _ => "Préparation du client"
        };
    }

    private static string BuildTransferDetail(GameRuntimeSnapshot snapshot)
    {
        List<string> parts = [];
        if (snapshot.BytesPerSecond is > 0)
        {
            parts.Add($"{FormatBytes((long)snapshot.BytesPerSecond.Value)}/s");
        }

        if (snapshot.Remaining is TimeSpan remaining && remaining > TimeSpan.Zero)
        {
            parts.Add(remaining.TotalMinutes >= 1
                ? $"{Math.Ceiling(remaining.TotalMinutes):0} min restantes"
                : $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0} s restantes");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CurrentFile))
        {
            parts.Add(snapshot.CurrentFile);
        }

        return string.Join(" · ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["o", "Ko", "Mo", "Go"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = unit == 0 ? "0" : "0.00";
        return value.ToString(format, CultureInfo.GetCultureInfo("fr-FR"))
            + " "
            + units[unit];
    }

    private static string FormatInstalledVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? "Inconnue" : version;
    }

    private static string FormatLanguage(string locale)
    {
        return string.Equals(locale, "enUS", StringComparison.OrdinalIgnoreCase)
            ? "English"
            : "Français";
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
                && operationId < _latestOperationId
            || snapshot.PlayAttemptId is long playAttemptId
                && playAttemptId < _latestPlayAttemptId)
        {
            return;
        }

        if (snapshot.OperationId is long currentOperationId)
        {
            _latestOperationId = Math.Max(_latestOperationId, currentOperationId);
        }


        if (snapshot.PlayAttemptId is long currentPlayAttemptId)
        {
            _latestPlayAttemptId = Math.Max(_latestPlayAttemptId, currentPlayAttemptId);
        }

        GameViewState viewState = Project(snapshot);
        _latestSequence = snapshot.Sequence;
        _target.ApplyRuntimeView(viewState);
    }
}
