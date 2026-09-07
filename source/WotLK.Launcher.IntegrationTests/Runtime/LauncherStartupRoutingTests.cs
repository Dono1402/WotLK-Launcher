using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;

internal static class LauncherStartupRoutingTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeRouting();
        CharacterizeExclusiveWindowDispatch();
        CharacterizeSingleInstanceRoutes();
        CharacterizeAutomaticStartup();
        EnforceSingleInstanceGate();
        CharacterizeSingleRuntimeComposition();
        await LauncherAutostartWpfTests.RunAsync();
        Console.WriteLine(
            "Routage de démarrage OK (05A.1 : V2 par défaut, fallback legacy, previews isolées, fenêtre/composition uniques).");
        return 0;
    }

    private static void CharacterizeRouting()
    {
        Equal(
            LauncherStartupMode.UiV2,
            App.ResolveStartupMode([]),
            "Le lancement sans argument doit sélectionner la V2 réelle.");
        Equal(
            LauncherStartupMode.Legacy,
            App.ResolveStartupMode(["--legacy"]),
            "--legacy doit sélectionner explicitement l'ancienne interface.");
        Equal(
            LauncherStartupMode.Legacy,
            App.ResolveStartupMode(["--LEGACY"]),
            "Le fallback legacy doit être insensible à la casse.");
        Equal(
            LauncherStartupMode.UiV2,
            App.ResolveStartupMode(["--ui-v2"]),
            "--ui-v2 doit rester un alias de compatibilité vers la V2 réelle.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--legacy", "--ui-v2"]),
            "Les deux routes interactives explicites ne doivent pas être composées ensemble.");
        True(
            !App.UsesV2Window(LauncherStartupMode.Legacy),
            "Le fallback legacy ne doit pas charger les ressources V2.");
        True(
            App.UsesV2Window(LauncherStartupMode.UiV2),
            "La route réelle V2 doit charger ses ressources.");

        Equal(
            LauncherStartupMode.UiV2Preview,
            App.ResolveStartupMode(["--ui-v2", "--preview-state=Ready"]),
            "Le preview Jeu historique doit conserver sa route isolée.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-state=Ready"]),
            "Un preview Jeu sans --ui-v2 doit être refusé avant toute composition réelle.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-auth=login"]),
            "Un preview dédié sans --ui-v2 doit rester refusé avant composition.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode([
                "--ui-v2",
                "--preview-auth=login",
                "--preview-friends=populated"
            ]),
            "Deux previews dédiés ne doivent jamais être composés ensemble.");

        (string Argument, LauncherStartupMode Mode)[] previews =
        [
            ("--preview-state=Ready", LauncherStartupMode.UiV2Preview),
            ("--preview-auth=login", LauncherStartupMode.UiV2AuthPreview),
            ("--preview-profile=signed-in", LauncherStartupMode.UiV2ProfilePreview),
            ("--preview-settings=game", LauncherStartupMode.UiV2SettingsPreview),
            ("--preview-friends=populated", LauncherStartupMode.UiV2FriendsPreview),
            ("--preview-account=profile", LauncherStartupMode.UiV2AccountPreview),
            ("--preview-addons=default", LauncherStartupMode.UiV2AddonsPreview),
            ("--preview-activity=idle", LauncherStartupMode.UiV2ActivityPreview)
        ];
        foreach ((string argument, LauncherStartupMode expectedMode) in previews)
        {
            Equal(
                expectedMode,
                App.ResolveStartupMode(["--ui-v2", argument]),
                $"La route de {argument} a changé.");
            True(
                App.UsesV2Window(expectedMode),
                $"La route de {argument} doit charger uniquement les ressources V2 nécessaires.");
        }

        Equal(
            LauncherStartupMode.GrantGameDirectoryAccess,
            App.ResolveStartupMode(["--grant-game-access", "C:\\Atlas", "S-1-5-18"]),
            "Le helper d'accès au dossier doit rester hors des routes interactives.");
        Equal(
            LauncherStartupMode.UninstallGame,
            App.ResolveStartupMode(["--uninstall-game"]),
            "La désinstallation doit rester hors des routes interactives.");
    }

    private static void CharacterizeExclusiveWindowDispatch()
    {
        AssertExclusiveDispatch([], legacyWindows: 0, runtimeV2Windows: 1, previewWindows: 0);
        AssertExclusiveDispatch(["--ui-v2"], legacyWindows: 0, runtimeV2Windows: 1, previewWindows: 0);
        AssertExclusiveDispatch(["--legacy"], legacyWindows: 1, runtimeV2Windows: 0, previewWindows: 0);
        AssertExclusiveDispatch(
            ["--ui-v2", "--preview-state=Ready"],
            legacyWindows: 0,
            runtimeV2Windows: 0,
            previewWindows: 1);
        AssertExclusiveDispatch(
            ["--ui-v2", "--preview-account=profile"],
            legacyWindows: 0,
            runtimeV2Windows: 0,
            previewWindows: 1);

        int calls = 0;
        Throws<ArgumentOutOfRangeException>(
            () => App.DispatchInteractiveStartup(
                LauncherStartupMode.InvalidArguments,
                () => calls++,
                () => calls++,
                _ => calls++),
            "Un mode non interactif ne doit créer aucune fenêtre.");
        Equal(0, calls, "Le dispatch invalide a invoqué une fabrique de fenêtre.");
    }

    private static void CharacterizeSingleInstanceRoutes()
    {
        True(App.UsesSingleInstance(LauncherStartupMode.UiV2),
            "La V2 réelle doit participer au verrou d'instance.");
        True(App.UsesSingleInstance(LauncherStartupMode.Legacy),
            "Le fallback legacy doit partager le verrou du launcher officiel.");
        True(!App.UsesSingleInstance(LauncherStartupMode.UiV2Preview),
            "Les previews isolées ne doivent pas bloquer le launcher local de test.");
        True(!App.UsesSingleInstance(LauncherStartupMode.GrantGameDirectoryAccess),
            "Un helper élevé ne doit jamais être pris pour une seconde interface.");
    }

    private static void EnforceSingleInstanceGate()
    {
        string identity = "AtlasLauncher.Tests." + Guid.NewGuid().ToString("N");
        True(LauncherSingleInstanceGate.TryAcquire(identity, out LauncherSingleInstanceGate? first),
            "La première instance doit acquérir le verrou immédiatement.");
        using (LauncherSingleInstanceGate acquired = first!)
        {
            using ManualResetEventSlim activation = new(initialState: false);
            acquired.ActivationRequested += (_, _) => activation.Set();

            True(!LauncherSingleInstanceGate.TryAcquire(identity, out LauncherSingleInstanceGate? second),
                "Une seconde instance doit être refusée immédiatement.");
            True(second is null, "Une instance refusée ne doit posséder aucun verrou.");
            True(!LauncherSingleInstanceGate.SignalExisting(identity, activateExisting: false),
                "Une relance automatique ne doit envoyer aucune activation à l'instance existante.");
            True(!activation.Wait(TimeSpan.FromMilliseconds(100)),
                "L'instance existante doit rester discrète lors du démarrage Windows.");
            True(LauncherSingleInstanceGate.SignalExisting(identity),
                "La seconde instance doit pouvoir réveiller la première.");
            True(activation.Wait(TimeSpan.FromSeconds(2)),
                "La première instance doit recevoir la demande d'activation.");
        }

        True(LauncherSingleInstanceGate.TryAcquire(identity, out LauncherSingleInstanceGate? reopened),
            "Le verrou doit être libéré à la fermeture réelle du launcher.");
        reopened!.Dispose();
        reopened.Dispose();
    }

    private static void CharacterizeAutomaticStartup()
    {
        foreach (string[] arguments in new[]
        {
            new[] { "--autostart" },
            new[] { "--AUTOSTART", "--ui-v2" },
            new[] { "--legacy", "--autostart" }
        })
        {
            True(App.ShouldStartMinimized(App.ResolveStartupMode(arguments), arguments),
                "Le démarrage Windows doit réduire uniquement la fenêtre runtime demandée.");
        }

        foreach (string[] arguments in new[]
        {
            Array.Empty<string>(),
            new[] { "--ui-v2" },
            new[] { "--legacy" },
            new[] { "--autostart=false" },
            new[] { "--ui-v2", "--preview-state=Ready", "--autostart" },
            new[] { "--grant-game-access", "--autostart" },
            new[] { "--uninstall-game", "--autostart" },
            new[] { "--legacy", "--ui-v2", "--autostart" }
        })
        {
            True(!App.ShouldStartMinimized(App.ResolveStartupMode(arguments), arguments),
                "Les lancements manuels, previews, helpers et routes invalides doivent garder leur comportement.");
        }
    }

    private static void CharacterizeSingleRuntimeComposition()
    {
        using TemporaryClient client = new();
        FakeLauncherAuthService authentication = new();
        int runtimeCompositions = 0;
        int v2Windows = 0;
        int settingsLoads = 0;
        int authenticationCreations = 0;
        int selfUpdateTimerCreations = 0;
        int selfUpdateClientCreations = 0;
        RuntimeCompositionSelfUpdateTimer selfUpdateTimer = new(
            LauncherSelfUpdateCoordinator.CheckInterval);
        LauncherRuntime? runtime = null;

        App.DispatchInteractiveStartup(
            App.ResolveStartupMode([]),
            () => throw new InvalidOperationException(
                "Le démarrage normal ne doit pas construire de fenêtre legacy."),
            () =>
            {
                runtimeCompositions++;
                runtime = new LauncherRuntime(new LauncherRuntimeDependencies
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
                    GetLauncherVersion = () => "v1.1.2-routing-test",
                    CreateLauncherSelfUpdateTimer = interval =>
                    {
                        selfUpdateTimerCreations++;
                        Equal(
                            LauncherSelfUpdateCoordinator.CheckInterval,
                            interval,
                            "La composition doit conserver l'unique cadence self-update.");
                        return selfUpdateTimer;
                    },
                    CreateLauncherSelfUpdateClient = _ =>
                    {
                        selfUpdateClientCreations++;
                        return new RuntimeCompositionSelfUpdateClient();
                    },
                    LauncherSelfUpdateFinalizer = new RuntimeCompositionSelfUpdateFinalizer(),
                    GetAvatarCacheRoot = () => Path.Combine(client.Root, "avatar-cache"),
                    RequestApplicationShutdown = static () =>
                        throw new InvalidOperationException(
                            "Le test de composition ne doit pas arrêter l'application.")
                });
                v2Windows++;
            },
            _ => throw new InvalidOperationException(
                "Le démarrage normal ne doit pas construire de preview."));

        try
        {
            Equal(1, v2Windows, "Le démarrage normal doit créer une seule LauncherShellV2.");
            Equal(1, runtimeCompositions, "Le démarrage normal doit créer un seul LauncherRuntime.");
            Equal(1, settingsLoads, "La composition unique doit charger les paramètres une seule fois.");
            Equal(1, authenticationCreations, "La composition unique doit créer l'authentification une seule fois.");
            Equal(1, selfUpdateTimerCreations, "La composition unique doit créer un seul timer self-update.");
            Equal(1, selfUpdateClientCreations, "La composition unique doit créer un seul client self-update.");

            LauncherRuntime actual = runtime
                ?? throw new InvalidOperationException("La composition V2 réelle est absente.");
            object[] firstRead =
            [
                actual.Session,
                actual.Game,
                actual.Addons,
                actual.Activity,
                actual.Account,
                actual.Friends,
                actual.SettingsRuntime,
                actual.SelfUpdate
            ];
            object[] secondRead =
            [
                actual.Session,
                actual.Game,
                actual.Addons,
                actual.Activity,
                actual.Account,
                actual.Friends,
                actual.SettingsRuntime,
                actual.SelfUpdate
            ];
            for (int index = 0; index < firstRead.Length; index++)
            {
                True(
                    ReferenceEquals(firstRead[index], secondRead[index]),
                    $"Le service runtime #{index + 1} a été recomposé entre deux lectures.");
            }
        }
        finally
        {
            runtime?.Dispose();
        }

        Equal(1, authentication.DisposeCalls, "L'authentification unique doit être libérée une seule fois.");
        Equal(1, selfUpdateTimer.StopCalls, "Le timer self-update unique doit être arrêté une seule fois.");
    }

    private static void AssertExclusiveDispatch(
        string[] arguments,
        int legacyWindows,
        int runtimeV2Windows,
        int previewWindows)
    {
        int actualLegacyWindows = 0;
        int actualRuntimeV2Windows = 0;
        int actualPreviewWindows = 0;
        int runtimeCompositions = 0;
        LauncherStartupMode mode = App.ResolveStartupMode(arguments);

        App.DispatchInteractiveStartup(
            mode,
            () => actualLegacyWindows++,
            () =>
            {
                actualRuntimeV2Windows++;
                runtimeCompositions++;
            },
            previewMode =>
            {
                True(
                    previewMode is not LauncherStartupMode.UiV2,
                    "Une route preview ne doit pas recevoir le mode runtime réel.");
                actualPreviewWindows++;
            });

        Equal(legacyWindows, actualLegacyWindows, "Nombre de fenêtres legacy incorrect.");
        Equal(runtimeV2Windows, actualRuntimeV2Windows, "Nombre de fenêtres V2 réelles incorrect.");
        Equal(previewWindows, actualPreviewWindows, "Nombre de fenêtres preview incorrect.");
        Equal(
            runtimeV2Windows,
            runtimeCompositions,
            "Chaque fenêtre V2 réelle doit correspondre à une et une seule composition runtime.");
        Equal(
            1,
            actualLegacyWindows + actualRuntimeV2Windows + actualPreviewWindows,
            "Une route interactive doit créer exactement une fenêtre.");
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
}
