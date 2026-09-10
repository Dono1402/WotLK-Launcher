using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherShopApiClient(HttpClient client, Uri apiBaseUri)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    internal async Task<ShopSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(apiBaseUri, "shop"));
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Shop session expired.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > ShopSnapshot.MaximumResponseBytes)
            throw new InvalidDataException("Shop response too large.");
        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using MemoryStream bytes = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > ShopSnapshot.MaximumResponseBytes) throw new InvalidDataException("Shop response too large.");
            bytes.Write(buffer, 0, count);
        }
        ShopSnapshot snapshot = JsonSerializer.Deserialize<ShopSnapshot>(bytes.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("Missing shop snapshot.");
        snapshot.Validate();
        return snapshot;
    }
}
