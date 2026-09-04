using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace WotLK.Launcher.Account;

internal sealed class AvatarImageCache : IDisposable
{
    internal const long MaximumDiskBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan[] PublicationRetryDelays =
    [
        TimeSpan.FromMilliseconds(90),
        TimeSpan.FromMilliseconds(240),
        TimeSpan.FromMilliseconds(520)
    ];
    private readonly object _inFlightSync = new();
    private readonly IAvatarMediaClient _mediaClient;
    private readonly string _root;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action _onUnauthorized;
    private readonly ConcurrentDictionary<AvatarImageCacheKey, BitmapSource> _memory = new();
    private readonly Dictionary<AvatarImageCacheKey, Task<BitmapSource?>> _inFlight = [];
    private int _disposeState;

    internal AvatarImageCache(
        IAvatarMediaClient mediaClient,
        string root,
        CancellationToken lifetimeToken,
        Action? onUnauthorized = null)
    {
        _mediaClient = mediaClient ?? throw new ArgumentNullException(nameof(mediaClient));
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        _lifetimeToken = lifetimeToken;
        _onUnauthorized = onUnauthorized ?? (() => { });
    }

    internal string Root => _root;

    internal async Task<BitmapSource?> GetAsync(
        AvatarDescriptor descriptor,
        int size,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        AvatarImageCacheKey key = AvatarImageCacheKey.Create(descriptor, size);
        if (_memory.TryGetValue(key, out BitmapSource? cached))
        {
            Touch(GetPath(key));
            return cached;
        }

        Task<BitmapSource?> shared;
        lock (_inFlightSync)
        {
            if (!_inFlight.TryGetValue(key, out shared!))
            {
                shared = Task.Run(
                    () => LoadSharedAsync(key, descriptor, size),
                    CancellationToken.None);
                _inFlight.Add(key, shared);
            }
        }

        try
        {
            return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    internal bool TryGetMemory(
        AvatarDescriptor descriptor,
        int size,
        out BitmapSource? image)
    {
        image = null;
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        return _memory.TryGetValue(AvatarImageCacheKey.Create(descriptor, size), out image);
    }

    internal void Evict(AvatarDescriptor descriptor)
    {
        foreach (int size in new[] { 32, 64, 128, 256 })
        {
            AvatarImageCacheKey key = AvatarImageCacheKey.Create(descriptor, size);
            _memory.TryRemove(key, out _);
            TryDelete(GetPath(key));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _memory.Clear();
        lock (_inFlightSync)
        {
            _inFlight.Clear();
        }
    }

    private async Task<BitmapSource?> LoadSharedAsync(
        AvatarImageCacheKey key,
        AvatarDescriptor descriptor,
        int size)
    {
        try
        {
            string path = GetPath(key);
            BitmapSource? disk = await TryLoadDiskAsync(path, _lifetimeToken).ConfigureAwait(false);
            if (disk is not null)
            {
                _memory[key] = disk;
                Touch(path);
                return disk;
            }

            AvatarMediaDownloadResult download = await DownloadPublishedVariantAsync(
                descriptor,
                size).ConfigureAwait(false);
            if (download.Status == AvatarMediaDownloadStatus.Unauthorized)
            {
                _onUnauthorized();
                return null;
            }
            if (download.Status != AvatarMediaDownloadStatus.Success || download.Bytes is null)
            {
                return null;
            }

            BitmapSource image;
            try
            {
                image = await Task.Run(
                    () => AvatarWpfImageDecoder.DecodePng(download.Bytes),
                    _lifetimeToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException
                                               or NotSupportedException
                                               or FileFormatException)
            {
                return null;
            }

            await PublishAsync(path, download.Bytes, _lifetimeToken).ConfigureAwait(false);
            _memory[key] = image;
            await TrimDiskAsync(_lifetimeToken).ConfigureAwait(false);
            return image;
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return null;
        }
        catch (AvatarMediaException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            lock (_inFlightSync)
            {
                _inFlight.Remove(key);
            }
        }
    }

    private async Task<AvatarMediaDownloadResult> DownloadPublishedVariantAsync(
        AvatarDescriptor descriptor,
        int size)
    {
        AvatarMediaDownloadResult result = await _mediaClient.DownloadAvatarAsync(
            descriptor,
            size,
            _lifetimeToken).ConfigureAwait(false);
        foreach (TimeSpan delay in PublicationRetryDelays)
        {
            if (result.Status != AvatarMediaDownloadStatus.NotFound)
            {
                return result;
            }

            await Task.Delay(delay, _lifetimeToken).ConfigureAwait(false);
            result = await _mediaClient.DownloadAvatarAsync(
                descriptor,
                size,
                _lifetimeToken).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task<BitmapSource?> TryLoadDiskAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return await Task.Run(
                () => AvatarWpfImageDecoder.DecodePng(bytes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or NotSupportedException
                                           or FileFormatException)
        {
            TryDelete(path);
            return null;
        }
    }

    private static async Task PublishAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Avatar cache path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task TrimDiskAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        await Task.Run(() =>
        {
            FileInfo[] files = new DirectoryInfo(_root)
                .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file.LastAccessTimeUtc)
                .ThenBy(file => file.LastWriteTimeUtc)
                .ToArray();
            long total = files.Sum(file => file.Length);
            foreach (FileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= MaximumDiskBytes)
                {
                    break;
                }

                long length = file.Length;
                try
                {
                    file.Delete();
                    total -= length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Cache cleanup is opportunistic.
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private string GetPath(AvatarImageCacheKey key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.CanonicalValue));
        return Path.Combine(_root, Convert.ToHexString(hash).ToLowerInvariant() + ".png");
    }

    private static void Touch(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    private readonly record struct AvatarImageCacheKey(
        Guid AvatarId,
        ulong Version,
        int Size)
    {
        internal string CanonicalValue => $"{AvatarId:N}|{Version}|{Size}";

        internal static AvatarImageCacheKey Create(AvatarDescriptor descriptor, int size)
        {
            if (size is not (32 or 64 or 128 or 256))
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            return new AvatarImageCacheKey(descriptor.AvatarId, descriptor.Version, size);
        }
    }
}
