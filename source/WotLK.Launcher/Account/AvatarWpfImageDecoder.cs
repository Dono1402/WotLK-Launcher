using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WotLK.Launcher.Account;

internal sealed record AvatarPreviewImage(
    byte[] OriginalBytes,
    string ContentType,
    BitmapSource OrientedImage,
    int OrientedPixelWidth,
    int OrientedPixelHeight,
    ushort ExifOrientation);

internal static class AvatarWpfImageDecoder
{
    internal static AvatarPreviewImage DecodePreview(byte[] bytes, string contentType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ValidateSignature(bytes, contentType);
        BitmapFrame frame = DecodeFrame(bytes);
        ushort orientation = ReadExifOrientation(frame);
        BitmapSource oriented = ApplyExifOrientation(frame, orientation);
        return new AvatarPreviewImage(
            bytes,
            contentType,
            oriented,
            oriented.PixelWidth,
            oriented.PixelHeight,
            orientation);
    }

    internal static BitmapSource DecodePng(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ValidateSignature(bytes, "image/png");
        BitmapFrame frame = DecodeFrame(bytes);
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
        {
            throw new InvalidDataException("Invalid PNG dimensions.");
        }

        frame.Freeze();
        return frame;
    }

    internal static BitmapSource ApplyExifOrientation(BitmapSource source, ushort orientation)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (orientation is < 2 or > 8)
        {
            if (source.CanFreeze && !source.IsFrozen)
            {
                source.Freeze();
            }
            return source;
        }

        Transform transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new MatrixTransform(new Matrix(0, 1, 1, 0, 0, 0)),
            6 => new RotateTransform(90),
            7 => new MatrixTransform(new Matrix(0, -1, -1, 0, 0, 0)),
            8 => new RotateTransform(270),
            _ => Transform.Identity
        };
        TransformedBitmap transformed = new(source, transform);
        transformed.Freeze();
        return transformed;
    }

    private static BitmapFrame DecodeFrame(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidDataException("The image contains no frame.");
        frame.CopyPixels(
            new System.Windows.Int32Rect(0, 0, 1, 1),
            new byte[Math.Max(4, (frame.Format.BitsPerPixel + 7) / 8)],
            Math.Max(4, (frame.Format.BitsPerPixel + 7) / 8),
            0);
        return frame;
    }

    private static ushort ReadExifOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata)
        {
            return 1;
        }

        try
        {
            object? value = metadata.GetQuery("/app1/ifd/{ushort=274}");
            return value switch
            {
                ushort number when number is >= 1 and <= 8 => number,
                short number when number is >= 1 and <= 8 => (ushort)number,
                uint number when number is >= 1 and <= 8 => (ushort)number,
                _ => 1
            };
        }
        catch (NotSupportedException)
        {
            return 1;
        }
    }

    private static void ValidateSignature(ReadOnlySpan<byte> bytes, string contentType)
    {
        bool valid = contentType switch
        {
            "image/jpeg" => bytes.Length >= 3
                && bytes[0] == 0xFF
                && bytes[1] == 0xD8
                && bytes[2] == 0xFF,
            "image/png" => bytes.Length >= 8
                && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/webp" => bytes.Length >= 12
                && bytes[..4].SequenceEqual("RIFF"u8)
                && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException("The selected file does not match its image type.");
        }
    }
}
