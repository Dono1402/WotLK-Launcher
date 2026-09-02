using System.Diagnostics;
using System.IO;

namespace WotLK.Launcher.Updater;

internal sealed class LauncherSelfUpdateFinalizer : ILauncherSelfUpdateFinalizer
{
    private readonly string _transactionsRoot;
    private readonly LauncherUpdateTransactionStore _store;
    private readonly ILauncherUpdateHelperLauncher _helperLauncher;

    internal LauncherSelfUpdateFinalizer(
        string transactionsRoot,
        LauncherUpdateTransactionStore store,
        ILauncherUpdateHelperLauncher helperLauncher)
    {
        _transactionsRoot = Path.GetFullPath(
            transactionsRoot ?? throw new ArgumentNullException(nameof(transactionsRoot)));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _helperLauncher = helperLauncher ?? throw new ArgumentNullException(nameof(helperLauncher));
    }

    internal static LauncherSelfUpdateFinalizer CreateProduction()
    {
        LauncherUpdateTransactionStore store = new(LauncherUpdatePaths.TransactionsRoot);
        return new LauncherSelfUpdateFinalizer(
            LauncherUpdatePaths.TransactionsRoot,
            store,
            new WindowsLauncherUpdateHelperLauncher(store));
    }

    public async Task<LauncherUpdateTransaction> PrepareAndLaunchAsync(
        string targetPath,
        string downloadedCandidatePath,
        long expectedSize,
        string expectedSha256,
        int parentProcessId,
        CancellationToken cancellationToken)
    {
        string target = RequireLocalFile(targetPath, "launcher actif");
        string downloadedCandidate = RequireLocalFile(
            downloadedCandidatePath,
            "candidat téléchargé");
        if (parentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        }

        if (expectedSize <= 0
            || string.IsNullOrWhiteSpace(expectedSha256)
            || expectedSha256.Length != 64
            || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "Le manifeste ne permet pas de valider sûrement le nouveau launcher.");
        }

        FileInfo sourceInfo = new(downloadedCandidate);
        if (!sourceInfo.Exists || sourceInfo.Length != expectedSize)
        {
            throw new InvalidDataException("Taille du candidat launcher invalide.");
        }

        string candidateHash = await LauncherUpdateTransactionStore.ComputeSha256Async(
                downloadedCandidate,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(candidateHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Empreinte du candidat launcher invalide.");
        }

        string previousHash = await LauncherUpdateTransactionStore.ComputeSha256Async(
                target,
                cancellationToken)
            .ConfigureAwait(false);
        Guid transactionId = Guid.NewGuid();
        string workspace = Path.Combine(_transactionsRoot, transactionId.ToString("N"));
        string candidate = Path.Combine(workspace, "candidate.exe");
        string helper = Path.Combine(workspace, "updater.exe");
        string transactionPath = Path.Combine(workspace, "transaction.json");
        string suffix = ".atlas-" + transactionId.ToString("N");

        Directory.CreateDirectory(workspace);
        bool transactionSaved = false;
        try
        {
            await CopyDurablyAsync(
                    downloadedCandidate,
                    candidate,
                    cancellationToken)
                .ConfigureAwait(false);
            await CopyDurablyAsync(target, helper, cancellationToken).ConfigureAwait(false);

            await ValidatePreparedCopyAsync(candidate, candidateHash, expectedSize, cancellationToken)
                .ConfigureAwait(false);
            await ValidatePreparedCopyAsync(helper, previousHash, expectedSize: null, cancellationToken)
                .ConfigureAwait(false);

            LauncherUpdateTransaction transaction = new(
                SchemaVersion: LauncherUpdateTransaction.CurrentSchemaVersion,
                TransactionId: transactionId,
                ParentProcessId: parentProcessId,
                TargetPath: target,
                WorkspacePath: workspace,
                CandidatePath: candidate,
                HelperPath: helper,
                StagedPath: target + suffix + ".new",
                BackupPath: target + suffix + ".backup",
                TransactionPath: transactionPath,
                HelperAcceptedSignalPath: Path.Combine(workspace, "helper-accepted.json"),
                StartedSignalPath: Path.Combine(workspace, "started.json"),
                ReadySignalPath: Path.Combine(workspace, "ready.json"),
                ExpectedSize: expectedSize,
                PreviousSha256: previousHash,
                CandidateSha256: candidateHash,
                Phase: LauncherUpdateTransactionPhase.Prepared,
                UpdatedAt: DateTimeOffset.UtcNow);
            _store.Save(transaction);
            transactionSaved = true;
            LauncherUpdateJournal.Append(transaction, "transaction préparée par le launcher actif");
            CleanupOriginalDownload(downloadedCandidate);

            cancellationToken.ThrowIfCancellationRequested();
            await _helperLauncher.LaunchApplyAsync(transaction, cancellationToken)
                .ConfigureAwait(false);
            LauncherUpdateJournal.Append(
                transaction,
                "helper élevé validé pendant que le launcher parent est actif");
            return transaction;
        }
        catch
        {
            if (!transactionSaved)
            {
                LauncherUpdateTransactionStore.TryDeleteDirectory(workspace);
            }

            throw;
        }
    }

    private static async Task CopyDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken)
            .ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task ValidatePreparedCopyAsync(
        string path,
        string expectedSha256,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || expectedSize is > 0 && info.Length != expectedSize.Value)
        {
            throw new InvalidDataException("Copie de préparation incomplète.");
        }

        string hash = await LauncherUpdateTransactionStore.ComputeSha256Async(
                path,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Copie de préparation corrompue.");
        }
    }

    private static string RequireLocalFile(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"Chemin du {label} invalide.");
        }

        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            || new Uri(fullPath).IsUnc)
        {
            throw new InvalidDataException($"Chemin réseau interdit pour le {label}.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Fichier du {label} absent.", fullPath);
        }

        return fullPath;
    }

    private static void CleanupOriginalDownload(string candidatePath)
    {
        LauncherUpdateTransactionStore.TryDeleteFile(candidatePath);
        try
        {
            string? directory = Path.GetDirectoryName(candidatePath);
            string expectedRoot = Path.Combine(Path.GetTempPath(), "WotLKLauncherUpdate");
            if (directory is not null
                && Path.GetFullPath(directory).StartsWith(
                    Path.GetFullPath(expectedRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
        }
    }
}

internal static class LauncherUpdatePaths
{
    internal static string SelfUpdateRoot => Path.Combine(
        LauncherSettings.SettingsDirectory,
        "SelfUpdate");

    internal static string TransactionsRoot => Path.Combine(
        SelfUpdateRoot,
        "Transactions");
}
