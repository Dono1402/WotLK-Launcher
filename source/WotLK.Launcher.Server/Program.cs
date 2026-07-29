using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text;
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
options.PublicBaseUrl = FirstNonEmpty(
    Environment.GetEnvironmentVariable("WOTLK_PUBLIC_BASE_URL"),
    options.PublicBaseUrl);
options.BrevoApiKey = FirstNonEmpty(
    Environment.GetEnvironmentVariable("WOTLK_BREVO_API_KEY"),
    options.BrevoApiKey);
options.BrevoSenderEmail = FirstNonEmpty(
    Environment.GetEnvironmentVariable("WOTLK_BREVO_SENDER_EMAIL"),
    options.BrevoSenderEmail);
options.BrevoSenderName = FirstNonEmpty(
    Environment.GetEnvironmentVariable("WOTLK_BREVO_SENDER_NAME"),
    options.BrevoSenderName);
if (bool.TryParse(
        Environment.GetEnvironmentVariable("WOTLK_BREVO_SANDBOX"),
        out bool brevoSandbox))
{
    options.BrevoSandbox = brevoSandbox;
}

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
builder.Services.AddHttpClient<BrevoEmailClient>(client =>
{
    client.BaseAddress = new Uri("https://api.brevo.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddTransient<EmailVerificationService>();
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
    EmailVerificationService emailVerification,
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
        await emailVerification.SendAsync(
            response.Profile.AccountId,
            cancellationToken);
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
    EmailVerificationService emailVerification,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();
    if (!new EmailAddressAttribute().IsValid(request.Email))
        return Results.BadRequest(new { error = "Adresse e-mail invalide." });

    try
    {
        AccountProfile profile = await db.ChangeEmailAsync(
            account.AccountId,
            request.Email,
            cancellationToken);
        EmailVerificationDispatchResult delivery =
            await emailVerification.SendAsync(
                account.AccountId,
                cancellationToken);
        return Results.Ok(new
        {
            profile.AccountId,
            profile.Username,
            profile.Email,
            profile.EmailVerified,
            profile.AvatarKey,
            profile.TwoFactorEnabled,
            profile.RecoveryCodesGenerated,
            profile.Completion,
            Profile = profile,
            VerificationEmailSent =
                delivery.Status == EmailVerificationDispatchStatus.Sent,
            VerificationMessage = DeliveryMessage(delivery)
        });
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
    EmailVerificationService emailVerification,
    CancellationToken cancellationToken) =>
{
    AuthenticatedAccount? account = await AuthenticateAsync(context, db, cancellationToken);
    if (account is null)
        return Results.Unauthorized();

    EmailVerificationDispatchResult delivery =
        await emailVerification.SendAsync(
            account.AccountId,
            cancellationToken);
    if (delivery.Status == EmailVerificationDispatchStatus.Cooldown)
    {
        context.Response.Headers.RetryAfter =
            Math.Max(1, delivery.RetryAfterSeconds).ToString();
    }

    return delivery.Status switch
    {
        EmailVerificationDispatchStatus.Sent => Results.Accepted(value: new
        {
            message = "L'e-mail de validation a été envoyé."
        }),
        EmailVerificationDispatchStatus.AlreadyVerified => Results.Ok(new
        {
            message = "Cette adresse e-mail est déjà validée."
        }),
        EmailVerificationDispatchStatus.Cooldown => Results.Json(
            new
            {
                error = $"Un e-mail vient déjà d'être envoyé. Réessaie dans {Math.Max(1, delivery.RetryAfterSeconds)} seconde(s)."
            },
            statusCode: StatusCodes.Status429TooManyRequests),
        EmailVerificationDispatchStatus.NotConfigured => Results.Json(
            new
            {
                error = "L'envoi des e-mails Atlas est temporairement indisponible."
            },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(
            new
            {
                error = "Brevo n'a pas pu envoyer l'e-mail. Réessaie dans quelques instants."
            },
            statusCode: StatusCodes.Status502BadGateway)
    };
}).RequireRateLimiting("auth");

app.MapGet("/api/v1/email/verify", (
    string? token,
    HttpContext context,
    LauncherServerOptions serverOptions) =>
{
    SetVerificationPageHeaders(context);
    if (!TokenService.IsEmailVerificationToken(token))
    {
        return Results.Content(
            EmailVerificationPages.Invalid(),
            "text/html; charset=utf-8",
            Encoding.UTF8,
            StatusCodes.Status400BadRequest);
    }

    return Results.Content(
        EmailVerificationPages.Confirmation(
            token!,
            serverOptions.PublicBaseUrl),
        "text/html; charset=utf-8",
        Encoding.UTF8);
}).RequireRateLimiting("auth");

app.MapPost("/api/v1/email/verify", async (
    HttpContext context,
    LauncherDatabase db,
    CancellationToken cancellationToken) =>
{
    SetVerificationPageHeaders(context);
    if (!context.Request.HasFormContentType
        || context.Request.ContentLength > 4096)
    {
        return Results.Content(
            EmailVerificationPages.Invalid(),
            "text/html; charset=utf-8",
            Encoding.UTF8,
            StatusCodes.Status400BadRequest);
    }

    IFormCollection form;
    try
    {
        form = await context.Request.ReadFormAsync(cancellationToken);
    }
    catch (InvalidDataException)
    {
        return Results.Content(
            EmailVerificationPages.Invalid(),
            "text/html; charset=utf-8",
            Encoding.UTF8,
            StatusCodes.Status400BadRequest);
    }

    EmailVerificationResult result = await db.VerifyEmailAsync(
        form["token"].ToString(),
        cancellationToken);
    int statusCode = result switch
    {
        EmailVerificationResult.Verified => StatusCodes.Status200OK,
        EmailVerificationResult.AlreadyVerified => StatusCodes.Status200OK,
        EmailVerificationResult.Expired => StatusCodes.Status410Gone,
        _ => StatusCodes.Status400BadRequest
    };
    return Results.Content(
        EmailVerificationPages.Result(result),
        "text/html; charset=utf-8",
        Encoding.UTF8,
        statusCode);
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

static string DeliveryMessage(EmailVerificationDispatchResult delivery)
{
    return delivery.Status switch
    {
        EmailVerificationDispatchStatus.Sent =>
            "Adresse mise à jour. Un e-mail de validation vient d'être envoyé.",
        EmailVerificationDispatchStatus.AlreadyVerified =>
            "Cette adresse e-mail est déjà validée.",
        EmailVerificationDispatchStatus.Cooldown =>
            "Adresse mise à jour. Un e-mail de validation a déjà été envoyé récemment.",
        EmailVerificationDispatchStatus.NotConfigured =>
            "Adresse mise à jour. L'envoi de l'e-mail est temporairement indisponible.",
        _ =>
            "Adresse mise à jour, mais l'e-mail n'a pas pu être envoyé. Utilise le bouton Renvoyer."
    };
}

static void SetVerificationPageHeaders(HttpContext context)
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Append("Referrer-Policy", "no-referrer");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append(
        "Content-Security-Policy",
        "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'");
}

public partial class Program;
