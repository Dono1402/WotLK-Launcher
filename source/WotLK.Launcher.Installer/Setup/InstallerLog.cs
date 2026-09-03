using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace WotLK.Launcher.Installer.Setup;

internal sealed partial class InstallerLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private bool _disposed;

    internal InstallerLog(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    internal string Path => _path;

    internal void Info(string message) => Write("INFO", message);

    internal void Warning(string message) => Write("WARN", message);

    internal void Error(string message, Exception? exception = null)
    {
        string detail = exception is null
            ? message
            : $"{message} ({exception.GetType().Name}: {exception.Message})";
        Write("ERROR", detail);
    }

    internal void Flush()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string safe = Redact(message).Replace('\r', ' ').Replace('\n', ' ');
            string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] {safe}{Environment.NewLine}";
            File.AppendAllText(_path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static string Redact(string value)
    {
        string redacted = BearerRegex().Replace(value, "Bearer [REDACTED]");
        return SensitiveValueRegex().Replace(redacted, "$1=[REDACTED]");
    }

    [GeneratedRegex("(?i)(password|token|secret|authorization)\\s*[=:]\\s*[^\\s,;]+")]
    private static partial Regex SensitiveValueRegex();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();
}
