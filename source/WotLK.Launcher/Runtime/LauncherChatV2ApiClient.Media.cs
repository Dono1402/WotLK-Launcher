using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherChatV2ApiClient
{
    public Task<ChatMediaStream> OpenAttachmentAsync(string attachmentId, string? range, CancellationToken cancellationToken)
        => OpenMediaAsync("attachments/" + Identifier(attachmentId), range, cancellationToken);

    public Task<ChatMediaStream> OpenPreviewImageAsync(string url, CancellationToken cancellationToken)
    {
        if (url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new ArgumentException("Invalid preview image URL.");
        return OpenMediaAsync("preview-image?url=" + Uri.EscapeDataString(url), null, cancellationToken);
    }

    public Task<ChatMediaStream> OpenLinkedMediaAsync(string url, string? range, CancellationToken cancellationToken)
    {
        if (url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new ArgumentException("Invalid linked media URL.");
        return OpenMediaAsync("linked-media?url=" + Uri.EscapeDataString(url), range, cancellationToken);
    }

    public Task<ChatMediaStream> OpenAvatarAsync(string authorizedUrl, CancellationToken cancellationToken)
    {
        Uri applicationRoot = new(_baseUri, "../../../");
        Uri avatarRoot = new(applicationRoot, "media/avatars/");
        string value = authorizedUrl.Trim();
        // Avatar descriptors are application-root relative, while a public API
        // can be mounted under /wotlk/. Match the account avatar client's rule.
        string candidate = value.StartsWith("/media/avatars/", StringComparison.Ordinal) ? value.TrimStart('/') : value;
        if (!Uri.TryCreate(applicationRoot, candidate, out Uri? uri) || uri.Scheme != _baseUri.Scheme
            || uri.IdnHost != _baseUri.IdnHost || uri.Port != _baseUri.Port || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.StartsWith(avatarRoot.AbsolutePath, StringComparison.Ordinal))
            throw new ArgumentException("Invalid authorized avatar resource.");
        return OpenMediaAsync(uri.AbsoluteUri, null, cancellationToken);
    }

    private async Task<ChatMediaStream> OpenMediaAsync(string resource, string? range, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_baseUri, resource));
        if (!string.IsNullOrEmpty(range))
        {
            if (range.Length > 100 || !RangeHeaderValue.TryParse(range, out RangeHeaderValue? parsed)
                || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)
                throw new ArgumentException("Invalid Messages media range.");
            request.Headers.Range = parsed;
        }
        HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).ConfigureAwait(false);
        try
        {
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            {
                _ = await ReadResponseAsync<object>(response, deadline.Token).ConfigureAwait(false);
                throw InvalidResponse();
            }
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (contentType.Length > 128 || response.Content.Headers.ContentLength > WotLK.Launcher.Chat.ChatLimits.MaximumAttachmentBytes)
                throw InvalidResponse();
            Stream content = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            CancellationTokenRegistration registration = cancellationToken.Register(static owner => ((HttpResponseMessage)owner!).Dispose(), response);
            string? fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName;
            return new ChatMediaStream(content, contentType, response.Content.Headers.ContentLength,
                response.Content.Headers.ContentRange?.ToString(), (int)response.StatusCode,
                fileName is null ? null : Path.GetFileName(fileName.Trim('"')),
                new MediaResponseOwner(response, registration));
        }
        catch { response.Dispose(); throw; }
    }

    private sealed class MediaResponseOwner(HttpResponseMessage response, CancellationTokenRegistration registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Dispose();
            response.Dispose();
        }
    }
}
