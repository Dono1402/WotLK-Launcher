using SkiaSharp;

namespace WotLK.Launcher.Server.Avatars;

internal sealed class SkiaAvatarImageProcessor : IDisposable
{
    internal const int MinimumDimension = 256;
    internal const int MaximumDimension = 8192;
    internal const long MaximumPixelCount = 40_000_000;
    private readonly SemaphoreSlim _gate;
    private int _activeOperations;
    private int _peakObservedConcurrency;

    internal SkiaAvatarImageProcessor(int maximumConcurrency = 2)
    {
        if (maximumConcurrency is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        MaximumConcurrency = maximumConcurrency;
        _gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    internal int MaximumConcurrency { get; }
    internal int PeakObservedConcurrency => Volatile.Read(ref _peakObservedConcurrency);

    internal async Task<ProcessedAvatarImage> ProcessAsync(
        Stream source,
        string declaredContentType,
        NormalizedAvatarCrop crop,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _gate.WaitAsync(cancellationToken);
        int active = Interlocked.Increment(ref _activeOperations);
        ObservePeak(active);

        try
        {
            byte[] encoded = await ReadBoundedAsync(source, LocalAvatarStorage.MaximumOriginalBytes, cancellationToken);
            return Process(encoded, declaredContentType, crop, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
            _gate.Release();
        }
    }

    internal static void ValidateDimensions(int width, int height)
    {
        if (width < MinimumDimension || height < MinimumDimension)
            throw Invalid("dimensions_too_small", "L'image doit mesurer au moins 256 x 256 pixels.");
        if (width > MaximumDimension || height > MaximumDimension)
            throw Invalid("dimensions_too_large", "L'image depasse 8192 pixels sur un cote.");
        if (checked((long)width * height) > MaximumPixelCount)
            throw Invalid("pixel_count_too_large", "L'image depasse 40 megapixels.");
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private static ProcessedAvatarImage Process(
        byte[] encoded,
        string declaredContentType,
        NormalizedAvatarCrop crop,
        CancellationToken cancellationToken)
    {
        string normalizedContentType = NormalizeContentType(declaredContentType);
        using SKData data = SKData.CreateCopy(encoded);
        using SKCodec codec = SKCodec.Create(data)
            ?? throw Invalid("decode_failed", "Le fichier n'est pas une image prise en charge.");

        string decodedContentType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => throw Invalid("unsupported_format", "Seuls JPEG, PNG et WebP sont acceptes.")
        };
        if (!string.Equals(normalizedContentType, decodedContentType, StringComparison.Ordinal))
            throw Invalid("mime_mismatch", "Le type annonce ne correspond pas au contenu de l'image.");
        if (codec.FrameCount > 1)
            throw Invalid("animated_image", "Les images animees ne sont pas acceptees.");

        SKImageInfo sourceInfo = codec.Info;
        ValidateDimensions(sourceInfo.Width, sourceInfo.Height);
        cancellationToken.ThrowIfCancellationRequested();

        using SKBitmap decoded = new(new SKImageInfo(
            sourceInfo.Width,
            sourceInfo.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            SKColorSpace.CreateSrgb()));
        SKCodecResult decodeResult = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (decodeResult != SKCodecResult.Success)
            throw Invalid("decode_failed", "L'image est incomplete ou corrompue.");

        using OrientedBitmap oriented = Orient(decoded, codec.EncodedOrigin);
        PixelCrop pixelCrop = ResolveCrop(crop, oriented.Bitmap.Width, oriented.Bitmap.Height);
        using SKImage orientedImage = SKImage.FromBitmap(oriented.Bitmap);
        Dictionary<int, byte[]> variants = [];
        foreach (int size in AvatarVariantSizes.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            variants.Add(size, EncodeVariant(orientedImage, pixelCrop, size));
        }

        return new ProcessedAvatarImage(
            decodedContentType,
            sourceInfo.Width,
            sourceInfo.Height,
            oriented.Bitmap.Width,
            oriented.Bitmap.Height,
            variants);
    }

    private static OrientedBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return new OrientedBitmap(source, ownsBitmap: false);

        bool swapsAxes = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
        int width = swapsAxes ? source.Height : source.Width;
        int height = swapsAxes ? source.Width : source.Height;
        SKBitmap target = new(new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            SKColorSpace.CreateSrgb()));

        SKMatrix matrix = origin switch
        {
            SKEncodedOrigin.TopRight => Matrix(-1, 0, source.Width, 0, 1, 0),
            SKEncodedOrigin.BottomRight => Matrix(-1, 0, source.Width, 0, -1, source.Height),
            SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, source.Height),
            SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => Matrix(0, -1, source.Height, 1, 0, 0),
            SKEncodedOrigin.RightBottom => Matrix(0, -1, source.Height, -1, 0, source.Width),
            SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, source.Width),
            _ => SKMatrix.Identity
        };

