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
        try
        {
            byte[] protectedData = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(LauncherSettings.SettingsDirectory);
            File.WriteAllBytes(SessionPath, protectedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
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

    public static void Clear() => Clear(SessionPath);

    internal static void Clear(string sessionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        try
        {
            File.Delete(sessionPath);
        }
        catch (Exception deleteFailure) when (deleteFailure is IOException or UnauthorizedAccessException)
        {
            try
            {
                // A reader can forbid deletion while still allowing writes. Leave an
                // empty tombstone so the stored session cannot be restored later.
                using FileStream tombstone = new(sessionPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                tombstone.Flush(flushToDisk: true);
            }
            catch (Exception truncateFailure) when (truncateFailure is IOException or UnauthorizedAccessException)
            {
                throw new SecureSessionStoreClearException(deleteFailure, truncateFailure);
            }
        }
    }
}

internal sealed class SecureSessionStoreClearException : IOException
{
    internal SecureSessionStoreClearException(Exception deleteFailure, Exception truncateFailure)
        : base("Impossible d’effacer la session enregistrée sur cet ordinateur.",
            new AggregateException(deleteFailure, truncateFailure))
    {
    }
}

internal sealed record StoredLauncherSession(
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);
