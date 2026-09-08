using System.Windows.Media.Imaging;

namespace WotLK.Launcher.Account;

// This budget covers decoded pixels retained by the cache. A visible Image may
// keep its own reference after eviction; evicting never clears an on-screen avatar.
internal sealed class AvatarBitmapMemoryCache<TKey>(long maximumBytes, int maximumEntries) : IDisposable where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = [];
    private long _bytes;
    private bool _disposed;

    internal long RetainedBytes { get { lock (_sync) return _bytes; } }
    internal int Count { get { lock (_sync) return _entries.Count; } }

    internal bool TryGetValue(TKey key, out BitmapSource? image)
    {
        lock (_sync)
        {
            image = null;
            if (_disposed || !_entries.TryGetValue(key, out LinkedListNode<Entry>? node)) return false;
            _recent.Remove(node);
            _recent.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    internal void Store(TKey key, BitmapSource image)
    {
        long bytes = checked(((long)image.PixelWidth * image.Format.BitsPerPixel + 7) / 8 * image.PixelHeight);
        lock (_sync)
        {
            if (_disposed) return;
            Remove(key);
            if (bytes > maximumBytes || maximumEntries < 1) return;
            while (_recent.Last is { } oldest && (_bytes > maximumBytes - bytes || _entries.Count >= maximumEntries))
                Remove(oldest.Value.Key);
            LinkedListNode<Entry> node = _recent.AddFirst(new Entry(key, image, bytes));
            _entries.Add(key, node);
            _bytes += bytes;
        }
    }

    internal void Remove(TKey key)
    {
        lock (_sync)
        {
            if (!_entries.Remove(key, out LinkedListNode<Entry>? node)) return;
            _recent.Remove(node);
            _bytes -= node.Value.Bytes;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _entries.Clear();
            _recent.Clear();
            _bytes = 0;
        }
    }

    private sealed record Entry(TKey Key, BitmapSource Image, long Bytes);
}
