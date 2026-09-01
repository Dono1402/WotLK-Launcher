using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WotLK.Launcher.Server.Database;

internal sealed record LauncherSchemaMigration(
    uint Version,
    string Name,
    string Sql,
    byte[] Sha256);

internal interface ILauncherSchemaMigrationSource
{
    IReadOnlyList<LauncherSchemaMigration> Load();
}

internal sealed partial class EmbeddedLauncherSchemaMigrationSource : ILauncherSchemaMigrationSource
{
    private readonly Assembly _assembly;

    internal EmbeddedLauncherSchemaMigrationSource(Assembly? assembly = null)
    {
        _assembly = assembly ?? typeof(EmbeddedLauncherSchemaMigrationSource).Assembly;
    }

    public IReadOnlyList<LauncherSchemaMigration> Load()
    {
        List<LauncherSchemaMigration> migrations = [];
        foreach (string resourceName in _assembly.GetManifestResourceNames())
        {
            Match match = MigrationResourcePattern().Match(resourceName);
            if (!match.Success)
                continue;

            using Stream stream = _assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Ressource de migration introuvable : {resourceName}.");
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string sql = NormalizeSql(reader.ReadToEnd());
            uint version = uint.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture);
            string name = match.Groups["name"].Value;
            migrations.Add(new LauncherSchemaMigration(
                version,
                name,
                sql,
                SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        }

        LauncherSchemaMigration[] ordered = migrations.OrderBy(item => item.Version).ToArray();
        if (ordered.Length == 0)
            throw new InvalidOperationException("Aucune migration SQL Atlas n'est embarquee.");
        if (ordered.Select(item => item.Version).Distinct().Count() != ordered.Length)
            throw new InvalidOperationException("Deux migrations Atlas utilisent la meme version.");
        if (ordered[0].Version != 1 || ordered[^1].Version != ordered.Length)
            throw new InvalidOperationException("Les migrations Atlas doivent etre continues a partir de 0001.");
        return ordered;
    }

    private static string NormalizeSql(string sql)
        => sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";

    [GeneratedRegex(@"\.Database\.Migrations\.(?<version>\d{4})_(?<name>[a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationResourcePattern();
}
