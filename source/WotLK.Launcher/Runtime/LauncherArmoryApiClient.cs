using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace WotLK.Launcher.Runtime;

internal sealed record LauncherArmoryDataRequest(long Id, string Operation, uint? CharacterId = null)
{
    internal bool IsValid => Id is > 0 and <= 9007199254740991
        && ((Operation == "roster" && CharacterId is null)
            || (Operation == "catalog" && CharacterId is > 0));
}

internal sealed class LauncherArmoryApiClient(HttpClient client, Uri apiBaseUri)
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;

    internal async Task<JsonElement> ReadAsync(LauncherArmoryDataRequest request, CancellationToken cancellationToken)
    {
        if (!request.IsValid) throw new ArgumentException("Invalid armory request.", nameof(request));
        string relative = request.Operation == "roster"
            ? "armory/characters"
            : $"armory/characters/{request.CharacterId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}/catalog";
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using HttpRequestMessage message = new(HttpMethod.Get, new Uri(apiBaseUri, relative));
        using HttpResponseMessage response = await client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Armory session expired.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("Armory response is too large.");
        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using MemoryStream bytes = new();
        byte[] buffer = new byte[16384];
        int length;
        while ((length = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + length > MaximumResponseBytes) throw new InvalidDataException("Armory response is too large.");
            bytes.Write(buffer, 0, length);
        }
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid armory response.");
        return document.RootElement.Clone();
    }
}
