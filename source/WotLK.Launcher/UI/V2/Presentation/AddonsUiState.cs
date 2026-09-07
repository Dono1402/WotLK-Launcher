using System.Collections.Immutable;
using System.Windows.Input;
using WotLK.Launcher.UI.V2.Commands;

namespace WotLK.Launcher.UI.V2.Presentation;

public enum AddonCatalogFilter
{
    All,
    Installed,
    Updates
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

    public bool RequiresRepair { get; init; }

    public bool IsDetectedUnmanaged { get; init; }

    public string InstalledSha256 { get; init; } = string.Empty;

    public DateTimeOffset? InstalledAtUtc { get; init; }

    public AddonPrimaryActionKind PrimaryAction { get; init; }

    public bool UsesExplicitPrimaryAction { get; init; }

    public bool ActionsEnabled { get; init; } = true;

    public bool CanCancelOperation { get; init; }

    public string ProgressDetail { get; init; } = string.Empty;

    public bool IsInstalled => IsManagedByAtlas
        || !string.IsNullOrWhiteSpace(InstalledVersion)
        || VisualState is AddonVisualState.Installed
            or AddonVisualState.UpdateAvailable
            or AddonVisualState.Updating
            or AddonVisualState.Removing
            or AddonVisualState.Repairing;

    public bool NeedsUpdate => VisualState is AddonVisualState.UpdateAvailable
        or AddonVisualState.Updating
        or AddonVisualState.Repairing
        || RequiresRepair
        || VisualState == AddonVisualState.Error && IsInstalled;

    public bool IsBusy => VisualState is AddonVisualState.Installing
        or AddonVisualState.Updating
        or AddonVisualState.Removing
        or AddonVisualState.Repairing;

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

    public bool ShowsRowAction => ShowsAction && (!RequiresRepair || IsBusy);

    public bool CanInvokePrimary => CanCancelOperation
        || ActionsEnabled && !IsBusy && EffectivePrimaryAction is
            AddonPrimaryActionKind.Install
            or AddonPrimaryActionKind.Update
            or AddonPrimaryActionKind.Repair;

    public bool CanRemove => IsInstalled && !IsBusy && ActionsEnabled;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);

    public string StatusLabel => VisualState switch
    {
        _ when RequiresRepair && VisualState == AddonVisualState.UpdateAvailable => "À réparer",
        _ when IsDetectedUnmanaged && VisualState == AddonVisualState.NotInstalled => "Détecté (non géré)",
        AddonVisualState.Installed => "Installé",
        AddonVisualState.UpdateAvailable => "Mise à jour",
        AddonVisualState.Installing => "Installation",
        AddonVisualState.Updating => "Mise à jour",
        AddonVisualState.Removing => "Suppression",
        AddonVisualState.Repairing => "Réparation",
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
    public int TotalCount => Catalog.Length;

    public int InstalledCount => Catalog.Count(addon => addon.IsInstalled);

    public int UpdateCount => Catalog.Count(addon => addon.NeedsUpdate);

    public string AllFilterLabel => $"Tous  {TotalCount}";

    public string InstalledFilterLabel => $"Installés  {InstalledCount}";

    public string UpdatesFilterLabel => $"Mises à jour  {UpdateCount}";

    public string ResultsLabel => VisibleAddons.Length == TotalCount
        ? $"{TotalCount} addon{(TotalCount > 1 ? "s" : string.Empty)}"
        : $"{VisibleAddons.Length} sur {TotalCount}";

    public bool HasVisibleAddons => !VisibleAddons.IsDefaultOrEmpty;

    public bool ShowsEmpty => !HasVisibleAddons;

    public bool ShowsCatalogError => !string.IsNullOrWhiteSpace(CatalogErrorMessage);

    public bool ShowsNotification => !string.IsNullOrWhiteSpace(NotificationMessage);

    public bool CanUpdateAll => IsPreview
        ? UpdateCount > 1 && Catalog.All(addon => !addon.IsBusy)
        : IsBatchOperation && CanCancelCurrent
            || CanMutate && UpdateCount > 1;

    public bool IsInteractive => IsPreview
        || IsRuntimeConnected && !IsCatalogLoading && TotalCount > 0;

    public string UpdateAllLabel => IsBatchOperation && CanCancelCurrent
        ? "Annuler"
        : "Tout mettre à jour";

    public string EmptyTitle => IsCatalogLoading
        ? "Chargement du catalogue…"
        : TotalCount == 0
            ? "Aucun addon disponible"
            : !string.IsNullOrWhiteSpace(SearchText)
                ? $"Aucun addon trouvé pour “{SearchText}”"
                : "Aucun addon ne correspond à ce filtre.";

    public string EmptyDescription => IsCatalogLoading
        ? "Atlas récupère les informations disponibles."
        : TotalCount == 0
            ? "Le catalogue ne contient actuellement aucun addon."
            : "Modifie la recherche ou le filtre sélectionné.";
}

public sealed class AddonsUiState : BindableUiState
{
    private AddonsViewState _current;

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

    public AddonsViewState Current => _current;

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
        AddonUiItem? selected = FindSelected(state.Catalog, _current.SelectedAddon);
        bool keepDetails = _current.IsDetailOpen && selected is not null;
        AddonsViewState merged = state with
        {
            Filter = _current.Filter,
            SearchText = _current.SearchText,
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
                _ => true
            };
            bool searchMatches = query.Length == 0
                || addon.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || addon.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            return filterMatches && searchMatches;
        });
        return state with
        {
            VisibleAddons = visible
                .OrderBy(addon => addon.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(addon => addon.Id, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray()
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
        _current = state;
        RaisePropertyChanged(string.Empty);
    }
}
