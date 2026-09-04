using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using MySqlConnector;

namespace WotLK.Launcher.Server.Avatars;

internal static class AvatarEndpoints
{
    internal static IServiceCollection AddAtlasAvatarBackend(
        this IServiceCollection services,
        LauncherServerOptions options)
    {
        services.AddSingleton<IAvatarStorage>(_ => new LocalAvatarStorage(options.AvatarMediaRoot));
        services.AddSingleton<IAvatarRepository>(_ => new AvatarRepository(options));
        services.AddSingleton<IAvatarMutationLockProvider>(_ => new AvatarMutationLockProvider(options));
        services.AddSingleton<IAvatarImageProcessor>(_ => new SkiaAvatarImageProcessor());
        services.AddSingleton(services => new AvatarMultipartUploadReader(
            services.GetRequiredService<IAvatarStorage>()));
        services.AddSingleton(services => new AvatarApplicationService(
            services.GetRequiredService<IAvatarRepository>(),
            services.GetRequiredService<IAvatarMutationLockProvider>(),
            services.GetRequiredService<IAvatarStorage>(),
            services.GetRequiredService<IAvatarImageProcessor>(),
            services.GetRequiredService<AvatarMultipartUploadReader>(),
            services.GetRequiredService<ILogger<AvatarApplicationService>>()));
        services.AddSingleton(services => new AvatarCleanupInspector(
            services.GetRequiredService<IAvatarRepository>(),
            services.GetRequiredService<IAvatarStorage>()));
        return services;
    }

    internal static IEndpointRouteBuilder MapAtlasAvatarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/me/avatar/photo", UploadAsync);
        endpoints.MapDelete("/api/v1/me/avatar/photo", DeleteAsync);
        endpoints.MapGet("/media/avatars/{avatarId:guid}/{version:long}/{size:int}.png", GetMediaAsync);
        return endpoints;
    }

    internal static async Task<IResult> UploadAsync(
        HttpContext context,
        LauncherDatabase database,
        AvatarApplicationService avatars,
        CancellationToken cancellationToken)
    {
        AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(
            context,
            database,
            cancellationToken);
        if (account is null)
            return Results.Unauthorized();

        IHttpMaxRequestBodySizeFeature? bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
            bodySize.MaxRequestBodySize = AvatarLimits.MaximumMultipartBodyBytes;

        AvatarCommandResult result = await avatars.UploadAsync(
            account.AccountId,
            context.Request,
            cancellationToken);
        return ToHttpResult(result);
    }

    internal static async Task<IResult> DeleteAsync(
        HttpContext context,
        LauncherDatabase database,
        AvatarApplicationService avatars,
        CancellationToken cancellationToken)
    {
        AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(
            context,
            database,
            cancellationToken);
        if (account is null)
            return Results.Unauthorized();

        AvatarCommandResult result = await avatars.DeleteAsync(account.AccountId, cancellationToken);
        return ToHttpResult(result);
    }

    internal static async Task GetMediaAsync(
        Guid avatarId,
        long version,
        int size,
        HttpContext context,
        LauncherDatabase database,
        AvatarApplicationService avatars,
        ILogger<AvatarApplicationService> logger,
        CancellationToken cancellationToken)
    {
        AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(
            context,
            database,
            cancellationToken);
        if (account is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (version <= 0 || !AvatarVariantSizes.IsSupported(size))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            AvatarMediaContent? media = await avatars.OpenMediaAsync(
                avatarId,
                checked((ulong)version),
                size,
                cancellationToken);
            if (media is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await using Stream content = media.Content;
            string etag = $"\"{Convert.ToHexString(media.Metadata.Sha256).ToLowerInvariant()}\"";
            SetMediaHeaders(context.Response, media.Metadata, etag);
            if (MatchesEtag(context.Request.Headers.IfNoneMatch, etag))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                context.Response.ContentLength = null;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentLength = media.Metadata.ByteLength;
            await content.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (Exception exception) when (exception is MySqlException or InvalidOperationException or IOException)
        {
            Guid operationId = Guid.NewGuid();
            logger.LogError(
                "Avatar media operation {OperationId} failed; avatar={AvatarId}; type={ExceptionType}.",
                operationId,
                avatarId,
                exception.GetType().Name);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new AvatarApiError(
                        "ProcessingFailed",
                        "La photo est temporairement indisponible.",
                        operationId.ToString("N")),
                    cancellationToken);
            }
        }
    }

    private static IResult ToHttpResult(AvatarCommandResult result)
    {
        if (result.StatusCode == StatusCodes.Status204NoContent)
            return Results.NoContent();
        if (result.Avatar is not null)
            return Results.Ok(result.Avatar);
        return Results.Json(result.Error, statusCode: result.StatusCode);
    }

    private static void SetMediaHeaders(
        HttpResponse response,
        AvatarMediaRecord media,
        string etag)
    {
        response.ContentType = media.ContentType;
        response.Headers.CacheControl = "private, max-age=31536000, immutable";
        response.Headers.ETag = etag;
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static bool MatchesEtag(StringValues values, string etag)
        => values.SelectMany(value => value?.Split(',') ?? [])
            .Select(value => value.Trim())
            .Any(value => value == "*" || string.Equals(value, etag, StringComparison.Ordinal));
}
