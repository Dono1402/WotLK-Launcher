using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WotLK.Launcher.Account;
using WotLK.Launcher.Server.Avatars;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static class AccountAvatarCropRenderingTests
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? sourcePath = ReadOption(args, "--source-image");
        string? captureDirectory = ReadOption(args, "--capture-directory");
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                Run(sourcePath, captureDirectory);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true, Name = "AtlasAvatarCropRendering" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        Console.WriteLine("Avatar crop rendering OK: source DPI, display DPI, initial crop, wheel, reset, server output. No window or network server opened.");
        return 0;
    }

    private static void Run(string? sourcePath, string? captureDirectory)
    {
        Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[]
                 {
                     "UI/V2/Resources/AtlasV2.Tokens.xaml",
                     "Assets/Icons/AtlasV2.Icons.xaml",
                     "UI/V2/Resources/AtlasV2.Controls.xaml"
                 })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/WotLK.Launcher;component/{resource}", UriKind.Relative)
            });
        }

        try
        {
            BitmapSource pattern = CreatePattern();
            foreach ((double dpiX, double dpiY) in new[]
                     {
                         (25.4, 25.4), (96d, 96d), (192d, 192d), (72d, 144d)
                     })
            {
                AvatarPreviewImage preview = EncodeAndDecode(WithDpi(pattern, dpiX, dpiY));
                Validate(preview, $"pattern-{dpiX}-{dpiY}", captureDirectory: null);
            }

            BitmapSource rotated = AvatarWpfImageDecoder.ApplyExifOrientation(
                WithDpi(pattern, 72, 144), 6);
            Validate(EncodeAndDecode(rotated), "rotated-asymmetric-dpi", captureDirectory: null);

            if (sourcePath is not null)
            {
                byte[] bytes = File.ReadAllBytes(sourcePath);
                byte[] originalHash = SHA256.HashData(bytes);
                AvatarPreviewImage source = AvatarWpfImageDecoder.DecodePreview(bytes, "image/png");
                Console.WriteLine($"Source: {source.OrientedPixelWidth}x{source.OrientedPixelHeight}; embedded DPI={source.OrientedImage.DpiX:F2}x{source.OrientedImage.DpiY:F2}.");
                Validate(source, "source-original", captureDirectory);
                foreach (double dpi in new[] { 96d, 192d })
                {
                    Validate(EncodeAndDecode(WithDpi(source.OrientedImage, dpi, dpi)),
                        $"source-{dpi:F0}dpi", captureDirectory);
                }
                AccountAvatarClientTests.True(
                    originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(sourcePath))),
                    "Le fichier source doit rester inchangé après la fixture.");
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void Validate(AvatarPreviewImage preview, string name, string? captureDirectory)
    {
        AvatarCropUiState state = new(AvatarCropUiState.Empty.Current);
        AvatarCropOverlayV2 overlay = new() { State = state, Width = 1100, Height = 800 };
        state.OpenReal(preview);
        overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Layout(overlay);

        AccountAvatarClientTests.Equal(1d, state.Current.Zoom, "L'ouverture doit afficher le cadrage maximal.");
        AccountAvatarClientTests.Equal(
            Math.Min(preview.OrientedPixelWidth, preview.OrientedPixelHeight),
            state.Current.Layout.PixelCrop.Size,
            "L'ouverture doit couvrir intégralement le petit côté de l'image.");
        ValidateRenderedCrop(overlay, state, preview, name + "-initial");
        Capture(overlay, captureDirectory, name + "-initial.png");

        Grid viewport = Required<Grid>(overlay, "CropViewport");
        AccountAvatarClientTests.Equal(BitmapScalingMode.HighQuality,
            RenderOptions.GetBitmapScalingMode(overlay),
            "Le contrôle doit conserver le filtrage haute qualité pour les réductions importantes.");
        MouseWheelEventArgs zoomIn = Wheel(viewport, 480);
        AccountAvatarClientTests.True(zoomIn.Handled && state.Current.Zoom > 1,
            "La molette doit zoomer dans le contrôle réel.");
        state.SetTransform(state.Current.Zoom, 34, -22);
        Layout(overlay);
        ValidateRenderedCrop(overlay, state, preview, name + "-zoom-pan");

        MouseWheelEventArgs zoomOut = Wheel(viewport, -4800);
        AccountAvatarClientTests.True(zoomOut.Handled && state.Current.Zoom == 1,
            "Dézoomer à la molette doit retrouver le cadrage maximal sans passer sous le minimum.");
        AccountAvatarClientTests.Equal(
            Math.Min(preview.OrientedPixelWidth, preview.OrientedPixelHeight),
            state.Current.Layout.PixelCrop.Size,
            "Le dézoom doit retrouver la totalité du petit côté.");

        state.SetTransform(1.8, 25, -20);
        Button reset = Required<Button>(overlay, "ResetCropButton");
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, reset));
        AccountAvatarClientTests.Equal(1d, state.Current.Zoom, "Le bouton doit dézoomer complètement.");
        AccountAvatarClientTests.Equal(0d, state.Current.OffsetX, "Le bouton doit recentrer horizontalement.");
        AccountAvatarClientTests.Equal(0d, state.Current.OffsetY, "Le bouton doit recentrer verticalement.");
        Layout(overlay);
        ValidateRenderedCrop(overlay, state, preview, name + "-reset");
        overlay.DetachFromShell();
        Console.WriteLine($"  {name}: initial, wheel/pan and reset rendering verified at display DPI 96/144/192.");
    }

    private static void ValidateRenderedCrop(
        AvatarCropOverlayV2 overlay, AvatarCropUiState state, AvatarPreviewImage preview, string label)
    {
        AvatarPixelCrop crop = state.Current.Layout.PixelCrop;
        CroppedBitmap expectedSource = new(preview.OrientedImage,
            new Int32Rect(crop.X, crop.Y, crop.Size, crop.Size));
        ImageBrush reference = new(expectedSource) { Stretch = Stretch.Fill };
        // Compare two independently expressed viewboxes on the same full bitmap:
        // relative coordinates in the control, absolute DIPs in this oracle.
        // Physically cropping the texture first changes WPF's high-quality
        // downsampling stages and edge samples, even for the same pixel crop.
        double dipPerPixelX = preview.OrientedImage.Width / preview.OrientedPixelWidth;
        double dipPerPixelY = preview.OrientedImage.Height / preview.OrientedPixelHeight;
        ImageBrush dipReference = new(preview.OrientedImage)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(crop.X * dipPerPixelX, crop.Y * dipPerPixelY,
                crop.Size * dipPerPixelX, crop.Size * dipPerPixelY)
        };
        foreach (string brushName in new[] { "CropEditorBrush", "Preview128Brush", "Preview64Brush", "Preview32Brush" })
        {
            ImageBrush brush = Required<ImageBrush>(overlay, brushName);
            AccountAvatarClientTests.True(ReferenceEquals(preview.OrientedImage, brush.ImageSource),
                "Les aperçus doivent utiliser l'image source complète.");
            foreach (double dpi in new[] { 96d, 144d, 192d })
            {
                BitmapSource actual = RenderBrush(brush, 128, dpi);
                BitmapSource expected = RenderBrush(dipReference, 128, dpi);
                double error = MeanPixelError(expected, actual);
                AccountAvatarClientTests.True(error < 0.01,
                    $"{label}/{brushName} à {dpi} DPI doit correspondre au cadrage converti en DIPs (écart moyen={error:F3}).");
                // Also retain the independent integer-pixel crop oracle with
                // bilinear filtering, which avoids the different reduction stages.
                double pixelCropError = MeanPixelError(
                    RenderBrush(reference, 128, dpi, BitmapScalingMode.LowQuality),
                    RenderBrush(brush, 128, dpi, BitmapScalingMode.LowQuality));
                AccountAvatarClientTests.True(pixelCropError < 1.5,
                    $"{label}/{brushName} à {dpi} DPI doit montrer le crop en pixels (écart moyen={pixelCropError:F3}).");
            }
        }

        // Execute the real server processor in memory, without HTTP or a running service.
        AvatarNormalizedCrop normalized = state.Current.Layout.Crop;
        using SkiaAvatarImageProcessor processor = new();
        using MemoryStream stream = new(preview.OriginalBytes, writable: false);
        ProcessedAvatarImage exported = processor.ProcessAsync(stream, preview.ContentType,
            new NormalizedAvatarCrop(normalized.X, normalized.Y, normalized.Size, normalized.Size))
            .GetAwaiter().GetResult();
        BitmapSource server128 = AvatarWpfImageDecoder.DecodePng(exported.Variants[128]);
        double serverError = MeanPixelError(server128,
            RenderBrush(Required<ImageBrush>(overlay, "Preview128Brush"), 128, 96));
        AccountAvatarClientTests.True(serverError < 8,
            $"{label}: l'export serveur doit correspondre à l'aperçu (écart moyen={serverError:F3}).");
    }

    private static BitmapSource CreatePattern()
    {
        const int width = 1200;
        const int height = 900;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(x * 255 / width);
            pixels[offset + 1] = (byte)(y * 255 / height);
            pixels[offset + 2] = (byte)(((x / 150 + y / 150) % 2) * 200 + 20);
            pixels[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource WithDpi(BitmapSource source, double dpiX, double dpiY)
    {
        BitmapSource readable = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = ReadPixels(readable);
        BitmapSource result = BitmapSource.Create(source.PixelWidth, source.PixelHeight,
            dpiX, dpiY, PixelFormats.Bgra32, null, pixels, source.PixelWidth * 4);
        result.Freeze();
        return result;
    }

    private static AvatarPreviewImage EncodeAndDecode(BitmapSource source)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return AvatarWpfImageDecoder.DecodePreview(stream.ToArray(), "image/png");
    }

    private static BitmapSource RenderBrush(
        ImageBrush brush, int pixelSize, double dpi,
        BitmapScalingMode scalingMode = BitmapScalingMode.HighQuality)
    {
        double size = pixelSize * 96d / dpi;
        Rectangle rectangle = new() { Width = size, Height = size, Fill = brush };
        RenderOptions.SetBitmapScalingMode(rectangle, scalingMode);
        rectangle.Measure(new Size(size, size));
        rectangle.Arrange(new Rect(0, 0, size, size));
        rectangle.UpdateLayout();
        RenderTargetBitmap target = new(pixelSize, pixelSize, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(rectangle);
        return target;
    }

    private static double MeanPixelError(BitmapSource expected, BitmapSource actual)
    {
        byte[] a = ReadPixels(expected);
        byte[] b = ReadPixels(actual);
        AccountAvatarClientTests.Equal(a.Length, b.Length, "Les images comparées doivent avoir les mêmes dimensions.");
        long error = 0;
        for (int index = 0; index < a.Length; index++)
            error += Math.Abs(a[index] - b[index]);
        return error / (double)a.Length;
    }

    private static byte[] ReadPixels(BitmapSource source)
    {
        BitmapSource readable = source.Format == PixelFormats.Bgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[readable.PixelWidth * readable.PixelHeight * 4];
        readable.CopyPixels(pixels, readable.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    private static void Capture(FrameworkElement element, string? directory, string fileName)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        RenderTargetBitmap target = new((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(element);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using FileStream output = File.Create(System.IO.Path.Combine(directory, fileName));
        encoder.Save(output);
    }

    private static MouseWheelEventArgs Wheel(UIElement target, int delta)
    {
        MouseWheelEventArgs args = new(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        };
        target.RaiseEvent(args);
        return args;
    }

    private static T Required<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"Contrôle absent : {name}.");

    private static string? ReadOption(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
