using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WotLK.Launcher;

internal static class SecureSessionStore
{
    internal const int MaxProtectedSessionBytes = 64 * 1024;
    private static readonly byte[] Entropy =
        "Atlas WotLK Launcher session v1"u8.ToArray();

    private static string SessionPath =>
        Path.Combine(LauncherSettings.SettingsDirectory, "session.bin");

    public static void Save(StoredLauncherSession session) => Save(session, SessionPath);

    internal static void Save(StoredLauncherSession session, string sessionPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[]? protectedData = null;
        try
        {
            protectedData = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);
            string fullPath = Path.GetFullPath(sessionPath);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("Chemin de session locale invalide.");
            Directory.CreateDirectory(directory);
            using SessionStoreLock _ = SessionStoreLock.Acquire();
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(protectedData);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(temporary, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, fullPath);
                }
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedData is not null)
                CryptographicOperations.ZeroMemory(protectedData);
        }
    }

    public static StoredLauncherSession? Load() => Load(SessionPath);

    internal static StoredLauncherSession? Load(string sessionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        string fullPath = Path.GetFullPath(sessionPath);
        using SessionStoreLock _ = SessionStoreLock.Acquire();
        if (!File.Exists(fullPath)) return null;

        using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        byte[]? protectedData = null;
        byte[]? plaintext = null;
        try
        {
            protectedData = ReadBounded(stream);
            plaintext = ProtectedData.Unprotect(
                protectedData,
                Entropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredLauncherSession>(plaintext)
                ?? throw new JsonException("Stored session must contain an object.");
        }
        catch (Exception exception) when (exception is CryptographicException
            or JsonException
            or InvalidDataException)
        {
            InvalidateStableSession(stream);
            return null;
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (protectedData is not null) CryptographicOperations.ZeroMemory(protectedData);
        }
    }

    private static byte[] ReadBounded(FileStream stream)
    {
        stream.Position = 0;
        if (stream.Length > MaxProtectedSessionBytes)
            throw new InvalidDataException($"Stored session exceeds the {MaxProtectedSessionBytes}-byte limit.");

        using MemoryStream bounded = new(capacity: (int)Math.Min(stream.Length, MaxProtectedSessionBytes));
        byte[] buffer = new byte[4096];
        while (true)
        {
            int remaining = MaxProtectedSessionBytes + 1 - checked((int)bounded.Length);
            int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
                break;
            bounded.Write(buffer, 0, read);
            if (bounded.Length > MaxProtectedSessionBytes)
                throw new InvalidDataException($"Stored session exceeds the {MaxProtectedSessionBytes}-byte limit.");
        }

        return bounded.ToArray();
    }

    private static void InvalidateStableSession(FileStream stream)
    {
        stream.Position = 0;
        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
    }

    public static void Clear() => Clear(SessionPath);

    internal static void Clear(string sessionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        string fullPath = Path.GetFullPath(sessionPath);
        using SessionStoreLock _ = SessionStoreLock.Acquire();
        ClearUnderLock(fullPath);
    }

    private static void ClearUnderLock(string sessionPath)
    {
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

    private sealed class SessionStoreLock : IDisposable
    {
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
        private const string MutexName = @"Local\Atlas.WotLK.SessionStore.v2";
        private readonly Mutex _mutex;
        private bool _ownsMutex;

        private SessionStoreLock(Mutex mutex, bool ownsMutex)
        {
            _mutex = mutex;
            _ownsMutex = ownsMutex;
        }

        internal static SessionStoreLock Acquire()
        {
            Mutex mutex = new(initiallyOwned: false, MutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                throw new IOException("Le stockage de session locale est occupé par une autre instance.");
            }

            return new SessionStoreLock(mutex, ownsMutex: true);
        }

        public void Dispose()
        {
            if (_ownsMutex)
            {
                _ownsMutex = false;
                _mutex.ReleaseMutex();
            }

            _mutex.Dispose();
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
