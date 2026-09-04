using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using WotLK.Launcher.Installer.Setup;

internal static class InstallerRuntimeTests
{
    internal static async Task<int> RunAsync()
    {
        ValidateEmbeddedPayload();
        ValidateSelfDeleteCompatibility();
        await ValidateSelfDeleteHelperAsync();
        ValidatePaths();
        await ValidateTransactionalInstallAndUninstallAsync();
        await ValidateRollbackAsync();
        await ValidateSingleFlightAsync();
        await ValidateExactProcessMatchingAsync();
        await ValidateUnelevatedLaunchAsync();
        Console.WriteLine("Atlas installer runtime OK (isolated, non-elevated suite)." );
        return 0;
    }

    internal static async Task<int> RunElevatedAsync(string setupArtifact, string resultPath)
    {
        string result = "FAILED";
        try
        {
            True(
                WindowsInstallerSystemActions.IsCurrentProcessElevated(),
                "Le harnais Program Files doit être élevé.");
            setupArtifact = Path.GetFullPath(setupArtifact);
            True(File.Exists(setupArtifact), "AtlasLauncherSetup.exe est absent.");
            ProductSafetySnapshot before = ProductSafetySnapshot.Capture();
            await RunElevatedInstallCyclesAsync(setupArtifact);
            ProductSafetySnapshot after = ProductSafetySnapshot.Capture();
            Equal(before, after, "L'installation principale ou ses données ont été modifiées.");
            await ValidateUnelevatedLaunchAsync();
            result = "PASS";
            Console.WriteLine("Atlas installer Program Files/UAC/uninstall OK.");
            return 0;
        }
        catch (Exception exception)
        {
            result = "FAIL: " + exception;
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
            await File.WriteAllTextAsync(resultPath, result);
        }
    }

    internal static async Task<int> RunArtifactAsync(string setupArtifact)
    {
        setupArtifact = Path.GetFullPath(setupArtifact);
        True(File.Exists(setupArtifact), "AtlasLauncherSetup.exe est absent.");
        ValidateSetupArtifact(setupArtifact);
        string id = Guid.NewGuid().ToString("N")[..10];
        string root = $@"D:\Atlas Launcher 04D2 Test artifact {id}";
        string installRoot = Path.Combine(root, "Atlas Launcher");
        string desktopShortcut = Path.Combine(root, "Desktop", $"Atlas Launcher 04D2 Test {id}.lnk");
        string startShortcut = Path.Combine(root, "Start Menu", $"Atlas Launcher 04D2 Test {id}", "Atlas Launcher.lnk");
        string registryKey = InstallerProduct.RegistryRoot + $@"\AtlasLauncher.04D2.Test.{id}";
        string logPath = Path.Combine(root, "logs", "install.log");
        Directory.CreateDirectory(root);
        using InstallerLog log = new(logPath);
        MemoryInstallerRegistry registry = new();
        WindowsInstallerShortcutService shortcuts = new();
        InstallerEnvironment environment = new(
            installRoot,
            desktopShortcut,
            startShortcut,
            registryKey,
            [registryKey],
            setupArtifact,
            logPath,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            [@"C:\Program Files (x86)\WotLK"],
            IsTest: true,
            AllowedTestInstallRoots: [root]);
        try
        {
            EmbeddedInstallerPayloadSource payload = new();
            InstallerEngine engine = new(
                environment,
                payload,
                new InstallerPathValidator(environment),
                registry,
                shortcuts,
                new FixedProcessInspector([]),
                log);
            List<InstallerProgress> progress = [];
            InstallerInstallResult installed = await engine.InstallAsync(
                new InstallerRequest(installRoot, true, true),
                new InlineProgress<InstallerProgress>(progress.Add),
                CancellationToken.None);
            Equal(payload.Length, new FileInfo(installed.LauncherPath).Length,
                "La taille du payload réellement installé est incorrecte.");
            Equal(InstallerProduct.PayloadSha256, Hash(installed.LauncherPath),
                "Le payload réellement installé est altéré.");
            Equal(Hash(setupArtifact), Hash(installed.UninstallerPath),
                "Le désinstalleur installé diffère de l'artefact autonome.");
            ValidateProgress(progress);
            ValidateRegistration(registry.Read(registryKey), installed);
            ValidateShortcut(shortcuts, desktopShortcut, installed);
            ValidateShortcut(shortcuts, startShortcut, installed);

            UninstallerEngine uninstaller = new(
                environment,
                registry,
                shortcuts,
                new FixedProcessInspector([]),
                new FakeSystemActions(),
                log);
            await uninstaller.UninstallAsync(installRoot, CancellationToken.None);
            True(!Directory.Exists(installRoot), "L'installation artefact n'a pas été retirée.");
            Console.WriteLine(
                $"Atlas installer artifact OK (installed bytes={installed.InstalledBytes}, required bytes={engine.RequiredBytes}).");
            return 0;
        }
        finally
        {
            registry.Unregister(registryKey);
            DeleteTree(root);
        }
    }

