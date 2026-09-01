using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WotLK.Launcher.Account;

internal interface IAvatarMediaClient
{
    Task<AvatarProfileReadResult> GetProfileAsync(CancellationToken cancellationToken);

    Task<AvatarDescriptor> UploadAvatarAsync(
        AvatarUploadRequest upload,
        IProgress<AvatarUploadTransferProgress>? progress,
        CancellationToken cancellationToken);

    Task DeleteAvatarAsync(CancellationToken cancellationToken);

    Task<AvatarMediaDownloadResult> DownloadAvatarAsync(
        AvatarDescriptor descriptor,
        int size,
        CancellationToken cancellationToken);
}

internal sealed class AvatarMediaClient : IAvatarMediaClient
{
    internal const long MaximumUploadBytes = 8L * 1024 * 1024;
    internal const long MaximumDownloadedVariantBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _apiBaseUri;
    private readonly Uri _mediaBaseUri;

    internal AvatarMediaClient(HttpClient httpClient, Uri apiBaseUri)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiBaseUri = EnsureTrailingSlash(apiBaseUri ?? throw new ArgumentNullException(nameof(apiBaseUri)));
        _mediaBaseUri = new Uri(_apiBaseUri, "../../");
    }

    public async Task<AvatarProfileReadResult> GetProfileAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_apiBaseUri, "me"));
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AvatarMediaException(
                AvatarMediaFailureCategory.BackendUnavailable,
                response.StatusCode);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        bool hasAvatarProperty = HasProperty(document.RootElement, "avatar");
        LauncherProfile profile = document.RootElement.Deserialize<LauncherProfile>(JsonOptions)
            ?? throw new AvatarMediaException(AvatarMediaFailureCategory.ProcessingFailed);
        return new AvatarProfileReadResult(profile, hasAvatarProperty);
    }

    public async Task<AvatarDescriptor> UploadAvatarAsync(
        AvatarUploadRequest upload,
        IProgress<AvatarUploadTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (upload.OriginalBytes.Length <= 0
            || upload.OriginalBytes.Length > MaximumUploadBytes)
        {
            throw new AvatarMediaException(AvatarMediaFailureCategory.AvatarTooLarge);
        }
        if (!upload.Crop.IsValid)
        {
            throw new AvatarMediaException(AvatarMediaFailureCategory.InvalidCrop);
        }

        progress?.Report(new AvatarUploadTransferProgress(
            AvatarUploadPhase.Preparing,
            0,
            upload.OriginalBytes.Length));
        using MultipartFormDataContent multipart = new("atlas-avatar-" + Guid.NewGuid().ToString("N"));
        ProgressMemoryContent image = new(upload.OriginalBytes, progress);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse(upload.ContentType);
        multipart.Add(image, "image", GetGenericFileName(upload.ContentType));
        multipart.Add(Number(upload.Crop.X), "cropX");
        multipart.Add(Number(upload.Crop.Y), "cropY");
        multipart.Add(Number(upload.Crop.Size), "cropSize");

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(_apiBaseUri, "me/avatar/photo"))
        {
            Content = multipart
        };
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using Stream responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<AvatarDescriptor>(
                responseStream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AvatarMediaException(AvatarMediaFailureCategory.ProcessingFailed);
    }

    public async Task DeleteAvatarAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Delete,
            new Uri(_apiBaseUri, "me/avatar/photo"));
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AvatarMediaDownloadResult> DownloadAvatarAsync(
        AvatarDescriptor descriptor,
        int size,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Uri uri = ResolveMediaUri(descriptor.GetUrl(size));
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return AvatarMediaDownloadResult.NotFound;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return AvatarMediaDownloadResult.Unauthorized;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            throw new AvatarMediaException(
                AvatarMediaFailureCategory.InvalidImage,
                response.StatusCode);
        }
        if (response.Content.Headers.ContentLength is long length
            && length > MaximumDownloadedVariantBytes)
        {
            throw new AvatarMediaException(
                AvatarMediaFailureCategory.InvalidImage,
                response.StatusCode);
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(
            stream,
            MaximumDownloadedVariantBytes,
            cancellationToken).ConfigureAwait(false);
        return AvatarMediaDownloadResult.Success(bytes);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new AvatarMediaException(
                AvatarMediaFailureCategory.Network,
                exception.StatusCode,
                innerException: exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        AvatarMediaFailureCategory category = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => AvatarMediaFailureCategory.Unauthorized,
            HttpStatusCode.NotFound => AvatarMediaFailureCategory.BackendUnavailable,
            HttpStatusCode.Conflict => AvatarMediaFailureCategory.UploadInProgress,
            HttpStatusCode.TooManyRequests => AvatarMediaFailureCategory.RateLimited,
            HttpStatusCode.RequestEntityTooLarge => AvatarMediaFailureCategory.AvatarTooLarge,
            _ => AvatarMediaFailureCategory.Unknown
        };
        try
        {
            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            AvatarApiError? error = await JsonSerializer.DeserializeAsync<AvatarApiError>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                category = MapServerCode(error.Code, category);
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // The stable status mapping remains authoritative for malformed legacy errors.
        }

        int? retryAfter = response.Headers.RetryAfter?.Delta is TimeSpan delta
            ? Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds))
            : int.TryParse(
                response.Headers.RetryAfter?.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int seconds)
                    ? Math.Max(1, seconds)
                    : null;
        throw new AvatarMediaException(category, response.StatusCode, retryAfter);
    }

    private Uri ResolveMediaUri(string value)
    {
        Uri resolved;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute))
        {
            if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new AvatarMediaException(AvatarMediaFailureCategory.InvalidImage);
            }
            resolved = absolute;
        }
        else
        {
            resolved = new Uri(_mediaBaseUri, value.TrimStart('/'));
        }

        bool sameOrigin = string.Equals(
                resolved.Scheme,
                _mediaBaseUri.Scheme,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                resolved.Host,
                _mediaBaseUri.Host,
                StringComparison.OrdinalIgnoreCase)
            && resolved.Port == _mediaBaseUri.Port
            && string.IsNullOrEmpty(resolved.UserInfo);
        if (!sameOrigin
            || !resolved.AbsolutePath.StartsWith(
                "/media/avatars/",
                StringComparison.Ordinal))
        {
            throw new AvatarMediaException(AvatarMediaFailureCategory.InvalidImage);
        }

        return resolved;
    }

    private static bool HasProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return element.EnumerateObject().Any(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static AvatarMediaFailureCategory MapServerCode(
        string code,
        AvatarMediaFailureCategory fallback)
    {
        return code switch
        {
            "AvatarTooLarge" => AvatarMediaFailureCategory.AvatarTooLarge,
            "InvalidImage" => AvatarMediaFailureCategory.InvalidImage,
            "UnsupportedFormat" => AvatarMediaFailureCategory.UnsupportedFormat,
            "InvalidDimensions" => AvatarMediaFailureCategory.InvalidDimensions,
            "InvalidCrop" => AvatarMediaFailureCategory.InvalidCrop,
            "UploadInProgress" => AvatarMediaFailureCategory.UploadInProgress,
            "RateLimited" => AvatarMediaFailureCategory.RateLimited,
            "ProcessingFailed" => AvatarMediaFailureCategory.ProcessingFailed,
            "StorageFailed" => AvatarMediaFailureCategory.StorageFailed,
            _ => fallback
        };
    }

    private static StringContent Number(double value) => new(
        value.ToString("R", CultureInfo.InvariantCulture),
        Encoding.UTF8,
        "text/plain");

    private static string GetGenericFileName(string contentType) => contentType switch
    {
        "image/jpeg" => "avatar.jpg",
        "image/png" => "avatar.png",
        "image/webp" => "avatar.webp",
        _ => "avatar.bin"
    };

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("The Atlas API URI must be absolute.", nameof(uri));
        }

        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            if (buffer.Length + read > maximumBytes)
            {
                throw new AvatarMediaException(AvatarMediaFailureCategory.InvalidImage);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record AvatarApiError(string Code);

    private sealed class ProgressMemoryContent : HttpContent
    {
        private readonly ReadOnlyMemory<byte> _bytes;
        private readonly IProgress<AvatarUploadTransferProgress>? _progress;

        internal ProgressMemoryContent(
            ReadOnlyMemory<byte> bytes,
            IProgress<AvatarUploadTransferProgress>? progress)
        {
            _bytes = bytes;
            _progress = progress;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            const int chunkSize = 64 * 1024;
            long sent = 0;
            _progress?.Report(new AvatarUploadTransferProgress(
                AvatarUploadPhase.Sending,
                sent,
                _bytes.Length));
            while (sent < _bytes.Length)
            {
                int count = (int)Math.Min(chunkSize, _bytes.Length - sent);
                await stream.WriteAsync(
                    _bytes.Slice((int)sent, count),
                    cancellationToken).ConfigureAwait(false);
                sent += count;
                _progress?.Report(new AvatarUploadTransferProgress(
                    sent == _bytes.Length
                        ? AvatarUploadPhase.Processing
                        : AvatarUploadPhase.Sending,
                    sent,
                    _bytes.Length));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }
}
