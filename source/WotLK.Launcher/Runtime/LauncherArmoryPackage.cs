using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WotLK.Launcher.Runtime;

internal static class LauncherArmoryPackage
{
    internal const string ResourceName = "Atlas.Armory.Runtime.zip";
    private static readonly object ExtractionGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record PackageFile(string Path, long Size, string Sha256);
    private sealed record PackageManifest(int SchemaVersion, PackageFile[] Files);

    internal static LauncherArmoryLocalConfiguration LoadConfiguration(string? clientRoot)
    {
        using Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The armory runtime is missing from this launcher package.");
        string root = Extract(payload, Path.Combine(LauncherSettings.SettingsDirectory, "armory", "runtime"));
        return new LauncherArmoryLocalConfiguration(
            Path.Combine(root, "node", "node.exe"), Path.Combine(root, "app", "launcher-server.cjs"),
            IsPackaged: true,
            ClientRoot: !string.IsNullOrWhiteSpace(clientRoot) && Path.IsPathFullyQualified(clientRoot)
                ? Path.GetFullPath(clientRoot) : null,
            DataRoot: Path.Combine(LauncherSettings.SettingsDirectory, "armory", "data"),
            VendorRoot: Path.Combine(root, "vendor", "wow-export", "src", "js"),
            AssetRoot: Path.Combine(root, "assets"),
            MetadataRoot: Path.Combine(root, "metadata"),
            WebViewInstallerPath: Path.Combine(root, "prerequisites", "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"));
    }

    internal static string Extract(Stream payload, string cacheRoot)
    {
        if (!payload.CanSeek) throw new ArgumentException("The runtime payload must be seekable.", nameof(payload));
        string revision = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        payload.Position = 0;
        string root = Path.GetFullPath(cacheRoot);
        string destination = Path.Combine(root, revision);
        using ZipArchive archive = new(payload, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Runtime manifest missing.");
        if (manifestEntry.Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid runtime manifest size.");
        PackageManifest manifest;
        using (Stream stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<PackageManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException("Invalid runtime manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Files is null || manifest.Files.Length is < 5 or > 20000)
            throw new InvalidDataException("Invalid runtime file list.");
        Dictionary<string, PackageFile> files = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (PackageFile file in manifest.Files)
        {
            if (file is null || string.IsNullOrEmpty(file.Sha256)) throw new InvalidDataException("Invalid runtime file entry.");
            _ = ResolveFile(destination, file.Path);
            if (file.Size is < 0 or > 320 * 1024 * 1024 || file.Sha256.Length != 64
                || !file.Sha256.All(Uri.IsHexDigit) || !files.TryAdd(file.Path, file))
                throw new InvalidDataException("Invalid runtime file entry.");
            totalBytes += file.Size;
        }
        if (totalBytes > 768L * 1024 * 1024) throw new InvalidDataException("Runtime is too large.");
        string[] required = ["node/node.exe", "app/launcher-server.cjs", "vendor/wow-export/src/js/casc/casc-source-local.js",
            "metadata/manifest.json", "assets/Fonts/Inter-Regular.ttf",
            "prerequisites/MicrosoftEdgeWebView2RuntimeInstallerX64.exe"];
        if (required.Any(path => !files.ContainsKey(path))) throw new InvalidDataException("Incomplete armory runtime.");
        lock (ExtractionGate)
        {
            Directory.CreateDirectory(root);
            if (Directory.Exists(destination) && Verify(destination, manifest.Files)) return destination;
            // A fresh directory is used if an earlier extraction is incomplete; no running runtime is overwritten.
            string stage = Path.Combine(root, revision + "." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                foreach (PackageFile file in manifest.Files)
                {
                    ZipArchiveEntry entry = archive.GetEntry(file.Path)
                        ?? throw new InvalidDataException("Missing runtime resource.");
                    if (entry.Length != file.Size) throw new InvalidDataException("Runtime resource size mismatch.");
                    string target = ResolveFile(stage, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using Stream source = entry.Open();
                    using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    source.CopyTo(output);
                }
                if (!Verify(stage, manifest.Files)) throw new InvalidDataException("Runtime verification failed.");
                if (Directory.Exists(destination)) return stage;
                Directory.Move(stage, destination);
                return destination;
            }
            catch
            {
                // Only remove the fresh child directory created by this invocation.
                if (Directory.Exists(stage) && Path.GetDirectoryName(stage) == root) Directory.Delete(stage, recursive: true);
                throw;
            }
        }
    }

    private static string ResolveFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.StartsWith('/') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Invalid runtime path.");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime path escapes its directory.");
        return path;
    }

    private static bool Verify(string directory, PackageFile[] files)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (PackageFile file in files)
            {
                string path = ResolveFile(directory, file.Path);
                for (string? parent = Path.GetDirectoryName(path); parent is not null && parent != directory; parent = Path.GetDirectoryName(parent))
                    if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
                FileInfo info = new(path);
                if (!info.Exists || info.Length != file.Size || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                using Stream stream = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
