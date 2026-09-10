namespace WotLK.Launcher.Installer;

internal static class InstallerServices
{
    internal const string DefaultManifestUrl =
        "https://animeclub.fr/wotlk/manifest.json";

    internal static bool IsUninstallMode(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(arg =>
            string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "/remove", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryKeepExistingManifestUrl(
        string? value,
        out string manifestUrl)
    {
        manifestUrl = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.Host,
                "animeclub.fr",
                StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !string.Equals(
                uri.AbsolutePath,
                "/wotlk/manifest.json",
                StringComparison.Ordinal))
        {
            return false;
        }

        manifestUrl = uri.ToString();
        return true;
    }
}
