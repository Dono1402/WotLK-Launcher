using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using WotLK.Launcher;
using WotLK.Launcher.Server;

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
