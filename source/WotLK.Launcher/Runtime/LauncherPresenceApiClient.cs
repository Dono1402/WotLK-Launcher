using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal interface ILauncherPresenceApiClient
{
    Task<LauncherPresenceStateDto> UpdateAsync(LauncherPresenceUpdateRequest request, CancellationToken token);
}

internal sealed class LauncherPresenceApiException(HttpStatusCode statusCode, string code) : Exception(code)
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
    internal string Code { get; } = code;
}

internal sealed class LauncherPresenceApiClient(HttpClient client, Uri apiV1BaseUri) : ILauncherPresenceApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    public async Task<LauncherPresenceStateDto> UpdateAsync(LauncherPresenceUpdateRequest request, CancellationToken token)
    {
        if ((request.Status is not null && !LauncherPresenceStatus.IsValid(request.Status)) || request.IdleSeconds < 0)
            throw new ArgumentException("Invalid presence request.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpRequestMessage message = new(HttpMethod.Put, new Uri(apiV1BaseUri, "me/presence")) { Content = JsonContent.Create(request, options: Json) };
        using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LauncherPresenceApiException(response.StatusCode, response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented
                ? "presence-unavailable" : "presence-request-failed");
        if (response.Content.Headers.ContentLength > 8192) throw new InvalidDataException("Invalid presence response.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using MemoryStream bytes = new();
        byte[] buffer = new byte[2048];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > 8192) throw new InvalidDataException("Invalid presence response.");
            bytes.Write(buffer, 0, count);
        }
        LauncherPresenceStateDto state = JsonSerializer.Deserialize<LauncherPresenceStateDto>(bytes.ToArray(), Json) ?? throw new InvalidDataException("Invalid presence response.");
        if (state.AccountId == 0 || !LauncherPresenceStatus.IsValid(state.Status) || !LauncherPresenceStatus.IsValid(state.ManualStatus) || state.Version < 1
            || state.IsAutomaticAway != (state.Status == "away" && state.ManualStatus == "online")
            || (state.Status != "offline" && state.Status != state.ManualStatus && !state.IsAutomaticAway))
            throw new InvalidDataException("Invalid presence response.");
        return state;
    }
}
