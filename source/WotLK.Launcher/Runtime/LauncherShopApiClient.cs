using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Runtime;

internal sealed class ShopApiException(string code, HttpStatusCode status) : HttpRequestException(code, null, status)
{
    internal string Code { get; } = code;
}

internal sealed class LauncherShopApiClient(HttpClient client, Uri apiBaseUri)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    internal Task<ShopSnapshot> ReadAsync(CancellationToken cancellationToken) => SendAsync<ShopSnapshot>(HttpMethod.Get, "shop", null, cancellationToken);
    internal Task<ShopGoldConversion> CreateConversionAsync(ShopCreateGoldConversion input, CancellationToken token) => SendAsync<ShopGoldConversion>(HttpMethod.Post,"shop/conversions",input,token);
    internal Task<ShopGoldConversion> ReadConversionAsync(string id, CancellationToken token) => SendAsync<ShopGoldConversion>(HttpMethod.Get,"shop/conversions/"+RequestId(id),null,token);
    internal Task<ShopOrder> CreateOrderAsync(ShopCreateOrder input, CancellationToken token) => SendAsync<ShopOrder>(HttpMethod.Post,"shop/orders",input,token);
    internal Task<ShopOrder> CancelOrderAsync(string id, CancellationToken token) => SendAsync<ShopOrder>(HttpMethod.Post,"shop/orders/"+RequestId(id)+"/cancel",null,token);
    internal Task<ShopTopUp> CreateTopUpAsync(ShopCreateTopUp input, CancellationToken token) => SendAsync<ShopTopUp>(HttpMethod.Post,"shop/top-ups",input,token);
    internal Task<ShopTopUp> CancelTopUpAsync(string id, CancellationToken token) => SendAsync<ShopTopUp>(HttpMethod.Post,"shop/top-ups/"+RequestId(id)+"/cancel",null,token);
    internal Task<ShopAdminTopUpPage> ListTopUpsAsync(string? status,long? before,CancellationToken token)
    {
        List<string> query = [];
        if (status is not null) query.Add("status="+Uri.EscapeDataString(status));
        if (before is not null) query.Add("before="+before.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return SendAsync<ShopAdminTopUpPage>(HttpMethod.Get,"shop/admin/top-ups"+(query.Count==0?"":"?"+string.Join("&",query)),null,token);
    }
    internal Task<ShopAdminTopUp> ReadTopUpAsync(string id,CancellationToken token) => SendAsync<ShopAdminTopUp>(HttpMethod.Get,"shop/admin/top-ups/"+RequestId(id),null,token);
    internal Task<ShopAdminTopUp> DecideTopUpAsync(string id,ShopTopUpDecision input,CancellationToken token)
        => SendAsync<ShopAdminTopUp>(HttpMethod.Post,"shop/admin/top-ups/"+RequestId(id)+"/decision",input,token);
    private static string RequestId(string id) => ShopFundingValidation.IsId(id) ? id : throw new InvalidDataException("Invalid top-up ID.");

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? input, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using HttpRequestMessage request = new(method, new Uri(apiBaseUri, path));
        if (input is not null) request.Content = JsonContent.Create(input, options: JsonOptions);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Shop session expired.");
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
        if (!response.IsSuccessStatusCode)
        {
            string code = "shop-request-failed";
            try
            {
                using JsonDocument error = JsonDocument.Parse(bytes.ToArray());
                if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error",out JsonElement value) && value.ValueKind==JsonValueKind.String
                    && value.GetString() is { Length: <= 100 } message) code=message;
            }
            catch (JsonException) { }
            throw new ShopApiException(code,response.StatusCode);
        }
        T result = JsonSerializer.Deserialize<T>(bytes.ToArray(), JsonOptions) ?? throw new InvalidDataException("Missing shop response.");
        switch (result)
        {
            case ShopSnapshot snapshot: snapshot.Validate(); break;
            case ShopGoldConversion conversion: conversion.Validate(); break;
            case ShopOrder order: order.Validate(); break;
            case ShopTopUp topUp: topUp.Validate(); break;
            case ShopAdminTopUp admin: admin.Validate(); break;
            case ShopAdminTopUpPage page: page.Validate(); break;
        }
        return result;
    }
}
