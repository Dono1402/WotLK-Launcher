using System.IO;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;

internal static class OptimizationStorageTests
{
    internal static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "atlas-optimization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            LauncherSettings settings = new() { InstallPath = Path.Combine(root, "Game"), GameLocale = "enUS" };
            settings.SaveTo(path);
            Check(LauncherSettings.LoadFrom(path).RecoveryNotice is null && File.Exists(path + ".bak"), "Initial write has a valid backup and needs no recovery.");
            settings.GameLocale = "frFR";
            settings.SaveTo(path);
            Check(LauncherSettings.LoadFrom(path).GameLocale == "frFR" && LauncherSettings.LoadFrom(path + ".bak").GameLocale == "enUS", "Atomic replacement preserves the previous settings.");
            File.WriteAllText(path, "{incomplete");
            LauncherSettings recovered = LauncherSettings.LoadFrom(path);
            Check(recovered.GameLocale == "enUS" && recovered.InstallPath == settings.InstallPath && recovered.RecoveryNotice is not null,
                "Corruption recovers the prior preferences with a visible notice.");
            using (LauncherOperationCoordinator operations = new())
            using (LauncherSettingsCoordinator coordinator = new(recovered, operations, _ => { }, _ => { }, _ => { }, _ => false))
                Check(coordinator.CurrentSnapshot.RecoveryNotice == recovered.RecoveryNotice, "Runtime exposes the recovery notice to presentation.");
            recovered.SaveTo(path);
            Check(Directory.GetFiles(root, "settings.json.corrupt-*").Single() is string damaged && File.ReadAllText(damaged) == "{incomplete",
                "Repair preserves the original damaged bytes.");
            Check(LauncherSettings.LoadFrom(path + ".bak").GameLocale == "enUS", "Damaged primary never replaces the good backup.");
            string before = File.ReadAllText(path);
            using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                recovered.GameLocale = "frFR";
                try { recovered.SaveTo(path); throw new InvalidOperationException("Locked replacement unexpectedly succeeded."); }
                catch (IOException) { }
            }
            Check(File.ReadAllText(path) == before && Directory.GetFiles(root, "*.tmp").Length == 0, "Failed replacement leaves the primary intact and cleans only its temporary file.");
            File.Delete(path);
            Check(LauncherSettings.LoadFrom(path).RecoveryNotice is not null, "A missing primary can recover its backup.");
            File.WriteAllText(path, "null");
            File.WriteAllText(path + ".bak", "[]");
            try { LauncherSettings.LoadFrom(path); throw new InvalidOperationException("Both invalid files must not silently reset preferences."); }
            catch (JsonException) { }

            string oversizedPath = Path.Combine(root, "oversized-settings.json");
            File.WriteAllBytes(oversizedPath, new byte[LauncherSettings.MaxSettingsFileBytes + 1]);
            File.WriteAllText(oversizedPath + ".bak", "{\"GameLocale\":\"enUS\"}");
            LauncherSettings boundedRecovery = LauncherSettings.LoadFrom(oversizedPath);
            Check(boundedRecovery.GameLocale == "enUS" && boundedRecovery.RecoveryNotice is not null,
                "An oversized primary settings file is rejected before parsing and recovers the bounded backup.");
            File.WriteAllBytes(oversizedPath + ".bak", new byte[LauncherSettings.MaxSettingsFileBytes + 1]);
            try { LauncherSettings.LoadFrom(oversizedPath); throw new InvalidOperationException("Two oversized settings files must be rejected."); }
            catch (JsonException) { }
            await ValidateCacheOrderingAsync(root);
            Console.WriteLine("Optimization storage PASS: atomic replacement, backup/corrupt-file recovery, missing primary, locked-write preservation, clear recovery notice and ordered background cache invalidation.");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ValidateCacheOrderingAsync(string root)
    {
        LauncherBackgroundWorkQueue queue = new();
        using LauncherArmoryFriendCache cache = new();
        string directory = await queue.RunAsync(() => cache.GetDirectory(root, 42, 73));
        File.WriteAllText(Path.Combine(directory, "old-data"), "previous-session");
        TaskCompletionSource helperStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task removal = queue.RunAsync(() => cache.Remove(73), helperStopped.Task);
        Task<string> reopen = queue.RunAsync(() => cache.GetDirectory(root, 42, 73));
        Check(!removal.IsCompleted && !reopen.IsCompleted, "Disk invalidation waits for helper shutdown; later reads cannot overtake it.");
        helperStopped.SetResult();
        directory = await reopen;
        await removal;
        Check(!File.Exists(Path.Combine(directory, "old-data")), "Reopening cannot reuse invalidated data from the prior session.");
        Task failed = queue.RunAsync((Action)(() => throw new IOException("fixture")));
        try { await failed; } catch (IOException) { }
        await queue.RunAsync(cache.Clear);
        await queue.Pending;
        Check(!Directory.Exists(directory), "A failed disk action does not prevent queued cleanup.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
