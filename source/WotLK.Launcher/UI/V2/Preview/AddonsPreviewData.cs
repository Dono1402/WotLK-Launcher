using System.Collections.Immutable;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Preview;

internal static class AddonsPreviewData
{
    private const string IconRoot =
        "/WotLK.Launcher;component/Assets/Launcher/addon-icons/";

    internal static AddonsUiState Create(AddonsPreviewScenario scenario)
    {
        ImmutableArray<AddonUiItem> catalog = scenario switch
        {
            AddonsPreviewScenario.Empty => ImmutableArray<AddonUiItem>.Empty,
            AddonsPreviewScenario.Updates => BuildCatalog(20, updatesFirst: true),
            AddonsPreviewScenario.Many => BuildCatalog(50, updatesFirst: false),
            AddonsPreviewScenario.Installing => BuildInstallingCatalog(),
            AddonsPreviewScenario.Error => BuildErrorCatalog(),
            AddonsPreviewScenario.Detail => BuildCatalog(13, updatesFirst: false),
            _ => BuildCatalog(6, updatesFirst: false)
        };

        if (scenario == AddonsPreviewScenario.Detail)
        {
            catalog = catalog.Select(addon => addon.Id == "questie"
                ? addon with
                {
                    VisualState = AddonVisualState.Installed,
                    InstalledVersion = addon.AvailableVersion
                }
                : addon).ToImmutableArray();
        }

        AddonUiItem? selected = scenario == AddonsPreviewScenario.Detail
            ? catalog.First(addon => addon.Id == "questie")
            : null;
        string error = scenario == AddonsPreviewScenario.Error
            ? "Le catalogue Atlas n'a pas pu être actualisé. Les informations affichées sont conservées localement."
            : string.Empty;
        bool gameRunning = scenario == AddonsPreviewScenario.GameRunning;
        AddonsViewState view = new(
            IsPreview: true,
            Catalog: catalog,
            VisibleAddons: catalog
                .OrderBy(addon => addon.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToImmutableArray(),
            Filter: scenario == AddonsPreviewScenario.Updates
                ? AddonCatalogFilter.Updates
                : AddonCatalogFilter.All,
            SearchText: string.Empty,
            SelectedAddon: selected,
            IsDetailOpen: selected is not null,
            IsDeleteConfirmationOpen: false,
            IsGameRunning: gameRunning,
            CatalogErrorMessage: error,
            NotificationMessage: string.Empty);

        if (scenario == AddonsPreviewScenario.Updates)
        {
            view = view with
            {
                VisibleAddons = catalog
                    .Where(addon => addon.NeedsUpdate)
                    .OrderBy(addon => addon.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToImmutableArray()
            };
        }

        return new AddonsUiState(view);
    }

    internal static AddonsUiState CreateRuntimePlaceholder() =>
        new(AddonsUiState.EmptyView);

    private static ImmutableArray<AddonUiItem> BuildInstallingCatalog()
    {
        ImmutableArray<AddonUiItem> source = BuildCatalog(6, updatesFirst: false);
        return source.Select(addon => addon.Id switch
        {
            "atlaslootclassic" => addon with
            {
                VisualState = AddonVisualState.Installing,
                InstalledVersion = string.Empty,
                ProgressPercent = 36,
                IsIndeterminate = false
            },
            "attune" => addon with
            {
                VisualState = AddonVisualState.Updating,
                ProgressPercent = 68,
                IsIndeterminate = false
            },
            "auctionator" => addon with
            {
                VisualState = AddonVisualState.Removing,
                InstalledVersion = addon.AvailableVersion,
                ProgressPercent = null,
                IsIndeterminate = true
            },
            _ => addon
        }).ToImmutableArray();
    }

    private static ImmutableArray<AddonUiItem> BuildErrorCatalog()
    {
        ImmutableArray<AddonUiItem> source = BuildCatalog(6, updatesFirst: false);
        return source.Select(addon => addon.Id == "atlaslootclassic"
            ? addon with
            {
                VisualState = AddonVisualState.Error,
                InstalledVersion = "3.1.9",
                ProgressPercent = null,
                IsIndeterminate = false,
                ErrorMessage = "L'archive locale est incomplète. Réessaie l'installation."
            }
            : addon).ToImmutableArray();
    }

    private static ImmutableArray<AddonUiItem> BuildCatalog(int count, bool updatesFirst)
    {
        ImmutableArray<AddonFixture> fixtures = Fixtures;
        ImmutableArray<AddonUiItem>.Builder result = ImmutableArray.CreateBuilder<AddonUiItem>(count);
        for (int index = 0; index < count; index++)
        {
            AddonFixture fixture = fixtures[index % fixtures.Length];
            int cycle = index / fixtures.Length;
            string id = cycle == 0 ? fixture.Id : $"{fixture.Id}-sample-{cycle + 1}";
            string name = cycle == 0 ? fixture.Name : $"{fixture.Name} · Profil {cycle + 1}";
            AddonVisualState state = ResolveVisualState(index, updatesFirst);
            string installedVersion = state switch
            {
                AddonVisualState.NotInstalled => string.Empty,
                AddonVisualState.UpdateAvailable => PreviousVersion(fixture.Version),
                _ => fixture.Version
            };
            result.Add(new AddonUiItem(
                id,
                name,
                fixture.Description,
                fixture.Category,
                fixture.Version,
                installedVersion,
                "30403",
                "Sélection Atlas",
                fixture.Dependencies,
                fixture.Folders,
                fixture.HasOfficialIcon ? IconRoot + fixture.Id + ".png" : string.Empty,
                fixture.HasOfficialIcon,
                state,
                ProgressPercent: null,
                IsIndeterminate: false,
                ErrorMessage: string.Empty));
        }

        return result.MoveToImmutable();
    }

    private static AddonVisualState ResolveVisualState(int index, bool updatesFirst)
    {
        if (updatesFirst)
        {
            return index % 5 == 4
                ? AddonVisualState.Installed
                : AddonVisualState.UpdateAvailable;
        }

        return (index % 4) switch
        {
            0 => AddonVisualState.Installed,
            1 => AddonVisualState.UpdateAvailable,
            _ => AddonVisualState.NotInstalled
        };
    }

    private static string PreviousVersion(string availableVersion)
    {
        const string wrathSuffix = "-wrath";
        bool hasWrathSuffix = availableVersion.EndsWith(
            wrathSuffix,
            StringComparison.OrdinalIgnoreCase);
        string core = hasWrathSuffix
            ? availableVersion[..^wrathSuffix.Length]
            : availableVersion;
        int digitStart = core.Length;
        while (digitStart > 0 && char.IsAsciiDigit(core[digitStart - 1]))
        {
            digitStart--;
        }

        if (digitStart < core.Length
            && int.TryParse(core[digitStart..], out int revision)
            && revision > 0)
        {
            return core[..digitStart]
                + (revision - 1)
                + (hasWrathSuffix ? wrathSuffix : string.Empty);
        }

        return "Version précédente";
    }

    private static ImmutableArray<AddonFixture> Fixtures { get; } =
    [
        Fixture(
            "atlaslootclassic",
            "AtlasLootClassic",
            "3.2.0",
            "Collections",
            "Catalogue des butins de donjons, raids, factions, JcJ et artisanat.",
            ["AtlasLootClassic", "AtlasLootClassic_Data", "AtlasLootClassic_DungeonsAndRaids"]),
        Fixture(
            "attune",
            "Attune",
            "WOTLK-314",
            "Quêtes",
            "Suivi des accès, prérequis et progressions d'harmonisation.",
            ["Attune"]),
        Fixture(
            "auctionator",
            "Auctionator",
            "10.2.0-wrath",
            "Économie",
            "Outils pratiques pour acheter, vendre et analyser l'hôtel des ventes.",
            ["Auctionator"]),
        Fixture(
            "baganator",
            "Baganator",
            "158-wrath",
            "Inventaire",
            "Sacs et banque unifiés avec catégories, recherche et tri rapide.",
            ["Baganator"]),
        Fixture(
            "dbm",
            "Deadly Boss Mods (DBM)",
            "11.0.34",
            "Combat",
            "Alertes de boss pour les raids et donjons de WotLK Classic.",
            ["DBM-Core", "DBM-Raids-WoTLK", "DBM-Party-WotLK"]),
        Fixture(
            "details",
            "Details!",
            "20250119.13388.161",
            "Combat",
            "Mesure des dégâts, soins, menaces et statistiques de combat.",
            ["Details", "Details_EncounterDetails", "Details_TinyThreat"]),
        Fixture(
            "elvui",
            "ElvUI",
            "13.61",
            "Interface",
            "Remplacement complet et configurable de l'interface.",
            ["ElvUI", "ElvUI_Libraries", "ElvUI_Options"]),
        Fixture(
            "leatrix-maps",
            "Leatrix Maps",
            "3.0.191",
            "Interface",
            "Carte du monde améliorée avec exploration, coordonnées et navigation.",
            ["Leatrix_Maps"]),
        Fixture(
            "leatrix-plus",
            "Leatrix Plus",
            "3.0.191",
            "Interface",
            "Améliorations de confort et réglages pratiques du client.",
            ["Leatrix_Plus"]),
        Fixture(
            "nova-instance-tracker",
            "Nova Instance Tracker",
            "1.55-Wrath",
            "Instances",
            "Suivi des entrées, verrouillages et temps passé dans les instances.",
            ["NovaInstanceTracker"]),
        Fixture(
            "questie",
            "Questie",
            "10.19.2",
            "Quêtes",
            "Suivi des quêtes, objectifs et marqueurs sur la carte.",
            ["Questie"]),
        Fixture(
            "weakauras",
            "WeakAuras",
            "5.13.1",
            "Combat",
            "Auras, alertes et éléments d'interface personnalisables.",
            ["WeakAuras", "WeakAurasOptions", "WeakAurasTemplates"]),
        Fixture(
            "whats-training",
            "What's Training?",
            "1.8.11-wrath",
            "Interface",
            "Affiche les prochains sorts disponibles auprès de ton maître de classe.",
            ["WhatsTraining"]),
        Fixture(
            "bartender4",
            "Bartender4",
            "4.14.3",
            "Interface",
            "Personnalisation complète des barres d'actions.",
            ["Bartender4"],
            hasOfficialIcon: false),
        Fixture(
            "grid2",
            "Grid2",
            "2.5.12",
            "Combat",
            "Cadres de raid compacts et configurables.",
            ["Grid2"],
            hasOfficialIcon: false),
        Fixture(
            "omnicc",
            "OmniCC",
            "10.2.4",
            "Combat",
            "Décompte lisible des temps de recharge sur les boutons.",
            ["OmniCC"],
            hasOfficialIcon: false),
        Fixture(
            "pawn",
            "Pawn",
            "2.8.10",
            "Équipement",
            "Comparaison d'équipement à partir de pondérations personnalisées.",
            ["Pawn"],
            hasOfficialIcon: false),
        Fixture(
            "plater",
            "Plater Nameplates",
            "585",
            "Combat",
            "Barres de vie personnalisables pour alliés et ennemis.",
            ["Plater"],
            hasOfficialIcon: false),
        Fixture(
            "prat",
            "Prat 3.0",
            "3.9.59",
            "Discussion",
            "Options avancées et historique pour les fenêtres de discussion.",
            ["Prat-3.0"],
            hasOfficialIcon: false),
        Fixture(
            "titan-panel",
            "Titan Panel Classic",
            "8.2.2",
            "Interface",
            "Barre d'informations modulaire pour les données utiles du personnage.",
            ["TitanClassic"],
            hasOfficialIcon: false)
    ];

    private static AddonFixture Fixture(
        string id,
        string name,
        string version,
        string category,
        string description,
        ImmutableArray<string> folders,
        bool hasOfficialIcon = true,
        ImmutableArray<string> dependencies = default) =>
        new(
            id,
            name,
            version,
            category,
            description,
            folders,
            hasOfficialIcon,
            dependencies.IsDefault ? ImmutableArray<string>.Empty : dependencies);

    private sealed record AddonFixture(
        string Id,
        string Name,
        string Version,
        string Category,
        string Description,
        ImmutableArray<string> Folders,
        bool HasOfficialIcon,
        ImmutableArray<string> Dependencies);
}
