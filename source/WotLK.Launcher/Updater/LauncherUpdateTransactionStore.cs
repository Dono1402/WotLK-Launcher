using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace WotLK.Launcher.Updater;

internal sealed class LauncherUpdateTransactionStore
{
    private const int MaximumTransactionJsonBytes = 64 * 1024;
    private const int MaximumSignalJsonBytes = 8 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _transactionsRoot;
    private readonly ILauncherUpdateUserOperationRunner _userOperations;
    private readonly bool _enforceProtectedArtifactAcl;

    internal string TransactionsRoot => _transactionsRoot;

    internal LauncherUpdateTransactionStore(
        string? transactionsRoot = null,
        ILauncherUpdateUserOperationRunner? userOperations = null)
    {
        _transactionsRoot = RequireLocalAbsolutePath(
            transactionsRoot ?? LauncherUpdatePaths.TransactionsRoot,
            "racine des transactions");
        _userOperations = userOperations
            ?? LauncherUpdateDirectUserOperationRunner.Instance;
        _enforceProtectedArtifactAcl =
            _userOperations is LauncherUpdateRequesterImpersonation;
    }

    internal LauncherUpdateTransaction Load(string transactionPath) =>
        _userOperations.Run(() => LoadCore(transactionPath));

    // Only the new, unelevated application's startup handshake may read schema 1.
    // Apply, recovery and Save continue to require the authenticated schema 2.
    internal LauncherUpdateTransaction LoadForStartup(string transactionPath, string currentExecutable) =>
        _userOperations.Run(() => LoadCore(transactionPath, currentExecutable));

    private LauncherUpdateTransaction LoadCore(string transactionPath, string? startupExecutable = null)
    {
        string canonicalPath = Path.GetFullPath(transactionPath);
        byte[] json = ReadStableBoundedJson(canonicalPath);
        DemandUniqueJsonProperties(json);
        LauncherUpdateTransaction transaction = JsonSerializer.Deserialize<LauncherUpdateTransaction>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException("Transaction de mise à jour illisible.");
        bool legacyStartup = startupExecutable is not null && transaction.SchemaVersion == 1;
        ValidateShape(transaction, canonicalPath, legacyStartup);
        LauncherUpdateElevationSecurity.DemandNoReparseTransactionPaths(transaction);
        if (legacyStartup)
        {
            ValidateLegacyStartupTarget(transaction, startupExecutable!);
        }
        return transaction;
    }

