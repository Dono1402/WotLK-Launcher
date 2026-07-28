using System.Net.Http.Json;

namespace WotLK.Launcher.Server;

public sealed class HermesTicketClient
{
    private readonly HttpClient _httpClient;
    private readonly LauncherServerOptions _options;

    public HermesTicketClient(HttpClient httpClient, LauncherServerOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<GameTicketResponse> CreateAsync(
        AuthenticatedAccount account,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.HermesSharedSecret))
            throw new InvalidOperationException("Le secret interne Hermes n'est pas configuré.");

        string ticket = TokenService.CreateGameTicket();
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            _options.HermesTicketUrl);
        request.Headers.Add("X-Atlas-Internal-Secret", _options.HermesSharedSecret);
        request.Content = JsonContent.Create(new HermesTicketRequest(account.Username, ticket, "frFR"));

        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        HermesTicketResponse? hermes = await response.Content.ReadFromJsonAsync<HermesTicketResponse>(
            cancellationToken: cancellationToken);
        if (hermes is null)
            throw new InvalidOperationException("Hermes a renvoyé une réponse vide.");

        return new GameTicketResponse(
            ticket,
            hermes.ExpiresAt,
            account.Username,
            account.Username);
    }
}
