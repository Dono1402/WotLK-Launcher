using System.Net.Http;
using System.Net.Http.Headers;

namespace WotLK.Launcher;

internal sealed class AtlasAuthorizationHandler : DelegatingHandler
{
    private readonly Func<string?> _getAccessToken;

    public AtlasAuthorizationHandler(Func<string?> getAccessToken)
        : this(getAccessToken, AtlasNetwork.CreateHandler())
    {
    }

    internal AtlasAuthorizationHandler(
        Func<string?> getAccessToken,
        HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _getAccessToken = getAccessToken ?? throw new ArgumentNullException(nameof(getAccessToken));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = null;
        Uri? uri = request.RequestUri;
        if (uri is { IsAbsoluteUri: true }
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "animeclub.fr", StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443
            && uri.UserInfo.Length == 0)
        {
            string? accessToken = _getAccessToken();
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
