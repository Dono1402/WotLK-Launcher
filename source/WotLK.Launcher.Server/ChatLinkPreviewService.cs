using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

public sealed record ChatPreviewImage(byte[] Bytes, string ContentType);
public sealed record ChatLinkedMediaRead(Stream Stream, string ContentType, long? Length,
    string? ContentRange, int StatusCode);

/// <summary>Reads public metadata only. Remote HTML and credentials never reach the chat renderer.</summary>
public sealed class ChatLinkPreviewService : IDisposable
{
    private static readonly Regex Links = new(@"https?://[^\s<>""\u0000-\u001f]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(80));
    private static readonly Regex YoutubeId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);
    private static readonly Regex VimeoId = new("^[0-9]{1,15}$", RegexOptions.CultureInvariant);
    private readonly HttpClient _client;
    private readonly Func<Uri, CancellationToken, Task> _validate;
    private readonly ConcurrentDictionary<string, CachedPreview> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _requests = new(8, 8);

    public ChatLinkPreviewService() : this(CreateHandler(), ChatPublicUrlPolicy.ValidateAsync) { }

    internal ChatLinkPreviewService(HttpMessageHandler handler, Func<Uri, CancellationToken, Task> validate)
    {
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AtlasLauncher-LinkPreview/1.0 (+https://animeclub.fr)");
        _validate = validate;
    }

    public async Task<IReadOnlyList<ChatLinkPreviewDto>> BuildAsync(string body, CancellationToken ct)
    {
        IReadOnlyList<Uri> urls = ExtractUrls(body);
        if (urls.Count == 0) return [];
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        ChatLinkPreviewDto[] results = await Task.WhenAll(urls.Select(uri => BuildOneAsync(uri, budget.Token))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return results;
    }

    public async Task<ChatPreviewImage?> FetchImageAsync(string url, CancellationToken ct)
    {
        if (!ChatPublicUrlPolicy.TryNormalize(url, out Uri? uri)) return null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using HttpResponseMessage response = await SendPublicAsync(uri, null, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (contentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) return null;
            byte[] bytes = await ReadBoundedAsync(response, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            if (!ImageSignatureMatches(bytes, contentType)) return null;
            return new(bytes, contentType);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ChatOperationException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    public async Task<ChatLinkedMediaRead> OpenMediaAsync(string url, string? range, CancellationToken ct)
    {
        if (!ChatPublicUrlPolicy.TryNormalize(url, out Uri? uri)) throw new ChatOperationException("chat-invalid-link");
        if (!string.IsNullOrWhiteSpace(range) && (!RangeHeaderValue.TryParse(range, out RangeHeaderValue? parsed)
            || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)) throw new ChatOperationException("chat-invalid-range");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        HttpResponseMessage response = await SendPublicAsync(uri, range, timeout.Token).ConfigureAwait(false);
        try
        {
            string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)
                || ChatAttachmentFormats.MediaKindForContentType(type) is null)
                throw new ChatOperationException("chat-link-media-unavailable");
            long? total = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
            if (total > ChatLimits.MaximumAttachmentBytes) throw new ChatOperationException("chat-request-too-large");
            Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            Stream owned = new ResponseStream(stream, response, ChatLimits.MaximumAttachmentBytes);
            return new(owned, type, response.Content.Headers.ContentLength,
                response.Content.Headers.ContentRange?.ToString(), (int)response.StatusCode);
        }
        catch { response.Dispose(); throw; }
    }

    internal static IReadOnlyList<Uri> ExtractUrls(string? body)
    {
        if (string.IsNullOrEmpty(body)) return [];
        List<Uri> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Match match in Links.Matches(body.Length <= 10000 ? body : body[..10000]))
        {
            string candidate = match.Value.TrimEnd('.', ',', ';', '!', '?', ')', ']', '}');
            if (!ChatPublicUrlPolicy.TryNormalize(candidate, out Uri? uri) || !seen.Add(uri.AbsoluteUri)) continue;
            result.Add(uri);
            if (result.Count == ChatLimits.MaximumPreviewsPerMessage) break;
        }
        return result;
    }

    private async Task<ChatLinkPreviewDto> BuildOneAsync(Uri uri, CancellationToken ct)
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant()[..24];
        ChatLinkPreviewDto fallback = new() { Id = id, Url = uri.AbsoluteUri, Title = uri.IdnHost, Provider = uri.IdnHost };
        if (_cache.TryGetValue(uri.AbsoluteUri, out CachedPreview? cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Preview;
        if (TryVideoProvider(uri, fallback, out ChatLinkPreviewDto? video)) return video;
        bool entered = false;
        try
        {
            await _requests.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            using HttpResponseMessage response = await SendPublicAsync(uri, null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return fallback;
            string mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            string finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri;
            ChatLinkPreviewDto result;
            if (mime is "image/png" or "image/jpeg" or "image/gif" or "image/webp")
                result = fallback with { Kind = "image", ImageUrl = finalUrl, Title = FileTitle(uri) };
            else if (ChatAttachmentFormats.MediaKindForContentType(mime) == "video")
                result = fallback with { Kind = "video", EmbedUrl = finalUrl, CanRemove = false, Title = FileTitle(uri) };
            else if (ChatAttachmentFormats.MediaKindForContentType(mime) == "audio")
                result = fallback with { Kind = "audio", EmbedUrl = finalUrl, Title = FileTitle(uri) };
            else if (mime is "text/html" or "application/xhtml+xml")
            {
                byte[] bytes = await ReadBoundedAsync(response, 512 * 1024, ct).ConfigureAwait(false);
                result = ParseMetadata(uri, new Uri(finalUrl), bytes, fallback);
            }
            else result = fallback;
            if (_cache.Count > 2000)
                foreach (string key in _cache.OrderBy(pair => pair.Value.ExpiresAt).Take(200).Select(pair => pair.Key))
                    _cache.TryRemove(key, out _);
            _cache[uri.AbsoluteUri] = new(result, DateTimeOffset.UtcNow.AddMinutes(15));
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
            or ChatOperationException or ArgumentException or InvalidOperationException)
        { return fallback; }
        finally { if (entered) _requests.Release(); }
    }

    internal static bool TryVideoProvider(Uri uri, ChatLinkPreviewDto basis, [NotNullWhen(true)] out ChatLinkPreviewDto? result)
    {
        result = null;
        string host = uri.IdnHost.ToLowerInvariant();
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? videoId = null;
        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtube-nocookie.com" or "www.youtube-nocookie.com")
        {
            if (parts.Length >= 2 && parts[0] is "embed" or "shorts" or "live") videoId = parts[1];
            else if (parts.Length == 1 && parts[0] == "watch")
            {
                foreach (string field in uri.Query.TrimStart('?').Split('&'))
                {
                    string[] pair = field.Split('=', 2);
                    if (pair[0] == "v" && pair.Length == 2) { videoId = Uri.UnescapeDataString(pair[1]); break; }
                }
            }
        }
        else if (host is "youtu.be" or "www.youtu.be" && parts.Length >= 1) videoId = parts[0];
        if (videoId is not null && YoutubeId.IsMatch(videoId))
        {
            result = basis with
            {
                Kind = "video", Provider = "YouTube", Title = "YouTube",
                EmbedUrl = "https://www.youtube.com/embed/" + videoId + "?playsinline=1&rel=0&origin=https%3A%2F%2Fanimeclub.fr",
                ImageUrl = "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg", CanRemove = false
            };
            return true;
        }
        if (host is "vimeo.com" or "www.vimeo.com" or "player.vimeo.com"
            && parts.LastOrDefault() is string vimeo && VimeoId.IsMatch(vimeo))
        {
            result = basis with { Kind = "video", Provider = "Vimeo", Title = "Vimeo",
                EmbedUrl = "https://player.vimeo.com/video/" + vimeo, CanRemove = false };
            return true;
        }
        return false;
    }

    internal static ChatLinkPreviewDto ParseMetadata(Uri original, Uri resolved, byte[] bytes, ChatLinkPreviewDto basis)
    {
        // HtmlParser has no browsing loader, scripting engine or subresource fetcher.
        using IDocument document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(Encoding.UTF8.GetString(bytes));
        string Meta(params string[] names)
        {
            foreach (string name in names)
            {
                IElement? element = document.QuerySelectorAll("meta").FirstOrDefault(meta =>
                    string.Equals(meta.GetAttribute("property"), name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(meta.GetAttribute("name"), name, StringComparison.OrdinalIgnoreCase));
                if (element?.GetAttribute("content") is string value && !string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }
        string? image = ResolvePublicSyntax(resolved, Meta("og:image:secure_url", "og:image", "twitter:image"));
        string title = Meta("og:title", "twitter:title");
        if (string.IsNullOrWhiteSpace(title)) title = document.Title ?? "";
        string site = Meta("og:site_name");
        return basis with
        {
            Title = Clean(title, 200, original.IdnHost),
            Description = Clean(Meta("og:description", "description", "twitter:description"), 500, ""),
            Provider = Clean(site, 80, original.IdnHost), ImageUrl = image
        };
    }

    private async Task<HttpResponseMessage> SendPublicAsync(Uri uri, string? range, CancellationToken ct)
    {
        for (int redirect = 0; redirect <= 3; ++redirect)
        {
            await _validate(uri, ct).ConfigureAwait(false);
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,image/*,video/*,audio/*;q=0.8,*/*;q=0.1");
            if (!string.IsNullOrWhiteSpace(range)) request.Headers.Range = RangeHeaderValue.Parse(range);
            HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? target = response.Headers.Location;
                response.Dispose();
                if (target is null || redirect == 3 || !ChatPublicUrlPolicy.TryNormalize(new Uri(uri, target).AbsoluteUri, out Uri? next))
                    throw new ChatOperationException("chat-invalid-link");
                uri = next;
                continue;
            }
            return response;
        }
        throw new ChatOperationException("chat-invalid-link");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new IOException("Preview exceeds its size limit.");
        await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[32768];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (count > maximum - output.Length) throw new IOException("Preview exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, ct) =>
        {
            IPAddress[] addresses = await ChatPublicUrlPolicy.ResolveAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            Exception? last = null;
            foreach (IPAddress address in addresses)
            {
                Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, true);
                }
                catch (Exception error) { socket.Dispose(); last = error; }
            }
            throw new HttpRequestException("Public preview connection failed.", last);
        }
    };

    private static string? ResolvePublicSyntax(Uri basis, string value) =>
        Uri.TryCreate(basis, value, out Uri? candidate) && ChatPublicUrlPolicy.TryNormalize(candidate.AbsoluteUri, out Uri? normalized)
            ? normalized.AbsoluteUri : null;
    private static string FileTitle(Uri uri) => Clean(Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? ""), 150, uri.IdnHost);
    private static string Clean(string? value, int limit, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string text = Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(40)).Trim();
        text = new string(text.Where(character => !char.IsControl(character) && character is not ('\u202e' or '\u202d')).ToArray());
        return text.Length > limit ? text[..limit] : text;
    }

    private static bool ImageSignatureMatches(byte[] bytes, string mime) => mime switch
    {
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
        "image/gif" => bytes.AsSpan().StartsWith("GIF87a"u8) || bytes.AsSpan().StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan().StartsWith("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    public void Dispose() { _client.Dispose(); _requests.Dispose(); }
    private sealed record CachedPreview(ChatLinkPreviewDto Preview, DateTimeOffset ExpiresAt);

    private sealed class ResponseStream(Stream inner, HttpResponseMessage owner, long maximum) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { int read = inner.Read(buffer, offset, count); Check(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { int read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false); Check(read); return read; }
        private void Check(int amount) { _read += amount; if (_read > maximum) throw new IOException("Media exceeds its size limit."); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); owner.Dispose(); GC.SuppressFinalize(this); }
    }
}

internal static class ChatPublicUrlPolicy
{
    internal static bool TryNormalize(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https") || parsed.UserInfo.Length > 0
            || parsed.Port is not (80 or 443) || parsed.IdnHost.Length == 0) return false;
        string host = parsed.IdnHost.ToLowerInvariant().TrimEnd('.');
        if (host == "localhost" || !host.Contains('.') && !IPAddress.TryParse(host, out _)
            || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal) || host.EndsWith(".home.arpa", StringComparison.Ordinal)) return false;
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && !IsPublicAddress(address)) return false;
        uri = parsed;
        return true;
    }

    internal static async Task ValidateAsync(Uri uri, CancellationToken ct)
    {
        if (!TryNormalize(uri.AbsoluteUri, out _)) throw new ChatOperationException("chat-invalid-link");
        await ResolveAsync(uri.DnsSafeHost, ct).ConfigureAwait(false);
    }

    internal static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
            ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new ChatOperationException("chat-invalid-link");
        return addresses;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2)
                || bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)
                || bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0) return false;
        // Global unicast only; exclude transition and documentation prefixes.
        return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }
}
