using System.IO;
using System.Security.Cryptography;
using System.Text;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal sealed record ChatSelectedFile(string SourcePath, string FileName, string ContentType, long Size,
    DateTimeOffset LastWriteAt, string Sha256);

internal interface IChatAttachmentFileSource
{
    Task<ChatSelectedFile> InspectAsync(uint ownerAccountId, string path, CancellationToken cancellationToken);
    Task<Stream> OpenVerifiedAsync(ChatLocalUpload file, CancellationToken cancellationToken);
    Task<Stream> OpenPreviewAsync(ChatLocalUpload file, CancellationToken cancellationToken);
    Task DeleteStagedAsync(uint ownerAccountId, string path, CancellationToken cancellationToken);
}

internal sealed class ChatAttachmentFileSource : IChatAttachmentFileSource
{
    private readonly string _root;

    internal ChatAttachmentFileSource(string root, Uri apiBaseUri)
    {
        string environment = apiBaseUri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        string partition = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment))).ToLowerInvariant();
        _root = Path.Combine(Path.GetFullPath(root), partition);
    }

    internal static string ContentTypeForName(string fileName) =>
        ChatAttachmentFormats.TryGetByFileName(fileName, out ChatAttachmentFormat? format)
            ? format.ContentType : throw new ChatWorkspaceException("chat-file-type-not-supported");

    public async Task<ChatSelectedFile> InspectAsync(uint ownerAccountId, string path, CancellationToken cancellationToken)
    {
        if (ownerAccountId == 0) throw new ArgumentOutOfRangeException(nameof(ownerAccountId));
        if (!Path.IsPathFullyQualified(path)) throw new ChatWorkspaceException("chat-invalid-file");
        string resolved = Path.GetFullPath(path);
        string fileName = Path.GetFileName(resolved);
        if (fileName.Length is 0 or > 255) throw new ChatWorkspaceException("chat-invalid-file");
        string contentType = ContentTypeForName(fileName);
        await using FileStream stream = Open(resolved);
        if (stream.Length is <= 0 or > ChatLimits.MaximumAttachmentBytes) throw new ChatWorkspaceException("chat-file-too-large");
        if (ChatAttachmentFormats.TryGetByFileName(fileName, out ChatAttachmentFormat? format)
            && format.Kind is "audio" or "video" && !ChatMediaSignatures.Matches(stream, format, cancellationToken))
            throw new ChatWorkspaceException("chat-file-content-mismatch");
        string directory = AccountDirectory(ownerAccountId);
        Directory.CreateDirectory(directory);
        string staged = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".blob");
        string temporary = staged + ".partial";
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long copied = 0;
            await using (FileStream target = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (copied > ChatLimits.MaximumAttachmentBytes - count) throw new ChatWorkspaceException("chat-file-too-large");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    copied += count;
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }
            if (copied != stream.Length) throw new ChatWorkspaceException("chat-file-changed");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, staged);
            string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return new ChatSelectedFile(staged, fileName, contentType, copied, File.GetLastWriteTimeUtc(staged), sha256);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Delete(staged); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public Task DeleteStagedAsync(uint ownerAccountId, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string expectedDirectory = Path.GetFullPath(AccountDirectory(ownerAccountId));
        string candidate = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(candidate), expectedDirectory, StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(candidate) != ".blob" || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(candidate), "N", out _))
            throw new InvalidDataException("Only this account's staged Messages file can be removed.");
        File.Delete(candidate);
        return Task.CompletedTask;
    }

    private string AccountDirectory(uint ownerAccountId)
    {
        if (ownerAccountId == 0) throw new ArgumentOutOfRangeException(nameof(ownerAccountId));
        return Path.Combine(_root, ownerAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "staged");
    }

    public async Task<Stream> OpenVerifiedAsync(ChatLocalUpload file, CancellationToken cancellationToken)
    {
        FileStream stream = Open(file.SourcePath);
        try
        {
            if (stream.Length != file.Size || File.GetLastWriteTimeUtc(file.SourcePath) != file.LastWriteAt.UtcDateTime)
                throw new ChatWorkspaceException("chat-file-changed");
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal)) throw new ChatWorkspaceException("chat-file-changed");
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public Task<Stream> OpenPreviewAsync(ChatLocalUpload file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream = Open(file.SourcePath);
        if (stream.Length != file.Size || File.GetLastWriteTimeUtc(file.SourcePath) != file.LastWriteAt.UtcDateTime)
        {
            stream.Dispose();
            throw new ChatWorkspaceException("chat-file-changed");
        }
        return Task.FromResult<Stream>(stream);
    }

    private static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
