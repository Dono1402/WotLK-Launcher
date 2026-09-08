using System.IO;
using WotLK.Launcher;

internal static class SecureSessionStoreLogoutTests
{
    internal static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "atlas-session-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int assertions = 0;
        byte[] syntheticSession = [0x41, 0x74, 0x6c, 0x61, 0x73, 0x01, 0x02, 0x03];
        try
        {
            string absentPath = Path.Combine(root, "absent.bin");
            SecureSessionStore.Clear(absentPath);
            SecureSessionStore.Clear(absentPath);
            Check(!File.Exists(absentPath), "Clearing an absent session is idempotent and does not create a tombstone.");

            string removablePath = Path.Combine(root, "removable.bin");
            await File.WriteAllBytesAsync(removablePath, syntheticSession);
            SecureSessionStore.Clear(removablePath);
            Check(!File.Exists(removablePath), "An unlocked session file is deleted.");

            foreach (FileShare sharing in new[] { FileShare.Write, FileShare.ReadWrite })
            {
                string path = Path.Combine(root, $"truncate-{sharing}.bin");
                await File.WriteAllBytesAsync(path, syntheticSession);
                using (FileStream reader = new(path, FileMode.Open, FileAccess.Read, sharing))
                {
                    Check(DeleteIsBlocked(path), "The fixture must deny deletion while its reader is open.");
                    SecureSessionStore.Clear(path);
                    Check(reader.Length == 0, "A reader denying deletion must not prevent the writable session from being emptied.");
                }
                Check(File.Exists(path) && new FileInfo(path).Length == 0,
                    "The fallback leaves an empty tombstone containing no session bytes.");
                SecureSessionStore.Clear(path);
                Check(!File.Exists(path), "The empty tombstone can be deleted once the reader closes.");
            }

            string lockedPath = Path.Combine(root, "exclusive.bin");
            await File.WriteAllBytesAsync(lockedPath, syntheticSession);
            SecureSessionStoreClearException? clearFailure = null;
            using (FileStream exclusive = new(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    SecureSessionStore.Clear(lockedPath);
                }
                catch (SecureSessionStoreClearException exception)
                {
                    clearFailure = exception;
                }
                Check(clearFailure is not null, "An exclusive lock must surface a classified failure when neither deletion nor truncation succeeds.");
                Check(clearFailure?.InnerException is AggregateException { InnerExceptions.Count: 2 } causes
                        && causes.InnerExceptions.All(error => error is IOException or UnauthorizedAccessException),
                    "The classified failure preserves both disk errors for diagnosis.");
                Check(clearFailure?.Message.Contains("session", StringComparison.OrdinalIgnoreCase) == true,
                    "The failure gives the caller a readable session-specific explanation.");
            }
            Check((await File.ReadAllBytesAsync(lockedPath)).SequenceEqual(syntheticSession),
                "A failed clear must never be reported as success while the original bytes remain.");
            SecureSessionStore.Clear(lockedPath);
            Check(!File.Exists(lockedPath), "Clearing succeeds after the exclusive lock is released.");

            Console.WriteLine($"Secure session logout PASS: {assertions} assertions; synthetic temporary files only, no real session or SSO registry accessed.");
            return 0;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
    }

    private static bool DeleteIsBlocked(string path)
    {
        try
        {
            File.Delete(path);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
