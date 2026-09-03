using System.Globalization;
using System.Text.RegularExpressions;

namespace WotLK.Launcher.Server.Database;

internal static partial class LauncherSchemaMigrationCeiling
{
    internal const string EnvironmentVariableName = "WOTLK_LAUNCHER_MAX_SCHEMA_VERSION";

    internal static uint? Resolve(string? configuredValue, bool isProduction)
    {
        if (configuredValue is null)
        {
            if (isProduction)
            {
                throw new InvalidOperationException(
                    $"{EnvironmentVariableName} est obligatoire en production.");
            }

            return null;
        }

        if (!CanonicalVersion().IsMatch(configuredValue)
            || !uint.TryParse(
                configuredValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint version))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableName} doit etre un entier decimal positif sans signe, espace ni zero initial.");
        }

        return version;
    }

    [GeneratedRegex("^[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalVersion();
}