    internal void Save(LauncherUpdateTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        _userOperations.Run(() =>
        {
            ValidateShape(transaction, Path.GetFullPath(transaction.TransactionPath));
            LauncherUpdateElevationSecurity.DemandNoReparseTransactionPaths(transaction);
            string json = JsonSerializer.Serialize(transaction, JsonOptions)
                + Environment.NewLine;
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumTransactionJsonBytes)
            {
                throw new InvalidDataException("Transaction de mise à jour trop volumineuse.");
            }

            WriteAtomic(transaction.TransactionPath, json);
        });
    }

    internal void WriteStartedSignal(
        LauncherUpdateTransaction transaction,
        LauncherUpdateProcessSignal signal)
    {
        ValidateSignal(transaction, signal);
        _userOperations.Run(() =>
        {
            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(
                transaction.StartedSignalPath);
            WriteAtomic(
                transaction.StartedSignalPath,
                JsonSerializer.Serialize(signal, JsonOptions) + Environment.NewLine);
        });
    }

    internal void WriteHelperAcceptedSignal(
        LauncherUpdateTransaction transaction,
        LauncherUpdateProcessSignal signal)
    {
        ValidateSignal(transaction, signal);
        LauncherUpdateElevationSecurity.ValidateNoReparsePoints(
            transaction.HelperAcceptedSignalPath);
        string content = JsonSerializer.Serialize(signal, JsonOptions)
            + Environment.NewLine;
        WriteAtomic(
            transaction.HelperAcceptedSignalPath,
            content);
        ValidateProtectedArtifactAfterWrite(
            transaction,
            transaction.HelperAcceptedSignalPath,
            content);
    }

    internal void WriteReadySignal(
        LauncherUpdateTransaction transaction,
        LauncherUpdateProcessSignal signal)
    {
        ValidateSignal(transaction, signal);
        _userOperations.Run(() =>
        {
            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(
                transaction.ReadySignalPath);
            WriteAtomic(
                transaction.ReadySignalPath,
                JsonSerializer.Serialize(signal, JsonOptions) + Environment.NewLine);
        });
    }

    internal void WriteProtectedCommitSignal(
        LauncherUpdateTransaction transaction,
        LauncherUpdateProcessSignal signal)
    {
        ValidateSignal(transaction, signal);
        if (!signal.IsElevated)
        {
            throw new InvalidDataException("Le commit protégé doit provenir du helper élevé.");
        }

        string path = LauncherUpdateElevationSecurity.GetProtectedCommitSignalPath(
            transaction.TargetPath,
            transaction.TransactionId);
        LauncherUpdateElevationSecurity.ValidateNoReparsePoints(path);
        string content = JsonSerializer.Serialize(signal, JsonOptions)
            + Environment.NewLine;
        WriteAtomic(path, content);
        ValidateProtectedArtifactAfterWrite(transaction, path, content);
    }

    internal LauncherUpdateProcessSignal? TryReadStartedSignal(
        LauncherUpdateTransaction transaction) =>
        _userOperations.Run(() =>
            TryReadSignal(transaction.StartedSignalPath, transaction.TransactionId));

    internal LauncherUpdateProcessSignal? TryReadHelperAcceptedSignal(
        LauncherUpdateTransaction transaction) => TryReadProtectedSignal(
        transaction,
        transaction.HelperAcceptedSignalPath);

    internal LauncherUpdateProcessSignal? TryReadReadySignal(
        LauncherUpdateTransaction transaction) =>
        _userOperations.Run(() =>
            TryReadSignal(transaction.ReadySignalPath, transaction.TransactionId));

    internal LauncherUpdateProcessSignal? TryReadProtectedCommitSignal(
        LauncherUpdateTransaction transaction) => TryReadProtectedSignal(
        transaction,
        LauncherUpdateElevationSecurity.GetProtectedCommitSignalPath(
            transaction.TargetPath,
            transaction.TransactionId));

    internal void DeleteSignals(LauncherUpdateTransaction transaction)
    {
        TryDeleteFile(transaction.HelperAcceptedSignalPath);
        _userOperations.Run(() =>
        {
            TryDeleteFile(transaction.StartedSignalPath);
            TryDeleteFile(transaction.ReadySignalPath);
        });
    }

    internal void AppendJournal(LauncherUpdateTransaction transaction, string message) =>
        _userOperations.Run(() => LauncherUpdateJournal.Append(transaction, message));

    internal void TryDeleteUserFile(string path) =>
        _userOperations.Run(() => TryDeleteFile(path));

    internal void TryDeleteUserDirectory(string path) =>
        _userOperations.Run(() => TryDeleteDirectory(path));

    internal bool UserFileExists(string path) =>
        _userOperations.Run(() => File.Exists(path));

    internal FileStream OpenUserFileForStableRead(string path) =>
        _userOperations.Run(() => OpenStableRead(path));

    internal void DemandProtectedFileForElevation(
        LauncherUpdateTransaction transaction,
        string path) =>
        _userOperations.Run(() =>
        {
            if (SamePath(path, transaction.TargetPath))
            {
                LauncherUpdateElevationSecurity.DemandProtectedTargetForElevation(
                    transaction);
            }
            else
            {
                LauncherUpdateElevationSecurity.DemandProtectedSwapFileForElevation(
                    transaction,
                    path);
            }
        });

    internal async Task<string> ComputeUserFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = OpenUserFileForStableRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static bool AreEquivalent(
        LauncherUpdateTransaction left,
        LauncherUpdateTransaction right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        LauncherUpdateManifest? leftManifest = left.AuthenticatedManifest;
        LauncherUpdateManifest? rightManifest = right.AuthenticatedManifest;
        return left with { AuthenticatedManifest = null }
               == right with { AuthenticatedManifest = null }
               && ManifestsEquivalent(leftManifest, rightManifest);
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void ValidateShape(
        LauncherUpdateTransaction transaction,
        string expectedTransactionPath,
        bool legacyStartup = false)
    {
        if (transaction.SchemaVersion != (legacyStartup ? 1 : LauncherUpdateTransaction.CurrentSchemaVersion))
        {
            throw new InvalidDataException("Version de transaction de mise à jour non prise en charge.");
        }

        if (transaction.TransactionId == Guid.Empty)
        {
            throw new InvalidDataException("Identifiant de transaction absent.");
        }

        string workspace = Path.GetFullPath(transaction.WorkspacePath);
        string target = RequireLocalAbsolutePath(transaction.TargetPath, "cible");
        string expectedWorkspaceName = transaction.TransactionId.ToString("N");
        if (!SamePath(workspace, Path.Combine(_transactionsRoot, expectedWorkspaceName)))
        {
            throw new InvalidDataException("Dossier de transaction incohérent.");
        }

        RequireExactChild(transaction.CandidatePath, workspace, "candidate.exe");
        string expectedHelperPath = legacyStartup
            ? Path.Combine(workspace, "updater.exe")
            : LauncherUpdateElevationSecurity.GetProtectedHelperPath(target, transaction.TransactionId);
        if (!SamePath(transaction.HelperPath, expectedHelperPath))
        {
            throw new InvalidDataException("Chemin du helper protégé incohérent.");
        }

        string helperDirectory = Path.GetDirectoryName(expectedHelperPath)
            ?? throw new InvalidDataException("Dossier du helper protégé absent.");
        RequireExactChild(
            transaction.HelperAcceptedSignalPath,
            helperDirectory,
            "helper-accepted.json");
        RequireExactChild(transaction.StartedSignalPath, workspace, "started.json");
        RequireExactChild(transaction.ReadySignalPath, workspace, "ready.json");
        RequireExactChild(transaction.TransactionPath, workspace, "transaction.json");

        if (!SamePath(transaction.TransactionPath, expectedTransactionPath))
        {
            throw new InvalidDataException("Chemin du marqueur de transaction incohérent.");
        }

        string suffix = ".atlas-" + expectedWorkspaceName;
        if (!SamePath(transaction.StagedPath, target + suffix + ".new")
            || !SamePath(transaction.BackupPath, target + suffix + ".backup"))
        {
            throw new InvalidDataException("Chemins de swap non contrôlés.");
        }

        if (!string.Equals(
                Path.GetPathRoot(Path.GetFullPath(transaction.StagedPath)),
                Path.GetPathRoot(target),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Le candidat final doit être sur le volume de la cible.");
        }

        if (transaction.ExpectedSize <= 0
            || !IsSha256(transaction.PreviousSha256)
            || !IsSha256(transaction.CandidateSha256))
        {
            throw new InvalidDataException("Métadonnées de validation incomplètes.");
        }

        if (legacyStartup)
        {
            return;
        }

        bool hasNewProcess = transaction.NewProcessId is > 0;
        if (transaction.NewProcessId is <= 0
            || hasNewProcess != transaction.NewProcessStartedAt.HasValue)
        {
            throw new InvalidDataException(
                "Identité du nouveau processus de mise à jour incohérente.");
        }

        LauncherUpdateManifest? manifest = transaction.AuthenticatedManifest;
        if (!LauncherUpdateVersionPolicy.IsValid(transaction.AuthenticatedTargetVersion)
            || manifest is null
            || !string.Equals(
                transaction.AuthenticatedTargetVersion,
                manifest.Version,
                StringComparison.Ordinal)
            || manifest.Size != transaction.ExpectedSize
            || !string.Equals(
                manifest.Sha256,
                transaction.CandidateSha256,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.Signature))
        {
            throw new InvalidDataException("Preuve signée de mise à jour incohérente.");
        }
    }

    private static void ValidateLegacyStartupTarget(
        LauncherUpdateTransaction transaction,
        string currentExecutable)
    {
        if (LauncherUpdateSecurity.IsCurrentProcessElevated()
            || !SamePath(transaction.TargetPath, currentExecutable)
            || transaction.Phase is not (LauncherUpdateTransactionPhase.SwappedAwaitingStart
                or LauncherUpdateTransactionPhase.StartedAwaitingReady)
            || transaction.ParentProcessId <= 0
            || transaction.NewProcessId is int processId && processId != Environment.ProcessId
            || transaction.AuthenticatedManifest is not null
            || transaction.NewProcessStartedAt is not null
            || !Version.TryParse(transaction.AuthenticatedTargetVersion, out Version? targetVersion))
        {
            throw new InvalidDataException("Transaction historique incompatible avec ce démarrage.");
        }

        string? versionText = System.Diagnostics.FileVersionInfo.GetVersionInfo(currentExecutable).FileVersion;
        if (!Version.TryParse(versionText, out Version? currentVersion)
            || currentVersion.Major != targetVersion.Major
            || currentVersion.Minor != targetVersion.Minor
            || Math.Max(currentVersion.Build, 0) != Math.Max(targetVersion.Build, 0)
            || Math.Max(currentVersion.Revision, 0) != Math.Max(targetVersion.Revision, 0))
        {
            throw new InvalidDataException("La version démarrée diffère de la mise à jour historique.");
        }

        using FileStream executable = OpenStableRead(currentExecutable);
        if (executable.Length != transaction.ExpectedSize
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(executable), Convert.FromHexString(transaction.CandidateSha256)))
        {
            throw new InvalidDataException("Le launcher démarré diffère du paquet historique.");
        }
    }

    private static string RequireLocalAbsolutePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"Chemin {label} non absolu.");
        }

        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            || new Uri(fullPath).IsUnc)
        {
            throw new InvalidDataException($"Chemin réseau interdit pour {label}.");
        }

        return fullPath;
    }

    private static void RequireExactChild(
        string path,
        string parent,
        string expectedFileName)
    {
        string fullPath = RequireLocalAbsolutePath(path, expectedFileName);
        string expectedPath = Path.Combine(parent, expectedFileName);
        if (!SamePath(fullPath, expectedPath))
        {
            throw new InvalidDataException($"Chemin {expectedFileName} non contrôlé.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool ManifestsEquivalent(
        LauncherUpdateManifest? left,
        LauncherUpdateManifest? right) =>
        ReferenceEquals(left, right)
        || left is not null
           && right is not null
           && left.SchemaVersion == right.SchemaVersion
           && string.Equals(left.KeyId, right.KeyId, StringComparison.Ordinal)
           && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
           && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
           && left.Size == right.Size
           && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
           && string.Equals(left.PublishedAt, right.PublishedAt, StringComparison.Ordinal)
           && string.Equals(left.Signature, right.Signature, StringComparison.Ordinal);

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateSignal(
        LauncherUpdateTransaction transaction,
        LauncherUpdateProcessSignal signal)
    {
        if (signal.TransactionId != transaction.TransactionId || signal.ProcessId <= 0)
        {
            throw new InvalidDataException("Signal de mise à jour incohérent.");
        }
    }

    private LauncherUpdateProcessSignal? TryReadProtectedSignal(
        LauncherUpdateTransaction transaction,
        string path)
    {
        if (_enforceProtectedArtifactAcl)
        {
            try
            {
                _userOperations.Run(() =>
                    LauncherUpdateElevationSecurity
                        .DemandProtectedHelperArtifactForElevation(
                            transaction,
                            path));
            }
            catch
            {
                return null;
            }
        }

        return TryReadSignal(path, transaction.TransactionId);
    }

    private void ValidateProtectedArtifactAfterWrite(
        LauncherUpdateTransaction transaction,
        string path,
        string expectedContent)
    {
        if (!_enforceProtectedArtifactAcl)
        {
            return;
        }

        _userOperations.Run(() =>
            LauncherUpdateElevationSecurity.DemandProtectedHelperArtifactForElevation(
                transaction,
                path));
        byte[] expectedHash = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(expectedContent));
        byte[] actualHash;
        using (FileStream stream = OpenStableRead(path))
        {
            actualHash = SHA256.HashData(stream);
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException(
                "Un artefact protégé du helper a changé après son écriture.");
        }
    }

    private static LauncherUpdateProcessSignal? TryReadSignal(
        string path,
        Guid transactionId)
    {
        try
        {
            using FileStream stream = OpenStableRead(path);
            if (stream.Length <= 0 || stream.Length > MaximumSignalJsonBytes)
            {
                return null;
            }

            byte[] json = new byte[checked((int)stream.Length)];
            stream.ReadExactly(json);
            DemandUniqueJsonProperties(json);
            LauncherUpdateProcessSignal? signal = JsonSerializer.Deserialize<LauncherUpdateProcessSignal>(
                json,
                JsonOptions);
            return signal is { ProcessId: > 0 }
                   && signal.TransactionId == transactionId
                ? signal
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Dossier de transaction absent.");
        LauncherUpdateElevationSecurity.ValidateNoReparsePoints(fullPath);
        Directory.CreateDirectory(directory);
        LauncherUpdateElevationSecurity.ValidateNoReparsePoints(fullPath);
        string temporaryPath = Path.Combine(
            directory,
            "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(fullPath);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static FileStream OpenStableRead(string path)
    {
        string fullPath = Path.GetFullPath(path);
        LauncherUpdateElevationSecurity.ValidateNoReparsePoints(fullPath);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            LauncherUpdateElevationSecurity.ValidateNoReparsePoints(fullPath);
            FileStream result = stream;
            stream = null;
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static byte[] ReadStableBoundedJson(string path)
    {
        using FileStream stream = OpenStableRead(path);
        if (stream.Length <= 0 || stream.Length > MaximumTransactionJsonBytes)
        {
            throw new InvalidDataException("Transaction de mise à jour trop volumineuse.");
        }

        byte[] json = new byte[checked((int)stream.Length)];
        stream.ReadExactly(json);
        return json;
    }

    private static void DemandUniqueJsonProperties(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        DemandUniqueJsonProperties(document.RootElement);
    }

    private static void DemandUniqueJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "Propriété dupliquée dans la transaction de mise à jour.");
                }

                DemandUniqueJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                DemandUniqueJsonProperties(child);
            }
        }
    }
}
