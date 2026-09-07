using System.IO;

namespace WotLK.Launcher.Runtime;

public sealed record ChatMediaStream(
    Stream Stream,
    string ContentType,
    long? Length = null,
    string? ContentRange = null,
    int StatusCode = 200,
    string? FileName = null,
    IDisposable? Owner = null) : IDisposable
{
    public void Dispose()
    {
        Stream.Dispose();
        Owner?.Dispose();
    }
}
