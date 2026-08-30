using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static class LauncherRuntimeCompositionTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeStartupModes();
        CharacterizeMissingClient();
        CharacterizePlayableClientAndVersion();
        CharacterizeInvalidVersionMarker();
        await CharacterizeReadOnlyRuntimeAndSingleRestoreAsync();
        await CharacterizeRuntimeShutdownDuringRestoreAsync();
        Console.WriteLine("Launcher runtime composition OK (02A).");
        return 0;
    }

    private static void CharacterizeStartupModes()
    {
        Equal(LauncherStartupMode.Legacy, App.ResolveStartupMode([]), "Sans argument, la v1.1.0 reste le défaut.");
        Equal(LauncherStartupMode.UiV2, App.ResolveStartupMode(["--ui-v2"]), "--ui-v2 doit sélectionner la V2 réelle.");
        Equal(
            LauncherStartupMode.UiV2Preview,
            App.ResolveStartupMode(["--ui-v2", "--preview-state=Ready"]),
            "Le preview doit nécessiter un état explicite.");
        Equal(
            LauncherStartupMode.Legacy,
            App.ResolveStartupMode(["--preview-state=Ready"]),
            "Un argument preview sans --ui-v2 ne doit produire aucun effet.");
    }

    private static void CharacterizeMissingClient()
    {
        using TemporaryClient client = new();
        GameClientLocalState state = new GameClientStateReader().Read(client.Settings);

        Equal(GameAction.Install, state.Action, "Un client absent doit produire GameAction.Install.");
        True(!state.IsPlayable, "Un client absent ne doit pas être présenté comme jouable.");
        Equal<string?>(null, state.InstalledVersion, "Aucune version ne doit être inventée.");
        Equal(
            GameUpdateKnowledge.Unknown,
            state.UpdateKnowledge,
            "La connaissance de mise à jour doit commencer à Unknown.");

        GameUiState presentation = LauncherV2RuntimePresentation.CreateGame(state);
        Equal(GamePreviewScenario.NotInstalled, presentation.Scenario, "La vue locale doit afficher NotInstalled.");
        Equal("Client non installé", presentation.ClientStatus, "Le libellé local absent est incorrect.");
        True(!presentation.IsPrimaryActionEnabled, "Installer doit rester désactivé pendant 02A.");
        True(!presentation.IsVerifyEnabled, "Vérifier reste désactivé avant 02C.");
        True(!presentation.OpenGameFolderCommand.CanExecute(null), "Dossier attend sa connexion 02B.");
        True(!presentation.OpenDiagnosticCommand.CanExecute(null), "Diagnostic attend sa connexion 02B.");
    }

    private static void CharacterizePlayableClientAndVersion()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        client.WriteVersionMarker("wotlk-classic-3.4.3.54261-frFR-2026.08.29.1");

        GameClientLocalState state = new GameClientStateReader().Read(client.Settings);
        Equal(GameAction.Play, state.Action, "Un client local complet doit produire GameAction.Play.");
        True(state.IsPlayable, "Les deux exécutables témoins doivent rendre le client jouable.");
        Equal(
            "wotlk-classic-3.4.3.54261-frFR-2026.08.29.1",
            state.InstalledVersion,
            "La version doit provenir de client-install.json.");
        Equal(GameUpdateKnowledge.Unknown, state.UpdateKnowledge, "Play ne doit pas signifier Known.");

        GameUiState presentation = LauncherV2RuntimePresentation.CreateGame(state);
        Equal("Client prêt", presentation.ClientStatus, "Un client jouable doit afficher Client prêt.");
        Equal("Non vérifié", presentation.InstallBadgeText, "Le badge ne doit pas afficher À jour sans manifeste.");
        True(
            !presentation.InstallBadgeText.Contains("jour", StringComparison.OrdinalIgnoreCase),
            "Le mode local ne doit jamais inventer un statut À jour.");
        True(!presentation.IsPrimaryActionEnabled, "Jouer doit rester désactivé pendant 02A.");
        True(!presentation.IsOptionsEnabled, "Options doit rester désactivé avant 02G.");
        True(!presentation.IsVerifyEnabled, "Vérifier attend toujours 02C.");
        True(!presentation.OpenGameFolderCommand.CanExecute(null), "Dossier est inactif avant la composition 02B.");
        True(!presentation.OpenDiagnosticCommand.CanExecute(null), "Diagnostic est inactif avant la composition 02B.");
        Equal(client.Root, presentation.InstallPath, "Le chemin réel doit être projeté dans la V2.");
        Equal("Français", presentation.Language, "La locale frFR doit être présentée en français.");
    }

    private static void CharacterizeInvalidVersionMarker()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        File.WriteAllText(
            Path.Combine(client.Root, GameInstallServices.ClientMarkerFileName),
            "{not-json");

        GameClientLocalState state = new GameClientStateReader().Read(client.Settings);
        Equal(GameAction.Play, state.Action, "Un marqueur invalide ne doit pas rendre le client injouable.");
        Equal<string?>(null, state.InstalledVersion, "Un marqueur invalide doit produire une version inconnue.");
    }

    private static async Task CharacterizeReadOnlyRuntimeAndSingleRestoreAsync()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        client.WriteVersionMarker("3.4.3.54261");
        FakeLauncherAuthService authentication = new()
        {
            RestoreResult = true,
            Session = FakeLauncherAuthService.CreateSession()
        };
        int settingsLoads = 0;
        int authenticationCreations = 0;
        LauncherRuntimeDependencies dependencies = new()
        {
            LoadSettings = () =>
            {
                settingsLoads++;
                return client.Settings;
            },
            CreateAuthentication = () =>
            {
                authenticationCreations++;
                return authentication;
            },
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = () => "v1.1.0-test"
        };

        LauncherRuntime runtime = new(dependencies);
        try
        {
            Equal(1, settingsLoads, "La V2 réelle doit charger les paramètres une fois.");
            Equal(1, authenticationCreations, "La V2 réelle doit créer une authentification.");
            Equal("v1.1.0-test", runtime.LauncherVersion, "La version du produit doit provenir de la composition.");

            ShellUiState shell = LauncherV2RuntimePresentation.CreateShell(runtime);
            GameUiState game = LauncherV2RuntimePresentation.CreateGame(runtime.LocalClient);
            FriendsUiState friends = LauncherV2RuntimePresentation.CreateFriends();
            Equal(RealmServiceState.Unknown, shell.RealmState, "Le royaume doit rester neutre sans requête.");
            Equal("Non vérifié", shell.RealmStatus, "Le royaume ne doit pas être déclaré en ligne sans preuve.");
            True(shell.IsGameNavigationEnabled, "La page Jeu locale doit rester visible.");
            True(!shell.IsNavigationEnabled, "Les destinations non migrées doivent rester désactivées.");
            Equal("Compte", shell.Username, "Aucune identité ne doit être inventée avant la restauration.");
            Equal(0, friends.Friends.Count, "La V2 réelle ne doit pas reprendre les amis fictifs du preview.");
            True(!game.IsPrimaryActionEnabled, "Aucune commande mutante ne doit être active en 02A.");

            GameUiState previewGame = LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready);
            FriendsUiState previewFriends = LauncherV2PreviewData.CreateFriends();
            True(previewGame.IsPrimaryActionEnabled, "Le preview doit conserver son comportement visuel fictif.");
            True(previewFriends.Friends.Count > 0, "Les amis fictifs doivent rester confinés au preview.");

            Task<LauncherSessionRestoreResult> first = runtime.InitializeAsync();
            Task<LauncherSessionRestoreResult> second = runtime.InitializeAsync();
            LauncherSessionRestoreResult[] results = await Task.WhenAll(first, second);
            LauncherSessionRestoreResult third = await runtime.InitializeAsync();
            Equal(1, authentication.RestoreCalls, "La session ne doit être restaurée qu'une fois.");
            True(results.All(result => result.Status == LauncherSessionRestoreStatus.Restored), "Les appels concurrents doivent partager le même résultat.");
            Equal(LauncherSessionRestoreStatus.Restored, third.Status, "Le résultat restauré doit être rejoué sans second appel.");

            LauncherV2RuntimePresentation.ApplySession(shell, third);
            Equal("Dono1402", shell.Username, "L'identité doit être appliquée après restauration seulement.");
            Equal("D", shell.ProfileInitial, "L'initiale du profil restauré est incorrecte.");
        }
        finally
        {
            runtime.Dispose();
            runtime.Dispose();
        }

        Equal(1, authentication.DisposeCalls, "Le runtime doit libérer l'authentification une seule fois.");
    }

    private static async Task CharacterizeRuntimeShutdownDuringRestoreAsync()
    {
        using TemporaryClient client = new();
        FakeLauncherAuthService authentication = new();
        authentication.RestoreHandler = cancellationToken =>
        {
            TaskCompletionSource<bool> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
            return pending.Task;
        };
        LauncherRuntime runtime = new(new LauncherRuntimeDependencies
        {
            LoadSettings = () => client.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = () => "v1.1.0-test"
        });

        Task<LauncherSessionRestoreResult> restore = runtime.InitializeAsync();
        runtime.Dispose();
        LauncherSessionRestoreResult result = await restore;
        Equal(LauncherSessionRestoreStatus.Cancelled, result.Status, "La fermeture doit interrompre la restauration.");
        Equal(1, authentication.RestoreCalls, "La restauration annulée ne doit pas redémarrer.");
        Equal(1, authentication.DisposeCalls, "La fermeture doit libérer l'authentification une fois.");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; actuel={actual}.");
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

internal sealed class TemporaryClient : IDisposable
{
    internal TemporaryClient()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "AtlasRuntimeComposition",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Settings = new LauncherSettings
        {
            InstallPath = Root,
            ManifestUrl = LauncherSettings.GetDefaultManifestUrl(),
            GameLocale = "frFR",
            AutomaticLauncherUpdates = false,
            CloseLauncherOnGameStart = false
        };
    }

    internal string Root { get; }

    internal LauncherSettings Settings { get; }

    internal void CreatePlayableFiles()
    {
        string classicDirectory = Path.Combine(Root, GameInstallServices.ClassicDirectoryName);
        Directory.CreateDirectory(classicDirectory);
        File.WriteAllBytes(Path.Combine(Root, GameInstallServices.GameLauncherFileName), []);
        File.WriteAllBytes(Path.Combine(Root, GameInstallServices.GameExecutableRelativePath), []);
    }

    internal void WriteVersionMarker(string version)
    {
        File.WriteAllText(
            Path.Combine(Root, GameInstallServices.ClientMarkerFileName),
            JsonSerializer.Serialize(new { clientVersion = version }));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
