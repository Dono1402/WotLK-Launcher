using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace WotLK.Launcher.Server.Avatars;

internal sealed record StagedAvatarUpload(
    string DeclaredContentType,
    NormalizedAvatarCrop Crop);

internal sealed class AvatarRequestValidationException : Exception
{
    internal AvatarRequestValidationException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    internal string Code { get; }
    internal int StatusCode { get; }
}

internal sealed class AvatarMultipartUploadReader
{
    private const int MaximumSectionCount = 8;
    private const int MaximumFieldBytes = 128;
    private readonly IAvatarStorage _storage;

    internal AvatarMultipartUploadReader(IAvatarStorage storage)
    {
        _storage = storage;
    }

    internal async Task<StagedAvatarUpload> ReadAsync(
        HttpRequest request,
        AvatarStagingHandle staging,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > AvatarLimits.MaximumMultipartBodyBytes)
            throw TooLarge();
        if (string.IsNullOrWhiteSpace(request.ContentType)
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? mediaType)
            || !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("InvalidImage", "La requete doit contenir une image multipart.");
        }

        string boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value ?? "";
        if (boundary.Length is < 1 or > 128)
            throw Invalid("InvalidImage", "La limite multipart est invalide.");

        MultipartReader reader = new(boundary, request.Body)
        {
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024,
            BodyLengthLimit = AvatarLimits.MaximumMultipartBodyBytes
        };
        string? contentType = null;
        bool imageFound = false;
        double? cropX = null;
        double? cropY = null;
        double? cropSize = null;
        int sectionCount = 0;

        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                sectionCount++;
                if (sectionCount > MaximumSectionCount)
                    throw Invalid("InvalidImage", "La requete multipart contient trop de sections.");
                if (!ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition,
                        out ContentDispositionHeaderValue? disposition)
                    || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                {
                    throw Invalid("InvalidImage", "Une section multipart est invalide.");
                }

                string name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                bool isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
                if (string.Equals(name, "image", StringComparison.Ordinal))
                {
                    if (!isFile || imageFound)
                        throw Invalid("InvalidImage", "Une seule image est acceptee.");
                    imageFound = true;
                    contentType = section.ContentType ?? "application/octet-stream";
                    try
                    {
                        await _storage.WriteOriginalAsync(staging, section.Body, cancellationToken);
                    }
                    catch (AvatarStorageException exception)
                        when (exception.Message.Contains("8 Mio", StringComparison.Ordinal))
                    {
                        throw TooLarge();
                    }
                    continue;
                }

                if (isFile)
                    throw Invalid("InvalidImage", "Champ fichier avatar inconnu.");
                string value = await ReadFieldAsync(section.Body, cancellationToken);
                switch (name)
                {
                    case "cropX":
                        cropX = ParseSingle(cropX, value, name);
                        break;
                    case "cropY":
                        cropY = ParseSingle(cropY, value, name);
                        break;
                    case "cropSize":
                        cropSize = ParseSingle(cropSize, value, name);
                        break;
                    default:
                        throw Invalid("InvalidCrop", "Champ de recadrage inconnu.");
                }
            }
        }
        catch (InvalidDataException)
        {
            throw Invalid("InvalidImage", "La requete multipart est invalide.");
        }

        if (contentType is null || !imageFound)
            throw Invalid("InvalidImage", "Le fichier image est obligatoire.");
        if (cropX is null || cropY is null || cropSize is null)
            throw Invalid("InvalidCrop", "Les coordonnees de recadrage sont obligatoires.");
        return new StagedAvatarUpload(
            contentType,
            new NormalizedAvatarCrop(cropX.Value, cropY.Value, cropSize.Value, cropSize.Value));
    }

    private static double ParseSingle(double? existing, string value, string name)
    {
        if (existing is not null
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed))
        {
            throw Invalid("InvalidCrop", $"La coordonnee {name} est invalide.");
        }
        return parsed;
    }

    private static async Task<string> ReadFieldAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] bytes = new byte[64];
        while (true)
        {
            int read = await stream.ReadAsync(bytes, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumFieldBytes)
                throw Invalid("InvalidCrop", "Une coordonnee de recadrage est trop longue.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).Trim();
    }

    private static AvatarRequestValidationException Invalid(string code, string message)
        => new(code, message, StatusCodes.Status400BadRequest);

    private static AvatarRequestValidationException TooLarge()
        => new("AvatarTooLarge", "L'image depasse la limite de 8 Mio.", StatusCodes.Status413PayloadTooLarge);
}
