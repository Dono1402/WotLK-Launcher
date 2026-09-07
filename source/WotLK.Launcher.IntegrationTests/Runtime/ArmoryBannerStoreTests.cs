using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WotLK.Launcher.Account;

internal static class ArmoryBannerStoreTests
{
    internal static async Task RunAsync()
    {
        (byte[] first, byte[] second, byte[] jpeg) = await CreateImagesOnStaAsync();
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AtlasArmoryBannerTests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            ArmoryBannerStore store = new(root);
            ArmoryBannerData firstBanner = new(first, 0.25, 0.75, 1.25);
            ArmoryBannerData secondBanner = new(second, 1, 0, 2.75);
            True(await store.LoadAsync(42, CancellationToken.None) is null, "Un compte sans bannière doit conserver le fond par défaut.");
            await store.SaveAsync(42, firstBanner, CancellationToken.None);
            True(await store.LoadAsync(84, CancellationToken.None) is null, "Une bannière ne doit jamais être empruntée à un autre compte.");
            await store.SaveAsync(84, secondBanner, CancellationToken.None);
            await AssertBannerAsync(store, 42, firstBanner);
            await AssertBannerAsync(store, 84, secondBanner);

            ArmoryBannerStore reopened = new(root);
            await AssertBannerAsync(reopened, 42, firstBanner);
            await AssertBannerAsync(reopened, 84, secondBanner);
            ArmoryBannerData replacement = new(second, 0, 1, 3);
            await reopened.SaveAsync(42, replacement, CancellationToken.None);
            await AssertBannerAsync(new(root), 42, replacement);
            await AssertBannerAsync(new(root), 84, secondBanner);

            await ValidateRejectedWritesAsync(reopened, replacement, jpeg);
            await ValidateCancellationAsync(reopened, replacement, firstBanner);
            await ValidateLegacyDocumentsAsync(root, first);
            await AssertBannerAsync(new(root), 42, replacement);
            await AssertBannerAsync(new(root), 84, secondBanner);

            ArmoryBannerData defaultImage = new(null, 0.2, 0.8, 1.6);
            await reopened.SaveAsync(42, defaultImage, CancellationToken.None);
            await AssertBannerAsync(new(root), 42, defaultImage);
            await AssertBannerAsync(new(root), 84, secondBanner);

            await reopened.ResetAsync(42, CancellationToken.None);
            await reopened.ResetAsync(42, CancellationToken.None);
            True(await new ArmoryBannerStore(root).LoadAsync(42, CancellationToken.None) is null,
                "Réinitialiser deux fois doit supprimer durablement la bannière du compte.");
            await AssertBannerAsync(new(root), 84, secondBanner);
            await reopened.ResetAsync(84, CancellationToken.None);
            True(await reopened.LoadAsync(84, CancellationToken.None) is null, "La dernière bannière doit pouvoir être réinitialisée.");
            Console.WriteLine("Armory banner store OK: STA normalization, account isolation, persistence, replacement, default-image focal point, zoom, legacy documents, reset, invalid data and cancellation.");
        }
        finally
        {
            string allowedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AtlasArmoryBannerTests"))
                + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Le nettoyage doit rester dans le dossier temporaire des tests de bannières.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ValidateRejectedWritesAsync(ArmoryBannerStore store, ArmoryBannerData previous, byte[] jpeg)
    {
        byte[] previousPng = previous.PngBytes ?? throw new InvalidOperationException("Le témoin de validation doit contenir une image PNG.");
        byte[] tooLarge = new byte[checked(ArmoryBannerStore.MaximumImageBytes + 1)];
        previousPng.CopyTo(tooLarge, 0);
        foreach ((string reason, byte[] bytes) in new (string, byte[])[]
        {
            ("PNG vide", []),
            ("JPEG présenté comme PNG", jpeg),
            ("SVG présenté comme PNG", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>")),
            ("PNG tronqué", previousPng[..16]),
            ("PNG dépassant la limite d'octets", tooLarge)
        })
        {
            await ExpectRejectedAsync(() => store.SaveAsync(42, previous with { PngBytes = bytes }, CancellationToken.None), reason);
            await AssertBannerAsync(store, 42, previous);
        }
        foreach (double value in new[] { -0.01, 1.01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            await ExpectRejectedAsync(() => store.SaveAsync(42, previous with { PositionX = value }, CancellationToken.None), "Position horizontale invalide");
            await AssertBannerAsync(store, 42, previous);
            await ExpectRejectedAsync(() => store.SaveAsync(42, previous with { PositionY = value }, CancellationToken.None), "Position verticale invalide");
            await AssertBannerAsync(store, 42, previous);
        }
        foreach (double value in new[] { 0.99, 3.01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            await ExpectRejectedAsync(() => store.SaveAsync(42, previous with { Zoom = value }, CancellationToken.None), "Zoom invalide");
            await AssertBannerAsync(store, 42, previous);
            await ExpectRejectedAsync(() => store.SaveAsync(42, previous with { PngBytes = null, Zoom = value }, CancellationToken.None), "Zoom invalide du fond par défaut");
            await AssertBannerAsync(store, 42, previous);
        }
    }

    private static async Task ValidateLegacyDocumentsAsync(string root, byte[] png)
    {
        // These documents deliberately reproduce the previous persisted schema,
        // rather than serializing the current record that now includes Zoom.
        await File.WriteAllTextAsync(Path.Combine(root, "126.json"),
            "{\"PngBytes\":\"" + Convert.ToBase64String(png) + "\",\"PositionX\":0.3,\"PositionY\":0.7}", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(root, "168.json"),
            "{\"PngBytes\":null,\"PositionX\":0.6,\"PositionY\":0.4}", CancellationToken.None);
        ArmoryBannerData imageExpected = new(png, 0.3, 0.7, 1);
        ArmoryBannerData defaultExpected = new(null, 0.6, 0.4, 1);
        foreach ((uint accountId, ArmoryBannerData expected) in new[] { (126u, imageExpected), (168u, defaultExpected) })
        {
            ArmoryBannerStore store = new(root);
            await AssertBannerAsync(store, accountId, expected);
            ArmoryBannerData loaded = await store.LoadAsync(accountId, CancellationToken.None)
                ?? throw new InvalidOperationException("Un ancien document doit rester disponible après lecture.");
            await store.SaveAsync(accountId, loaded, CancellationToken.None);
            await AssertBannerAsync(new(root), accountId, expected);
        }
        ArmoryBannerStore reopened = new(root);
        await reopened.ResetAsync(126, CancellationToken.None);
        True(await new ArmoryBannerStore(root).LoadAsync(126, CancellationToken.None) is null,
            "Le document migré avec image doit pouvoir être réinitialisé.");
        await AssertBannerAsync(new(root), 168, defaultExpected);
        await reopened.ResetAsync(168, CancellationToken.None);
        True(await new ArmoryBannerStore(root).LoadAsync(168, CancellationToken.None) is null,
            "Le document migré sans image doit pouvoir être réinitialisé.");
    }

    private static async Task ValidateCancellationAsync(ArmoryBannerStore store, ArmoryBannerData previous, ArmoryBannerData replacement)
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await ExpectCancelledAsync(async () => { _ = await store.LoadAsync(42, cancelled.Token); });
        await ExpectCancelledAsync(() => store.SaveAsync(42, replacement, cancelled.Token));
        await AssertBannerAsync(store, 42, previous);
        await ExpectCancelledAsync(() => store.ResetAsync(42, cancelled.Token));
        await AssertBannerAsync(store, 42, previous);
        await ExpectCancelledAsync(async () => { _ = await store.LoadAsync(126, cancelled.Token); });
        await ExpectCancelledAsync(() => store.SaveAsync(126, replacement, cancelled.Token));
        True(await store.LoadAsync(126, CancellationToken.None) is null, "Une sauvegarde déjà annulée ne doit créer aucune bannière.");
        await ExpectCancelledAsync(() => store.ResetAsync(126, cancelled.Token));
    }

    private static async Task AssertBannerAsync(ArmoryBannerStore store, uint accountId, ArmoryBannerData expected)
    {
        ArmoryBannerData actual = await store.LoadAsync(accountId, CancellationToken.None)
            ?? throw new InvalidOperationException($"La bannière persistée du compte {accountId} est absente.");
        True(expected.PngBytes is null ? actual.PngBytes is null : actual.PngBytes is not null && expected.PngBytes.SequenceEqual(actual.PngBytes),
            $"L'image de la bannière du compte {accountId} a changé.");
        True(expected.PositionX == actual.PositionX && expected.PositionY == actual.PositionY,
            $"Le cadrage de la bannière du compte {accountId} n'a pas été conservé.");
        True(expected.Zoom == actual.Zoom, $"Le zoom de la bannière du compte {accountId} n'a pas été conservé.");
    }

    private static Task<(byte[] First, byte[] Second, byte[] Jpeg)> CreateImagesOnStaAsync()
    {
        TaskCompletionSource<(byte[], byte[], byte[])> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new(() =>
        {
            try
            {
                True(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "La normalisation WPF doit être vérifiée sur un thread STA.");
                BitmapSource first = CreateBitmap(96, 48, 0x30, 0x80, 0xE0);
                BitmapSource second = CreateBitmap(48, 96, 0xE0, 0x50, 0x20);
                byte[] firstPng = ArmoryBannerStore.Normalize(first);
                byte[] secondPng = ArmoryBannerStore.Normalize(second);
                AssertNormalizedImage(firstPng, first.PixelWidth, first.PixelHeight);
                AssertNormalizedImage(secondPng, second.PixelWidth, second.PixelHeight);
                BitmapSource large = CreateBitmap(2560, 1280, 0x28, 0x48, 0x68);
                AssertNormalizedImage(ArmoryBannerStore.Normalize(large), large.PixelWidth, large.PixelHeight);
                using MemoryStream stream = new();
                JpegBitmapEncoder encoder = new();
                encoder.Frames.Add(BitmapFrame.Create(first));
                encoder.Save(stream);
                completion.TrySetResult((firstPng, secondPng, stream.ToArray()));
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "AtlasArmoryBannerStoreTests" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static BitmapSource CreateBitmap(int width, int height, byte red, byte green, byte blue)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = blue; pixels[index + 1] = green; pixels[index + 2] = red; pixels[index + 3] = 255;
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static void AssertNormalizedImage(byte[] bytes, int originalWidth, int originalHeight)
    {
        True(bytes.Length > 8 && bytes.Length <= ArmoryBannerStore.MaximumImageBytes, "La normalisation doit respecter la limite d'octets.");
        using MemoryStream stream = new(bytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        True(decoder is PngBitmapDecoder && decoder.Frames.Count == 1, "La normalisation doit produire un véritable PNG décodable.");
        BitmapFrame frame = decoder.Frames[0];
        True(frame.PixelWidth > 0 && frame.PixelHeight > 0 && frame.PixelWidth <= originalWidth && frame.PixelHeight <= originalHeight,
            "La normalisation doit conserver une image valide sans agrandissement inutile.");
        True(Math.Abs(frame.PixelWidth / (double)frame.PixelHeight - originalWidth / (double)originalHeight) < 0.01,
            "La normalisation ne doit pas déformer le rapport largeur/hauteur.");
    }

    private static async Task ExpectRejectedAsync(Func<Task> action, string reason)
    {
        try { await action(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException) { return; }
        throw new InvalidOperationException(reason + " : la sauvegarde aurait dû être refusée.");
    }

    private static async Task ExpectCancelledAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Une opération déjà annulée doit lever OperationCanceledException.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
