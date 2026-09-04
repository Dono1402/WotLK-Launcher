using MySqlConnector;

namespace WotLK.Launcher.Server.Avatars;

internal sealed class AvatarApplicationService
{
    private readonly IAvatarRepository _repository;
    private readonly IAvatarMutationLockProvider _locks;
    private readonly IAvatarStorage _storage;
    private readonly IAvatarImageProcessor _processor;
    private readonly AvatarMultipartUploadReader _multipart;
    private readonly ILogger<AvatarApplicationService> _logger;

    internal AvatarApplicationService(
        IAvatarRepository repository,
        IAvatarMutationLockProvider locks,
        IAvatarStorage storage,
        IAvatarImageProcessor processor,
        AvatarMultipartUploadReader multipart,
        ILogger<AvatarApplicationService> logger)
    {
        _repository = repository;
        _locks = locks;
        _storage = storage;
        _processor = processor;
        _multipart = multipart;
        _logger = logger;
    }

    internal async Task<AvatarCommandResult> UploadAsync(
        uint accountId,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Guid operationId = Guid.NewGuid();
        IAvatarMutationLease? mutation;
        try
        {
            mutation = await _locks.TryAcquireAsync(accountId, cancellationToken);
        }
        catch (Exception exception) when (exception is MySqlException or InvalidOperationException)
        {
            LogFailure(operationId, accountId, "ProcessingFailed", exception);
            return AvatarCommandResult.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "ProcessingFailed",
                "La photo n'a pas pu etre traitee.",
                operationId);
        }

        if (mutation is null)
        {
            return AvatarCommandResult.Failure(
                StatusCodes.Status409Conflict,
                "UploadInProgress",
                "Une modification de photo est deja en cours.",
                operationId);
        }

