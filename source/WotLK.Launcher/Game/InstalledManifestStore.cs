using System.IO;
using System.Text;
using System.Text.Json;

namespace WotLK.Launcher.Game;

internal sealed class InstalledManifestStore
{
    internal const string CacheFileName = "client-manifest-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<string, bool> _canWrite;

    internal InstalledManifestStore(Func<string, bool>? canWrite = null)
    {
        _canWrite = canWrite ?? GameDirectoryAccess.CanWrite;
    }

    internal string GetPath(string installRoot)
    {
        return Path.Combine(installRoot, CacheFileName);
    }

    internal LauncherManifest? Load(string installRoot)
    {
        string historyPath = GetPath(installRoot);
        if (!File.Exists(historyPath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(historyPath);
            return JsonSerializer.Deserialize<LauncherManifest>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void Save(string installRoot, LauncherManifest manifest)
    {
        if (!_canWrite(installRoot))
        {
            return;
        }

        Directory.CreateDirectory(installRoot);
        JsonSerializerOptions options = new(JsonOptions)
        {
            WriteIndented = true
        };
        string json = JsonSerializer.Serialize(manifest, options);
        File.WriteAllText(
            GetPath(installRoot),
            json + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
