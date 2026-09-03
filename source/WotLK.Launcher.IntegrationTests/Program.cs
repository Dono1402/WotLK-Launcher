using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using WotLK.Launcher;
using WotLK.Launcher.Server;

if (args.Length == 1
    && string.Equals(args[0], "--legacy-characterization", StringComparison.OrdinalIgnoreCase))
{
    return await LegacyMainWindowCharacterizationTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--runtime-composition", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherRuntimeCompositionTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--runtime-hardening", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherRuntimeHardeningTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--local-shell-actions", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherLocalActionTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--game-verification", StringComparison.OrdinalIgnoreCase))
{
    return await GameVerificationTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--operation-coordinator", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherOperationCoordinatorTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--operation-activity-contracts", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherOperationActivityContractTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--game-maintenance", StringComparison.OrdinalIgnoreCase))
{
    return await GameClientMaintenanceTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--game-runtime", StringComparison.OrdinalIgnoreCase))
{
    return await GameRuntimeCoordinatorTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--full-repair", StringComparison.OrdinalIgnoreCase))
{
    return await GameFullVerificationRepairTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--dashboard", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherDashboardTests.RunAsync();
}

if (args.Length >= 1
    && string.Equals(args[0], "--auth-preview", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await AuthOverlayPreviewTests.RunAsync(captureDirectory);
}

if (args.Length == 1
    && string.Equals(args[0], "--auth-runtime", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherAuthenticationTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--game-launch", StringComparison.OrdinalIgnoreCase))
{
    return await GameLaunchTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--profile-logout", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherProfileLogoutTests.RunAsync();
}

if (args.Length >= 1
    && string.Equals(args[0], "--settings-preview", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await SettingsPreviewTests.RunAsync(captureDirectory);
}

if (args.Length >= 1
    && string.Equals(args[0], "--settings-runtime", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await LauncherSettingsRuntimeTests.RunAsync(captureDirectory);
}

if (args.Length >= 1
    && string.Equals(args[0], "--account-preview", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await AccountPreviewTests.RunAsync(captureDirectory);
}

if (args.Length >= 1
    && string.Equals(args[0], "--account-avatar-client", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await AccountAvatarClientTests.RunAsync(captureDirectory);
}

if (args.Length >= 1
    && string.Equals(args[0], "--account-security-sessions", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await AccountSecuritySessionTests.RunAsync(captureDirectory);
}

if (args.Length == 1
    && string.Equals(args[0], "--friends-runtime", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherFriendsTests.RunAsync();
}

if (args.Length >= 1
    && string.Equals(args[0], "--friends-preview", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await FriendsDrawerWpfTests.RunAsync(captureDirectory);
}

if (args.Length >= 1
    && string.Equals(args[0], "--addons-preview", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await AddonsPreviewTests.RunAsync(captureDirectory);
}

if (args.Length >= 1
    && string.Equals(args[0], "--addons-runtime", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await LauncherAddonsRuntimeTests.RunAsync(captureDirectory);
}

if (args.Length >= 1
    && string.Equals(args[0], "--activity-preview", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await ActivityCenterPreviewTests.RunAsync(captureDirectory);
}

if (args.Length == 1
    && string.Equals(args[0], "--activity-runtime", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherActivityCoordinatorTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--launcher-self-update-atomic", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherSelfUpdateAtomicReplacementTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--launcher-self-update-runtime", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherSelfUpdateCoordinatorTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--secure-self-update", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherSelfUpdateSecurityTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--secure-self-update-live", StringComparison.OrdinalIgnoreCase))
{
    return await LauncherSelfUpdateSecurityTests.RunProductionAsync();
}

if (args.Length >= 1
    && string.Equals(args[0], "--activity-runtime-wpf", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await ActivityCenterRuntimeWpfTests.RunAsync(captureDirectory);
}

if (args.Length >= 1
    && string.Equals(args[0], "--v2-rollout-audit", StringComparison.OrdinalIgnoreCase))
{
    string? captureDirectory = args.Length == 3
        && string.Equals(args[1], "--capture-directory", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;
    return await V2RolloutReadinessTests.RunAsync(captureDirectory);
}

if (args.Length == 1
    && string.Equals(args[0], "--avatar-foundation", StringComparison.OrdinalIgnoreCase))
{
    return await AvatarFoundationTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--avatar-migrations-mysql", StringComparison.OrdinalIgnoreCase))
{
    return await AvatarFoundationTests.RunMySqlAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--avatar-backend", StringComparison.OrdinalIgnoreCase))
{
    return await AvatarBackendTests.RunAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--avatar-backend-mysql", StringComparison.OrdinalIgnoreCase))
{
    return await AvatarBackendTests.RunMySqlAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--atlas-identity-mysql", StringComparison.OrdinalIgnoreCase))
{
    return await AvatarBackendTests.RunIdentityMySqlAsync();
}

if (args.Length == 1
    && string.Equals(args[0], "--local-shell-windows-smoke", StringComparison.OrdinalIgnoreCase))
{
    return LauncherLocalActionTests.RunWindowsSmoke();
}

if (args.Length == 1
    && string.Equals(args[0], "--ticket-format", StringComparison.OrdinalIgnoreCase))
{
    string first = TokenService.CreateGameTicket();
    string second = TokenService.CreateGameTicket();
    Assert(
        Regex.IsMatch(first, "^HP-[0-9A-F]{40}$", RegexOptions.CultureInvariant),
        "Le ticket de jeu doit utiliser le format Hermes natif HP- suivi de 40 caracteres hexadecimaux.");
    Assert(first != second, "Deux tickets de jeu consecutifs doivent etre distincts.");
    Console.WriteLine("Game ticket format OK.");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--email-token-format", StringComparison.OrdinalIgnoreCase))
{
    string first = TokenService.CreateEmailVerificationToken();
    string second = TokenService.CreateEmailVerificationToken();
    Assert(
        Regex.IsMatch(
            first,
            "^atl_email-[A-Za-z0-9_-]{43}$",
            RegexOptions.CultureInvariant),
        "Le jeton e-mail doit contenir 32 octets aléatoires encodés en Base64 URL-safe.");
    Assert(first != second, "Deux jetons e-mail consécutifs doivent être distincts.");
    Assert(
        TokenService.Hash(first).Length == 32,
        "L'empreinte stockée en base doit être un SHA-256 de 32 octets.");
    Assert(
        TokenService.IsEmailVerificationToken(first),
        "Le validateur doit accepter un jeton généré par Atlas.");
    Assert(
        !TokenService.IsEmailVerificationToken(first + "="),
        "Le validateur doit refuser les caractères hors Base64 URL-safe.");
    string page = EmailVerificationPages.Confirmation(
        first,
        "https://animeclub.fr/wotlk");
    Assert(
        page.Contains("method=\"post\"", StringComparison.Ordinal)
        && page.Contains(first, StringComparison.Ordinal),
        "Le lien reçu par e-mail doit demander une confirmation POST avant de consommer le jeton.");
    Console.WriteLine("Email verification token format OK.");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--brevo-payload", StringComparison.OrdinalIgnoreCase))
{
    BrevoCaptureHandler handler = new();
    using HttpClient http = new(handler)
    {
        BaseAddress = new Uri("https://api.brevo.com/")
    };
    LauncherServerOptions options = new()
    {
        PublicBaseUrl = "https://animeclub.fr/wotlk",
        BrevoApiKey = "integration-test-api-key",
        BrevoSenderEmail = "noreply@animeclub.fr",
        BrevoSenderName = "Atlas - Arthas",
        BrevoSandbox = true
    };
    BrevoEmailClient brevo = new(http, options);
    string token = TokenService.CreateEmailVerificationToken();
    await brevo.SendVerificationAsync(
        new EmailVerificationChallenge(
            42,
            "Dono1402",
            "dono@example.test",
            token,
            TokenService.Hash(token),
            DateTimeOffset.UtcNow.AddHours(24)),
        CancellationToken.None);

    Assert(
        handler.Method == HttpMethod.Post
        && handler.RequestUri == new Uri("https://api.brevo.com/v3/smtp/email"),
        "Brevo doit être appelé avec POST /v3/smtp/email.");
    Assert(
        handler.ApiKey == "integration-test-api-key",
        "La clé Brevo doit être transmise dans l'en-tête api-key.");
    Assert(handler.Body is not null, "La requête Brevo doit contenir un document JSON.");
    using JsonDocument document = JsonDocument.Parse(handler.Body!);
    JsonElement root = document.RootElement;
    Assert(
        root.GetProperty("sender").GetProperty("email").GetString()
            == "noreply@animeclub.fr",
        "L'expéditeur Brevo est incorrect.");
    Assert(
        root.GetProperty("to")[0].GetProperty("email").GetString()
            == "dono@example.test",
        "Le destinataire Brevo est incorrect.");
    Assert(
        root.GetProperty("headers").GetProperty("X-Sib-Sandbox").GetString()
            == "drop",
        "Le test Brevo doit activer le mode bac à sable.");
    Assert(
        root.GetProperty("htmlContent").GetString()!.Contains(token, StringComparison.Ordinal),
        "Le contenu Brevo doit inclure le jeton de validation.");
    Assert(
        !handler.Body!.Contains("integration-test-api-key", StringComparison.Ordinal),
        "La clé Brevo ne doit jamais apparaître dans le corps JSON.");
    Console.WriteLine("Brevo transactional payload OK.");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--game-ticket-live", StringComparison.OrdinalIgnoreCase))
{
    using LauncherAuthService auth = new();
    Assert(await auth.RestoreAsync(), "Une session launcher Atlas valide est requise pour le test du ticket.");
    GameTicket ticket = await auth.CreateGameTicketAsync();
    Assert(
        Regex.IsMatch(ticket.Ticket, "^HP-[0-9A-F]{40}$", RegexOptions.CultureInvariant),
        "L'API Atlas doit delivrer le format de ticket Hermes natif.");
    Assert(ticket.AccountId > 0, "L'identifiant numerique du compte doit accompagner le ticket.");
    Assert(
        string.Equals(ticket.Username, auth.Session!.Profile.Username, StringComparison.OrdinalIgnoreCase),
        "Le ticket doit appartenir a la session launcher active.");
    Console.WriteLine(
        $"Live game ticket OK: account={ticket.Username}, id={ticket.AccountId}, expires={ticket.ExpiresAt:O}.");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--email-resend-live", StringComparison.OrdinalIgnoreCase))
{
    using LauncherAuthService auth = new();
    Assert(
        await auth.RestoreAsync(),
        "Une session launcher Atlas valide est requise pour envoyer l'e-mail de test.");
    string message = await auth.ResendVerificationAsync();
    Console.WriteLine($"Live email verification request OK: {message}");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--atlas-network", StringComparison.OrdinalIgnoreCase))
{
    using HttpClient atlas = new(AtlasNetwork.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    using HttpResponseMessage response = await atlas.PostAsJsonAsync(
        "https://animeclub.fr/wotlk/api/v1/auth/login",
        new
        {
            username = "Dono1402",
            password = "atlas-network-diagnostic-invalid-password",
            deviceName = "LauncherIntegrationTest"
        });
    TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
    Assert(response.StatusCode == HttpStatusCode.Unauthorized, "Atlas doit refuser le mot de passe de diagnostic.");
    Assert(elapsed < TimeSpan.FromSeconds(5), "La connexion IPv4 Atlas doit répondre en moins de cinq secondes.");
    Console.WriteLine($"Atlas network OK: {(int)response.StatusCode} in {elapsed.TotalMilliseconds:F0} ms.");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--client-config", StringComparison.OrdinalIgnoreCase))
{
    string root = Path.Combine(Path.GetTempPath(), "AtlasClientConfigTest", Guid.NewGuid().ToString("N"));
    string wtfDirectory = Path.Combine(root, "_classic_", "WTF");
    Directory.CreateDirectory(wtfDirectory);
    string configPath = Path.Combine(wtfDirectory, "Config.wtf");

    try
    {
        await File.WriteAllTextAsync(
            configPath,
            "SET textLocale \"enUS\"\nSET instantQuestText \"0\"\nSET instantQuestText \"0\"\n");

        string writtenPath = GameInstallServices.EnsureDefaultClientConfig(root, "frFR");
        string[] lines = await File.ReadAllLinesAsync(writtenPath);
        Assert(
            lines.Count(line => line.StartsWith("SET instantQuestText ", StringComparison.OrdinalIgnoreCase)) == 1
            && lines.Any(line => string.Equals(line, "SET instantQuestText \"0\"", StringComparison.OrdinalIgnoreCase)),
            "Le launcher doit conserver une seule valeur instantQuestText explicite.");
        Assert(
            lines.Any(line => string.Equals(line, "SET textLocale \"frFR\"", StringComparison.OrdinalIgnoreCase)),
            "Le launcher doit conserver la langue demandée dans Config.wtf.");
        Console.WriteLine("Client Config.wtf defaults OK.");
        return 0;
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: WotLK.Launcher.IntegrationTests --live | <catalog-directory> [archive-cache-directory ...]");
    return 2;
}

var live = string.Equals(args[0], "--live", StringComparison.OrdinalIgnoreCase);
var archiveDirectories = live ? [] : args.Select(Path.GetFullPath).ToArray();
var testRoot = Path.Combine(Path.GetTempPath(), "AtlasAddonManagerTest", Guid.NewGuid().ToString("N"));
var classicDirectory = Path.Combine(testRoot, "_classic_");
var addonsDirectory = Path.Combine(classicDirectory, "Interface", "AddOns");
Directory.CreateDirectory(addonsDirectory);
await File.WriteAllBytesAsync(Path.Combine(classicDirectory, "WowClassic.exe"), []);
await File.WriteAllBytesAsync(Path.Combine(testRoot, GameInstallServices.GameLauncherFileName), []);

var customAddonDirectory = Path.Combine(addonsDirectory, "AtlasUserAddon");
Directory.CreateDirectory(customAddonDirectory);
await File.WriteAllTextAsync(
    Path.Combine(customAddonDirectory, "AtlasUserAddon.toc"),
    "## Interface: 30403\n## Title: Atlas user addon\n");

Process? simulatedGame = null;
try
{
    using LauncherAuthService? auth = live ? new LauncherAuthService() : null;
    if (live && !await auth!.RestoreAsync())
    {
        throw new InvalidOperationException("Une session launcher Atlas valide est requise pour le test live.");
    }

    PackageDirectoryHandler? handler = live ? null : new PackageDirectoryHandler(archiveDirectories);
    using var http = live
        ? new HttpClient(new AtlasAuthorizationHandler(() => auth!.AccessToken))
        : new HttpClient(handler!);
    http.Timeout = TimeSpan.FromMinutes(30);
    var catalog = await AddonInstallServices.LoadCatalogAsync(
        http,
        new Uri(live
            ? "http://152.228.225.7/launcher/addons/catalog.json"
            : "http://atlas.test/catalog.json"),
        CancellationToken.None);
    handler?.ResetRequestCount();
    var selectAll = catalog.Addons.ToDictionary(addon => addon.Id, _ => true, StringComparer.OrdinalIgnoreCase);
    var expectedArchiveRequests = catalog.Addons.Count + catalog.Addons.Sum(addon => addon.Components.Count);

    if (OperatingSystem.IsWindows())
    {
        var wowExecutable = Path.Combine(classicDirectory, "WowClassic.exe");
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        File.Copy(commandProcessor, wowExecutable, overwrite: true);
        simulatedGame = Process.Start(new ProcessStartInfo
        {
            FileName = wowExecutable,
            Arguments = "/d /c ping 127.0.0.1 -n 120 > nul",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert(simulatedGame is not null, "Le faux processus WoW n'a pas pu démarrer.");
        await Task.Delay(250);
        Assert(
            GameInstallServices.IsGameRunning(testRoot),
            "Le test doit détecter le faux client WoW ouvert.");
    }

    await AddonInstallServices.ApplySelectionAsync(
        http,
        catalog,
        testRoot,
        selectAll,
        progress: null,
        log: Console.WriteLine,
        CancellationToken.None);

    if (handler is not null)
    {
        Assert(handler.RequestCount == expectedArchiveRequests, "Chaque archive doit etre telechargee une fois.");
    }
    Assert(
        simulatedGame is null || !simulatedGame.HasExited,
        "L'installation des addons ne doit pas fermer le jeu en cours.");
    foreach (var addon in catalog.Addons)
    {
        foreach (var folder in addon.Folders)
        {
            Assert(Directory.Exists(Path.Combine(addonsDirectory, folder)), $"Dossier installe absent: {folder}");
        }
    }

    var installedState = AddonInstallServices.Inspect(catalog, testRoot);
    Assert(installedState.Values.All(value => value.Status == AddonLocalStatus.Installed), "Tous les addons doivent etre a jour.");

    await AddonInstallServices.ApplySelectionAsync(
        http,
        catalog,
        testRoot,
        selectAll,
        progress: null,
        log: Console.WriteLine,
        CancellationToken.None);
    if (handler is not null)
    {
        Assert(handler.RequestCount == expectedArchiveRequests, "Un second passage a jour ne doit rien telecharger.");
    }

    var selectNone = catalog.Addons.ToDictionary(addon => addon.Id, _ => false, StringComparer.OrdinalIgnoreCase);
    await AddonInstallServices.ApplySelectionAsync(
        http,
        catalog,
        testRoot,
        selectNone,
        progress: null,
        log: Console.WriteLine,
        CancellationToken.None);

    foreach (var addon in catalog.Addons)
    {
        foreach (var folder in addon.Folders)
        {
            Assert(!Directory.Exists(Path.Combine(addonsDirectory, folder)), $"Dossier gere non supprime: {folder}");
        }
    }
    Assert(Directory.Exists(customAddonDirectory), "Un addon utilisateur non gere a ete supprime.");

    var unmanagedQuestie = Path.Combine(addonsDirectory, "Questie");
    Directory.CreateDirectory(unmanagedQuestie);
    await File.WriteAllTextAsync(Path.Combine(unmanagedQuestie, "Questie-WOTLKC.toc"), "## Interface: 30403\n");
    var unmanagedState = AddonInstallServices.Inspect(catalog, testRoot);
    Assert(unmanagedState["questie"].Status == AddonLocalStatus.DetectedUnmanaged, "Un addon externe doit etre detecte sans devenir gere.");

    Console.WriteLine($"OK: {catalog.Addons.Count} addons installes, verifies et retires dans le client jetable.");
    Console.WriteLine("OK: l'addon utilisateur temoin est intact.");
    return 0;
}
finally
{
    if (simulatedGame is { HasExited: false })
    {
        simulatedGame.Kill(entireProcessTree: true);
        simulatedGame.WaitForExit(5_000);
    }
    simulatedGame?.Dispose();

    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class PackageDirectoryHandler(IReadOnlyList<string> archiveDirectories) : HttpMessageHandler
{
    internal int RequestCount { get; private set; }

    internal void ResetRequestCount()
    {
        RequestCount = 0;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestUri = request.RequestUri ?? throw new InvalidOperationException("URL de paquet absente.");
        var fileName = Path.GetFileName(requestUri.AbsolutePath);
        var filePath = archiveDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories))
            .FirstOrDefault();
        if (filePath is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        RequestCount++;
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentLength = stream.Length;
        return Task.FromResult(response);
    }
}

internal sealed class BrevoCaptureHandler : HttpMessageHandler
{
    internal HttpMethod? Method { get; private set; }
    internal Uri? RequestUri { get; private set; }
    internal string? ApiKey { get; private set; }
    internal string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Method = request.Method;
        RequestUri = request.RequestUri;
        ApiKey = request.Headers.TryGetValues("api-key", out IEnumerable<string>? values)
            ? values.SingleOrDefault()
            : null;
        Body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new { messageId = "sandbox-test" })
        };
    }
}
