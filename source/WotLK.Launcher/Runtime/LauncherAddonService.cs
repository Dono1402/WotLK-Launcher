using System.Net.Http;

namespace WotLK.Launcher.Runtime;

internal interface IAddonManagementService
{
    Task<AddonCatalog> LoadCatalogAsync(CancellationToken cancellationToken);

    IReadOnlyDictionary<string, AddonInspection> Inspect(
        AddonCatalog catalog,
        string installRoot);

    Task ApplySelectionAsync(
        AddonCatalog catalog,
        string installRoot,
        IReadOnlyDictionary<string, bool> selection,
        IProgress<AddonTransferProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken);

    IReadOnlyList<ManualAddonInstallation> InspectManualAddons(AddonCatalog catalog, string installRoot) => [];

    Task<AddonVerificationResult> VerifyAsync(AddonCatalog catalog, string installRoot, string addonId, CancellationToken cancellationToken)
        => Task.FromResult(new AddonVerificationResult(addonId, AddonVerificationStatus.LegacyUnverified, 0, [], [], [], DateTimeOffset.UtcNow));

    void ValidatePlan(AddonCatalog catalog, string installRoot, IEnumerable<string> installIds,
        IEnumerable<string> removalIds, bool allowExternalReplacement, CancellationToken cancellationToken) { }

    Task ApplyPackageAsync(AddonCatalog catalog, string installRoot, AddonPackage package, bool install,
        bool forceReinstall, bool allowExternalReplacement, IProgress<AddonTransferProgress>? progress,
        Action<string>? log, CancellationToken cancellationToken)
        => ApplySelectionAsync(new AddonCatalog { SchemaVersion = catalog.SchemaVersion, ClientInterface = catalog.ClientInterface, Addons = [package] },
            installRoot, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [package.Id] = install }, progress, log, cancellationToken);
}

internal sealed class LegacyAddonManagementService : IAddonManagementService
{
    internal static readonly Uri ProductionCatalogUri =
        new("https://animeclub.fr/wotlk/addons/catalog.json", UriKind.Absolute);

    private readonly HttpClient _httpClient;
    private readonly Uri _catalogUri;

    internal LegacyAddonManagementService(HttpClient httpClient, Uri? catalogUri = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _catalogUri = catalogUri ?? ProductionCatalogUri;
    }

    public Task<AddonCatalog> LoadCatalogAsync(CancellationToken cancellationToken) =>
        AddonInstallServices.LoadCatalogAsync(
            _httpClient,
            _catalogUri,
            cancellationToken);

    public IReadOnlyDictionary<string, AddonInspection> Inspect(
        AddonCatalog catalog,
        string installRoot) =>
        AddonInstallServices.Inspect(catalog, installRoot);

    public Task ApplySelectionAsync(
        AddonCatalog catalog,
        string installRoot,
        IReadOnlyDictionary<string, bool> selection,
        IProgress<AddonTransferProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken) =>
        AddonInstallServices.ApplySelectionAsync(
            _httpClient,
            catalog,
            installRoot,
            selection,
            progress,
            log,
            cancellationToken);

    public IReadOnlyList<ManualAddonInstallation> InspectManualAddons(AddonCatalog catalog, string installRoot)
        => AddonInstallServices.InspectManualAddons(catalog, installRoot);

    public Task<AddonVerificationResult> VerifyAsync(AddonCatalog catalog, string installRoot, string addonId, CancellationToken cancellationToken)
        => AddonInstallServices.VerifyAsync(catalog, installRoot, addonId, cancellationToken);

    public void ValidatePlan(AddonCatalog catalog, string installRoot, IEnumerable<string> installIds,
        IEnumerable<string> removalIds, bool allowExternalReplacement, CancellationToken cancellationToken)
        => AddonInstallServices.ValidatePlan(catalog, installRoot, installIds, removalIds, allowExternalReplacement, cancellationToken);

    public Task ApplyPackageAsync(AddonCatalog catalog, string installRoot, AddonPackage package, bool install,
        bool forceReinstall, bool allowExternalReplacement, IProgress<AddonTransferProgress>? progress,
        Action<string>? log, CancellationToken cancellationToken)
        => AddonInstallServices.ApplySelectionAsync(_httpClient, catalog, installRoot,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [package.Id] = install }, progress, log, cancellationToken,
            forceReinstall, allowExternalReplacement, resolveDependencies: false);
}

internal interface IAddonsSessionContext
{
    event EventHandler<AuthSessionSnapshotEventArgs>? SnapshotChanged;

    AuthSessionSnapshot CurrentSnapshot { get; }

    Task<AtlasRequestPreparationStatus> PrepareAuthenticatedRequestAsync(
        CancellationToken cancellationToken);

    void NotifyAuthenticatedRequestUnauthorized();
}

internal sealed class LauncherAddonsSessionContext : IAddonsSessionContext
{
    private readonly LauncherSessionCoordinator _session;

    internal LauncherAddonsSessionContext(LauncherSessionCoordinator session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public event EventHandler<AuthSessionSnapshotEventArgs>? SnapshotChanged
    {
        add => _session.SnapshotChanged += value;
        remove => _session.SnapshotChanged -= value;
    }

    public AuthSessionSnapshot CurrentSnapshot => _session.CurrentSnapshot;

    public Task<AtlasRequestPreparationStatus> PrepareAuthenticatedRequestAsync(
        CancellationToken cancellationToken) =>
        _session.PrepareAuthenticatedRequestAsync(cancellationToken);

    public void NotifyAuthenticatedRequestUnauthorized() =>
        _session.NotifyAuthenticatedRequestUnauthorized();
}
