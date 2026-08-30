using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace WotLK.Launcher.Game;

internal interface IGameManifestClient
{
    Task<LauncherManifest> LoadAsync(string manifestUrl, CancellationToken cancellationToken);
}

internal sealed class GameManifestClient(HttpClient httpClient) : IGameManifestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<LauncherManifest> LoadAsync(
        string manifestUrl,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            manifestUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<LauncherManifest>(
                stream,
                JsonOptions,
                cancellationToken)
            ?? throw new InvalidOperationException("Impossible de lire le manifeste.");
    }
}
