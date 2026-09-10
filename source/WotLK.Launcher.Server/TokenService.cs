using System.Security.Cryptography;

namespace WotLK.Launcher.Server;

public sealed class TokenService
{
    public SessionTokens Create(
        int accessMinutes,
        int refreshDays,
        DateTimeOffset? absoluteRefreshExpiresAt = null)
    {
        if (accessMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(accessMinutes));
        if (refreshDays <= 0) throw new ArgumentOutOfRangeException(nameof(refreshDays));
        string accessToken = CreateToken("atl_access");
        string refreshToken = CreateToken("atl_refresh");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset rollingRefreshExpiresAt = now.AddDays(refreshDays);
        DateTimeOffset refreshExpiresAt = absoluteRefreshExpiresAt is { } absolute
            && absolute < rollingRefreshExpiresAt
                ? absolute
                : rollingRefreshExpiresAt;
        DateTimeOffset accessExpiresAt = now.AddMinutes(accessMinutes);
        if (accessExpiresAt > refreshExpiresAt)
            accessExpiresAt = refreshExpiresAt;
        return new SessionTokens(
            accessToken,
            Hash(accessToken),
            accessExpiresAt,
            refreshToken,
            Hash(refreshToken),
            refreshExpiresAt);
    }

    public static byte[] Hash(string token)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public static string CreateGameTicket()
        => "HP-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

    public static string CreateEmailVerificationToken()
        => CreateToken("atl_email");

    public static bool IsRefreshToken(string? token)
        => IsToken(token, "atl_refresh");

    public static bool IsEmailVerificationToken(string? token)
    {
        return IsToken(token, "atl_email");
    }

    private static bool IsToken(string? token, string kind)
    {
        string prefix = kind + "-";
        return token is not null
            && token.Length == prefix.Length + 43
            && token.StartsWith(prefix, StringComparison.Ordinal)
            && token[prefix.Length..].All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_');
    }

    private static string CreateToken(string prefix)
        => prefix + "-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
