using System.IO;
using WotLK.Launcher.Runtime;

internal static class ArmoryFriendCacheTests
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "atlas-friend-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ValidatePersistenceAndRevocation(Path.Combine(root, "sessions"));
            ValidateRetentionLimits(Path.Combine(root, "retention"));
            ValidateLockedRevocation(Path.Combine(root, "locked"));
            ValidateLinkProtection(Path.Combine(root, "links"));
            Console.WriteLine("Friend cache OK: restart reuse, viewer isolation, unopened removal/logout, sibling and link protection, age/count/byte limits, active generation and locked-file invalidation.");
            return 0;
        }
        finally
        {
            string fullRoot = Path.GetFullPath(root);
            if (Path.GetDirectoryName(fullRoot) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(fullRoot).StartsWith("atlas-friend-cache-test-", StringComparison.Ordinal))
                Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static void ValidatePersistenceAndRevocation(string root)
    {
        string first, second;
        using (LauncherArmoryFriendCache initial = new())
        {
            first = initial.GetDirectory(root, 42, 91);
            File.WriteAllText(Path.Combine(first, "generated-model.bin"), "owned fixture");
            second = initial.GetDirectory(root, 42, 92);
            Check(first != second && initial.GetDirectory(root, 42, 91) == first, "Navigation must isolate friends and reuse the same target.");
        }
        Check(File.Exists(Path.Combine(first, "generated-model.bin")), "Normal disposal must preserve generated assets.");
        string unrelated = Path.Combine(Path.GetDirectoryName(first)!, "unrelated-data");
        Directory.CreateDirectory(unrelated);
        string unrelatedViewer = Path.Combine(root, "friend-cache-v1", "unrelated-viewer", "friend-91");
        Directory.CreateDirectory(unrelatedViewer);
        using (LauncherArmoryFriendCache restarted = new())
        {
            Check(restarted.GetDirectory(root, 42, 91) == first && File.ReadAllText(Path.Combine(first, "generated-model.bin")) == "owned fixture",
                "A new cache instance must reuse the previous process's exact directory and bytes.");
        }
        using (LauncherArmoryFriendCache beforeOpening = new())
        {
            beforeOpening.ConfigureRoot(root);
            beforeOpening.Retain(new HashSet<uint> { 92 });
            Check(!Directory.Exists(first) && Directory.Exists(second), "A fresh roster must purge removed friends even before opening a profile after restart.");
            beforeOpening.Remove(92);
            Check(!Directory.Exists(second), "Explicit removal must find persisted entries absent from in-memory navigation.");
        }
        using (LauncherArmoryFriendCache reseed = new()) { second = reseed.GetDirectory(root, 42, 92); }
        using (LauncherArmoryFriendCache logout = new())
        {
            logout.ConfigureRoot(root);
            logout.Clear();
            Check(!Directory.Exists(second), "Logout must purge persisted caches even without binding or opening any friend this run.");
        }
        using (LauncherArmoryFriendCache accounts = new())
        {
            first = accounts.GetDirectory(root, 42, 91);
            File.WriteAllText(Path.Combine(first, "private.bin"), "previous viewer");
            second = accounts.GetDirectory(root, 84, 91);
            Check(first != second && !Directory.Exists(first) && !File.Exists(Path.Combine(second, "private.bin")), "Changing account must use a distinct scope and purge previous viewer data.");
        }
        using (LauncherArmoryFriendCache changedAfterRestart = new())
        {
            first = changedAfterRestart.GetDirectory(root, 42, 91);
            Check(first != second && !Directory.Exists(second), "Account isolation must also hold across separate instances.");
            changedAfterRestart.Clear();
        }
        Check(Directory.Exists(unrelated) && Directory.Exists(unrelatedViewer), "All purges must preserve unrelated sibling directories.");
    }

    private static void ValidateRetentionLimits(string root)
    {
        DateTime now = DateTime.UtcNow;
        using LauncherArmoryFriendCache cache = new(() => now, maximumFriends: 3, maximumBytes: 512);
        string large = cache.GetDirectory(root, 42, 91);
        File.WriteAllBytes(Path.Combine(large, "model.bin"), new byte[400]);
        now = now.AddMinutes(1);
        string small = cache.GetDirectory(root, 42, 92);
        File.WriteAllBytes(Path.Combine(small, "model.bin"), new byte[10]);
        cache.GetDirectory(root, 42, 91);
        cache.GetDirectory(root, 42, 92);
        Check(Directory.Exists(large) && Directory.Exists(small), "Changing the active friend must not incorrectly charge the previous friend's size to the new one.");
        string activeBuild = Path.Combine(small, "build-in-progress.bin");
        File.WriteAllBytes(activeBuild, new byte[600]);
        cache.Retain(new HashSet<uint> { 91, 92 });
        Check(File.Exists(activeBuild), "Retention must not scan or evict the actively generated cache when it temporarily exceeds the budget.");
        cache.GetDirectory(root, 42, 92);
        Check(!File.Exists(activeBuild) && Directory.Exists(large), "An oversized completed cache must be rebuilt at the next open before serving it.");
        now = now.AddDays(15);
        cache.GetDirectory(root, 42, 92);
        Check(!Directory.Exists(large), "Unused caches must expire after the retention age.");
        cache.Retain(new HashSet<uint> { 92, 100, 101, 102, 103, 104 });
        for (uint friend = 100; friend < 105; friend++) { now = now.AddMinutes(1); cache.GetDirectory(root, 42, friend); }
        Check(Directory.GetDirectories(Path.GetDirectoryName(small)!, "friend-*").Length == 3, "The friend count must be bounded with oldest entries evicted first.");
    }

    private static void ValidateLockedRevocation(string root)
    {
        string directory;
        using (LauncherArmoryFriendCache initial = new()) { directory = initial.GetDirectory(root, 42, 91); }
        string file = Path.Combine(directory, "locked-model.bin");
        File.WriteAllText(file, "revoked geometry");
        using (FileStream locked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using LauncherArmoryFriendCache logout = new();
            logout.ConfigureRoot(root);
            logout.Clear();
            Check(File.Exists(directory + ".invalid"), "Failed deletion must leave a persistent revocation marker.");
            using LauncherArmoryFriendCache nextProcess = new();
            bool rejected = false;
            try { nextProcess.GetDirectory(root, 42, 91); } catch (IOException) { rejected = true; }
            Check(rejected, "A new process must never reuse a locked cache invalidated by logout.");
            nextProcess.Retain(new HashSet<uint>());
            nextProcess.Clear();
        }
        using LauncherArmoryFriendCache retry = new();
        Check(retry.GetDirectory(root, 42, 91) == directory && !File.Exists(file) && !File.Exists(directory + ".invalid"),
            "Once the lock is released, invalidation must complete before the stable directory is reused.");
    }

    private static void ValidateLinkProtection(string root)
    {
        Directory.CreateDirectory(root);
        string sibling = Path.Combine(root, "unrelated-target");
        Directory.CreateDirectory(sibling);
        string sentinel = Path.Combine(sibling, "keep.txt");
        File.WriteAllText(sentinel, "untouched");
        string directory;
        using LauncherArmoryFriendCache cache = new();
        directory = cache.GetDirectory(root, 42, 91);
        string link = Path.Combine(directory, "linked-assets");
        try { Directory.CreateSymbolicLink(link, sibling); }
        catch (UnauthorizedAccessException) { Console.WriteLine("Symlink case skipped: this host does not grant symlink creation."); return; }
        catch (IOException error) when (error.HResult == unchecked((int)0x80070522))
        { Console.WriteLine("Symlink case skipped: this host does not grant symlink creation."); return; }
        try
        {
            cache.Retain(new HashSet<uint> { 91 });
            cache.Remove(91);
            cache.Clear();
            bool rejected = false;
            try { cache.GetDirectory(root, 42, 91); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(sentinel) == "untouched", "Link-containing caches must be refused without touching the target, even during best-effort purge.");
        }
        finally { Directory.Delete(link); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
