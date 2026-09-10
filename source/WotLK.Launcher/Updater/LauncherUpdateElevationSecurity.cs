using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WotLK.Launcher.Updater;

internal static class LauncherUpdateElevationSecurity
{
    private const string ProtectedRootName = ".atlas-self-update";
    private const uint FileAddSubdirectory = 0x0004;
    private const uint FileWriteData = 0x0002;
    private const uint FileAppendData = 0x0004;
    private const uint FileWriteExtendedAttributes = 0x0010;
    private const uint FileDeleteChild = 0x0040;
    private const uint FileWriteAttributes = 0x0100;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDacAccess = 0x00040000;
    private const uint WriteOwnerAccess = 0x00080000;
    private const uint GenericAllAccess = 0x10000000;
    private const uint GenericWriteAccess = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileMutationAccess =
        FileWriteData
        | FileAppendData
        | FileWriteExtendedAttributes
        | FileWriteAttributes
        | DeleteAccess
        | WriteDacAccess
        | WriteOwnerAccess
        | GenericAllAccess
        | GenericWriteAccess;
    private static readonly HashSet<string> TrustedWriterSids = new(
        StringComparer.Ordinal)
    {
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        // NT SERVICE\TrustedInstaller
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    };

    internal static string GetProtectedHelperPath(string targetPath, Guid transactionId)
    {
        if (transactionId == Guid.Empty)
        {
            throw new InvalidDataException("Identifiant de transaction absent.");
        }

        string target = Path.GetFullPath(targetPath);
        string targetDirectory = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("Dossier du launcher absent.");
        return Path.Combine(
            targetDirectory,
            ProtectedRootName,
            transactionId.ToString("N"),
            "updater.exe");
    }

    internal static string GetProtectedHelperAcceptedSignalPath(
        string targetPath,
        Guid transactionId)
    {
        string helper = GetProtectedHelperPath(targetPath, transactionId);
        string directory = Path.GetDirectoryName(helper)
            ?? throw new InvalidDataException("Dossier du helper protégé absent.");
        return Path.Combine(directory, "helper-accepted.json");
    }

    internal static string GetProtectedCommitSignalPath(
        string targetPath,
        Guid transactionId)
    {
        string helper = GetProtectedHelperPath(targetPath, transactionId);
        string directory = Path.GetDirectoryName(helper)
            ?? throw new InvalidDataException("Dossier du helper protégé absent.");
        return Path.Combine(directory, "committed.json");
    }

    internal static void DemandProtectedTargetForElevation(
        LauncherUpdateTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        string target = Path.GetFullPath(transaction.TargetPath);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException("Launcher cible absent.", target);
        }

        DemandNoReparseTransactionPaths(transaction);
        string targetDirectory = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("Dossier du launcher absent.");
        if (IsDirectoryWritableByCurrentUser(targetDirectory))
        {
            throw new UnauthorizedAccessException(
                "La mise à jour élevée est refusée depuis un dossier modifiable par l'utilisateur.");
        }

