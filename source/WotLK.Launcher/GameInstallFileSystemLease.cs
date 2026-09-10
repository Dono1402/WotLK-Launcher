using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WotLK.Launcher;

internal enum GameInstallRootLeaseMode
{
    PrepareInstall,
    ExistingClient
}

internal interface IGameInstallRootLease : IDisposable
{
    string InstallRoot { get; }

    GameInstallRootOwnership Ownership { get; }

    void Revalidate();

    IGameInstallDirectoryLease AcquireDirectory(string directoryPath, bool createIfMissing);

    IGameInstallReadLease OpenFileForRead(string filePath, bool demandSingleHardLink = true);

    IGameInstallFileReplacementLease OpenFileForReplacement(string filePath);

    void WriteFileAtomically(string filePath, Action<FileStream> writer);
}

internal interface IGameInstallDirectoryLease : IDisposable
{
    string DirectoryPath { get; }

    void Revalidate();

    void DemandChildFileSafe(string filePath, bool allowMissing);

    void DemandChildDirectorySafe(string directoryPath, bool allowMissing);

    void NormalizeChildFileAttributes(string filePath);

    void DeleteChildFile(string filePath);

    bool TryDeleteChildDirectoryIfEmpty(string directoryPath);
}

internal interface IGameInstallReadLease : IDisposable
{
    FileStream Stream { get; }
}

internal interface IGameInstallFileReplacementLease : IDisposable
{
    FileStream Stream { get; }

    void ReplaceFile(string destinationPath);
}

internal static partial class GameInstallServices
{
    private const uint GenericWriteAccess = 0x40000000;
    private const uint CreateNewDisposition = 1;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int FileRenameInfoClass = 3;

    internal static IGameInstallRootLease AcquireGameInstallRootLease(
        string installRoot,
        GameInstallRootLeaseMode mode)
    {
        string root = NormalizeAndValidateGameRoot(installRoot);
        if (mode == GameInstallRootLeaseMode.ExistingClient && !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Le dossier du client n’existe pas.");
        }

        DemandNoReparsePoints(root, requireLeaf: false);
        List<SafeFileHandle> chain = OpenOrCreateLockedDirectoryChain(
            root,
            createLeaf: mode == GameInstallRootLeaseMode.PrepareInstall);
        try
        {
            SafeFileHandle rootHandle = chain[^1];
            ByHandleFileInformation identity = ReadFileInformation(rootHandle, root);
            GameInstallRootOwnership ownership;
            if (TryReadInstallRootOwnership(root, out GameInstallRootOwnership? existing))
            {
                ownership = existing;
            }
            else
            {
                bool initiallyEmpty = !Directory.EnumerateFileSystemEntries(root).Any();
                if (mode == GameInstallRootLeaseMode.ExistingClient || !initiallyEmpty)
                {
                    DemandRecognizedWotlkClientRoot(root);
                }

                ownership = WriteInstallRootOwnership(
                    root,
                    Path.Combine(root, InstallRootOwnershipMarkerFileName));
                if (initiallyEmpty)
                {
                    string markerPath = Path.Combine(
                        root,
                        InstallRootOwnershipMarkerFileName);
                    string? unexpected = Directory
                        .EnumerateFileSystemEntries(root)
                        .FirstOrDefault(path => !SamePath(path, markerPath));
                    if (unexpected is not null)
                    {
                        DeleteFreshOwnershipMarker(markerPath);
                        throw new InvalidDataException(
                            "Le dossier WotLK a reçu un contenu étranger pendant sa prise de propriété: "
                            + unexpected);
                    }
                }
            }

            WindowsGameInstallRootLease lease = new(
                root,
                ownership,
                chain,
                identity);
            chain = [];
            lease.Revalidate();
            return lease;
        }
        finally
        {
            DisposeHandles(chain);
        }
    }

    internal static IGameInstallRootLease CreateNoOpGameInstallRootLeaseForTests(
        string installRoot)
        => new NoOpGameInstallRootLease(Path.GetFullPath(installRoot));

