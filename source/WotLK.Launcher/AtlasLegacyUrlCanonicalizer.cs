namespace WotLK.Launcher;

internal static class AtlasLegacyUrlCanonicalizer
{
    private const string AtlasHost = "animeclub.fr";

    internal static bool TryCanonicalize(string? value, out string canonicalUrl)
    {
        canonicalUrl = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, AtlasHost, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 80
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0)
        {
            return false;
        }

        canonicalUrl = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = 443
        }.Uri.AbsoluteUri;
        return true;
    }
}