        DemandNoUntrustedFileControl(target);
        DemandDirectoryCannotBeReplacedByCurrentUser(targetDirectory);
        DemandNoReparseTransactionPaths(transaction);
    }

    internal static void DemandProtectedHelperForElevation(
        LauncherUpdateTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        string expected = GetProtectedHelperPath(
            transaction.TargetPath,
            transaction.TransactionId);
        if (!SamePath(expected, transaction.HelperPath)
            || !File.Exists(expected))
        {
            throw new FileNotFoundException(
                "Helper protégé de mise à jour absent.",
                expected);
        }

        DemandProtectedHelperArtifactForElevation(transaction, expected);

        string helperHash = LauncherUpdateTransactionStore.ComputeSha256Async(
                expected,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (!string.Equals(
                helperHash,
                transaction.PreviousSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Helper protégé de mise à jour invalide.");
        }
    }

    internal static void DemandProtectedHelperArtifactForElevation(
        LauncherUpdateTransaction transaction,
        string path)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        string fullPath = Path.GetFullPath(path);
        string helper = GetProtectedHelperPath(
            transaction.TargetPath,
            transaction.TransactionId);
        string accepted = GetProtectedHelperAcceptedSignalPath(
            transaction.TargetPath,
            transaction.TransactionId);
        string committed = GetProtectedCommitSignalPath(
            transaction.TargetPath,
            transaction.TransactionId);
        if ((!SamePath(fullPath, helper)
             && !SamePath(fullPath, accepted)
             && !SamePath(fullPath, committed))
            || !File.Exists(fullPath))
        {
            throw new InvalidDataException(
                "Artefact protégé du helper non contrôlé.");
        }

        DemandNoReparseTransactionPaths(transaction);
        ValidateNoReparsePoints(fullPath);
        string helperDirectory = Path.GetDirectoryName(helper)
            ?? throw new InvalidDataException("Dossier du helper absent.");
        if (IsDirectoryWritableByCurrentUser(helperDirectory))
        {
            throw new UnauthorizedAccessException(
                "Un artefact du helper est modifiable par l'utilisateur.");
        }

        DemandNoUntrustedFileControl(fullPath);
        DemandDirectoryCannotBeReplacedByCurrentUser(helperDirectory);
        DemandNoReparseTransactionPaths(transaction);
        ValidateNoReparsePoints(fullPath);
    }

    internal static void DemandProtectedSwapFileForElevation(
        LauncherUpdateTransaction transaction,
        string path)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        string fullPath = Path.GetFullPath(path);
        if ((!SamePath(fullPath, transaction.StagedPath)
             && !SamePath(fullPath, transaction.BackupPath))
            || !File.Exists(fullPath))
        {
            throw new InvalidDataException(
                "Fichier intermédiaire de mise à jour non contrôlé.");
        }

        DemandNoReparseTransactionPaths(transaction);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(
                "Dossier du fichier intermédiaire absent.");
        if (IsDirectoryWritableByCurrentUser(directory))
        {
            throw new UnauthorizedAccessException(
                "Un fichier intermédiaire de mise à jour est modifiable par l'utilisateur.");
        }

        DemandNoUntrustedFileControl(fullPath);
        DemandDirectoryCannotBeReplacedByCurrentUser(directory);
        DemandNoReparseTransactionPaths(transaction);
    }

    internal static void DemandProtectedDirectoryForElevation(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                "Dossier protégé absent: " + fullPath);
        }

        ValidateNoReparsePoints(fullPath);
        if (IsDirectoryWritableByCurrentUser(fullPath)
            || HasDirectoryAccess(fullPath, WriteDacAccess)
            || HasDirectoryAccess(fullPath, WriteOwnerAccess))
        {
            throw new UnauthorizedAccessException(
                "Le dossier protégé est contrôlable par l'utilisateur.");
        }

        DemandDirectoryCannotBeReplacedByCurrentUser(fullPath);
        ValidateNoReparsePoints(fullPath);
    }

    internal static void DemandProtectedFileForElevation(string file)
    {
        string fullPath = Path.GetFullPath(file);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "Fichier protégé absent: " + fullPath,
                fullPath);
        }

        ValidateNoReparsePoints(fullPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Dossier du fichier protégé absent.");
        if (IsDirectoryWritableByCurrentUser(directory))
        {
            throw new UnauthorizedAccessException(
                "Le dossier du fichier protégé est modifiable par l'utilisateur.");
        }

        DemandNoUntrustedFileControl(fullPath);
        DemandDirectoryCannotBeReplacedByCurrentUser(directory);
        ValidateNoReparsePoints(fullPath);
    }

    internal static void ValidateNoReparsePoints(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Un lien ou un point de jonction est interdit dans le chemin de mise à jour.");
            }

            string trimmed = current.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent) || SamePath(parent, current))
            {
                break;
            }

            current = parent;
        }
    }

    internal static void DemandNoReparseTransactionPaths(
        LauncherUpdateTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        foreach (string path in new[]
                 {
                     transaction.TargetPath,
                     transaction.WorkspacePath,
                     transaction.CandidatePath,
                     transaction.HelperPath,
                     transaction.StagedPath,
                     transaction.BackupPath,
                     transaction.TransactionPath,
                     transaction.HelperAcceptedSignalPath,
                     transaction.StartedSignalPath,
                     transaction.ReadySignalPath
                 })
        {
            ValidateNoReparsePoints(path);
        }
    }

    private static bool IsDirectoryWritableByCurrentUser(string directory)
    {
        string fileProbe = Path.Combine(
            directory,
            ".atlas-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using FileStream stream = new(
                fileProbe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
            return true;
        }
        finally
        {
            LauncherUpdateTransactionStore.TryDeleteFile(fileProbe);
        }

        string directoryProbe = Path.Combine(
            directory,
            ".atlas-directory-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directoryProbe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        finally
        {
            LauncherUpdateTransactionStore.TryDeleteDirectory(directoryProbe);
        }
    }

    private static void DemandDirectoryCannotBeReplacedByCurrentUser(string directory)
    {
        string child = Path.GetFullPath(directory);
        bool isProtectedLeaf = true;
        while (true)
        {
            DemandNoUntrustedOwnerOrAclControl(child, isDirectory: true);
            if (isProtectedLeaf
                && HasUntrustedAllowedAccess(
                    child,
                    isDirectory: true,
                    FileWriteData | FileAppendData | FileDeleteChild))
            {
                throw new UnauthorizedAccessException(
                    "Un principal non privilégié peut modifier le contenu du dossier protégé.");
            }

            if (HasDirectoryAccess(child, WriteDacAccess)
                || HasDirectoryAccess(child, WriteOwnerAccess))
            {
                throw new UnauthorizedAccessException(
                    "Un dossier du launcher permet de modifier les droits pendant l'élévation.");
            }

            string? parent = Path.GetDirectoryName(child.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent) || SamePath(parent, child))
            {
                break;
            }

            bool untrustedCanCreateReplacement = HasUntrustedAllowedAccess(
                parent,
                isDirectory: true,
                FileAddSubdirectory);
            bool untrustedCanRemoveChild = HasUntrustedAllowedAccess(
                    parent,
                    isDirectory: true,
                    FileDeleteChild)
                || HasUntrustedAllowedAccess(
                    child,
                    isDirectory: true,
                    DeleteAccess);
            if (untrustedCanCreateReplacement && untrustedCanRemoveChild)
            {
                throw new UnauthorizedAccessException(
                    "Un principal non privilégié peut remplacer un ancêtre du launcher.");
            }

            // Replacing a protected descendant requires permission to create the
            // substitute in its parent and permission to remove the current child.
            if (HasDirectoryAccess(parent, FileAddSubdirectory)
                && (HasDirectoryAccess(parent, FileDeleteChild)
                    || HasDirectoryAccess(child, DeleteAccess)))
            {
                throw new UnauthorizedAccessException(
                    "Un ancêtre du launcher peut remplacer la cible pendant l'élévation.");
            }

            child = parent;
            isProtectedLeaf = false;
        }
    }

    private static void DemandNoUntrustedFileControl(string path)
    {
        DemandNoUntrustedOwnerOrAclControl(path, isDirectory: false);
        if (HasUntrustedAllowedAccess(path, isDirectory: false, FileMutationAccess))
        {
            throw new UnauthorizedAccessException(
                "Un principal non privilégié peut modifier un fichier de mise à jour protégé.");
        }
    }

    private static void DemandNoUntrustedOwnerOrAclControl(
        string path,
        bool isDirectory)
    {
        FileSystemSecurity security = GetAccessControl(path, isDirectory);
        SecurityIdentifier owner = GetOwnerSid(security);
        if (!IsTrustedWriter(owner, owner)
            || HasUntrustedAllowedAccess(
                security,
                owner,
                WriteDacAccess | WriteOwnerAccess))
        {
            throw new UnauthorizedAccessException(
                "Le propriétaire ou les droits du chemin protégé ne sont pas fiables.");
        }
    }

    private static bool HasUntrustedAllowedAccess(
        string path,
        bool isDirectory,
        uint desiredAccess)
    {
        FileSystemSecurity security = GetAccessControl(path, isDirectory);
        SecurityIdentifier owner = GetOwnerSid(security);
        return HasUntrustedAllowedAccess(security, owner, desiredAccess);
    }

    private static bool HasUntrustedAllowedAccess(
        FileSystemSecurity security,
        SecurityIdentifier owner,
        uint desiredAccess)
    {
        RawSecurityDescriptor raw = new(
            security.GetSecurityDescriptorBinaryForm(),
            0);
        if (raw.DiscretionaryAcl is null)
        {
            return true;
        }

        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0
                || rule.IdentityReference is not SecurityIdentifier sid
                || IsTrustedWriter(sid, owner))
            {
                continue;
            }

            uint granted = unchecked((uint)rule.FileSystemRights);
            if ((granted & desiredAccess) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static FileSystemSecurity GetAccessControl(string path, bool isDirectory) =>
        isDirectory
            ? FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Access | AccessControlSections.Owner)
            : FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Access | AccessControlSections.Owner);

    private static SecurityIdentifier GetOwnerSid(FileSystemSecurity security) =>
        security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
        ?? throw new UnauthorizedAccessException(
            "Le propriétaire du chemin protégé est introuvable.");

    private static bool IsTrustedWriter(
        SecurityIdentifier sid,
        SecurityIdentifier owner)
    {
        if (TrustedWriterSids.Contains(sid.Value))
        {
            return true;
        }

        return owner.Value is not null
               && TrustedWriterSids.Contains(owner.Value)
               && (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
                   || string.Equals(sid.Value, "S-1-3-4", StringComparison.Ordinal));
    }

    private static bool HasDirectoryAccess(string directory, uint desiredAccess)
    {
        using SafeFileHandle handle = CreateFile(
            directory,
            desiredAccess,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return true;
        }

        const int accessDenied = 5;
        return Marshal.GetLastWin32Error() != accessDenied;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        BestFitMapping = false)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
