using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Localization;

namespace WotLK.Launcher.UI.V2.Presentation;

public enum AddonCatalogFilter
{
    All,
    Installed,
    Updates,
    Favorites,
    Manual
}

public enum AddonSortOrder { Name, UpdatesFirst, FavoritesFirst, RecentlyInstalled }

public sealed record AddonLibraryChoice(string Key, string Label)
{
    public string DisplayLabel => LauncherLocalization.Text(Label);
}

public sealed record AddonPackChoice(string Id, string Name, string Description, ImmutableArray<string> AddonIds)
{
    public string DisplayName => LauncherLocalization.Text(Name);
    public string DisplayDescription => LauncherLocalization.Text(Description);
}

public enum AddonVisualState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Installing,
    Updating,
    Removing,
    Repairing,
    Verifying,
    Reinstalling,
    Error
}

public enum AddonPrimaryActionKind
{
    None,
    Install,
    Update,
    Repair,
    Cancel
}

public sealed record AddonUiItem(
    string Id,
    string Name,
    string Description,
    string Category,
    string AvailableVersion,
    string InstalledVersion,
    string InterfaceVersion,
    string Author,
    ImmutableArray<string> Dependencies,
    ImmutableArray<string> ManagedFolders,
    string IconPath,
    bool HasOfficialIcon,
    AddonVisualState VisualState,
    double? ProgressPercent,
    bool IsIndeterminate,
    string ErrorMessage)
{
    public bool IsManagedByAtlas { get; init; }

    public bool IsCatalogEntry { get; init; } = true;

    public bool IsFavorite { get; init; }

    public bool IsSelectedForBatch { get; init; }

    public string VerificationMessage { get; init; } = string.Empty;

    public bool IsVerified { get; init; }

    public string KnownLimitations { get; init; } = string.Empty;

    public string AtlasValidationEvidence { get; init; } = string.Empty;

    public bool IsInterfaceCompatible { get; init; } = true;

    public bool IsAtlasValidated => !string.IsNullOrWhiteSpace(AtlasValidationEvidence);

    public bool HasKnownLimitations => !string.IsNullOrWhiteSpace(KnownLimitations);

    public string CompatibilitySummary => IsAtlasValidated ? "Testé sur Atlas"
        : IsInterfaceCompatible ? "Interface compatible" : "Interface différente";

    public string CompatibilityHint => string.Join("\n", new[] { CompatibilitySummary,
        AtlasValidationEvidence, KnownLimitations, VerificationMessage }.Where(text => !string.IsNullOrWhiteSpace(text)));

    public bool CanVerify => IsCatalogEntry && IsManagedByAtlas && !IsBusy && ActionsEnabled;

    public bool CanReinstall => IsCatalogEntry && IsInstalled && !IsBusy && ActionsEnabled;

    public string FavoriteLabel => IsFavorite ? "Retirer des favoris" : "Ajouter aux favoris";

    public string DetailActionLabel => LauncherLocalization.IsEnglish ? $"View details for {Name}" : $"Voir les détails de {Name}";

    public string SelectionLabel => LauncherLocalization.IsEnglish ? $"Select {Name}" : $"Sélectionner {Name}";

    public bool CanSelectForBatch => IsCatalogEntry && !IsBusy;

    public bool RequiresRepair { get; init; }

    public bool IsDetectedUnmanaged { get; init; }

    public string InstalledSha256 { get; init; } = string.Empty;

    public DateTimeOffset? InstalledAtUtc { get; init; }

    public AddonPrimaryActionKind PrimaryAction { get; init; }

    public bool UsesExplicitPrimaryAction { get; init; }

    public bool ActionsEnabled { get; init; } = true;

    public bool CanCancelOperation { get; init; }

    public string ProgressDetail { get; init; } = string.Empty;

    public bool IsInstalled => IsManagedByAtlas || IsDetectedUnmanaged
        || !string.IsNullOrWhiteSpace(InstalledVersion)
        || VisualState is AddonVisualState.Installed
            or AddonVisualState.UpdateAvailable
            or AddonVisualState.Updating
            or AddonVisualState.Removing
            or AddonVisualState.Repairing
            or AddonVisualState.Verifying
            or AddonVisualState.Reinstalling;

    public bool NeedsUpdate => VisualState is AddonVisualState.UpdateAvailable
        or AddonVisualState.Updating
        or AddonVisualState.Repairing
        || RequiresRepair
        || VisualState == AddonVisualState.Error && IsInstalled;

    public bool IsBusy => VisualState is AddonVisualState.Installing
        or AddonVisualState.Updating
        or AddonVisualState.Removing
        or AddonVisualState.Repairing
        or AddonVisualState.Verifying
        or AddonVisualState.Reinstalling;

    public bool ShowsProgress => IsBusy;

    public bool ShowsProgressPercent => ProgressPercent.HasValue;

    public bool ShowsProgressDetail => !string.IsNullOrWhiteSpace(ProgressDetail);

    public bool ShowsError => VisualState == AddonVisualState.Error
        && !string.IsNullOrWhiteSpace(ErrorMessage);

    public AddonPrimaryActionKind EffectivePrimaryAction => UsesExplicitPrimaryAction
        ? PrimaryAction
        : RequiresRepair
            ? AddonPrimaryActionKind.Repair
            : VisualState switch
            {
                AddonVisualState.NotInstalled => AddonPrimaryActionKind.Install,
                AddonVisualState.UpdateAvailable => AddonPrimaryActionKind.Update,
                AddonVisualState.Error when IsInstalled => AddonPrimaryActionKind.Update,
                AddonVisualState.Error => AddonPrimaryActionKind.Install,
                _ => AddonPrimaryActionKind.None
            };

    public bool ShowsAction => IsBusy || EffectivePrimaryAction != AddonPrimaryActionKind.None;

    public bool ShowsRowAction => IsCatalogEntry && ShowsAction;

    public bool CanInvokePrimary => IsCatalogEntry && (CanCancelOperation
        || ActionsEnabled && !IsBusy && EffectivePrimaryAction is
            AddonPrimaryActionKind.Install
            or AddonPrimaryActionKind.Update
            or AddonPrimaryActionKind.Repair);

    public bool CanRemove => IsCatalogEntry && IsInstalled && !IsDetectedUnmanaged && !IsBusy && ActionsEnabled;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);

    public string StatusLabel => VisualState switch
    {
        _ when !IsCatalogEntry => "Installé manuellement",
        _ when RequiresRepair && VisualState == AddonVisualState.UpdateAvailable => "À réparer",
        _ when IsDetectedUnmanaged && VisualState == AddonVisualState.NotInstalled => "Détecté (non géré)",
        AddonVisualState.Installed => "Installé",
        AddonVisualState.UpdateAvailable => "Mise à jour",
        AddonVisualState.Installing => "Installation",
        AddonVisualState.Updating => "Mise à jour",
        AddonVisualState.Removing => "Suppression",
        AddonVisualState.Repairing => "Réparation",
        AddonVisualState.Verifying => "Vérification",
        AddonVisualState.Reinstalling => "Réinstallation",
        AddonVisualState.Error => "Erreur",
        _ => "Non installé"
    };

    public string ActionLabel => CanCancelOperation
        ? "Annuler"
        : IsBusy
            ? VisualState switch
            {
                AddonVisualState.Installing => "Installation…",
                AddonVisualState.Updating => "Mise à jour…",
                AddonVisualState.Removing => "Suppression…",
                AddonVisualState.Repairing => "Réparation…",
                AddonVisualState.Verifying => "Vérification…",
                AddonVisualState.Reinstalling => "Réinstallation…",
                _ => string.Empty
            }
            : EffectivePrimaryAction switch
    {
        AddonPrimaryActionKind.Install => VisualState == AddonVisualState.Error
            ? "Réessayer"
            : "Installer",
        AddonPrimaryActionKind.Update => VisualState == AddonVisualState.Error
            ? "Réessayer"
            : "Mettre à jour",
        AddonPrimaryActionKind.Repair => VisualState == AddonVisualState.Error
            ? "Réessayer"
            : "Réparer",
        _ => string.Empty
    };

    public string VersionSummary => IsDetectedUnmanaged && !IsManagedByAtlas
        ? "Installation externe détectée"
        : IsInstalled
        ? VisualState == AddonVisualState.UpdateAvailable
            ? $"{InstalledVersion}  →  {AvailableVersion}"
            : $"Version {InstalledVersion}"
        : $"Version {AvailableVersion}";

    public string AvailableVersionText => $"Version disponible  {AvailableVersion}";

    public string CompactVersionSummary
    {
        get
        {
            if (IsDetectedUnmanaged && !IsManagedByAtlas)
            {
                return "Installation externe";
            }

            if (VisualState == AddonVisualState.UpdateAvailable && !RequiresRepair
                && !string.IsNullOrWhiteSpace(InstalledVersion))
                return $"{CompactVersion(InstalledVersion)} → {CompactVersion(AvailableVersion)}";
            string version = CompactVersion(NeedsUpdate || !IsInstalled ? AvailableVersion : InstalledVersion);
            return version.Length > 0 && char.IsAsciiDigit(version[0]) ? $"v{version}" : version;
        }
    }

    private static string CompactVersion(string version)
    {
        string primary = version.Split('+', 2)[0].TrimStart('v', 'V');
        return primary.Length > 16 ? primary[..15] + "…" : primary;
    }

    public string InstalledVersionText => IsInstalled
        ? $"Version installée  {InstalledVersion}"
        : "Aucune version installée";

    public string CompatibilityText => InterfaceVersion == "30403"
        ? "WotLK Classic 3.4.3"
        : $"Interface {InterfaceVersion}";

    public string ManagedFoldersText => ManagedFolders.Length switch
    {
        0 => "Aucun dossier déclaré",
        1 => $"1 dossier géré · {ManagedFolders[0]}",
        _ => $"{ManagedFolders.Length} dossiers gérés"
    };

    public string DependenciesText => Dependencies.IsDefaultOrEmpty
        ? "Aucune dépendance requise"
        : "Dépendances · " + string.Join(", ", Dependencies);
}

