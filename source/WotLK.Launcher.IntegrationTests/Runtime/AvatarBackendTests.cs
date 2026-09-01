using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SkiaSharp;
using WotLK.Launcher.Server.Avatars;

internal static partial class AvatarBackendTests
{
    private const string TestConnectionVariable = "ATLAS_03A2B_TEST_DB";

    internal static async Task<int> RunAsync()
    {
        await ValidateImageMatrixAsync();
        await ValidateStorageAndApplicationAsync();
        Console.WriteLine("Avatar backend local OK: image matrix, storage, atomic publication, concurrency and cleanup.");
        return 0;
    }

    private static async Task ValidateImageMatrixAsync()
    {
        using SkiaAvatarImageProcessor processor = new(maximumConcurrency: 2);
        NormalizedAvatarCrop fullSquare = new(0, 0, 1, 1);
        foreach ((SKEncodedImageFormat format, string mime) in new[]
        {
            (SKEncodedImageFormat.Jpeg, "image/jpeg"),
            (SKEncodedImageFormat.Png, "image/png"),
            (SKEncodedImageFormat.Webp, "image/webp")
        })
        {
            byte[] source = CreateImage(format, 512, 512);
            ProcessedAvatarImage processed = await processor.ProcessAsync(
                new MemoryStream(source, false), mime, fullSquare);
            ValidateVariants(processed);
        }

        byte[] png = CreateImage(SKEncodedImageFormat.Png, 640, 480);
        ProcessedAvatarImage landscape = await processor.ProcessAsync(
            new MemoryStream(png, false),
            "image/png",
            new NormalizedAvatarCrop(0.125, 0, 1, 1));
        ValidateVariants(landscape);

        await ExpectImageErrorAsync(processor, png, "image/jpeg", fullSquare, "mime_mismatch");
        await ExpectImageErrorAsync(processor, [0x13, 0x37, 0x00], "image/png", fullSquare, "decode_failed");
        await ExpectImageErrorAsync(
            processor,
            Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="),
            "image/gif",
            fullSquare,
            "unsupported_mime");
        await ExpectImageErrorAsync(
            processor,
            Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='512' height='512'/>") ,
            "image/svg+xml",
            fullSquare,
            "unsupported_mime");
        await ExpectImageErrorAsync(
            processor,
            CreateAnimatedWebP(256, 256),
            "image/webp",
            fullSquare,
            "animated_image");
        await ExpectImageErrorAsync(
            processor,
            CreateImage(SKEncodedImageFormat.Png, 255, 256),
            "image/png",
            fullSquare,
            "dimensions_too_small");
        await ExpectImageErrorAsync(
            processor,
            CreateImage(SKEncodedImageFormat.Png, 8193, 256),
            "image/png",
            fullSquare,
            "dimensions_too_large");
        ExpectImageError(
            () => SkiaAvatarImageProcessor.ValidateDimensions(8000, 5001),
            "pixel_count_too_large");
        await ExpectImageErrorAsync(
            processor,
            new byte[AvatarLimits.MaximumFileBytes + 1],
            "image/png",
            fullSquare,
            "file_too_large");

        await ExpectImageErrorAsync(
            processor,
            png,
            "image/png",
            new NormalizedAvatarCrop(-0.01, 0, 1, 1),
            "invalid_crop");
        await ExpectImageErrorAsync(
            processor,
            png,
            "image/png",
            new NormalizedAvatarCrop(0.9, 0, 1, 1),
            "crop_out_of_bounds");
        await ExpectImageErrorAsync(
            processor,
            png,
            "image/png",
            new NormalizedAvatarCrop(0, 0, 0.4, 0.4),
            "crop_too_small");

        byte[] jpeg = CreateImage(SKEncodedImageFormat.Jpeg, 320, 480);
        byte[] exifJpeg = AddExifOrientation(jpeg, 6);
        ProcessedAvatarImage oriented = await processor.ProcessAsync(
            new MemoryStream(exifJpeg, false),
            "image/jpeg",
            new NormalizedAvatarCrop(1d / 6d, 0, 2d / 3d, 1));
        Equal(480, oriented.OrientedWidth, "L'orientation EXIF doit inverser la largeur.");
        Equal(320, oriented.OrientedHeight, "L'orientation EXIF doit inverser la hauteur.");
        foreach (byte[] variant in oriented.Variants.Values)
        {
            True(!Encoding.ASCII.GetString(variant).Contains("Exif", StringComparison.Ordinal),
                "Les variantes normalisees ne doivent pas conserver l'EXIF.");
            using SKCodec codec = SKCodec.Create(SKData.CreateCopy(variant))
                ?? throw new InvalidOperationException("Une variante PNG est indecodable.");
            Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin, "La variante ne doit pas conserver l'orientation EXIF.");
        }
    }

    private static async Task ValidateStorageAndApplicationAsync()
    {
        string root = NewTemporaryRoot();
        try
        {
            LocalAvatarStorage realStorage = new(root);
            InMemoryAvatarRepository repository = new();
            InMemoryAvatarMutationLockProvider locks = new();
            using SkiaAvatarImageProcessor processor = new();
            AvatarApplicationService service = CreateService(repository, locks, realStorage, processor);
            byte[] png = CreateImage(SKEncodedImageFormat.Png, 512, 512);

            DefaultHttpContext firstRequest = await CreateUploadRequestAsync(
                png, "image/png", "portrait.gif", 0, 0, 1);
            AvatarCommandResult first = await service.UploadAsync(42, firstRequest.Request, CancellationToken.None);
            Equal(StatusCodes.Status200OK, first.StatusCode,
                $"La premiere photo doit etre publiee ({first.Error?.Code}: {first.Error?.Message}).");
            AvatarDescriptor firstAvatar = first.Avatar ?? throw new InvalidOperationException("Descripteur absent.");
            ValidatePublicDescriptor(firstAvatar);
            await ValidateStoredMediaAsync(repository, realStorage, firstAvatar);
            Equal(0, (await realStorage.InspectAsync(CancellationToken.None)).Staging.Count,
                "L'original et le staging doivent etre supprimes apres succes.");

            AvatarCommandResult replacement = await service.UploadAsync(
                42,
                (await CreateUploadRequestAsync(png, "image/png", "portrait.png", 0, 0, 1)).Request,
                CancellationToken.None);
            Equal(StatusCodes.Status200OK, replacement.StatusCode, "Le remplacement doit reussir.");
            AvatarDescriptor secondAvatar = replacement.Avatar!;
            True(secondAvatar.AvatarId != firstAvatar.AvatarId, "Le remplacement doit produire une URL immuable differente.");
            Equal(AvatarAssetStatus.Retired, repository.GetAsset(firstAvatar.AvatarId).Status,
                "L'ancien avatar doit devenir Retired seulement apres publication.");
            Equal(secondAvatar, await repository.GetActiveDescriptorAsync(42, CancellationToken.None),
                "Le profil doit pointer vers le nouvel avatar.");

            FaultingAvatarStorage variantFailureStorage = new(realStorage) { FailVariantSize = 64 };
            AvatarApplicationService variantFailure = CreateService(repository, locks, variantFailureStorage, processor);
            AvatarCommandResult failedVariant = await variantFailure.UploadAsync(
                42,
                (await CreateUploadRequestAsync(png, "image/png", "portrait.png", 0, 0, 1)).Request,
                CancellationToken.None);
            Equal(StatusCodes.Status503ServiceUnavailable, failedVariant.StatusCode,
                "Une erreur pendant la variante 64 doit etre controlee.");
            Equal(secondAvatar, await repository.GetActiveDescriptorAsync(42, CancellationToken.None),
                "Une erreur de variante ne doit pas remplacer l'ancien avatar.");
            Equal(0, (await realStorage.InspectAsync(CancellationToken.None)).Staging.Count,
                "Une erreur de variante doit nettoyer le staging et l'original.");

            FaultingAvatarStorage publishFailureStorage = new(realStorage) { FailPublish = true };
            AvatarApplicationService publishFailure = CreateService(repository, locks, publishFailureStorage, processor);
            AvatarCommandResult failedPublish = await publishFailure.UploadAsync(
                42,
                (await CreateUploadRequestAsync(png, "image/png", "portrait.png", 0, 0, 1)).Request,
                CancellationToken.None);
            Equal(StatusCodes.Status503ServiceUnavailable, failedPublish.StatusCode,
                "Une erreur avant publication doit etre controlee.");
            Equal(secondAvatar, await repository.GetActiveDescriptorAsync(42, CancellationToken.None),
                "Une erreur avant publication ne doit pas changer le profil.");

            FaultingAvatarStorage deniedStorage = new(realStorage) { FailBeginWithUnauthorizedAccess = true };
            AvatarCommandResult denied = await CreateService(repository, locks, deniedStorage, processor).UploadAsync(
                42,
                (await CreateUploadRequestAsync(png, "image/png", "portrait.png", 0, 0, 1)).Request,
                CancellationToken.None);
            Equal(StatusCodes.Status503ServiceUnavailable, denied.StatusCode,
                "Un stockage inaccessible doit produire une erreur controlee.");
            Equal("StorageFailed", denied.Error?.Code, "L'acces refuse doit rester une erreur de stockage stable.");

            AvatarCommandResult failedCrop = await service.UploadAsync(
                42,
                (await CreateUploadRequestAsync(png, "image/png", "portrait.png", 0.9, 0, 1)).Request,
                CancellationToken.None);
            Equal(StatusCodes.Status400BadRequest, failedCrop.StatusCode,
                "Un crop hors image doit etre refuse proprement.");
            Equal("InvalidCrop", failedCrop.Error?.Code, "La categorie du crop invalide doit rester stable.");
            Equal(secondAvatar, await repository.GetActiveDescriptorAsync(42, CancellationToken.None),
                "Un crop invalide ne doit pas remplacer l'ancien avatar.");
            Equal(0, (await realStorage.InspectAsync(CancellationToken.None)).Staging.Count,
                "Un crop invalide doit nettoyer l'original temporaire.");

            repository.FailNextPublish = true;
            AvatarCommandResult failedDatabase = await service.UploadAsync(
                42,
                (await CreateUploadRequestAsync(png, "image/png", "portrait.png", 0, 0, 1)).Request,
                CancellationToken.None);
            Equal(StatusCodes.Status503ServiceUnavailable, failedDatabase.StatusCode,
                "Une panne DB apres publication fichiers doit etre controlee.");
            Equal(secondAvatar, await repository.GetActiveDescriptorAsync(42, CancellationToken.None),
                "Une panne DB ne doit jamais detacher l'ancien avatar.");
            AvatarCleanupPlan orphanPlan = await new AvatarCleanupInspector(repository, realStorage)
                .InspectAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));
            True(orphanPlan.OrphanedMedia.Count >= 1,
                "Le dossier publie avant l'echec DB doit etre detectable comme orphelin.");

            BlockingAvatarImageProcessor blocking = new(processor);
            AvatarApplicationService concurrent = CreateService(repository, locks, realStorage, blocking);
            Task<AvatarCommandResult> activeUpload = concurrent.UploadAsync(
                84,
                (await CreateUploadRequestAsync(png, "image/png", "avatar.png", 0, 0, 1)).Request,
                CancellationToken.None);
            await blocking.WaitUntilEnteredAsync();
            AvatarCommandResult simultaneousUpload = await concurrent.UploadAsync(
                84,
                (await CreateUploadRequestAsync(png, "image/png", "avatar.png", 0, 0, 1)).Request,
                CancellationToken.None);
            Equal(StatusCodes.Status409Conflict, simultaneousUpload.StatusCode,
                "Deux uploads concurrents doivent etre refuses immediatement.");
            AvatarCommandResult simultaneousDelete = await concurrent.DeleteAsync(84, CancellationToken.None);
            Equal(StatusCodes.Status409Conflict, simultaneousDelete.StatusCode,
                "Upload et suppression concurrents doivent etre refuses immediatement.");
            blocking.Release();
            Equal(StatusCodes.Status200OK, (await activeUpload).StatusCode, "L'upload initial doit finir.");
            Equal(StatusCodes.Status204NoContent,
                (await concurrent.DeleteAsync(84, CancellationToken.None)).StatusCode,
                "Une mutation suivante doit fonctionner apres liberation du verrou.");
            Equal(StatusCodes.Status204NoContent,
                (await concurrent.DeleteAsync(84, CancellationToken.None)).StatusCode,
                "La suppression sans avatar doit etre idempotente.");

            await ValidateStorageCollisionAsync(realStorage, png);
            await ValidateCleanupSafetyAsync(repository, realStorage, root);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task ValidateStoredMediaAsync(
        InMemoryAvatarRepository repository,
        IAvatarStorage storage,
        AvatarDescriptor descriptor)
    {
        foreach (int size in AvatarVariantSizes.All)
        {
            AvatarMediaRecord media = await repository.GetMediaAsync(
                descriptor.AvatarId, descriptor.Version, size, CancellationToken.None)
                ?? throw new InvalidOperationException($"Metadonnee {size}px absente.");
            await using Stream stream = await storage.OpenVariantReadAsync(media.StorageKey, size, CancellationToken.None);
            using MemoryStream copy = new();
            await stream.CopyToAsync(copy);
            byte[] bytes = copy.ToArray();
            True(SHA256.HashData(bytes).SequenceEqual(media.Sha256), "Le SHA-256 DB doit correspondre aux octets stockes.");
            Equal(bytes.LongLength, media.ByteLength, "La taille DB doit correspondre au fichier.");
            using SKBitmap bitmap = SKBitmap.Decode(bytes)
                ?? throw new InvalidOperationException("Variante stockee indecodable.");
            Equal(size, bitmap.Width, "Largeur de variante incorrecte.");
            Equal(size, bitmap.Height, "Hauteur de variante incorrecte.");
        }
    }

    private static async Task ValidateStorageCollisionAsync(LocalAvatarStorage storage, byte[] png)
    {
        AvatarStorageKey key = AvatarStorageKey.Create(Guid.NewGuid(), 1);
        AvatarStagingHandle first = await StageVariantsAsync(storage, png);
        await storage.PublishAsync(first, key, CancellationToken.None);
        AvatarStagingHandle collision = await StageVariantsAsync(storage, png);
        await ExpectAsync<AvatarStorageException>(
            () => storage.PublishAsync(collision, key, CancellationToken.None),
            "Une collision de stockage doit etre refusee.");
        await storage.DiscardStagingAsync(collision, CancellationToken.None);
        await ExpectAsync<ArgumentException>(
            () => Task.FromResult(AvatarStorageKey.Parse("avatars/../../etc")),
            "La traversal doit etre refusee.");
    }

    private static async Task ValidateCleanupSafetyAsync(
        InMemoryAvatarRepository repository,
        LocalAvatarStorage storage,
        string root)
    {
        DateTimeOffset old = DateTimeOffset.UtcNow.AddHours(-3);
        AvatarStagingHandle stale = await storage.BeginStagingAsync(CancellationToken.None);
        Directory.SetLastWriteTimeUtc(
            Path.Combine(root, "staging", stale.OperationId.ToString("N")),
            old.UtcDateTime);
        AvatarAssetRecord pending = repository.AddAsset(100, AvatarAssetStatus.Pending, isActive: false, old);
        AvatarAssetRecord retired = repository.AddAsset(100, AvatarAssetStatus.Retired, isActive: false, old);
        AvatarAssetRecord active = repository.AddAsset(100, AvatarAssetStatus.Ready, isActive: true, old);

        AvatarCleanupPlan plan = await new AvatarCleanupInspector(repository, storage)
            .InspectAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        True(plan.StaleStaging.Any(item => item.Handle == stale), "Le staging ancien doit etre identifie.");
        True(plan.AbandonedPending.Any(item => item.Id == pending.Id), "Le Pending ancien doit etre identifie.");
        True(plan.PurgeableAssets.Any(item => item.Id == retired.Id), "Le Retired ancien doit etre identifiable.");
        True(plan.PurgeableAssets.All(item => item.Id != active.Id), "L'avatar Ready actif ne doit jamais etre purgeable.");
        True(plan.OrphanedMedia.All(item => item.StorageKey != active.StorageKey),
            "L'avatar Ready actif ne doit jamais etre classe media orphelin.");
    }

    private static AvatarApplicationService CreateService(
        IAvatarRepository repository,
        IAvatarMutationLockProvider locks,
        IAvatarStorage storage,
        IAvatarImageProcessor processor)
        => new(
            repository,
            locks,
            storage,
            processor,
            new AvatarMultipartUploadReader(storage),
            NullLogger<AvatarApplicationService>.Instance);

    private static async Task<DefaultHttpContext> CreateUploadRequestAsync(
        byte[] image,
        string contentType,
        string fileName,
        double cropX,
        double cropY,
        double cropSize)
    {
        using MultipartFormDataContent multipart = new("atlas-avatar-boundary-" + Guid.NewGuid().ToString("N"));
        ByteArrayContent imageContent = new(image);
        imageContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        multipart.Add(imageContent, "image", fileName);
        multipart.Add(new StringContent(cropX.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropX");
        multipart.Add(new StringContent(cropY.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropY");
        multipart.Add(new StringContent(cropSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropSize");
        MemoryStream body = new();
        await multipart.CopyToAsync(body);
        body.Position = 0;
        DefaultHttpContext context = new();
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = body;
        return context;
    }

    private static async Task<AvatarStagingHandle> StageVariantsAsync(IAvatarStorage storage, byte[] png)
    {
        AvatarStagingHandle handle = await storage.BeginStagingAsync(CancellationToken.None);
        foreach (int size in AvatarVariantSizes.All)
            await storage.WriteVariantAsync(handle, size, new MemoryStream(png, false), CancellationToken.None);
        return handle;
    }

    private static void ValidateVariants(ProcessedAvatarImage processed)
    {
        True(processed.Variants.Keys.Order().SequenceEqual(AvatarVariantSizes.All),
            "Les quatre variantes MVP doivent etre produites.");
        foreach ((int size, byte[] bytes) in processed.Variants)
        {
            using SKCodec codec = SKCodec.Create(SKData.CreateCopy(bytes))
                ?? throw new InvalidOperationException("Une variante PNG est indecodable.");
            Equal(SKEncodedImageFormat.Png, codec.EncodedFormat, "Toutes les variantes doivent etre PNG.");
            Equal(size, codec.Info.Width, "Largeur de variante incorrecte.");
            Equal(size, codec.Info.Height, "Hauteur de variante incorrecte.");
        }
    }

    private static void ValidatePublicDescriptor(AvatarDescriptor descriptor)
    {
        string serialized = System.Text.Json.JsonSerializer.Serialize(descriptor);
        True(!serialized.Contains("StorageKey", StringComparison.OrdinalIgnoreCase)
            && !serialized.Contains("srv/", StringComparison.OrdinalIgnoreCase)
            && !serialized.Contains("email", StringComparison.OrdinalIgnoreCase),
            "Le DTO public ne doit exposer aucun detail interne.");
    }

    private static async Task ExpectImageErrorAsync(
        IAvatarImageProcessor processor,
        byte[] source,
        string mime,
        NormalizedAvatarCrop crop,
        string expectedCode)
    {
        try
        {
            await processor.ProcessAsync(new MemoryStream(source, false), mime, crop);
        }
        catch (AvatarImageValidationException exception) when (exception.Code == expectedCode)
        {
            return;
        }
        throw new InvalidOperationException($"L'image devait etre refusee avec {expectedCode}.");
    }

    private static void ExpectImageError(Action action, string expectedCode)
    {
        try
        {
            action();
        }
        catch (AvatarImageValidationException exception) when (exception.Code == expectedCode)
        {
            return;
        }
        throw new InvalidOperationException($"L'image devait etre refusee avec {expectedCode}.");
    }

    private static byte[] CreateImage(SKEncodedImageFormat format, int width, int height)
    {
        using SKSurface surface = SKSurface.Create(new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            SKColorSpace.CreateSrgb())) ?? throw new InvalidOperationException("Fixture Skia impossible.");
        surface.Canvas.Clear(new SKColor(18, 27, 41));
        using SKPaint paint = new() { Color = new SKColor(219, 177, 82), IsAntialias = false };
        surface.Canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        using SKImage image = surface.Snapshot();
        using SKData encoded = image.Encode(format, 92)
            ?? throw new InvalidOperationException($"Encodage fixture {format} impossible.");
        return encoded.ToArray();
    }

    private static byte[] AddExifOrientation(byte[] jpeg, ushort orientation)
    {
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

    private static byte[] CreateAnimatedWebP(int width, int height)
    {
        byte[] first = CreateImage(SKEncodedImageFormat.Webp, width, height);
        byte[] second = CreateImage(SKEncodedImageFormat.Webp, width, height);
        byte[] frameChunksA = first[12..];
        byte[] frameChunksB = second[12..];
        using MemoryStream payload = new();
        WriteWebPChunk(payload, "VP8X", [
            0x02, 0, 0, 0,
            (byte)((width - 1) & 0xff), (byte)(((width - 1) >> 8) & 0xff), (byte)(((width - 1) >> 16) & 0xff),
            (byte)((height - 1) & 0xff), (byte)(((height - 1) >> 8) & 0xff), (byte)(((height - 1) >> 16) & 0xff)
        ]);
        WriteWebPChunk(payload, "ANIM", [0, 0, 0, 0, 0, 0]);
        WriteAnimationFrame(payload, width, height, frameChunksA);
        WriteAnimationFrame(payload, width, height, frameChunksB);

        byte[] chunks = payload.ToArray();
        using MemoryStream result = new();
        result.Write(Encoding.ASCII.GetBytes("RIFF"));
        result.Write(BitConverter.GetBytes(chunks.Length + 4));
        result.Write(Encoding.ASCII.GetBytes("WEBP"));
        result.Write(chunks);
        return result.ToArray();
    }

    private static void WriteAnimationFrame(Stream destination, int width, int height, byte[] imageChunks)
    {
        using MemoryStream frame = new();
        frame.Write(new byte[6]);
        frame.Write([
            (byte)((width - 1) & 0xff), (byte)(((width - 1) >> 8) & 0xff), (byte)(((width - 1) >> 16) & 0xff),
            (byte)((height - 1) & 0xff), (byte)(((height - 1) >> 8) & 0xff), (byte)(((height - 1) >> 16) & 0xff),
            100, 0, 0,
            0
        ]);
        frame.Write(imageChunks);
        WriteWebPChunk(destination, "ANMF", frame.ToArray());
    }

    private static void WriteWebPChunk(Stream destination, string fourCc, byte[] payload)
    {
        destination.Write(Encoding.ASCII.GetBytes(fourCc));
        destination.Write(BitConverter.GetBytes(payload.Length));
        destination.Write(payload);
        if ((payload.Length & 1) != 0)
            destination.WriteByte(0);
    }

    private static string NewTemporaryRoot()
        => Path.Combine(Path.GetTempPath(), "AtlasAvatarBackendTests", Guid.NewGuid().ToString("N"));

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static async Task ExpectAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Attendu={expected}, reel={actual}.");
    }

    private sealed class InMemoryAvatarMutationLockProvider : IAvatarMutationLockProvider
    {
        private readonly ConcurrentDictionary<uint, byte> _active = new();

        public Task<IAvatarMutationLease?> TryAcquireAsync(uint accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IAvatarMutationLease? lease = _active.TryAdd(accountId, 0)
                ? new CallbackLease(() => _active.TryRemove(accountId, out _))
                : null;
            return Task.FromResult(lease);
        }
    }

    private sealed class CallbackLease(Action release) : IAvatarMutationLease
    {
        private Action? _release = release;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingAvatarImageProcessor(IAvatarImageProcessor inner) : IAvatarImageProcessor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessedAvatarImage> ProcessAsync(
            Stream source,
            string declaredContentType,
            NormalizedAvatarCrop crop,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await inner.ProcessAsync(source, declaredContentType, crop, cancellationToken);
        }

        internal Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        internal void Release() => _release.TrySetResult();
    }

    private sealed class FaultingAvatarStorage(IAvatarStorage inner) : IAvatarStorage
    {
        internal int? FailVariantSize { get; init; }
        internal bool FailPublish { get; init; }
        internal bool FailBeginWithUnauthorizedAccess { get; init; }

        public Task<AvatarStagingHandle> BeginStagingAsync(CancellationToken cancellationToken)
            => FailBeginWithUnauthorizedAccess
                ? Task.FromException<AvatarStagingHandle>(new UnauthorizedAccessException("Injected access denial."))
                : inner.BeginStagingAsync(cancellationToken);
        public Task WriteOriginalAsync(AvatarStagingHandle handle, Stream source, CancellationToken cancellationToken)
            => inner.WriteOriginalAsync(handle, source, cancellationToken);
        public Task<Stream> OpenOriginalReadAsync(AvatarStagingHandle handle, CancellationToken cancellationToken)
            => inner.OpenOriginalReadAsync(handle, cancellationToken);
        public Task<AvatarStoredVariant> WriteVariantAsync(
            AvatarStagingHandle handle, int size, Stream png, CancellationToken cancellationToken)
            => size == FailVariantSize
                ? Task.FromException<AvatarStoredVariant>(new AvatarStorageException("Injected variant failure."))
                : inner.WriteVariantAsync(handle, size, png, cancellationToken);
        public Task PublishAsync(AvatarStagingHandle handle, AvatarStorageKey storageKey, CancellationToken cancellationToken)
            => FailPublish
                ? Task.FromException(new AvatarStorageException("Injected publication failure."))
                : inner.PublishAsync(handle, storageKey, cancellationToken);
        public Task<Stream> OpenVariantReadAsync(AvatarStorageKey storageKey, int size, CancellationToken cancellationToken)
            => inner.OpenVariantReadAsync(storageKey, size, cancellationToken);
        public Task<bool> MoveToTrashAsync(AvatarStorageKey storageKey, CancellationToken cancellationToken)
            => inner.MoveToTrashAsync(storageKey, cancellationToken);
        public Task QuarantineAsync(AvatarStagingHandle handle, CancellationToken cancellationToken)
            => inner.QuarantineAsync(handle, cancellationToken);
        public Task DiscardStagingAsync(AvatarStagingHandle handle, CancellationToken cancellationToken)
            => inner.DiscardStagingAsync(handle, cancellationToken);
        public Task<AvatarStorageInventory> InspectAsync(CancellationToken cancellationToken)
            => inner.InspectAsync(cancellationToken);
    }

    private sealed class InMemoryAvatarRepository : IAvatarRepository
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, AvatarAssetRecord> _assets = [];
        private readonly Dictionary<Guid, IReadOnlyList<AvatarStoredVariant>> _variants = [];
        private readonly Dictionary<uint, Guid> _active = [];
        private ulong _version;

        internal bool FailNextPublish { get; set; }

        public Task<AvatarRateLimitDecision> TryConsumeUploadPermitAsync(uint accountId, CancellationToken cancellationToken)
            => Task.FromResult(AvatarRateLimitDecision.Permit());

        public Task<AvatarAssetRecord> CreatePendingAsync(uint accountId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                AvatarAssetRecord asset = new(
                    Guid.NewGuid(),
                    accountId,
                    ++_version,
                    AvatarAssetStatus.Pending,
                    AvatarStorageKey.Create(Guid.NewGuid(), _version),
                    now,
                    now);
                asset = asset with { StorageKey = AvatarStorageKey.Create(asset.Id, asset.Version) };
                _assets.Add(asset.Id, asset);
                return Task.FromResult(asset);
            }
        }

        public Task<AvatarPublicationResult> PublishReadyAsync(
            uint accountId,
            AvatarAssetRecord pending,
            IReadOnlyList<AvatarStoredVariant> variants,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (FailNextPublish)
                {
                    FailNextPublish = false;
                    throw new InvalidOperationException("Injected DB commit failure.");
                }
                AvatarAssetRecord? previous = _active.TryGetValue(accountId, out Guid previousId)
                    ? _assets[previousId]
                    : null;
                if (previous is not null)
                    _assets[previous.Id] = previous with { Status = AvatarAssetStatus.Retired, UpdatedAt = DateTimeOffset.UtcNow };
                _assets[pending.Id] = pending with { Status = AvatarAssetStatus.Ready, UpdatedAt = DateTimeOffset.UtcNow };
                _variants[pending.Id] = variants.ToArray();
                _active[accountId] = pending.Id;
                return Task.FromResult(new AvatarPublicationResult(
                    AvatarDescriptor.Create(pending.Id, pending.Version),
                    previous));
            }
        }

        public Task MarkPendingDeletedAsync(uint accountId, Guid avatarId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_assets.TryGetValue(avatarId, out AvatarAssetRecord? asset)
                    && asset.OwnerAccountId == accountId
                    && asset.Status == AvatarAssetStatus.Pending)
                {
                    _assets[avatarId] = asset with { Status = AvatarAssetStatus.Deleted, UpdatedAt = DateTimeOffset.UtcNow };
                }
            }
            return Task.CompletedTask;
        }

        public Task<AvatarDeletionResult> DeleteActiveAsync(uint accountId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (!_active.Remove(accountId, out Guid avatarId))
                    return Task.FromResult(new AvatarDeletionResult(false, null));
                AvatarAssetRecord active = _assets[avatarId];
                _assets[avatarId] = active with { Status = AvatarAssetStatus.Deleted, UpdatedAt = DateTimeOffset.UtcNow };
                return Task.FromResult(new AvatarDeletionResult(true, active));
            }
        }

        public Task<AvatarDescriptor?> GetActiveDescriptorAsync(uint accountId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                AvatarDescriptor? descriptor = _active.TryGetValue(accountId, out Guid id)
                    ? AvatarDescriptor.Create(id, _assets[id].Version)
                    : null;
                return Task.FromResult(descriptor);
            }
        }

        public Task<AvatarMediaRecord?> GetMediaAsync(
            Guid avatarId,
            ulong version,
            int size,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (!_assets.TryGetValue(avatarId, out AvatarAssetRecord? asset)
                    || asset.Status != AvatarAssetStatus.Ready
                    || asset.Version != version
                    || !_variants.TryGetValue(avatarId, out IReadOnlyList<AvatarStoredVariant>? variants))
                {
                    return Task.FromResult<AvatarMediaRecord?>(null);
                }
                AvatarStoredVariant? variant = variants.SingleOrDefault(item => item.Size == size);
                return Task.FromResult(variant is null
                    ? null
                    : new AvatarMediaRecord(
                        avatarId, version, asset.StorageKey, size, variant.ContentType, variant.ByteLength, variant.Sha256));
            }
        }

        public Task<AvatarRepositoryInventory> InspectAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult(new AvatarRepositoryInventory(
                    _assets.Values.Select(asset => new AvatarRepositoryAssetState(
                        asset,
                        _active.TryGetValue(asset.OwnerAccountId, out Guid id) && id == asset.Id)).ToArray()));
            }
        }

        internal AvatarAssetRecord GetAsset(Guid id)
        {
            lock (_sync)
                return _assets[id];
        }

        internal AvatarAssetRecord AddAsset(
            uint accountId,
            AvatarAssetStatus status,
            bool isActive,
            DateTimeOffset timestamp)
        {
            lock (_sync)
            {
                Guid id = Guid.NewGuid();
                ulong version = ++_version;
                AvatarAssetRecord asset = new(
                    id,
                    accountId,
                    version,
                    status,
                    AvatarStorageKey.Create(id, version),
                    timestamp,
                    timestamp);
                _assets[id] = asset;
                if (isActive)
                    _active[accountId] = id;
                return asset;
            }
        }
    }
}
