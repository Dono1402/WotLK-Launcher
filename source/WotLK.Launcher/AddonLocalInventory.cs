using System.IO;
using System.Text.RegularExpressions;

namespace WotLK.Launcher;

internal static partial class AddonLocalInventory
{
    private const int MaximumTocBytes = 1024 * 1024;

    internal static IReadOnlyList<ManualAddonInstallation> Read(AddonCatalog catalog, string addonsDirectory, bool includeCatalogFolders = false)
    {
        EnsureNotLinked(addonsDirectory);
        if (!Directory.Exists(addonsDirectory)) return [];
        HashSet<string> known = catalog.Addons.SelectMany(package => package.Folders).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ManualAddonInstallation> result = [];
        foreach (string directory in Directory.EnumerateDirectories(addonsDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            string folder = Path.GetFileName(directory);
            if (folder.StartsWith('.') || !includeCatalogFolders && known.Contains(folder)) continue;
            try
            {
                EnsureNotLinked(directory);
                string[] tocFiles = Directory.GetFiles(directory, "*.toc", SearchOption.TopDirectoryOnly);
                if (tocFiles.Length == 0) continue;
                var toc = tocFiles.Select(path => (Path: path, Fields: ReadToc(path)))
                    .OrderByDescending(item => InterfaceValues(item.Fields).Contains(AddonInstallServices.SupportedInterface, StringComparer.Ordinal))
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).First();
                string[] interfaces = InterfaceValues(toc.Fields);
                string title = Field(toc.Fields, "Title-frFR", Field(toc.Fields, "Title", folder));
                string[] dependencies = new[] { "Dependencies", "RequiredDeps", "RequiredDependencies" }
                    .SelectMany(key => Field(toc.Fields, key, "").Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                result.Add(new("manual:" + folder.ToLowerInvariant(), folder, CleanTitle(title),
                    Field(toc.Fields, "Version", ""), CleanTitle(Field(toc.Fields, "Author", "")),
                    string.Join(", ", interfaces), interfaces.Contains(AddonInstallServices.SupportedInterface, StringComparer.Ordinal),
                    dependencies, ""));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                result.Add(new("manual:" + folder.ToLowerInvariant(), folder, folder, "", "", "", false, [], "unreadable"));
            }
        }
        return result;
    }

    internal static void EnsureNotLinked(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Addon paths cannot traverse symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static Dictionary<string, string> ReadToc(string path)
    {
        EnsureNotLinked(path);
        if (new FileInfo(path).Length > MaximumTocBytes) throw new IOException("Addon TOC exceeds the supported size.");
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith("##", StringComparison.Ordinal)) continue;
            int separator = line.IndexOf(':', 2);
            if (separator <= 2) continue;
            fields[line[2..separator].Trim()] = line[(separator + 1)..].Trim()[..Math.Min(line[(separator + 1)..].Trim().Length, 512)];
        }
        return fields;
    }

    private static string Field(Dictionary<string, string> fields, string name, string fallback)
        => fields.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string[] InterfaceValues(Dictionary<string, string> fields)
        => Field(fields, "Interface", "").Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CleanTitle(string value) => ColorMarkup().Replace(value, "");

    [GeneratedRegex(@"\|c[0-9a-fA-F]{8}|\|r|\|T.*?\|t|\|A.*?\|a", RegexOptions.CultureInvariant)]
    private static partial Regex ColorMarkup();
}
