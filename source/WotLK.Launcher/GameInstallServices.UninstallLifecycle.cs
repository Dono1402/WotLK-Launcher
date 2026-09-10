using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WotLK.Launcher;

internal static partial class GameInstallServices
{
    private const long MaximumTransientHelperBytes = 1024L * 1024L * 1024L;
    private const string TransientCleanupDirectoryName = "GameUninstall";
    private const string TransientCleanupProductDirectoryName = "Atlas Launcher";

    private static readonly SecurityIdentifier LocalSystemSid = new(
        WellKnownSidType.LocalSystemSid,
        domainSid: null);

    private static readonly SecurityIdentifier BuiltinAdministratorsSid = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        domainSid: null);

    internal static TransientGameUninstallWorkspace CreateTransientGameUninstallWorkspace(
        GameUninstallIdentity identity,
        string? localApplicationDataRoot = null,
        string? requesterSid = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string sidValue = requesterSid ?? GetCurrentUserSid();
        if (!TryNormalizeSecurityIdentifier(sidValue, out sidValue))
        {
            throw new InvalidDataException(
                "L'identité Windows du nettoyage WotLK est invalide.");
        }

        string localRoot = ResolveLocalApplicationDataRoot(localApplicationDataRoot);
        string cleanupRoot = Path.Combine(
            localRoot,
            TransientCleanupProductDirectoryName,
            TransientCleanupDirectoryName);
        Directory.CreateDirectory(cleanupRoot);
        DemandNoReparsePoints(cleanupRoot, requireLeaf: true);

        Guid requestId = Guid.NewGuid();
        string workspacePath = Path.Combine(
            cleanupRoot,
            requestId.ToString("N", CultureInfo.InvariantCulture));
        string helperPath = Path.Combine(
            workspacePath,
            TransientCleanupHelperFileName);
        string cleanupEventName = @"Local\Atlas.GameUninstall."
            + requestId.ToString("N", CultureInfo.InvariantCulture);

        List<SafeFileHandle> workspaceAncestorHandles = [];
        List<SafeFileHandle> gameSourceAncestorHandles = [];
        SafeFileHandle? workspaceHandle = null;
        FileStream? sourceStream = null;
        FileStream? helperStableStream = null;
        bool workspaceCreated = false;
        try
        {
            workspaceAncestorHandles = OpenAncestorDirectoryHandles(workspacePath);
            gameSourceAncestorHandles = OpenAncestorDirectoryHandles(
                identity.UninstallerPath);
            sourceStream = OpenStableReadStream(identity.UninstallerPath);
            DemandSingleHardLink(sourceStream.SafeFileHandle, identity.UninstallerPath);
            ValidateStableFileLength(sourceStream, "désinstalleur WotLK");
            string sourceSha256 = ComputeSha256(sourceStream);

            CreatePrivateWorkspaceDirectory(
                workspacePath,
                new SecurityIdentifier(sidValue));
            workspaceCreated = true;
            workspaceHandle = OpenValidatedFileSystemEntry(
                workspacePath,
                expectDirectory: true,
                denyDeleteSharing: true);
            ValidatePrivateWorkspaceSecurity(workspacePath, sidValue);

            sourceStream.Position = 0;
            using (FileStream destination = new(
                       helperPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.WriteThrough))
            {
                sourceStream.CopyTo(destination, 128 * 1024);
                destination.Flush(flushToDisk: true);
            }

            helperStableStream = OpenStableReadStream(helperPath);
            DemandSingleHardLink(helperStableStream.SafeFileHandle, helperPath);
            ValidatePrivateWorkspaceFileSecurity(helperPath, sidValue);
            if (helperStableStream.Length != sourceStream.Length
                || !string.Equals(
                    ComputeSha256(helperStableStream),
                    sourceSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "La copie transitoire du nettoyage WotLK est altérée.");
            }

            return new TransientGameUninstallWorkspace(
                workspacePath,
                helperPath,
                sidValue,
                cleanupEventName,
                sourceSha256,
                sourceStream,
                helperStableStream,
                workspaceHandle,
                gameSourceAncestorHandles,
                workspaceAncestorHandles);
        }
        catch
        {
            helperStableStream?.Dispose();
            sourceStream?.Dispose();
            workspaceHandle?.Dispose();
            DisposeHandles(gameSourceAncestorHandles);
            DisposeHandles(workspaceAncestorHandles);
            if (workspaceCreated)
            {
                TryDeleteTransientWorkspace(workspacePath, helperPath);
            }

            throw;
        }
    }

    internal static TransientGameUninstallValidation ValidateTransientGameUninstallCleanup(
        GameUninstallCleanupRequest request,
        string? currentExecutablePath,
        string currentSid,
        string? localApplicationDataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryNormalizeSecurityIdentifier(currentSid, out string normalizedCurrentSid)
            || !string.Equals(
                normalizedCurrentSid,
                request.RequesterSid,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Le nettoyage WotLK ne correspond pas à l'utilisateur demandeur.");
        }

        string requestIdText = request.CleanupEventName[
            (request.CleanupEventName.LastIndexOf('.') + 1)..];
        if (!Guid.TryParseExact(requestIdText, "N", out Guid requestId))
        {
            throw new InvalidDataException(
                "L'identifiant du nettoyage WotLK est invalide.");
        }

        string localRoot = ResolveLocalApplicationDataRoot(localApplicationDataRoot);
        string expectedWorkspace = Path.Combine(
            localRoot,
            TransientCleanupProductDirectoryName,
            TransientCleanupDirectoryName,
            requestId.ToString("N", CultureInfo.InvariantCulture));
        string expectedHelper = Path.Combine(
            expectedWorkspace,
            TransientCleanupHelperFileName);
        if (string.IsNullOrWhiteSpace(currentExecutablePath)
            || !SamePath(request.WorkspacePath, expectedWorkspace)
            || !SamePath(request.HelperPath, expectedHelper)
            || !SamePath(currentExecutablePath, expectedHelper))
        {
            throw new InvalidDataException(
                "Le helper transitoire WotLK n'est pas à son emplacement attendu.");
        }

        DemandNoReparsePoints(request.WorkspacePath, requireLeaf: true);
        DemandNoReparsePoints(request.HelperPath, requireLeaf: true);
        ValidatePrivateWorkspaceSecurity(
            request.WorkspacePath,
            normalizedCurrentSid);
        ValidatePrivateWorkspaceFileSecurity(
            request.HelperPath,
            normalizedCurrentSid);

        List<SafeFileHandle> workspaceAncestorHandles = [];
        List<SafeFileHandle> gameSourceAncestorHandles = [];
        SafeFileHandle? workspaceHandle = null;
        FileStream? helperStream = null;
        FileStream? sourceStream = null;
        try
        {
            workspaceAncestorHandles = OpenAncestorDirectoryHandles(
                request.WorkspacePath);
            workspaceHandle = OpenValidatedFileSystemEntry(
                request.WorkspacePath,
                expectDirectory: true,
                denyDeleteSharing: true);
            gameSourceAncestorHandles = OpenAncestorDirectoryHandles(
                request.UninstallerPath);
            helperStream = OpenStableReadStream(request.HelperPath);
            sourceStream = OpenStableReadStream(request.UninstallerPath);
            DemandSingleHardLink(helperStream.SafeFileHandle, request.HelperPath);
            DemandSingleHardLink(sourceStream.SafeFileHandle, request.UninstallerPath);

            string helperSha256 = ComputeSha256(helperStream);
            string sourceSha256 = ComputeSha256(sourceStream);
            if (helperStream.Length != sourceStream.Length
                || !string.Equals(
                    helperSha256,
                    request.ExpectedHelperSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    sourceSha256,
                    request.ExpectedHelperSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "L'identité binaire du helper transitoire WotLK est invalide.");
            }

            return new TransientGameUninstallValidation(
                helperStream,
                sourceStream,
                workspaceHandle,
                gameSourceAncestorHandles,
                workspaceAncestorHandles);
        }
        catch
        {
            helperStream?.Dispose();
            sourceStream?.Dispose();
            workspaceHandle?.Dispose();
            DisposeHandles(gameSourceAncestorHandles);
            DisposeHandles(workspaceAncestorHandles);
            throw;
        }
    }

    internal static bool ParentProcessMatchesExpected(
        Process parent,
        long expectedStartTimeUtcTicks,
        Func<string?> readProcessPath,
        string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(readProcessPath);
        try
        {
            return parent.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks
                && ProcessPathMatchesExpected(readProcessPath, expectedPath);
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessStartInfo CreateTransientGameUninstallSelfDeleteStartInfo(
        GameUninstallCleanupRequest request,
        int childProcessId,
        long childStartTimeUtcTicks,
        string? systemDirectory = null,
        string? localApplicationDataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (childProcessId <= 0 || childStartTimeUtcTicks <= 0)
        {
            throw new ArgumentException(
                "L'identité du processus de nettoyage WotLK est invalide.");
        }

        string systemRoot = string.IsNullOrWhiteSpace(systemDirectory)
            ? Environment.SystemDirectory
            : Path.GetFullPath(systemDirectory);
        string powerShellPath = Path.Combine(
            systemRoot,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        string localRoot = ResolveLocalApplicationDataRoot(localApplicationDataRoot);
        string script = BuildTransientSelfDeleteScript(
            request,
            childProcessId,
            childStartTimeUtcTicks,
            localRoot);
        string encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(script));

        ProcessStartInfo startInfo = new()
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        return startInfo;
    }

    private static void ScheduleTransientGameUninstallSelfDelete(
        GameUninstallCleanupRequest request,
        int childProcessId,
        long childStartTimeUtcTicks)
    {
        ProcessStartInfo startInfo =
            CreateTransientGameUninstallSelfDeleteStartInfo(
                request,
                childProcessId,
                childStartTimeUtcTicks);
        using Process _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Impossible de programmer le nettoyage du helper WotLK.");
    }

    private static string BuildTransientSelfDeleteScript(
        GameUninstallCleanupRequest request,
        int childProcessId,
        long childStartTimeUtcTicks,
        string localApplicationDataRoot)
    {
        string workspace = QuotePowerShellLiteral(request.WorkspacePath);
        string helper = QuotePowerShellLiteral(request.HelperPath);
        string sid = QuotePowerShellLiteral(request.RequesterSid);
        string localRoot = QuotePowerShellLiteral(localApplicationDataRoot);
        string expectedWorkspaceParent = QuotePowerShellLiteral(Path.Combine(
            localApplicationDataRoot,
            TransientCleanupProductDirectoryName,
            TransientCleanupDirectoryName));
        string helperName = QuotePowerShellLiteral(
            TransientCleanupHelperFileName);

        return $$"""
        $ErrorActionPreference='Stop'
        $workspace='{{workspace}}'
        $helper='{{helper}}'
        $sid='{{sid}}'
        $localRoot='{{localRoot}}'
        $workspaceParent='{{expectedWorkspaceParent}}'
        $helperName='{{helperName}}'
        $process=Get-Process -Id {{childProcessId.ToString(CultureInfo.InvariantCulture)}} -ErrorAction Stop
        if($process.StartTime.ToUniversalTime().Ticks -ne {{childStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture)}}){exit 31}
        $process.WaitForExit()
        $workspace=[IO.Path]::GetFullPath($workspace)
        $helper=[IO.Path]::GetFullPath($helper)
        if(-not [String]::Equals([IO.Path]::GetDirectoryName($workspace),$workspaceParent,[StringComparison]::OrdinalIgnoreCase)){exit 32}
        if([IO.Path]::GetFileName($workspace) -notmatch '^[0-9a-f]{32}$'){exit 33}
        if(-not [String]::Equals($helper,[IO.Path]::Combine($workspace,$helperName),[StringComparison]::OrdinalIgnoreCase)){exit 34}
        $cursor=[IO.DirectoryInfo]::new($workspace)
        while($null -ne $cursor){
          if(($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){exit 35}
          if([String]::Equals($cursor.FullName,[IO.Path]::GetPathRoot($cursor.FullName),[StringComparison]::OrdinalIgnoreCase)){break}
          $cursor=$cursor.Parent
        }
        if(-not [IO.Directory]::Exists($workspace)){exit 36}
        $acl=Get-Acl -LiteralPath $workspace
        $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if(-not $acl.AreAccessRulesProtected -or $owner -ne $sid){exit 37}
        $allowed=@($sid,'S-1-5-18','S-1-5-32-544')
        $rules=$acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])
        foreach($rule in $rules){
          if($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $rule.IsInherited -or $allowed -notcontains $rule.IdentityReference.Value){exit 38}
        }
        if([IO.File]::Exists($helper)){
          if(([IO.File]::GetAttributes($helper) -band [IO.FileAttributes]::ReparsePoint) -ne 0){exit 39}
          $fileAcl=Get-Acl -LiteralPath $helper
          if($fileAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sid){exit 40}
          foreach($rule in $fileAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
            if($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $allowed -notcontains $rule.IdentityReference.Value){exit 41}
          }
          [IO.File]::SetAttributes($helper,[IO.FileAttributes]::Normal)
          [IO.File]::Delete($helper)
        }
        if([IO.Directory]::Exists($workspace)){
          $enumerator=[IO.Directory]::EnumerateFileSystemEntries($workspace).GetEnumerator()
          try{$empty=-not $enumerator.MoveNext()}finally{$enumerator.Dispose()}
          if($empty){[IO.Directory]::Delete($workspace,$false)}
        }
        """;
    }

    private static string ResolveLocalApplicationDataRoot(string? overridePath)
    {
        string localRoot = string.IsNullOrWhiteSpace(overridePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : overridePath;
        if (string.IsNullOrWhiteSpace(localRoot)
            || !Path.IsPathFullyQualified(localRoot))
        {
            throw new InvalidOperationException(
                "Windows n'a pas fourni le dossier LocalAppData.");
        }

        return Path.GetFullPath(localRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new UnauthorizedAccessException(
                "L'identité Windows courante est introuvable.");
    }

    private static bool TryNormalizeSecurityIdentifier(
        string value,
        out string normalizedSid)
    {
        normalizedSid = string.Empty;
        try
        {
            SecurityIdentifier sid = new(value);
            normalizedSid = sid.Value;
            return string.Equals(value, normalizedSid, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException
            or SystemException)
        {
            return false;
        }
    }

    private static bool TryNormalizeSha256(string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        normalized = value.ToLowerInvariant();
        return string.Equals(value, normalized, StringComparison.Ordinal);
    }

    private static void CreatePrivateWorkspaceDirectory(
        string workspacePath,
        SecurityIdentifier requesterSid)
    {
        DirectorySecurity security = new();
        security.SetOwner(requesterSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControlRule(security, requesterSid);
        AddFullControlRule(security, LocalSystemSid);
        AddFullControlRule(security, BuiltinAdministratorsSid);
        FileSystemAclExtensions.Create(
            new DirectoryInfo(workspacePath),
            security);
    }

    private static void AddFullControlRule(
        DirectorySecurity security,
        SecurityIdentifier sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    internal static void ValidatePrivateWorkspaceSecurity(
        string workspacePath,
        string expectedOwnerSid)
    {
        DirectorySecurity security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(workspacePath),
            AccessControlSections.Access | AccessControlSections.Owner);
        SecurityIdentifier owner = security.GetOwner(
                typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException(
                "Le propriétaire du workspace WotLK est introuvable.");
        if (!security.AreAccessRulesProtected
            || !string.Equals(owner.Value, expectedOwnerSid, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Le workspace WotLK n'est pas privé.");
        }

        ValidateWorkspaceRules(
            security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier)),
            expectedOwnerSid,
            requireExplicit: true);
    }

    private static void ValidatePrivateWorkspaceFileSecurity(
        string helperPath,
        string expectedOwnerSid)
    {
        FileSecurity security = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(helperPath),
            AccessControlSections.Access | AccessControlSections.Owner);
        SecurityIdentifier owner = security.GetOwner(
                typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException(
                "Le propriétaire du helper WotLK est introuvable.");
        if (!string.Equals(owner.Value, expectedOwnerSid, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Le helper WotLK n'appartient pas à l'utilisateur attendu.");
        }

        ValidateWorkspaceRules(
            security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier)),
            expectedOwnerSid,
            requireExplicit: false);
    }

    private static void ValidateWorkspaceRules(
        AuthorizationRuleCollection rules,
        string requesterSid,
        bool requireExplicit)
    {
        HashSet<string> expected = new(StringComparer.Ordinal)
        {
            requesterSid,
            LocalSystemSid.Value,
            BuiltinAdministratorsSid.Value
        };
        HashSet<string> fullControl = new(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid
                || !expected.Contains(sid.Value)
                || rule.AccessControlType != AccessControlType.Allow
                || (requireExplicit && rule.IsInherited))
            {
                throw new UnauthorizedAccessException(
                    "Les droits du workspace WotLK ne sont pas fiables.");
            }

            if ((rule.FileSystemRights & FileSystemRights.FullControl)
                == FileSystemRights.FullControl)
            {
                fullControl.Add(sid.Value);
            }
        }

        if (requireExplicit && !expected.SetEquals(fullControl))
        {
            throw new UnauthorizedAccessException(
                "Les droits privés du workspace WotLK sont incomplets.");
        }
    }

    private static FileStream OpenStableReadStream(string path)
    {
        SafeFileHandle handle = OpenValidatedFileSystemEntry(
            path,
            expectDirectory: false,
            denyDeleteSharing: true,
            desiredAccess: GenericReadAccess,
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

    private static void DemandSingleHardLink(
        SafeFileHandle handle,
        string path)
    {
        ByHandleFileInformation information = ReadFileInformation(handle, path);
        if (information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                "Le nettoyage WotLK refuse un fichier lié à un autre emplacement.");
        }
    }

    private static void ValidateStableFileLength(
        FileStream stream,
        string label)
    {
        if (stream.Length <= 0 || stream.Length > MaximumTransientHelperBytes)
        {
            throw new InvalidDataException(
                $"La taille du {label} est invalide.");
        }
    }

    private static string ComputeSha256(FileStream stream)
    {
        stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
        stream.Position = 0;
        return hash;
    }

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (int index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }

        handles.Clear();
    }

    internal static void TryDeleteTransientWorkspace(
        string workspacePath,
        string helperPath)
    {
        try
        {
            if (!Directory.Exists(workspacePath)
                || !SamePath(
                    Path.GetDirectoryName(helperPath) ?? string.Empty,
                    workspacePath))
            {
                return;
            }

            DemandNoReparsePoints(workspacePath, requireLeaf: true);
            if (File.Exists(helperPath))
            {
                DemandNoReparsePoints(helperPath, requireLeaf: true);
                File.SetAttributes(helperPath, FileAttributes.Normal);
                File.Delete(helperPath);
            }

            if (!Directory.EnumerateFileSystemEntries(
                    workspacePath,
                    "*",
                    SearchOption.TopDirectoryOnly).Any())
            {
                Directory.Delete(workspacePath, recursive: false);
            }
        }
        catch
        {
            // A failed cleanup leaves only a bounded private workspace.
        }
    }

    private static string QuotePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}

internal sealed class TransientGameUninstallWorkspace : IDisposable
{
    private FileStream? _sourceStream;
    private FileStream? _helperStream;
    private SafeFileHandle? _workspaceHandle;
    private List<SafeFileHandle>? _gameSourceAncestorHandles;
    private List<SafeFileHandle>? _workspaceAncestorHandles;
    private bool _handedOff;

    internal TransientGameUninstallWorkspace(
        string workspacePath,
        string helperPath,
        string requesterSid,
        string cleanupEventName,
        string helperSha256,
        FileStream sourceStream,
        FileStream helperStream,
        SafeFileHandle workspaceHandle,
        List<SafeFileHandle> gameSourceAncestorHandles,
        List<SafeFileHandle> workspaceAncestorHandles)
    {
        WorkspacePath = workspacePath;
        HelperPath = helperPath;
        RequesterSid = requesterSid;
        CleanupEventName = cleanupEventName;
        HelperSha256 = helperSha256;
        _sourceStream = sourceStream;
        _helperStream = helperStream;
        _workspaceHandle = workspaceHandle;
        _gameSourceAncestorHandles = gameSourceAncestorHandles;
        _workspaceAncestorHandles = workspaceAncestorHandles;
    }

    internal string WorkspacePath { get; }

    internal string HelperPath { get; }

    internal string RequesterSid { get; }

    internal string CleanupEventName { get; }

    internal string HelperSha256 { get; }

    internal void MarkHandedOff() => _handedOff = true;

    public void Dispose()
    {
        _helperStream?.Dispose();
        _helperStream = null;
        _sourceStream?.Dispose();
        _sourceStream = null;
        _workspaceHandle?.Dispose();
        _workspaceHandle = null;
        DisposeHandleList(ref _gameSourceAncestorHandles);
        DisposeHandleList(ref _workspaceAncestorHandles);
        if (!_handedOff)
        {
            GameInstallServices.TryDeleteTransientWorkspace(
                WorkspacePath,
                HelperPath);
        }
    }

    private static void DisposeHandleList(ref List<SafeFileHandle>? handles)
    {
        if (handles is null)
        {
            return;
        }

        for (int index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }

        handles = null;
    }

}

internal sealed class TransientGameUninstallValidation : IDisposable
{
    private FileStream? _helperStream;
    private FileStream? _sourceStream;
    private SafeFileHandle? _workspaceHandle;
    private List<SafeFileHandle>? _gameSourceAncestorHandles;
    private List<SafeFileHandle>? _workspaceAncestorHandles;

    internal TransientGameUninstallValidation(
        FileStream helperStream,
        FileStream sourceStream,
        SafeFileHandle workspaceHandle,
        List<SafeFileHandle> gameSourceAncestorHandles,
        List<SafeFileHandle> workspaceAncestorHandles)
    {
        _helperStream = helperStream;
        _sourceStream = sourceStream;
        _workspaceHandle = workspaceHandle;
        _gameSourceAncestorHandles = gameSourceAncestorHandles;
        _workspaceAncestorHandles = workspaceAncestorHandles;
    }

    internal void ReleaseGameSourceLocks()
    {
        _sourceStream?.Dispose();
        _sourceStream = null;
        DisposeHandleList(ref _gameSourceAncestorHandles);
    }

    public void Dispose()
    {
        ReleaseGameSourceLocks();
        _helperStream?.Dispose();
        _helperStream = null;
        _workspaceHandle?.Dispose();
        _workspaceHandle = null;
        DisposeHandleList(ref _workspaceAncestorHandles);
    }

    private static void DisposeHandleList(ref List<SafeFileHandle>? handles)
    {
        if (handles is null)
        {
            return;
        }

        for (int index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }

        handles = null;
    }
}
