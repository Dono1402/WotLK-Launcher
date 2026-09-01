using System.Diagnostics;
using SkiaSharp;

namespace WotLK.Launcher.Server.Avatars;

internal static class AvatarImageSpikeRunner
{
    internal static async Task<int> RunAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        try
        {
            using SkiaAvatarImageProcessor processor = new(maximumConcurrency: 2);
            NormalizedAvatarCrop landscapeCrop = new(0.125, 0, 0.75, 1);
            await VerifyFormatAsync(processor, SKEncodedImageFormat.Jpeg, "image/jpeg", landscapeCrop, output, cancellationToken);
            await VerifyFormatAsync(processor, SKEncodedImageFormat.Png, "image/png", landscapeCrop, output, cancellationToken);
            await VerifyFormatAsync(processor, SKEncodedImageFormat.Webp, "image/webp", landscapeCrop, output, cancellationToken);

            byte[] portraitJpeg = CreateImage(SKEncodedImageFormat.Jpeg, 320, 480, quality: 92);
            byte[] orientedJpeg = AddExifOrientation(portraitJpeg, orientation: 6);
            ProcessedAvatarImage oriented = await processor.ProcessAsync(
                new MemoryStream(orientedJpeg, writable: false),
                "image/jpeg",
                new NormalizedAvatarCrop(1d / 6d, 0, 2d / 3d, 1),
                cancellationToken);
            Require(oriented.OrientedWidth == 480 && oriented.OrientedHeight == 320, "L'orientation EXIF 6 n'a pas ete appliquee.");
            await output.WriteLineAsync("EXIF_ORIENTATION=OK");

            await ExpectValidationAsync(
                processor,
                [0x13, 0x37, 0x42, 0x00],
                "image/png",
                new NormalizedAvatarCrop(0, 0, 1, 1),
                "decode_failed",
                cancellationToken);
            await output.WriteLineAsync("CORRUPTED_IMAGE=REJECTED");

            byte[] oversized = CreateImage(SKEncodedImageFormat.Png, 9000, 256, quality: 100);
            await ExpectValidationAsync(
                processor,
                oversized,
                "image/png",
                new NormalizedAvatarCrop(0, 0, 1, 1),
                "dimensions_too_large",
                cancellationToken);
            SkiaAvatarImageProcessor.ValidateDimensions(8000, 5001);
            throw new InvalidOperationException("Le plafond de 40 megapixels n'a pas ete applique.");
        }
        catch (AvatarImageValidationException exception) when (exception.Code == "pixel_count_too_large")
        {
            await output.WriteLineAsync("EXCESSIVE_DIMENSIONS=REJECTED");
        }

        try
        {
            using SkiaAvatarImageProcessor concurrencyProcessor = new(maximumConcurrency: 2);
            byte[] workload = CreateImage(SKEncodedImageFormat.Jpeg, 2048, 2048, quality: 90);
            Task<ProcessedAvatarImage>[] tasks = Enumerable.Range(0, 6)
                .Select(_ => Task.Run(
                    () => concurrencyProcessor.ProcessAsync(
                        new MemoryStream(workload, writable: false),
                        "image/jpeg",
                        new NormalizedAvatarCrop(0, 0, 1, 1),
                        cancellationToken),
                    cancellationToken))
                .ToArray();
            await Task.WhenAll(tasks);
            Require(concurrencyProcessor.PeakObservedConcurrency == 2, "La limite de concurrence Skia n'a pas ete observee.");
            long peakMiB = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
            await output.WriteLineAsync($"CONCURRENCY=OK peak={concurrencyProcessor.PeakObservedConcurrency} limit={concurrencyProcessor.MaximumConcurrency}");
            await output.WriteLineAsync($"PEAK_WORKING_SET_MIB={peakMiB}");
            await output.WriteLineAsync("SKIASHARP_SPIKE=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync($"SKIASHARP_SPIKE=FAIL type={exception.GetType().Name} message={exception.Message}");
            return 1;
        }
    }

    private static async Task VerifyFormatAsync(
        SkiaAvatarImageProcessor processor,
        SKEncodedImageFormat format,
        string contentType,
        NormalizedAvatarCrop crop,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        byte[] encoded = CreateImage(format, 640, 480, quality: 92);
        ProcessedAvatarImage processed = await processor.ProcessAsync(
            new MemoryStream(encoded, writable: false),
            contentType,
            crop,
            cancellationToken);
        Require(processed.SourceContentType == contentType, $"Le type {contentType} n'a pas ete reconnu.");
        Require(processed.Variants.Keys.Order().SequenceEqual(AvatarVariantSizes.All), "Les variantes MVP sont incompletes.");
        foreach ((int size, byte[] png) in processed.Variants)
        {
            using SKBitmap bitmap = SKBitmap.Decode(png)
                ?? throw new InvalidOperationException($"La variante {size}px ne se decode pas.");
            Require(bitmap.Width == size && bitmap.Height == size, $"La variante {size}px a de mauvaises dimensions.");
        }
        await output.WriteLineAsync($"{format.ToString().ToUpperInvariant()}=OK");
    }

    private static async Task ExpectValidationAsync(
        SkiaAvatarImageProcessor processor,
        byte[] encoded,
        string contentType,
        NormalizedAvatarCrop crop,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await processor.ProcessAsync(
                new MemoryStream(encoded, writable: false),
                contentType,
                crop,
                cancellationToken);
        }
        catch (AvatarImageValidationException exception) when (exception.Code == expectedCode)
        {
            return;
        }
        throw new InvalidOperationException($"L'image devait etre refusee avec {expectedCode}.");
    }

    private static byte[] CreateImage(SKEncodedImageFormat format, int width, int height, int quality)
    {
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using SKSurface surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Impossible de creer l'image du spike.");
        surface.Canvas.Clear(new SKColor(18, 27, 41));
        using SKPaint paint = new() { IsAntialias = false };
        paint.Color = new SKColor(219, 177, 82);
        surface.Canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        paint.Color = new SKColor(81, 204, 227);
        surface.Canvas.DrawRect(width / 2f, 0, width / 2f, height / 2f, paint);
        paint.Color = new SKColor(90, 211, 156);
        surface.Canvas.DrawRect(0, height / 2f, width / 2f, height / 2f, paint);
        paint.Color = new SKColor(196, 93, 107);
        surface.Canvas.DrawRect(width / 2f, height / 2f, width / 2f, height / 2f, paint);
        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(format, quality)
            ?? throw new InvalidOperationException($"Impossible d'encoder le fixture {format}.");
        return data.ToArray();
    }

    private static byte[] AddExifOrientation(byte[] jpeg, ushort orientation)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new ArgumentException("Le fixture EXIF doit etre un JPEG.", nameof(jpeg));

        byte[] payload =
        [
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
            0, 1,
            0x01, 0x12, 0, 3, 0, 0, 0, 1,
            (byte)(orientation >> 8), (byte)orientation, 0, 0,
            0, 0, 0, 0
        ];
        int segmentLength = payload.Length + 2;
        byte[] result = new byte[jpeg.Length + payload.Length + 4];
        result[0] = 0xff;
        result[1] = 0xd8;
        result[2] = 0xff;
        result[3] = 0xe1;
        result[4] = (byte)(segmentLength >> 8);
        result[5] = (byte)segmentLength;
        Buffer.BlockCopy(payload, 0, result, 6, payload.Length);
        Buffer.BlockCopy(jpeg, 2, result, 6 + payload.Length, jpeg.Length - 2);
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
