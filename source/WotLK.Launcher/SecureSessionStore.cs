using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WotLK.Launcher;

internal static class SecureSessionStore
{
    private static readonly byte[] Entropy =
        "Atlas WotLK Launcher session v1"u8.ToArray();

    private static string SessionPath =>
        Path.Combine(LauncherSettings.SettingsDirectory, "session.bin");

    public static void Save(StoredLauncherSession session)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] protectedData = ProtectedData.Protect(
            plaintext,
            Entropy,
            DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(LauncherSettings.SettingsDirectory);
        File.WriteAllBytes(SessionPath, protectedData);
        CryptographicOperations.ZeroMemory(plaintext);
    }

    public static StoredLauncherSession? Load()
    {
        if (!File.Exists(SessionPath))
            return null;

        try
        {
            byte[] protectedData = File.ReadAllBytes(SessionPath);
            byte[] plaintext = ProtectedData.Unprotect(
                protectedData,
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<StoredLauncherSession>(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException)
        {
            Clear();
            return null;
        }
        catch (JsonException)
        {
            Clear();
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(SessionPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record StoredLauncherSession(
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);
