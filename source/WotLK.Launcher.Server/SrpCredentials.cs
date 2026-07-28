using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WotLK.Launcher.Server;

public static class SrpCredentials
{
    private const string LegacyNHex = "894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7";
    private const string ModernNHex = "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73";

    private static readonly BigInteger LegacyN = ParseHex(LegacyNHex);
    private static readonly BigInteger ModernN = ParseHex(ModernNHex);

    public static (byte[] Salt, byte[] Verifier) MakeLegacy(string username, string password)
    {
        string identity = $"{username.ToUpperInvariant()}:{password.ToUpperInvariant()}";
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] identityHash = SHA1.HashData(Encoding.UTF8.GetBytes(identity));
        byte[] xInput = new byte[salt.Length + identityHash.Length];
        salt.CopyTo(xInput, 0);
        identityHash.CopyTo(xInput, salt.Length);
        byte[] xHash = SHA1.HashData(xInput);
        BigInteger x = new(xHash, isUnsigned: true, isBigEndian: false);
        BigInteger verifier = BigInteger.ModPow(7, x, LegacyN);
        return (salt, ToLittleEndian(verifier, 32));
    }

    public static bool VerifyLegacy(
        string username,
        string password,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> expectedVerifier)
    {
        string identity = $"{username.ToUpperInvariant()}:{password.ToUpperInvariant()}";
        byte[] identityHash = SHA1.HashData(Encoding.UTF8.GetBytes(identity));
        byte[] xInput = new byte[salt.Length + identityHash.Length];
        salt.CopyTo(xInput);
        identityHash.CopyTo(xInput.AsSpan(salt.Length));
        byte[] xHash = SHA1.HashData(xInput);
        BigInteger x = new(xHash, isUnsigned: true, isBigEndian: false);
        byte[] actual = ToLittleEndian(BigInteger.ModPow(7, x, LegacyN), 32);
        return CryptographicOperations.FixedTimeEquals(actual, expectedVerifier);
    }

    public static (byte[] Salt, byte[] Verifier) MakeModern(string username, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        string usernameHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(username.ToUpperInvariant())));
        byte[] xBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(usernameHash + ":" + password),
            salt,
            15000,
            HashAlgorithmName.SHA512,
            64);

        BigInteger verifier = CalculateModernVerifier(xBytes);
        return (salt, ToBigEndian(verifier));
    }

    public static bool VerifyModern(
        string username,
        string password,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> expectedVerifier)
    {
        string usernameHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(username.ToUpperInvariant())));
        byte[] xBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(usernameHash + ":" + password),
            salt,
            15000,
            HashAlgorithmName.SHA512,
            64);

        byte[] actual = ToBigEndian(CalculateModernVerifier(xBytes));
        return CryptographicOperations.FixedTimeEquals(actual, expectedVerifier);
    }

    private static BigInteger CalculateModernVerifier(ReadOnlySpan<byte> xBytes)
    {
        BigInteger unsignedX = new(xBytes, isUnsigned: true, isBigEndian: true);
        if ((xBytes[0] & 0x80) == 0)
            return BigInteger.ModPow(2, unsignedX, ModernN);

        // Hermes interprets a high-bit PBKDF2 result as a signed 512-bit value,
        // then normalizes it modulo N - 1. Computing 2^(N-1-d) directly is
        // needlessly expensive; for this prime group it equals (2^-1)^d.
        BigInteger magnitude =
            (BigInteger.One << (xBytes.Length * 8)) - unsignedX;
        BigInteger inverseGenerator = (ModernN + BigInteger.One) / 2;
        return BigInteger.ModPow(inverseGenerator, magnitude, ModernN);
    }

    private static BigInteger ParseHex(string value)
        => BigInteger.Parse("0" + value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    private static byte[] ToLittleEndian(BigInteger value, int length)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (bytes.Length == length)
            return bytes;
        if (bytes.Length > length)
            return bytes[..length];

        byte[] padded = new byte[length];
        bytes.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] ToBigEndian(BigInteger value)
        => value.ToByteArray(isUnsigned: true, isBigEndian: true);
}
