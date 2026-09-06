using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WotLK.Launcher.Account;

internal sealed record ArmoryBannerData(byte[]? PngBytes, double PositionX, double PositionY, double Zoom = 1, string Fit = "contain")
{
    internal string? DataUrl => PngBytes is null ? null : "data:image/png;base64," + Convert.ToBase64String(PngBytes);
}

// A single atomic document keeps the local image and its focal point together.
// The account ID comes exclusively from the authenticated native session.
internal sealed class ArmoryBannerStore
{
    internal const int MaximumImageBytes = 3_000_000;
    private const int MaximumDimension = 1600;
    private readonly string _root;

    internal ArmoryBannerStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LauncherBuildFlavor.IsLocalClient ? "Atlas Launcher Local" : "Atlas Launcher",
            "profile-banners"));
    }

    internal async Task<ArmoryBannerData?> LoadAsync(uint accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(accountId);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumImageBytes * 4L / 3 + 4096)
            throw new InvalidDataException("Banner document is too large.");
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes);
        ArmoryBannerData banner = document.RootElement.Deserialize<ArmoryBannerData>()
            ?? throw new InvalidDataException("Invalid banner document.");
        // Banners saved before zoom was introduced retain their image and focal point.
        if (!document.RootElement.TryGetProperty(nameof(ArmoryBannerData.Zoom), out _))
            banner = banner with { Zoom = 1 };
        if (!document.RootElement.TryGetProperty(nameof(ArmoryBannerData.Fit), out _))
            banner = banner with { Fit = "contain" };
        Validate(banner);
        cancellationToken.ThrowIfCancellationRequested();
        return banner;
    }

    internal async Task SaveAsync(uint accountId, ArmoryBannerData banner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(banner);
        string path = GetPath(accountId);
        Directory.CreateDirectory(_root);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(banner), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal Task ResetAsync(uint accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(GetPath(accountId));
        return Task.CompletedTask;
    }

    internal static byte[] Normalize(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = image.PixelWidth, height = image.PixelHeight;
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 40_000_000)
            throw new InvalidDataException("Banner dimensions are not supported.");
        int stride = checked((width * image.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[checked(stride * height)];
        image.CopyPixels(pixels, stride, 0);
        BitmapSource detached = BitmapSource.Create(width, height, 96, 96, image.Format, image.Palette, pixels, stride);
        double scale = Math.Min(1, (double)MaximumDimension / Math.Max(width, height));
        for (int attempt = 0; attempt < 10; attempt++, scale *= 0.75)
        {
            BitmapSource sized = scale < 1 ? new TransformedBitmap(detached, new ScaleTransform(scale, scale)) : detached;
            using MemoryStream stream = new();
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(sized));
            encoder.Save(stream);
            if (stream.Length <= MaximumImageBytes) return stream.ToArray();
        }
        throw new InvalidDataException("The normalized banner is too large.");
    }

    private string GetPath(uint accountId)
    {
        if (accountId == 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        return Path.Combine(_root, accountId.ToString(CultureInfo.InvariantCulture) + ".json");
    }

    private static void Validate(ArmoryBannerData banner)
    {
        ArgumentNullException.ThrowIfNull(banner);
        if (!double.IsFinite(banner.PositionX) || !double.IsFinite(banner.PositionY)
            || banner.PositionX is < 0 or > 1 || banner.PositionY is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(banner), "Banner focal points must be between zero and one.");
        if (!double.IsFinite(banner.Zoom) || banner.Zoom is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(banner), "Banner zoom must be between one and three.");
        if (banner.Fit is not "contain" and not "cover")
            throw new ArgumentOutOfRangeException(nameof(banner), "Banner display must be contain or cover.");
        byte[]? bytes = banner.PngBytes;
        if (bytes is null) return;
        if (bytes.Length < 24 || bytes.Length > MaximumImageBytes
            || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("A banner must be a normalized PNG.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension)
            throw new InvalidDataException("Invalid banner dimensions.");
        _ = AvatarWpfImageDecoder.DecodePng(bytes);
    }
}