public sealed record AddonsViewState(
    bool IsPreview,
    ImmutableArray<AddonUiItem> Catalog,
    ImmutableArray<AddonUiItem> VisibleAddons,
    AddonCatalogFilter Filter,
    string SearchText,
    AddonUiItem? SelectedAddon,
    bool IsDetailOpen,
    bool IsDeleteConfirmationOpen,
    bool IsGameRunning,
    string CatalogErrorMessage,
    string NotificationMessage,
    bool IsRuntimeConnected = false,
    bool IsCatalogLoading = false,
    bool CanMutate = false,
    bool CanCancelCurrent = false,
    bool IsBatchOperation = false,
    string ActiveAddonId = "",
    ImmutableArray<string> TemporarilyVisibleAddonIds = default)
{
    public string CategoryFilter { get; init; } = string.Empty;

    public AddonSortOrder SortOrder { get; init; } = AddonSortOrder.Name;

    public int? ActiveAddonPosition { get; init; }

    public int? ActiveAddonTotal { get; init; }

    public bool CanRetryFailed { get; init; }

    public string BatchProgressLabel
    {
        get
        {
            if (!IsBatchOperation) return CanRetryFailed ? L("Certaines opérations restent à traiter.", "Some operations still need to be processed.") : string.Empty;
            if (ActiveAddonPosition is not int position || ActiveAddonTotal is not int total)
                return L("Traitement de la sélection…", "Processing selection…");
            string name = Catalog.FirstOrDefault(addon => addon.Id == ActiveAddonId)?.Name ?? L("Traitement en cours", "Processing");
            return L($"Addon {position} sur {total} · {name}", $"Addon {position} of {total} · {name}");
        }
    }

    public bool ShowsBatchProgress => IsBatchOperation || CanRetryFailed;

    public bool ShowsUpdateAll => IsBatchOperation || UpdateCount > 0;

    public int TotalCount => Catalog.Length;

    public int InstalledCount => Catalog.Count(addon => addon.IsInstalled);

    public int UpdateCount => Catalog.Count(addon => addon.IsCatalogEntry && addon.NeedsUpdate);

    public int FavoriteCount => Catalog.Count(addon => addon.IsFavorite);

    public int ManualCount => Catalog.Count(addon => addon.IsDetectedUnmanaged || !addon.IsCatalogEntry);

    public string AllFilterLabel => L($"Tous  {TotalCount}", $"All  {TotalCount}");

    public string InstalledFilterLabel => L($"Installés  {InstalledCount}", $"Installed  {InstalledCount}");

    public string UpdatesFilterLabel => L($"Mises à jour  {UpdateCount}", $"Updates  {UpdateCount}");

    public string FavoritesFilterLabel => L($"Favoris  {FavoriteCount}", $"Favourites  {FavoriteCount}");

    public string ManualFilterLabel => L($"Manuels  {ManualCount}", $"Manual  {ManualCount}");

    public string ResultsLabel => VisibleAddons.Length == TotalCount
        ? $"{TotalCount} addon{(TotalCount > 1 ? "s" : string.Empty)}"
        : L($"{VisibleAddons.Length} sur {TotalCount}", $"{VisibleAddons.Length} of {TotalCount}");

    public bool HasVisibleAddons => !VisibleAddons.IsDefaultOrEmpty;

    public bool ShowsEmpty => !HasVisibleAddons;

    public bool ShowsCatalogError => !string.IsNullOrWhiteSpace(CatalogErrorMessage);

    public bool ShowsNotification => !string.IsNullOrWhiteSpace(NotificationMessage);

    public bool CanUpdateAll => IsPreview
        ? UpdateCount > 0 && Catalog.All(addon => !addon.IsBusy)
        : IsBatchOperation && CanCancelCurrent
            || CanMutate && UpdateCount > 0;

    public bool IsInteractive => IsPreview
        || IsRuntimeConnected && !IsCatalogLoading && TotalCount > 0;

    public string UpdateAllLabel => IsBatchOperation && CanCancelCurrent
        ? "Annuler"
        : UpdateCount == 1 ? "Mettre à jour" : "Tout mettre à jour";

    public string EmptyTitle => IsCatalogLoading
        ? string.Empty
        : TotalCount == 0
            ? "Aucun addon disponible"
            : !string.IsNullOrWhiteSpace(SearchText)
                ? L($"Aucun addon trouvé pour “{SearchText}”", $"No addons found for “{SearchText}”")
                : "Aucun addon ne correspond à ce filtre.";

    public string EmptyDescription => IsCatalogLoading
        ? string.Empty
        : TotalCount == 0
            ? "Le catalogue ne contient actuellement aucun addon."
            : "Modifie la recherche ou le filtre sélectionné.";

    private static string L(string french, string english) => LauncherLocalization.IsEnglish ? english : french;
}

