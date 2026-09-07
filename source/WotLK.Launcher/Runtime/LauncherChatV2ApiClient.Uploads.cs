using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherChatV2ApiClient
{
    public async Task<ChatUploadDto> StartUploadAsync(ChatUploadRequest request, CancellationToken cancellationToken)
    {
        if (request.Size is <= 0 or > ChatLimits.MaximumAttachmentBytes || string.IsNullOrWhiteSpace(request.FileName)
            || request.FileName.Length > 255 || Path.GetFileName(request.FileName) != request.FileName
            || string.IsNullOrWhiteSpace(request.ContentType) || request.ContentType.Length > 128)
            throw new ArgumentException("Invalid Messages attachment.");
        ChatUploadDto upload = await ReadAsync<ChatUploadDto>(HttpMethod.Post, "uploads", request, cancellationToken).ConfigureAwait(false);
        ValidateUpload(upload);
        if (upload.Size != request.Size) throw InvalidResponse();
        return upload;
    }

    public async Task<ChatUploadDto> GetUploadAsync(string uploadId, CancellationToken cancellationToken)
    {
        ChatUploadDto upload = await ReadAsync<ChatUploadDto>(HttpMethod.Get, "uploads/" + Identifier(uploadId), null,
            cancellationToken).ConfigureAwait(false);
        ValidateUpload(upload, uploadId);
        return upload;
    }

    public async Task<ChatUploadDto> UploadChunkAsync(string uploadId, long offset, Stream source, int length,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || offset < 0 || length is <= 0 or > ChatLimits.UploadChunkBytes
            || offset > ChatLimits.MaximumAttachmentBytes - length)
            throw new ArgumentException("Invalid Messages upload range.");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using HttpRequestMessage request = new(HttpMethod.Put,
            new Uri(_baseUri, "uploads/" + Identifier(uploadId) + "?offset=" + Number(offset)));
        request.Content = new ChatUploadChunkContent(source, length, progress);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).ConfigureAwait(false);
        ChatUploadDto upload = await ReadResponseAsync<ChatUploadDto>(response, deadline.Token).ConfigureAwait(false);
        ValidateUpload(upload, uploadId);
        if (upload.Offset != offset + length) throw InvalidResponse();
        return upload;
    }

    public async Task<ChatUploadDto> CompleteUploadAsync(string uploadId, CancellationToken cancellationToken)
    {
        ChatUploadDto upload = await ReadAsync<ChatUploadDto>(HttpMethod.Post, "uploads/" + Identifier(uploadId) + "/complete",
            null, cancellationToken, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        ValidateUpload(upload, uploadId);
        if (!upload.IsComplete || upload.Attachment is null) throw InvalidResponse();
        return upload;
    }

    public async Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken)
    {
        _ = await ReadAsync<JsonElement>(HttpMethod.Delete, "uploads/" + Identifier(uploadId), null,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateUpload(ChatUploadDto? upload, string? expectedId = null)
    {
        if (upload is null || string.IsNullOrWhiteSpace(upload.Id) || upload.Id.Length > 128
            || expectedId is not null && upload.Id != expectedId || upload.Size is <= 0 or > ChatLimits.MaximumAttachmentBytes
            || upload.Offset < 0 || upload.Offset > upload.Size || upload.FileName is null || upload.FileName.Length > 255
            || upload.IsComplete && (upload.Offset != upload.Size || upload.Attachment is null))
            throw InvalidResponse();
    }
}

/// <summary>A single bounded chunk; the caller retains ownership of the input stream.</summary>
internal sealed class ChatUploadChunkContent(Stream source, int length, IProgress<long>? progress = null) : HttpContent
{
    protected override bool TryComputeLength(out long computedLength)
    {
        computedLength = length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
        => CopyAsync(target, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken cancellationToken)
        => CopyAsync(target, cancellationToken);

    private async Task CopyAsync(Stream target, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Math.Min(65536, length)];
        int sent = 0;
        while (sent < length)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length - sent)), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The selected attachment changed or ended unexpectedly.");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            sent += count;
            progress?.Report(sent);
        }
    }
}
