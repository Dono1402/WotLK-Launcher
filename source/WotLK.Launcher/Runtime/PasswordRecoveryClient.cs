using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace WotLK.Launcher.Runtime;

internal sealed record PasswordRecoveryResult(bool Accepted, string Error)
{
    internal static PasswordRecoveryResult Unavailable { get; } = new(false,
        "La récupération du mot de passe est temporairement indisponible. Réessaie plus tard.");
}

internal static class PasswordRecoveryClient
{
    internal static async Task<PasswordRecoveryResult> RequestAsync(string email, CancellationToken token)
    {
        using HttpClient client = new(AtlasNetwork.CreateHandler())
        { BaseAddress = AtlasNetwork.LauncherApiBaseUri, Timeout = TimeSpan.FromSeconds(20) };
        return await RequestAsync(client, email, token).ConfigureAwait(false);
    }

    internal static async Task<PasswordRecoveryResult> RequestAsync(HttpClient client, string email, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "auth/password-reset/request")
            { Content = JsonContent.Create(new { email }) };
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.Accepted => new(true, ""),
                HttpStatusCode.TooManyRequests => new(false, "Trop de demandes. Patiente une minute avant de réessayer."),
                _ => PasswordRecoveryResult.Unavailable
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        { return PasswordRecoveryResult.Unavailable; }
    }
}