public sealed class AddonsUiState : BindableUiState
{
    private AddonsViewState _current;
    private readonly HashSet<string> _favoriteIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectionIds = new(StringComparer.OrdinalIgnoreCase);
    private ImmutableArray<AddonSelectionProfile> _profiles = [];
    private IAddonLibraryStore? _libraryStore;
    private string _libraryRoot = string.Empty;
    private bool _isLibraryOpen;
    private string _profileName = string.Empty;
    private string _selectedPackId = "starter";
    private string _selectedProfileName = string.Empty;

    internal AddonsUiState(AddonsViewState? initial = null)
    {
        _current = initial ?? EmptyView;
    }

    public static AddonsViewState EmptyView { get; } = new(
        IsPreview: false,
        Catalog: ImmutableArray<AddonUiItem>.Empty,
        VisibleAddons: ImmutableArray<AddonUiItem>.Empty,
        Filter: AddonCatalogFilter.All,
        SearchText: string.Empty,
        SelectedAddon: null,
        IsDetailOpen: false,
        IsDeleteConfirmationOpen: false,
        IsGameRunning: false,
        CatalogErrorMessage: string.Empty,
        NotificationMessage: string.Empty);

    public ICommand PrimaryCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand UpdateAllCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand RemoveCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand VerifyCommand { get; private set; } = DisabledCommand.Instance;
    public ICommand ReinstallCommand { get; private set; } = DisabledCommand.Instance;
    public ICommand InstallSelectionCommand { get; private set; } = DisabledCommand.Instance;
    public ICommand RetryFailedCommand { get; private set; } = DisabledCommand.Instance;
    public ICommand ImportProfileCommand { get; private set; } = DisabledCommand.Instance;
    public ICommand ExportProfileCommand { get; private set; } = DisabledCommand.Instance;

