using System.Security.Cryptography;

namespace WotLK.Launcher.Server.Avatars;

internal sealed class LocalAvatarStorage : IAvatarStorage
{
    private const long MaximumVariantBytes = 4L * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly string _root;
    private readonly string _avatarsRoot;
    private readonly string _stagingRoot;
    private readonly string _quarantineRoot;
    private readonly string _trashRoot;

    internal LocalAvatarStorage(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("La racine de stockage avatar est obligatoire.", nameof(rootPath));

        _root = Path.GetFullPath(rootPath);
        _avatarsRoot = UnderRoot("avatars");
        _stagingRoot = UnderRoot("staging");
        _quarantineRoot = UnderRoot("quarantine");
        _trashRoot = UnderRoot("trash");
        Directory.CreateDirectory(_avatarsRoot);
        Directory.CreateDirectory(_stagingRoot);
        Directory.CreateDirectory(_quarantineRoot);
        Directory.CreateDirectory(_trashRoot);
    }

    public Task<AvatarStagingHandle> BeginStagingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AvatarStagingHandle handle = AvatarStagingHandle.Create();
        Directory.CreateDirectory(StagingSourceDirectory(handle));
        Directory.CreateDirectory(StagingVariantsDirectory(handle));
        return Task.FromResult(handle);
    }

    public async Task WriteOriginalAsync(
        AvatarStagingHandle handle,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        string target = OriginalPath(handle);
        try
        {
            await using FileStream output = new(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyBoundedAsync(source, output, AvatarLimits.MaximumFileBytes, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            TryDeleteFile(target);
            throw;
        }
    }

    public Task<Stream> OpenOriginalReadAsync(
        AvatarStagingHandle handle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            OriginalPath(handle),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public async Task<AvatarStoredVariant> WriteVariantAsync(
        AvatarStagingHandle handle,
        int size,
        Stream png,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(png);
        EnsureVariantSize(size);
        string target = StagedVariantPath(handle, size);
        string temporary = target + ".tmp";
        TryDeleteFile(temporary);

        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long byteLength = 0;
            byte[] buffer = new byte[81920];
            await using (FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    int read = await png.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    byteLength += read;
                    if (byteLength > MaximumVariantBytes)
                        throw new AvatarStorageException("Une variante avatar depasse la limite autorisee.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            if (byteLength < PngSignature.Length || !await HasPngSignatureAsync(temporary, cancellationToken))
                throw new AvatarStorageException("La variante avatar n'est pas un PNG valide.");

            File.Move(temporary, target, overwrite: false);
            return new AvatarStoredVariant(size, "image/png", byteLength, hash.GetHashAndReset());
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    public Task PublishAsync(
        AvatarStagingHandle handle,
        AvatarStorageKey storageKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        handle.EnsureValid();
        string stagingRoot = StagingDirectory(handle);
        string variants = StagingVariantsDirectory(handle);
        foreach (int size in AvatarVariantSizes.All)
        {
            string variant = StagedVariantPath(handle, size);
            if (!File.Exists(variant) || new FileInfo(variant).Length <= PngSignature.Length)
                throw new AvatarStorageException($"La variante {size}px est absente ou vide.");
        }

        string final = StorageDirectory(storageKey);
        if (Directory.Exists(final))
            throw new AvatarStorageException("Cette version d'avatar est deja publiee.");

        DeleteDirectoryRequired(
            StagingSourceDirectory(handle),
            "Impossible de supprimer l'image originale avant publication.");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        Directory.Move(variants, final);
        TryDeleteDirectory(stagingRoot);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenVariantReadAsync(
        AvatarStorageKey storageKey,
        int size,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVariantSize(size);
        Stream stream = new FileStream(
            Path.Combine(StorageDirectory(storageKey), $"{size}.png"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> MoveToTrashAsync(
        AvatarStorageKey storageKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = StorageDirectory(storageKey);
        if (!Directory.Exists(source))
            return Task.FromResult(false);

        string destination = SafeCombine(_trashRoot, $"{storageKey.AvatarId:N}-{storageKey.Version}-{Guid.NewGuid():N}");
        Directory.Move(source, destination);
        DeleteEmptyParent(source);
        return Task.FromResult(true);
    }

    public Task QuarantineAsync(AvatarStagingHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = StagingDirectory(handle);
        if (Directory.Exists(source))
        {
            DeleteDirectoryRequired(
                StagingSourceDirectory(handle),
                "Impossible de supprimer l'image originale avant quarantaine.");
            string destination = SafeCombine(_quarantineRoot, $"{handle.OperationId:N}-{Guid.NewGuid():N}");
            Directory.Move(source, destination);
        }
        return Task.CompletedTask;
    }

    public Task DiscardStagingAsync(AvatarStagingHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDeleteDirectory(StagingDirectory(handle));
        return Task.CompletedTask;
    }

    public Task<AvatarStorageInventory> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<AvatarStagingEntry> staging = [];
        foreach (string directory in Directory.EnumerateDirectories(_stagingRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            if (Guid.TryParseExact(name, "N", out Guid operationId))
            {
                staging.Add(new AvatarStagingEntry(
                    new AvatarStagingHandle(operationId),
                    Directory.GetLastWriteTimeUtc(directory)));
            }
        }

        List<AvatarPublishedEntry> published = [];
        foreach (string avatarDirectory in Directory.EnumerateDirectories(_avatarsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string avatarName = Path.GetFileName(avatarDirectory);
            if (avatarName.Length != 32 || !Guid.TryParseExact(avatarName, "N", out Guid avatarId))
                continue;
            foreach (string versionDirectory in Directory.EnumerateDirectories(avatarDirectory))
            {
                string versionName = Path.GetFileName(versionDirectory);
                if (!ulong.TryParse(
                        versionName,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out ulong version)
                    || version == 0)
                {
                    continue;
                }
                published.Add(new AvatarPublishedEntry(
                    AvatarStorageKey.Create(avatarId, version),
                    Directory.GetLastWriteTimeUtc(versionDirectory)));
            }
        }

        return Task.FromResult(new AvatarStorageInventory(staging, published));
    }

    private string StagingDirectory(AvatarStagingHandle handle)
    {
        handle.EnsureValid();
        return SafeCombine(_stagingRoot, handle.OperationId.ToString("N"));
    }

    private string StagingSourceDirectory(AvatarStagingHandle handle)
        => SafeCombine(StagingDirectory(handle), "source");

    private string StagingVariantsDirectory(AvatarStagingHandle handle)
        => SafeCombine(StagingDirectory(handle), "variants");

    private string OriginalPath(AvatarStagingHandle handle)
        => SafeCombine(StagingSourceDirectory(handle), "upload.bin");

    private string StagedVariantPath(AvatarStagingHandle handle, int size)
    {
        EnsureVariantSize(size);
        return SafeCombine(StagingVariantsDirectory(handle), $"{size}.png");
    }

    private string StorageDirectory(AvatarStorageKey storageKey)
        => SafeCombine(_root, storageKey.Value.Replace('/', Path.DirectorySeparatorChar));

    private string UnderRoot(string name) => SafeCombine(_root, name);

    private string SafeCombine(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!candidate.StartsWith(fullRoot, StringComparison.Ordinal))
            throw new AvatarStorageException("Un chemin avatar sort de la racine autorisee.");
        return candidate;
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        long total = 0;
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > maximumBytes)
                throw new AvatarStorageException("L'image originale depasse 8 Mio.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<bool> HasPngSignatureAsync(string path, CancellationToken cancellationToken)
    {
        byte[] signature = new byte[PngSignature.Length];
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = await stream.ReadAsync(signature, cancellationToken);
        return read == signature.Length && signature.AsSpan().SequenceEqual(PngSignature);
    }

    private static void EnsureVariantSize(int size)
    {
        if (!AvatarVariantSizes.IsSupported(size))
            throw new ArgumentOutOfRangeException(nameof(size), "Taille de variante avatar non supportee.");
    }

    private static void DeleteEmptyParent(string childPath)
    {
        string? parent = Path.GetDirectoryName(childPath);
        if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void DeleteDirectoryRequired(string path, string errorMessage)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AvatarStorageException(errorMessage, exception);
        }

        if (Directory.Exists(path))
            throw new AvatarStorageException(errorMessage);
    }
}
