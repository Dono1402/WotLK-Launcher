using System.Reflection;
using System.IO;

namespace WotLK.Launcher.Installer.Setup;

internal interface IInstallerPayloadSource
{
    long Length { get; }

    string Sha256 { get; }

    Stream OpenRead();
}

internal sealed class EmbeddedInstallerPayloadSource : IInstallerPayloadSource
{
    private readonly Assembly _assembly;
    private readonly string _resourceName;
    private readonly long _length;

    internal EmbeddedInstallerPayloadSource()
        : this(typeof(EmbeddedInstallerPayloadSource).Assembly)
    {
    }

    internal EmbeddedInstallerPayloadSource(Assembly assembly)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name =>
                name.EndsWith("Payload.WotLK.Launcher.exe", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Le payload Atlas Launcher {InstallerProduct.Version} est absent de l'installateur.");

        using Stream stream = OpenRead();
        _length = stream.Length;
    }

    public long Length => _length;

    public string Sha256 => InstallerProduct.PayloadSha256;

    public Stream OpenRead() => _assembly.GetManifestResourceStream(_resourceName)
        ?? throw new InvalidOperationException("Impossible d'ouvrir le payload Atlas Launcher.");
}

internal sealed class FileInstallerPayloadSource : IInstallerPayloadSource
{
    private readonly string _path;

    internal FileInstallerPayloadSource(string path, long length, string sha256)
    {
        _path = Path.GetFullPath(path);
        Length = length;
        Sha256 = sha256;
    }

    public long Length { get; }

    public string Sha256 { get; }

    public Stream OpenRead() => new FileStream(
        _path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
