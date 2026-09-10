using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WotLK.Launcher.Installer.Setup;

internal static class InstallerProtectedPathSecurity
{
    private const uint FileWriteDataOrAddFile = 0x0002;
    private const uint FileAppendDataOrAddSubdirectory = 0x0004;
    private const uint FileWriteExtendedAttributes = 0x0010;
    private const uint FileDeleteChild = 0x0040;
    private const uint FileWriteAttributes = 0x0100;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDacAccess = 0x00040000;
    private const uint WriteOwnerAccess = 0x00080000;
    private const uint GenericAllAccess = 0x10000000;
    private const uint GenericWriteAccess = 0x40000000;
    private const uint MutationAccess =
        FileWriteDataOrAddFile
        | FileAppendDataOrAddSubdirectory
        | FileWriteExtendedAttributes
        | FileDeleteChild
        | FileWriteAttributes
        | DeleteAccess
        | WriteDacAccess
        | WriteOwnerAccess
        | GenericAllAccess
        | GenericWriteAccess;

    private static readonly HashSet<string> TrustedWriterSids = new(StringComparer.Ordinal)
    {
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        // NT SERVICE\TrustedInstaller
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    };

    internal static void DemandTrustedDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        InstallerPathValidator.DemandNoReparsePoints(fullPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                "Le dossier protégé attendu n'existe pas : " + fullPath);
        }

        DemandTrustedOwnerAndAcl(fullPath, isDirectory: true);
    }

    internal static void DemandTrustedFile(string file)
    {
        string fullPath = Path.GetFullPath(file);
        InstallerPathValidator.DemandNoReparsePoints(fullPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Le fichier protégé attendu n'existe pas.", fullPath);
        }

        DemandTrustedOwnerAndAcl(fullPath, isDirectory: false);
    }

    private static void DemandTrustedOwnerAndAcl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Access | AccessControlSections.Owner)
            : FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Access | AccessControlSections.Owner);
        SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException("Le propriétaire du chemin protégé est introuvable.");
        if (!TrustedWriterSids.Contains(owner.Value))
        {
            throw new UnauthorizedAccessException(
                "Le propriétaire du chemin d'installation protégé n'est pas fiable.");
        }

        RawSecurityDescriptor raw = new(security.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException(
                "Le chemin d'installation protégé ne possède pas de DACL.");
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
            if ((granted & MutationAccess) != 0)
            {
                throw new UnauthorizedAccessException(
                    "Un principal non privilégié peut modifier le chemin d'installation protégé.");
            }
        }
    }

    private static bool IsTrustedWriter(SecurityIdentifier sid, SecurityIdentifier owner)
    {
        if (TrustedWriterSids.Contains(sid.Value))
        {
            return true;
        }

        return TrustedWriterSids.Contains(owner.Value)
               && (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
                   || string.Equals(sid.Value, "S-1-3-4", StringComparison.Ordinal));
    }
}
