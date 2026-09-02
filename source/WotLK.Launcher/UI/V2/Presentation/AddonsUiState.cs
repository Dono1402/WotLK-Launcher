using System.Collections.Immutable;

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
    Error
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
    public bool IsInstalled => !string.IsNullOrWhiteSpace(InstalledVersion)
        || VisualState is AddonVisualState.Installed
            or AddonVisualState.UpdateAvailable
            or AddonVisualState.Updating
            or AddonVisualState.Removing;

    public bool NeedsUpdate => VisualState is AddonVisualState.UpdateAvailable
        or AddonVisualState.Updating
        || VisualState == AddonVisualState.Error && IsInstalled;

    public bool IsBusy => VisualState is AddonVisualState.Installing
        or AddonVisualState.Updating
        or AddonVisualState.Removing;

    public bool ShowsProgress => IsBusy;

    public bool ShowsProgressPercent => ProgressPercent.HasValue;

    public bool ShowsError => VisualState == AddonVisualState.Error
        && !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool ShowsAction => VisualState != AddonVisualState.Installed;

    public bool CanInvokePrimary => VisualState is AddonVisualState.NotInstalled
        or AddonVisualState.UpdateAvailable
        or AddonVisualState.Error;

    public bool CanRemove => IsInstalled && !IsBusy;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);

    public string StatusLabel => VisualState switch
    {
        AddonVisualState.Installed => "Installé",
        AddonVisualState.UpdateAvailable => "Mise à jour",
        AddonVisualState.Installing => "Installation",
        AddonVisualState.Updating => "Mise à jour",
        AddonVisualState.Removing => "Suppression",
        AddonVisualState.Error => "Erreur",
        _ => "Non installé"
    };

    public string ActionLabel => VisualState switch
    {
        AddonVisualState.NotInstalled => "Installer",
        AddonVisualState.UpdateAvailable => "Mettre à jour",
        AddonVisualState.Installing => "Installation…",
        AddonVisualState.Updating => "Mise à jour…",
        AddonVisualState.Removing => "Suppression…",
        AddonVisualState.Error => "Réessayer",
        _ => string.Empty
    };

    public string VersionSummary => IsInstalled
        ? VisualState == AddonVisualState.UpdateAvailable
            ? $"{InstalledVersion}  →  {AvailableVersion}"
            : $"Version {InstalledVersion}"
        : $"Version {AvailableVersion}";

    public string AvailableVersionText => $"Version disponible  {AvailableVersion}";

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
    string NotificationMessage)
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
        && UpdateCount > 1
        && Catalog.All(addon => !addon.IsBusy);

    public bool IsInteractive => IsPreview;

    public string EmptyTitle => TotalCount == 0
        ? "Aucun addon disponible"
        : "Aucun résultat";

    public string EmptyDescription => TotalCount == 0
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

    public AddonsViewState Current => _current;

    internal bool UpdateSearch(string? value)
    {
        if (!_current.IsPreview)
        {
            return false;
        }

        string search = value?.Trim() ?? string.Empty;
        Publish(ApplyFilter(_current with
        {
            SearchText = search,
            NotificationMessage = string.Empty
        }));
        return true;
    }

    internal bool SelectFilter(AddonCatalogFilter filter)
    {
        if (!_current.IsPreview)
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
        if (!_current.IsPreview)
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
        if (!_current.IsPreview || !_current.IsDetailOpen)
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
        if (!_current.IsPreview || _current.SelectedAddon?.CanRemove != true)
        {
            return false;
        }

        Publish(_current with { IsDeleteConfirmationOpen = true });
        return true;
    }

    internal void CancelRemove()
    {
        if (_current.IsPreview && _current.IsDeleteConfirmationOpen)
        {
            Publish(_current with { IsDeleteConfirmationOpen = false });
        }
    }

    internal bool ConfirmRemove()
    {
        if (!_current.IsPreview || _current.SelectedAddon is not AddonUiItem selected)
        {
            return false;
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
            return false;
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
        IEnumerable<AddonUiItem> visible = state.Catalog.Where(addon =>
        {
            bool filterMatches = state.Filter switch
            {
                AddonCatalogFilter.Installed => addon.IsInstalled,
                AddonCatalogFilter.Updates => addon.NeedsUpdate,
                _ => true
            };
            bool searchMatches = string.IsNullOrWhiteSpace(state.SearchText)
                || addon.Name.Contains(state.SearchText, StringComparison.CurrentCultureIgnoreCase)
                || addon.Description.Contains(state.SearchText, StringComparison.CurrentCultureIgnoreCase);
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
