using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotLK.Launcher.Game;

internal interface IGameManifestClient
{
    Task<LauncherManifest> LoadAsync(string manifestUrl, CancellationToken cancellationToken);
}

internal sealed class GameManifestClient(HttpClient httpClient) : IGameManifestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };

    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<LauncherManifest> LoadAsync(
        string manifestUrl,
        CancellationToken cancellationToken)
    {
        Uri manifestUri = GameManifestValidator.RequireHttpsUri(
            manifestUrl,
            "URL du manifeste du client");
        using HttpResponseMessage response = await _httpClient.GetAsync(
            manifestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        Uri responseUri = GameManifestValidator.RequireHttpsUri(
            response.RequestMessage?.RequestUri ?? manifestUri,
            "URL finale du manifeste du client");
        if (!responseUri.Equals(manifestUri))
        {
            throw new InvalidDataException(
                "La redirection automatique du manifeste du client est refusée.");
        }
        response.EnsureSuccessStatusCode();

        byte[] payload = await BoundedJsonHttpContent.ReadAsync(
            response.Content,
            GameManifestValidator.MaximumManifestBytes,
            "Le manifeste du client",
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            MaxDepth = JsonOptions.MaxDepth
        });
        BoundedJsonHttpContent.RejectDuplicateProperties(document.RootElement, "Le manifeste du client");
        LauncherManifest manifest = document.RootElement.Deserialize<LauncherManifest>(JsonOptions)
            ?? throw new InvalidOperationException("Impossible de lire le manifeste.");
        GameManifestValidator.Validate(manifest);
        return manifest;
    }
}
