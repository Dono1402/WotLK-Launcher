using System.Collections.Immutable;
using System.Globalization;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class AddonsStateAdapter : IDisposable
{
    private const string IconRoot =
        "/WotLK.Launcher;component/Assets/Launcher/addon-icons/";

    private static readonly ImmutableHashSet<string> PackagedIconIds =
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

    private readonly AddonsUiState _target;
    private readonly LauncherAddonsCoordinator _runtime;
    private readonly Dispatcher _dispatcher;
    private long _latestSequence = -1;
    private int _disposeState;

    internal AddonsStateAdapter(
        AddonsUiState target,
        LauncherAddonsCoordinator runtime,
        Dispatcher dispatcher)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
        ApplyOrQueue(_runtime.CurrentSnapshot);
    }

    internal static AddonsViewState Project(AddonsRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ImmutableArray<AddonUiItem> catalog = snapshot.Items
            .Select(item => ProjectItem(item, snapshot))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new AddonsViewState(
            IsPreview: false,
            Catalog: catalog,
            VisibleAddons: catalog,
            Filter: AddonCatalogFilter.All,
            SearchText: string.Empty,
            SelectedAddon: null,
            IsDetailOpen: false,
            IsDeleteConfirmationOpen: false,
            IsGameRunning: snapshot.IsGameRunning,
            CatalogErrorMessage: MapCatalogError(snapshot),
            NotificationMessage: MapNotice(snapshot),
            IsRuntimeConnected: snapshot.IsAuthenticated,
            IsCatalogLoading: snapshot.LoadState == AddonsCatalogLoadState.Loading,
            CanMutate: snapshot.CanMutate,
            CanCancelCurrent: snapshot.CanCancel,
            IsBatchOperation: snapshot.OperationState == AddonsOperationState.UpdatingAll,
            ActiveAddonId: snapshot.ActiveAddonId);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
        }
    }

    private void Runtime_SnapshotChanged(object? sender, AddonsRuntimeSnapshotEventArgs e)
    {
        ApplyOrQueue(e.Snapshot);
    }

    private void ApplyOrQueue(AddonsRuntimeSnapshot snapshot)
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

    private void Apply(AddonsRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0 || snapshot.Sequence <= _latestSequence)
        {
            return;
        }

        _latestSequence = snapshot.Sequence;
        _target.ApplyRuntimeView(Project(snapshot));
    }

    private static AddonUiItem ProjectItem(
        AddonRuntimeItem item,
        AddonsRuntimeSnapshot snapshot)
    {
        bool active = string.Equals(
            item.Id,
            snapshot.ActiveAddonId,
            StringComparison.OrdinalIgnoreCase);
        AddonVisualState visualState = item.ErrorCategory != AddonsErrorCategory.None
            ? AddonVisualState.Error
            : item.ActiveOperation switch
            {
                AddonsOperationState.Installing => AddonVisualState.Installing,
                AddonsOperationState.Updating => AddonVisualState.Updating,
                AddonsOperationState.Removing => AddonVisualState.Removing,
                AddonsOperationState.Repairing => AddonVisualState.Repairing,
                _ => item.LocalStatus switch
                {
                    AddonLocalStatus.Installed => AddonVisualState.Installed,
                    AddonLocalStatus.UpdateAvailable or AddonLocalStatus.MissingFiles =>
                        AddonVisualState.UpdateAvailable,
                    _ => AddonVisualState.NotInstalled
                }
            };
        AddonPrimaryActionKind primaryAction = active && snapshot.CanCancel
            ? AddonPrimaryActionKind.Cancel
            : item.ErrorCategory != AddonsErrorCategory.None
                ? ToPrimaryAction(item.RetryAction)
                : item.LocalStatus switch
                {
                    AddonLocalStatus.MissingFiles => AddonPrimaryActionKind.Repair,
                    AddonLocalStatus.UpdateAvailable => AddonPrimaryActionKind.Update,
                    AddonLocalStatus.NotInstalled or AddonLocalStatus.DetectedUnmanaged =>
                        AddonPrimaryActionKind.Install,
                    _ => AddonPrimaryActionKind.None
                };
        AddonsRuntimeProgress progress = snapshot.Progress;
        bool hasProgress = active
            && string.Equals(progress.AddonId, item.Id, StringComparison.OrdinalIgnoreCase)
            && item.IsBusy;
        bool hasOfficialIcon = PackagedIconIds.Contains(item.Id);
        return new AddonUiItem(
            item.Id,
            item.Name,
            item.Description,
            item.Category,
            item.AvailableVersion,
            item.InstalledVersion,
            item.InterfaceVersion,
            string.IsNullOrWhiteSpace(item.Author) ? "Sélection Atlas" : item.Author,
            item.Dependencies,
            item.ManagedFolders,
            hasOfficialIcon ? IconRoot + item.Id.ToLowerInvariant() + ".png" : string.Empty,
            hasOfficialIcon,
            visualState,
            hasProgress ? progress.Percent : null,
            hasProgress && progress.IsIndeterminate,
            MapItemError(item.ErrorCategory))
        {
            IsManagedByAtlas = item.IsManaged,
            RequiresRepair = item.NeedsRepair,
            IsDetectedUnmanaged = item.IsDetectedUnmanaged,
            InstalledSha256 = item.InstalledSha256,
            InstalledAtUtc = item.InstalledAtUtc,
            PrimaryAction = primaryAction,
            UsesExplicitPrimaryAction = true,
            ActionsEnabled = snapshot.CanMutate,
            CanCancelOperation = active && snapshot.CanCancel,
            ProgressDetail = hasProgress ? FormatProgress(progress) : string.Empty
        };
    }

    private static AddonPrimaryActionKind ToPrimaryAction(AddonsRequestedAction action) =>
        action switch
        {
            AddonsRequestedAction.Install => AddonPrimaryActionKind.Install,
            AddonsRequestedAction.Update => AddonPrimaryActionKind.Update,
            AddonsRequestedAction.Repair => AddonPrimaryActionKind.Repair,
            _ => AddonPrimaryActionKind.None
        };

    private static string MapCatalogError(AddonsRuntimeSnapshot snapshot)
    {
        if (snapshot.IsCatalogStale)
        {
            return "Le catalogue Atlas n’a pas pu être actualisé. Les informations affichées sont conservées localement.";
        }
        if (snapshot.LoadState != AddonsCatalogLoadState.Failed)
        {
            return string.Empty;
        }

        return snapshot.CatalogErrorCategory switch
        {
            AddonsErrorCategory.Unauthorized => "Reconnecte-toi pour charger le catalogue Atlas.",
            AddonsErrorCategory.Network or AddonsErrorCategory.Timeout
                or AddonsErrorCategory.ServiceUnavailable =>
                "Le catalogue Atlas est temporairement indisponible.",
            _ => "Le catalogue Atlas n’a pas pu être chargé."
        };
    }

    private static string MapNotice(AddonsRuntimeSnapshot snapshot)
    {
        if (snapshot.OperationState != AddonsOperationState.None)
        {
            string addonName = snapshot.Items.FirstOrDefault(item =>
                string.Equals(item.Id, snapshot.ActiveAddonId, StringComparison.OrdinalIgnoreCase))?.Name
                ?? "l’addon";
            return snapshot.OperationState switch
            {
                AddonsOperationState.Installing => $"Installation de {addonName}…",
                AddonsOperationState.Updating => $"Mise à jour de {addonName}…",
                AddonsOperationState.Removing => $"Suppression de {addonName}…",
                AddonsOperationState.Repairing => $"Réparation de {addonName}…",
                AddonsOperationState.UpdatingAll => $"Mise à jour de {addonName}…",
                _ => string.Empty
            };
        }
        if (snapshot.Error.Category != AddonsErrorCategory.None)
        {
            return MapItemError(snapshot.Error.Category);
        }
        if (snapshot.LoadState == AddonsCatalogLoadState.Loaded
            && !snapshot.IsClientPlayable)
        {
            return "Installe d’abord le client WotLK pour gérer ses addons.";
        }

        string suffix = snapshot.IsGameRunning
            && snapshot.Notice is AddonsNoticeKind.Installed
                or AddonsNoticeKind.Updated
                or AddonsNoticeKind.Repaired
                or AddonsNoticeKind.BatchUpdated
                    ? " Utilise /reload dans le jeu pour l’activer."
                    : string.Empty;
        return snapshot.Notice switch
        {
            AddonsNoticeKind.Installed => "Addon installé." + suffix,
            AddonsNoticeKind.Updated => "Addon mis à jour." + suffix,
            AddonsNoticeKind.Removed => "Addon supprimé.",
            AddonsNoticeKind.Repaired => "Addon réparé." + suffix,
            AddonsNoticeKind.BatchUpdated => "Tous les addons disponibles ont été mis à jour." + suffix,
            AddonsNoticeKind.Cancelled => "Opération annulée.",
            _ => string.Empty
        };
    }

    internal static string MapItemError(AddonsErrorCategory category) => category switch
    {
        AddonsErrorCategory.None => string.Empty,
        AddonsErrorCategory.Unauthorized => "Ta session Atlas doit être renouvelée.",
        AddonsErrorCategory.Network => "Impossible de télécharger cet addon pour le moment.",
        AddonsErrorCategory.Timeout => "Le téléchargement a pris trop de temps. Réessaie.",
        AddonsErrorCategory.ServiceUnavailable => "Le service Addons est temporairement indisponible.",
        AddonsErrorCategory.ClientUnavailable => "Installe d’abord le client WotLK.",
        AddonsErrorCategory.AccessDenied => "Atlas n’a pas accès au dossier du jeu.",
        AddonsErrorCategory.FilesLocked => "Certains fichiers de l’addon sont utilisés par une autre application.",
        AddonsErrorCategory.Disk => "Atlas n’a pas pu écrire les fichiers de cet addon.",
        AddonsErrorCategory.InvalidPackage => "L’archive reçue n’est pas valide.",
        _ => "Une erreur inattendue empêche cette opération."
    };

    private static string FormatProgress(AddonsRuntimeProgress progress)
    {
        if (progress.BytesReceived is not long received)
        {
            return string.Empty;
        }

        string transferred = progress.TotalBytes is long total
            ? $"{FormatBytes(received)} / {FormatBytes(total)}"
            : FormatBytes(received);
        string speed = progress.BytesPerSecond is > 0
            ? $" · {FormatBytes((long)progress.BytesPerSecond.Value)}/s"
            : string.Empty;
        string eta = progress.EstimatedRemaining is TimeSpan remaining
            ? $" · {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} s restantes"
            : string.Empty;
        return transferred + speed + eta;
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
}
