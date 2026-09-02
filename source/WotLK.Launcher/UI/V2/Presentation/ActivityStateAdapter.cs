using System.Collections.Immutable;
using System.Globalization;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class ActivityStateAdapter : IDisposable
{
    private const string AddonIconRoot =
        "/WotLK.Launcher;component/Assets/Launcher/addon-icons/";
    private const string GameIcon =
        "/WotLK.Launcher;component/Assets/AppIcon.png";
    private const string LauncherIcon =
        "/WotLK.Launcher;component/Assets/Branding/AtlasLauncherLogo.png";

    private static readonly ImmutableHashSet<string> PackagedAddonIcons =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "atlaslootclassic",
            "attune",
            "auctionator",
            "baganator",
            "dbm",
            "details",
            "elvui",
            "leatrix-maps",
            "leatrix-plus",
            "nova-instance-tracker",
            "questie",
            "weakauras",
            "whats-training");

    private readonly ActivityUiState _target;
    private readonly LauncherActivityCoordinator _runtime;
    private readonly Dispatcher _dispatcher;
    private long _latestSequence = -1;
    private int _disposeState;

    internal ActivityStateAdapter(
        ActivityUiState target,
        LauncherActivityCoordinator runtime,
        Dispatcher dispatcher)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
        ApplyOrQueue(_runtime.CurrentSnapshot);
    }

    internal static ActivityViewState Project(LauncherActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ActivityViewState(
            IsPreview: false,
            ActiveOperation: snapshot.ActiveOperation is null
                ? null
                : ProjectActive(snapshot.ActiveOperation),
            PendingOperations: snapshot.PendingItems
                .Select(ProjectPending)
                .ToImmutableArray(),
            RecentOperations: snapshot.RecentItems
                .Select(ProjectRecent)
                .ToImmutableArray());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
        }
    }

    private static ActivityOperationUiItem ProjectActive(
        LauncherActivityOperationSnapshot operation)
    {
        (string iconUri, bool hasIcon) = ResolveIcon(
            operation.OperationType,
            operation.TargetId);
        string batchPosition = operation.AddonPosition is int position
            && operation.AddonTotal is int total
            && total > 0
                ? $"{position} sur {total}"
                : string.Empty;
        return new ActivityOperationUiItem(
            ProductName: operation.DisplayName,
            ActionName: operation.IsCancellationRequested
                ? "Annulation…"
                : MapAction(operation.OperationType),
            PhaseText: MapPhase(operation),
            ProgressPercent: operation.Percent,
            IsIndeterminate:
                operation.ProgressMode == LauncherActivityProgressMode.Indeterminate,
            TransferText: FormatTransfer(operation.BytesProcessed, operation.BytesTotal),
            RateAndEtaText: FormatRateAndEta(operation.BytesPerSecond, operation.Eta),
            DetailText: FormatFileProgress(operation.FilesProcessed, operation.FilesTotal),
            IconUri: iconUri,
            HasIcon: hasIcon,
            CanUserCancel: operation.CanUserCancel,
            IsCancellationRequested: operation.IsCancellationRequested,
            ErrorMessage: MapActiveError(operation.ErrorCategory),
            BatchPosition: batchPosition,
            OperationId: operation.OperationId,
            TargetId: operation.TargetId,
            NavigationTarget: MapNavigation(operation.NavigationTarget));
    }

    private static ActivityPendingUiItem ProjectPending(LauncherActivityPendingItem item)
    {
        (string iconUri, bool hasIcon) = ResolveIcon(item.OperationType, item.TargetId);
        return new ActivityPendingUiItem(
            item.TargetName,
            "En attente",
            iconUri,
            hasIcon,
            item.TargetId);
    }

    private static ActivityRecentUiItem ProjectRecent(LauncherActivityRecentItem item)
    {
        (string iconUri, bool hasIcon) = ResolveIcon(item.OperationType, item.TargetId);
        return new ActivityRecentUiItem(
            ProductName: item.OperationType == LauncherOperationType.AddonBatchUpdate
                ? "Mise à jour des addons"
                : item.TargetName,
            ResultText: MapResult(item.OperationType, item.Outcome),
            CompletedAtText: item.CompletedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
            Outcome: item.Outcome switch
            {
                LauncherOperationOutcome.Succeeded => ActivityRecentOutcome.Succeeded,
                LauncherOperationOutcome.Failed => ActivityRecentOutcome.Failed,
                _ => ActivityRecentOutcome.Cancelled
            },
            NavigationTarget: MapNavigation(item.NavigationTarget),
            IconUri: iconUri,
            HasIcon: hasIcon,
            OperationId: item.OperationId,
            TargetId: item.TargetId);
    }

    private static string MapAction(LauncherOperationType operationType) => operationType switch
    {
        LauncherOperationType.GameInstall => "Installation",
        LauncherOperationType.GameUpdate => "Mise à jour",
        LauncherOperationType.GameVerify => "Vérification",
        LauncherOperationType.GameRepair => "Réparation",
        LauncherOperationType.AddonInstall => "Installation",
        LauncherOperationType.AddonUpdate => "Mise à jour",
        LauncherOperationType.AddonRepair => "Réparation",
        LauncherOperationType.AddonRemove => "Suppression",
        LauncherOperationType.AddonBatchUpdate => "Mise à jour des addons",
        LauncherOperationType.LauncherAutoUpdate => "Mise à jour",
        _ => "Opération en cours"
    };

    private static string MapPhase(LauncherActivityOperationSnapshot operation)
    {
        if (operation.IsCancellationRequested
            || operation.Phase == LauncherActivityPhase.Cancelling)
        {
            return "Arrêt de l’opération en cours…";
        }

        return operation.Phase switch
        {
            LauncherActivityPhase.CheckingLocalClient => "Analyse du client local…",
            LauncherActivityPhase.LoadingManifest => "Chargement du manifeste…",
            LauncherActivityPhase.ComparingManifest => "Comparaison des fichiers…",
            LauncherActivityPhase.ScanningFiles =>
                operation.OperationType == LauncherOperationType.GameRepair
                    ? "Vérification complète des fichiers…"
                    : "Analyse des fichiers locaux…",
            LauncherActivityPhase.Cleaning => "Nettoyage des anciens fichiers…",
            LauncherActivityPhase.Downloading => operation.OperationType switch
            {
                LauncherOperationType.LauncherAutoUpdate =>
                    "Téléchargement d’Atlas Launcher…",
                LauncherOperationType.AddonInstall
                    or LauncherOperationType.AddonUpdate
                    or LauncherOperationType.AddonRepair
                    or LauncherOperationType.AddonBatchUpdate =>
                    "Téléchargement de l’addon",
                _ => "Téléchargement des fichiers du client"
            },
            LauncherActivityPhase.Applying =>
                operation.OperationType == LauncherOperationType.LauncherAutoUpdate
                    ? "Validation de la mise à jour…"
                    : "Application des fichiers réparés…",
            LauncherActivityPhase.Finalizing =>
                operation.OperationType == LauncherOperationType.LauncherAutoUpdate
                    ? "Préparation du redémarrage…"
                    : "Finalisation de l’installation…",
            LauncherActivityPhase.Removing => "Suppression des fichiers gérés…",
            _ => "Préparation de l’opération…"
        };
    }

    private static string MapResult(
        LauncherOperationType operationType,
        LauncherOperationOutcome outcome)
    {
        if (outcome == LauncherOperationOutcome.Cancelled)
        {
            return operationType switch
            {
                LauncherOperationType.GameVerify => "Vérification annulée",
                LauncherOperationType.GameRepair => "Réparation annulée",
                LauncherOperationType.AddonRemove => "Suppression annulée",
                _ => "Opération annulée"
            };
        }

        if (outcome == LauncherOperationOutcome.Failed)
        {
            return operationType switch
            {
                LauncherOperationType.GameInstall => "Installation échouée",
                LauncherOperationType.GameVerify => "Vérification échouée",
                LauncherOperationType.GameRepair => "Réparation échouée",
                LauncherOperationType.AddonInstall => "Installation échouée",
                LauncherOperationType.AddonRepair => "Réparation échouée",
                LauncherOperationType.AddonRemove => "Suppression échouée",
                _ => "Mise à jour échouée"
            };
        }

        return operationType switch
        {
            LauncherOperationType.GameInstall => "Installation terminée",
            LauncherOperationType.GameUpdate => "Mise à jour terminée",
            LauncherOperationType.GameVerify => "Vérification terminée",
            LauncherOperationType.GameRepair => "Réparation terminée",
            LauncherOperationType.AddonInstall => "Installé",
            LauncherOperationType.AddonUpdate => "Mis à jour",
            LauncherOperationType.AddonRepair => "Réparé",
            LauncherOperationType.AddonRemove => "Supprimé",
            LauncherOperationType.AddonBatchUpdate => "Mise à jour terminée",
            LauncherOperationType.LauncherAutoUpdate => "Mise à jour terminée",
            _ => "Terminé"
        };
    }

    private static string FormatTransfer(long? processed, long? total)
    {
        if (processed is not long processedBytes)
        {
            return string.Empty;
        }

        return total is long totalBytes
            ? $"{FormatBytes(processedBytes)} / {FormatBytes(totalBytes)}"
            : FormatBytes(processedBytes);
    }

    private static string FormatRateAndEta(double? bytesPerSecond, TimeSpan? eta)
    {
        List<string> parts = [];
        if (bytesPerSecond is > 0)
        {
            parts.Add($"{FormatBytes((long)bytesPerSecond.Value)}/s");
        }
        if (eta is TimeSpan remaining && remaining > TimeSpan.Zero)
        {
            parts.Add(remaining.TotalMinutes >= 1
                ? $"{Math.Ceiling(remaining.TotalMinutes):0} min restantes"
                : $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0} s restantes");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatFileProgress(int? processed, int? total)
    {
        if (processed is not int processedFiles)
        {
            return string.Empty;
        }

        return total is int totalFiles && totalFiles > 0
            ? $"{processedFiles:N0} / {totalFiles:N0} fichiers analysés"
            : $"{processedFiles:N0} fichiers analysés";
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

        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture)
            + " "
            + units[unit];
    }

    private static string MapActiveError(string? errorCategory) =>
        string.IsNullOrWhiteSpace(errorCategory)
            ? string.Empty
            : "L’opération n’a pas pu être terminée.";

    private static ActivityNavigationTarget MapNavigation(
        LauncherActivityNavigationTarget navigation) => navigation switch
        {
            LauncherActivityNavigationTarget.Game => ActivityNavigationTarget.Game,
            LauncherActivityNavigationTarget.Addons => ActivityNavigationTarget.Addons,
            _ => ActivityNavigationTarget.None
        };

    private static (string Uri, bool HasIcon) ResolveIcon(
        LauncherOperationType operationType,
        string targetId)
    {
        if (operationType is LauncherOperationType.GameInstall
            or LauncherOperationType.GameUpdate
            or LauncherOperationType.GameVerify
            or LauncherOperationType.GameRepair)
        {
            return (GameIcon, true);
        }
        if (operationType == LauncherOperationType.LauncherAutoUpdate)
        {
            return (LauncherIcon, true);
        }
        if (PackagedAddonIcons.Contains(targetId))
        {
            return (AddonIconRoot + targetId.ToLowerInvariant() + ".png", true);
        }

        return (string.Empty, false);
    }

    private void Runtime_SnapshotChanged(
        object? sender,
        LauncherActivitySnapshotEventArgs eventArgs) =>
        ApplyOrQueue(eventArgs.Snapshot);

    private void ApplyOrQueue(LauncherActivitySnapshot snapshot)
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

    private void Apply(LauncherActivitySnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0 || snapshot.Sequence <= _latestSequence)
        {
            return;
        }

        _latestSequence = snapshot.Sequence;
        _target.ApplyRuntimeView(Project(snapshot));
    }
}
