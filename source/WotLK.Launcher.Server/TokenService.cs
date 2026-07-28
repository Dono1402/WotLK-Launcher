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
        => CreateToken("ATL");

    private static string CreateToken(string prefix)
        => prefix + "-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