    internal static string DemandLeaseMatchesGameRoot(
        string installRoot,
        IGameInstallRootLease rootLease)
    {
        ArgumentNullException.ThrowIfNull(rootLease);
        string root = Path.GetFullPath(installRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!SamePath(root, rootLease.InstallRoot))
        {
            throw new InvalidDataException(
                "Le verrou du système de fichiers ne correspond pas au dossier WotLK.");
        }

        rootLease.Revalidate();
        return root;
    }

    private static List<SafeFileHandle> OpenOrCreateLockedDirectoryChain(
        string directoryPath,
        bool createLeaf)
    {
        string fullPath = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string volumeRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("La racine du volume WotLK est introuvable.");
        string relative = Path.GetRelativePath(volumeRoot, fullPath);
        string[] segments = relative == "."
            ? []
            : relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("Le chemin WotLK sort de son volume.");
        }

        List<SafeFileHandle> handles = [];
        string current = volumeRoot;
        try
        {
            handles.Add(OpenValidatedFileSystemEntry(
                current,
                expectDirectory: true,
                denyDeleteSharing: true,
                desiredAccess: GenericReadAccess));
            for (int index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                if (!Directory.Exists(current))
                {
                    if (!createLeaf)
                    {
                        throw new DirectoryNotFoundException(
                            "Un dossier WotLK requis est absent: " + current);
                    }

                    // Build the requested root one component at a time. The
                    // already opened parent is held without delete sharing, so
                    // it cannot be swapped for a junction between creation and
                    // the no-follow open below.
                    Directory.CreateDirectory(current);
                }

                handles.Add(OpenValidatedFileSystemEntry(
                    current,
                    expectDirectory: true,
                    denyDeleteSharing: true,
                    desiredAccess: GenericReadAccess));
            }

            return handles;
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    private static List<SafeFileHandle> OpenOrCreateLockedDescendantChain(
        WindowsGameInstallRootLease rootLease,
        string directoryPath,
        bool createIfMissing)
    {
        string fullPath = DemandPathInsideLeaseRoot(rootLease.InstallRoot, directoryPath);
        string relative = Path.GetRelativePath(rootLease.InstallRoot, fullPath);
        if (relative == ".")
        {
            return [];
        }

        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("Le chemin WotLK sort de la racine verrouillée.");
        }

        List<SafeFileHandle> handles = [];
        string current = rootLease.InstallRoot;
        try
        {
            rootLease.Revalidate();
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                {
                    if (!createIfMissing)
                    {
                        throw new DirectoryNotFoundException(
                            "Un dossier WotLK requis est absent: " + current);
                    }

                    // Create one component at a time while its parent handle is
                    // held. An attacker-created junction is rejected by the
                    // no-follow open immediately below.
                    Directory.CreateDirectory(current);
                }

                handles.Add(OpenValidatedFileSystemEntry(
                    current,
                    expectDirectory: true,
                    denyDeleteSharing: true,
                    desiredAccess: GenericReadAccess));
            }

            return handles;
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    private static string DemandPathInsideLeaseRoot(string root, string path)
    {
        string fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!SamePath(root, fullPath) && !IsPathInside(root, fullPath))
        {
            throw new InvalidDataException(
                "Le chemin WotLK sort de la racine verrouillée: " + path);
        }

        return fullPath;
    }

    private static bool SameFileSystemIdentity(
        ByHandleFileInformation left,
        ByHandleFileInformation right)
        => left.VolumeSerialNumber == right.VolumeSerialNumber
           && left.FileIndexHigh == right.FileIndexHigh
           && left.FileIndexLow == right.FileIndexLow;

    private static FileStream CreateStableReplacementFile(string path)
    {
        SafeFileHandle handle = CreateFileW(
            Path.GetFullPath(path),
            GenericWriteAccess | DeleteAccess,
            FileShareRead,
            securityAttributes: IntPtr.Zero,
            CreateNewDisposition,
            FileAttributeNormal | FileFlagWriteThrough | FileFlagOpenReparsePoint,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Impossible de créer un fichier temporaire WotLK verrouillé: " + path,
                new Win32Exception(error));
        }

