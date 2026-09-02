using System.Collections.Immutable;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Preview;

internal static class ActivityPreviewData
{
    private const string AddonIconRoot =
        "/WotLK.Launcher;component/Assets/Launcher/addon-icons/";
    private const string GameIcon =
        "/WotLK.Launcher;component/Assets/AppIcon.png";
    private const string LauncherIcon =
        "/WotLK.Launcher;component/Assets/Branding/AtlasLauncherLogo.png";

    internal static ActivityUiState Create(ActivityPreviewScenario scenario)
    {
        ActivityViewState view = scenario switch
        {
            ActivityPreviewScenario.GameDownload => View(
                Active(
                    "WotLK Classic",
                    "Mise à jour",
                    "Téléchargement des fichiers du client",
                    68,
                    "3,42 Go / 5,00 Go",
                    "18,4 Mo/s · environ 2 min",
                    GameIcon,
                    canCancel: true),
                recent: RecentShort),
            ActivityPreviewScenario.GameInstall => View(
                Active(
                    "WotLK Classic",
                    "Installation",
                    "Téléchargement du client",
                    42,
                    "2,10 Go / 5,00 Go",
                    "18,4 Mo/s · 2 min restantes",
                    GameIcon,
                    canCancel: true)),
            ActivityPreviewScenario.GameVerify => View(
                Active(
                    "WotLK Classic",
                    "Vérification",
                    "Analyse des fichiers locaux…",
                    percent: null,
                    transfer: string.Empty,
                    rateAndEta: string.Empty,
                    GameIcon,
                    canCancel: false,
                    isIndeterminate: true,
                    detail: "1 248 fichiers analysés"),
                recent: RecentShort),
            ActivityPreviewScenario.GameRepair => View(
                Active(
                    "WotLK Classic",
                    "Réparation",
                    "Téléchargement des fichiers manquants",
                    61,
                    "742 Mo / 1,20 Go",
                    "12,1 Mo/s · 40 s restantes",
                    GameIcon,
                    canCancel: true),
                recent: RecentShort),
            ActivityPreviewScenario.Addon => View(
                Active(
                    "Questie",
                    "Mise à jour",
                    "Téléchargement de l’addon",
                    68,
                    "24,8 Mo / 36,4 Mo",
                    "8,2 Mo/s · quelques secondes",
                    AddonIcon("questie"),
                    canCancel: true),
                recent: RecentShort),
            ActivityPreviewScenario.AddonBatch => View(
                Active(
                    "Questie",
                    "Mise à jour des addons",
                    "Téléchargement de l’addon",
                    68,
                    "24,8 Mo / 36,4 Mo",
                    "8,2 Mo/s · quelques secondes",
                    AddonIcon("questie"),
                    canCancel: true,
                    batchPosition: "1 sur 4"),
                pending:
                [
                    Pending("Deadly Boss Mods", "En attente", "dbm"),
                    Pending("Details!", "En attente", "details"),
                    Pending("Auctionator", "En attente", "auctionator")
                ],
                recent: RecentShort),
            ActivityPreviewScenario.AddonRemove => View(
                Active(
                    "Auctionator",
                    "Suppression",
                    "Suppression des fichiers gérés…",
                    percent: null,
                    transfer: string.Empty,
                    rateAndEta: string.Empty,
                    AddonIcon("auctionator"),
                    canCancel: false,
                    isIndeterminate: true),
                recent: RecentShort),
            ActivityPreviewScenario.SelfUpdate => View(
                Active(
                    "Atlas Launcher",
                    "Mise à jour",
                    "Téléchargement de la nouvelle version…",
                    72,
                    "82,4 Mo / 114,5 Mo",
                    "11,6 Mo/s · quelques secondes",
                    LauncherIcon,
                    canCancel: true),
                recent: RecentShort),
            ActivityPreviewScenario.Error => View(
                Active(
                    "Questie",
                    "Mise à jour interrompue",
                    string.Empty,
                    percent: null,
                    transfer: string.Empty,
                    rateAndEta: string.Empty,
                    AddonIcon("questie"),
                    canCancel: false,
                    error: "Le téléchargement n’a pas pu être terminé."),
                recent:
                [
                    Recent("Questie", "Mise à jour échouée", "13:08", ActivityRecentOutcome.Failed, ActivityNavigationTarget.Addons, "questie"),
                    Recent("WotLK Classic", "Vérification terminée", "12:54", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Game, iconUri: GameIcon)
                ]),
            ActivityPreviewScenario.History => View(recent: RecentHistory),
            ActivityPreviewScenario.ManyHistory => View(
                Active(
                    "WotLK Classic",
                    "Mise à jour",
                    "Téléchargement des fichiers du client",
                    68,
                    "3,42 Go / 5,00 Go",
                    "18,4 Mo/s · environ 2 min",
                    GameIcon,
                    canCancel: true),
                recent: ManyRecentHistory),
            ActivityPreviewScenario.QuickSuccess => View(
                recent:
                [
                    Recent("Questie", "Mis à jour", "à l’instant", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Addons, "questie")
                ]),
            ActivityPreviewScenario.Cancelling => View(
                Active(
                    "WotLK Classic",
                    "Annulation…",
                    "Arrêt de l’opération en cours…",
                    37,
                    "1,85 Go / 5,00 Go",
                    string.Empty,
                    GameIcon,
                    canCancel: false,
                    cancellationRequested: true),
                recent:
                [
                    Recent("WotLK Classic", "Téléchargement annulé", "12:30", ActivityRecentOutcome.Cancelled, ActivityNavigationTarget.Game, iconUri: GameIcon)
                ]),
            _ => View()
        };

        return new ActivityUiState(view);
    }

