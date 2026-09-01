using System.Net;

namespace WotLK.Launcher.Account;

internal sealed record AvatarDescriptor(
    Guid AvatarId,
    ulong Version,
    string Url32,
    string Url64,
    string Url128,
    string Url256)
{
    internal string GetUrl(int size) => size switch
    {
        32 => Url32,
        64 => Url64,
        128 => Url128,
        256 => Url256,
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };
}

internal readonly record struct AvatarNormalizedCrop(
    double X,
    double Y,
    double Size)
{
    internal bool IsValid => double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Size)
        && X >= 0
        && Y >= 0
        && Size > 0
        && Size <= 1;
}

internal sealed record AvatarUploadRequest(
    ReadOnlyMemory<byte> OriginalBytes,
    string ContentType,
    AvatarNormalizedCrop Crop);

internal enum AvatarUploadPhase
{
    Preparing,
    Sending,
    Processing
}

internal sealed record AvatarUploadTransferProgress(
    AvatarUploadPhase Phase,
    long BytesSent,
    long TotalBytes)
{
    internal int? Percentage => TotalBytes > 0
        ? (int)Math.Clamp(Math.Round(BytesSent * 100d / TotalBytes), 0, 100)
        : null;
}

internal sealed record AvatarProfileReadResult(
    LauncherProfile Profile,
    bool SupportsProfilePhotos);

internal enum AvatarMediaDownloadStatus
{
    Success,
    NotFound,
    Unauthorized
}

internal sealed record AvatarMediaDownloadResult(
    AvatarMediaDownloadStatus Status,
    byte[]? Bytes)
{
    internal static AvatarMediaDownloadResult Success(byte[] bytes) =>
        new(AvatarMediaDownloadStatus.Success, bytes);

    internal static AvatarMediaDownloadResult NotFound { get; } =
        new(AvatarMediaDownloadStatus.NotFound, null);

    internal static AvatarMediaDownloadResult Unauthorized { get; } =
        new(AvatarMediaDownloadStatus.Unauthorized, null);
}

internal enum AvatarMediaFailureCategory
{
    Unknown,
    AvatarTooLarge,
    InvalidImage,
    UnsupportedFormat,
    InvalidDimensions,
    InvalidCrop,
    UploadInProgress,
    RateLimited,
    ProcessingFailed,
    StorageFailed,
    Unauthorized,
    BackendUnavailable,
    Network,
    Cancelled
}

internal sealed class AvatarMediaException : Exception
{
    internal AvatarMediaException(
        AvatarMediaFailureCategory category,
        HttpStatusCode? statusCode = null,
        int? retryAfterSeconds = null,
        Exception? innerException = null)
        : base($"Avatar media request failed: {category}.", innerException)
    {
        Category = category;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    internal AvatarMediaFailureCategory Category { get; }

    internal HttpStatusCode? StatusCode { get; }

    internal int? RetryAfterSeconds { get; }
}
