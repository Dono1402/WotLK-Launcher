using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

internal static class AccountAvatarClientTests
{
    private static readonly Uri ApiBase = new("https://atlas-avatar.test/api/v1/");

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        await ValidateMediaClientContractsAsync();
        ValidateCropGeometry();
        await ValidateWpfDecodeAndSelectionAsync();
        await ValidateCacheAsync();
        await ValidateCoordinatorAsync();
        await ValidateLifecycleCancellationAsync();
        await AccountAvatarWpfTests.RunAsync(captureDirectory);
        Console.WriteLine("Account avatar client OK: HTTP, selection, EXIF, crop, cache, runtime and WPF test server.");
        return 0;
    }

    private static async Task ValidateMediaClientContractsAsync()
    {
        AvatarDescriptor descriptor = Descriptor(version: 7);
        byte[] png = CreatePng(8, 8);
        ConcurrentQueue<HttpRequestMessageSnapshot> requests = new();
        Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> responses = new();
        responses.Enqueue((request, _) => Task.FromResult(Json(HttpStatusCode.OK, new
        {
            accountId = 1,
            username = "Dono1402",
            email = "dono@example.test",
            emailVerified = true,
            avatarKey = "gold",
            twoFactorEnabled = false,
            recoveryCodesGenerated = false,
            completion = 75
        })));
        responses.Enqueue((request, _) => Task.FromResult(Json(HttpStatusCode.OK, new
        {
            accountId = 1,
            username = "Dono1402",
            email = "dono@example.test",
            emailVerified = true,
            avatarKey = "gold",
            twoFactorEnabled = false,
            recoveryCodesGenerated = false,
            completion = 75,
            avatar = descriptor
        })));
        responses.Enqueue(async (request, cancellationToken) =>
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Enqueue(new HttpRequestMessageSnapshot(
                request.Method,
                request.RequestUri!,
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return Json(HttpStatusCode.OK, descriptor);
        });
        responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        responses.Enqueue((_, _) => Task.FromResult(Png(HttpStatusCode.OK, png)));
        responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        using HttpClient http = new(new QueueHttpHandler(responses));
        AvatarMediaClient client = new(http, ApiBase);
        AvatarProfileReadResult legacy = await client.GetProfileAsync(CancellationToken.None);
        False(legacy.SupportsProfilePhotos, "Un profil legacy sans propriété avatar doit rester compatible.");
        True(legacy.Profile.Avatar is null, "Un profil legacy ne doit pas inventer de photo.");
        AvatarProfileReadResult modern = await client.GetProfileAsync(CancellationToken.None);
        True(modern.SupportsProfilePhotos, "La présence explicite de avatar doit annoncer la capacité.");
        Equal(descriptor, modern.Profile.Avatar, "Le descripteur public du profil est altéré.");

        RecordingProgress progress = new();
        AvatarDescriptor uploaded = await client.UploadAvatarAsync(
            new AvatarUploadRequest(
                png,
                "image/png",
                new AvatarNormalizedCrop(0.125, 0.25, 0.5)),
            progress,
            CancellationToken.None);
        Equal(descriptor, uploaded, "Le résultat d'upload doit être le descripteur serveur.");
        HttpRequestMessageSnapshot upload = requests.Single();
        Equal(HttpMethod.Post, upload.Method, "L'upload avatar doit utiliser POST.");
        Equal("/api/v1/me/avatar/photo", upload.Uri.AbsolutePath, "La route d'upload est incorrecte.");
        True(upload.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true,
            "L'upload doit être multipart/form-data.");
        True(upload.Body.Contains("0.125", StringComparison.Ordinal)
            && upload.Body.Contains("0.25", StringComparison.Ordinal)
            && upload.Body.Contains("0.5", StringComparison.Ordinal),
            "Les coordonnées de crop invariantes doivent être transmises.");
        True(progress.Values.Any(value => value.Phase == AvatarUploadPhase.Preparing)
            && progress.Values.Any(value => value.Phase == AvatarUploadPhase.Sending)
            && progress.Values.Any(value => value.Phase == AvatarUploadPhase.Processing),
            "Les phases préparation, envoi et traitement doivent être observables.");
        Equal(100, progress.Values.Last().Percentage, "Le dernier progrès d'envoi doit atteindre 100 %.");

        await client.DeleteAvatarAsync(CancellationToken.None);
        AvatarMediaDownloadResult media = await client.DownloadAvatarAsync(descriptor, 128, CancellationToken.None);
        Equal(AvatarMediaDownloadStatus.Success, media.Status, "Le média PNG authentifié doit être accepté.");
        True(png.SequenceEqual(media.Bytes!), "Les octets du média ne doivent pas être modifiés par le client.");
        Equal(AvatarMediaDownloadStatus.NotFound,
            (await client.DownloadAvatarAsync(descriptor, 64, CancellationToken.None)).Status,
            "Un média absent doit produire le fallback sans exception.");
        Equal(AvatarMediaDownloadStatus.Unauthorized,
            (await client.DownloadAvatarAsync(descriptor, 32, CancellationToken.None)).Status,
            "Un média 401 doit déléguer la sémantique de session.");
        AvatarMediaException foreignMedia = await ExpectAsync<AvatarMediaException>(() =>
            client.DownloadAvatarAsync(
                descriptor with
                {
                    Url256 = "https://media-attacker.test/media/avatars/foreign/1/256.png"
                },
                256,
                CancellationToken.None));
        Equal(AvatarMediaFailureCategory.InvalidImage, foreignMedia.Category,
            "Une URL média externe ne doit jamais atteindre le HttpClient authentifié Atlas.");

        await ValidateMediaErrorAsync(HttpStatusCode.RequestEntityTooLarge, "AvatarTooLarge", AvatarMediaFailureCategory.AvatarTooLarge);
        await ValidateMediaErrorAsync(HttpStatusCode.BadRequest, "InvalidImage", AvatarMediaFailureCategory.InvalidImage);
        await ValidateMediaErrorAsync(HttpStatusCode.BadRequest, "UnsupportedFormat", AvatarMediaFailureCategory.UnsupportedFormat);
        await ValidateMediaErrorAsync(HttpStatusCode.BadRequest, "InvalidDimensions", AvatarMediaFailureCategory.InvalidDimensions);
        await ValidateMediaErrorAsync(HttpStatusCode.BadRequest, "InvalidCrop", AvatarMediaFailureCategory.InvalidCrop);
        await ValidateMediaErrorAsync(HttpStatusCode.Conflict, "UploadInProgress", AvatarMediaFailureCategory.UploadInProgress);
        await ValidateMediaErrorAsync(HttpStatusCode.TooManyRequests, "RateLimited", AvatarMediaFailureCategory.RateLimited);
        await ValidateMediaErrorAsync(HttpStatusCode.UnprocessableEntity, "ProcessingFailed", AvatarMediaFailureCategory.ProcessingFailed);
        await ValidateMediaErrorAsync(HttpStatusCode.ServiceUnavailable, "StorageFailed", AvatarMediaFailureCategory.StorageFailed);
        await ValidateMediaErrorAsync(HttpStatusCode.Unauthorized, "Unauthorized", AvatarMediaFailureCategory.Unauthorized);
        await ValidateMediaErrorAsync(HttpStatusCode.NotFound, "NotFound", AvatarMediaFailureCategory.BackendUnavailable);
    }

    private static async Task ValidateMediaErrorAsync(
        HttpStatusCode status,
        string code,
        AvatarMediaFailureCategory expected)
    {
        Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> responses = new();
        responses.Enqueue((_, _) => Task.FromResult(Json(status, new { code })));
        using HttpClient http = new(new QueueHttpHandler(responses));
        AvatarMediaClient client = new(http, ApiBase);
        AvatarMediaException exception = await ExpectAsync<AvatarMediaException>(() =>
            client.UploadAvatarAsync(
                new AvatarUploadRequest(
                    CreatePng(2, 2),
                    "image/png",
                    new AvatarNormalizedCrop(0, 0, 1)),
                null,
                CancellationToken.None));
        Equal(expected, exception.Category, $"La catégorie {code} est instable.");
    }

    private static void ValidateCropGeometry()
    {
        AvatarCropLayout landscape = AvatarCropGeometry.Calculate(1600, 900, 1, 0, 0);
        Near(0.21875, landscape.Crop.X, 0.000001, "Le crop paysage doit être centré horizontalement.");
        Near(0, landscape.Crop.Y, 0.000001, "Le crop paysage doit toucher le bord vertical.");
        Near(1, landscape.Crop.Size, 0.000001, "Le zoom minimum doit utiliser le petit côté complet.");

        AvatarCropLayout portrait = AvatarCropGeometry.Calculate(900, 1600, 1, 0, 0);
        Near(0, portrait.Crop.X, 0.000001, "Le crop portrait doit toucher le bord horizontal.");
        Near(0.21875, portrait.Crop.Y, 0.000001, "Le crop portrait doit être centré verticalement.");
        AvatarCropLayout square = AvatarCropGeometry.Calculate(1000, 1000, 1, 0, 0);
        Equal(new AvatarNormalizedCrop(0, 0, 1), square.Crop, "Le crop carré minimal doit couvrir l'image.");

        double maximum = AvatarCropGeometry.GetMaximumZoom(2048, 1024);
        Near(2.4, maximum, 0.000001, "Le zoom maximal produit doit être plafonné.");
        AvatarCropLayout edge = AvatarCropGeometry.Calculate(2048, 1024, 99, 10000, -10000);
        Near(maximum, edge.Zoom, 0.000001, "Le zoom doit être borné.");
        Near(edge.MaximumOffsetX, edge.OffsetX, 0.000001, "Le déplacement droit doit être borné.");
        Near(-edge.MaximumOffsetY, edge.OffsetY, 0.000001, "Le déplacement haut doit être borné.");
        double pixelX = edge.Crop.X * 2048;
        double pixelY = edge.Crop.Y * 1024;
        double pixelSize = edge.Crop.Size * 1024;
        True(pixelX >= -0.001 && pixelY >= -0.001
            && pixelX + pixelSize <= 2048.001
            && pixelY + pixelSize <= 1024.001,
            "Un crop au bord doit rester intégralement dans l'image orientée.");

        AvatarCropLayout rotated = AvatarCropGeometry.Calculate(900, 1600, 1.5, 31, -47);
        AvatarCropLayout equivalentPortrait = AvatarCropGeometry.Calculate(900, 1600, 1.5, 31, -47);
        Equal(equivalentPortrait.Crop, rotated.Crop, "Le crop doit être calculé dans l'espace orienté.");
    }

    private static async Task ValidateWpfDecodeAndSelectionAsync()
    {
        await RunStaAsync(async () =>
        {
            ValidateOrientationTransforms();
            string root = NewRoot("selection");
            try
            {
                Directory.CreateDirectory(root);
                string pngPath = Path.Combine(root, "avatar.png");
                string jpegPath = Path.Combine(root, "avatar.jpg");
                string webpPath = Path.Combine(root, "avatar.webp");
                await File.WriteAllBytesAsync(pngPath, CreatePng(32, 24));
                await File.WriteAllBytesAsync(jpegPath, CreateJpegWithOrientation(32, 24, 1));
                await File.WriteAllBytesAsync(webpPath, CreateWebp(32, 24));

                Equal("image/png", (await SelectAsync(pngPath))!.ContentType, "PNG doit être sélectionnable.");
                Equal("image/jpeg", (await SelectAsync(jpegPath))!.ContentType, "JPEG doit être sélectionnable.");
                try
                {
                    Equal("image/webp", (await SelectAsync(webpPath))!.ContentType, "WebP doit être sélectionnable si WIC est disponible.");
                }
                catch (AvatarSelectionException exception) when (
                    exception.Category == AvatarSelectionFailureCategory.InvalidImage)
                {
                    Console.WriteLine("WebP WIC indisponible sur cet hôte : rejet UX propre validé, codec Windows à valider sur la cible.");
                }

                string tooLarge = Path.Combine(root, "large.png");
                await using (FileStream stream = new(tooLarge, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(AvatarMediaClient.MaximumUploadBytes + 1);
                }
                Equal(AvatarSelectionFailureCategory.TooLarge,
                    (await ExpectAsync<AvatarSelectionException>(() => SelectAsync(tooLarge)))!.Category,
                    "La limite locale 8 Mio doit être appliquée.");

                string unsupported = Path.Combine(root, "avatar.gif");
                await File.WriteAllBytesAsync(unsupported, [0x47, 0x49, 0x46]);
                Equal(AvatarSelectionFailureCategory.UnsupportedFormat,
                    (await ExpectAsync<AvatarSelectionException>(() => SelectAsync(unsupported)))!.Category,
                    "Une extension non autorisée doit être refusée.");

                string invalid = Path.Combine(root, "invalid.png");
                await File.WriteAllBytesAsync(invalid, [1, 2, 3, 4]);
                Equal(AvatarSelectionFailureCategory.InvalidImage,
                    (await ExpectAsync<AvatarSelectionException>(() => SelectAsync(invalid)))!.Category,
                    "Une image indécodable doit être refusée.");

                string removed = Path.Combine(root, "removed.jpg");
                Equal(AvatarSelectionFailureCategory.FileUnavailable,
                    (await ExpectAsync<AvatarSelectionException>(() => SelectAsync(removed)))!.Category,
                    "Un fichier supprimé après sélection doit être signalé proprement.");
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    private static void ValidateOrientationTransforms()
    {
        WriteableBitmap source = CreateCornerBitmap(2, 3);
        BitmapSource normal = AvatarWpfImageDecoder.ApplyExifOrientation(source, 1);
        BitmapSource rotated180 = AvatarWpfImageDecoder.ApplyExifOrientation(source, 3);
        BitmapSource rotated90 = AvatarWpfImageDecoder.ApplyExifOrientation(source, 6);
        BitmapSource rotated270 = AvatarWpfImageDecoder.ApplyExifOrientation(source, 8);
        Equal((2, 3), (normal.PixelWidth, normal.PixelHeight), "EXIF 1 doit conserver les dimensions.");
        Equal((2, 3), (rotated180.PixelWidth, rotated180.PixelHeight), "EXIF 3 doit conserver les dimensions.");
        Equal((3, 2), (rotated90.PixelWidth, rotated90.PixelHeight), "EXIF 6 doit échanger les dimensions.");
        Equal((3, 2), (rotated270.PixelWidth, rotated270.PixelHeight), "EXIF 8 doit échanger les dimensions.");
        Equal(ReadPixel(source, 0, 0), ReadPixel(normal, 0, 0),
            "EXIF 1 doit conserver le pixel supérieur gauche.");
        Equal(ReadPixel(source, 1, 2), ReadPixel(rotated180, 0, 0),
            "EXIF 3 doit appliquer une rotation réelle de 180 degrés.");
        Equal(ReadPixel(source, 0, 2), ReadPixel(rotated90, 0, 0),
            "EXIF 6 doit appliquer une rotation réelle de 90 degrés.");
        Equal(ReadPixel(source, 1, 0), ReadPixel(rotated270, 0, 0),
            "EXIF 8 doit appliquer une rotation réelle de 270 degrés.");
        Equal(ReadPixel(source, 1, 0), ReadPixel(rotated90, 2, 1),
            "EXIF 6 doit conserver le coin opposé après rotation.");
        Equal(ReadPixel(source, 0, 2), ReadPixel(rotated270, 2, 1),
            "EXIF 8 doit conserver le coin opposé après rotation.");

        byte[] jpeg = CreateJpegWithOrientation(40, 24, 6);
        AvatarPreviewImage decoded = AvatarWpfImageDecoder.DecodePreview(jpeg, "image/jpeg");
        Equal((24, 40), (decoded.OrientedPixelWidth, decoded.OrientedPixelHeight),
            "La preview JPEG doit appliquer EXIF 6 comme le serveur.");
        Equal((ushort)6, decoded.ExifOrientation, "L'orientation EXIF doit être caractérisée.");
    }

    private static async Task<AvatarPreviewImage?> SelectAsync(string path)
    {
        AvatarFileSelectionService selection = new(new FixedAvatarFilePicker(path));
        return await selection.PickAndLoadAsync(CancellationToken.None);
    }

    private static async Task ValidateCacheAsync()
    {
        byte[] png = CreatePng(16, 16);
        string root = NewRoot("cache");
        using CancellationTokenSource lifetime = new();
        try
        {
            AvatarDescriptor descriptor = Descriptor(1);
            StubAvatarMediaClient network = new() { DownloadBytes = png };
            using (AvatarImageCache cache = new(network, root, lifetime.Token))
            {
                BitmapSource? first = await cache.GetAsync(descriptor, 128, CancellationToken.None);
                True(first is { IsFrozen: true }, "Un miss cache doit publier un BitmapSource figé.");
                Equal(1, network.DownloadCalls, "Un miss doit télécharger une seule fois.");
                BitmapSource? memory = await cache.GetAsync(descriptor, 128, CancellationToken.None);
                True(ReferenceEquals(first, memory), "Un hit mémoire doit réutiliser l'image figée.");
                Equal(1, network.DownloadCalls, "Un hit mémoire ne doit pas retélécharger.");
            }

            StubAvatarMediaClient diskNetwork = new() { DownloadBytes = png };
            using (AvatarImageCache diskCache = new(diskNetwork, root, lifetime.Token))
            {
                BitmapSource? disk = await diskCache.GetAsync(descriptor, 128, CancellationToken.None);
                True(disk is not null, "Un hit disque doit se décoder.");
                Equal(0, diskNetwork.DownloadCalls, "Une URL versionnée en cache disque ne doit pas utiliser le réseau.");
                string cacheFile = Directory.EnumerateFiles(root, "*.png").Single();
                using FileStream exclusive = new(cacheFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                True(exclusive.Length > 0, "BitmapCacheOption.OnLoad doit libérer le fichier de cache.");
            }

            string dedupeRoot = NewRoot("cache-dedupe");
            try
            {
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                StubAvatarMediaClient delayed = new()
                {
                    DownloadBytes = png,
                    DownloadGate = release.Task
                };
                using AvatarImageCache cache = new(delayed, dedupeRoot, lifetime.Token);
                Task<BitmapSource?> one = cache.GetAsync(descriptor, 64, CancellationToken.None);
                Task<BitmapSource?> two = cache.GetAsync(descriptor, 64, CancellationToken.None);
                Task<BitmapSource?> accountVariant = cache.GetAsync(descriptor, 256, CancellationToken.None);
                await delayed.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (delayed.DownloadCalls < 2 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10);
                }
                Equal(2, delayed.DownloadCalls,
                    "Shell/Profile doivent partager 64 px tandis que Compte conserve sa variante 256 px.");
                release.TrySetResult();
                await Task.WhenAll(one, two, accountVariant);
                True(ReferenceEquals(one.Result, two.Result), "La déduplication doit publier la même image.");
                False(ReferenceEquals(one.Result, accountVariant.Result),
                    "Deux tailles doivent conserver des BitmapSource distincts.");
            }
            finally
            {
                TryDelete(dedupeRoot);
            }

            string corruptRoot = NewRoot("cache-corrupt");
            try
            {
                Directory.CreateDirectory(corruptRoot);
                string corruptPath = CachePath(corruptRoot, descriptor, 32);
                await File.WriteAllBytesAsync(corruptPath, [1, 2, 3]);
                StubAvatarMediaClient redownload = new() { DownloadBytes = png };
                using AvatarImageCache cache = new(redownload, corruptRoot, lifetime.Token);
                True(await cache.GetAsync(descriptor, 32, CancellationToken.None) is not null,
                    "Une entrée corrompue doit être supprimée puis retéléchargée.");
                Equal(1, redownload.DownloadCalls, "Une corruption ne doit provoquer qu'un nouveau téléchargement.");
            }
            finally
            {
                TryDelete(corruptRoot);
            }

            string statusRoot = NewRoot("cache-status");
            try
            {
                int unauthorized = 0;
                StubAvatarMediaClient missing = new() { DownloadStatus = AvatarMediaDownloadStatus.NotFound };
                using (AvatarImageCache cache = new(missing, statusRoot, lifetime.Token))
                {
                    True(await cache.GetAsync(descriptor, 32, CancellationToken.None) is null,
                        "404 média doit produire le fallback.");
                }
                StubAvatarMediaClient rejected = new() { DownloadStatus = AvatarMediaDownloadStatus.Unauthorized };
                using (AvatarImageCache cache = new(rejected, statusRoot, lifetime.Token, () => unauthorized++))
                {
                    True(await cache.GetAsync(descriptor with { Version = 2 }, 32, CancellationToken.None) is null,
                        "401 média doit produire le fallback.");
                }
                Equal(1, unauthorized, "Le cache doit déléguer exactement une invalidation 401.");

                StubAvatarMediaClient networkFailure = new()
                {
                    DownloadFailure = new AvatarMediaException(AvatarMediaFailureCategory.Network)
                };
                using (AvatarImageCache cache = new(networkFailure, statusRoot, lifetime.Token))
                {
                    True(await cache.GetAsync(descriptor with { Version = 3 }, 64, CancellationToken.None) is null,
                        "Une erreur réseau média doit rester décorative et revenir au fallback.");
                }
            }
            finally
            {
                TryDelete(statusRoot);
            }

            string versionRoot = NewRoot("cache-version");
            try
            {
                StubAvatarMediaClient versions = new() { DownloadBytes = png };
                using AvatarImageCache cache = new(versions, versionRoot, lifetime.Token);
                await cache.GetAsync(descriptor, 32, CancellationToken.None);
                await cache.GetAsync(descriptor with { Version = 2 }, 32, CancellationToken.None);
                await cache.GetAsync(descriptor with { Version = 2 }, 64, CancellationToken.None);
                Equal(3, versions.DownloadCalls, "Version et taille doivent produire des clés distinctes.");
                string[] names = Directory.GetFiles(versionRoot, "*.png").Select(Path.GetFileName).ToArray()!;
                True(names.Length == 3
                    && names.All(name => name!.Length == 68)
                    && names.All(name => !name.Contains("Dono", StringComparison.OrdinalIgnoreCase)),
                    "Les clés disque doivent être des SHA-256 sans identité utilisateur.");
            }
            finally
            {
                TryDelete(versionRoot);
            }

            string trimRoot = NewRoot("cache-trim");
            try
            {
                Directory.CreateDirectory(trimRoot);
                byte[] megabyte = new byte[1024 * 1024];
                for (int index = 0; index < 65; index++)
                {
                    string path = Path.Combine(trimRoot, index.ToString("D64") + ".png");
                    await File.WriteAllBytesAsync(path, megabyte);
                    File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(-100 + index));
                }
                StubAvatarMediaClient trimNetwork = new() { DownloadBytes = png };
                using AvatarImageCache cache = new(trimNetwork, trimRoot, lifetime.Token);
                await cache.GetAsync(descriptor, 128, CancellationToken.None);
                long total = Directory.EnumerateFiles(trimRoot, "*.png")
                    .Sum(path => new FileInfo(path).Length);
                True(total <= AvatarImageCache.MaximumDiskBytes, "Le cache disque doit rester sous 64 Mio.");
            }
            finally
            {
                TryDelete(trimRoot);
            }
        }
        finally
        {
            lifetime.Cancel();
            TryDelete(root);
        }
    }

    private static async Task ValidateCoordinatorAsync()
    {
        string root = NewRoot("coordinator");
        using CancellationTokenSource lifetime = new();
        FakeLauncherAuthService authentication = new()
        {
            Session = FakeLauncherAuthService.CreateSession("AvatarUser", "avatar@example.test"),
            RestoreResult = true,
            EnsureFreshHandler = _ => Task.FromResult(true)
        };
        using LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
        Equal(LauncherSessionRestoreStatus.Restored,
            (await session.RestoreOnceAsync()).Status,
            "Le test coordinateur exige une session restaurée.");
        using LauncherOperationCoordinator operations = new();
        StubAvatarMediaClient media = new()
        {
            ProfileResult = ProfileResult(avatar: null, supports: true),
            DownloadBytes = CreatePng(8, 8)
        };
        using AvatarImageCache cache = new(media, root, lifetime.Token);
        List<string> logs = [];
        using LauncherAccountCoordinator coordinator = new(
            session,
            operations,
            media,
            cache,
            () => authentication.Session?.Profile,
            logs.Add);
        List<AccountRuntimeSnapshot> snapshots = [];
        coordinator.SnapshotChanged += (_, e) => snapshots.Add(e.Snapshot);
        try
        {
            TaskCompletionSource profileRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            media.ResetProfileGate(profileRelease.Task);
            AccountActionStartResult refresh = coordinator.TryRefresh();
            True(refresh.IsStarted, "Le premier rafraîchissement doit démarrer.");
            await media.ProfileEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(AccountActionStartStatus.Busy, coordinator.TryRefresh().Status,
                "Un second rafraîchissement doit être refusé immédiatement.");
            profileRelease.TrySetResult();
            Equal(AccountActionCompletionStatus.Succeeded, (await refresh.Completion!).Status,
                "Le profil moderne doit devenir disponible.");
            media.ResetProfileGate(gate: null);
            Equal(AvatarBackendAvailability.Available, coordinator.CurrentSnapshot.AvatarAvailability,
                "La capacité avatar doit devenir explicite.");

            TaskCompletionSource uploadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            media.UploadGate = uploadRelease.Task;
            media.UploadResult = Descriptor(9);
            AccountActionStartResult upload = coordinator.TryUpload(new AvatarUploadRequest(
                CreatePng(4, 4),
                "image/png",
                new AvatarNormalizedCrop(0, 0, 1)));
            True(upload.IsStarted, "Le premier upload doit démarrer.");
            await media.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(AccountActionStartStatus.Busy,
                coordinator.TryUpload(new AvatarUploadRequest(CreatePng(2, 2), "image/png", new AvatarNormalizedCrop(0, 0, 1))).Status,
                "Un double upload doit être refusé sans file d'attente.");
            media.ProfileResult = ProfileResult(Descriptor(10), supports: true);
            True(coordinator.CancelUploadFromUser(), "L'upload doit être annulable par l'utilisateur.");
            uploadRelease.TrySetResult();
            AccountActionCompletion cancelled = await upload.Completion!;
            Equal(AccountActionCompletionStatus.Cancelled, cancelled.Status,
                "L'annulation ambiguë doit rester catégorisée Cancelled.");
            Equal((ulong)10, coordinator.CurrentSnapshot.Avatar?.Version,
                "Après annulation, l'état serveur rafraîchi doit rester autoritaire.");

            media.ResetDeleteGate();
            AccountActionStartResult delete = coordinator.TryDelete();
            True(delete.IsStarted, "La suppression doit démarrer.");
            await media.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(AccountActionStartStatus.Busy, coordinator.TryDelete().Status,
                "Une double suppression doit être refusée.");
            media.ReleaseDelete();
            Equal(AccountActionCompletionStatus.Succeeded, (await delete.Completion!).Status,
                "La suppression doit réussir.");
            True(coordinator.CurrentSnapshot.Avatar is null, "Le succès DELETE doit revenir au fallback.");

            media.ProfileFailure = new AvatarMediaException(AvatarMediaFailureCategory.BackendUnavailable, HttpStatusCode.NotFound);
            AccountActionStartResult unavailable = coordinator.TryRefresh();
            Equal(AccountActionCompletionStatus.BackendUnavailable, (await unavailable.Completion!).Status,
                "Un ancien backend doit être indisponible sans casser le compte.");
            Equal(AvatarBackendAvailability.Unavailable, coordinator.CurrentSnapshot.AvatarAvailability,
                "Le rollout production doit désactiver proprement la modification.");
            media.ProfileFailure = new JsonException("contenu sensible de test");
            AccountActionStartResult malformed = coordinator.TryRefresh();
            Equal(AccountActionCompletionStatus.Failed, (await malformed.Completion!).Status,
                "Une réponse profil inattendue doit être observée sans laisser le chargement actif.");
            Equal(AccountLoadingState.Loaded, coordinator.CurrentSnapshot.LoadingState,
                "Une réponse inattendue doit revenir à un état Compte stable.");
            Equal(AccountAvatarErrorCategory.Unknown, coordinator.CurrentSnapshot.ErrorCategory,
                "Une réponse inattendue doit utiliser une catégorie stable sans exception brute.");
            False(logs.Any(line => line.Contains("token", StringComparison.OrdinalIgnoreCase)
                || line.Contains("avatar@example.test", StringComparison.OrdinalIgnoreCase)
                || line.Contains("contenu sensible", StringComparison.OrdinalIgnoreCase)),
                "Les logs avatar ne doivent contenir ni token, ni e-mail, ni contenu d'exception.");
            True(snapshots.Zip(snapshots.Skip(1), (previous, current) =>
                    current.Sequence > previous.Sequence).All(inOrder => inOrder),
                "Les snapshots Compte doivent être publiés dans un ordre strictement croissant.");

            int snapshotCount = snapshots.Count;
            coordinator.Dispose();
            media.ProfileFailure = null;
            media.RaiseLateCompletion();
            await Task.Delay(30);
            Equal(snapshotCount, snapshots.Count, "Aucun snapshot tardif ne doit atteindre la présentation après Dispose.");
        }
        finally
        {
            lifetime.Cancel();
            TryDelete(root);
        }
    }

    private static async Task ValidateLifecycleCancellationAsync()
    {
        string downloadRoot = NewRoot("lifecycle-download");
        using (CancellationTokenSource lifetime = new())
        {
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StubAvatarMediaClient media = new()
            {
                DownloadBytes = CreatePng(8, 8),
                DownloadGate = release.Task
            };
            using AvatarImageCache cache = new(media, downloadRoot, lifetime.Token);
            Task<BitmapSource?> download = cache.GetAsync(Descriptor(), 128, CancellationToken.None);
            await media.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.Cancel();
            True(await download.WaitAsync(TimeSpan.FromSeconds(5)) is null,
                "Fermer pendant le téléchargement média doit terminer par le fallback.");
            release.TrySetResult();
        }
        TryDelete(downloadRoot);

        await ValidateMutationShutdownAsync(isUpload: true);
        await ValidateMutationShutdownAsync(isUpload: false);
    }

    private static async Task ValidateMutationShutdownAsync(bool isUpload)
    {
        string root = NewRoot(isUpload ? "lifecycle-upload" : "lifecycle-delete");
        using CancellationTokenSource lifetime = new();
        FakeLauncherAuthService authentication = new()
        {
            Session = FakeLauncherAuthService.CreateSession("LifecycleUser", "lifecycle@example.test"),
            RestoreResult = true,
            EnsureFreshHandler = _ => Task.FromResult(true)
        };
        using LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
        _ = await session.RestoreOnceAsync();
        using LauncherOperationCoordinator operations = new();
        StubAvatarMediaClient media = new()
        {
            ProfileResult = ProfileResult(isUpload ? null : Descriptor(4), supports: true),
            DownloadBytes = CreatePng(8, 8)
        };
        using AvatarImageCache cache = new(media, root, lifetime.Token);
        using LauncherAccountCoordinator account = new(
            session,
            operations,
            media,
            cache,
            () => authentication.Session?.Profile,
            _ => { });
        AccountActionStartResult refresh = account.TryRefresh();
        _ = await refresh.Completion!;
        AccountActionStartResult started;
        if (isUpload)
        {
            media.UploadGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
            started = account.TryUpload(new AvatarUploadRequest(
                CreatePng(4, 4),
                "image/png",
                new AvatarNormalizedCrop(0, 0, 1)));
            await media.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            media.ResetDeleteGate();
            started = account.TryDelete();
            await media.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            False(operations.CancelFromUser(),
                "La suppression ne doit pas être annulable depuis l'interface.");
        }

        True(started.IsStarted, "La mutation lifecycle doit démarrer.");
        operations.CancelForShutdown();
        account.BeginShutdown();
        AccountActionCompletion completion = await started.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(AccountActionCompletionStatus.Cancelled, completion.Status,
            "La fermeture doit interrompre toute mutation avatar.");
        True(await account.WaitForIdleAsync(TimeSpan.FromSeconds(2)),
            "La fermeture doit observer toutes les tâches avatar.");
        TryDelete(root);
    }

    internal static byte[] CreatePng(int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(42, 169, 255, 255));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static byte[] CreateWebp(int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(230, 181, 82, 255));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Webp, 90);
        return data.ToArray();
    }

    internal static AvatarDescriptor Descriptor(ulong version = 1)
    {
        Guid id = Guid.Parse("7d4cc6fa-9c8e-4a3d-a5e8-28ff4c0d9f17");
        string prefix = $"/media/avatars/{id:N}/{version}";
        return new AvatarDescriptor(
            id,
            version,
            $"{prefix}/32.png",
            $"{prefix}/64.png",
            $"{prefix}/128.png",
            $"{prefix}/256.png");
    }

    internal static AvatarProfileReadResult ProfileResult(AvatarDescriptor? avatar, bool supports)
    {
        return new AvatarProfileReadResult(
            new LauncherProfile(
                1,
                "AvatarUser",
                "avatar@example.test",
                true,
                "gold",
                false,
                false,
                75,
                avatar),
            supports);
    }

    internal static string NewRoot(string suffix) => Path.Combine(
        Path.GetTempPath(),
        "AtlasAvatarClientTests",
        suffix + "-" + Guid.NewGuid().ToString("N"));

    internal static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }

    internal static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void False(bool value, string message) => True(!value, message);

    internal static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }

    internal static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }

    internal static async Task<T> ExpectAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Exception attendue absente : {typeof(T).Name}.");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
    }

    private static HttpResponseMessage Png(HttpStatusCode status, byte[] bytes)
    {
        ByteArrayContent content = new(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(status) { Content = content };
    }

    private static string CachePath(string root, AvatarDescriptor descriptor, int size)
    {
        string canonical = $"{descriptor.AvatarId:N}|{descriptor.Version}|{size}";
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return Path.Combine(root, name + ".png");
    }

    private static byte[] CreateJpegWithOrientation(int width, int height, ushort orientation)
    {
        WriteableBitmap bitmap = CreateCornerBitmap(width, height);
        BitmapMetadata metadata = new("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        JpegBitmapEncoder encoder = new() { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static WriteableBitmap CreateCornerBitmap(int width, int height)
    {
        WriteableBitmap bitmap = new(width, height, 96, 96, PixelFormats.Bgra32, null);
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                pixels[offset] = (byte)(x * 31 + y * 7);
                pixels[offset + 1] = (byte)(y * 53 + 20);
                pixels[offset + 2] = (byte)(x * 71 + 40);
                pixels[offset + 3] = 255;
            }
        }
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static (byte B, byte G, byte R, byte A) ReadPixel(
        BitmapSource source,
        int x,
        int y)
    {
        BitmapSource readable = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        readable.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    private static async Task RunStaAsync(Func<Task> action)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "AtlasAvatarImageTests"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed record HttpRequestMessageSnapshot(
        HttpMethod Method,
        Uri Uri,
        string? ContentType,
        string Body);

    private sealed class QueueHttpHandler(
        Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> responses)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("Réponse HTTP de test manquante.");
            }
            return responses.Dequeue()(request, cancellationToken);
        }
    }

    private sealed class RecordingProgress : IProgress<AvatarUploadTransferProgress>
    {
        internal List<AvatarUploadTransferProgress> Values { get; } = [];
        public void Report(AvatarUploadTransferProgress value) => Values.Add(value);
    }

    private sealed class FixedAvatarFilePicker(string path) : IAvatarFilePicker
    {
        public string? PickImagePath() => path;
    }
}

