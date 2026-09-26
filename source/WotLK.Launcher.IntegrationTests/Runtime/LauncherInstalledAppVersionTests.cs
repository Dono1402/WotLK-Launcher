using Microsoft.Win32;
using System.Security.Cryptography;
using WotLK.Launcher;
using WotLK.Launcher.Installer.Setup;
using WotLK.Launcher.Updater;

internal static class LauncherInstalledAppVersionTests
{
    internal static Task<int> RunAsync()
    {
        ValidateStableInstallerIdentity();
        if (OperatingSystem.IsWindows())
        {
            RunIsolatedWindowsRegistryScenario(machineWide: false);
        }

        Console.WriteLine(
            "Launcher Installed Apps version/publisher synchronization OK; legacy migration and local isolation verified.");
        return Task.FromResult(0);
    }

    internal static int RunWindowsSmoke()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows is required for the 04D.3 registry smoke.");
            return 2;
        }

        RunIsolatedWindowsRegistryScenario(machineWide: false);
        Console.WriteLine(
            "Atlas 04D.3 isolated per-user Installed Apps smoke OK: DisplayVersion 1.1.2 -> 1.2.0.");
        return 0;
    }

    internal static int RunElevatedWindowsSmoke()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows is required for the 04D.3 registry smoke.");
            return 2;
        }
        if (!LauncherUpdateSecurity.IsCurrentProcessElevated())
        {
            Console.Error.WriteLine("The 04D.3 machine-wide smoke requires elevation.");
            return 3;
        }

        RunIsolatedWindowsRegistryScenario(machineWide: true);
        Console.WriteLine(
            "Atlas 04D.3 isolated machine-wide Installed Apps smoke OK: DisplayVersion 1.1.2 -> 1.2.0.");
        return 0;
    }

    private static void ValidateStableInstallerIdentity()
    {
        Equal("Atlas", InstallerProduct.Publisher, "Le nouvel éditeur doit être Atlas.");
        Equal(
            InstallerProduct.RegistrySubKey,
            WindowsLauncherInstalledAppVersionRegistry.StableRegistrySubKey,
            "Le helper doit cibler uniquement la clé stable créée par l'installateur.");
        Equal(
            InstallerProduct.Name,
            WindowsLauncherInstalledAppVersionRegistry.StableDisplayName,
            "L'identité produit du helper et de l'installateur doit rester identique.");
        Equal(
            InstallerProduct.Publisher,
            WindowsLauncherInstalledAppVersionRegistry.StablePublisher,
            "L'éditeur attendu doit rester celui de l'installation officielle.");

        int refreshCalls = 0;
        LauncherInstalledAppBrandingMigration.RefreshWhenEligible(true, false, () => refreshCalls++);
        LauncherInstalledAppBrandingMigration.RefreshWhenEligible(false, true, () => refreshCalls++);
        Equal(0, refreshCalls, "Le local et une mise à jour en cours ne doivent pas consulter/modifier l'installation publique.");
        LauncherInstalledAppBrandingMigration.RefreshWhenEligible(false, false, () => refreshCalls++);
        Equal(1, refreshCalls, "Seul un démarrage public sans transaction en cours peut réparer l'éditeur.");
        LauncherInstalledAppBrandingMigration.RefreshWhenEligible(false, false,
            () => throw new System.ComponentModel.Win32Exception(1223));
        True(!LauncherInstalledAppBrandingMigration.CanRunElevated(true, true),
            "Même élevé, le build local doit refuser la commande de réparation.");
        True(!LauncherInstalledAppBrandingMigration.CanRunElevated(false, false),
            "La réparation publique exige un processus élevé.");
        True(LauncherInstalledAppBrandingMigration.CanRunElevated(false, true),
            "La commande publique élevée peut vérifier son installation.");
    }

    private static void RunIsolatedWindowsRegistryScenario(bool machineWide)
    {
        Guid testId = Guid.NewGuid();
        WindowsLauncherInstalledAppVersionRegistry registry =
            WindowsLauncherInstalledAppVersionRegistry.CreateIsolatedTest(
                testId,
                machineWide);
        True(
            !string.Equals(
                registry.RegistrySubKey,
                WindowsLauncherInstalledAppVersionRegistry.StableRegistrySubKey,
                StringComparison.OrdinalIgnoreCase),
            "Le smoke ne doit jamais cibler l'entrée officielle AtlasLauncher.");

        string root = Path.Combine(
            Path.GetTempPath(),
            "AtlasLauncher04D3Tests",
            testId.ToString("N"));
        string installLocation = Path.Combine(root, "Atlas Launcher");
        string launcherPath = Path.Combine(
            installLocation,
            InstallerProduct.LauncherFileName);
        string uninstallerPath = Path.Combine(
            installLocation,
            InstallerProduct.UninstallerFileName);
        Directory.CreateDirectory(installLocation);
        File.WriteAllText(launcherPath, "isolated launcher");
        File.WriteAllText(uninstallerPath, "isolated uninstaller");

        using RegistryKey registryRoot = RegistryKey.OpenBaseKey(
            registry.RegistryHive,
            RegistryView.Registry64);
        try
        {
            registryRoot.DeleteSubKeyTree(
                registry.RegistrySubKey,
                throwOnMissingSubKey: false);
            LauncherInstalledAppVersionSynchronizer synchronizer = new(registry);
            LauncherUpdateTransaction transaction = CreateTransaction(
                launcherPath,
                "1.2.0");

            LauncherInstalledAppVersionSyncResult missing =
                synchronizer.Synchronize(transaction);
            Equal(
                LauncherInstalledAppVersionSyncStatus.EntryMissing,
                missing.Status,
                "Une installation portable ne doit créer aucune entrée Windows.");
            using RegistryKey? absentEntry = registryRoot.OpenSubKey(
                registry.RegistrySubKey);
            True(
                absentEntry is null,
                "Le helper ne doit pas créer une entrée absente.");

            CreateRegistration(
                registryRoot,
                registry,
                installLocation,
                launcherPath,
                uninstallerPath);
            SetPublisher(registryRoot, registry.RegistrySubKey, registry.AcceptedLegacyPublisher);
            LauncherInstalledAppVersionSyncResult updated =
                synchronizer.Synchronize(transaction);
            Equal(
                LauncherInstalledAppVersionSyncStatus.Updated,
                updated.Status,
                "L'installation officielle isolée doit accepter la version authentifiée.");
            using (RegistryKey key = registryRoot.OpenSubKey(registry.RegistrySubKey)!)
            {
                Equal(
                    "1.2.0",
                    key.GetValue("DisplayVersion") as string,
                    "Applications installées doit exposer la nouvelle version.");
                Equal(
                    registry.ExpectedPublisher,
                    key.GetValue("Publisher") as string,
                    "La mise à jour doit migrer l'ancien éditeur vers Atlas.");
                Equal(
                    installLocation,
                    key.GetValue("InstallLocation") as string,
                    "InstallLocation ne doit pas être modifié.");
                Equal(
                    launcherPath,
                    key.GetValue("DisplayIcon") as string,
                    "DisplayIcon ne doit pas être modifié.");
                Equal(
                    $"\"{uninstallerPath}\" --uninstall",
                    key.GetValue("UninstallString") as string,
                    "UninstallString ne doit pas être modifié.");
            }

            Equal(LauncherInstalledAppVersionSyncStatus.AlreadyCurrent,
                synchronizer.Synchronize(transaction).Status,
                "La nouvelle identité doit être reconnue lors des mises à jour suivantes.");
            SetPublisher(registryRoot, registry.RegistrySubKey, registry.AcceptedLegacyPublisher);
            Equal(LauncherInstalledAppVersionSyncStatus.Updated,
                synchronizer.Synchronize(transaction).Status,
                "L'éditeur doit être migré même si DisplayVersion est déjà à jour.");

            SetPublisher(registryRoot, registry.RegistrySubKey, registry.AcceptedLegacyPublisher);
            Equal(LauncherInstalledAppVersionSyncStatus.NeedsUpdate,
                registry.SynchronizePublisher(installLocation, launcherPath, writable: false).Status,
                "Le démarrage public doit détecter l'ancien éditeur sans écrire avant UAC.");
            Equal(registry.AcceptedLegacyPublisher,
                ReadPublisher(registryRoot, registry.RegistrySubKey), "La détection est en lecture seule.");
            Equal(LauncherInstalledAppVersionSyncStatus.Updated,
                registry.SynchronizePublisher(installLocation, launcherPath, writable: true).Status,
                "La réparation après une mise à jour par l'ancien helper doit migrer l'éditeur.");
            Equal(registry.ExpectedPublisher, ReadPublisher(registryRoot, registry.RegistrySubKey),
                "La réparation doit inscrire le nouvel éditeur.");
            Equal("1.2.0", ReadDisplayVersion(registryRoot, registry.RegistrySubKey),
                "La réparation d'éditeur ne doit pas modifier la version.");
            Equal(LauncherInstalledAppVersionSyncStatus.AlreadyCurrent,
                registry.SynchronizePublisher(installLocation, launcherPath, writable: false).Status,
                "Une migration terminée ne doit plus déclencher de demande UAC.");

            SetPublisher(registryRoot, registry.RegistrySubKey, "Unrelated publisher");
            Equal(LauncherInstalledAppVersionSyncStatus.EntryNotOfficial,
                synchronizer.Synchronize(transaction).Status,
                "Un autre éditeur ne doit pas être assimilé à l'ancienne identité Atlas.");
            Equal("Unrelated publisher", ReadPublisher(registryRoot, registry.RegistrySubKey),
                "L'éditeur inconnu doit rester intact.");
            SetPublisher(registryRoot, registry.RegistrySubKey, registry.AcceptedLegacyPublisher);

            SetDisplayVersion(registryRoot, registry.RegistrySubKey, "1.1.2");
            LauncherInstalledAppVersionSyncResult mismatch =
                synchronizer.Synchronize(CreateTransaction(
                    Path.Combine(root, "Other install", InstallerProduct.LauncherFileName),
                    "1.2.0"));
            Equal(
                LauncherInstalledAppVersionSyncStatus.InstallLocationMismatch,
                mismatch.Status,
                "Une entrée appartenant à une autre cible doit être ignorée.");
            Equal(
                "1.1.2",
                ReadDisplayVersion(registryRoot, registry.RegistrySubKey),
                "Un mauvais InstallLocation doit conserver l'ancienne version.");
            Equal(registry.AcceptedLegacyPublisher, ReadPublisher(registryRoot, registry.RegistrySubKey),
                "Le local ou une autre installation ne doit pas migrer l'éditeur public.");

            LauncherInstalledAppVersionSyncResult invalid =
                synchronizer.Synchronize(transaction with
                {
                    AuthenticatedTargetVersion = "1.2.0-preview"
                });
            Equal(
                LauncherInstalledAppVersionSyncStatus.InvalidAuthenticatedVersion,
                invalid.Status,
                "Une version hors du format du manifeste signé doit être refusée.");
            Equal(
                "1.1.2",
                ReadDisplayVersion(registryRoot, registry.RegistrySubKey),
                "Une version invalide ne doit provoquer aucune écriture.");

            using (RegistryKey key = registryRoot.OpenSubKey(
                       registry.RegistrySubKey,
                       writable: true)!)
            {
                key.SetValue(
                    "DisplayName",
                    registry.ExpectedDisplayName + " similaire",
                    RegistryValueKind.String);
            }
            LauncherInstalledAppVersionSyncResult similar =
                synchronizer.Synchronize(transaction);
            Equal(
                LauncherInstalledAppVersionSyncStatus.EntryNotOfficial,
                similar.Status,
                "Une entrée portant seulement un nom similaire doit être ignorée.");
            Equal(
                "1.1.2",
                ReadDisplayVersion(registryRoot, registry.RegistrySubKey),
                "Une identité produit incorrecte ne doit provoquer aucune écriture.");

            using (RegistryKey key = registryRoot.OpenSubKey(
                       registry.RegistrySubKey,
                       writable: true)!)
            {
                key.SetValue(
                    "DisplayName",
                    registry.ExpectedDisplayName,
                    RegistryValueKind.String);
            }
            SetDisplayVersion(registryRoot, registry.RegistrySubKey, "1.1.2");
            File.WriteAllText(launcherPath, "isolated launcher 1.1.2");
            LauncherUpdateExecutionResult atomicResult = RunAtomicUpdate(
                root,
                launcherPath,
                synchronizer);
            Equal(
                LauncherUpdateExecutionOutcome.Succeeded,
                atomicResult.Outcome,
                "Le smoke doit confirmer le remplacement complet après Ready.");
            Equal(
                "1.2.0",
                ReadDisplayVersion(registryRoot, registry.RegistrySubKey),
                "Le vrai pipeline atomique doit publier 1.2.0 dans Applications installées.");
        }
        finally
        {
            registryRoot.DeleteSubKeyTree(
                registry.RegistrySubKey,
                throwOnMissingSubKey: false);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CreateRegistration(
        RegistryKey registryRoot,
        WindowsLauncherInstalledAppVersionRegistry registry,
        string installLocation,
        string launcherPath,
        string uninstallerPath)
    {
        using RegistryKey key = registryRoot.CreateSubKey(
            registry.RegistrySubKey,
            writable: true);
        key.SetValue(
            "DisplayName",
            registry.ExpectedDisplayName,
            RegistryValueKind.String);
        key.SetValue("DisplayVersion", "1.1.2", RegistryValueKind.String);
        key.SetValue(
            "Publisher",
            registry.ExpectedPublisher,
            RegistryValueKind.String);
        key.SetValue("InstallLocation", installLocation, RegistryValueKind.String);
        key.SetValue("DisplayIcon", launcherPath, RegistryValueKind.String);
        key.SetValue(
            "UninstallString",
            $"\"{uninstallerPath}\" --uninstall",
            RegistryValueKind.String);
    }

    private static void SetDisplayVersion(
        RegistryKey registryRoot,
        string registrySubKey,
        string version)
    {
        using RegistryKey key = registryRoot.OpenSubKey(
            registrySubKey,
            writable: true)!;
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
    }

    private static void SetPublisher(RegistryKey root, string subKey, string publisher)
    {
        using RegistryKey key = root.OpenSubKey(subKey, writable: true)!;
        key.SetValue("Publisher", publisher, RegistryValueKind.String);
    }

    private static string? ReadPublisher(RegistryKey root, string subKey)
    {
        using RegistryKey key = root.OpenSubKey(subKey)!;
        return key.GetValue("Publisher") as string;
    }

    private static string? ReadDisplayVersion(
        RegistryKey registryRoot,
        string registrySubKey)
    {
        using RegistryKey key = registryRoot.OpenSubKey(registrySubKey)!;
        return key.GetValue("DisplayVersion") as string;
    }

    private static LauncherUpdateTransaction CreateTransaction(
        string targetPath,
        string authenticatedTargetVersion)
    {
        Guid id = Guid.NewGuid();
        string workspace = Path.Combine(
            Path.GetTempPath(),
            "AtlasLauncher04D3Transaction",
            id.ToString("N"));
        return new LauncherUpdateTransaction(
            LauncherUpdateTransaction.CurrentSchemaVersion,
            id,
            Environment.ProcessId,
            targetPath,
            workspace,
            Path.Combine(workspace, "candidate.exe"),
            LauncherUpdateElevationSecurity.GetProtectedHelperPath(targetPath, id),
            targetPath + ".new",
            targetPath + ".backup",
            Path.Combine(workspace, "transaction.json"),
            LauncherUpdateElevationSecurity.GetProtectedHelperAcceptedSignalPath(
                targetPath,
                id),
            Path.Combine(workspace, "started.json"),
            Path.Combine(workspace, "ready.json"),
            1,
            new string('a', 64),
            new string('b', 64),
            LauncherUpdateTransactionPhase.Committed,
            DateTimeOffset.UtcNow,
            AuthenticatedTargetVersion: authenticatedTargetVersion,
            AuthenticatedManifest: TestManifest(
                authenticatedTargetVersion,
                1,
                new string('b', 64)));
    }

    private static LauncherUpdateExecutionResult RunAtomicUpdate(
        string root,
        string targetPath,
        ILauncherInstalledAppVersionSynchronizer synchronizer)
    {
        Guid id = Guid.NewGuid();
        string transactionsRoot = Path.Combine(root, "SelfUpdate", "Transactions");
        string workspace = Path.Combine(transactionsRoot, id.ToString("N"));
        Directory.CreateDirectory(workspace);
        byte[] previous = File.ReadAllBytes(targetPath);
        byte[] candidate = System.Text.Encoding.UTF8.GetBytes(
            "isolated launcher 1.2.0");
        string candidatePath = Path.Combine(workspace, "candidate.exe");
        string helperPath = LauncherUpdateElevationSecurity.GetProtectedHelperPath(
            targetPath,
            id);
        File.WriteAllBytes(candidatePath, candidate);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllBytes(helperPath, previous);
        string suffix = ".atlas-" + id.ToString("N");
        LauncherUpdateTransaction transaction = new(
            LauncherUpdateTransaction.CurrentSchemaVersion,
            id,
            Environment.ProcessId,
            targetPath,
            workspace,
            candidatePath,
            helperPath,
            targetPath + suffix + ".new",
            targetPath + suffix + ".backup",
            Path.Combine(workspace, "transaction.json"),
            LauncherUpdateElevationSecurity.GetProtectedHelperAcceptedSignalPath(
                targetPath,
                id),
            Path.Combine(workspace, "started.json"),
            Path.Combine(workspace, "ready.json"),
            candidate.LongLength,
            Hash(previous),
            Hash(candidate),
            LauncherUpdateTransactionPhase.Prepared,
            DateTimeOffset.UtcNow,
            AuthenticatedTargetVersion: "1.2.0",
            AuthenticatedManifest: TestManifest(
                "1.2.0",
                candidate.LongLength,
                Hash(candidate)));
        LauncherUpdateTransactionStore store = new(transactionsRoot);
        store.Save(transaction);
        ReadyApplicationLauncher applicationLauncher = new(store);
        LauncherAtomicReplacementService service = new(
            store,
            new WindowsLauncherAtomicFileMover(),
            new ExitedParentWaiter(),
            applicationLauncher,
            new LauncherUpdateRetryPolicy(
                FileAttempts: 3,
                FileRetryDelay: TimeSpan.FromMilliseconds(5),
                ParentExitTimeout: TimeSpan.FromSeconds(1),
                ProcessStartTimeout: TimeSpan.FromSeconds(1),
                ReadyTimeout: TimeSpan.FromSeconds(1),
                SignalPollInterval: TimeSpan.FromMilliseconds(5)),
            delayAsync: static (_, _) => Task.CompletedTask,
            installedAppVersionSynchronizer: synchronizer);
        return service.ApplyAsync(transaction).GetAwaiter().GetResult();
    }

    private static string Hash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static LauncherUpdateManifest TestManifest(
        string version,
        long size,
        string sha256) => new()
    {
        SchemaVersion = 1,
        KeyId = "integration-test",
        Version = version,
        Url = $"https://update.animeclub.fr/launcher/{version}/WotLK-Launcher.exe",
        Size = size,
        Sha256 = sha256,
        PublishedAt = "2026-09-09T00:00:00Z",
        Signature = "integration-test-signature"
    };

    private sealed class ExitedParentWaiter : ILauncherUpdateParentWaiter
    {
        public Task<bool> WaitForExitAsync(
            int processId,
            string expectedExecutablePath,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class ReadyApplicationLauncher(LauncherUpdateTransactionStore store)
        : ILauncherUpdateApplicationLauncher
    {
        public Task<ILauncherUpdateLaunchedProcess> LaunchUpdatedAsync(
            LauncherUpdateTransaction transaction,
            TimeSpan startTimeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            ReadyProcess process = new(42_043);
            store.WriteReadySignal(
                transaction,
                new LauncherUpdateProcessSignal(
                    transaction.TransactionId,
                    process.ProcessId,
                    IsElevated: false,
                    DateTimeOffset.UtcNow));
            return Task.FromResult<ILauncherUpdateLaunchedProcess>(process);
        }

        public Task LaunchRollbackAsync(
            LauncherUpdateTransaction transaction,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ReadyProcess(int processId) : ILauncherUpdateLaunchedProcess
    {
        public int ProcessId { get; } = processId;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public bool HasExited { get; private set; }

        public void Kill() => HasExited = true;

        public void Dispose()
        {
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Attendu={expected}; actuel={actual}.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
