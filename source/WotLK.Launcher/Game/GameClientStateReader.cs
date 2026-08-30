using System.IO;
using System.Text.Json;

namespace WotLK.Launcher.Game;

internal sealed class GameClientStateReader
{
    private readonly Func<string, bool> _hasPlayableClient;

    internal GameClientStateReader(Func<string, bool>? hasPlayableClient = null)
    {
        _hasPlayableClient = hasPlayableClient ?? GameInstallServices.HasPlayableClient;
    }

    internal GameClientLocalState Read(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string installPath = LauncherSettings.NormalizeInstallPath(settings.InstallPath);
        bool isPlayable = _hasPlayableClient(installPath);
        return new GameClientLocalState(
            installPath,
            LauncherSettings.NormalizeGameLocale(settings.GameLocale),
            isPlayable,
            ReadInstalledVersion(installPath),
            GameUpdateKnowledge.Unknown);
    }

    internal string? ReadInstalledVersion(string installPath)
    {
        string markerPath = Path.Combine(installPath, GameInstallServices.ClientMarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(markerPath));
            if (!document.RootElement.TryGetProperty("clientVersion", out JsonElement versionElement))
            {
                return null;
            }

            return versionElement.GetString();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
