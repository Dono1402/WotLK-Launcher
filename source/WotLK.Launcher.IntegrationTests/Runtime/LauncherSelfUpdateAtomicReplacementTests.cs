using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Updater;

internal static class LauncherSelfUpdateAtomicReplacementTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeSingleExecutableReleaseContract();
        ValidateGameDirectoryElevationBoundary();
        ValidateRequesterImpersonationBoundary();
        ValidateUpdaterProtectedPathBoundary();
        await ValidateActiveExecutableSharingBoundaryAsync();
        ValidateInternalCommandLineContract();
        ValidateHelperRequesterBoundary();
        ValidateHelperHashDoesNotCaptureWpfContext();
        await LauncherUpdateProcessIdentityTests.RunAsync();
        await PreserveLegacyStartupHandshakeWithoutLegacyElevationAsync();
        await RejectElevatedUpdatedProcessAndReleaseTargetAsync();
        await PrepareTransactionWithoutTouchingActiveReleaseAsync();
        await RejectInvalidAuthenticatedVersionBeforeTransactionAsync();
        await RejectInvalidCandidateBeforeTouchingReleaseAsync();
        await RejectCandidateSwapAfterInitialValidationAsync();
        await RejectStagedSwapBeforeAtomicMoveAsync();
        await RejectStagedSwapAfterFinalValidationAsync();
        await RefuseSwapWhenProtectedAclValidatorRejectsAsync();
        await KeepPreviousReleaseAcrossPreSwapCrashPointsAsync();
        await RetryTransientAtomicSwapFailureAsync();
        await AbandonPermanentAtomicSwapFailureAsync();
        await RecoverCrashAfterAtomicSwapAsync();
        await RecoverCrashBeforeNewLauncherStartAsync();
        await RecoverCrashAfterNewLauncherStartAsync();
        await RecoverCrashAfterReadyConfirmationAsync();
        await RecoverCrashAfterCommitPersistedAsync();
        await RejectForgedCommittedPhaseWithoutProtectedProofAsync();
        await RefuseStaleRecoveryOverUnknownNewerTargetAsync();
        await RollBackWhenNewLauncherCannotStartAsync();
        await RollBackWhenNewLauncherExitsImmediatelyAsync();
        await RejectReadyFromExitedLauncherAsync();
        await RollBackWhenReadyNeverArrivesAsync();
        await IgnoreWrongAndStaleReadySignalsAsync();
        await AcceptImmediateAndDelayedReadySignalsAsync();
        await KeepPortableUpdateSuccessfulWithoutRegistrationAsync();
        await RetryTemporaryWindowsLockAsync();
        await AbandonPermanentWindowsLockAsync();
        await KeepTargetWholeDuringAtomicSwapAsync();
        await RefuseUnsafeTransactionPathsAsync();
        RejectAmbiguousAndOversizedTransactionJson();
        RejectAmbiguousAndOversizedProcessSignals();
        RejectUserWorkspaceReparseSwapAtWrite();
        HoldCandidateIdentityAcrossPrivilegedCopyBoundary();
        await LeaveReleaseUntouchedWhenParentDoesNotExitAsync();
        Console.WriteLine("Launcher self-update atomic replacement OK (04B.3a).");
        return 0;
    }

    private static async Task PreserveLegacyStartupHandshakeWithoutLegacyElevationAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        File.Copy(typeof(LauncherManifest).Assembly.Location, environment.TargetPath, overwrite: true);
        LauncherUpdateTransaction original = environment.Transaction;
        LauncherUpdateTransaction legacy = original with
        {
            SchemaVersion = 1,
            HelperPath = Path.Combine(original.WorkspacePath, "updater.exe"),
            HelperAcceptedSignalPath = Path.Combine(original.WorkspacePath, "helper-accepted.json"),
            ExpectedSize = new FileInfo(environment.TargetPath).Length,
            CandidateSha256 = await LauncherUpdateTransactionStore.ComputeSha256Async(
                environment.TargetPath, CancellationToken.None),
            AuthenticatedTargetVersion = FileVersionInfo.GetVersionInfo(environment.TargetPath).FileVersion,
            AuthenticatedManifest = null,
            Phase = LauncherUpdateTransactionPhase.SwappedAwaitingStart
        };

        void WriteLegacy(LauncherUpdateTransaction transaction)
        {
            JsonSerializerOptions options = new()
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };
            System.Text.Json.Nodes.JsonObject json =
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(transaction, options))!.AsObject();
            // These fields did not exist in the serializer shipped in version 1.5.0.
            json.Remove("AuthenticatedManifest");
            json.Remove("NewProcessStartedAt");
            File.WriteAllText(original.TransactionPath, json.ToJsonString());
        }

        WriteLegacy(legacy);
        string before = File.ReadAllText(original.TransactionPath);
        Throws<InvalidDataException>(() => environment.Store.Load(original.TransactionPath),
            "Le helper élevé et la récupération doivent continuer à refuser le schéma historique.");
        Throws<InvalidDataException>(() => environment.Store.Save(legacy),
            "Aucune nouvelle transaction historique ne doit pouvoir être enregistrée.");

        LauncherUpdateStartupSession withoutMarker = LauncherUpdateStartupSession.BeginForTests(
            [], true, environment.Store, environment.TargetPath);
        True(!withoutMarker.HasPendingTransactions && !withoutMarker.RecoveryOccurred,
            "Un démarrage normal ne doit pas relancer le helper historique.");
        True(!File.Exists(legacy.StartedSignalPath), "Le signal historique nécessite le marqueur explicite.");

        LauncherUpdateStartupSession startup = LauncherUpdateStartupSession.BeginForTests(
            [LauncherUpdateCommandLine.BuildPostUpdateArgument(legacy.TransactionId)],
            true, environment.Store, environment.TargetPath);
        True(startup.HasPendingTransactions && !startup.RecoveryOccurred,
            "La transition 1.5.0 doit uniquement participer au handshake explicite.");
        True(environment.Store.TryReadStartedSignal(legacy)?.ProcessId == Environment.ProcessId,
            "L'ancien helper doit pouvoir lire le signal Started du nouveau client.");
        await startup.ConfirmReadyAsync(() => true);
        True(environment.Store.TryReadReadySignal(legacy)?.ProcessId == Environment.ProcessId,
            "L'ancien helper doit pouvoir lire le signal Ready après stabilisation.");
        Equal(before, File.ReadAllText(original.TransactionPath),
            "Le nouveau client ne doit ni migrer ni réécrire la transaction pilotée par l'ancien helper.");

        LauncherUpdateTransaction[] invalid =
        [
            legacy with { SchemaVersion = 0 },
            legacy with { CandidateSha256 = new string('0', 64) },
            legacy with { ExpectedSize = legacy.ExpectedSize + 1 },
            legacy with { AuthenticatedTargetVersion = "99.0.0" },
            legacy with { Phase = LauncherUpdateTransactionPhase.Prepared },
            legacy with { TargetPath = Path.Combine(environment.Root, "another.exe") },
            legacy with { ReadySignalPath = Path.Combine(environment.Root, "outside.json") },
            legacy with { HelperPath = original.HelperPath },
            legacy with { NewProcessId = int.MaxValue }
        ];
        foreach (LauncherUpdateTransaction transaction in invalid)
        {
            WriteLegacy(transaction);
            Throws<InvalidDataException>(
                () => environment.Store.LoadForStartup(original.TransactionPath, environment.TargetPath),
                "La compatibilité historique doit refuser une cible, une version ou un chemin incohérent.");
        }
        WriteLegacy(legacy with { Phase = LauncherUpdateTransactionPhase.StartedAwaitingReady,
            NewProcessId = Environment.ProcessId });
        Equal(1, environment.Store.LoadForStartup(original.TransactionPath, environment.TargetPath).SchemaVersion,
            "Le signal Ready doit rester compatible lorsque l'ancien helper a déjà enregistré le PID.");
    }

    internal static int RunCommandLineSecurity()
    {
        ValidateInternalCommandLineContract();
        Console.WriteLine("Launcher self-update command-line security OK.");
        return 0;
    }

    private static void CharacterizeSingleExecutableReleaseContract()
    {
        string repository = FindRepositoryRoot();
        string project = File.ReadAllText(
            Path.Combine(repository, "source", "WotLK.Launcher", "WotLK.Launcher.csproj"));
        string readme = File.ReadAllText(
            Path.Combine(repository, "source", "README.md"));
        string manifest = File.ReadAllText(
            Path.Combine(repository, "source", "launcher-update.json"));

        True(readme.Contains("-p:PublishSingleFile=true", StringComparison.Ordinal),
            "La release launcher caractérisée doit rester publiée en fichier unique.");
        True(project.Contains("IncludeNativeLibrariesForSelfExtract", StringComparison.Ordinal),
            "Le projet doit conserver son contrat single-file natif.");
        True(manifest.Contains("WotLK-Launcher.exe", StringComparison.Ordinal)
             && !manifest.Contains("files", StringComparison.OrdinalIgnoreCase),
            "Le manifeste d'auto-update doit continuer à cibler un unique EXE.");
    }

    private static void ValidateGameDirectoryElevationBoundary()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier sid = identity.User
            ?? throw new InvalidOperationException("SID Windows de test absent.");
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Exécutable de test absent.");
        True(
            GameDirectoryAccess.ValidateRequester(
                Environment.ProcessId,
                executable,
                sid),
            "Le helper ACL doit lier le PID vivant, son exécutable et son SID.");
        True(
            !GameDirectoryAccess.ValidateRequester(
                Environment.ProcessId,
                executable,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)),
            "Un SID arbitraire ne doit jamais être accepté pour un PID valide.");

        ProcessStartInfo icacls = GameDirectoryAccess.BuildIcaclsStartInfo(
            @"C:\Atlas Test",
            sid);
        string[] arguments = icacls.ArgumentList.ToArray();
        True(arguments.Contains("/L", StringComparer.OrdinalIgnoreCase),
            "icacls doit agir sur le lien lui-même si la racine change en reparse point.");
        True(!arguments.Contains("/T", StringComparer.OrdinalIgnoreCase),
            "La concession ACL ne doit jamais parcourir récursivement les enfants.");

        string root = Path.Combine(
            Path.GetTempPath(),
            "Atlas Game ACL reparse " + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "target");
        string junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        CreateDirectoryJunction(junction, target);
        try
        {
            Throws<InvalidDataException>(
                () => GameDirectoryAccess.ValidateGrantRoot(
                    Path.Combine(junction, "WotLK")),
                "Une jonction dans l'ascendance du client doit être refusée.");
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }

            Directory.Delete(root, recursive: true);
        }

        string controlledParent = NewNonSensitiveTestRoot("acl-controlled-parent");
        string lockedChild = Path.Combine(controlledParent, "locked-child");
        Directory.CreateDirectory(lockedChild);
        try
        {
            RunIcacls(
                lockedChild,
                "/inheritance:r",
                "/grant:r",
                "*S-1-5-18:(OI)(CI)F",
                "*S-1-5-32-544:(OI)(CI)F");
            True(!GameDirectoryAccess.CanWrite(lockedChild),
                "Le scénario synthétique doit avoir un enfant non inscriptible.");
            Throws<UnauthorizedAccessException>(
                () => GameDirectoryAccess.DemandStableGrantRootForCurrentUser(lockedChild),
                "Un enfant verrouillé sous un parent utilisateur remplaçable doit être refusé avant UAC.");
        }
        finally
        {
            try
            {
                RunIcacls(
                    lockedChild,
                    "/inheritance:e",
                    "/grant:r",
                    $"*{sid.Value}:(OI)(CI)F");
            }
            catch
            {
            }

            if (Directory.Exists(controlledParent))
            {
                Directory.Delete(controlledParent, recursive: true);
            }
        }

        string admissibilityRoot = NewNonSensitiveTestRoot("acl-admissibility");
        string arbitraryApplication = Path.Combine(admissibilityRoot, "OtherApplication");
        string emptyWotlk = Path.Combine(admissibilityRoot, "EmptyWotLK");
        string managedWotlk = Path.Combine(admissibilityRoot, "ManagedWotLK");
        Directory.CreateDirectory(arbitraryApplication);
        Directory.CreateDirectory(emptyWotlk);
        Directory.CreateDirectory(managedWotlk);
        File.WriteAllText(Path.Combine(arbitraryApplication, "service.exe"), "foreign");
        File.WriteAllText(
            Path.Combine(managedWotlk, GameInstallServices.ClientMarkerFileName),
            JsonSerializer.Serialize(new
            {
                registeredApp = GameInstallServices.AppDisplayName,
                installRoot = managedWotlk
            }));
        try
        {
            Throws<UnauthorizedAccessException>(
                () => GameDirectoryAccess.DemandAdmissibleElevatedGrantTarget(
                    arbitraryApplication),
                "Un dossier protégé non vide d'une autre application ne doit jamais recevoir Modify.");
            Throws<UnauthorizedAccessException>(
                () => GameDirectoryAccess.DemandAdmissibleElevatedGrantTarget(emptyWotlk),
                "Un dossier protégé vide arbitraire ne doit jamais recevoir Modify.");
            GameDirectoryAccess.DemandAdmissibleElevatedGrantTarget(managedWotlk);
            Throws<UnauthorizedAccessException>(
                () => GameDirectoryAccess.DemandAdmissibleElevatedGrantTarget(
                    Path.Combine(admissibilityRoot, "UnmanagedMissingDirectory")),
                "Un chemin protégé inexistant arbitraire doit être refusé; seul le chemin WotLK par défaut est admissible.");
        }
        finally
        {
            Directory.Delete(admissibilityRoot, recursive: true);
        }
    }

    private static void ValidateRequesterImpersonationBoundary()
    {
        if (!OperatingSystem.IsWindows()
            || LauncherUpdateSecurity.IsCurrentProcessElevated())
        {
            return;
        }

        using LauncherUpdateRequesterImpersonation requester =
            LauncherUpdateRequesterImpersonation.Capture(Environment.ProcessId);
        Equal(
            Path.GetFullPath(LauncherUpdatePaths.TransactionsRoot),
            Path.GetFullPath(requester.TransactionsRoot),
            "La racine LocalAppData doit être dérivée du jeton du demandeur, y compris en UAC OTS.");
        requester.DemandMatchesRequester(
            Environment.ProcessId,
            Environment.ProcessPath
            ?? throw new InvalidOperationException("Exécutable de test absent."));
        Throws<UnauthorizedAccessException>(
            () => requester.DemandMatchesRequester(
                Environment.ProcessId,
                Path.Combine(Path.GetTempPath(), "recycled-requester.exe")),
            "Le jeton capturé doit rester lié au même objet processus et au même exécutable.");
    }

    private static void ValidateUpdaterProtectedPathBoundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using AtomicUpdateEnvironment environment = new();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier sid = identity.User
            ?? throw new InvalidOperationException("SID Windows de test absent.");
        string install = Path.GetDirectoryName(environment.TargetPath)
            ?? throw new InvalidOperationException("Dossier cible de test absent.");
        try
        {
            RunIcacls(
                install,
                "/inheritance:r",
                "/grant:r",
                $"*{sid.Value}:(OI)(CI)RX",
                "*S-1-5-18:(OI)(CI)F",
                "*S-1-5-32-544:(OI)(CI)F",
                "/T",
                "/C");
            True(!GameDirectoryAccess.CanWrite(install),
                "Le scénario updater doit avoir une cible directement non inscriptible.");
            Throws<UnauthorizedAccessException>(
                () => LauncherUpdateElevationSecurity.DemandProtectedTargetForElevation(
                    environment.Transaction),
                "Une cible dont le propriétaire ou un ancêtre peut réouvrir les droits doit être refusée avant UAC.");
        }
        finally
        {
            try
            {
                RunIcacls(
                    install,
                    "/inheritance:e",
                    "/grant:r",
                    $"*{sid.Value}:(OI)(CI)F",
                    "/T",
                    "/C");
            }
            catch
            {
            }
        }

        Throws<InvalidDataException>(
            () => LauncherUpdateElevationSecurity.DemandProtectedSwapFileForElevation(
                environment.Transaction,
                environment.Transaction.HelperPath),
            "Un fichier autre que staged/backup ne doit jamais entrer dans le validateur de swap.");
    }

    private static async Task ValidateActiveExecutableSharingBoundaryAsync()
    {
        if (!OperatingSystem.IsWindows() || LauncherUpdateSecurity.IsCurrentProcessElevated())
        {
            return;
        }

        using AtomicUpdateEnvironment environment = new();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier sid = identity.User
            ?? throw new InvalidOperationException("SID Windows de test absent.");
        string install = Path.GetDirectoryName(environment.TargetPath)
            ?? throw new InvalidOperationException("Dossier cible de test absent.");
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        File.Copy(commandInterpreter, environment.TargetPath, overwrite: true);
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = environment.TargetPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "ping -n 30 127.0.0.1 >nul" }
        }) ?? throw new InvalidOperationException("L'image EXE de test n'a pas démarré.");

        try
        {
            RunIcacls(
                install,
                "/inheritance:r",
                "/grant:r",
                $"*{sid.Value}:(OI)(CI)RX",
                "*S-1-5-18:(OI)(CI)F",
                "*S-1-5-32-544:(OI)(CI)F",
                "/T",
                "/C");
            RunIcacls(
                environment.TargetPath,
                "/grant:r",
                $"*{sid.Value}:M",
                "*S-1-5-18:F",
                "*S-1-5-32-544:F",
                "/C");

            UnauthorizedAccessException rejected;
            try
            {
                LauncherUpdateElevationSecurity.DemandProtectedTargetForElevation(
                    environment.Transaction);
                throw new InvalidOperationException(
                    "La DACL modifiable de la cible active aurait dû être refusée.");
            }
            catch (UnauthorizedAccessException exception)
            {
                rejected = exception;
            }

            True(
                rejected.Message.Contains("propriétaire ou les droits", StringComparison.Ordinal),
                "Une image EXE verrouillée doit atteindre la décision DACL; une sharing violation ne doit pas être classée comme droit d'écriture.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            try
            {
                RunIcacls(
                    install,
                    "/inheritance:e",
                    "/grant:r",
                    $"*{sid.Value}:(OI)(CI)F",
                    "/T",
                    "/C");
            }
            catch
            {
            }
        }
    }

    private static void ValidateInternalCommandLineContract()
    {
        True(LauncherUpdateCommandLine.TryParseHelper(
                [LauncherUpdateCommandLine.ApplySwitch, @"C:\temp\transaction.json", "42"],
                out bool recovery,
                out string path,
                out int requesterProcessId)
             && !recovery
             && requesterProcessId == 42
             && path.EndsWith("transaction.json", StringComparison.Ordinal),
            "Le mode helper Apply doit être explicite et strict.");
        True(LauncherUpdateCommandLine.TryParseHelper(
                [LauncherUpdateCommandLine.RecoverSwitch, @"C:\temp\transaction.json", "43"],
                out recovery,
                out _,
                out requesterProcessId)
             && recovery
             && requesterProcessId == 43,
            "Le mode helper Recovery doit être distinct.");
        True(LauncherUpdateCommandLine.TryParseBootstrap(
                [LauncherUpdateCommandLine.BootstrapSwitch, @"C:\temp\transaction.json", "44"],
                out _,
                out requesterProcessId)
             && requesterProcessId == 44,
            "Le bootstrap élevé doit avoir un mode explicite et strict.");
        True(!LauncherUpdateCommandLine.TryParseHelper(
                [LauncherUpdateCommandLine.ApplySwitch, "one", "extra"],
                out _,
                out _,
                out _),
            "Des arguments helper supplémentaires doivent être refusés.");
        True(!LauncherUpdateCommandLine.TryParseHelper(
                [LauncherUpdateCommandLine.ApplySwitch, @"C:\temp\transaction.json", "0"],
                out _,
                out _,
                out _),
            "Un PID demandeur invalide doit être refusé.");

        Guid id = Guid.NewGuid();
        string postUpdate = LauncherUpdateCommandLine.BuildPostUpdateArgument(id);
        Equal(id, LauncherUpdateCommandLine.FindPostUpdateTransaction([postUpdate]),
            "Le handshake doit transporter uniquement l'identifiant de transaction.");
        True(LauncherUpdateCommandLine.HasPostUpdateMarker([postUpdate]),
            "Un handshake post-update valide doit activer le gate de migration.");
        string malformedPostUpdate = postUpdate[..^32] + "not-a-guid";
        True(LauncherUpdateCommandLine.HasPostUpdateMarker([malformedPostUpdate])
             && LauncherUpdateCommandLine.FindPostUpdateTransaction(
                 [malformedPostUpdate]) is null,
            "Le préfixe post-update réservé doit activer le gate même si son identifiant est invalide.");
        SequenceEqual(
            ["--ui-v2"],
            LauncherUpdateCommandLine.ApplicationArguments(
                ["--ui-v2", postUpdate, malformedPostUpdate]),
            "L'argument interne ne doit pas modifier la résolution du mode UI.");
        Equal(
            "\"C:\\Program Files (x86)\\Atlas Launcher\\AtlasLauncher.exe\" " + postUpdate,
            WindowsUnelevatedProcessLauncher.BuildCommandLine(
                @"C:\Program Files (x86)\Atlas Launcher\AtlasLauncher.exe",
                postUpdate),
            "La relance non élevée doit préserver le chemin avec espaces et le handshake.");
    }

    private static void ValidateHelperRequesterBoundary()
    {
        using AtomicUpdateEnvironment environment = new();
        LauncherUpdateHelperRunner.ValidateRequester(
            recovery: false,
            environment.Transaction,
            environment.Transaction.ParentProcessId,
            (processId, path) => processId == environment.Transaction.ParentProcessId
                                 && string.Equals(
                                     path,
                                     environment.TargetPath,
                                     StringComparison.OrdinalIgnoreCase));

        Throws<InvalidDataException>(
            () => LauncherUpdateHelperRunner.ValidateRequester(
                recovery: false,
                environment.Transaction,
                environment.Transaction.ParentProcessId + 1,
                (_, _) => true),
            "Le helper Apply doit refuser un autre processus demandeur.");
        Throws<InvalidDataException>(
            () => LauncherUpdateHelperRunner.ValidateRequester(
                recovery: true,
                environment.Transaction,
                environment.Transaction.ParentProcessId,
                (_, _) => false),
            "Le helper doit refuser une cible arbitraire ne correspondant pas au demandeur vivant.");
    }

    private static void ValidateHelperHashDoesNotCaptureWpfContext()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "AtlasLauncherHashContextTest",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "updater.exe");
        Directory.CreateDirectory(root);
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(32 * 1024 * 1024);
        }

        try
        {
            using ManualResetEventSlim completed = new();
            Exception? failure = null;
            Thread thread = new(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new NonPumpingSynchronizationContext());
                try
                {
                    _ = LauncherUpdateTransactionStore.ComputeSha256Async(
                            path,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    completed.Set();
                }
            })
            {
                IsBackground = true,
                Name = "Atlas launcher updater hash context test"
            };
            thread.Start();

            True(
                completed.Wait(TimeSpan.FromSeconds(10)),
                "Le hash du helper ne doit pas capturer le contexte WPF lors d'un appel synchrone.");
            if (failure is not null)
            {
                throw new InvalidOperationException(
                    "Le hash du helper a échoué sous un contexte WPF bloqué.",
                    failure);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RejectElevatedUpdatedProcessAndReleaseTargetAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        int stoppedProcessId = 0;
        string? stoppedPath = null;
        WindowsLauncherUpdateApplicationLauncher launcher = new(
            environment.Store,
            launchProcess: (_, _, _) => environment.Store.WriteStartedSignal(
                environment.Transaction,
                new LauncherUpdateProcessSignal(
                    environment.Transaction.TransactionId,
                    Environment.ProcessId,
                    IsElevated: true,
                    DateTimeOffset.UtcNow)),
            processMatchesPath: (processId, path) =>
                processId == Environment.ProcessId
                && string.Equals(path, environment.TargetPath, StringComparison.OrdinalIgnoreCase),
            stopProcess: (processId, path) =>
            {
                stoppedProcessId = processId ?? 0;
                stoppedPath = path;
            });

        await ThrowsAsync<InvalidOperationException>(
            () => launcher.LaunchUpdatedAsync(
                environment.Transaction,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None));
        Equal(Environment.ProcessId, stoppedProcessId,
            "Le candidat élevé doit être arrêté avant le rollback.");
        Equal(environment.TargetPath, stoppedPath,
            "Seule la cible validée peut être arrêtée avant le rollback.");

        WindowsLauncherUpdateApplicationLauncher staleLauncher = new(
            environment.Store,
            launchProcess: (_, _, _) => environment.Store.WriteStartedSignal(
                environment.Transaction,
                new LauncherUpdateProcessSignal(
                    environment.Transaction.TransactionId,
                    Environment.ProcessId,
                    IsElevated: false,
                    DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5))),
            processMatchesPath: (processId, path) =>
                processId == Environment.ProcessId
                && string.Equals(path, environment.TargetPath, StringComparison.OrdinalIgnoreCase));
        await ThrowsAsync<InvalidDataException>(
            () => staleLauncher.LaunchUpdatedAsync(
                environment.Transaction,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None));
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }
    }

    private static async Task PrepareTransactionWithoutTouchingActiveReleaseAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        RecordingHelperLauncher helper = new();
        LauncherSelfUpdateFinalizer finalizer = new(
            environment.TransactionsRoot,
            environment.Store,
            helper);
        string downloaded = Path.Combine(environment.Root, "downloaded.exe");
        await File.WriteAllBytesAsync(downloaded, environment.NewBytes);

        LauncherUpdateTransaction transaction = await finalizer.PrepareAndLaunchAsync(
            environment.TargetPath,
            downloaded,
            environment.NewBytes.Length,
            Hash(environment.NewBytes),
            "1.2.0",
            CreateTestManifest("1.2.0", environment.NewBytes),
            Environment.ProcessId,
            CancellationToken.None);

        Equal(1, helper.ApplyCalls, "La préparation doit démarrer un unique helper élevé.");
        Equal(transaction.TransactionId, helper.LastTransaction!.TransactionId,
            "Le helper doit recevoir la transaction préparée.");
        BytesEqual(environment.OldBytes, await File.ReadAllBytesAsync(environment.TargetPath),
            "La préparation ne doit jamais modifier la release active.");
        BytesEqual(environment.NewBytes, await File.ReadAllBytesAsync(transaction.CandidatePath),
            "Le candidat durable doit être complet avant le helper.");
        True(!File.Exists(transaction.HelperPath),
            "Le launcher non élevé ne doit jamais déposer le helper avant le bootstrap protégé.");
        True(!File.Exists(downloaded),
            "Le téléchargement initial doit être nettoyé après sa copie durable validée.");
        True(File.Exists(transaction.TransactionPath),
            "Le marqueur transactionnel doit précéder l'élévation.");
        Equal("1.2.0", transaction.AuthenticatedTargetVersion,
            "La transaction doit conserver la version du manifeste signé.");
    }

    private static async Task RejectInvalidCandidateBeforeTouchingReleaseAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        environment.CorruptCandidate();
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Un candidat corrompu doit être refusé avant le swap.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Aucun nouveau processus ne doit être créé après validation invalide.");
        Equal(0, environment.VersionSynchronizer.Calls,
            "Une validation échouée ne doit jamais modifier DisplayVersion.");
        Equal("1.1.2", environment.VersionSynchronizer.DisplayVersion,
            "Une mise à jour échouée doit conserver l'ancienne version Windows.");
    }

    private static async Task RejectCandidateSwapAfterInitialValidationAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultInjector: new MutatingFaultInjector(
                LauncherUpdateFaultPoint.AfterCandidateValidation,
                transaction => File.WriteAllBytes(
                    transaction.CandidatePath,
                    CreatePayload("attacker-candidate"))));
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Un candidat modifié entre le premier hash et le staging doit être refusé.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Le candidat remplacé dans LocalAppData ne doit jamais être lancé.");
    }

    private static async Task RejectStagedSwapBeforeAtomicMoveAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultInjector: new MutatingFaultInjector(
                LauncherUpdateFaultPoint.AfterBackupCreated,
                transaction => File.WriteAllBytes(
                    transaction.StagedPath,
                    CreatePayload("attacker-staged"))));
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Le fichier staged doit être rehaché immédiatement avant le swap atomique.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Un staged altéré ne doit jamais remplacer le launcher.");
    }

    private static async Task RejectStagedSwapAfterFinalValidationAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultInjector: new MutatingFaultInjector(
                LauncherUpdateFaultPoint.AfterStagedValidatedBeforeAtomicSwap,
                transaction => File.WriteAllBytes(
                    transaction.StagedPath,
                    CreatePayload("attacker-after-final-hash"))));
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Le staged modifié dans la dernière fenêtre post-hash doit être rehaché dans la tentative MoveFileEx.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Une mutation post-hash ne doit jamais atteindre la cible finale.");
    }

    private static async Task RefuseSwapWhenProtectedAclValidatorRejectsAsync()
    {
        int stagedChecks = 0;
        using AtomicUpdateEnvironment environment = new(
            protectedFileValidator: (transaction, path) =>
            {
                if (string.Equals(
                        path,
                        transaction.StagedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    stagedChecks++;
                    throw new UnauthorizedAccessException("simulated inherited writable ACL");
                }
            });
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Un staged dont l'ACL héritée reste modifiable doit être refusé avant MoveFileEx.");
        True(stagedChecks > 0,
            "Le validateur ACL requester doit examiner chaque staged créé par le helper élevé.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RejectInvalidAuthenticatedVersionBeforeTransactionAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        RecordingHelperLauncher helper = new();
        LauncherSelfUpdateFinalizer finalizer = new(
            environment.TransactionsRoot,
            environment.Store,
            helper);
        string downloaded = Path.Combine(environment.Root, "invalid-version.exe");
        await File.WriteAllBytesAsync(downloaded, environment.NewBytes);

        await ThrowsAsync<InvalidDataException>(() => finalizer.PrepareAndLaunchAsync(
            environment.TargetPath,
            downloaded,
            environment.NewBytes.Length,
            Hash(environment.NewBytes),
            "1.2.0-preview",
            CreateTestManifest("1.2.0-preview", environment.NewBytes),
            Environment.ProcessId,
            CancellationToken.None));

        Equal(0, helper.ApplyCalls,
            "Une version de manifeste invalide ne doit jamais lancer le helper élevé.");
        await environment.AssertTargetIsOldAsync();
        Equal("1.1.2", environment.VersionSynchronizer.DisplayVersion,
            "Une version invalide ne doit provoquer aucune écriture Windows.");
    }

    private static async Task KeepPreviousReleaseAcrossPreSwapCrashPointsAsync()
    {
        LauncherUpdateFaultPoint[] points =
        [
            LauncherUpdateFaultPoint.BeforeCandidateValidation,
            LauncherUpdateFaultPoint.AfterCandidateValidation,
            LauncherUpdateFaultPoint.AfterCandidateStaged,
            LauncherUpdateFaultPoint.AfterBackupCreated,
            LauncherUpdateFaultPoint.AfterStagedValidatedBeforeAtomicSwap
        ];

        foreach (LauncherUpdateFaultPoint point in points)
        {
            using AtomicUpdateEnvironment environment = new(faultPoint: point);
            try
            {
                await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
                    () => environment.Service.ApplyAsync(environment.Transaction));
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Le point de crash synthétique {point} n'a pas été observé.",
                    exception);
            }
            await environment.AssertTargetIsOldAsync();

            LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
                environment.Store.Load(environment.Transaction.TransactionPath));
            Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, recovered.Outcome,
                $"La récupération de {point} doit conserver l'ancienne version.");
            await environment.AssertTargetIsOldAsync();
        }
    }

    private static async Task RecoverCrashAfterAtomicSwapAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.AfterAtomicSwap);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));
        await environment.AssertTargetIsNewAsync();
        True(File.Exists(environment.Transaction.BackupPath),
            "Le backup doit survivre à un crash juste après le swap.");

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            environment.Store.Load(environment.Transaction.TransactionPath));
        Equal(LauncherUpdateExecutionOutcome.RolledBack, recovered.Outcome,
            "Une transaction swapée sans Ready doit être rollbackée.");
        await environment.AssertTargetIsOldAsync();
        Equal(1, environment.RecoveryLauncher.RollbackLaunchCalls,
            "L'ancienne version doit être relancée après récupération.");
    }

    private static async Task RefuseStaleRecoveryOverUnknownNewerTargetAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        byte[] newerRelease = CreatePayload("newer-release-installed-later");
        await File.WriteAllBytesAsync(
            environment.Transaction.BackupPath,
            environment.OldBytes);
        await File.WriteAllBytesAsync(environment.TargetPath, newerRelease);
        LauncherUpdateTransaction stale = environment.Transaction with
        {
            Phase = LauncherUpdateTransactionPhase.BackupReady,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        environment.Store.Save(stale);

        LauncherUpdateExecutionResult result = await environment.RecoveryService.RecoverAsync(
            stale);
        Equal(LauncherUpdateExecutionOutcome.RecoveryRequired, result.Outcome,
            "Une récupération ancienne doit rester fail-close face à une cible plus récente inconnue.");
        Equal("UnexpectedTarget", result.FailureCategory,
            "La récupération doit distinguer une cible étrangère d'une cible absente.");
        Equal(
            Hash(newerRelease),
            await LauncherUpdateTransactionStore.ComputeSha256Async(
                environment.TargetPath,
                CancellationToken.None),
            "Une transaction 1.1→1.2 obsolète ne doit jamais écraser une cible 1.3 installée ensuite.");
    }

    private static async Task RetryTransientAtomicSwapFailureAsync()
    {
        FailingThenAtomicMover mover = new(failuresBeforeSuccess: 2);
        using AtomicUpdateEnvironment environment = new(
            retryPolicy: FastRetryPolicy(fileAttempts: 4),
            atomicMover: mover);

        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.Succeeded, result.Outcome,
            "Un verrou transitoire au swap doit être réessayé.");
        Equal(3, mover.Attempts,
            "Le swap doit réussir immédiatement après les deux échecs transitoires.");
        await environment.AssertTargetIsNewAsync();
    }

    private static async Task AbandonPermanentAtomicSwapFailureAsync()
    {
        AlwaysFailingAtomicMover mover = new();
        using AtomicUpdateEnvironment environment = new(
            retryPolicy: FastRetryPolicy(fileAttempts: 3),
            atomicMover: mover);

        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Un swap durablement refusé doit abandonner sans toucher à l'ancienne release.");
        Equal(3, mover.Attempts, "Les retries du swap doivent rester bornés.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Aucun nouveau launcher ne doit être lancé après un swap refusé.");
    }

    private static async Task RecoverCrashBeforeNewLauncherStartAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.BeforeNewLauncherStart);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            environment.Store.Load(environment.Transaction.TransactionPath));
        Equal(LauncherUpdateExecutionOutcome.RolledBack, recovered.Outcome,
            "Un crash avant le lancement doit restaurer l'ancienne release.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RecoverCrashAfterNewLauncherStartAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.AfterNewLauncherStart,
            launchBehavior: FakeLaunchBehavior.NoReady);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            environment.Store.Load(environment.Transaction.TransactionPath));
        Equal(LauncherUpdateExecutionOutcome.RolledBack, recovered.Outcome,
            "Un launcher démarré sans Ready doit être arrêté puis rollbacké après reprise.");
        True(environment.Launcher.LastProcess is { KillCalls: 1 },
            "La récupération doit arrêter le nouveau processus non confirmé.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RecoverCrashAfterReadyConfirmationAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.AfterReadyConfirmation,
            launchBehavior: FakeLaunchBehavior.Ready);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            environment.Store.Load(environment.Transaction.TransactionPath));
        Equal(LauncherUpdateExecutionOutcome.Succeeded, recovered.Outcome,
            "Un Ready encore rattaché au bon processus doit permettre de terminer le commit.");
        Equal(1, environment.VersionSynchronizer.Calls,
            "La reprise ne doit synchroniser DisplayVersion qu'après le Ready retrouvé.");
        Equal("1.2.0", environment.VersionSynchronizer.DisplayVersion,
            "La reprise confirmée doit publier la version authentifiée.");
        await environment.AssertTargetIsNewAsync();
        True(!File.Exists(environment.Transaction.BackupPath),
            "Le backup peut être supprimé après récupération d'un Ready valide.");
    }

    private static async Task RecoverCrashAfterCommitPersistedAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.AfterCommitPersisted,
            launchBehavior: FakeLaunchBehavior.Ready,
            recoveryProcessIsAlive: false);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));
        LauncherUpdateTransaction persisted = environment.Store.Load(
            environment.Transaction.TransactionPath);
        Equal(LauncherUpdateTransactionPhase.Committed, persisted.Phase,
            "Le commit doit être durable avant la suppression du backup.");
        Equal("1.1.2", environment.VersionSynchronizer.DisplayVersion,
            "Un crash juste après le commit durable doit précéder l'écriture registre.");

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            persisted);
        Equal(LauncherUpdateExecutionOutcome.Succeeded, recovered.Outcome,
            "Un commit durable doit rester valide même si son processus a disparu.");
        await environment.AssertTargetIsNewAsync();
        Equal("1.2.0", environment.VersionSynchronizer.DisplayVersion,
            "La reprise d'un commit durable doit terminer la synchronisation Windows.");
        True(!File.Exists(environment.Transaction.BackupPath)
             && !File.Exists(environment.Transaction.TransactionPath),
            "La reprise doit terminer le nettoyage d'un commit interrompu.");
    }

    private static async Task RejectForgedCommittedPhaseWithoutProtectedProofAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        await File.WriteAllBytesAsync(
            environment.Transaction.BackupPath,
            environment.OldBytes);
        await File.WriteAllBytesAsync(
            environment.TargetPath,
            environment.NewBytes);
        LauncherUpdateTransaction forged = environment.Transaction with
        {
            Phase = LauncherUpdateTransactionPhase.Committed,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        environment.Store.Save(forged);

        LauncherUpdateExecutionResult recovered =
            await environment.RecoveryService.RecoverAsync(forged);
        Equal(LauncherUpdateExecutionOutcome.RolledBack, recovered.Outcome,
            "Un Phase=Committed modifiable dans LocalAppData ne doit pas remplacer la preuve protégée.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RollBackWhenNewLauncherCannotStartAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            launchBehavior: FakeLaunchBehavior.Throw);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
            "Un Process.Start impossible doit provoquer un rollback.");
        await environment.AssertTargetIsOldAsync();
        Equal(1, environment.Launcher.RollbackLaunchCalls,
            "L'ancienne release doit être relancée après l'échec.");
    }

    private static async Task RollBackWhenNewLauncherExitsImmediatelyAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            launchBehavior: FakeLaunchBehavior.ImmediateExit);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
            "Un crash immédiat avant Ready doit provoquer un rollback.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RejectReadyFromExitedLauncherAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            launchBehavior: FakeLaunchBehavior.ReadyThenExit);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
            "Un marqueur Ready ne doit pas valider un processus déjà terminé.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RollBackWhenReadyNeverArrivesAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            launchBehavior: FakeLaunchBehavior.NoReady);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
            "L'absence de Ready doit provoquer un rollback après timeout.");
        True(environment.Launcher.LastProcess?.KillCalls == 1,
            "Le processus non confirmé doit être arrêté avant rollback.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.VersionSynchronizer.Calls,
            "Un rollback ne doit jamais modifier DisplayVersion.");
        Equal("1.1.2", environment.VersionSynchronizer.DisplayVersion,
            "Un rollback doit conserver l'ancienne version Windows.");
    }

    private static async Task IgnoreWrongAndStaleReadySignalsAsync()
    {
        foreach (FakeLaunchBehavior behavior in new[]
                 {
                     FakeLaunchBehavior.WrongTransactionReady,
                     FakeLaunchBehavior.WrongProcessReady
                 })
        {
            using AtomicUpdateEnvironment environment = new(launchBehavior: behavior);
            LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
                environment.Transaction);
            Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
                $"Le signal {behavior} ne doit jamais confirmer la transaction.");
            await environment.AssertTargetIsOldAsync();
        }
    }

    private static async Task AcceptImmediateAndDelayedReadySignalsAsync()
    {
        foreach (FakeLaunchBehavior behavior in new[]
                 {
                     FakeLaunchBehavior.Ready,
                     FakeLaunchBehavior.DelayedReady
                 })
        {
            using AtomicUpdateEnvironment environment = new(launchBehavior: behavior);
            LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
                environment.Transaction);
            Equal(LauncherUpdateExecutionOutcome.Succeeded, result.Outcome,
                $"Le signal {behavior} doit confirmer la transaction.");
            await environment.AssertTargetIsNewAsync();
            True(!File.Exists(environment.Transaction.BackupPath),
                "Le backup ne doit être supprimé qu'après Ready valide.");
            True(!File.Exists(environment.Transaction.TransactionPath),
                "Le marqueur doit être retiré après commit.");
            Equal(0, environment.Launcher.RollbackLaunchCalls,
                "Une mise à jour confirmée ne doit pas relancer l'ancienne version.");
            Equal(1, environment.VersionSynchronizer.Calls,
                "DisplayVersion doit être synchronisé une fois après Ready.");
            Equal("1.2.0", environment.VersionSynchronizer.LastVersion,
                "DisplayVersion doit provenir du manifeste signé.");
            Equal("1.2.0", environment.VersionSynchronizer.DisplayVersion,
                "Une mise à jour confirmée doit publier la nouvelle version Windows.");
        }
    }

    private static async Task RetryTemporaryWindowsLockAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using WindowsLockingAtomicMover mover = new(TimeSpan.FromMilliseconds(80));
        using AtomicUpdateEnvironment environment = new(
            retryPolicy: FastRetryPolicy(fileAttempts: 30),
            atomicMover: mover);

        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);
        await mover.WaitForAutomaticReleaseAsync();
        Equal(LauncherUpdateExecutionOutcome.Succeeded, result.Outcome,
            "Un verrou Windows transitoire pendant le swap doit être absorbé par les retries.");
        True(mover.Attempts > 1,
            "Le test Windows doit observer au moins un échec réel de MoveFileEx.");
        await environment.AssertTargetIsNewAsync();
    }

    private static async Task KeepPortableUpdateSuccessfulWithoutRegistrationAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            registrationStatus: LauncherInstalledAppVersionSyncStatus.EntryMissing);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.Succeeded, result.Outcome,
            "Une entrée Applications installées absente ne doit pas faire échouer l'update.");
        Equal(1, environment.VersionSynchronizer.Calls,
            "Le helper doit constater l'absence uniquement après Ready.");
        Equal("1.1.2", environment.VersionSynchronizer.DisplayVersion,
            "Une installation portable ne doit créer aucune version Windows.");
        await environment.AssertTargetIsNewAsync();
    }

    private static async Task AbandonPermanentWindowsLockAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using WindowsLockingAtomicMover mover = new(releaseAfter: null);
        using AtomicUpdateEnvironment environment = new(
            retryPolicy: FastRetryPolicy(fileAttempts: 3),
            atomicMover: mover);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Un verrou permanent pendant le swap doit abandonner après des retries bornés.");
        Equal(3, mover.Attempts, "Le verrou permanent ne doit provoquer aucune boucle infinie.");
        mover.Release();
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task KeepTargetWholeDuringAtomicSwapAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "AtlasAtomicObservation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "AtlasLauncher.exe");
        string candidate = Path.Combine(root, "AtlasLauncher.exe.new");
        byte[] oldBytes = CreatePayload("old", 2 * 1024 * 1024);
        byte[] newBytes = CreatePayload("new", 2 * 1024 * 1024);
        await File.WriteAllBytesAsync(target, oldBytes);
        await File.WriteAllBytesAsync(candidate, newBytes);
        string oldHash = Hash(oldBytes);
        string newHash = Hash(newBytes);
        ConcurrentBag<string> observedHashes = [];
        using CancellationTokenSource stop = new();

        Task observer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using FileStream stream = new(
                        target,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    observedHashes.Add(Convert.ToHexString(
                        await SHA256.HashDataAsync(stream)).ToLowerInvariant());
                }
                catch (IOException)
                {
                }

                await Task.Delay(5);
            }
        });

        try
        {
            await Task.Delay(20);
            WindowsLauncherAtomicFileMover mover = new();
            bool swapped = false;
            for (int attempt = 0; attempt < 100 && !swapped; attempt++)
            {
                try
                {
                    mover.Replace(candidate, target);
                    swapped = true;
                }
                catch (IOException) when (attempt < 99)
                {
                    await Task.Delay(2);
                }
            }

            True(swapped, "Le swap atomique doit finir par réussir malgré les lectures concurrentes.");
            await Task.Delay(20);
            stop.Cancel();
            await observer;
            string finalHash = await LauncherUpdateTransactionStore.ComputeSha256Async(
                target,
                CancellationToken.None);
            observedHashes.Add(finalHash);
            True(observedHashes.Count > 0
                 && observedHashes.All(hash => hash == oldHash || hash == newHash),
                "Un observateur ne doit voir que l'ancien ou le nouveau fichier complet.");
            Equal(newHash, finalHash,
                "La destination finale doit être le nouveau fichier complet.");
        }
        finally
        {
            stop.Cancel();
            await IgnoreFailureAsync(observer);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RefuseUnsafeTransactionPathsAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        LauncherUpdateTransaction escaped = environment.Transaction with
        {
            CandidatePath = Path.Combine(environment.Root, "outside.exe")
        };
        await File.WriteAllBytesAsync(escaped.CandidatePath, environment.NewBytes);

        Throws<InvalidDataException>(
            () => environment.Store.Save(escaped),
            "Un chemin candidat hors du workspace doit être refusé.");

        LauncherUpdateTransaction networkTarget = environment.Transaction with
        {
            TargetPath = @"\\server\share\AtlasLauncher.exe"
        };
        Throws<InvalidDataException>(
            () => environment.Store.Save(networkTarget),
            "Une destination réseau doit être refusée.");

        LauncherUpdateTransaction externalWorkspace = environment.Transaction with
        {
            WorkspacePath = Path.Combine(environment.Root, Guid.NewGuid().ToString("N"))
        };
        Throws<InvalidDataException>(
            () => environment.Store.Save(externalWorkspace),
            "Le marqueur doit rester sous la racine interne des transactions.");

        LauncherUpdateTransaction userWritableHelper = environment.Transaction with
        {
            HelperPath = Path.Combine(environment.Transaction.WorkspacePath, "updater.exe")
        };
        Throws<InvalidDataException>(
            () => environment.Store.Save(userWritableHelper),
            "Un helper situé dans le workspace LocalAppData doit être refusé.");

        LauncherUpdateManifest changedManifest = CreateTestManifest(
            "1.2.1",
            environment.NewBytes);
        LauncherUpdateTransaction mismatchedProof = environment.Transaction with
        {
            AuthenticatedManifest = changedManifest
        };
        Throws<InvalidDataException>(
            () => environment.Store.Save(mismatchedProof),
            "La preuve signée doit rester liée à la version, la taille et l'empreinte transactionnelles.");
    }

    private static void RejectUserWorkspaceReparseSwapAtWrite()
    {
        using AtomicUpdateEnvironment environment = new();
        string workspace = environment.Transaction.WorkspacePath;
        string parkedWorkspace = workspace + ".parked";
        string victim = Path.Combine(
            Path.GetTempPath(),
            "AtlasUpdaterProtectedWriteVictim",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(victim);

        void SwapWorkspace()
        {
            Directory.Move(workspace, parkedWorkspace);
            CreateDirectoryJunction(workspace, victim);
        }

        try
        {
            SwapAndDenyUserOperationRunner runner = new(SwapWorkspace);
            LauncherUpdateTransactionStore store = new(
                environment.TransactionsRoot,
                runner);
            Throws<UnauthorizedAccessException>(
                () => store.Save(environment.Transaction),
                "Une écriture transaction doit rester enfermée dans le jeton utilisateur après un swap de jonction.");
            True(runner.RunCalls == 1,
                "Save doit obligatoirement traverser la frontière d'impersonation utilisateur.");
            True(!File.Exists(Path.Combine(victim, "transaction.json")),
                "Le helper élevé ne doit jamais écrire transaction.json dans la cible d'une jonction.");

            Directory.Delete(workspace);
            Directory.Move(parkedWorkspace, workspace);
            runner = new SwapAndDenyUserOperationRunner(SwapWorkspace);
            store = new LauncherUpdateTransactionStore(environment.TransactionsRoot, runner);
            Throws<UnauthorizedAccessException>(
                () => store.AppendJournal(environment.Transaction, "reparse-race"),
                "Le journal doit rester enfermé dans le jeton utilisateur après un swap de jonction.");
            True(runner.RunCalls == 1,
                "Le journal doit obligatoirement traverser la frontière d'impersonation utilisateur.");
            True(!File.Exists(Path.Combine(victim, "updater.log")),
                "Le helper élevé ne doit jamais écrire updater.log dans la cible d'une jonction.");
        }
        finally
        {
            if (Directory.Exists(workspace)
                && (File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(workspace);
            }

            if (Directory.Exists(parkedWorkspace) && !Directory.Exists(workspace))
            {
                Directory.Move(parkedWorkspace, workspace);
            }

            if (Directory.Exists(victim))
            {
                Directory.Delete(victim, recursive: true);
            }
        }
    }

    private static void RejectAmbiguousAndOversizedTransactionJson()
    {
        using (AtomicUpdateEnvironment environment = new())
        {
            string json = File.ReadAllText(environment.Transaction.TransactionPath);
            string duplicate = json.Replace(
                "\"SchemaVersion\": 2,",
                "\"SchemaVersion\": 2,\n  \"SchemaVersion\": 2,",
                StringComparison.Ordinal);
            True(!string.Equals(json, duplicate, StringComparison.Ordinal),
                "Le fixture doit pouvoir injecter une propriété JSON dupliquée.");
            File.WriteAllText(environment.Transaction.TransactionPath, duplicate);
            Throws<InvalidDataException>(
                () => environment.Store.Load(environment.Transaction.TransactionPath),
                "Une propriété transactionnelle dupliquée doit être refusée avant toute élévation.");
        }

        using (AtomicUpdateEnvironment environment = new())
        {
            string json = File.ReadAllText(environment.Transaction.TransactionPath);
            string unknown = json.Replace(
                "\"SchemaVersion\": 2,",
                "\"SchemaVersion\": 2,\n  \"UnexpectedPrivilegedPath\": \"C:\\\\Windows\",",
                StringComparison.Ordinal);
            File.WriteAllText(environment.Transaction.TransactionPath, unknown);
            Throws<JsonException>(
                () => environment.Store.Load(environment.Transaction.TransactionPath),
                "Une propriété transactionnelle inconnue doit être refusée.");
        }

        using (AtomicUpdateEnvironment environment = new())
        {
            File.WriteAllBytes(
                environment.Transaction.TransactionPath,
                new byte[64 * 1024 + 1]);
            Throws<InvalidDataException>(
                () => environment.Store.Load(environment.Transaction.TransactionPath),
                "La lecture du JSON transactionnel doit être bornée avant désérialisation.");
        }
    }

    private static void RejectAmbiguousAndOversizedProcessSignals()
    {
        using (AtomicUpdateEnvironment environment = new())
        {
            File.WriteAllBytes(
                environment.Transaction.ReadySignalPath,
                new byte[8 * 1024 + 1]);
            True(environment.Store.TryReadReadySignal(environment.Transaction) is null,
                "Un ready.json surdimensionné doit être rejeté avant allocation/désérialisation.");
        }

        using (AtomicUpdateEnvironment environment = new())
        {
            LauncherUpdateProcessSignal signal = new(
                environment.Transaction.TransactionId,
                42,
                IsElevated: false,
                DateTimeOffset.UtcNow);
            string json = JsonSerializer.Serialize(signal);
            string duplicate = json.Replace(
                "\"ProcessId\":42",
                "\"ProcessId\":42,\"processId\":43",
                StringComparison.Ordinal);
            True(!string.Equals(json, duplicate, StringComparison.Ordinal),
                "Le fixture signal doit injecter une propriété dupliquée sans tenir compte de la casse.");
            File.WriteAllText(environment.Transaction.ReadySignalPath, duplicate);
            True(environment.Store.TryReadReadySignal(environment.Transaction) is null,
                "Un signal avec propriété dupliquée doit être rejeté.");
        }

        using (AtomicUpdateEnvironment environment = new())
        {
            LauncherUpdateProcessSignal signal = new(
                environment.Transaction.TransactionId,
                42,
                IsElevated: false,
                DateTimeOffset.UtcNow);
            string json = JsonSerializer.Serialize(signal);
            string unknown = json[..^1] + ",\"PrivilegedPath\":\"C:\\\\Windows\"}";
            File.WriteAllText(environment.Transaction.ReadySignalPath, unknown);
            True(environment.Store.TryReadReadySignal(environment.Transaction) is null,
                "Un signal avec propriété inconnue doit être rejeté.");
        }
    }

    private static void HoldCandidateIdentityAcrossPrivilegedCopyBoundary()
    {
        using AtomicUpdateEnvironment environment = new();
        using FileStream stableCandidate = environment.Store.OpenUserFileForStableRead(
            environment.Transaction.CandidatePath);
        Throws<IOException>(
            () => File.WriteAllBytes(
                environment.Transaction.CandidatePath,
                CreatePayload("replacement")),
            "Le handle candidat transmis au copieur protégé doit refuser toute réécriture concurrente.");
        Throws<IOException>(
            () => File.Move(
                environment.Transaction.CandidatePath,
                environment.Transaction.CandidatePath + ".moved"),
            "Le handle candidat doit refuser un renommage qui changerait son identité pendant la copie.");
        string hash = Convert.ToHexString(SHA256.HashData(stableCandidate)).ToLowerInvariant();
        Equal(environment.Transaction.CandidateSha256, hash,
            "Le handle stable doit conserver exactement l'identité du candidat authentifié.");
    }

    private static async Task LeaveReleaseUntouchedWhenParentDoesNotExitAsync()
    {
        using AtomicUpdateEnvironment environment = new(parentExits: false);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);
        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Le helper doit abandonner si le PID parent ne se ferme pas.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Aucun nouveau launcher ne doit être démarré tant que l'ancien vit.");
    }

    private static LauncherUpdateRetryPolicy FastRetryPolicy(int fileAttempts = 5) => new(
        fileAttempts,
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(140),
        TimeSpan.FromMilliseconds(10));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "source", "README.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string NewNonSensitiveTestRoot(string scenario)
    {
        string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory));
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new InvalidOperationException("Racine de volume de test introuvable.");
        }

        return Path.Combine(
            volumeRoot,
            "AtlasLauncherElevationTest-" + scenario + "-" + Guid.NewGuid().ToString("N"));
    }

    private static byte[] CreatePayload(string marker, int size = 256 * 1024)
    {
        byte[] markerBytes = Encoding.UTF8.GetBytes(marker);
        byte[] payload = new byte[size];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = markerBytes[index % markerBytes.Length];
        }

        return payload;
    }

    private static string Hash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static LauncherUpdateManifest CreateTestManifest(
        string version,
        byte[] payload) => new()
    {
        SchemaVersion = 1,
        KeyId = "atlas-test-key",
        Version = version,
        Url = $"https://update.animeclub.fr/launcher/{version}/AtlasLauncher.exe",
        Size = payload.LongLength,
        Sha256 = Hash(payload),
        PublishedAt = "2026-09-09T00:00:00Z",
        Signature = "test-signature"
    };

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de créer la jonction de test.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "La création de la jonction de test a échoué: "
                + process.StandardError.ReadToEnd());
        }
    }

    private static void RunIcacls(string path, params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(path);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de lancer icacls pour le test.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "icacls a échoué pendant le test: " + process.StandardError.ReadToEnd());
        }
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Exception attendue: " + typeof(T).Name);
    }

    private static void Throws<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static void BytesEqual(byte[] expected, byte[] actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void SequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
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

    private enum FakeLaunchBehavior
    {
        Ready,
        DelayedReady,
        NoReady,
        WrongTransactionReady,
        WrongProcessReady,
        ImmediateExit,
        ReadyThenExit,
        Throw
    }

    private sealed class AtomicUpdateEnvironment : IDisposable
    {
        private readonly LauncherUpdateFaultPoint? _faultPoint;

        internal AtomicUpdateEnvironment(
            LauncherUpdateFaultPoint? faultPoint = null,
            FakeLaunchBehavior launchBehavior = FakeLaunchBehavior.Ready,
            LauncherUpdateRetryPolicy? retryPolicy = null,
            bool parentExits = true,
            ILauncherAtomicFileMover? atomicMover = null,
            ILauncherUpdateFaultInjector? faultInjector = null,
            bool recoveryProcessIsAlive = true,
            LauncherInstalledAppVersionSyncStatus registrationStatus =
                LauncherInstalledAppVersionSyncStatus.Updated,
            Action<LauncherUpdateTransaction, string>? protectedFileValidator = null)
        {
            _faultPoint = faultPoint;
            Root = NewNonSensitiveTestRoot("atomic");
            TransactionsRoot = Path.Combine(Root, "SelfUpdate", "Transactions");
            Directory.CreateDirectory(TransactionsRoot);
            OldBytes = CreatePayload("old-release");
            NewBytes = CreatePayload("new-release");
            string install = Path.Combine(Root, "install");
            Directory.CreateDirectory(install);
            TargetPath = Path.Combine(install, "AtlasLauncher.exe");
            File.WriteAllBytes(TargetPath, OldBytes);

            Store = new LauncherUpdateTransactionStore(TransactionsRoot);
            Transaction = CreateTransaction();
            Store.Save(Transaction);
            Launcher = new FakeApplicationLauncher(Store, launchBehavior);
            RecoveryLauncher = new FakeApplicationLauncher(Store, FakeLaunchBehavior.Ready);
            LauncherUpdateRetryPolicy policy = retryPolicy ?? FastRetryPolicy();
            ILauncherUpdateFaultInjector injector = faultInjector
                ?? (faultPoint is null
                    ? NullLauncherUpdateFaultInjector.Instance
                    : new ThrowingFaultInjector(faultPoint.Value));
            VersionSynchronizer = new RecordingInstalledAppVersionSynchronizer(
                registrationStatus);
            Service = new LauncherAtomicReplacementService(
                Store,
                atomicMover ?? new WindowsLauncherAtomicFileMover(),
                new FakeParentWaiter(parentExits),
                Launcher,
                policy,
                injector,
                installedAppVersionSynchronizer: VersionSynchronizer,
                protectedFileValidator: protectedFileValidator);
            RecoveryService = new LauncherAtomicReplacementService(
                Store,
                new WindowsLauncherAtomicFileMover(),
                new FakeParentWaiter(true),
                RecoveryLauncher,
                policy,
                processMatchesPath: (processId, path) =>
                    recoveryProcessIsAlive
                    && Launcher.LastProcess is { HasExited: false } process
                    && process.ProcessId == processId
                    && string.Equals(path, TargetPath, StringComparison.OrdinalIgnoreCase),
                processMatchesIdentity: (processId, path, startedAt) =>
                    recoveryProcessIsAlive
                    && Launcher.LastProcess is { HasExited: false } process
                    && process.ProcessId == processId
                    && string.Equals(path, TargetPath, StringComparison.OrdinalIgnoreCase)
                    && (process.StartedAt - startedAt).Duration()
                       <= TimeSpan.FromSeconds(1),
                stopProcess: (processId, path) =>
                {
                    if (Launcher.LastProcess is { HasExited: false } process
                        && process.ProcessId == processId
                        && string.Equals(path, TargetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                    }
                },
                installedAppVersionSynchronizer: VersionSynchronizer,
                protectedFileValidator: protectedFileValidator);
        }

        internal string Root { get; }

        internal string TransactionsRoot { get; }

        internal string TargetPath { get; }

        internal byte[] OldBytes { get; }

        internal byte[] NewBytes { get; }

        internal LauncherUpdateTransactionStore Store { get; }

        internal LauncherUpdateTransaction Transaction { get; private set; }

        internal FakeApplicationLauncher Launcher { get; }

        internal FakeApplicationLauncher RecoveryLauncher { get; }

        internal LauncherAtomicReplacementService Service { get; }

        internal LauncherAtomicReplacementService RecoveryService { get; }

        internal RecordingInstalledAppVersionSynchronizer VersionSynchronizer { get; }

        internal void CorruptCandidate()
        {
            File.WriteAllText(Transaction.CandidatePath, "corrupt");
        }

        internal async Task AssertTargetIsOldAsync()
        {
            Equal(Hash(OldBytes), await LauncherUpdateTransactionStore.ComputeSha256Async(
                    TargetPath,
                    CancellationToken.None),
                "La cible doit contenir exactement l'ancienne release.");
        }

        internal async Task AssertTargetIsNewAsync()
        {
            Equal(Hash(NewBytes), await LauncherUpdateTransactionStore.ComputeSha256Async(
                    TargetPath,
                    CancellationToken.None),
                "La cible doit contenir exactement la nouvelle release.");
        }

        public void Dispose()
        {
            Launcher.Dispose();
            RecoveryLauncher.Dispose();
            LauncherUpdateTransactionStore.TryDeleteDirectory(Root);
        }

        private LauncherUpdateTransaction CreateTransaction()
        {
            Guid id = Guid.NewGuid();
            string workspace = Path.Combine(TransactionsRoot, id.ToString("N"));
            Directory.CreateDirectory(workspace);
            string candidate = Path.Combine(workspace, "candidate.exe");
            string helper = LauncherUpdateElevationSecurity.GetProtectedHelperPath(
                TargetPath,
                id);
            File.WriteAllBytes(candidate, NewBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
            File.WriteAllBytes(helper, OldBytes);
            string suffix = ".atlas-" + id.ToString("N");
            return new LauncherUpdateTransaction(
                LauncherUpdateTransaction.CurrentSchemaVersion,
                id,
                Environment.ProcessId,
                TargetPath,
                workspace,
                candidate,
                helper,
                TargetPath + suffix + ".new",
                TargetPath + suffix + ".backup",
                Path.Combine(workspace, "transaction.json"),
                LauncherUpdateElevationSecurity.GetProtectedHelperAcceptedSignalPath(
                    TargetPath,
                    id),
                Path.Combine(workspace, "started.json"),
                Path.Combine(workspace, "ready.json"),
                NewBytes.Length,
                Hash(OldBytes),
                Hash(NewBytes),
                LauncherUpdateTransactionPhase.Prepared,
                DateTimeOffset.UtcNow,
                AuthenticatedTargetVersion: "1.2.0",
                AuthenticatedManifest: CreateTestManifest("1.2.0", NewBytes));
        }
    }

    private sealed class SwapAndDenyUserOperationRunner(Action swap)
        : ILauncherUpdateUserOperationRunner
    {
        private bool _swapped;

        internal int RunCalls { get; private set; }

        public void Run(Action operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            EnterDeniedBoundary();
        }

        public T Run<T>(Func<T> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            EnterDeniedBoundary();
            throw new InvalidOperationException("Frontière utilisateur non bloquante.");
        }

        private void EnterDeniedBoundary()
        {
            RunCalls++;
            if (!_swapped)
            {
                _swapped = true;
                swap();
            }

            throw new UnauthorizedAccessException(
                "Écriture refusée par le jeton utilisateur synthétique.");
        }
    }

    private sealed class RecordingInstalledAppVersionSynchronizer(
        LauncherInstalledAppVersionSyncStatus status)
        : ILauncherInstalledAppVersionSynchronizer
    {
        internal int Calls { get; private set; }

        internal string? LastVersion { get; private set; }

        internal string DisplayVersion { get; private set; } = "1.1.2";

        public LauncherInstalledAppVersionSyncResult Synchronize(
            LauncherUpdateTransaction transaction)
        {
            Calls++;
            LastVersion = transaction.AuthenticatedTargetVersion;
            if (status is LauncherInstalledAppVersionSyncStatus.Updated
                or LauncherInstalledAppVersionSyncStatus.AlreadyCurrent)
            {
                DisplayVersion = transaction.AuthenticatedTargetVersion
                    ?? DisplayVersion;
            }
            return new LauncherInstalledAppVersionSyncResult(
                status);
        }
    }

    private sealed class FakeParentWaiter(bool exits) : ILauncherUpdateParentWaiter
    {
        public Task<bool> WaitForExitAsync(
            int processId,
            string expectedExecutablePath,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(exits);
    }

    private sealed class ThrowingFaultInjector(LauncherUpdateFaultPoint point)
        : ILauncherUpdateFaultInjector
    {
        public void Hit(
            LauncherUpdateFaultPoint current,
            LauncherUpdateTransaction transaction)
        {
            if (current == point)
            {
                throw new LauncherUpdateSimulatedCrashException(point);
            }
        }
    }

    private sealed class MutatingFaultInjector(
        LauncherUpdateFaultPoint point,
        Action<LauncherUpdateTransaction> mutate) : ILauncherUpdateFaultInjector
    {
        public void Hit(
            LauncherUpdateFaultPoint current,
            LauncherUpdateTransaction transaction)
        {
            if (current == point)
            {
                mutate(transaction);
            }
        }
    }

    private sealed class FailingThenAtomicMover(int failuresBeforeSuccess)
        : ILauncherAtomicFileMover
    {
        private readonly WindowsLauncherAtomicFileMover _inner = new();

        internal int Attempts { get; private set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            Attempts++;
            if (Attempts <= failuresBeforeSuccess)
            {
                throw new IOException("simulated transient lock");
            }

            _inner.Replace(sourcePath, destinationPath);
        }
    }

    private sealed class AlwaysFailingAtomicMover : ILauncherAtomicFileMover
    {
        internal int Attempts { get; private set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            Attempts++;
            throw new UnauthorizedAccessException("simulated permission failure");
        }
    }

    private sealed class WindowsLockingAtomicMover(TimeSpan? releaseAfter)
        : ILauncherAtomicFileMover, IDisposable
    {
        private readonly WindowsLauncherAtomicFileMover _inner = new();
        private FileStream? _lock;
        private Task _automaticRelease = Task.CompletedTask;

        internal int Attempts { get; private set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            Attempts++;
            if (_lock is null && Attempts == 1)
            {
                _lock = new FileStream(
                    destinationPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
                if (releaseAfter is TimeSpan delay)
                {
                    _automaticRelease = Task.Run(async () =>
                    {
                        await Task.Delay(delay);
                        Release();
                    });
                }
            }

            _inner.Replace(sourcePath, destinationPath);
        }

        internal Task WaitForAutomaticReleaseAsync() => _automaticRelease;

        internal void Release()
        {
            Interlocked.Exchange(ref _lock, null)?.Dispose();
        }

        public void Dispose() => Release();
    }

    private sealed class FakeApplicationLauncher(
        LauncherUpdateTransactionStore store,
        FakeLaunchBehavior behavior) : ILauncherUpdateApplicationLauncher, IDisposable
    {
        private readonly List<FakeLaunchedProcess> _processes = [];
        private int _nextProcessId = 20_000;

        internal int UpdatedLaunchCalls { get; private set; }

        internal int RollbackLaunchCalls { get; private set; }

        internal FakeLaunchedProcess? LastProcess { get; private set; }

        public async Task<ILauncherUpdateLaunchedProcess> LaunchUpdatedAsync(
            LauncherUpdateTransaction transaction,
            TimeSpan startTimeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            UpdatedLaunchCalls++;
            if (behavior == FakeLaunchBehavior.Throw)
            {
                throw new InvalidOperationException("simulated launch failure");
            }

            int processId = Interlocked.Increment(ref _nextProcessId);
            LastProcess = new FakeLaunchedProcess(
                processId,
                behavior is FakeLaunchBehavior.ImmediateExit
                    or FakeLaunchBehavior.ReadyThenExit);
            _processes.Add(LastProcess);
            store.WriteStartedSignal(
                transaction,
                new LauncherUpdateProcessSignal(
                    transaction.TransactionId,
                    processId,
                    IsElevated: false,
                    DateTimeOffset.UtcNow));

            switch (behavior)
            {
                case FakeLaunchBehavior.Ready:
                case FakeLaunchBehavior.ReadyThenExit:
                    WriteReady(transaction, transaction.TransactionId, processId);
                    break;
                case FakeLaunchBehavior.DelayedReady:
                    await Task.Delay(35, cancellationToken);
                    WriteReady(transaction, transaction.TransactionId, processId);
                    break;
                case FakeLaunchBehavior.WrongTransactionReady:
                    string originalPath = transaction.ReadySignalPath;
                    LauncherUpdateTransaction wrong = transaction with
                    {
                        TransactionId = Guid.NewGuid(),
                        ReadySignalPath = Path.Combine(
                            transaction.WorkspacePath,
                            "wrong-ready.json")
                    };
                    store.WriteReadySignal(
                        wrong,
                        new LauncherUpdateProcessSignal(
                            wrong.TransactionId,
                            processId,
                            false,
                            DateTimeOffset.UtcNow));
                    File.Copy(wrong.ReadySignalPath, originalPath, overwrite: true);
                    break;
                case FakeLaunchBehavior.WrongProcessReady:
                    WriteReady(transaction, transaction.TransactionId, processId + 100);
                    break;
            }

            return LastProcess;
        }

        public Task LaunchRollbackAsync(
            LauncherUpdateTransaction transaction,
            CancellationToken cancellationToken)
        {
            RollbackLaunchCalls++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            foreach (FakeLaunchedProcess process in _processes)
            {
                process.Dispose();
            }
        }

        private void WriteReady(
            LauncherUpdateTransaction transaction,
            Guid transactionId,
            int processId)
        {
            store.WriteReadySignal(
                transaction,
                new LauncherUpdateProcessSignal(
                    transactionId,
                    processId,
                    IsElevated: false,
                    DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeLaunchedProcess(int processId, bool hasExited)
        : ILauncherUpdateLaunchedProcess
    {
        internal int KillCalls { get; private set; }

        public int ProcessId { get; } = processId;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public bool HasExited { get; private set; } = hasExited;

        public void Kill()
        {
            KillCalls++;
            HasExited = true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingHelperLauncher : ILauncherUpdateHelperLauncher
    {
        internal int ApplyCalls { get; private set; }

        internal LauncherUpdateTransaction? LastTransaction { get; private set; }

        public Task LaunchApplyAsync(
            LauncherUpdateTransaction transaction,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            LastTransaction = transaction;
            return Task.CompletedTask;
        }

        public Task LaunchRecoveryAsync(
            LauncherUpdateTransaction transaction,
            int requesterProcessId,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Recovery inattendue dans ce test.");
        }
    }
}
