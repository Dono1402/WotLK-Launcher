using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.RateLimiting;
using WotLK.Launcher.Server;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

LauncherServerOptions options = new();
builder.Configuration.GetSection("LauncherServer").Bind(options);
options.ConnectionString = FirstNonEmpty(
    Environment.GetEnvironmentVariable("WOTLK_LAUNCHER_DB"),
    builder.Configuration["LauncherServer:ConnectionString"]);
options.HermesSharedSecret = FirstNonEmpty(
    Environment.GetEnvironmentVariable("WOTLK_HERMES_SHARED_SECRET"),
    builder.Configuration["LauncherServer:HermesSharedSecret"]);

if (string.IsNullOrWhiteSpace(options.ConnectionString))
    throw new InvalidOperationException("LauncherServer:ConnectionString est obligatoire.");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<LauncherDatabase>();
builder.Services.AddSingleton<AtlasStatusService>();
builder.Services.AddHttpClient<HermesTicketClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

WebApplication app = builder.Build();
app.UseRateLimiter();

LauncherDatabase database = app.Services.GetRequiredService<LauncherDatabase>();
await database.InitializeAsync();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/v1/accounts", async (
    RegisterRequest request,
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    string? validation = ValidateRegistration(request);
    if (validation is not null)
        return Results.BadRequest(new { error = validation });

    try
    {
        string? deviceName = context.Request.Headers["X-Atlas-Device"].FirstOrDefault();
        AuthResponse response = await db.RegisterAsync(
            request, deviceName, cancellationToken);
        return Results.Ok(response);
    }
    catch (DuplicateNameException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
}).RequireRateLimiting("auth");

app.MapPost("/api/v1/auth/login", async (
    LoginRequest request,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthResponse? response = await db.LoginAsync(request, cancellationToken);
    return response is null
        ? Results.Unauthorized()
        : Results.Ok(response);
}).RequireRateLimiting("auth");

app.MapPost("/api/v1/auth/refresh", async (
    RefreshRequest request,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthResponse? response = await db.RefreshAsync(request.RefreshToken, cancellationToken);
    return response is null
        ? Results.Unauthorized()
        : Results.Ok(response);
}).RequireRateLimiting("auth");

app.MapPost("/api/v1/auth/logout", async (
    HttpContext context,
    LauncherDatabase db,
    HermesTicketClient hermes,
    CancellationToken cancellationToken) =>
{
    string? token = ReadBearer(context);
    if (token is null)
        return Results.Unauthorized();

    AuthenticatedAccount? account =
        await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    await db.LogoutAsync(token, cancellationToken);
    try
    {
        await hermes.RevokeAsync(account.Username, cancellationToken);
    }
    catch (HttpRequestException)
    {
        // Launcher logout must still succeed if Hermes is temporarily unavailable.
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        // An internal Hermes timeout must not keep the local launcher session alive.
    }

    return Results.NoContent();
});

app.MapGet("/api/v1/me", async (
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    return account is null
        ? Results.Unauthorized()
        : Results.Ok(await db.GetProfileAsync(account.AccountId, cancellationToken));
});

app.MapPatch("/api/v1/me/email", async (
    ChangeEmailRequest request,
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();
    if (!new EmailAddressAttribute().IsValid(request.Email))
        return Results.BadRequest(new { error = "Adresse e-mail invalide." });

    try
    {
        return Results.Ok(await db.ChangeEmailAsync(
            account.AccountId, request.Email, cancellationToken));
    }
    catch (Exception ex) when (
        ex is DuplicateNameException
        || ex is MySqlConnector.MySqlException { Number: 1062 })
    {
        return Results.Conflict(new { error = "Cette adresse e-mail est déjà utilisée." });
    }
});

app.MapPost("/api/v1/me/email/resend", async (
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    return account is null
        ? Results.Unauthorized()
        : Results.Accepted(value: new
        {
            deliveryConfigured = false,
            message = "L'envoi Brevo sera activé dans la dernière étape."
        });
}).RequireRateLimiting("auth");

app.MapPost("/api/v1/me/password", async (
    ChangePasswordRequest request,
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();
    if (request.NewPassword.Length is < 10 or > 128)
        return Results.BadRequest(new { error = "Le nouveau mot de passe doit contenir entre 10 et 128 caractères." });

    bool changed = await db.ChangePasswordAsync(
        account.AccountId,
        request.CurrentPassword,
        request.NewPassword,
        cancellationToken);
    return changed ? Results.NoContent() : Results.Unauthorized();
}).RequireRateLimiting("auth");

app.MapPatch("/api/v1/me/avatar", async (
    ChangeAvatarRequest request,
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    string? avatarKey = string.IsNullOrWhiteSpace(request.AvatarKey)
        ? null
        : request.AvatarKey.Trim().ToLowerInvariant();
    string[] allowed = ["gold", "ice", "emerald", "crimson"];
    if (avatarKey is not null && !allowed.Contains(avatarKey, StringComparer.Ordinal))
        return Results.BadRequest(new { error = "Avatar inconnu." });

    return Results.Ok(await db.ChangeAvatarAsync(
        account.AccountId,
        avatarKey,
        cancellationToken));
});

