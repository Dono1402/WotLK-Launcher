using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Updater;

internal static class GameUninstallSecurityTests
{
    internal static int Run()
    {
        ValidateModeAndIdentityBinding();
        ValidateInstallRootOwnershipPolicy();
        ValidateElevationFailsBeforeReadingRegistration();
        ValidateCorruptRegistrationIsRejected();
        ValidateStrictMarkerParsing();
        ValidateReparseTreeIsRejected();
        ValidateHardLinkIsRejected();
        ValidateTransientCleanupHelperBoundary();
        ValidateDeletionRechecksAfterPreflight();
        ValidateBoundedDeletionRemovesOnlyInstallRoot();
        ValidateStructuredCleanupArgumentsPreserveLiteralPaths();
        ValidateEncodedSelfDeleteCommand();
        ValidateOfflineLegacyUninstallerMigration();
        ValidatePendingUpdateDefersUninstallerMigration();
        ValidateMigrationNeutralizesProtectedOrReparseRoots();
        ValidateMigrationPreservesForeignRegistration();
        ValidateInaccessibleProcessFailsClosed();
        Console.WriteLine("Game uninstall security OK.");
        return 0;
    }

    private static void ValidateInstallRootOwnershipPolicy()
    {
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        string[] sensitiveRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty,
            string.IsNullOrWhiteSpace(userProfile)
                ? string.Empty
                : Path.Combine(userProfile, "Downloads"),
            Environment.GetEnvironmentVariable("OneDrive")
                ?? (string.IsNullOrWhiteSpace(userProfile)
                    ? string.Empty
                    : Path.Combine(userProfile, "OneDrive"))
        ];
        foreach (string sensitive in sensitiveRoots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Throws<InvalidOperationException>(
                () => GameInstallServices.NormalizeAndValidateGameRoot(sensitive),
                "Une racine shell, AppData, temp ou OneDrive doit être refusée exactement.");
            Throws<InvalidOperationException>(
                () => GameInstallServices.NormalizeAndValidateGameRoot(
                    Path.Combine(sensitive, "AtlasGames", "WotLK")),
                "Un descendant d'une racine shell, AppData, temp ou OneDrive doit être refusé.");
        }

