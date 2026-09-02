using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WotLK.Launcher;

public sealed class AddonCatalog
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("clientInterface")]
    public string ClientInterface { get; set; } = "";

    [JsonPropertyName("addons")]
    public List<AddonPackage> Addons { get; set; } = [];
}

public sealed class AddonPackage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Utilitaires";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("interface")]
    public string Interface { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("installHash")]
    public string InstallHash { get; set; } = "";

    [JsonPropertyName("stripPrefix")]
    public string StripPrefix { get; set; } = "";

    [JsonPropertyName("components")]
    public List<AddonPackageComponent> Components { get; set; } = [];

    [JsonPropertyName("tokenReplacements")]
    public Dictionary<string, string> TokenReplacements { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } = [];

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = [];

    [JsonIgnore]
    internal string EffectiveInstallHash => string.IsNullOrWhiteSpace(InstallHash) ? Sha256 : InstallHash;
}

public sealed class AddonPackageComponent
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("stripPrefix")]
    public string StripPrefix { get; set; } = "";
}

internal enum AddonLocalStatus
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    MissingFiles,
    DetectedUnmanaged
}

internal sealed record AddonInspection(
    AddonLocalStatus Status,
    bool IsManaged,
    string? InstalledVersion = null,
    string? InstalledSha256 = null,
    IReadOnlyList<string>? InstalledFolders = null,
    DateTimeOffset? InstalledAtUtc = null);

internal sealed record AddonTransferProgress(string AddonName, long BytesReceived, long TotalBytes);

internal sealed class AddonSelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _statusText = "Non installé";
    private AddonLocalStatus _localStatus = AddonLocalStatus.NotInstalled;

    internal AddonSelectionItem(AddonPackage package)
    {
        Package = package;
    }

    internal AddonPackage Package { get; }

    public string Id => Package.Id;
    public string Name => Package.Name;
    public string Description => Package.Description;
    public string Category => Package.Category;
    public string VersionText => $"Version {Package.Version}  |  Interface {Package.Interface}";
    public string ShortVersionText => $"v{Package.Version}";
    public string AuthorText => string.IsNullOrWhiteSpace(Package.Author) ? "Sélection Atlas" : Package.Author;
    public string CompatibilityText => Package.Interface == "30403"
        ? "Compatible WotLK 3.4.3"
        : $"Interface {Package.Interface}";
    public string DependencyText => Package.Dependencies.Count == 0
        ? "Aucune dépendance requise"
        : "Dépendances : " + string.Join(", ", Package.Dependencies);
    public bool IsInstalled => _localStatus != AddonLocalStatus.NotInstalled;
    public bool NeedsUpdate => _localStatus is AddonLocalStatus.UpdateAvailable or AddonLocalStatus.MissingFiles;
    public string IconPath => $"Assets/Launcher/addon-icons/{Package.Id.ToLowerInvariant()}.png";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    internal void ApplyInspection(AddonInspection inspection)
    {
        _localStatus = inspection.Status;
        IsSelected = inspection.Status != AddonLocalStatus.NotInstalled;
        StatusText = inspection.Status switch
        {
            AddonLocalStatus.Installed => "À jour",
            AddonLocalStatus.UpdateAvailable => "Mise à jour disponible",
            AddonLocalStatus.MissingFiles => "Installation à réparer",
            AddonLocalStatus.DetectedUnmanaged => "Détecté (non géré)",
            _ => "Non installé"
        };
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(NeedsUpdate));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class AddonInstallState
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("addons")]
    public Dictionary<string, InstalledAddonState> Addons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class InstalledAddonState
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } = [];

    [JsonPropertyName("installedAtUtc")]
    public DateTimeOffset InstalledAtUtc { get; set; }
}
