using System.IO;

namespace WotLK.Launcher.Installer.Setup;

/// <summary>
/// Holds the setup executable open for the complete installer lifetime. The
/// restrictive share mode prevents another process from replacing or rewriting
/// the executable between the initial UI and the Uninstall.exe copy.
/// </summary>
internal sealed class InstallerSetupSource : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private InstallerSetupSource(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
        Length = stream.Length;
    }

    internal string Path { get; }

    internal long Length { get; }

    internal static InstallerSetupSource CaptureCurrentProcess()
    {
        string path = Environment.ProcessPath
            ?? throw new InvalidOperationException("Impossible de localiser AtlasLauncherSetup.exe.");
        return Open(path);
    }

    internal static InstallerSetupSource Open(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        InstallerPathValidator.DemandNoReparsePoints(fullPath);
        FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length <= 0)
            {
                throw new InvalidDataException("Le fichier setup est vide.");
            }

            return new InstallerSetupSource(fullPath, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal Stream RewindForCopy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Position = 0;
        return _stream;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