internal sealed class StubAvatarMediaClient : IAvatarMediaClient
{
    private TaskCompletionSource _profileEntered = NewSignal();
    private TaskCompletionSource _downloadEntered = NewSignal();
    private TaskCompletionSource _uploadEntered = NewSignal();
    private TaskCompletionSource _deleteEntered = NewSignal();
    private TaskCompletionSource _deleteRelease = NewSignal();

    internal AvatarProfileReadResult ProfileResult { get; set; } =
        AccountAvatarClientTests.ProfileResult(null, supports: true);
    internal Exception? ProfileFailure { get; set; }
    internal Task? ProfileGate { get; set; }
    internal byte[]? DownloadBytes { get; set; }
    internal AvatarMediaDownloadStatus DownloadStatus { get; set; } = AvatarMediaDownloadStatus.Success;
    internal Exception? DownloadFailure { get; set; }
    internal Task? DownloadGate { get; set; }
    internal Task? UploadGate { get; set; }
    internal AvatarDescriptor UploadResult { get; set; } = AccountAvatarClientTests.Descriptor(2);
    internal int DownloadCalls { get; private set; }
    internal int UploadCalls { get; private set; }
    internal int DeleteCalls { get; private set; }
    internal TaskCompletionSource DownloadEntered => _downloadEntered;
    internal TaskCompletionSource ProfileEntered => _profileEntered;
    internal TaskCompletionSource UploadEntered => _uploadEntered;
    internal TaskCompletionSource DeleteEntered => _deleteEntered;

