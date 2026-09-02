using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace WotLK.Launcher.Updater;

internal sealed class LauncherUpdateTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _transactionsRoot;

    internal LauncherUpdateTransactionStore(string? transactionsRoot = null)
    {
        _transactionsRoot = RequireLocalAbsolutePath(
            transactionsRoot ?? LauncherUpdatePaths.TransactionsRoot,
            "racine des transactions");
    }

    internal LauncherUpdateTransaction Load(string transactionPath)
    {
        string canonicalPath = Path.GetFullPath(transactionPath);
        string json = File.ReadAllText(canonicalPath);
        LauncherUpdateTransaction transaction = JsonSerializer.Deserialize<LauncherUpdateTransaction>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException("Transaction de mise à jour illisible.");
        ValidateShape(transaction, canonicalPath);
        return transaction;
    }

    internal void Save(LauncherUpdateTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateShape(transaction, Path.GetFullPath(transaction.TransactionPath));
        string json = JsonSerializer.Serialize(transaction, JsonOptions) + Environment.NewLine;
        WriteAtomic(transaction.TransactionPath, json);
    }

    internal void WriteStartedSignal(
        LauncherUpdateTransaction transaction,
        LauncherUpdateProcessSignal signal)
    {
        ValidateSignal(transaction, signal);
        WriteAtomic(
            transaction.StartedSignalPath,
            JsonSerializer.Serialize(signal, JsonOptions) + Environment.NewLine);
    }

    internal void WriteHelperAcceptedSignal(
        LauncherUpdateTransaction transaction,
        LauncherUpdateProcessSignal signal)
    {
        ValidateSignal(transaction, signal);
        WriteAtomic(
            transaction.HelperAcceptedSignalPath,
            JsonSerializer.Serialize(signal, JsonOptions) + Environment.NewLine);
    }

    internal void WriteReadySignal(
        LauncherUpdateTransaction transaction,
        LauncherUpdateProcessSignal signal)
    {
        ValidateSignal(transaction, signal);
        WriteAtomic(
            transaction.ReadySignalPath,
            JsonSerializer.Serialize(signal, JsonOptions) + Environment.NewLine);
    }

    internal LauncherUpdateProcessSignal? TryReadStartedSignal(
        LauncherUpdateTransaction transaction) =>
        TryReadSignal(transaction.StartedSignalPath, transaction.TransactionId);

    internal LauncherUpdateProcessSignal? TryReadHelperAcceptedSignal(
        LauncherUpdateTransaction transaction) =>
        TryReadSignal(transaction.HelperAcceptedSignalPath, transaction.TransactionId);

    internal LauncherUpdateProcessSignal? TryReadReadySignal(
        LauncherUpdateTransaction transaction) =>
        TryReadSignal(transaction.ReadySignalPath, transaction.TransactionId);

    internal void DeleteSignals(LauncherUpdateTransaction transaction)
    {
        TryDeleteFile(transaction.HelperAcceptedSignalPath);
        TryDeleteFile(transaction.StartedSignalPath);
        TryDeleteFile(transaction.ReadySignalPath);
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
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
        string expectedTransactionPath)
    {
        if (transaction.SchemaVersion != LauncherUpdateTransaction.CurrentSchemaVersion)
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
        RequireExactChild(transaction.HelperPath, workspace, "updater.exe");
        RequireExactChild(
            transaction.HelperAcceptedSignalPath,
            workspace,
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

    private static LauncherUpdateProcessSignal? TryReadSignal(
        string path,
        Guid transactionId)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            LauncherUpdateProcessSignal? signal = JsonSerializer.Deserialize<LauncherUpdateProcessSignal>(
                File.ReadAllText(path),
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
        Directory.CreateDirectory(directory);
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

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }
}