        string testRoot = NewNonSensitiveTestRoot("install-root-ownership");
        string unrelated = Path.Combine(testRoot, "unrelated");
        string empty = Path.Combine(testRoot, "empty-client");
        string recognized = Path.Combine(testRoot, "existing-wotlk");
        try
        {
            Directory.CreateDirectory(unrelated);
            string sentinel = Path.Combine(unrelated, "family-photos.txt");
            File.WriteAllText(sentinel, "keep", Encoding.UTF8);
            Throws<InvalidDataException>(
                () => GameInstallServices.PrepareGameInstallRoot(unrelated),
                "Un dossier non vide sans client WotLK doit être refusé avant installation.");
            Assert(File.Exists(sentinel)
                   && !File.Exists(Path.Combine(
                       unrelated,
                       GameInstallServices.InstallRootOwnershipMarkerFileName))
                   && !File.Exists(Path.Combine(
                       unrelated,
                       GameInstallServices.ClientMarkerFileName))
                   && !File.Exists(Path.Combine(
                       unrelated,
                       GameInstallServices.UninstallerFileName)),
                "Le refus d'un dossier non lié ne doit écrire ni preuve, ni registre simulé, ni désinstalleur.");

            Directory.CreateDirectory(empty);
            GameInstallRootOwnership emptyOwnership =
                GameInstallServices.PrepareGameInstallRoot(empty);
            Assert(emptyOwnership.OwnershipId != Guid.Empty
                   && File.Exists(Path.Combine(
                       empty,
                       GameInstallServices.InstallRootOwnershipMarkerFileName)),
                "Un dossier dédié vide doit recevoir une preuve de propriété bornée.");

            Directory.CreateDirectory(Path.Combine(recognized, "Data"));
            Directory.CreateDirectory(Path.Combine(recognized, "_classic_"));
            File.WriteAllText(
                Path.Combine(recognized, ".build.info"),
                "build",
                Encoding.UTF8);
            File.WriteAllText(
                GameInstallServices.GetGameExecutablePath(recognized),
                "wow",
                Encoding.UTF8);
            GameInstallRootOwnership recognizedOwnership =
                GameInstallServices.PrepareGameInstallRoot(recognized);
            Assert(recognizedOwnership.OwnershipId != Guid.Empty,
                "Une racine WotLK reconnue par Data/.build.info/WowClassic.exe doit être acceptée.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void ValidateModeAndIdentityBinding()
    {
        Assert(
            GameInstallServices.IsGameUninstallMode(["--uninstall-game"]),
            "Le mode de désinstallation du jeu doit reconnaître son argument long.");
        Assert(
            !GameInstallServices.IsGameUninstallMode(["--ui-v2"]),
            "Un démarrage normal ne doit pas sélectionner la désinstallation du jeu.");

        using TestFixture fixture = TestFixture.Create("identity");
        GameUninstallIdentity identity = GameInstallServices.ValidateGameUninstallPreparation(
            fixture.UninstallerPath,
            isElevated: false,
            () => fixture.Registration);
        Equal(fixture.InstallRoot, identity.InstallRoot,
            "La racine doit être dérivée du parent canonique du désinstalleur.");
        Equal(fixture.UninstallerPath, identity.UninstallerPath,
            "Le désinstalleur validé doit être l'exécutable courant exact.");

        string wrongName = Path.Combine(fixture.InstallRoot, "WotLK.Launcher.exe");
        File.WriteAllText(wrongName, "fixture", Encoding.UTF8);
        Throws<InvalidDataException>(
            () => GameInstallServices.DeriveGameUninstallIdentity(wrongName),
            "Un exécutable portant un autre nom ne doit pas devenir désinstalleur.");

        string driveRoot = Path.GetPathRoot(Path.GetTempPath())
            ?? throw new InvalidOperationException("Racine de volume de test introuvable.");
        Throws<InvalidOperationException>(
            () => GameInstallServices.NormalizeAndValidateGameRoot(driveRoot),
            "La racine d'un volume ne doit jamais être une cible de désinstallation.");
    }

    private static void ValidateElevationFailsBeforeReadingRegistration()
    {
        bool registrationRead = false;
        Throws<UnauthorizedAccessException>(
            () => GameInstallServices.ValidateGameUninstallPreparation(
                currentExecutablePath: @"C:\not-even-read\WotLK Uninstaller.exe",
                isElevated: true,
                () =>
                {
                    registrationRead = true;
                    throw new InvalidOperationException("La couture registre ne doit pas être appelée.");
                }),
            "La désinstallation doit refuser un processus élevé.");
        Assert(!registrationRead,
            "Le refus du token élevé doit précéder toute lecture du registre ou du disque.");
    }

    private static void ValidateCorruptRegistrationIsRejected()
    {
        using TestFixture fixture = TestFixture.Create("registry");
        GameUninstallRegistryBinding[] corrupt =
        [
            fixture.Registration with { UsesOnlyLiteralStrings = false },
            fixture.Registration with { DisplayName = "Other product" },
            fixture.Registration with { InstallLocation = fixture.InstallRoot + ".other" },
            fixture.Registration with { UninstallString = "WotLK Uninstaller.exe /uninstall-game" },
            fixture.Registration with { QuietUninstallString = null }
        ];

        foreach (GameUninstallRegistryBinding registration in corrupt)
        {
            Throws<InvalidDataException>(
                () => GameInstallServices.ValidateGameUninstallPreparation(
                    fixture.UninstallerPath,
                    isElevated: false,
                    () => registration),
                "Une inscription Windows absente, expansible ou incohérente doit être refusée.");
        }
    }

    private static void ValidateStrictMarkerParsing()
    {
        using TestFixture fixture = TestFixture.Create("marker");
        string markerPath = fixture.MarkerPath;

        File.WriteAllText(
            markerPath,
            CreateMarkerJson(fixture, installRoot: fixture.InstallRoot + ".other"),
            new UTF8Encoding(false));
        Throws<InvalidDataException>(
            () => fixture.Validate(),
            "Un marqueur lié à une autre racine doit être refusé.");

        File.WriteAllText(
            markerPath,
            CreateMarkerJson(fixture, duplicateInstallRoot: true),
            new UTF8Encoding(false));
        Throws<InvalidDataException>(
            () => fixture.Validate(),
            "Un marqueur contenant une propriété dupliquée doit être refusé.");

        File.WriteAllText(
            markerPath,
            CreateMarkerJson(fixture, includeUnknownProperty: true),
            new UTF8Encoding(false));
        Throws<InvalidDataException>(
            () => fixture.Validate(),
            "Un marqueur contenant une propriété inconnue doit être refusé.");

        File.WriteAllText(
            markerPath,
            CreateMarkerJson(fixture, omitInstalledAt: true),
            new UTF8Encoding(false));
        Throws<InvalidDataException>(
            () => fixture.Validate(),
            "Un marqueur auquel il manque une propriété requise doit être refusé.");

        File.WriteAllBytes(
            markerPath,
            Enumerable.Repeat(
                    (byte)' ',
                    GameInstallServices.MaximumInstallMarkerBytes + 1)
                .ToArray());
        Throws<InvalidDataException>(
            () => fixture.Validate(),
            "Un marqueur supérieur à 64 Kio doit être refusé avant parsing.");

        File.WriteAllText(
            markerPath,
            CreateMarkerJson(fixture),
            new UTF8Encoding(false));
        fixture.Validate();
    }

    private static void ValidateReparseTreeIsRejected()
    {
        using TestFixture fixture = TestFixture.Create("reparse");
        string victim = Path.Combine(fixture.Root, "victim");
        string sentinel = Path.Combine(victim, "sentinel.txt");
        string junction = Path.Combine(fixture.InstallRoot, "linked-content");
        Directory.CreateDirectory(victim);
        File.WriteAllText(sentinel, "keep", Encoding.UTF8);
        CreateDirectoryJunction(junction, victim);
        try
        {
            Throws<InvalidDataException>(
                () => fixture.Validate(),
                "Un point de jonction dans l'arbre WotLK doit arrêter la désinstallation.");
            Equal("keep", File.ReadAllText(sentinel, Encoding.UTF8),
                "La validation ne doit ni suivre ni modifier la cible d'une jonction.");
        }
        finally
        {
            DeleteJunction(junction);
        }
    }

    private static void ValidateHardLinkIsRejected()
    {
        using TestFixture fixture = TestFixture.Create("hardlink");
        string victim = Path.Combine(fixture.Root, "hardlink-victim.txt");
        string linkedFile = Path.Combine(fixture.InstallRoot, "linked-file.bin");
        File.WriteAllText(victim, "keep", Encoding.UTF8);
        CreateHardLink(linkedFile, victim);

        Throws<InvalidDataException>(
            () => fixture.Validate(),
            "Un fichier possédant un autre lien physique doit arrêter la désinstallation.");
        Equal("keep", File.ReadAllText(victim, Encoding.UTF8),
            "La validation d'un hardlink ne doit pas modifier sa cible partagée.");
    }

    private static void ValidateTransientCleanupHelperBoundary()
    {
        using TestFixture fixture = TestFixture.Create("transient-helper");
        string localRoot = Path.Combine(fixture.Root, "LocalAppData");
        string sid = GameInstallServices.GetCurrentUserSid();
        GameUninstallIdentity identity = new(
            fixture.InstallRoot,
            fixture.UninstallerPath);
        using TransientGameUninstallWorkspace workspace =
            GameInstallServices.CreateTransientGameUninstallWorkspace(
                identity,
                localRoot,
                sid);

        Assert(File.Exists(workspace.HelperPath),
            "Le helper medium doit être copié sans dépendre du launcher Program Files ou de HKLM.");
        Equal(sid, workspace.RequesterSid,
            "Le workspace transitoire doit être lié au SID demandeur.");
        GameInstallServices.ValidatePrivateWorkspaceSecurity(
            workspace.WorkspacePath,
            sid);

        ProcessStartInfo startInfo =
            GameInstallServices.CreateGameUninstallCleanupStartInfo(
                workspace,
                identity,
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
        Assert(
            GameInstallServices.TryParseGameUninstallCleanupArguments(
                startInfo.ArgumentList.ToArray(),
                out GameUninstallCleanupRequest parsed),
            "La demande liée au helper transitoire doit être parsée.");
        using (GameInstallServices.ValidateTransientGameUninstallCleanup(
                   parsed,
                   workspace.HelperPath,
                   sid,
                   localRoot))
        {
        }

        ThrowsAny<IOException, UnauthorizedAccessException>(
            () => File.WriteAllText(
                workspace.HelperPath,
                "tamper",
                Encoding.UTF8),
            "Le handle parent doit interdire l'altération du helper avant le handshake.");

        DirectorySecurity security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(workspace.WorkspacePath));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(workspace.WorkspacePath),
            security);
        Throws<UnauthorizedAccessException>(
            () => GameInstallServices.ValidatePrivateWorkspaceSecurity(
                workspace.WorkspacePath,
                sid),
            "Un tiers autorisé à écrire dans le workspace doit être refusé.");

        string junctionParent = Path.Combine(
            localRoot,
            "Atlas Launcher",
            "GameUninstall");
        string junctionId = Guid.NewGuid().ToString("N");
        string junction = Path.Combine(junctionParent, junctionId);
        string target = Path.Combine(fixture.Root, "junction-target");
        Directory.CreateDirectory(target);
        string junctionHelper = Path.Combine(
            target,
            "WotLK Game Uninstall Helper.exe");
        File.Copy(fixture.UninstallerPath, junctionHelper);
        CreateDirectoryJunction(junction, target);
        try
        {
            GameUninstallCleanupRequest reparseRequest = parsed with
            {
                WorkspacePath = junction,
                HelperPath = Path.Combine(
                    junction,
                    "WotLK Game Uninstall Helper.exe"),
                CleanupEventName = @"Local\Atlas.GameUninstall." + junctionId
            };
            Throws<InvalidDataException>(
                () => GameInstallServices.ValidateTransientGameUninstallCleanup(
                    reparseRequest,
                    reparseRequest.HelperPath,
                    sid,
                    localRoot),
                "Un workspace transitoire remplacé par une jonction doit être refusé.");
        }
        finally
        {
            DeleteJunction(junction);
        }
    }

    private static void ValidateDeletionRechecksAfterPreflight()
    {
        using TestFixture fixture = TestFixture.Create("delete-race");
        string parked = fixture.InstallRoot + ".parked";
        string victim = Path.Combine(fixture.Root, "race-victim");
        string sentinel = Path.Combine(victim, "sentinel.txt");
        Directory.CreateDirectory(victim);
        File.WriteAllText(sentinel, "keep", Encoding.UTF8);

        try
        {
            Throws<InvalidDataException>(
                () => GameInstallServices.DeleteDirectoryTreeWithRetry(
                    fixture.InstallRoot,
                    validateUnderLock: fixture.Validate,
                    afterPreflight: () =>
                    {
                        Directory.Move(fixture.InstallRoot, parked);
                        Directory.Move(victim, fixture.InstallRoot);
                    }),
                "Une racine réelle remplacée après le préflight doit être refusée sous verrou.");
            Equal(
                "keep",
                File.ReadAllText(
                    Path.Combine(fixture.InstallRoot, "sentinel.txt"),
                    Encoding.UTF8),
                "La revalidation sous verrou doit laisser la victime réelle intacte.");
        }
        finally
        {
            if (Directory.Exists(fixture.InstallRoot)
                && !Directory.Exists(victim)
                && Directory.Exists(parked))
            {
                Directory.Move(fixture.InstallRoot, victim);
            }
            if (Directory.Exists(parked) && !Directory.Exists(fixture.InstallRoot))
            {
                Directory.Move(parked, fixture.InstallRoot);
            }
        }
    }

    private static void ValidateBoundedDeletionRemovesOnlyInstallRoot()
    {
        using TestFixture fixture = TestFixture.Create("delete-bounds");
        string sibling = Path.Combine(fixture.Root, "sibling.txt");
        string nested = Path.Combine(fixture.InstallRoot, "data", "client.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "client", Encoding.UTF8);
        File.WriteAllText(sibling, "keep", Encoding.UTF8);

        GameInstallServices.DeleteDirectoryTreeWithRetry(
            fixture.InstallRoot,
            fixture.Validate);
        Assert(!Directory.Exists(fixture.InstallRoot),
            "Le parcours borné doit supprimer la racine WotLK validée.");
        Equal("keep", File.ReadAllText(sibling, Encoding.UTF8),
            "Le parcours borné ne doit pas toucher un frère de la racine WotLK.");
    }

    private static void ValidateStructuredCleanupArgumentsPreserveLiteralPaths()
    {
        using TestFixture fixture = TestFixture.Create("%ATLAS_GAME_ROOT%");
        string root = fixture.InstallRoot;
        string uninstaller = fixture.UninstallerPath;
        string localRoot = Path.Combine(fixture.Root, "LocalAppData");
        GameUninstallIdentity identity = new(root, uninstaller);
        using TransientGameUninstallWorkspace workspace =
            GameInstallServices.CreateTransientGameUninstallWorkspace(
                identity,
                localRoot);
        ProcessStartInfo startInfo = GameInstallServices.CreateGameUninstallCleanupStartInfo(
            workspace,
            identity,
            parentProcessId: 4242,
            parentStartTimeUtcTicks: 638900000000000000L);

        Equal(string.Empty, startInfo.Arguments,
            "Le nettoyage ne doit pas construire une chaîne de commande interprétable.");
        Assert(!startInfo.UseShellExecute,
            "Le nettoyage doit démarrer directement l'exécutable protégé.");
        Assert(Path.IsPathFullyQualified(startInfo.FileName),
            "L'exécutable de nettoyage doit avoir un chemin pleinement qualifié.");
        Assert(!string.Equals(
                Path.GetFileName(startInfo.FileName),
                "cmd.exe",
                StringComparison.OrdinalIgnoreCase),
            "Le nettoyage ne doit jamais invoquer cmd.exe.");
        Equal(root, startInfo.ArgumentList[3],
            "Un pourcentage dans la racine doit rester un caractère littéral.");

        Assert(
            GameInstallServices.TryParseGameUninstallCleanupArguments(
                startInfo.ArgumentList.ToArray(),
                out GameUninstallCleanupRequest parsed),
            "Les arguments structurés produits doivent être acceptés par le mode interne.");
        Equal(root, parsed.InstallRoot,
            "Le parsing interne ne doit pas développer %ATLAS_GAME_ROOT%.");
        Equal(uninstaller, parsed.UninstallerPath,
            "Le chemin littéral du désinstalleur doit rester inchangé.");

        string[] malformed = startInfo.ArgumentList.ToArray();
        malformed[5] = "-1";
        Assert(
            !GameInstallServices.TryParseGameUninstallCleanupArguments(
                malformed,
                out _),
            "Un PID parent invalide doit être refusé.");
    }

    private static void ValidateEncodedSelfDeleteCommand()
    {
        using TestFixture fixture = TestFixture.Create("self-delete");
        string localRoot = Path.Combine(fixture.Root, "LocalAppData");
        GameUninstallIdentity identity = new(
            fixture.InstallRoot,
            fixture.UninstallerPath);
        using TransientGameUninstallWorkspace workspace =
            GameInstallServices.CreateTransientGameUninstallWorkspace(
                identity,
                localRoot);
        ProcessStartInfo cleanup =
            GameInstallServices.CreateGameUninstallCleanupStartInfo(
                workspace,
                identity,
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
        Assert(
            GameInstallServices.TryParseGameUninstallCleanupArguments(
                cleanup.ArgumentList.ToArray(),
                out GameUninstallCleanupRequest request),
            "La fixture self-delete doit produire une demande valide.");

        string systemRoot = Path.Combine(fixture.Root, "System32");
        ProcessStartInfo selfDelete =
            GameInstallServices.CreateTransientGameUninstallSelfDeleteStartInfo(
                request,
                childProcessId: 4242,
                childStartTimeUtcTicks: 638900000000000000L,
                systemRoot,
                localRoot);
        Equal(string.Empty, selfDelete.Arguments,
            "Le self-delete ne doit jamais construire une chaîne de commande brute.");
        Equal(
            Path.Combine(
                systemRoot,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            selfDelete.FileName,
            "PowerShell doit être qualifié depuis System32.");
        int encodedIndex = selfDelete.ArgumentList.IndexOf("-EncodedCommand");
        Assert(encodedIndex >= 0
            && encodedIndex == selfDelete.ArgumentList.Count - 2,
            "Le script de suppression doit être transmis uniquement via EncodedCommand.");
        foreach (string argument in selfDelete.ArgumentList)
        {
            Assert(!argument.Contains(workspace.WorkspacePath, StringComparison.OrdinalIgnoreCase)
                && !argument.Contains(workspace.HelperPath, StringComparison.OrdinalIgnoreCase),
                "Aucun chemin mutable ne doit être livré en texte brut au shell.");
        }

        string script = Encoding.Unicode.GetString(Convert.FromBase64String(
            selfDelete.ArgumentList[^1]));
        Assert(script.Contains("AreAccessRulesProtected", StringComparison.Ordinal)
            && script.Contains("ReparsePoint", StringComparison.Ordinal)
            && script.Contains("Directory]::Delete($workspace,$false)", StringComparison.Ordinal)
            && !script.Contains("-Recurse", StringComparison.OrdinalIgnoreCase),
            "Le script encodé doit revalider ACL/reparse et supprimer seulement le workspace vide.");
    }

    private static void ValidateOfflineLegacyUninstallerMigration()
    {
        using TestFixture fixture = TestFixture.Create("offline-migration");
        string currentLauncher = Path.Combine(fixture.Root, "current-launcher.exe");
        byte[] currentBytes = Encoding.UTF8.GetBytes(
            "current-safe-launcher-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(currentLauncher, currentBytes);
        File.WriteAllText(
            fixture.UninstallerPath,
            "legacy-unsafe-uninstaller",
            Encoding.UTF8);
        FakeMigrationRegistryStore store = new(
            CreateMigrationRegistration(fixture));
        bool networkTouched = false;

        GameUninstallMigrationResult result =
            GameInstallServices.MigrateRegisteredGameUninstaller(
                currentLauncher,
                store,
                _ => { });

        Equal(
            GameUninstallMigrationStatus.Migrated.ToString(),
            result.Status.ToString(),
            "Le démarrage local doit migrer un ancien désinstalleur hors ligne.");
        Assert(!networkTouched,
            "La migration de démarrage ne doit appeler ni HTTP ni authentification.");
        Assert(File.ReadAllBytes(fixture.UninstallerPath).SequenceEqual(currentBytes),
            "Le binaire legacy doit être remplacé par les octets du handle stable courant.");
        Assert(store.NeutralizeCount == 1 && store.RegisterCount == 1,
            "L'entrée Apps doit disparaître pendant la migration puis être recréée après validation.");
        fixture.Validate(store.CurrentBinding);
    }

    private static void ValidatePendingUpdateDefersUninstallerMigration()
    {
        using (TestFixture fixture = TestFixture.Create("update-rollback-policy"))
        {
            string currentLauncher = Path.Combine(fixture.Root, "WotLK.Launcher.exe");
            byte[] candidate = Encoding.UTF8.GetBytes("candidate-before-ready");
            byte[] previous = Encoding.UTF8.GetBytes("previous-after-rollback");
            byte[] legacy = Encoding.UTF8.GetBytes("legacy-uninstaller");
            File.WriteAllBytes(currentLauncher, candidate);
            File.WriteAllBytes(fixture.UninstallerPath, legacy);
            FakeMigrationRegistryStore store = new(
                CreateMigrationRegistration(fixture));
            bool migrationCalled = false;
            string sentinel = Path.Combine(fixture.InstallRoot, "client-data.bin");
            File.WriteAllText(sentinel, "keep", Encoding.UTF8);
            GameUninstallMigrationResult? neutralization = null;

            bool ranBeforeReady = App.TryRunGameUninstallMigrationForFinalLauncher(
                hasPendingUpdateTransaction: true,
                neutralizeGameUninstaller: () => neutralization =
                    GameInstallServices
                        .NeutralizeRegisteredGameUninstallerForPendingUpdate(store),
                migrateGameUninstaller: () => migrationCalled = true);
            Assert(!ranBeforeReady
                   && !migrationCalled
                   && neutralization?.Status == GameUninstallMigrationStatus.Neutralized,
                "Une transaction pré-Ready doit neutraliser l'entrée reconnue sans migrer le candidat.");
            Assert(store.Current is null && store.Pending is not null,
                "L'entrée Apps active doit être supprimée tout en gardant seulement un snapshot interne borné.");
            Assert(File.ReadAllBytes(fixture.UninstallerPath).SequenceEqual(legacy),
                "Un crash pré-Ready doit laisser l'ancien désinstalleur intact.");
            Assert(File.ReadAllText(sentinel, Encoding.UTF8) == "keep",
                "La neutralisation pré-Ready ne doit toucher aucun fichier du jeu.");

            File.WriteAllBytes(currentLauncher, previous);
            GameUninstallMigrationResult? result = null;
            bool ranAfterRollback = App.TryRunGameUninstallMigrationForFinalLauncher(
                hasPendingUpdateTransaction: false,
                neutralizeGameUninstaller: () => throw new InvalidOperationException(
                    "Le démarrage normal ne doit plus neutraliser."),
                migrateGameUninstaller: () => result =
                    GameInstallServices.MigrateRegisteredGameUninstaller(
                        currentLauncher,
                        store));
            Assert(ranAfterRollback
                && result?.Status == GameUninstallMigrationStatus.Migrated,
                "Le démarrage normal suivant un rollback doit migrer le binaire final précédent.");
            Assert(File.ReadAllBytes(fixture.UninstallerPath).SequenceEqual(previous),
                "Après rollback, seul le hash précédent final doit être copié.");
            Assert(store.Current is not null && store.Pending is null,
                "Le démarrage normal suivant doit recréer l'entrée sûre et effacer le snapshot.");
        }

        using (TestFixture fixture = TestFixture.Create("update-commit-policy"))
        {
            string currentLauncher = Path.Combine(fixture.Root, "WotLK.Launcher.exe");
            byte[] committedCandidate = Encoding.UTF8.GetBytes("committed-candidate");
            byte[] legacy = Encoding.UTF8.GetBytes("legacy-uninstaller");
            File.WriteAllBytes(currentLauncher, committedCandidate);
            File.WriteAllBytes(fixture.UninstallerPath, legacy);
            FakeMigrationRegistryStore store = new(
                CreateMigrationRegistration(fixture));

            Assert(!App.TryRunGameUninstallMigrationForFinalLauncher(
                    hasPendingUpdateTransaction: true,
                    neutralizeGameUninstaller: () =>
                        GameInstallServices
                            .NeutralizeRegisteredGameUninstallerForPendingUpdate(store),
                    migrateGameUninstaller: () => throw new InvalidOperationException(
                        "La migration pré-commit ne doit pas être appelée.")),
                "Le candidat doit rester différé jusqu'au commit.");
            GameUninstallMigrationResult? result = null;
            Assert(App.TryRunGameUninstallMigrationForFinalLauncher(
                    hasPendingUpdateTransaction: false,
                    neutralizeGameUninstaller: () => throw new InvalidOperationException(
                        "Le démarrage normal ne doit plus neutraliser."),
                    migrateGameUninstaller: () => result =
                        GameInstallServices.MigrateRegisteredGameUninstaller(
                            currentLauncher,
                            store)),
                "Le premier démarrage normal post-commit doit autoriser la migration.");
            Assert(result?.Status == GameUninstallMigrationStatus.Migrated
                && File.ReadAllBytes(fixture.UninstallerPath)
                    .SequenceEqual(committedCandidate),
                "Après commit, le désinstalleur doit recevoir le candidat final confirmé.");
        }

        using (TestFixture fixture = TestFixture.Create("pending-foreign"))
        {
            GameUninstallMigrationRegistration foreign =
                CreateMigrationRegistration(fixture) with
                {
                    DisplayName = "Application tierce"
                };
            FakeMigrationRegistryStore store = new(foreign);
            _ = App.TryRunGameUninstallMigrationForFinalLauncher(
                hasPendingUpdateTransaction: true,
                neutralizeGameUninstaller: () =>
                    GameInstallServices
                        .NeutralizeRegisteredGameUninstallerForPendingUpdate(store),
                migrateGameUninstaller: () => throw new InvalidOperationException(
                    "Le candidat ne doit jamais être migré pré-Ready."));
            Assert(store.Current?.Equals(foreign) == true && store.Pending is null,
                "Une entrée étrangère doit rester intacte pendant une mise à jour en attente.");
        }

        string transactionRoot = Path.Combine(
            Path.GetTempPath(),
            "AtlasPendingUpdateGate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionRoot);
        try
        {
            LauncherUpdateTransactionStore transactionStore = new(transactionRoot);
            string target = Path.Combine(transactionRoot, "WotLK.Launcher.exe");
            string malformedMarker =
                LauncherUpdateCommandLine.BuildPostUpdateArgument(Guid.NewGuid())[..^32]
                + "not-a-guid";
            LauncherUpdateStartupSession malformed =
                LauncherUpdateStartupSession.BeginForTests(
                    [malformedMarker],
                    recoverInterruptedTransactions: false,
                    transactionStore,
                    target);
            Assert(malformed.HasPendingTransactions
                   && LauncherUpdateCommandLine.FindPostUpdateTransaction(
                       [malformedMarker]) is null,
                "Tout préfixe post-update réservé doit bloquer la migration, même avec un identifiant invalide.");

            Guid missingId = Guid.NewGuid();
            LauncherUpdateStartupSession missing =
                LauncherUpdateStartupSession.BeginForTests(
                    [LauncherUpdateCommandLine.BuildPostUpdateArgument(missingId)],
                    recoverInterruptedTransactions: false,
                    transactionStore,
                    target);
            Assert(missing.HasPendingTransactions,
                "Un marqueur post-update valide doit bloquer la migration même si la transaction manque.");

            Guid corruptId = Guid.NewGuid();
            string corruptDirectory = Path.Combine(
                transactionRoot,
                corruptId.ToString("N"));
            Directory.CreateDirectory(corruptDirectory);
            File.WriteAllText(
                Path.Combine(corruptDirectory, "transaction.json"),
                "{not-json",
                Encoding.UTF8);
            LauncherUpdateStartupSession corrupt =
                LauncherUpdateStartupSession.BeginForTests(
                    [LauncherUpdateCommandLine.BuildPostUpdateArgument(corruptId)],
                    recoverInterruptedTransactions: false,
                    transactionStore,
                    target);
            Assert(corrupt.HasPendingTransactions,
                "Un marqueur post-update valide doit bloquer la migration si la transaction est corrompue.");
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
            {
                Directory.Delete(transactionRoot, recursive: true);
            }
        }
    }

    private static void ValidateMigrationNeutralizesProtectedOrReparseRoots()
    {
        using (TestFixture fixture = TestFixture.Create("protected-migration"))
        {
            string currentLauncher = Path.Combine(fixture.Root, "current-launcher.exe");
            File.WriteAllText(currentLauncher, "current", Encoding.UTF8);
            string sentinel = Path.Combine(fixture.InstallRoot, "client-data.bin");
            File.WriteAllText(sentinel, "keep", Encoding.UTF8);
            string legacy = File.ReadAllText(fixture.UninstallerPath, Encoding.UTF8);
            FakeMigrationRegistryStore store = new(
                CreateMigrationRegistration(fixture));

            GameUninstallMigrationResult result =
                GameInstallServices.MigrateRegisteredGameUninstaller(
                    currentLauncher,
                    store,
                    point =>
                    {
                        if (point == GameUninstallMigrationFaultPoint.BeforeAtomicReplace)
                        {
                            throw new UnauthorizedAccessException("protected fixture");
                        }
                    });

            Equal(
                GameUninstallMigrationStatus.Neutralized.ToString(),
                result.Status.ToString(),
                "Une racine protégée doit neutraliser l'ancienne entrée Apps.");
            Assert(store.Current is null && store.RegisterCount == 0,
                "Une migration sans écriture ne doit pas réexposer l'ancien désinstalleur.");
            Equal(legacy, File.ReadAllText(fixture.UninstallerPath, Encoding.UTF8),
                "L'échec ne doit pas remplacer partiellement le désinstalleur legacy.");
            Equal("keep", File.ReadAllText(sentinel, Encoding.UTF8),
                "L'échec de migration ne doit supprimer aucun fichier du jeu.");
        }

        string root = NewNonSensitiveTestRoot("migration-reparse");
        string target = Path.Combine(root, "target");
        string junction = Path.Combine(root, "game");
        Directory.CreateDirectory(target);
        string uninstaller = Path.Combine(
            target,
            GameInstallServices.UninstallerFileName);
        File.WriteAllText(uninstaller, "legacy", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(target, GameInstallServices.ClientMarkerFileName),
            CreateMarkerJson(target, uninstaller),
            new UTF8Encoding(false));
        CreateDirectoryJunction(junction, target);
        try
        {
            FakeMigrationRegistryStore store = new(
                CreateMigrationRegistration(junction));
            string currentLauncher = Path.Combine(root, "current.exe");
            File.WriteAllText(currentLauncher, "current", Encoding.UTF8);
            GameUninstallMigrationResult result =
                GameInstallServices.MigrateRegisteredGameUninstaller(
                    currentLauncher,
                    store);
            Equal(
                GameUninstallMigrationStatus.Neutralized.ToString(),
                result.Status.ToString(),
                "Une racine de jeu jonction doit neutraliser l'entrée reconnue.");
            Assert(store.Current is null,
                "Une racine reparse ne doit pas rester exposée dans Apps.");
            Equal("legacy", File.ReadAllText(uninstaller, Encoding.UTF8),
                "La migration ne doit jamais suivre la jonction pour modifier le jeu.");
        }
        finally
        {
            DeleteJunction(junction);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateMigrationPreservesForeignRegistration()
    {
        using TestFixture fixture = TestFixture.Create("foreign-migration");
        GameUninstallMigrationRegistration foreign =
            CreateMigrationRegistration(fixture) with
            {
                DisplayName = "Another product"
            };
        FakeMigrationRegistryStore store = new(foreign);
        string currentLauncher = Path.Combine(fixture.Root, "current.exe");
        File.WriteAllText(currentLauncher, "current", Encoding.UTF8);

        GameUninstallMigrationResult result =
            GameInstallServices.MigrateRegisteredGameUninstaller(
                currentLauncher,
                store);
        Equal(
            GameUninstallMigrationStatus.ForeignRegistrationPreserved.ToString(),
            result.Status.ToString(),
            "Une entrée étrangère doit être distinguée d'une entrée Atlas reconnue.");
        Assert(store.Current == foreign
            && store.NeutralizeCount == 0
            && store.RegisterCount == 0,
            "Une entrée étrangère doit rester strictement intacte.");
    }

    private static void ValidateInaccessibleProcessFailsClosed()
    {
        using TestFixture fixture = TestFixture.Create("process");
        Assert(
            !GameInstallServices.ProcessPathMatchesInstallRoot(
                fixture.InstallRoot,
                () => throw new UnauthorizedAccessException("fixture")),
            "Une erreur de lecture du processus ne doit jamais être traitée comme une correspondance.");
        Assert(
            !GameInstallServices.ProcessPathMatchesExpected(
                () => throw new UnauthorizedAccessException("fixture"),
                fixture.UninstallerPath),
            "Le helper doit refuser un parent dont le chemin est inaccessible.");
        Assert(
            !GameInstallServices.ProcessPathMatchesInstallRoot(
                fixture.InstallRoot,
                () => Path.Combine(fixture.Root, "outside.exe")),
            "Un processus homonyme situé hors de la racine ne doit pas être ciblé.");
    }

    private static string CreateMarkerJson(
        TestFixture fixture,
        string? installRoot = null,
        bool duplicateInstallRoot = false,
        bool includeUnknownProperty = false,
        bool omitInstalledAt = false)
    {
        List<string> properties =
        [
            "\"clientVersion\":\"1.5.0\"",
            "\"installRoot\":" + JsonSerializer.Serialize(installRoot ?? fixture.InstallRoot),
            "\"uninstaller\":" + JsonSerializer.Serialize(fixture.UninstallerPath),
            "\"registeredApp\":" + JsonSerializer.Serialize(GameInstallServices.AppDisplayName)
        ];
        if (!omitInstalledAt)
        {
            properties.Insert(
                0,
                "\"installedAt\":" + JsonSerializer.Serialize(DateTimeOffset.UtcNow));
        }
        if (duplicateInstallRoot)
        {
            properties.Add("\"installRoot\":" + JsonSerializer.Serialize(fixture.InstallRoot));
        }
        if (includeUnknownProperty)
        {
            properties.Add("\"unexpected\":true");
        }

        return "{" + string.Join(',', properties) + "}";
    }

    private static string CreateMarkerJson(
        string installRoot,
        string uninstallerPath)
    {
        return "{"
            + "\"installedAt\":" + JsonSerializer.Serialize(DateTimeOffset.UtcNow) + ","
            + "\"clientVersion\":\"1.5.0\","
            + "\"installRoot\":" + JsonSerializer.Serialize(installRoot) + ","
            + "\"uninstaller\":" + JsonSerializer.Serialize(uninstallerPath) + ","
            + "\"registeredApp\":"
            + JsonSerializer.Serialize(GameInstallServices.AppDisplayName)
            + "}";
    }

    private static GameUninstallMigrationRegistration CreateMigrationRegistration(
        TestFixture fixture) => CreateMigrationRegistration(fixture.InstallRoot);

    private static GameUninstallMigrationRegistration CreateMigrationRegistration(
        string installRoot)
    {
        string uninstaller = Path.Combine(
            installRoot,
            GameInstallServices.UninstallerFileName);
        string uninstallCommand = '"' + uninstaller + '"' + " /uninstall-game";
        return new GameUninstallMigrationRegistration(
            GameInstallServices.AppDisplayName,
            "1.5.0",
            installRoot,
            uninstallCommand,
            uninstallCommand + " /quiet",
            EstimatedSizeKb: 1024,
            UsesOnlyLiteralBoundedValues: true);
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
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

    private static void CreateHardLink(string linkPath, string targetPath)
    {
        ProcessStartInfo startInfo = CreateCmdStartInfo();
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/H");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de créer le hardlink de test.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "La création du hardlink de test a échoué: "
                + process.StandardError.ReadToEnd());
        }
    }

    private static ProcessStartInfo CreateCmdStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private static void DeleteJunction(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private static void Equal(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{message} Attendu: '{expected}'. Obtenu: '{actual}'.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void ThrowsAny<TFirst, TSecond>(
        Action action,
        string message)
        where TFirst : Exception
        where TSecond : Exception
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is TFirst or TSecond)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class FakeMigrationRegistryStore
        : IGameUninstallMigrationRegistryStore
    {
        internal FakeMigrationRegistryStore(
            GameUninstallMigrationRegistration? current)
        {
            Current = current;
        }

        internal GameUninstallMigrationRegistration? Current { get; private set; }

        internal GameUninstallMigrationRegistration? Pending { get; private set; }

        internal int NeutralizeCount { get; private set; }

        internal int RegisterCount { get; private set; }

        internal GameUninstallRegistryBinding CurrentBinding => Current is null
            ? throw new InvalidOperationException("La fixture registre est vide.")
            : new GameUninstallRegistryBinding(
                Current.DisplayName,
                Current.InstallLocation,
                Current.UninstallString,
                Current.QuietUninstallString,
                Current.UsesOnlyLiteralBoundedValues);

        public GameUninstallMigrationRegistration? Read() => Current ?? Pending;

        public bool TryNeutralize(GameUninstallMigrationRegistration expected)
        {
            if (expected.IsPendingSnapshot)
            {
                return Pending?.Equals(expected) == true;
            }

            if (Current is null || !Current.Equals(expected))
            {
                return false;
            }

            Pending = expected with { IsPendingSnapshot = true };
            Current = null;
            NeutralizeCount++;
            return true;
        }

        public void Register(
            GameUninstallMigrationRegistration previous,
            GameUninstallIdentity identity)
        {
            string uninstall = '"' + identity.UninstallerPath + '"'
                + " /uninstall-game";
            Current = previous with
            {
                DisplayName = GameInstallServices.AppDisplayName,
                InstallLocation = identity.InstallRoot,
                UninstallString = uninstall,
                QuietUninstallString = uninstall + " /quiet",
                UsesOnlyLiteralBoundedValues = true,
                IsPendingSnapshot = false
            };
            Pending = null;
            RegisterCount++;
        }
    }

    private sealed class TestFixture : IDisposable
    {
        private TestFixture(string root)
        {
            Root = root;
            InstallRoot = Path.Combine(root, "game");
            UninstallerPath = Path.Combine(
                InstallRoot,
                GameInstallServices.UninstallerFileName);
            MarkerPath = Path.Combine(
                InstallRoot,
                GameInstallServices.ClientMarkerFileName);
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(Path.Combine(InstallRoot, "Data"));
            Directory.CreateDirectory(Path.Combine(InstallRoot, "_classic_"));
            File.WriteAllText(
                Path.Combine(InstallRoot, ".build.info"),
                "fixture-build",
                Encoding.UTF8);
            File.WriteAllText(
                GameInstallServices.GetGameExecutablePath(InstallRoot),
                "fixture-wow",
                Encoding.UTF8);
            File.WriteAllText(UninstallerPath, "fixture", Encoding.UTF8);
            File.WriteAllText(
                MarkerPath,
                CreateMarkerJson(this),
                new UTF8Encoding(false));
            string uninstall = '"' + UninstallerPath + '"' + " /uninstall-game";
            Registration = new GameUninstallRegistryBinding(
                GameInstallServices.AppDisplayName,
                InstallRoot,
                uninstall,
                uninstall + " /quiet",
                UsesOnlyLiteralStrings: true);
        }

        internal string Root { get; }

        internal string InstallRoot { get; }

        internal string UninstallerPath { get; }

        internal string MarkerPath { get; }

        internal GameUninstallRegistryBinding Registration { get; }

        internal static TestFixture Create(string scenario)
        {
            string root = NewNonSensitiveTestRoot(scenario);
            return new TestFixture(root);
        }

        internal void Validate()
        {
            Validate(Registration);
        }

        internal void Validate(GameUninstallRegistryBinding registration)
        {
            _ = GameInstallServices.ValidateGameUninstallPreparation(
                UninstallerPath,
                isElevated: false,
                () => registration);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static string NewNonSensitiveTestRoot(string scenario)
    {
        string? volumeRoot = Path.GetPathRoot(
            Path.GetFullPath(AppContext.BaseDirectory));
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new InvalidOperationException(
                "La racine du volume de test WotLK est introuvable.");
        }

        return Path.Combine(
            volumeRoot,
            "AtlasLauncherGameRootTest-"
            + scenario
            + "-"
            + Guid.NewGuid().ToString("N"));
    }
}