    public AddonsViewState Current => _current;

    public bool HasSearch => !string.IsNullOrEmpty(Current.SearchText);

    public bool IsLibraryOpen
    {
        get => _isLibraryOpen;
        set => SetProperty(ref _isLibraryOpen, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (SetProperty(ref _profileName, value ?? string.Empty)) RaisePropertyChanged(nameof(CanSaveProfile));
        }
    }

    public string SelectedPackId
    {
        get => _selectedPackId;
        set => SetProperty(ref _selectedPackId, value ?? "starter");
    }

    public string SelectedProfileName
    {
        get => _selectedProfileName;
        set
        {
            if (SetProperty(ref _selectedProfileName, value ?? string.Empty))
            {
                RaisePropertyChanged(nameof(HasSelectedProfile));
            }
        }
    }

    public ImmutableArray<string> SelectedAddonIds => _selectionIds.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    public ImmutableArray<AddonSelectionProfile> Profiles => _profiles;

    public bool HasProfiles => !_profiles.IsDefaultOrEmpty;

    public bool HasSelectedProfile => _profiles.Any(profile => string.Equals(profile.Name, SelectedProfileName, StringComparison.OrdinalIgnoreCase));

    public bool HasSelection => _selectionIds.Count > 0;

    public string SelectionSummary => LauncherLocalization.IsEnglish
        ? $"{_selectionIds.Count} addon{(_selectionIds.Count == 1 ? "" : "s")} selected"
        : $"{_selectionIds.Count} addon{(_selectionIds.Count > 1 ? "s" : "")} sélectionné{(_selectionIds.Count > 1 ? "s" : "")}";

    public bool CanSaveProfile => HasSelection && !string.IsNullOrWhiteSpace(ProfileName) && ProfileName.Trim().Length <= 60;

    public bool CanInstallSelection => HasSelection && (Current.IsPreview || Current.CanMutate);

    public ImmutableArray<AddonLibraryChoice> CategoryChoices => [new("", "Toutes les catégories"),
        .. Current.Catalog.Select(addon => addon.Category).Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(category => new AddonLibraryChoice(category, category))];