    private static ActivityViewState View(
        ActivityOperationUiItem? active = null,
        ImmutableArray<ActivityPendingUiItem> pending = default,
        ImmutableArray<ActivityRecentUiItem> recent = default) => new(
            IsPreview: true,
            ActiveOperation: active,
            PendingOperations: pending.IsDefault ? ImmutableArray<ActivityPendingUiItem>.Empty : pending,
            RecentOperations: recent.IsDefault ? ImmutableArray<ActivityRecentUiItem>.Empty : recent);

    private static ActivityOperationUiItem Active(
        string product,
        string action,
        string phase,
        double? percent,
        string transfer,
        string rateAndEta,
        string iconUri,
        bool canCancel,
        bool isIndeterminate = false,
        string detail = "",
        string error = "",
        string batchPosition = "",
        bool cancellationRequested = false) => new(
            ProductName: product,
            ActionName: action,
            PhaseText: phase,
            ProgressPercent: percent,
            IsIndeterminate: isIndeterminate,
            TransferText: transfer,
            RateAndEtaText: rateAndEta,
            DetailText: detail,
            IconUri: iconUri,
            HasIcon: true,
            CanUserCancel: canCancel,
            IsCancellationRequested: cancellationRequested,
            ErrorMessage: error,
            BatchPosition: batchPosition);

    private static ActivityPendingUiItem Pending(string product, string action, string iconId) =>
        new(product, action, AddonIcon(iconId), HasIcon: true);

    private static ActivityRecentUiItem Recent(
        string product,
        string result,
        string completedAt,
        ActivityRecentOutcome outcome,
        ActivityNavigationTarget target,
        string? iconId = null,
        string? iconUri = null) => new(
            ProductName: product,
            ResultText: result,
            CompletedAtText: completedAt,
            Outcome: outcome,
            NavigationTarget: target,
            IconUri: iconUri ?? AddonIcon(iconId ?? "questie"),
            HasIcon: true);

    private static string AddonIcon(string id) => AddonIconRoot + id + ".png";

    private static ImmutableArray<ActivityRecentUiItem> RecentShort { get; } =
    [
        Recent("DBM", "Mis à jour", "12:54", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Addons, "dbm"),
        Recent("WotLK Classic", "Vérification terminée", "12:41", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Game, iconUri: GameIcon)
    ];

    private static ImmutableArray<ActivityRecentUiItem> RecentHistory { get; } =
    [
        Recent("Questie", "Mis à jour", "13:08", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Addons, "questie"),
        Recent("WotLK Classic", "Vérification terminée", "12:54", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Game, iconUri: GameIcon),
        Recent("Auctionator", "Mise à jour échouée", "12:41", ActivityRecentOutcome.Failed, ActivityNavigationTarget.Addons, "auctionator"),
        Recent("WotLK Classic", "Téléchargement annulé", "12:30", ActivityRecentOutcome.Cancelled, ActivityNavigationTarget.Game, iconUri: GameIcon)
    ];

    private static ImmutableArray<ActivityRecentUiItem> ManyRecentHistory { get; } =
    [
        .. RecentHistory,
        Recent("Details!", "Mis à jour", "12:18", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Addons, "details"),
        Recent("Deadly Boss Mods", "Mis à jour", "12:12", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Addons, "dbm"),
        Recent("Atlas Launcher", "Mise à jour terminée", "11:58", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.None, iconUri: LauncherIcon),
        Recent("Questie", "Téléchargement annulé", "11:42", ActivityRecentOutcome.Cancelled, ActivityNavigationTarget.Addons, "questie"),
        Recent("Auctionator", "Supprimé", "11:31", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Addons, "auctionator"),
        Recent("WotLK Classic", "Réparation terminée", "11:08", ActivityRecentOutcome.Succeeded, ActivityNavigationTarget.Game, iconUri: GameIcon)
    ];
}