    internal static async Task<int> WriteIntegrityProbeAsync(string resultPath)
    {
        var report = new
        {
            Integrity = GetCurrentIntegrityLevel(),
            IsAdministrator = WindowsInstallerSystemActions.IsCurrentProcessElevated(),
            ProcessId = Environment.ProcessId
        };
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(report));
        return 0;
    }

    internal static async Task<int> RunUnelevatedLaunchAsync()
    {
        True(
            WindowsInstallerSystemActions.IsCurrentProcessElevated(),
            "Ce probe doit être lancé depuis PowerShell Administrateur.");
        await ValidateUnelevatedLaunchAsync();
        Console.WriteLine("Atlas installer unelevated Explorer launch OK.");
        return 0;
    }

    private static void ValidateEmbeddedPayload()
    {
        EmbeddedInstallerPayloadSource payload = new();
        True(payload.Length > 0, "Le payload 1.2.0 doit contenir le launcher canonique.");
        Equal(
            InstallerProduct.PayloadSha256,
            payload.Sha256,
            "Le SHA-256 déclaré du payload est incorrect.");
        using Stream stream = payload.OpenRead();
        Equal(payload.Length, stream.Length, "La taille générée doit correspondre au payload embarqué.");
        string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        Equal(payload.Sha256, actualHash, "Le payload embarqué diffère du package validé.");

        using Stream pe = payload.OpenRead();
        Span<byte> header = stackalloc byte[512];
        pe.ReadExactly(header);
        int peOffset = BitConverter.ToInt32(header[0x3c..0x40]);
        ushort machine = BitConverter.ToUInt16(header[(peOffset + 4)..(peOffset + 6)]);
        Equal((ushort)0x8664, machine, "Le payload embarqué doit être x64.");

        string[] references = typeof(InstallerEngine).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();
        True(
            !references.Contains("System.Net.Http", StringComparer.Ordinal),
            "Le moteur d'installation hors ligne ne doit pas référencer HttpClient.");
        Equal(
            @"C:\Program Files\Atlas Launcher",
            InstallerProduct.GetDefaultInstallPath(),
            "Le dossier par défaut x64 est incorrect.");
        InstallerWizardViewState welcome = InstallerWizardPreviewData.Create(InstallerPreviewScenario.Welcome);
        Equal("Bienvenue dans l’assistant d’installation", welcome.HeaderTitle, "Le titre d'accueil est incorrect.");
        Equal(
            "Cet assistant va installer Atlas Launcher 1.2.0 sur cet ordinateur.",
            welcome.HeaderSubtitle,
            "Le sous-titre d'accueil est incorrect.");
    }

    private static void ValidateSelfDeleteCompatibility()
    {
        string script = WindowsInstallerSystemActions.BuildSelfDeleteScript(
            @"C:\Atlas Launcher Test\Uninstall.exe",
            @"C:\Atlas Launcher Test",
            42);
        True(!script.Contains("Wait-Process", StringComparison.Ordinal),
            "L'auto-suppression ne doit pas dépendre d'une option PowerShell moderne.");
        True(script.Contains("Get-Process -Id 42", StringComparison.Ordinal)
            && script.Contains("Set-Location -LiteralPath $env:TEMP", StringComparison.Ordinal)
            && script.Contains("Start-Sleep -Milliseconds 100", StringComparison.Ordinal)
            && script.Contains("$attempt -lt 100", StringComparison.Ordinal),
            "L'auto-suppression doit attendre la fin du processus et réessayer les suppressions.");
    }

    private static async Task ValidateSelfDeleteHelperAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Atlas Launcher 04D2 Test self-delete " + Guid.NewGuid().ToString("N"));
        string uninstaller = Path.Combine(root, InstallerProduct.UninstallerFileName);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(uninstaller, "isolated self-delete probe");
        using Process blocker = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "ping -n 2 127.0.0.1 >nul" }
        }) ?? throw new InvalidOperationException("Le processus de test d'auto-suppression n'a pas démarré.");
        string originalWorkingDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = root;
            new WindowsInstallerSystemActions().ScheduleSelfDelete(uninstaller, root, blocker.Id);
            Environment.CurrentDirectory = originalWorkingDirectory;
            await WaitUntilAsync(
                () => !Directory.Exists(root),
                TimeSpan.FromSeconds(15),
                "Le helper Windows PowerShell n'a pas retiré le désinstalleur de test.");
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
            if (!blocker.HasExited)
            {
                blocker.Kill(entireProcessTree: true);
                blocker.WaitForExit();
            }

            DeleteTree(root);
        }
    }

    private static void ValidateSetupArtifact(string setupArtifact)
    {
        Equal("1.2.0.0", FileVersionInfo.GetVersionInfo(setupArtifact).FileVersion,
            "La version de l'artefact setup est incorrecte.");

        using FileStream stream = File.OpenRead(setupArtifact);
        Span<byte> header = stackalloc byte[512];
        stream.ReadExactly(header);
        int peOffset = BitConverter.ToInt32(header[0x3c..0x40]);
        ushort machine = BitConverter.ToUInt16(header[(peOffset + 4)..(peOffset + 6)]);
        Equal((ushort)0x8664, machine, "AtlasLauncherSetup.exe doit être x64.");

        const uint loadLibraryAsDataFile = 0x00000002;
        IntPtr module = LoadLibraryEx(setupArtifact, IntPtr.Zero, loadLibraryAsDataFile);
        if (module == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            IntPtr manifestResource = FindResource(module, (IntPtr)1, (IntPtr)24);
            True(manifestResource != IntPtr.Zero, "Le manifeste Windows du setup est absent.");
            uint manifestSize = SizeofResource(module, manifestResource);
            IntPtr loadedManifest = LoadResource(module, manifestResource);
            IntPtr manifestPointer = LockResource(loadedManifest);
            True(manifestSize > 0 && manifestPointer != IntPtr.Zero, "Le manifeste Windows du setup est illisible.");
            byte[] manifestBytes = new byte[manifestSize];
            Marshal.Copy(manifestPointer, manifestBytes, 0, manifestBytes.Length);
            string manifest = Encoding.UTF8.GetString(manifestBytes);
            True(manifest.Contains("requireAdministrator", StringComparison.Ordinal),
                "Le setup distribué ne demande pas l'élévation UAC.");
            True(manifest.Contains("PerMonitorV2", StringComparison.Ordinal),
                "Le setup distribué ne déclare pas PerMonitorV2.");

            bool hasIconGroup = false;
            EnumResourceNameCallback callback = (_, _, _, _) =>
            {
                hasIconGroup = true;
                return false;
            };
            _ = EnumResourceNames(module, (IntPtr)14, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            True(hasIconGroup, "L'icône native Atlas du setup est absente.");
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private static void ValidatePaths()
    {
        using TestFixture fixture = TestFixture.Create("paths");
        string valid = Path.Combine(fixture.Root, "Atlas Launcher 04D2 Test édition spéciale");
        InstallerPathValidationResult accepted = fixture.Engine.ValidatePath(valid);
        True(accepted.IsValid, "Un chemin local absolu, Unicode et inexistant doit être accepté.");

        Equal(
            InstallerPathError.Invalid,
            fixture.Engine.ValidatePath(@"Atlas Launcher\Test").Error,
            "Un chemin relatif doit être refusé.");
        Equal(
            InstallerPathError.Network,
            fixture.Engine.ValidatePath(@"\\server\share\Atlas Launcher").Error,
            "Un chemin réseau doit être refusé.");
        Equal(
            InstallerPathError.DriveRoot,
            fixture.Engine.ValidatePath(Path.GetPathRoot(fixture.Root)!).Error,
            "La racine du disque doit être refusée.");
        Equal(
            InstallerPathError.ProtectedLocation,
            fixture.Engine.ValidatePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Error,
            "Le dossier Windows doit être refusé.");

        Directory.CreateDirectory(fixture.WowRoot);
        Equal(
            InstallerPathError.WowClient,
            fixture.Engine.ValidatePath(fixture.WowRoot).Error,
            "Le dossier du client WoW doit être refusé.");

        string foreign = Path.Combine(fixture.Root, "Atlas Launcher 04D2 Test foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "foreign.txt"), "foreign");
        Equal(
            InstallerPathError.ForeignFiles,
            fixture.Engine.ValidatePath(foreign).Error,
            "Un dossier non vide étranger doit être refusé.");

        InstallerPathValidator insufficient = new(
            fixture.Environment,
            new FixedDriveSpace(1024, DriveType.Fixed),
            new FixedAccessProbe(true));
        Equal(
            InstallerPathError.InsufficientSpace,
            insufficient.Validate(valid, 2048).Error,
            "L'espace insuffisant doit bloquer Suivant.");

        InstallerPathValidator inaccessible = new(
            fixture.Environment,
            new FixedDriveSpace(long.MaxValue, DriveType.Fixed),
            new FixedAccessProbe(false));
        Equal(
            InstallerPathError.Inaccessible,
            inaccessible.Validate(valid, 2048).Error,
            "Un dossier inaccessible doit être refusé.");
    }

    private static async Task ValidateTransactionalInstallAndUninstallAsync()
    {
        using TestFixture fixture = TestFixture.Create("transaction");
        string userData = Path.Combine(fixture.Root, "LocalAppData sentinel.txt");
        string wowData = Path.Combine(fixture.WowRoot, "Config.wtf");
        Directory.CreateDirectory(fixture.WowRoot);
        File.WriteAllText(userData, "keep-user-data");
        File.WriteAllText(wowData, "keep-wow-data");

        List<InstallerProgress> progress = [];
        InstallerInstallResult installed = await fixture.Engine.InstallAsync(
            new InstallerRequest(fixture.InstallRoot, true, true),
            new InlineProgress<InstallerProgress>(progress.Add),
            CancellationToken.None);

        string[] files = Directory.GetFiles(fixture.InstallRoot)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        SequenceEqual(
            new[] { InstallerProduct.InstallStateFileName, InstallerProduct.UninstallerFileName, InstallerProduct.LauncherFileName }
                .Order(StringComparer.Ordinal),
            files,
            "Le dossier installé contient des fichiers inattendus.");
        Equal(fixture.PayloadSha256, Hash(installed.LauncherPath), "Le launcher installé est altéré.");
        Equal(Hash(fixture.SetupPath), Hash(installed.UninstallerPath), "Uninstall.exe doit être la copie autonome du setup.");
        True(File.Exists(fixture.Environment.DesktopShortcutPath), "Le raccourci Bureau manque.");
        True(File.Exists(fixture.Environment.StartMenuShortcutPath), "Le raccourci Démarrer manque.");
        ValidateShortcut(fixture.Shortcuts, fixture.Environment.DesktopShortcutPath, installed);
        ValidateShortcut(fixture.Shortcuts, fixture.Environment.StartMenuShortcutPath, installed);
        ValidateProgress(progress);
        ValidateRegistration(fixture.Registry.Read(fixture.Environment.RegistrySubKey), installed);

        AtlasInstallState state = await UninstallerEngine.ReadStateAsync(fixture.InstallRoot);
        Equal(state, UninstallerEngine.ReadState(fixture.InstallRoot),
            "La lecture synchrone utilisée par la fenêtre de désinstallation doit préserver l'état exact.");
        True(state.IsTestInstallation, "Le garde-fou de l'installation de test doit être persisté.");
        UninstallResult removed = await fixture.Uninstaller.UninstallAsync(
            fixture.InstallRoot,
            CancellationToken.None);
        Equal(UninstallStatus.Completed, removed.Status, "La désinstallation directe a échoué.");
        True(!Directory.Exists(fixture.InstallRoot), "Le dossier installé doit être retiré.");
        True(!File.Exists(fixture.Environment.DesktopShortcutPath), "Le raccourci Bureau doit être retiré.");
        True(!File.Exists(fixture.Environment.StartMenuShortcutPath), "Le raccourci Démarrer doit être retiré.");
        Equal(0, fixture.Registry.Read(fixture.Environment.RegistrySubKey).Count, "L'entrée registre doit être retirée.");
        Equal("keep-user-data", File.ReadAllText(userData), "Les données LocalAppData ont été supprimées.");
        Equal("keep-wow-data", File.ReadAllText(wowData), "Le client WoW a été modifié.");

        InstallerInstallResult second = await fixture.Engine.InstallAsync(
            new InstallerRequest(fixture.InstallRoot, false, true),
            progress: null,
            CancellationToken.None);
        True(!File.Exists(fixture.Environment.DesktopShortcutPath), "Bureau décoché ne doit créer aucun raccourci.");
        True(File.Exists(fixture.Environment.StartMenuShortcutPath), "Le menu Démarrer coché doit être créé.");
        Equal(1, Directory.GetFiles(Path.GetDirectoryName(fixture.Environment.StartMenuShortcutPath)!, "*.lnk").Length,
            "Une réinstallation ne doit pas dupliquer le raccourci Démarrer.");
        await fixture.Uninstaller.UninstallAsync(second.InstallPath, CancellationToken.None);
    }

    private static async Task ValidateRollbackAsync()
    {
        foreach (InstallerWorkPhase phase in new[]
        {
            InstallerWorkPhase.CreatingShortcuts,
            InstallerWorkPhase.RegisteringWindows
        })
        {
            using TestFixture fixture = TestFixture.Create("rollback-" + phase, new ThrowAfterPhase(phase));
            await ThrowsAsync<InstallerOperationException>(() => fixture.Engine.InstallAsync(
                new InstallerRequest(fixture.InstallRoot, true, true),
                progress: null,
                CancellationToken.None));
            AssertRolledBack(fixture);
        }

        using (TestFixture badHash = TestFixture.Create("bad-hash", payloadHashOverride: new string('0', 64)))
        {
            await ThrowsAsync<InstallerOperationException>(() => badHash.Engine.InstallAsync(
                new InstallerRequest(badHash.InstallRoot, true, true),
                progress: null,
                CancellationToken.None));
            AssertRolledBack(badHash);
        }

        using (TestFixture existingEmpty = TestFixture.Create("empty-restore", new ThrowAfterPhase(InstallerWorkPhase.InstallingFiles)))
        {
            Directory.CreateDirectory(existingEmpty.InstallRoot);
            await ThrowsAsync<InstallerOperationException>(() => existingEmpty.Engine.InstallAsync(
                new InstallerRequest(existingEmpty.InstallRoot, false, false),
                progress: null,
                CancellationToken.None));
            True(Directory.Exists(existingEmpty.InstallRoot), "Un dossier vide préexistant doit être restauré.");
            True(!Directory.EnumerateFileSystemEntries(existingEmpty.InstallRoot).Any(), "Le dossier restauré doit rester vide.");
        }

        using TestFixture redaction = TestFixture.Create("redaction");
        redaction.Log.Error("token=abc password=hunter2 Authorization: Bearer dangerous");
        string log = File.ReadAllText(redaction.Log.Path);
        True(!log.Contains("hunter2", StringComparison.Ordinal)
            && !log.Contains("dangerous", StringComparison.Ordinal)
            && !log.Contains("token=abc", StringComparison.Ordinal),
            "Le journal d'installation doit masquer les secrets.");
    }

    private static async Task ValidateSingleFlightAsync()
    {
        BlockingFault fault = new();
        using TestFixture fixture = TestFixture.Create("single-flight", fault);
        Task<InstallerInstallResult> first = Task.Run(() => fixture.Engine.InstallAsync(
            new InstallerRequest(fixture.InstallRoot, false, false),
            progress: null,
            CancellationToken.None));
        True(fault.Entered.Wait(TimeSpan.FromSeconds(10)), "La première installation n'a pas démarré.");
        await ThrowsAsync<InvalidOperationException>(() => fixture.Engine.InstallAsync(
            new InstallerRequest(fixture.InstallRoot, false, false),
            progress: null,
            CancellationToken.None));
        fault.Release.Set();
        InstallerInstallResult result = await first;
        await fixture.Uninstaller.UninstallAsync(result.InstallPath, CancellationToken.None);
    }

    private static async Task ValidateExactProcessMatchingAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "Atlas Launcher 04D2 Test process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string logPath = Path.Combine(root, "install.log");
        using InstallerLog log = new(logPath);
        string executable = Path.Combine(root, InstallerProduct.LauncherFileName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "ping -n 30 127.0.0.1 >nul" }
        }) ?? throw new InvalidOperationException("Le processus isolé n'a pas démarré.");
        try
        {
            await Task.Delay(250);
            WindowsInstallerProcessInspector inspector = new(log);
            True(
                inspector.FindByExactPath(executable).Contains(process.Id),
                "Le processus Atlas du chemin exact doit être détecté.");
            string other = Path.Combine(root, "other", InstallerProduct.LauncherFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), other);
            True(
                !inspector.FindByExactPath(other).Contains(process.Id),
                "Un exécutable homonyme situé ailleurs ne doit pas être ciblé.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Directory.Delete(root, recursive: true);
        }

        using TestFixture fixture = TestFixture.Create(
            "open-launcher",
            processInspector: new FixedProcessInspector([4242]));
        await ThrowsAsync<InstallerOperationException>(() => fixture.Engine.InstallAsync(
            new InstallerRequest(fixture.InstallRoot, false, false),
            progress: null,
            CancellationToken.None));
        AssertRolledBack(fixture);
    }

    private static async Task RunElevatedInstallCyclesAsync(string setupArtifact)
    {
        string id = Guid.NewGuid().ToString("N")[..10];
        string programFilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            $"Atlas Launcher 04D2 Test {id}");
        string secondDriveRoot = $@"D:\Atlas Launcher 04D2 Test {id}\Atlas Launcher";
        string desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"Atlas Launcher 04D2 Test {id}.lnk");
        string startMenuShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            $"Atlas Launcher 04D2 Test {id}",
            "Atlas Launcher.lnk");
        string registrySubKey = InstallerProduct.RegistryRoot + $@"\AtlasLauncher.04D2.Test.{id}";
        string testRoot = Path.Combine(Path.GetTempPath(), $"Atlas Launcher 04D2 Test elevated {id}");
        string logPath = Path.Combine(testRoot, "install.log");
        Directory.CreateDirectory(testRoot);
        using InstallerLog log = new(logPath);
        WindowsInstallerRegistry registry = new(log);
        WindowsInstallerShortcutService shortcuts = new();
        WindowsInstallerProcessInspector processes = new(log);
        FakeSystemActions systemActions = new();
        InstallerEnvironment environment = new(
            programFilesRoot,
            desktopShortcut,
            startMenuShortcut,
            registrySubKey,
            [registrySubKey],
            setupArtifact,
            logPath,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            [@"C:\Program Files (x86)\WotLK"],
            IsTest: true,
            AllowedTestInstallRoots: [programFilesRoot, secondDriveRoot]);
        EmbeddedInstallerPayloadSource payload = new();

        try
        {
            await RunCycleAsync(programFilesRoot, desktop: true, startMenu: true, invokeInstalledUninstaller: false);
            True(new DriveInfo(Path.GetPathRoot(secondDriveRoot)!).DriveType == DriveType.Fixed, "D: doit être un disque local fixe.");
            await RunCycleAsync(secondDriveRoot, desktop: false, startMenu: true, invokeInstalledUninstaller: false);
            await RunCycleAsync(programFilesRoot, desktop: true, startMenu: true, invokeInstalledUninstaller: true);
            await RunCycleAsync(programFilesRoot, desktop: true, startMenu: true, invokeInstalledUninstaller: true);
        }
        finally
        {
            registry.Unregister(registrySubKey);
            DeleteShortcut(shortcuts, desktopShortcut, programFilesRoot);
            DeleteShortcut(shortcuts, startMenuShortcut, programFilesRoot);
            DeleteTree(programFilesRoot);
            DeleteTree(Path.GetDirectoryName(secondDriveRoot));
            DeleteTree(testRoot);
        }

        async Task RunCycleAsync(
            string installRoot,
            bool desktop,
            bool startMenu,
            bool invokeInstalledUninstaller)
        {
            InstallerEnvironment cycleEnvironment = environment with
            {
                DefaultInstallPath = installRoot,
                AllowedTestInstallRoots = [installRoot]
            };
            InstallerPathValidator validator = new(cycleEnvironment);
            InstallerEngine engine = new(
                cycleEnvironment,
                payload,
                validator,
                registry,
                shortcuts,
                processes,
                log);
            List<InstallerProgress> progress = [];
            InstallerInstallResult result = await engine.InstallAsync(
                new InstallerRequest(installRoot, desktop, startMenu),
                new InlineProgress<InstallerProgress>(progress.Add),
                CancellationToken.None);

            Equal(InstallerProduct.PayloadSha256, Hash(result.LauncherPath), "Le payload Program Files est altéré.");
            Equal(payload.Length, new FileInfo(result.LauncherPath).Length, "La taille installée est incorrecte.");
            Equal(Hash(setupArtifact), Hash(result.UninstallerPath), "Uninstall.exe n'est pas autonome.");
            Equal("1.2.0.0", FileVersionInfo.GetVersionInfo(result.LauncherPath).FileVersion,
                "La version du launcher installé est incorrecte.");
            ValidateProgress(progress);
            IReadOnlyDictionary<string, object?> values = registry.Read(registrySubKey);
            ValidateRegistration(values, result);
            Equal(desktop, File.Exists(desktopShortcut), "L'option Bureau n'est pas respectée.");
            Equal(startMenu, File.Exists(startMenuShortcut), "L'option menu Démarrer n'est pas respectée.");
            if (desktop)
            {
                ValidateShortcut(shortcuts, desktopShortcut, result);
            }

            if (startMenu)
            {
                ValidateShortcut(shortcuts, startMenuShortcut, result);
                Equal(1, Directory.GetFiles(Path.GetDirectoryName(startMenuShortcut)!, "*.lnk").Length,
                    "Le menu Démarrer contient un doublon.");
            }

            string[] installedFiles = Directory.GetFiles(installRoot)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray()!;
            SequenceEqual(
                new[] { InstallerProduct.InstallStateFileName, InstallerProduct.UninstallerFileName, InstallerProduct.LauncherFileName }
                    .Order(StringComparer.Ordinal),
                installedFiles,
                "L'installation réelle contient des fichiers annexes.");

            if (invokeInstalledUninstaller)
            {
                string quiet = (string)values["QuietUninstallString"]!;
                using Process uninstall = StartCommandLine(quiet);
                True(uninstall.WaitForExit(60_000), "Le désinstalleur Windows n'a pas répondu.");
                Equal(0, uninstall.ExitCode, "Le désinstalleur Windows a échoué.");
                await WaitUntilAsync(
                    () => !Directory.Exists(installRoot),
                    TimeSpan.FromSeconds(30),
                    "Le désinstalleur ne s'est pas supprimé lui-même.");
            }
            else
            {
                UninstallerEngine uninstaller = new(
                    cycleEnvironment,
                    registry,
                    shortcuts,
                    processes,
                    systemActions,
                    log);
                UninstallResult removed = await uninstaller.UninstallAsync(installRoot, CancellationToken.None);
                Equal(UninstallStatus.Completed, removed.Status, "La désinstallation réelle directe a échoué.");
            }

            True(!Directory.Exists(installRoot), "Le dossier réel de test doit être retiré.");
            True(!File.Exists(desktopShortcut), "Le raccourci Bureau de test subsiste.");
            True(!File.Exists(startMenuShortcut), "Le raccourci Démarrer de test subsiste.");
            Equal(0, registry.Read(registrySubKey).Count, "L'entrée HKLM de test subsiste.");
        }
    }

    private static async Task ValidateUnelevatedLaunchAsync()
    {
        string testAssembly = Path.Combine(AppContext.BaseDirectory, "WotLK.Launcher.IntegrationTests.dll");
        True(File.Exists(testAssembly), "L'assembly du probe d'intégrité est absent.");
        string dotnetHost = FindDotnetHost();
        string resultPath = Path.Combine(
            Path.GetTempPath(),
            "Atlas Launcher 04D2 Test integrity-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            InstallerUnelevatedProcessLauncher.Launch(
                dotnetHost,
                $"\"{testAssembly}\" --installer-integrity-probe \"{resultPath}\"",
                AppContext.BaseDirectory);
            await WaitUntilAsync(
                () => File.Exists(resultPath),
                TimeSpan.FromSeconds(30),
                "Le processus lancé via Explorer n'a pas répondu.");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            string integrity = document.RootElement.GetProperty("Integrity").GetString()!;
            bool administrator = document.RootElement.GetProperty("IsAdministrator").GetBoolean();
            Equal("Medium", integrity, "Le launcher final doit être lancé en intégrité Medium.");
            True(!administrator, "Le launcher final ne doit pas hériter des droits administrateur.");
        }
        finally
        {
            File.Delete(resultPath);
        }
    }

    private static string FindDotnetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        DirectoryInfo? directory = new(RuntimeEnvironment.GetRuntimeDirectory());
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Le dotnet.exe du probe d'intégrité est introuvable.");
    }

    private static void ValidateRegistration(
        IReadOnlyDictionary<string, object?> values,
        InstallerInstallResult installed)
    {
        Equal(InstallerProduct.Name, values["DisplayName"] as string, "DisplayName incorrect.");
        Equal(InstallerProduct.Version, values["DisplayVersion"] as string, "DisplayVersion incorrect.");
        Equal(InstallerProduct.Publisher, values["Publisher"] as string, "Publisher incorrect.");
        Equal(installed.InstallPath, values["InstallLocation"] as string, "InstallLocation incorrect.");
        Equal(installed.LauncherPath, values["DisplayIcon"] as string, "DisplayIcon incorrect.");
        True(((string)values["UninstallString"]!).Contains(installed.UninstallerPath, StringComparison.OrdinalIgnoreCase),
            "UninstallString ne cible pas Uninstall.exe.");
        True(((string)values["QuietUninstallString"]!).Contains("--quiet", StringComparison.Ordinal),
            "QuietUninstallString doit correspondre au mode silencieux réellement supporté.");
        Equal(1, Convert.ToInt32(values["NoModify"]), "NoModify incorrect.");
        Equal(1, Convert.ToInt32(values["NoRepair"]), "NoRepair incorrect.");
        long expectedKiB = Math.Max(1, (installed.InstalledBytes + 1023) / 1024);
        Equal(expectedKiB, Convert.ToInt64(values["EstimatedSize"]), "EstimatedSize incorrect.");
        True(values["InstallDate"] is string date && date.Length == 8, "InstallDate incorrect.");
    }

    private static void ValidateShortcut(
        IInstallerShortcutService shortcuts,
        string shortcutPath,
        InstallerInstallResult installed)
    {
        InstallerShortcut shortcut = shortcuts.Read(shortcutPath)
            ?? throw new InvalidOperationException("Le raccourci est illisible.");
        True(InstallerEnvironment.SamePath(installed.LauncherPath, shortcut.TargetPath), "La cible du raccourci est incorrecte.");
        True(InstallerEnvironment.SamePath(installed.InstallPath, shortcut.WorkingDirectory), "Le working directory est incorrect.");
        True(shortcut.IconLocation.Contains(installed.LauncherPath, StringComparison.OrdinalIgnoreCase), "L'icône Atlas est incorrecte.");
    }

    private static void ValidateProgress(IReadOnlyList<InstallerProgress> progress)
    {
        True(progress.Count > 6, "La progression réelle est trop peu détaillée.");
        double previous = -1;
        foreach (InstallerProgress item in progress)
        {
            True(item.Percent >= previous, "La progression ne doit jamais reculer.");
            previous = item.Percent;
        }

        foreach (InstallerWorkPhase phase in Enum.GetValues<InstallerWorkPhase>())
        {
            True(progress.Any(item => item.Phase == phase), $"La phase {phase} n'a pas été publiée.");
        }

        Equal(100d, progress[^1].Percent, "La progression finale doit atteindre 100 %.");
    }

    private static void AssertRolledBack(TestFixture fixture)
    {
        True(!Directory.Exists(fixture.InstallRoot), "Le dossier incomplet n'a pas été retiré.");
        True(!File.Exists(fixture.Environment.DesktopShortcutPath), "Le raccourci Bureau incomplet subsiste.");
        True(!File.Exists(fixture.Environment.StartMenuShortcutPath), "Le raccourci Démarrer incomplet subsiste.");
        Equal(0, fixture.Registry.Read(fixture.Environment.RegistrySubKey).Count, "L'entrée registre partielle subsiste.");
        string parent = Path.GetDirectoryName(fixture.InstallRoot)!;
        True(!Directory.EnumerateDirectories(parent, ".atlas-launcher-staging-*", SearchOption.TopDirectoryOnly).Any(),
            "Un staging incomplet subsiste.");
    }

    private static Process StartCommandLine(string commandLine)
    {
        int quoteEnd = commandLine.IndexOf('"', 1);
        string executable = commandLine[1..quoteEnd];
        string arguments = commandLine[(quoteEnd + 1)..].Trim();
        return Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Le désinstalleur enregistré n'a pas démarré.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(100);
        }
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string GetCurrentIntegrityLevel()
    {
        const uint tokenQuery = 0x0008;
        const int tokenIntegrityLevel = 25;
        if (!OpenProcessToken(GetCurrentProcess(), tokenQuery, out IntPtr token))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            _ = GetTokenInformation(token, tokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, tokenIntegrityLevel, buffer, length, out _))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }

                IntPtr sid = Marshal.ReadIntPtr(buffer);
                IntPtr countPointer = GetSidSubAuthorityCount(sid);
                byte count = Marshal.ReadByte(countPointer);
                uint rid = unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))));
                return rid switch
                {
                    >= 0x4000 => "System",
                    >= 0x3000 => "High",
                    >= 0x2000 => "Medium",
                    _ => "Low"
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static void DeleteShortcut(
        IInstallerShortcutService shortcuts,
        string shortcutPath,
        string installRoot)
    {
        try
        {
            shortcuts.DeleteIfOwned(shortcutPath, Path.Combine(installRoot, InstallerProduct.LauncherFileName));
        }
        catch
        {
            File.Delete(shortcutPath);
        }
    }

    private static void DeleteTree(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"L'exception {typeof(T).Name} était attendue.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; obtenu={actual}.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    private delegate bool EnumResourceNameCallback(
        IntPtr module,
        IntPtr type,
        IntPtr name,
        IntPtr parameter);

    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNames(
        IntPtr module,
        IntPtr type,
        EnumResourceNameCallback callback,
        IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resourceData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    private sealed class TestFixture : IDisposable
    {
        private TestFixture(
            string root,
            string installRoot,
            string wowRoot,
            string setupPath,
            string payloadSha256,
            InstallerEnvironment environment,
            InstallerLog log,
            MemoryInstallerRegistry registry,
            WindowsInstallerShortcutService shortcuts,
            InstallerEngine engine,
            UninstallerEngine uninstaller)
        {
            Root = root;
            InstallRoot = installRoot;
            WowRoot = wowRoot;
            SetupPath = setupPath;
            PayloadSha256 = payloadSha256;
            Environment = environment;
            Log = log;
            Registry = registry;
            Shortcuts = shortcuts;
            Engine = engine;
            Uninstaller = uninstaller;
        }

        internal string Root { get; }
        internal string InstallRoot { get; }
        internal string WowRoot { get; }
        internal string SetupPath { get; }
        internal string PayloadSha256 { get; }
        internal InstallerEnvironment Environment { get; }
        internal InstallerLog Log { get; }
        internal MemoryInstallerRegistry Registry { get; }
        internal WindowsInstallerShortcutService Shortcuts { get; }
        internal InstallerEngine Engine { get; }
        internal UninstallerEngine Uninstaller { get; }

        internal static TestFixture Create(
            string name,
            IInstallerFaultInjector? faults = null,
            string? payloadHashOverride = null,
            IInstallerProcessInspector? processInspector = null)
        {
            string id = Guid.NewGuid().ToString("N")[..8];
            string root = Path.Combine(Path.GetTempPath(), $"Atlas Launcher 04D2 Test {name}-{id}");
            string fixtureRoot = Path.Combine(root, "fixtures");
            Directory.CreateDirectory(fixtureRoot);
            string setupPath = Path.Combine(fixtureRoot, "AtlasLauncherSetup.exe");
            File.WriteAllBytes(setupPath, Enumerable.Range(0, 64 * 1024).Select(value => (byte)(value % 251)).ToArray());
            string payloadPath = Path.Combine(fixtureRoot, "WotLK.Launcher.payload");
            File.WriteAllBytes(payloadPath, Enumerable.Range(0, 512 * 1024).Select(value => (byte)(value % 239)).ToArray());
            string payloadSha = Hash(payloadPath);
            string installRoot = Path.Combine(root, "Atlas Launcher 04D2 Test install");
            string wowRoot = Path.Combine(root, "WotLK client protected");
            string registryKey = InstallerProduct.RegistryRoot + $@"\AtlasLauncher.04D2.Test.{id}";
            InstallerEnvironment environment = new(
                installRoot,
                Path.Combine(root, "Desktop", $"Atlas Launcher 04D2 Test {id}.lnk"),
                Path.Combine(root, "Start Menu", $"Atlas Launcher 04D2 Test {id}", "Atlas Launcher.lnk"),
                registryKey,
                [registryKey],
                setupPath,
                Path.Combine(root, "logs", "install.log"),
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
                [wowRoot],
                IsTest: true,
                AllowedTestInstallRoots: [root]);
            InstallerLog log = new(environment.LogPath);
            MemoryInstallerRegistry registry = new();
            WindowsInstallerShortcutService shortcuts = new();
            IInstallerProcessInspector processes = processInspector ?? new FixedProcessInspector([]);
            FileInstallerPayloadSource payload = new(
                payloadPath,
                new FileInfo(payloadPath).Length,
                payloadHashOverride ?? payloadSha);
            InstallerPathValidator validator = new(environment);
            InstallerEngine engine = new(
                environment,
                payload,
                validator,
                registry,
                shortcuts,
                processes,
                log,
                faults);
            UninstallerEngine uninstaller = new(
                environment,
                registry,
                shortcuts,
                processes,
                new FakeSystemActions(),
                log);
            return new TestFixture(
                root,
                installRoot,
                wowRoot,
                setupPath,
                payloadSha,
                environment,
                log,
                registry,
                shortcuts,
                engine,
                uninstaller);
        }

        public void Dispose()
        {
            try
            {
                Registry.Unregister(Environment.RegistrySubKey);
                DeleteTree(Root);
            }
            finally
            {
                Log.Dispose();
            }
        }
    }

    private sealed class MemoryInstallerRegistry : IInstallerRegistry
    {
        private readonly Dictionary<string, Dictionary<string, object?>> _keys =
            new(StringComparer.OrdinalIgnoreCase);

        public ExistingInstallation Detect(IReadOnlyList<string> registrySubKeys, IEnumerable<string> fallbackPaths)
        {
            foreach (string key in registrySubKeys)
            {
                if (_keys.TryGetValue(key, out Dictionary<string, object?>? values))
                {
                    return new ExistingInstallation(
                        ExistingInstallationStatus.Installed,
                        values["InstallLocation"] as string,
                        "Installation de test existante.",
                        key);
                }
            }

            foreach (string path in fallbackPaths)
            {
                if (File.Exists(Path.Combine(path, InstallerProduct.LauncherFileName)))
                {
                    return new ExistingInstallation(
                        ExistingInstallationStatus.Installed,
                        path,
                        "Installation de test existante.",
                        null);
                }
            }

            return new ExistingInstallation(ExistingInstallationStatus.None, null, string.Empty, null);
        }

        public void Register(InstalledApplicationRegistration registration)
        {
            _keys[registration.RegistrySubKey] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DisplayName"] = InstallerProduct.Name,
                ["DisplayVersion"] = InstallerProduct.Version,
                ["Publisher"] = InstallerProduct.Publisher,
                ["InstallLocation"] = registration.InstallLocation,
                ["DisplayIcon"] = registration.LauncherPath,
                ["UninstallString"] = $"\"{registration.UninstallerPath}\" --uninstall",
                ["QuietUninstallString"] = $"\"{registration.UninstallerPath}\" --uninstall --quiet",
                ["InstallDate"] = DateTime.Now.ToString("yyyyMMdd"),
                ["EstimatedSize"] = registration.EstimatedSizeKiB,
                ["NoModify"] = 1,
                ["NoRepair"] = 1
            };
        }

        public void Unregister(string registrySubKey) => _keys.Remove(registrySubKey);

        public IReadOnlyDictionary<string, object?> Read(string registrySubKey) =>
            _keys.TryGetValue(registrySubKey, out Dictionary<string, object?>? values)
                ? values
                : new Dictionary<string, object?>();
    }

    private sealed class FixedDriveSpace(long available, DriveType type) : IInstallerDriveSpace
    {
        public bool TryGetAvailableBytes(string fullPath, out long availableBytes, out DriveType driveType)
        {
            availableBytes = available;
            driveType = type;
            return true;
        }
    }

    private sealed class FixedAccessProbe(bool canWrite) : IInstallerAccessProbe
    {
        public bool CanWrite(string fullPath) => canWrite;
    }

    private sealed class FixedProcessInspector(IReadOnlyList<int> processIds) : IInstallerProcessInspector
    {
        public IReadOnlyList<int> FindByExactPath(string executablePath) => processIds;
    }

    private sealed class ThrowAfterPhase(InstallerWorkPhase target) : IInstallerFaultInjector
    {
        public void AfterPhase(InstallerWorkPhase phase)
        {
            if (phase == target)
            {
                throw new IOException("Injected copy/commit failure.");
            }
        }
    }

    private sealed class BlockingFault : IInstallerFaultInjector
    {
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public void AfterPhase(InstallerWorkPhase phase)
        {
            if (phase == InstallerWorkPhase.Preparation)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    private sealed class FakeSystemActions : IInstallerSystemActions
    {
        public void OpenInstalledApps()
        {
        }

        public void LaunchUnelevated(string executablePath, string workingDirectory)
        {
        }

        public void ScheduleSelfDelete(string uninstallerPath, string installRoot, int processId)
        {
            File.Delete(uninstallerPath);
            if (Directory.Exists(installRoot) && !Directory.EnumerateFileSystemEntries(installRoot).Any())
            {
                Directory.Delete(installRoot);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record ProductSafetySnapshot(
        string RegistryFingerprint,
        string InstallFingerprint,
        string UserDataFingerprint,
        string WowFingerprint)
    {
        internal static ProductSafetySnapshot Capture() => new(
            CaptureRegistry(),
            CaptureFiles(@"C:\Program Files (x86)\WotLK Launcher"),
            CaptureFiles(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WotLK Launcher"))
                + "\n--- Atlas Launcher ---\n"
                + CaptureFiles(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Atlas Launcher")),
            CaptureFiles(@"C:\Program Files (x86)\WotLK", maxFiles: 40));

        private static string CaptureRegistry()
        {
            List<string> values = [];
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (string subKey in new[] { InstallerProduct.RegistrySubKey }.Concat(InstallerProduct.LegacyRegistrySubKeys))
                {
                    using RegistryKey? key = machine.OpenSubKey(subKey);
                    if (key is null)
                    {
                        continue;
                    }

                    values.Add(view + ":" + subKey + ":" + string.Join(
                        "|",
                        key.GetValueNames().Order(StringComparer.Ordinal).Select(name => name + "=" + key.GetValue(name))));
                }
            }

            return string.Join("\n", values);
        }

        private static string CaptureFiles(string root, int maxFiles = 100)
        {
            if (!Directory.Exists(root))
            {
                return "absent";
            }

            return string.Join(
                "\n",
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(maxFiles)
                    .Select(path =>
                    {
                        FileInfo info = new(path);
                        return Path.GetRelativePath(root, path) + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                    }));
        }
    }
}
