using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherChatWorkspace
{
    internal async Task<ChatMediaStream?> OpenMediaAsync(string resource, string? range, CancellationToken cancellationToken)
    {
        Guard guard = RequireGuard();
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(guard.Token, cancellationToken);
        try
        {
            ChatMediaStream? response = await OpenMediaCoreAsync(guard, resource, range, linked.Token).ConfigureAwait(false);
            if (response is null) { linked.Dispose(); return null; }
            return response with { Owner = new MediaScope(response.Owner, linked) };
        }
        catch { linked.Dispose(); throw; }
    }

    private async Task<ChatMediaStream?> OpenMediaCoreAsync(Guard guard, string resource, string? range, CancellationToken cancellationToken)
    {
        if (resource.Length > 8192) return null;
        if (resource.StartsWith("attachments/", StringComparison.Ordinal))
        {
            string id = resource[12..];
            _ = LauncherChatV2ApiClient.Identifier(id);
            ChatLocalUpload? local;
            bool authorized;
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                local = _local.Uploads.FirstOrDefault(upload => (upload.LocalId == id || upload.Attachment?.Id == id)
                    && _current.State.Threads.Any(thread => thread.Id == upload.ThreadId));
                authorized = local is not null || AccessibleMessagesUnsafe().Any(message => message.Attachments.Any(attachment => attachment.Id == id))
                    || _current.State.Threads.Any(thread => thread.AvatarAttachmentId == id);
            }
            if (!authorized) return null;
            if (local is not null && !local.SourceReleased)
            {
                Stream stream = await _files.OpenPreviewAsync(local, cancellationToken).ConfigureAwait(false);
                if (!IsCurrent(guard)) { stream.Dispose(); return null; }
                return CreateLocalMedia(stream, local, range, cancellationToken);
            }
            await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
            ChatMediaStream result = await _api.OpenAttachmentAsync(local?.Attachment?.Id ?? id, range, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(guard)) { result.Dispose(); return null; }
            return result;
        }
        if (resource.StartsWith("avatars/", StringComparison.Ordinal))
        {
            string[] parts = resource.Split('/');
            if (parts.Length != 3 || parts[2].Length > 128 || !uint.TryParse(parts[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out uint accountId)) return null;
            string? avatar;
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                avatar = ProfilesUnsafe().FirstOrDefault(profile => profile.AccountId == accountId && profile.AvatarUrl is not null)?.AvatarUrl;
            }
            if (avatar is null) return null;
            await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
            ChatMediaStream result = await _api.OpenAvatarAsync(avatar, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(guard)) { result.Dispose(); return null; }
            return result;
        }
        bool image = resource.StartsWith("preview-image?url=", StringComparison.Ordinal);
        bool media = resource.StartsWith("linked-media?url=", StringComparison.Ordinal);
        if (!image && !media) return null;
        string encoded = resource[(resource.IndexOf('=') + 1)..];
        string url;
        try { url = Uri.UnescapeDataString(encoded); }
        catch (UriFormatException) { return null; }
        bool allowed;
        lock (_sync)
        {
            EnsureCurrentUnsafe(guard);
            allowed = image
                ? AccessibleMessagesUnsafe().Any(message => message.LinkPreviews.Any(preview => !preview.IsRemoved && preview.ImageUrl == url)
                    || message.Card?.ImageUrl == url)
                : AccessibleMessagesUnsafe().Any(message => message.LinkPreviews.Any(preview => !preview.IsRemoved
                    && preview.EmbedUrl == url && preview.Kind is "video" or "audio"));
        }
        if (!allowed) return null;
        await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
        ChatMediaStream response = image ? await _api.OpenPreviewImageAsync(url, cancellationToken).ConfigureAwait(false)
            : await _api.OpenLinkedMediaAsync(url, range, cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(guard)) { response.Dispose(); return null; }
        return response;
    }

    private IEnumerable<ChatMessageDto> AccessibleMessagesUnsafe()
        => _current.Messages.Concat(_current.State.Threads.Select(thread => thread.LastMessage).OfType<ChatMessageDto>())
            .Concat(_current.State.Threads.SelectMany(thread => thread.PinnedMessages));

    private IEnumerable<ChatProfileDto> ProfilesUnsafe()
        => new[] { _current.State.Self }.Concat(_current.State.Contacts)
            .Concat(_current.State.Threads.SelectMany(thread => thread.Members).Select(member => member.Profile))
            .Concat(AccessibleMessagesUnsafe().Select(message => message.Sender));

    private static ChatMediaStream CreateLocalMedia(Stream source, ChatLocalUpload file, string? range, CancellationToken cancellationToken)
    {
        long start = 0, count = file.Size;
        string? contentRange = null;
        int status = 200;
        if (!string.IsNullOrEmpty(range))
        {
            if (!RangeHeaderValue.TryParse(range, out RangeHeaderValue? parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)
            { source.Dispose(); throw new ChatWorkspaceException("chat-invalid-range"); }
            RangeItemHeaderValue item = parsed.Ranges.Single();
            start = item.From ?? Math.Max(0, file.Size - (item.To ?? 0));
            long end = item.From is null ? file.Size - 1 : Math.Min(file.Size - 1, item.To ?? file.Size - 1);
            if (start < 0 || start >= file.Size || end < start)
            {
                source.Dispose();
                return new ChatMediaStream(Stream.Null, file.ContentType, 0, "bytes */" + file.Size.ToString(CultureInfo.InvariantCulture), 416, file.FileName);
            }
            count = end - start + 1;
            contentRange = $"bytes {start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}/{file.Size.ToString(CultureInfo.InvariantCulture)}";
            status = 206;
        }
        source.Position = start;
        ChatLimitedReadStream limited = new(source, count);
        CancellationTokenRegistration registration = cancellationToken.Register(static stream => ((Stream)stream!).Dispose(), limited);
        return new ChatMediaStream(limited, file.ContentType, count, contentRange, status, file.FileName, new RegistrationOwner(registration));
    }

    private sealed class RegistrationOwner(CancellationTokenRegistration registration) : IDisposable
    {
        public void Dispose() => registration.Dispose();
    }

    private sealed class MediaScope(IDisposable? inner, CancellationTokenSource cancellation) : IDisposable
    {
        public void Dispose() { inner?.Dispose(); cancellation.Dispose(); }
    }
}

internal sealed class ChatLimitedReadStream(Stream inner, long length) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, checked((int)Math.Min(count, _remaining)));
        _remaining -= read;
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer[..checked((int)Math.Min(buffer.Length, _remaining))], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