    public static ImmutableArray<AddonLibraryChoice> SortChoices { get; } =
        [new("Name", "Nom (A–Z)"), new("UpdatesFirst", "Mises à jour d’abord"),
            new("FavoritesFirst", "Favoris d’abord"), new("RecentlyInstalled", "Installés récemment")];

    public static ImmutableArray<AddonPackChoice> Packs { get; } =
    [
        new("starter", "Débuter", "Des repères pour les quêtes et quelques améliorations d’interface.", ["questie", "leatrix-plus", "atlaslootclassic"]),
        new("quests", "Quêtes", "Repères de quêtes et suivi des accès aux donjons.", ["questie", "attune", "atlaslootclassic"]),
        new("raids", "Raids", "Informations de combat et consultation du butin.", ["dbm", "details", "atlaslootclassic"])
    ];

    internal void AttachLibraryCommands(ICommand verify, ICommand reinstall, ICommand installSelected,
        ICommand retryFailed, ICommand importProfile, ICommand exportProfile)
    {
        VerifyCommand = verify ?? DisabledCommand.Instance;
        ReinstallCommand = reinstall ?? DisabledCommand.Instance;
        InstallSelectionCommand = installSelected ?? DisabledCommand.Instance;
        RetryFailedCommand = retryFailed ?? DisabledCommand.Instance;
        ImportProfileCommand = importProfile ?? DisabledCommand.Instance;
        ExportProfileCommand = exportProfile ?? DisabledCommand.Instance;
        RaisePropertyChanged(string.Empty);
    }

    internal void RefreshLocalizedText() => RaisePropertyChanged(string.Empty);