    public async Task<AvatarProfileReadResult> GetProfileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _profileEntered.TrySetResult();
        if (ProfileGate is not null)
        {
            await ProfileGate.WaitAsync(cancellationToken);
        }
        if (ProfileFailure is not null)
        {
            throw ProfileFailure;
        }
        return ProfileResult;
    }

    public async Task<AvatarDescriptor> UploadAvatarAsync(
        AvatarUploadRequest upload,
        IProgress<AvatarUploadTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        UploadCalls++;
        _uploadEntered.TrySetResult();
        progress?.Report(new AvatarUploadTransferProgress(AvatarUploadPhase.Preparing, 0, upload.OriginalBytes.Length));
        progress?.Report(new AvatarUploadTransferProgress(AvatarUploadPhase.Sending, upload.OriginalBytes.Length / 2, upload.OriginalBytes.Length));
        if (UploadGate is not null)
        {
            await UploadGate.WaitAsync(cancellationToken);
        }
        progress?.Report(new AvatarUploadTransferProgress(AvatarUploadPhase.Processing, upload.OriginalBytes.Length, upload.OriginalBytes.Length));
        return UploadResult;
    }

    public async Task DeleteAvatarAsync(CancellationToken cancellationToken)
    {
        DeleteCalls++;
        _deleteEntered.TrySetResult();
        await _deleteRelease.Task.WaitAsync(cancellationToken);
    }

    public async Task<AvatarMediaDownloadResult> DownloadAvatarAsync(
        AvatarDescriptor descriptor,
        int size,
        CancellationToken cancellationToken)
    {
        DownloadCalls++;
        _downloadEntered.TrySetResult();
        if (DownloadGate is not null)
        {
            await DownloadGate.WaitAsync(cancellationToken);
        }
        if (DownloadFailure is not null)
        {
            throw DownloadFailure;
        }
        return DownloadStatus switch
        {
            AvatarMediaDownloadStatus.Success => AvatarMediaDownloadResult.Success(
                DownloadBytes ?? AccountAvatarClientTests.CreatePng(size, size)),
            AvatarMediaDownloadStatus.NotFound => AvatarMediaDownloadResult.NotFound,
            _ => AvatarMediaDownloadResult.Unauthorized
        };
    }

    internal void ResetDeleteGate()
    {
        _deleteEntered = NewSignal();
        _deleteRelease = NewSignal();
    }

    internal void ReleaseDelete() => _deleteRelease.TrySetResult();

    internal void ResetProfileGate(Task? gate)
    {
        _profileEntered = NewSignal();
        ProfileGate = gate;
    }

    internal void RaiseLateCompletion()
    {
        _downloadEntered.TrySetResult();
        _uploadEntered.TrySetResult();
        _deleteEntered.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
