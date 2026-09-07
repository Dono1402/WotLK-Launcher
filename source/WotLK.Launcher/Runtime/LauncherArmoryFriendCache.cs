using System.Globalization;
using System.IO;

namespace WotLK.Launcher.Runtime;

// Disk assets survive normal application shutdown. They are never an authorization:
// every new helper must require a freshly authorized roster before serving them.
internal sealed class LauncherArmoryFriendCache : IDisposable
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(14);
    internal const int MaximumFriends = 32;
    internal const long MaximumBytes = 512L * 1024 * 1024;
    private readonly Func<DateTime> _utcNow;
    private readonly int _maximumFriends;
    private readonly long _maximumBytes;
    private string? _root;
    private uint? _viewer;
    private uint? _activeFriend;
    private long _activeBytes;
    private HashSet<uint>? _retainedFriends;
    private readonly HashSet<uint> _removedFriends = [];

    internal LauncherArmoryFriendCache(Func<DateTime>? utcNow = null, int maximumFriends = MaximumFriends, long maximumBytes = MaximumBytes)
    {
        if (maximumFriends < 1 || maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumFriends));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _maximumFriends = maximumFriends;
        _maximumBytes = maximumBytes;
    }

    // Establish the disk scope before asynchronous account resolution, so logout or
    // friend removal can also purge caches not yet reopened in this process.
    internal void ConfigureRoot(string dataRoot)
    {
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("Friend cache root must be absolute.", nameof(dataRoot));
        string root = Path.GetFullPath(Path.Combine(dataRoot, "friend-cache-v1"));
        if (string.Equals(_root, root, StringComparison.OrdinalIgnoreCase)) return;
        if (_root is not null) Clear();
        EnsureNoLinks(root);
        _root = root;
    }

    internal void Bind(string dataRoot, uint viewer, uint? activeFriend = null)
    {
        if (viewer == 0) throw new ArgumentOutOfRangeException(nameof(viewer));
        ConfigureRoot(dataRoot);
        if (_viewer is uint previous && previous != viewer) Clear();
        foreach (string directory in ViewerDirectories())
            if (ParseId(directory, "viewer-") != viewer) PurgeViewer(directory);
        _viewer = viewer;
        _activeFriend = activeFriend;
        _activeBytes = 0;
        foreach (string directory in FriendDirectories(ViewerDirectory(viewer)))
        {
            uint friend = ParseId(directory, "friend-")!.Value;
            if (_removedFriends.Contains(friend) || (_retainedFriends is not null && !_retainedFriends.Contains(friend))) Delete(directory);
        }
        Prune();
    }

    internal string GetDirectory(string dataRoot, uint viewer, uint friend)
    {
        if (viewer == 0 || friend == 0 || viewer == friend) throw new ArgumentOutOfRangeException(nameof(friend));
        Bind(dataRoot, viewer, friend);
        string directory = Path.Combine(ViewerDirectory(viewer), "friend-" + friend.ToString(CultureInfo.InvariantCulture));
        EnsureNoLinks(directory);
        string invalidation = directory + ".invalid";
        EnsureNoLinks(invalidation);
        _activeBytes = Directory.Exists(directory) ? DirectorySize(directory) : 0;
        if (File.Exists(invalidation) || (Directory.Exists(directory)
            && (_utcNow() - Directory.GetLastWriteTimeUtc(directory) > MaximumAge || _activeBytes > _maximumBytes)))
        {
            Delete(directory);
            if (Directory.Exists(directory) || File.Exists(invalidation))
                throw new IOException("Invalidated friend cache is still in use.");
            _activeBytes = 0;
        }
        Directory.CreateDirectory(directory);
        Directory.SetLastWriteTimeUtc(directory, _utcNow());
        _activeFriend = friend;
        Prune();
        return directory;
    }

    internal void Remove(uint friend)
    {
        _removedFriends.Add(friend);
        foreach (string viewer in ViewerDirectories())
            Delete(Path.Combine(viewer, "friend-" + friend.ToString(CultureInfo.InvariantCulture)));
        if (_activeFriend == friend) _activeFriend = null;
    }

    internal void Retain(IReadOnlySet<uint> friends)
    {
        _retainedFriends = friends.ToHashSet();
        _removedFriends.RemoveWhere(friends.Contains);
        foreach (string viewer in ViewerDirectories())
            foreach (string directory in FriendDirectories(viewer))
                if (!friends.Contains(ParseId(directory, "friend-")!.Value)) Delete(directory);
        Prune();
    }

    internal void Clear()
    {
        foreach (string viewer in ViewerDirectories()) PurgeViewer(viewer);
        _viewer = null;
        _activeFriend = null;
        _activeBytes = 0;
        _retainedFriends = null;
        _removedFriends.Clear();
    }

    private string ViewerDirectory(uint viewer) => Path.Combine(_root!, "viewer-" + viewer.ToString(CultureInfo.InvariantCulture));
    private string[] ViewerDirectories() => OwnedDirectories(_root, "viewer-");
    private static string[] FriendDirectories(string viewer) => OwnedDirectories(viewer, "friend-");

    private static string[] OwnedDirectories(string? parent, string prefix)
    {
        try
        {
            if (parent is null || !Directory.Exists(parent)) return [];
            EnsureNoLinks(parent);
            return Directory.GetDirectories(parent).Where(path => ParseId(path, prefix) is not null
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0).ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static uint? ParseId(string directory, string prefix)
    {
        string name = Path.GetFileName(directory);
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && uint.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
            && id > 0 && name == prefix + id.ToString(CultureInfo.InvariantCulture) ? id : null;
    }

    private static void PurgeViewer(string viewer)
    {
        foreach (string directory in FriendDirectories(viewer)) Delete(directory);
    }

    private void Prune()
    {
        if (_viewer is not uint viewer) return;
        try
        {
            var entries = FriendDirectories(ViewerDirectory(viewer))
                .Select(path => new { Path = path, Id = ParseId(path, "friend-"), Used = Directory.GetLastWriteTimeUtc(path) })
                .OrderByDescending(entry => entry.Id == _activeFriend).ThenByDescending(entry => entry.Used).ToArray();
            int retained = 0;
            long bytes = 0;
            foreach (var entry in entries)
            {
                // Node may be replacing build directories right now. The active
                // cache is measured only at the next open, before its helper starts.
                if (entry.Id == _activeFriend) { retained++; bytes += _activeBytes; continue; }
                try
                {
                    long size = DirectorySize(entry.Path);
                    if (_utcNow() - entry.Used > MaximumAge || retained >= _maximumFriends || size > _maximumBytes - bytes) Delete(entry.Path);
                    else { retained++; bytes = Math.Min(long.MaxValue - size, bytes) + size; }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long DirectorySize(string directory)
    {
        EnsureNoLinks(directory);
        return MeasureTree(directory);
    }

    private static long MeasureTree(string directory)
    {
        long bytes = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Friend cache entries cannot contain links.");
            long size = (attributes & FileAttributes.Directory) != 0 ? MeasureTree(entry) : new FileInfo(entry).Length;
            bytes = Math.Min(long.MaxValue - size, bytes) + size;
        }
        return bytes;
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Friend cache paths cannot contain links.");
    }

    private static void Delete(string directory)
    {
        if (ParseId(directory, "friend-") is null || ParseId(Path.GetDirectoryName(directory)!, "viewer-") is null
            || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(directory))) != "friend-cache-v1") return;
        try
        {
            string invalidation = directory + ".invalid";
            EnsureNoLinks(directory);
            EnsureNoLinks(invalidation);
            if (Directory.Exists(directory))
            {
                // Persist revocation before deletion. A locked file must not make
                // a later process silently reuse data invalidated at logout.
                File.WriteAllText(invalidation, "friend-cache-v1");
                _ = DirectorySize(directory);
                Directory.Delete(directory, recursive: true);
            }
            if (File.Exists(invalidation)) File.Delete(invalidation);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _root = null;
        _viewer = null;
        _activeFriend = null;
        _activeBytes = 0;
        _retainedFriends = null;
        _removedFriends.Clear();
    }
}