        try
        {
            ByHandleFileInformation information = ReadFileInformation(handle, path);
            if ((information.FileAttributes & (uint)(FileAttributes.Directory
                                                     | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "Le fichier temporaire WotLK n’est pas un fichier ordinaire.");
            }

            DemandSingleHardLink(handle, path);
            return new FileStream(
                handle,
                FileAccess.Write,
                128 * 1024,
                isAsync: false);
        }
        catch
        {
            TryDeleteFileByHandle(handle, path);
            handle.Dispose();
            throw;
        }
    }

    private static FileStream OpenStableReplacementReadStream(string path)
    {
        SafeFileHandle handle = OpenValidatedFileSystemEntry(
            path,
            expectDirectory: false,
            denyDeleteSharing: true,
            desiredAccess: GenericReadAccess | DeleteAccess,
            shareModeOverride: FileShareRead);
        try
        {
            return new FileStream(
                handle,
                FileAccess.Read,
                128 * 1024,
                isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ReplaceFileByHandle(
        SafeFileHandle sourceHandle,
        string destinationPath,
        bool replaceIfExists = true)
    {
        byte[] destinationBytes = System.Text.Encoding.Unicode.GetBytes(
            Path.GetFullPath(destinationPath));
        int rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        int fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        int fileNameOffset = fileNameLengthOffset + sizeof(uint);
        byte[] renameInformation = new byte[fileNameOffset + destinationBytes.Length];
        renameInformation[0] = replaceIfExists ? (byte)1 : (byte)0;
        BitConverter.GetBytes(destinationBytes.Length).CopyTo(
            renameInformation,
            fileNameLengthOffset);
        destinationBytes.CopyTo(renameInformation, fileNameOffset);

        GCHandle buffer = GCHandle.Alloc(renameInformation, GCHandleType.Pinned);
        try
        {
            if (!SetFileInformationByHandle(
                    sourceHandle,
                    FileRenameInfoClass,
                    buffer.AddrOfPinnedObject(),
                    (uint)renameInformation.Length))
            {
                throw new IOException(
                    "Impossible de remplacer le fichier WotLK verrouillé: "
                    + destinationPath,
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally
        {
            buffer.Free();
        }
    }

    private static void TryDeleteFileByHandle(
        SafeFileHandle handle,
        string path)
    {
        try
        {
            DemandSingleHardLink(handle, path);
            MarkDirectoryForDeletion(handle, path);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
        }
    }

    private static void DeleteFreshOwnershipMarker(string markerPath)
    {
        using SafeFileHandle handle = OpenValidatedFileSystemEntry(
            markerPath,
            expectDirectory: false,
            denyDeleteSharing: true,
            desiredAccess: DeleteAccess);
        DemandSingleHardLink(handle, markerPath);
        File.SetAttributes(markerPath, FileAttributes.Normal);
        MarkDirectoryForDeletion(handle, markerPath);
    }

    private sealed class WindowsGameInstallRootLease : IGameInstallRootLease
    {
        private readonly ByHandleFileInformation _rootIdentity;
        private List<SafeFileHandle>? _chain;

        internal WindowsGameInstallRootLease(
            string installRoot,
            GameInstallRootOwnership ownership,
            List<SafeFileHandle> chain,
            ByHandleFileInformation rootIdentity)
        {
            InstallRoot = installRoot;
            Ownership = ownership;
            _chain = chain;
            _rootIdentity = rootIdentity;
        }

        public string InstallRoot { get; }

        public GameInstallRootOwnership Ownership { get; }

        public void Revalidate()
        {
            ThrowIfDisposed();
            DemandNoReparsePoints(InstallRoot, requireLeaf: true);
            using SafeFileHandle current = OpenValidatedFileSystemEntry(
                InstallRoot,
                expectDirectory: true,
                denyDeleteSharing: false);
            if (!SameFileSystemIdentity(
                    _rootIdentity,
                    ReadFileInformation(current, InstallRoot))
                || !TryReadInstallRootOwnership(
                    InstallRoot,
                    out GameInstallRootOwnership? ownership)
                || ownership.OwnershipId != Ownership.OwnershipId)
            {
                throw new InvalidDataException(
                    "L’identité du dossier WotLK a changé pendant l’opération.");
            }
        }

        public IGameInstallDirectoryLease AcquireDirectory(
            string directoryPath,
            bool createIfMissing)
        {
            ThrowIfDisposed();
            string fullPath = DemandPathInsideLeaseRoot(InstallRoot, directoryPath);
            List<SafeFileHandle> handles = OpenOrCreateLockedDescendantChain(
                this,
                fullPath,
                createIfMissing);
            try
            {
                ByHandleFileInformation identity = handles.Count == 0
                    ? _rootIdentity
                    : ReadFileInformation(handles[^1], fullPath);
                WindowsGameInstallDirectoryLease lease = new(
                    this,
                    fullPath,
                    handles,
                    identity);
                handles = [];
                lease.Revalidate();
                return lease;
            }
            finally
            {
                DisposeHandles(handles);
            }
        }

        public IGameInstallReadLease OpenFileForRead(
            string filePath,
            bool demandSingleHardLink = true)
        {
            ThrowIfDisposed();
            string fullPath = DemandPathInsideLeaseRoot(InstallRoot, filePath);
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("Le fichier WotLK n’a pas de dossier parent.");
            IGameInstallDirectoryLease parentLease = AcquireDirectory(parent, createIfMissing: false);
            try
            {
                parentLease.DemandChildFileSafe(fullPath, allowMissing: false);
                FileStream stream = OpenStableReadStream(fullPath);
                try
                {
                    if (demandSingleHardLink)
                    {
                        DemandSingleHardLink(stream.SafeFileHandle, fullPath);
                    }

                    WindowsGameInstallReadLease lease = new(parentLease, stream);
                    parentLease = null!;
                    stream = null!;
                    return lease;
                }
                finally
                {
                    stream?.Dispose();
                }
            }
            finally
            {
                parentLease?.Dispose();
            }
        }

        public IGameInstallFileReplacementLease OpenFileForReplacement(string filePath)
        {
            ThrowIfDisposed();
            string fullPath = DemandPathInsideLeaseRoot(InstallRoot, filePath);
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("Le fichier WotLK n’a pas de dossier parent.");
            IGameInstallDirectoryLease parentLease = AcquireDirectory(parent, createIfMissing: false);
            try
            {
                parentLease.DemandChildFileSafe(fullPath, allowMissing: false);
                FileStream stream = OpenStableReplacementReadStream(fullPath);
                try
                {
                    DemandSingleHardLink(stream.SafeFileHandle, fullPath);
                    WindowsGameInstallFileReplacementLease lease = new(
                        parentLease,
                        stream,
                        fullPath);
                    parentLease = null!;
                    stream = null!;
                    return lease;
                }
                finally
                {
                    stream?.Dispose();
                }
            }
            finally
            {
                parentLease?.Dispose();
            }
        }

        public void WriteFileAtomically(string filePath, Action<FileStream> writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ThrowIfDisposed();
            string fullPath = DemandPathInsideLeaseRoot(InstallRoot, filePath);
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("Le fichier WotLK n’a pas de dossier parent.");
            using IGameInstallDirectoryLease parentLease = AcquireDirectory(
                parent,
                createIfMissing: true);
            parentLease.DemandChildFileSafe(fullPath, allowMissing: true);
            string temporary = fullPath
                + ".new-"
                + Guid.NewGuid().ToString("N")
                + ".tmp";
            try
            {
                using (FileStream stream = CreateStableReplacementFile(temporary))
                {
                    writer(stream);
                    stream.Flush(flushToDisk: true);
                    DemandSingleHardLink(stream.SafeFileHandle, temporary);
                    parentLease.Revalidate();
                    parentLease.DemandChildFileSafe(fullPath, allowMissing: true);
                    if (File.Exists(fullPath))
                    {
                        parentLease.NormalizeChildFileAttributes(fullPath);
                    }

                    ReplaceFileByHandle(stream.SafeFileHandle, fullPath);
                }
            }
            finally
            {
                TryDeleteLeaseTemporaryFile(temporary, parentLease);
            }
        }

        public void Dispose()
        {
            List<SafeFileHandle>? handles = Interlocked.Exchange(ref _chain, null);
            if (handles is not null)
            {
                DisposeHandles(handles);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_chain is null)
            {
                throw new ObjectDisposedException(nameof(IGameInstallRootLease));
            }
        }
    }

    private sealed class WindowsGameInstallDirectoryLease : IGameInstallDirectoryLease
    {
        private readonly WindowsGameInstallRootLease _rootLease;
        private readonly ByHandleFileInformation _identity;
        private List<SafeFileHandle>? _handles;

        internal WindowsGameInstallDirectoryLease(
            WindowsGameInstallRootLease rootLease,
            string directoryPath,
            List<SafeFileHandle> handles,
            ByHandleFileInformation identity)
        {
            _rootLease = rootLease;
            DirectoryPath = directoryPath;
            _handles = handles;
            _identity = identity;
        }

        public string DirectoryPath { get; }

        public void Revalidate()
        {
            ThrowIfDisposed();
            _rootLease.Revalidate();
            using SafeFileHandle current = OpenValidatedFileSystemEntry(
                DirectoryPath,
                expectDirectory: true,
                denyDeleteSharing: false);
            if (!SameFileSystemIdentity(
                    _identity,
                    ReadFileInformation(current, DirectoryPath)))
            {
                throw new InvalidDataException(
                    "L’identité d’un dossier WotLK a changé pendant l’opération.");
            }
        }

        public void DemandChildFileSafe(string filePath, bool allowMissing)
        {
            string fullPath = DemandDirectChild(filePath);
            Revalidate();
            if (!File.Exists(fullPath))
            {
                if (Directory.Exists(fullPath))
                {
                    throw new InvalidDataException(
                        "Un dossier remplace un fichier WotLK attendu: " + fullPath);
                }

                DemandNoReparsePoints(fullPath, requireLeaf: false);
                if (!allowMissing)
                {
                    throw new FileNotFoundException(
                        "Un fichier WotLK requis est absent.",
                        fullPath);
                }

                return;
            }

            DemandNoReparsePoints(fullPath, requireLeaf: true);
            using SafeFileHandle handle = OpenValidatedFileSystemEntry(
                fullPath,
                expectDirectory: false,
                denyDeleteSharing: true);
            DemandSingleHardLink(handle, fullPath);
        }

        public void DemandChildDirectorySafe(string directoryPath, bool allowMissing)
        {
            string fullPath = DemandDirectChild(directoryPath);
            Revalidate();
            if (!Directory.Exists(fullPath))
            {
                if (File.Exists(fullPath))
                {
                    throw new InvalidDataException(
                        "Un fichier remplace un dossier WotLK attendu: " + fullPath);
                }

                DemandNoReparsePoints(fullPath, requireLeaf: false);
                if (!allowMissing)
                {
                    throw new DirectoryNotFoundException(
                        "Un dossier WotLK requis est absent: " + fullPath);
                }

                return;
            }

            using SafeFileHandle handle = OpenValidatedFileSystemEntry(
                fullPath,
                expectDirectory: true,
                denyDeleteSharing: true);
        }

        public void NormalizeChildFileAttributes(string filePath)
        {
            string fullPath = DemandDirectChild(filePath);
            Revalidate();
            using SafeFileHandle handle = OpenValidatedFileSystemEntry(
                fullPath,
                expectDirectory: false,
                denyDeleteSharing: true,
                desiredAccess: FileWriteAttributesAccess);
            DemandSingleHardLink(handle, fullPath);
            File.SetAttributes(fullPath, FileAttributes.Normal);
        }

        public void DeleteChildFile(string filePath)
        {
            string fullPath = DemandDirectChild(filePath);
            Revalidate();
            using SafeFileHandle handle = OpenValidatedFileSystemEntry(
                fullPath,
                expectDirectory: false,
                denyDeleteSharing: true,
                desiredAccess: DeleteAccess);
            DemandSingleHardLink(handle, fullPath);

            // The open handle denies rename/delete and the single-link check
            // prevents a path-based attribute change from touching a second
            // name outside the install tree.
            File.SetAttributes(fullPath, FileAttributes.Normal);
            MarkDirectoryForDeletion(handle, fullPath);
        }

        public bool TryDeleteChildDirectoryIfEmpty(string directoryPath)
        {
            string fullPath = DemandDirectChild(directoryPath);
            Revalidate();
            DemandChildDirectorySafe(fullPath, allowMissing: true);
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            using SafeFileHandle handle = OpenValidatedFileSystemEntry(
                fullPath,
                expectDirectory: true,
                denyDeleteSharing: true,
                desiredAccess: DeleteAccess);
            if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                return false;
            }

            MarkDirectoryForDeletion(handle, fullPath);
            return true;
        }

        public void Dispose()
        {
            List<SafeFileHandle>? handles = Interlocked.Exchange(ref _handles, null);
            if (handles is not null)
            {
                DisposeHandles(handles);
            }
        }

        private string DemandDirectChild(string path)
        {
            ThrowIfDisposed();
            string fullPath = DemandPathInsideLeaseRoot(_rootLease.InstallRoot, path);
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("Le chemin WotLK n’a pas de parent.");
            if (!SamePath(parent, DirectoryPath))
            {
                throw new InvalidDataException(
                    "La destination WotLK n’est pas un enfant direct du dossier verrouillé.");
            }

            return fullPath;
        }

        private void ThrowIfDisposed()
        {
            if (_handles is null)
            {
                throw new ObjectDisposedException(nameof(IGameInstallDirectoryLease));
            }
        }
    }

    private sealed class WindowsGameInstallReadLease : IGameInstallReadLease
    {
        private IGameInstallDirectoryLease? _parentLease;
        private FileStream? _stream;

        internal WindowsGameInstallReadLease(
            IGameInstallDirectoryLease parentLease,
            FileStream stream)
        {
            _parentLease = parentLease;
            _stream = stream;
        }

        public FileStream Stream => _stream
            ?? throw new ObjectDisposedException(nameof(IGameInstallReadLease));

        public void Dispose()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            Interlocked.Exchange(ref _parentLease, null)?.Dispose();
        }
    }

    private sealed class WindowsGameInstallFileReplacementLease
        : IGameInstallFileReplacementLease
    {
        private readonly string _sourcePath;
        private IGameInstallDirectoryLease? _parentLease;
        private FileStream? _stream;

        internal WindowsGameInstallFileReplacementLease(
            IGameInstallDirectoryLease parentLease,
            FileStream stream,
            string sourcePath)
        {
            _parentLease = parentLease;
            _stream = stream;
            _sourcePath = sourcePath;
        }

        public FileStream Stream => _stream
            ?? throw new ObjectDisposedException(nameof(IGameInstallFileReplacementLease));

        public void ReplaceFile(string destinationPath)
        {
            FileStream stream = _stream
                ?? throw new ObjectDisposedException(nameof(IGameInstallFileReplacementLease));
            IGameInstallDirectoryLease parentLease = _parentLease
                ?? throw new ObjectDisposedException(nameof(IGameInstallFileReplacementLease));
            if (SamePath(_sourcePath, destinationPath))
            {
                throw new InvalidDataException(
                    "Le fichier WotLK temporaire ne peut pas se remplacer lui-même.");
            }

            DemandSingleHardLink(stream.SafeFileHandle, _sourcePath);
            parentLease.Revalidate();
            parentLease.DemandChildFileSafe(destinationPath, allowMissing: true);
            if (File.Exists(destinationPath))
            {
                parentLease.NormalizeChildFileAttributes(destinationPath);
            }

            ReplaceFileByHandle(stream.SafeFileHandle, destinationPath);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            Interlocked.Exchange(ref _parentLease, null)?.Dispose();
        }
    }

    private sealed class NoOpGameInstallRootLease : IGameInstallRootLease
    {
        internal NoOpGameInstallRootLease(string installRoot)
        {
            InstallRoot = installRoot;
            Ownership = new GameInstallRootOwnership(installRoot, Guid.Empty);
        }

        public string InstallRoot { get; }

        public GameInstallRootOwnership Ownership { get; }

        public void Revalidate()
        {
        }

        public IGameInstallDirectoryLease AcquireDirectory(
            string directoryPath,
            bool createIfMissing)
        {
            string fullPath = Path.GetFullPath(directoryPath);
            if (createIfMissing)
            {
                Directory.CreateDirectory(fullPath);
            }

            return new NoOpGameInstallDirectoryLease(fullPath);
        }

        public IGameInstallReadLease OpenFileForRead(
            string filePath,
            bool demandSingleHardLink = true)
            => new NoOpGameInstallReadLease(new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read));

        public IGameInstallFileReplacementLease OpenFileForReplacement(string filePath)
            => new NoOpGameInstallFileReplacementLease(
                Path.GetFullPath(filePath),
                new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read));

        public void WriteFileAtomically(string filePath, Action<FileStream> writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            string temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    writer(stream);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, filePath, overwrite: true);
            }
            finally
            {
                TryDeleteLeaseTemporaryFile(temporary);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoOpGameInstallDirectoryLease(string directoryPath)
        : IGameInstallDirectoryLease
    {
        public string DirectoryPath { get; } = directoryPath;

        public void Revalidate()
        {
        }

        public void DemandChildFileSafe(string filePath, bool allowMissing)
        {
            if (!allowMissing && !File.Exists(filePath))
            {
                throw new FileNotFoundException("Fichier absent.", filePath);
            }
        }

        public void DemandChildDirectorySafe(string directoryPath, bool allowMissing)
        {
            if (!allowMissing && !Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException(directoryPath);
            }
        }

        public void NormalizeChildFileAttributes(string filePath)
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }

        public void DeleteChildFile(string filePath)
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
            File.Delete(filePath);
        }

        public bool TryDeleteChildDirectoryIfEmpty(string directoryPath)
        {
            if (!Directory.Exists(directoryPath)
                || Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                return false;
            }

            Directory.Delete(directoryPath);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoOpGameInstallReadLease(FileStream stream)
        : IGameInstallReadLease
    {
        public FileStream Stream { get; } = stream;

        public void Dispose() => Stream.Dispose();
    }

    private sealed class NoOpGameInstallFileReplacementLease(
        string sourcePath,
        FileStream stream)
        : IGameInstallFileReplacementLease
    {
        private FileStream? _stream = stream;

        public FileStream Stream => _stream
            ?? throw new ObjectDisposedException(nameof(IGameInstallFileReplacementLease));

        public void ReplaceFile(string destinationPath)
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            File.Move(sourcePath, destinationPath, overwrite: true);
        }

        public void Dispose()
            => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    private static void TryDeleteLeaseTemporaryFile(
        string path,
        IGameInstallDirectoryLease parentLease)
    {
        try
        {
            parentLease.DemandChildFileSafe(path, allowMissing: true);
            if (File.Exists(path))
            {
                parentLease.DeleteChildFile(path);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
        }
    }

    private static void TryDeleteLeaseTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException)
        {
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