    internal void ConfigureLibraryStore(IAddonLibraryStore store, string clientRoot)
    {
        ArgumentNullException.ThrowIfNull(store);
        string root;
        try { root = JsonAddonLibraryStore.NormalizeRoot(clientRoot); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            root = string.Empty;
        }
        if (ReferenceEquals(store, _libraryStore) && root == _libraryRoot) return;
        _libraryStore = store;
        _libraryRoot = root;
        _favoriteIds.Clear();
        _selectionIds.Clear();
        _profiles = [];
        _selectedProfileName = string.Empty;
        _profileName = string.Empty;
        try
        {
            AddonLibraryPreferences preferences = root.Length == 0 ? AddonLibraryPreferences.Empty : store.Load(root);
            _favoriteIds.UnionWith(preferences.FavoriteIds);
            _profiles = preferences.Profiles;
            Publish(Current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Publish(Current with { NotificationMessage = "Les favoris et profils locaux n’ont pas pu être chargés." });
        }
    }

    internal bool ToggleFavorite(string addonId)
    {
        if (!Current.Catalog.Any(addon => addon.IsCatalogEntry && string.Equals(addon.Id, addonId, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!_favoriteIds.Remove(addonId)) _favoriteIds.Add(addonId);
        PersistLibrary();
        Publish(Current);
        return true;
    }

    internal bool SetSelected(string addonId, bool selected)
    {
        if (!Current.Catalog.Any(addon => addon.CanSelectForBatch && string.Equals(addon.Id, addonId, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (selected) _selectionIds.Add(addonId); else _selectionIds.Remove(addonId);
        Publish(Current);
        return true;
    }

    internal void SelectVisibleAddons()
    {
        _selectionIds.UnionWith(Current.VisibleAddons.Where(addon => addon.CanSelectForBatch).Select(addon => addon.Id));
        Publish(Current);
    }

    internal void ClearSelection()
    {
        _selectionIds.Clear();
        Publish(Current);
    }

    internal bool SelectCategory(string? category)
    {
        if (!Current.IsInteractive) return false;
        string value = category ?? string.Empty;
        if (value.Length > 0 && !Current.Catalog.Any(addon => string.Equals(addon.Category, value, StringComparison.CurrentCultureIgnoreCase)))
            return false;
        Publish(Current with { CategoryFilter = value });
        return true;
    }

    internal bool SelectSort(AddonSortOrder sort)
    {
        if (!Current.IsInteractive || !Enum.IsDefined(sort)) return false;
        Publish(Current with { SortOrder = sort });
        return true;
    }

    internal bool ApplyPack(string packId)
    {
        AddonPackChoice? pack = Packs.FirstOrDefault(item => item.Id == packId);
        if (pack is null) return false;
        SelectedPackId = packId;
        string name = LauncherLocalization.Text(pack.Name);
        return ApplySelection(pack.AddonIds, L($"Sélection « {name} » chargée. Choisis ses composants avant de lancer l’installation.",
            $"Selection “{name}” loaded. Choose its components before starting installation."));
    }

    internal bool LoadProfile(string profileName)
    {
        AddonSelectionProfile? profile = _profiles.FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return false;
        SelectedProfileName = profile.Name;
        ProfileName = profile.Name;
        return ApplySelection(profile.AddonIds, L($"Profil « {profile.Name} » chargé. Les fichiers du jeu restent inchangés.",
            $"Profile “{profile.Name}” loaded. Game files are unchanged."));
    }

    internal bool SaveProfile()
    {
        if (!CanSaveProfile) return false;
        string name;
        try { name = AddonSelectionJson.NormalizeName(ProfileName); }
        catch (InvalidDataException error) { ShowLocalNotification(error.Message); return false; }
        bool replacing = _profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!replacing && _profiles.Length >= 30)
        {
            ShowLocalNotification("Tu peux conserver jusqu’à 30 profils d’addons.");
            return false;
        }
        _profiles = _profiles.Where(profile => !string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
            .Append(new AddonSelectionProfile(name, SelectedAddonIds))
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
        SelectedProfileName = name;
        if (!PersistLibrary()) { Publish(Current); return false; }
        Publish(Current with { NotificationMessage = L($"Profil « {name} » enregistré.", $"Profile “{name}” saved.") });
        return true;
    }

    internal bool RemoveProfile(string profileName)
    {
        int before = _profiles.Length;
        _profiles = _profiles.Where(profile => !string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
        if (_profiles.Length == before) return false;
        SelectedProfileName = string.Empty;
        bool saved = PersistLibrary();
        Publish(saved ? Current with { NotificationMessage = "Profil de sélection supprimé." } : Current);
        return saved;
    }

    internal string ExportSelectionJson(string? profileName = null) => AddonSelectionJson.Export(new(
        string.IsNullOrWhiteSpace(profileName)
            ? string.IsNullOrWhiteSpace(ProfileName) ? "Sélection Atlas" : ProfileName
            : profileName,
        SelectedAddonIds));

    internal bool ImportSelectionJson(string json)
    {
        try
        {
            AddonSelectionProfile profile = AddonSelectionJson.Import(json);
            if (!ApplySelection(profile.AddonIds, "Sélection importée. Vérifie les composants avant l’installation.")) return false;
            ProfileName = profile.Name;
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            ShowLocalNotification("Le fichier de sélection est invalide ou incompatible.");
            return false;
        }
    }

    private bool ApplySelection(IEnumerable<string> ids, string notice)
    {
        string[] requested = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] available = requested.Where(id => Current.Catalog.Any(addon => addon.IsCatalogEntry
            && string.Equals(addon.Id, id, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (available.Length == 0)
        {
            ShowLocalNotification("Aucun composant de cette sélection n’est disponible dans le catalogue actuel.");
            return false;
        }
        _selectionIds.Clear();
        _selectionIds.UnionWith(available);
        IsLibraryOpen = true;
        int missing = requested.Length - available.Length;
        Publish(Current with
        {
            Filter = AddonCatalogFilter.All,
            CategoryFilter = string.Empty,
            SearchText = string.Empty,
            NotificationMessage = notice + (missing > 0 ? L($" {missing} composant(s) absent(s) du catalogue ont été ignorés.",
                $" {missing} component(s) absent from the catalogue were skipped.") : string.Empty)
        });
        return true;
    }

    private bool PersistLibrary()
    {
        if (_libraryStore is null || _libraryRoot.Length == 0) return true;
        try
        {
            _libraryStore.Save(_libraryRoot, new(_favoriteIds.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray(), _profiles));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _current = Current with { NotificationMessage = "Les préférences restent disponibles ici, mais leur enregistrement local a échoué." };
            return false;
        }
    }

    internal bool UpdateSearch(string? value)
    {
        if (!_current.IsInteractive)
        {
            return false;
        }

        string search = value ?? string.Empty;
        Publish(ApplyFilter(_current with
        {
            SearchText = search,
            NotificationMessage = string.Empty
        }));
        return true;
    }

    internal bool SelectFilter(AddonCatalogFilter filter)
    {
        if (!_current.IsInteractive)
        {
            return false;
        }

        Publish(ApplyFilter(_current with
        {
            Filter = filter,
            NotificationMessage = string.Empty
        }));
        return true;
    }

    internal bool OpenDetails(string addonId)
    {
        if (!_current.IsPreview && !_current.IsRuntimeConnected)
        {
            return false;
        }

        AddonUiItem? selected = _current.Catalog.FirstOrDefault(addon =>
            string.Equals(addon.Id, addonId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return false;
        }

        Publish(_current with
        {
            SelectedAddon = selected,
            IsDetailOpen = true,
            IsDeleteConfirmationOpen = false
        });
        return true;
    }

    internal void CloseDetails()
    {
        if (!_current.IsDetailOpen)
        {
            return;
        }

        Publish(_current with
        {
            SelectedAddon = null,
            IsDetailOpen = false,
            IsDeleteConfirmationOpen = false
        });
    }

    internal bool RequestRemoveSelected()
    {
        if (_current.SelectedAddon?.CanRemove != true)
        {
            return false;
        }

        Publish(_current with { IsDeleteConfirmationOpen = true });
        return true;
    }

    internal void CancelRemove()
    {
        if (_current.IsDeleteConfirmationOpen)
        {
            Publish(_current with { IsDeleteConfirmationOpen = false });
        }
    }

    internal bool ConfirmRemove()
    {
        if (_current.SelectedAddon is not AddonUiItem selected)
        {
            return false;
        }

        if (!_current.IsPreview)
        {
            if (!RemoveCommand.CanExecute(selected.Id))
            {
                return false;
            }

            Publish(_current with { IsDeleteConfirmationOpen = false });
            RemoveCommand.Execute(selected.Id);
            return true;
        }

        AddonUiItem removing = selected with
        {
            VisualState = AddonVisualState.Removing,
            ProgressPercent = null,
            IsIndeterminate = true,
            ErrorMessage = string.Empty
        };
        ReplaceAddon(
            removing,
            notification: $"Suppression de {selected.Name}…",
            keepDetailOpen: true);
        return true;
    }

    internal bool InvokePrimary(string addonId)
    {
        if (!_current.IsPreview)
        {
            if (!PrimaryCommand.CanExecute(addonId))
            {
                return false;
            }

            PrimaryCommand.Execute(addonId);
            return true;
        }

        AddonUiItem? addon = _current.Catalog.FirstOrDefault(item =>
            string.Equals(item.Id, addonId, StringComparison.OrdinalIgnoreCase));
        if (addon?.CanInvokePrimary != true)
        {
            return false;
        }

        bool installing = addon.VisualState == AddonVisualState.NotInstalled;
        AddonUiItem next = addon with
        {
            VisualState = installing ? AddonVisualState.Installing : AddonVisualState.Updating,
            ProgressPercent = installing ? 36 : 58,
            IsIndeterminate = false,
            ErrorMessage = string.Empty
        };
        ReplaceAddon(
            next,
            notification: installing
                ? $"Installation de {addon.Name}…"
                : $"Mise à jour de {addon.Name}…",
            keepDetailOpen: _current.IsDetailOpen);
        return true;
    }

    internal bool UpdateAll()
    {
        if (!_current.IsPreview)
        {
            if (!UpdateAllCommand.CanExecute(null))
            {
                return false;
            }

            UpdateAllCommand.Execute(null);
            return true;
        }

        if (!_current.CanUpdateAll)
        {
            return false;
        }

        int count = 0;
        ImmutableArray<AddonUiItem> catalog = _current.Catalog
            .Select(addon =>
            {
                if (addon.VisualState != AddonVisualState.UpdateAvailable)
                {
                    return addon;
                }

                count++;
                return addon with
                {
                    VisualState = AddonVisualState.Updating,
                    ProgressPercent = 24,
                    IsIndeterminate = false,
                    ErrorMessage = string.Empty
                };
            })
            .ToImmutableArray();
        Publish(ApplyFilter(_current with
        {
            Catalog = catalog,
            SelectedAddon = FindSelected(catalog, _current.SelectedAddon),
            NotificationMessage = $"Mise à jour de {count} addons…"
        }));
        return true;
    }

    internal void OnNavigatedAway()
    {
        if (_current.IsDetailOpen || _current.IsDeleteConfirmationOpen)
        {
            Publish(_current with
            {
                SelectedAddon = null,
                IsDetailOpen = false,
                IsDeleteConfirmationOpen = false
            });
        }
    }

    internal void AttachCommands(
        ICommand primary,
        ICommand updateAll,
        ICommand remove)
    {
        PrimaryCommand = primary ?? DisabledCommand.Instance;
        UpdateAllCommand = updateAll ?? DisabledCommand.Instance;
        RemoveCommand = remove ?? DisabledCommand.Instance;
        RaisePropertyChanged(string.Empty);
    }

    internal void ApplyRuntimeView(AddonsViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsPreview && !state.IsRuntimeConnected)
        {
            _selectionIds.Clear();
            _isLibraryOpen = false;
        }
        else if (!state.IsCatalogLoading)
        {
            _selectionIds.RemoveWhere(id => !state.Catalog.Any(addon => addon.IsCatalogEntry
                && string.Equals(addon.Id, id, StringComparison.OrdinalIgnoreCase)));
        }
        AddonUiItem? selected = FindSelected(state.Catalog, _current.SelectedAddon);
        bool keepDetails = _current.IsDetailOpen && selected is not null;
        AddonsViewState merged = state with
        {
            Filter = _current.Filter,
            SearchText = _current.SearchText,
            CategoryFilter = _current.CategoryFilter,
            SortOrder = _current.SortOrder,
            TemporarilyVisibleAddonIds = state.IsBatchOperation
                ? _current.IsBatchOperation && !_current.TemporarilyVisibleAddonIds.IsDefault
                    ? _current.TemporarilyVisibleAddonIds
                    : state.Catalog
                        .Where(addon => addon.NeedsUpdate)
                        .Select(addon => addon.Id)
                        .ToImmutableArray()
                : ImmutableArray<string>.Empty,
            SelectedAddon = keepDetails ? selected : null,
            IsDetailOpen = keepDetails,
            IsDeleteConfirmationOpen = keepDetails
                && _current.IsDeleteConfirmationOpen
                && selected?.CanRemove == true
        };
        Publish(ApplyFilter(merged));
    }

    internal void ShowLocalNotification(string message)
    {
        Publish(_current with { NotificationMessage = message ?? string.Empty });
    }

    private void ReplaceAddon(
        AddonUiItem replacement,
        string notification,
        bool keepDetailOpen)
    {
        ImmutableArray<AddonUiItem> catalog = _current.Catalog
            .Select(addon => string.Equals(addon.Id, replacement.Id, StringComparison.OrdinalIgnoreCase)
                ? replacement
                : addon)
            .ToImmutableArray();
        Publish(ApplyFilter(_current with
        {
            Catalog = catalog,
            SelectedAddon = replacement,
            IsDetailOpen = keepDetailOpen,
            IsDeleteConfirmationOpen = false,
            NotificationMessage = notification
        }));
    }

    private static AddonsViewState ApplyFilter(AddonsViewState state)
    {
        string query = state.SearchText.Trim();
        IEnumerable<AddonUiItem> visible = state.Catalog.Where(addon =>
        {
            bool filterMatches = state.Filter switch
            {
                AddonCatalogFilter.Installed => addon.IsInstalled,
                AddonCatalogFilter.Updates => addon.NeedsUpdate
                    || !state.TemporarilyVisibleAddonIds.IsDefaultOrEmpty
                    && state.TemporarilyVisibleAddonIds.Contains(
                        addon.Id,
                        StringComparer.OrdinalIgnoreCase),
                AddonCatalogFilter.Favorites => addon.IsFavorite,
                AddonCatalogFilter.Manual => addon.IsDetectedUnmanaged || !addon.IsCatalogEntry,
                _ => true
            };
            bool searchMatches = query.Length == 0
                || addon.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || addon.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || addon.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || addon.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            bool categoryMatches = state.CategoryFilter.Length == 0
                || string.Equals(addon.Category, state.CategoryFilter, StringComparison.CurrentCultureIgnoreCase);
            return filterMatches && searchMatches && categoryMatches;
        });
        IOrderedEnumerable<AddonUiItem> ordered = state.SortOrder switch
        {
            AddonSortOrder.UpdatesFirst => visible.OrderByDescending(addon => addon.NeedsUpdate)
                .ThenBy(addon => addon.Name, StringComparer.CurrentCultureIgnoreCase),
            AddonSortOrder.FavoritesFirst => visible.OrderByDescending(addon => addon.IsFavorite)
                .ThenBy(addon => addon.Name, StringComparer.CurrentCultureIgnoreCase),
            AddonSortOrder.RecentlyInstalled => visible.OrderByDescending(addon => addon.InstalledAtUtc)
                .ThenBy(addon => addon.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => visible.OrderBy(addon => addon.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        return state with
        {
            VisibleAddons = ordered.ThenBy(addon => addon.Id, StringComparer.OrdinalIgnoreCase).ToImmutableArray()
        };
    }

    private static AddonUiItem? FindSelected(
        ImmutableArray<AddonUiItem> catalog,
        AddonUiItem? selected)
    {
        return selected is null
            ? null
            : catalog.FirstOrDefault(addon =>
                string.Equals(addon.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void Publish(AddonsViewState state)
    {
        ImmutableArray<AddonUiItem> catalog = state.Catalog.Select(addon => addon with
        {
            IsFavorite = addon.IsCatalogEntry && _favoriteIds.Contains(addon.Id),
            IsSelectedForBatch = _selectionIds.Contains(addon.Id)
        }).ToImmutableArray();
        _current = ApplyFilter(state with
        {
            Catalog = catalog,
            SelectedAddon = FindSelected(catalog, state.SelectedAddon)
        });
        RaisePropertyChanged(string.Empty);
    }

    private static string L(string french, string english) => LauncherLocalization.IsEnglish ? english : french;
}