app.MapGet("/api/v1/me/sessions", async (
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    string? token = ReadBearer(context);
    if (token is null)
        return Results.Unauthorized();
    AuthenticatedAccount? account = await db.AuthenticateAsync(token, cancellationToken);
    return account is null
        ? Results.Unauthorized()
        : Results.Ok(await db.ListSessionsAsync(
            account.AccountId,
            token,
            cancellationToken));
});

app.MapDelete("/api/v1/me/sessions/{sessionId}", async (
    string sessionId,
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    return await db.RevokeSessionAsync(
        account.AccountId,
        sessionId,
        cancellationToken)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapGet("/api/v1/status", async (
    HttpContext context,
    LauncherDatabase db,
    AtlasStatusService status,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    return account is null
        ? Results.Unauthorized()
        : Results.Ok(await status.GetAsync(cancellationToken));
});

app.MapGet("/api/v1/news", async (
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    LauncherNewsItem[] news =
    [
        new(
            "launcher-account",
            "Launcher",
            "Le compte Atlas arrive dans le launcher",
            "Connexion unique, profil, appareils connectés et téléchargements protégés.",
            new DateTimeOffset(2026, 7, 28, 6, 0, 0, TimeSpan.Zero)),
        new(
            "addons-catalog",
            "Addons",
            "Le catalogue Atlas s'agrandit",
            "Onze addons validés pour WotLK Classic sont disponibles par catégorie.",
            new DateTimeOffset(2026, 7, 28, 5, 0, 0, TimeSpan.Zero)),
        new(
            "french-client",
            "Client",
            "Le client français est disponible",
            "Interface, textes et voix françaises sont distribués directement par Atlas.",
            new DateTimeOffset(2026, 7, 27, 18, 0, 0, TimeSpan.Zero))
    ];
    return Results.Ok(news);
});

app.MapPost("/api/v1/game-ticket", async (
    HttpContext context,
    LauncherDatabase db,
    HermesTicketClient hermes,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    return account is null
        ? Results.Unauthorized()
        : Results.Ok(await hermes.CreateAsync(account, cancellationToken));
}).RequireRateLimiting("auth");

app.MapGet("/manifest.json", async (
    HttpContext context,
    LauncherDatabase db,
    LauncherServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    string manifestPath = Path.Combine(serverOptions.FeedRoot, "manifest.json");
    if (!File.Exists(manifestPath))
        return Results.NotFound();

    JsonNode manifest = JsonNode.Parse(
        await File.ReadAllTextAsync(manifestPath, cancellationToken))
        ?? throw new InvalidOperationException("Le manifeste du client est invalide.");
    manifest["baseUrl"] = "https://animeclub.fr/wotlk/files/";
    return Results.Text(manifest.ToJsonString(), "application/json");
});

app.MapGet("/files/{**relativePath}", async (
    string relativePath,
    HttpContext context,
    LauncherDatabase db,
    LauncherServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    string filesRoot = Path.Combine(serverOptions.FeedRoot, "files");
    string? path = ResolveUnderRoot(filesRoot, relativePath);
    return path is null || !File.Exists(path)
        ? Results.NotFound()
        : Results.File(path, "application/octet-stream", enableRangeProcessing: true);
});

app.MapGet("/addons/catalog.json", async (
    HttpContext context,
    LauncherDatabase db,
    LauncherServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    string catalogPath = Path.Combine(serverOptions.AddonRoot, "catalog.json");
    return File.Exists(catalogPath)
        ? Results.File(catalogPath, "application/json")
        : Results.NotFound();
});

app.MapGet("/addons/packages/{**relativePath}", async (
    string relativePath,
    HttpContext context,
    LauncherDatabase db,
    LauncherServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    string packagesRoot = Path.Combine(serverOptions.AddonRoot, "packages");
    string? path = ResolveUnderRoot(packagesRoot, relativePath);
    return path is null || !File.Exists(path)
        ? Results.NotFound()
        : Results.File(path, "application/zip", enableRangeProcessing: true);
});

app.Run();

static async Task<AuthenticatedAccount?> AuthenticateAsync(
    HttpContext context,
    LauncherDatabase database,
    CancellationToken cancellationToken)
{
    string? token = ReadBearer(context);
    return token is null
        ? null
        : await database.AuthenticateAsync(token, cancellationToken);
}

static string? ReadBearer(HttpContext context)
{
    string authorization = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? authorization[prefix.Length..].Trim()
        : null;
}

static string? ValidateRegistration(RegisterRequest request)
{
    if (!Regex.IsMatch(request.Username.Trim(), "^[A-Za-z0-9_]{3,20}$"))
        return "Le nom d'utilisateur doit contenir 3 à 20 lettres, chiffres ou underscores.";
    if (!new EmailAddressAttribute().IsValid(request.Email))
        return "Adresse e-mail invalide.";
    if (request.Password.Length is < 10 or > 128)
        return "Le mot de passe doit contenir entre 10 et 128 caractères.";
    return null;
}

static string? ResolveUnderRoot(string root, string relativePath)
{
    if (string.IsNullOrWhiteSpace(relativePath))
        return null;

    string fullRoot = Path.GetFullPath(root)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    string candidate = Path.GetFullPath(Path.Combine(
        fullRoot,
        relativePath.Replace('/', Path.DirectorySeparatorChar)));
    return candidate.StartsWith(fullRoot, StringComparison.Ordinal)
        ? candidate
        : null;
}

static string FirstNonEmpty(params string?[] values)
    => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

public partial class Program;
