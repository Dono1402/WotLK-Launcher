using System.Security.Cryptography;

namespace WotLK.Launcher.Server;

public sealed class TokenService
{
    public SessionTokens Create(int accessMinutes, int refreshDays)
    {
        string accessToken = CreateToken("atl_access");
        string refreshToken = CreateToken("atl_refresh");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SessionTokens(
            accessToken,
            Hash(accessToken),
            now.AddMinutes(accessMinutes),
            refreshToken,
            Hash(refreshToken),
            now.AddDays(refreshDays));
    }

    public static byte[] Hash(string token)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public static string CreateGameTicket()
        => "HP-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

    public static string CreateEmailVerificationToken()
        => CreateToken("atl_email");

    public static bool IsEmailVerificationToken(string? token)
    {
        const string prefix = "atl_email-";
        if (token is null
            || token.Length != prefix.Length + 43
            || !token.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return token[prefix.Length..].All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_');
    }

    private static string CreateToken(string prefix)
        => prefix + "-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
