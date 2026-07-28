using System.Net.Http;
using System.Net.Http.Headers;

namespace WotLK.Launcher;

internal sealed class AtlasAuthorizationHandler : DelegatingHandler
{
    private readonly Func<string?> _getAccessToken;

    public AtlasAuthorizationHandler(Func<string?> getAccessToken)
        : base(AtlasNetwork.CreateHandler())
    {
        _getAccessToken = getAccessToken;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                request.RequestUri?.Host,
                "animeclub.fr",
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_getAccessToken()))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _getAccessToken());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