        using SKCanvas canvas = new(target);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions());
        canvas.Flush();
        return new OrientedBitmap(target, ownsBitmap: true);
    }

    private static SKMatrix Matrix(
        float scaleX,
        float skewX,
        float translateX,
        float skewY,
        float scaleY,
        float translateY)
    {
        return new SKMatrix
        {
            ScaleX = scaleX,
            SkewX = skewX,
            TransX = translateX,
            SkewY = skewY,
            ScaleY = scaleY,
            TransY = translateY,
            Persp2 = 1
        };
    }

    private static PixelCrop ResolveCrop(NormalizedAvatarCrop crop, int width, int height)
    {
        double[] values = [crop.X, crop.Y, crop.Width, crop.Height];
        if (values.Any(value => !double.IsFinite(value))
            || crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0
            || crop.X + crop.Width > 1.000001 || crop.Y + crop.Height > 1.000001)
        {
            throw Invalid("invalid_crop", "Le recadrage normalise est invalide.");
        }

        int left = (int)Math.Round(crop.X * width, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(crop.Y * height, MidpointRounding.AwayFromZero);
        int right = (int)Math.Round((crop.X + crop.Width) * width, MidpointRounding.AwayFromZero);
        int bottom = (int)Math.Round((crop.Y + crop.Height) * height, MidpointRounding.AwayFromZero);
        int cropWidth = right - left;
        int cropHeight = bottom - top;
        if (Math.Abs(cropWidth - cropHeight) > 2)
            throw Invalid("crop_not_square", "Le recadrage doit etre carre.");

        int size = Math.Min(cropWidth, cropHeight);
        if (size < MinimumDimension)
            throw Invalid("crop_too_small", "Le recadrage doit mesurer au moins 256 x 256 pixels.");
        if (left < 0 || top < 0 || left + size > width || top + size > height)
            throw Invalid("crop_out_of_bounds", "Le recadrage sort de l'image.");
        return new PixelCrop(left, top, size);
    }

    private static byte[] EncodeVariant(SKImage source, PixelCrop crop, int size)
    {
        SKImageInfo targetInfo = new(
            size,
            size,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            SKColorSpace.CreateSrgb());
        using SKSurface surface = SKSurface.Create(targetInfo)
            ?? throw new InvalidOperationException("Impossible de creer la surface PNG avatar.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(
            source,
            new SKRect(crop.X, crop.Y, crop.X + crop.Size, crop.Y + crop.Size),
            new SKRect(0, 0, size, size),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Impossible d'encoder la variante PNG avatar.");
        return png.ToArray();
    }

    private static string NormalizeContentType(string contentType)
    {
        string normalized = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "image/jpg" => "image/jpeg",
            "image/jpeg" or "image/png" or "image/webp" => normalized,
            _ => throw Invalid("unsupported_mime", "Seuls JPEG, PNG et WebP sont acceptes.")
        };
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
            int read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > maximumBytes)
                throw Invalid("file_too_large", "L'image depasse 8 Mio.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private void ObservePeak(int active)
    {
        while (true)
        {
            int peak = Volatile.Read(ref _peakObservedConcurrency);
            if (active <= peak || Interlocked.CompareExchange(ref _peakObservedConcurrency, active, peak) == peak)
                return;
        }
    }

    private static AvatarImageValidationException Invalid(string code, string message)
        => new(code, message);

    private readonly record struct PixelCrop(int X, int Y, int Size);

    private sealed class OrientedBitmap : IDisposable
    {
        private readonly bool _ownsBitmap;

        internal OrientedBitmap(SKBitmap bitmap, bool ownsBitmap)
        {
            Bitmap = bitmap;
            _ownsBitmap = ownsBitmap;
        }

        internal SKBitmap Bitmap { get; }

        public void Dispose()
        {
            if (_ownsBitmap)
                Bitmap.Dispose();
        }
    }
}
