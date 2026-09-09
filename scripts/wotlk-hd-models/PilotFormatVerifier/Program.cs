using System.Security.Cryptography;
using System.Text.Json;
using WoWFormatLib;
using WoWFormatLib.FileProviders;
using WoWFormatLib.FileReaders;

// Independent structural parse, not a renderer, loader test or client writer.
if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: PilotFormatVerifier <pilot-manifest.json> [stock-human-male.m2]");
    return 64;
}
var manifestPath = Path.GetFullPath(args[0]);
using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
var root = Path.GetDirectoryName(manifestPath)!;
var files = new Dictionary<uint, string>();
string? modelPath = null;
int verified = 0;
foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
{
    var relative = asset.GetProperty("path").GetString()!;
    var path = Path.GetFullPath(Path.Combine(root, relative));
    if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Manifest path leaves pilot directory.");
    if (new FileInfo(path).Length > 64 * 1024 * 1024)
        throw new InvalidDataException("Asset exceeds bounds.");
    using var stream = File.OpenRead(path);
    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    if (hash != asset.GetProperty("outputSha256").GetString())
        throw new InvalidDataException($"Asset hash mismatch: {relative}");
    if (asset.GetProperty("fileDataId").ValueKind == JsonValueKind.Number)
        files.Add(asset.GetProperty("fileDataId").GetUInt32(), path);
    if (asset.GetProperty("role").GetString() == "experimental-model")
        modelPath = path;
    verified++;
}
FileProvider.SetProvider(new LocalPilotProvider(files), "offline-pilot");
var reader = new M2Reader();
using (var stream = File.OpenRead(modelPath ?? throw new InvalidDataException("No model in manifest.")))
    reader.LoadM2(stream, true);
var model = reader.model;
if (model.version != 274 || model.vertices.Length != 16271 || model.bones.Length != 228
    || model.animations.Length != 241 || model.skins.Length != 4 || model.animFileDataIDs.Length != 53
    || model.textures.Length != 8 || model.cameras.Length != 2)
    throw new InvalidDataException("Independent parser returned unexpected pilot counts.");
object? stock = null;
if (args.Length == 2)
{
    var stockReader = new M2Reader();
    using var stream = File.OpenRead(args[1]);
    stockReader.LoadM2(stream, false);
    stock = new { version = stockReader.model.version, vertices = stockReader.model.vertices.Length,
                  bones = stockReader.model.bones.Length, cameras = stockReader.model.cameras.Length };
}
Console.WriteLine(JsonSerializer.Serialize(new { verifiedAssetHashes = verified, independentParser = "WoWFormatLib",
    version = model.version, vertices = model.vertices.Length, bones = model.bones.Length,
    animations = model.animations.Length, externalAnimationIds = model.animFileDataIDs.Length,
    cameras = model.cameras.Length,
    skins = model.skins.Select(skin => new { indices = skin.indices.Length,
        triangles = skin.triangles.Length, submeshes = skin.submeshes.Length, batches = skin.textureunit.Length }),
    stock, runtimeValidated = false, gameLaunched = false }, new JsonSerializerOptions { WriteIndented = true }));
return 0;

sealed class LocalPilotProvider(Dictionary<uint, string> files) : IFileProvider
{
    public bool FileExists(uint id) => files.ContainsKey(id);
    public Stream OpenFile(uint id) => File.OpenRead(files[id]);
    public uint GetFileDataIdByName(string name) => throw new NotSupportedException();
    public Stream OpenFile(string name) => throw new NotSupportedException();
    public bool FileExists(string name) => false;
    public Stream OpenFile(byte[] key) => throw new NotSupportedException();
    public bool FileExists(byte[] key) => false;
    public void SetBuild(string build) => throw new NotSupportedException();
}