        await using (mutation)
        {
            AvatarStagingHandle? staging = null;
            AvatarAssetRecord? pending = null;
            bool mediaPublished = false;
            try
            {
                staging = await _storage.BeginStagingAsync(cancellationToken);
                StagedAvatarUpload upload = await _multipart.ReadAsync(request, staging.Value, cancellationToken);
                pending = await _repository.CreatePendingAsync(accountId, cancellationToken);
                ProcessedAvatarImage processed;
                await using (Stream original = await _storage.OpenOriginalReadAsync(staging.Value, cancellationToken))
                {
                    processed = await _processor.ProcessAsync(
                        original,
                        upload.DeclaredContentType,
                        upload.Crop,
                        cancellationToken);
                }

                List<AvatarStoredVariant> storedVariants = [];
                foreach (int size in AvatarVariantSizes.All)
                {
                    byte[] png = processed.Variants[size];
                    storedVariants.Add(await _storage.WriteVariantAsync(
                        staging.Value,
                        size,
                        new MemoryStream(png, writable: false),
                        cancellationToken));
                }

                await _storage.PublishAsync(staging.Value, pending.StorageKey, cancellationToken);
                mediaPublished = true;
                AvatarPublicationResult publication = await _repository.PublishReadyAsync(
                    accountId,
                    pending,
                    storedVariants,
                    cancellationToken);
                _logger.LogInformation(
                    "Avatar operation {OperationId} published for account {AccountId} version {Version}.",
                    operationId,
                    accountId,
                    pending.Version);
                await MoveRetiredMediaToTrashAsync(publication.Retired, operationId, accountId);
                return AvatarCommandResult.Success(publication.Current);
            }
            catch (AvatarRequestValidationException exception)
            {
                await HandleFailedPendingAsync(accountId, pending, mediaPublished, operationId);
                return AvatarCommandResult.Failure(
                    exception.StatusCode,
                    exception.Code,
                    exception.Message,
                    operationId);
            }
            catch (AvatarImageValidationException exception)
            {
                await HandleFailedPendingAsync(accountId, pending, mediaPublished, operationId);
                (string code, string message) = MapImageError(exception.Code);
                return AvatarCommandResult.Failure(
                    code == "AvatarTooLarge" ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest,
                    code,
                    message,
                    operationId);
            }
            catch (Exception exception) when (
                exception is AvatarStorageException or IOException or UnauthorizedAccessException)
            {
                await HandleFailedPendingAsync(accountId, pending, mediaPublished, operationId);
                LogFailure(operationId, accountId, "StorageFailed", exception);
                return AvatarCommandResult.Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "StorageFailed",
                    "Le stockage de la photo est temporairement indisponible.",
                    operationId);
            }
            catch (BadHttpRequestException exception)
            {
                await HandleFailedPendingAsync(accountId, pending, mediaPublished, operationId);
                return AvatarCommandResult.Failure(
                    exception.StatusCode == StatusCodes.Status413PayloadTooLarge
                        ? StatusCodes.Status413PayloadTooLarge
                        : StatusCodes.Status400BadRequest,
                    exception.StatusCode == StatusCodes.Status413PayloadTooLarge
                        ? "AvatarTooLarge"
                        : "InvalidImage",
                    exception.StatusCode == StatusCodes.Status413PayloadTooLarge
                        ? "L'image depasse la limite de 25 Mio."
                        : "La requete image est invalide.",
                    operationId);
            }
            catch (Exception exception) when (exception is MySqlException or InvalidOperationException)
            {
                await HandleFailedPendingAsync(accountId, pending, mediaPublished, operationId);
                LogFailure(operationId, accountId, "ProcessingFailed", exception);
                return AvatarCommandResult.Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "ProcessingFailed",
                    "La photo n'a pas pu etre traitee.",
                    operationId);
            }
            finally
            {
                if (!mediaPublished && staging is not null)
                {
                    try
                    {
                        await _storage.DiscardStagingAsync(staging.Value, CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        LogFailure(operationId, accountId, "StagingCleanupFailed", exception);
                    }
                }
            }
        }
    }

    internal async Task<AvatarCommandResult> DeleteAsync(
        uint accountId,
        CancellationToken cancellationToken)
    {
        Guid operationId = Guid.NewGuid();
        IAvatarMutationLease? mutation;
        try
        {
            mutation = await _locks.TryAcquireAsync(accountId, cancellationToken);
        }
        catch (Exception exception) when (exception is MySqlException or InvalidOperationException)
        {
            LogFailure(operationId, accountId, "ProcessingFailed", exception);
            return AvatarCommandResult.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "ProcessingFailed",
                "La photo n'a pas pu etre supprimee.",
                operationId);
        }

        if (mutation is null)
        {
            return AvatarCommandResult.Failure(
                StatusCodes.Status409Conflict,
                "UploadInProgress",
                "Une modification de photo est deja en cours.",
                operationId);
        }

        await using (mutation)
        {
            try
            {
                AvatarDeletionResult deletion = await _repository.DeleteActiveAsync(accountId, cancellationToken);
                await MoveRetiredMediaToTrashAsync(deletion.DeletedAsset, operationId, accountId);
                return AvatarCommandResult.NoContent();
            }
            catch (Exception exception) when (exception is MySqlException or InvalidOperationException)
            {
                LogFailure(operationId, accountId, "ProcessingFailed", exception);
                return AvatarCommandResult.Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "ProcessingFailed",
                    "La photo n'a pas pu etre supprimee.",
                    operationId);
            }
        }
    }

    internal async Task<AvatarMediaContent?> OpenMediaAsync(
        Guid avatarId,
        ulong version,
        int size,
        CancellationToken cancellationToken)
    {
        if (!AvatarVariantSizes.IsSupported(size))
            return null;
        AvatarMediaRecord? media = await _repository.GetMediaAsync(
            avatarId,
            version,
            size,
            cancellationToken);
        if (media is null)
            return null;
        try
        {
            Stream stream = await _storage.OpenVariantReadAsync(
                media.StorageKey,
                size,
                cancellationToken);
            return new AvatarMediaContent(media, stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Avatar media unavailable for {AvatarId} version {Version} size {Size}; type={ExceptionType}.",
                avatarId,
                version,
                size,
                exception.GetType().Name);
            return null;
        }
    }

    private async Task HandleFailedPendingAsync(
        uint accountId,
        AvatarAssetRecord? pending,
        bool mediaPublished,
        Guid operationId)
    {
        if (pending is null || mediaPublished)
            return;
        try
        {
            await _repository.MarkPendingDeletedAsync(
                accountId,
                pending.Id,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is MySqlException or InvalidOperationException)
        {
            LogFailure(operationId, accountId, "PendingCleanupFailed", exception);
        }
    }

    private async Task MoveRetiredMediaToTrashAsync(
        AvatarAssetRecord? retired,
        Guid operationId,
        uint accountId)
    {
        if (retired is null)
            return;
        try
        {
            await _storage.MoveToTrashAsync(retired.StorageKey, CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is AvatarStorageException or IOException or UnauthorizedAccessException)
        {
            LogFailure(operationId, accountId, "DeferredMediaCleanupFailed", exception);
        }
    }

    private void LogFailure(Guid operationId, uint accountId, string category, Exception exception)
    {
        _logger.LogError(
            "Avatar operation {OperationId} failed for account {AccountId}; category={Category}; type={ExceptionType}.",
            operationId,
            accountId,
            category,
            exception.GetType().Name);
    }

    private static (string Code, string Message) MapImageError(string sourceCode)
        => sourceCode switch
        {
            "file_too_large" => ("AvatarTooLarge", "L'image depasse la limite de 25 Mio."),
            "unsupported_mime" or "unsupported_format" or "mime_mismatch" or "animated_image"
                => ("UnsupportedFormat", "Le format de l'image n'est pas pris en charge."),
            "dimensions_too_small" or "dimensions_too_large" or "pixel_count_too_large"
                => ("InvalidDimensions", "Les dimensions de l'image ne sont pas autorisees."),
            "invalid_crop" or "crop_not_square" or "crop_too_small" or "crop_out_of_bounds"
                => ("InvalidCrop", "Le recadrage de l'image est invalide."),
            _ => ("InvalidImage", "Le fichier image est invalide ou corrompu.")
        };
}
