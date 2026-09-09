using System.Security.Cryptography;
using System.Text.Json;
using CASCLib;

// This tool reads data files only. It has no game-launch, injection, or client-write code.
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: CascPilotReader <client-storage> <separate-existing-output-dir> <comma-separated-file-ids>");
    return 64;
}

var storage = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!Directory.Exists(storage) || !Directory.Exists(output)
    || Path.GetPathRoot(output) == output || IsInside(output, storage) || IsInside(storage, output))
    throw new ArgumentException("Use separate existing storage/output directories, never a drive root or client folder.");
RejectLinks(output);
// Stock upstream silently falls back to CDN in two paths. Refuse anything other
// than the separately audited/hardened local build used for this pilot.
using (var assemblyStream = File.OpenRead(typeof(CASCConfig).Assembly.Location))
    if (Convert.ToHexString(SHA256.HashData(assemblyStream))
        != "ABB8B4496195FE8629EA9C86E6E3572F3DFEC231D37D8521B53A52732F299B29")
        throw new InvalidDataException("Expected the audited local-only CascLib assembly; see pilot documentation before rebuilding.");
var ids = args[2].Split(',').Select(int.Parse).Distinct().ToArray();
if (ids.Length is < 1 or > 256 || ids.Any(id => id <= 0))
    throw new ArgumentException("Specify between 1 and 256 positive file IDs.");
if (ids.Any(id => File.Exists(Path.Combine(output, $"{id}.bin"))))
    throw new IOException("An output already exists; refusing overwrite.");

CASCConfig.UseOnlineFallbackForMissingFiles = false;
CASCConfig.ValidateData = true;
CASCConfig.ThrowOnMissingDecryptionKey = true;
CASCConfig.ThrowOnFileNotFound = true;
CASCConfig.LoadFlags = LoadFlags.None;
CDNCache.Enabled = false;
var config = CASCConfig.LoadLocalStorageConfig(storage, "wow_classic",
    new PilotLogger(Path.Combine(output, $"casc-read-{Guid.NewGuid():N}.log")));
if (config.OnlineMode || config.VersionName != "3.4.3.54261")
    throw new InvalidDataException("This pilot requires local Classic build 54261.");
var casc = CASCHandlerLite.OpenStorage(LocaleFlags.frFR, config);
var results = new List<object>();
var failure = false;
foreach (var id in ids)
{
    try
    {
        using var stream = casc.OpenFile(id) ?? throw new FileNotFoundException("FileDataID absent from local root.");
        if (stream.Length is < 4 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Asset exceeds pilot bounds.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        var path = Path.Combine(output, $"{id}.bin");
        using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            destination.Write(data);
        results.Add(new { fileDataId = id, bytes = data.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            magic = System.Text.Encoding.ASCII.GetString(data, 0, 4), outputFile = Path.GetFileName(path) });
    }
    catch (Exception error)
    {
        failure = true;
        results.Add(new { fileDataId = id, error = error.Message });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { build = config.VersionName, files = results,
    onlineFallbackEnabled = false, clientWritten = false, gameLaunched = false },
    new JsonSerializerOptions { WriteIndented = true }));
return failure ? 2 : 0;

static bool IsInside(string candidate, string root)
{
    var relative = Path.GetRelativePath(root, candidate);
    return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
        && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
}

static void RejectLinks(string path)
{
    for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Output may not traverse a junction or symbolic link.");
}

sealed record PilotLogger(string LogFileName) : ILoggerOptions
{
    public bool TimeStamp => true;
}
