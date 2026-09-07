using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

public sealed record ChatAttachmentRead(Stream Stream, long Length, string ContentType, string FileName);

/// <summary>Private, resumable files. SQL owns message membership and download authorization.</summary>
public sealed class ChatAttachmentStorage
{
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, (string Mime, string Kind)> Formats =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = ("image/png", "image"), [".jpg"] = ("image/jpeg", "image"),
            [".jpeg"] = ("image/jpeg", "image"), [".gif"] = ("image/gif", "image"),
            [".webp"] = ("image/webp", "image"), [".pdf"] = ("application/pdf", "document"),
            [".txt"] = ("text/plain; charset=utf-8", "document"),
            [".md"] = ("text/markdown; charset=utf-8", "document"),
            [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "document"),
            [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "document"),
            [".pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", "document"),
            [".odt"] = ("application/vnd.oasis.opendocument.text", "document"),
            [".ods"] = ("application/vnd.oasis.opendocument.spreadsheet", "document"),
            [".odp"] = ("application/vnd.oasis.opendocument.presentation", "document"),
            [".mp3"] = ("audio/mpeg", "audio"), [".ogg"] = ("audio/ogg", "audio"),
            [".wav"] = ("audio/wav", "audio"), [".mp4"] = ("video/mp4", "video"),
            [".webm"] = ("video/webm", "video")
        };

    public ChatAttachmentStorage(LauncherServerOptions options)
        : this(options.ChatMediaRoot ?? Path.Combine(options.AvatarMediaRoot, "chat"), TimeProvider.System) { }

    internal ChatAttachmentStorage(string root, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A private media directory is required.");
        _root = Path.GetFullPath(root);
        _time = time ?? TimeProvider.System;
    }

    public async Task<ChatUploadDto> BeginAsync(uint owner, ChatUploadRequest request, CancellationToken ct)
    {
        if (owner == 0) throw new ChatOperationException("chat-upload-not-found");
        string name = ValidateName(request.FileName);
        if (request.Size < 0 || request.Size > ChatLimits.MaximumAttachmentBytes)
            throw new ChatOperationException("chat-request-too-large");
        var format = Formats[Path.GetExtension(name)];
        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_root);
        await using (FileStream file = new(FilePath(id, ".part"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        UploadRecord record = new(id, owner, name, format.Mime, format.Kind, request.Size, 0,
            false, null, _time.GetUtcNow(), _time.GetUtcNow());
        await SaveAsync(record, ct).ConfigureAwait(false);
        return ToDto(record);
    }

    public async Task<ChatUploadDto> GetAsync(uint owner, string id, CancellationToken ct)
    {
        using IDisposable gate = await LockAsync(id, ct).ConfigureAwait(false);
        return ToDto(await ReadOwnedAsync(owner, id, ct).ConfigureAwait(false));
    }

    public async Task<ChatUploadDto> AppendAsync(uint owner, string id, long offset, Stream source,
        long? contentLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (contentLength is < 0 or > ChatLimits.UploadChunkBytes)
            throw new ChatOperationException("chat-request-too-large");
        using IDisposable gate = await LockAsync(id, ct).ConfigureAwait(false);
        UploadRecord record = await ReadOwnedAsync(owner, id, ct).ConfigureAwait(false);
        if (record.Complete) throw new ChatOperationException("chat-upload-complete");
        if (offset != record.Offset || offset < 0) throw new ChatOperationException("chat-upload-offset-conflict");
        if (contentLength > record.Size - offset) throw new ChatOperationException("chat-request-too-large");

        await using FileStream target = new(FilePath(id, ".part"), FileMode.Open, FileAccess.Write,
            FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Recover a process interruption after bytes were written but before metadata committed.
        if (target.Length < record.Offset) throw new ChatOperationException("chat-upload-corrupt");
        target.SetLength(record.Offset);
        target.Position = record.Offset;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        int copied = 0;
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) != 0)
            {
                if (count > ChatLimits.UploadChunkBytes - copied || count > record.Size - record.Offset - copied)
                    throw new ChatOperationException("chat-request-too-large");
                await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                copied += count;
            }
            if (contentLength is long declared && declared != copied)
                throw new ChatOperationException("chat-upload-incomplete-chunk");
            if (copied == 0 && record.Size > record.Offset)
                throw new ChatOperationException("chat-upload-incomplete-chunk");
            await target.FlushAsync(ct).ConfigureAwait(false);
            UploadRecord updated = record with { Offset = record.Offset + copied, UpdatedAt = _time.GetUtcNow() };
            await SaveAsync(updated, ct).ConfigureAwait(false);
            return ToDto(updated);
        }
        catch
        {
            target.SetLength(record.Offset);
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task<ChatUploadDto> CompleteAsync(uint owner, string id, CancellationToken ct)
    {
        using IDisposable gate = await LockAsync(id, ct).ConfigureAwait(false);
        UploadRecord record = await ReadOwnedAsync(owner, id, ct).ConfigureAwait(false);
        if (record.Complete) return ToDto(record);
        if (record.Offset != record.Size) throw new ChatOperationException("chat-upload-incomplete");
        string part = FilePath(id, ".part");
        string blob = FilePath(id, ".blob");
        string path = File.Exists(part) ? part : blob; // Recover interrupted finalization.
        await using (FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (file.Length != record.Size) throw new ChatOperationException("chat-upload-corrupt");
            await ValidateContentAsync(file, Path.GetExtension(record.FileName), record.ContentType, ct).ConfigureAwait(false);
            file.Position = 0;
            string sha = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
            record = record with { Complete = true, Sha256 = sha, UpdatedAt = _time.GetUtcNow() };
        }
        if (File.Exists(part)) File.Move(part, blob, false);
        await SaveAsync(record, ct).ConfigureAwait(false);
        return ToDto(record);
    }

    public async Task CancelAsync(uint owner, string id, CancellationToken ct)
    {
        using IDisposable gate = await LockAsync(id, ct).ConfigureAwait(false);
        UploadRecord record = await ReadOwnedAsync(owner, id, ct).ConfigureAwait(false);
        // Finished files may already be referenced by a committed message. A draft
        // removal must never destroy media displayed to other participants.
        if (record.Complete) return;
        File.Delete(FilePath(id, ".part"));
        File.Delete(FilePath(id, ".json"));
    }

    public async Task<ChatAttachmentDto> RequireCompleteAsync(uint owner, string id, CancellationToken ct)
    {
        using IDisposable gate = await LockAsync(id, ct).ConfigureAwait(false);
        UploadRecord record = await ReadOwnedAsync(owner, id, ct).ConfigureAwait(false);
        if (!record.Complete || !File.Exists(FilePath(id, ".blob")))
            throw new ChatOperationException("chat-upload-incomplete");
        record = record with { UpdatedAt = _time.GetUtcNow() };
        await SaveAsync(record, ct).ConfigureAwait(false);
        return Attachment(record);
    }

    public async Task<ChatAttachmentRead> OpenReadAsync(string id, CancellationToken ct)
    {
        using IDisposable gate = await LockAsync(id, ct).ConfigureAwait(false);
        UploadRecord record = await ReadAsync(id, ct).ConfigureAwait(false);
        if (!record.Complete) throw new ChatOperationException("chat-upload-incomplete");
        FileStream stream = new(FilePath(id, ".blob"), FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(stream, stream.Length, record.ContentType, record.FileName);
    }

    public async Task<int> CleanupAbandonedAsync(TimeSpan age,
        Func<string, CancellationToken, Task<bool>> isReferenced, CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return 0;
        int removed = 0;
        DateTimeOffset cutoff = _time.GetUtcNow() - age;
        foreach (string metadata in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            string id = Path.GetFileNameWithoutExtension(metadata);
            if (!ValidId(id)) continue;
            using IDisposable gate = await LockAsync(id, ct).ConfigureAwait(false);
            UploadRecord record;
            try { record = await ReadAsync(id, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or JsonException or ChatOperationException) { continue; }
            if (record.UpdatedAt >= cutoff) continue;
            if (record.Complete && await isReferenced(id, ct).ConfigureAwait(false)) continue;
            File.Delete(FilePath(id, ".part"));
            File.Delete(FilePath(id, ".blob"));
            File.Delete(FilePath(id, ".json"));
            ++removed;
        }
        return removed;
    }

    internal static string ValidateName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(character =>
            char.IsControl(character) || character is '\\' or '/' or ':' or '\u202e' or '\u202d'))
            throw new ChatOperationException("chat-invalid-file-name");
        string name = value.Trim();
        if (!Formats.ContainsKey(Path.GetExtension(name))) throw new ChatOperationException("chat-unsupported-file-type");
        return name;
    }

    private static async Task ValidateContentAsync(FileStream stream, string extension, string mime, CancellationToken ct)
    {
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            if (!await IsTextAsync(stream, ct).ConfigureAwait(false)) throw new ChatOperationException("chat-file-content-mismatch");
            return;
        }
        byte[] prefix = new byte[(int)Math.Min(stream.Length, 65536)];
        await stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        bool Prefix(string text) => prefix.AsSpan().StartsWith(Encoding.ASCII.GetBytes(text));
        bool At(int start, string text) => prefix.Length >= start + text.Length
            && prefix.AsSpan(start, text.Length).SequenceEqual(Encoding.ASCII.GetBytes(text));
        bool valid = extension.ToLowerInvariant() switch
        {
            ".png" => prefix.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => prefix.Length >= 3 && prefix[0] == 255 && prefix[1] == 216 && prefix[2] == 255,
            ".gif" => Prefix("GIF87a") || Prefix("GIF89a"),
            ".webp" => Prefix("RIFF") && At(8, "WEBP"),
            ".pdf" => Prefix("%PDF-"),
            ".mp3" => Prefix("ID3") || (prefix.Length >= 2 && prefix[0] == 255 && (prefix[1] & 0xe0) == 0xe0),
            ".ogg" => Prefix("OggS"),
            ".wav" => Prefix("RIFF") && At(8, "WAVE"),
            ".mp4" => At(4, "ftyp"),
            ".webm" => prefix.AsSpan().StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })
                && Encoding.ASCII.GetString(prefix.AsSpan(0, Math.Min(prefix.Length, 4096))).Contains("webm", StringComparison.Ordinal),
            ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" =>
                Prefix("PK\u0003\u0004") && ValidateOfficeDocument(stream, extension, mime),
            _ => false
        };
        if (!valid) throw new ChatOperationException("chat-file-content-mismatch");
    }

    private static async Task<bool> IsTextAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            Decoder decoder = StrictUtf8.GetDecoder();
            byte[] buffer = new byte[65536];
            char[] chars = new char[StrictUtf8.GetMaxCharCount(buffer.Length)];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                for (int index = 0; index < read; ++index)
                    if (buffer[index] < 9 || buffer[index] is > 13 and < 32) return false;
                decoder.Convert(buffer.AsSpan(0, read), chars, false, out _, out _, out _);
            }
            decoder.Convert(ReadOnlySpan<byte>.Empty, chars, true, out _, out _, out _);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool ValidateOfficeDocument(FileStream file, string extension, string mime)
    {
        try
        {
            // Bound the ZIP central directory before ZipArchive allocates entries.
            int tailLength = (int)Math.Min(file.Length, 65557);
            byte[] tail = new byte[tailLength];
            file.Position = file.Length - tailLength;
            file.ReadExactly(tail);
            int end = -1;
            for (int index = tail.Length - 22; index >= 0; --index)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054b50
                    && index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) == tail.Length)
                { end = index; break; }
            }
            if (end < 0) return false;
            ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
            uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
            if (entries is 0 or > 4096 || directorySize > 4 * 1024 * 1024) return false;
            file.Position = 0;
            using ZipArchive zip = new(file, ZipArchiveMode.Read, true);
            if (zip.Entries.Count != entries || zip.Entries.Any(entry =>
                entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))) return false;
            if (extension.StartsWith(".od", StringComparison.OrdinalIgnoreCase))
            {
                ZipArchiveEntry? marker = zip.GetEntry("mimetype");
                if (marker is null || marker.Length > 256 || zip.GetEntry("content.xml") is null) return false;
                using StreamReader text = new(marker.Open(), StrictUtf8);
                return text.ReadToEnd().Trim() == mime;
            }
            string required = extension.ToLowerInvariant() switch
            {
                ".docx" => "word/document.xml", ".xlsx" => "xl/workbook.xml", ".pptx" => "ppt/presentation.xml", _ => ""
            };
            ZipArchiveEntry? types = zip.GetEntry("[Content_Types].xml");
            return zip.GetEntry(required) is not null && zip.GetEntry("_rels/.rels") is not null
                && types is { Length: > 0 and <= 131072 };
        }
        catch (Exception error) when (error is InvalidDataException or IOException or DecoderFallbackException) { return false; }
    }

    private async Task<UploadRecord> ReadOwnedAsync(uint owner, string id, CancellationToken ct)
    {
        UploadRecord record = await ReadAsync(id, ct).ConfigureAwait(false);
        if (owner == 0 || record.Owner != owner) throw new ChatOperationException("chat-upload-not-found");
        return record;
    }

    private async Task<UploadRecord> ReadAsync(string id, CancellationToken ct)
    {
        string path = FilePath(id, ".json");
        if (!File.Exists(path)) throw new ChatOperationException("chat-upload-not-found");
        if (new FileInfo(path).Length > 8192) throw new ChatOperationException("chat-upload-corrupt");
        await using FileStream stream = File.OpenRead(path);
        UploadRecord record = await JsonSerializer.DeserializeAsync<UploadRecord>(stream, ChatJson.Options, ct).ConfigureAwait(false)
            ?? throw new ChatOperationException("chat-upload-corrupt");
        if (record.Id != id || record.Owner == 0 || record.Size < 0 || record.Size > ChatLimits.MaximumAttachmentBytes
            || record.Offset < 0 || record.Offset > record.Size) throw new ChatOperationException("chat-upload-corrupt");
        return record;
    }

    private async Task SaveAsync(UploadRecord record, CancellationToken ct)
    {
        string temporary = FilePath(record.Id, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, record, ChatJson.Options, ct).ConfigureAwait(false);
            File.Move(temporary, FilePath(record.Id, ".json"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string FilePath(string id, string suffix)
    {
        if (!ValidId(id)) throw new ChatOperationException("chat-upload-not-found");
        string path = Path.GetFullPath(Path.Combine(_root, id + suffix));
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new ChatOperationException("chat-upload-not-found");
        return path;
    }

    private static bool ValidId(string id) => id.Length == 32 && id.All(character => char.IsAsciiHexDigit(character));

    private async Task<IDisposable> LockAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ChatOperationException("chat-upload-not-found");
        SemaphoreSlim semaphore = _locks.GetOrAdd(id, _ => new(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Gate(semaphore);
    }

    private static ChatUploadDto ToDto(UploadRecord record) => new()
    {
        Id = record.Id, FileName = record.FileName, ContentType = record.ContentType, Size = record.Size,
        Offset = record.Offset, IsComplete = record.Complete, Attachment = record.Complete ? Attachment(record) : null
    };

    private static ChatAttachmentDto Attachment(UploadRecord record) => new()
    {
        Id = record.Id, FileName = record.FileName, ContentType = record.ContentType, Kind = record.Kind,
        Size = record.Size, Sha256 = record.Sha256, Url = "/api/v2/chat/attachments/" + record.Id
    };

    private sealed record UploadRecord(string Id, uint Owner, string FileName, string ContentType, string Kind,
        long Size, long Offset, bool Complete, string? Sha256, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed class Gate(SemaphoreSlim semaphore) : IDisposable { public void Dispose() => semaphore.Release(); }
}
