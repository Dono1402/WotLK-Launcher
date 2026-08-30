using System.Reflection;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

internal static class LauncherRuntimeHardeningTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeLegacyInstalledVersionSemantics();
        CharacterizeLocalStateInvariant();
        await CharacterizeUnexpectedRestoreFailureAsync(playableClient: true);
        await CharacterizeUnexpectedRestoreFailureAsync(playableClient: false);
        await CharacterizeIgnoredCancellationCompletingAfterCloseAsync();
        await CharacterizeIgnoredCancellationFailingAfterCloseAsync();
        await CharacterizeRestoreDoesNotDependOnUiContextAsync();
        await CharacterizeFailingLogSinkAsync();
        Console.WriteLine("Launcher runtime hardening OK (02A.1).");
        return 0;
    }

    private static void CharacterizeLegacyInstalledVersionSemantics()
    {
        using TemporaryClient client = new();
        GameClientStateReader reader = new();
        string markerPath = Path.Combine(client.Root, GameInstallServices.ClientMarkerFileName);

        WriteMarker(markerPath, new { clientVersion = "3.4.3.54261" });
        Equal("3.4.3.54261", reader.ReadInstalledVersion(client.Root), "Une version normale doit être conservée.");

        WriteMarker(markerPath, new { clientVersion = " 3.4.3.54261 " });
        Equal(" 3.4.3.54261 ", reader.ReadInstalledVersion(client.Root), "Les espaces autour de la version doivent être conservés.");

        WriteMarker(markerPath, new { clientVersion = string.Empty });
        Equal(string.Empty, reader.ReadInstalledVersion(client.Root), "Une chaîne vide doit rester vide.");

        WriteMarker(markerPath, new { clientVersion = "   " });
        Equal("   ", reader.ReadInstalledVersion(client.Root), "Une chaîne composée d'espaces doit être conservée.");

        WriteMarker(markerPath, new { clientVersion = (string?)null });
        Equal<string?>(null, reader.ReadInstalledVersion(client.Root), "Une valeur JSON null doit produire null.");

        WriteMarker(markerPath, new { anotherProperty = "3.4.3" });
        Equal<string?>(null, reader.ReadInstalledVersion(client.Root), "Une propriété absente doit produire null.");

        File.WriteAllText(markerPath, "{invalid-json");
        Equal<string?>(null, reader.ReadInstalledVersion(client.Root), "Un JSON invalide doit produire null.");

        WriteMarker(markerPath, new { clientVersion = 30403 });
        Throws<InvalidOperationException>(
            () => reader.ReadInstalledVersion(client.Root),
            "Une propriété non textuelle doit conserver l'exception legacy de JsonElement.GetString.");

        File.Delete(markerPath);
        Equal<string?>(null, reader.ReadInstalledVersion(client.Root), "Un fichier absent doit produire null.");
    }

    private static void CharacterizeLocalStateInvariant()
    {
        GameClientLocalState missing = new(
            "C:\\AtlasMissing",
            "frFR",
            false,
            null,
            GameUpdateKnowledge.Unknown);
        Equal(GameAction.Install, missing.Action, "Un client absent doit toujours dériver Install.");

        GameClientLocalState playable = new(
            "C:\\AtlasPlayable",
            "frFR",
            true,
            "3.4.3",
            GameUpdateKnowledge.Unknown);
        Equal(GameAction.Play, playable.Action, "Un client jouable doit toujours dériver Play.");

        GameClientLocalState unavailable = playable with
        {
            UpdateKnowledge = GameUpdateKnowledge.Unavailable
        };
        Equal(GameAction.Play, unavailable.Action, "La connaissance distante doit rester orthogonale à Play.");

        GameClientLocalState removed = playable with { IsPlayable = false };
        Equal(GameAction.Install, removed.Action, "Une copie devenue injouable doit automatiquement dériver Install.");

        PropertyInfo actionProperty = typeof(GameClientLocalState).GetProperty(
                nameof(GameClientLocalState.Action),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("La propriété Action est absente.");
        True(actionProperty.SetMethod is null, "Action ne doit avoir aucun setter, même init-only.");
        True(
            typeof(GameClientLocalState)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .All(parameter => parameter.ParameterType != typeof(GameAction)),
            "Aucun constructeur local ne doit accepter un GameAction contradictoire.");
    }

    private static async Task CharacterizeUnexpectedRestoreFailureAsync(bool playableClient)
    {
        const string secret = "secret-access-token-must-not-be-logged";
        using TemporaryClient client = new();
        if (playableClient)
        {
            client.CreatePlayableFiles();
            client.WriteVersionMarker("3.4.3.54261");
        }

        List<string> logs = [];
        FakeLauncherAuthService authentication = new()
        {
            RestoreHandler = _ => Task.FromException<bool>(new InvalidOperationException(secret)),
            Session = FakeLauncherAuthService.CreateSession()
        };
        LauncherRuntime runtime = CreateRuntime(client, authentication, logs.Add);
        ShellUiState shell = LauncherV2RuntimePresentation.CreateShell(runtime);
        GameUiState gameBeforeRestore = LauncherV2RuntimePresentation.CreateGame(runtime.LocalClient);

        Task<LauncherSessionRestoreResult> firstTask = runtime.InitializeAsync();
        LauncherSessionRestoreResult first = await firstTask;
        LauncherSessionRestoreResult second = await runtime.InitializeAsync();

        Equal(LauncherSessionRestoreStatus.Unavailable, first.Status, "Une exception inattendue doit devenir Unavailable.");
        Equal(LauncherSessionRestoreStatus.Unavailable, second.Status, "Le résultat observé doit être réutilisé.");
        True(firstTask.IsCompletedSuccessfully, "La tâche de restauration ne doit jamais rester fautée.");
        Equal(1, authentication.RestoreCalls, "Une exception ne doit pas déclencher une seconde restauration.");
        Equal(1, logs.Count, "L'exception inattendue doit être journalisée une seule fois.");
        True(logs[0].Contains(nameof(InvalidOperationException), StringComparison.Ordinal), "Le type d'erreur doit être journalisé.");
        True(!logs[0].Contains(secret, StringComparison.Ordinal), "Le message potentiellement sensible ne doit pas être journalisé.");
        True(!logs[0].Contains("access-token", StringComparison.OrdinalIgnoreCase), "Aucun token ne doit apparaître dans le journal.");

        LauncherV2RuntimePresentation.ApplySession(shell, first);
        Equal("Compte", shell.Username, "Une restauration indisponible ne doit pas modifier l'identité WPF.");
        GameUiState gameAfterRestore = LauncherV2RuntimePresentation.CreateGame(runtime.LocalClient);
        Equal(gameBeforeRestore.ClientStatus, gameAfterRestore.ClientStatus, "L'échec d'authentification ne doit pas modifier le client local.");
        Equal(gameBeforeRestore.InstallBadgeText, gameAfterRestore.InstallBadgeText, "L'échec d'authentification ne doit pas modifier la connaissance de mise à jour.");
        Equal(
            playableClient ? "Client prêt" : "Client non installé",
            gameAfterRestore.ClientStatus,
            "Le statut local doit rester affichable après l'échec de restauration.");

        runtime.Dispose();
        runtime.Dispose();
        Equal(1, authentication.DisposeCalls, "Dispose doit rester idempotent.");
        LauncherSessionRestoreResult afterDispose = await runtime.InitializeAsync();
        Equal(LauncherSessionRestoreStatus.Cancelled, afterDispose.Status, "Un runtime disposé ne doit pas être réutilisé.");
        Equal(1, authentication.RestoreCalls, "Initialize après Dispose ne doit pas rappeler l'authentification.");
    }

    private static async Task CharacterizeIgnoredCancellationCompletingAfterCloseAsync()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = new()
        {
            RestoreHandler = _ => completion.Task,
            Session = FakeLauncherAuthService.CreateSession()
        };
        List<string> logs = [];
        LauncherRuntime runtime = CreateRuntime(client, authentication, logs.Add);
        ShellUiState shell = LauncherV2RuntimePresentation.CreateShell(runtime);

        Task<LauncherSessionRestoreResult> restore = runtime.InitializeAsync();
        runtime.Dispose();
        runtime.Dispose();
        completion.SetResult(true);
        LauncherSessionRestoreResult result = await restore;

        Equal(LauncherSessionRestoreStatus.Cancelled, result.Status, "Un succès tardif après fermeture doit devenir Cancelled.");
        True(restore.IsCompletedSuccessfully, "Le succès tardif doit rester observé.");
        Equal(1, authentication.RestoreCalls, "La fermeture ne doit pas relancer la restauration.");
        Equal(1, authentication.DisposeCalls, "La fermeture doit libérer l'authentification une fois.");
        Equal(0, logs.Count, "Un succès tardif annulé ne doit pas créer une fausse erreur.");
        LauncherV2RuntimePresentation.ApplySession(shell, result);
        Equal("Compte", shell.Username, "Un succès tardif ne doit pas modifier WPF après fermeture.");
        Equal(LauncherSessionRestoreStatus.Cancelled, (await runtime.InitializeAsync()).Status, "Le runtime fermé doit rester inutilisable.");
        Equal(1, authentication.RestoreCalls, "Aucune seconde restauration ne doit être tentée.");
    }

    private static async Task CharacterizeIgnoredCancellationFailingAfterCloseAsync()
    {
        const string secret = "refresh-token-secret-after-close";
        using TemporaryClient client = new();
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = new()
        {
            RestoreHandler = _ => completion.Task,
            Session = FakeLauncherAuthService.CreateSession()
        };
        List<string> logs = [];
        LauncherRuntime runtime = CreateRuntime(client, authentication, logs.Add);
        ShellUiState shell = LauncherV2RuntimePresentation.CreateShell(runtime);

        Task<LauncherSessionRestoreResult> restore = runtime.InitializeAsync();
        runtime.Dispose();
        completion.SetException(new InvalidOperationException(secret));
        LauncherSessionRestoreResult result = await restore;

        Equal(LauncherSessionRestoreStatus.Cancelled, result.Status, "Une exception tardive après fermeture doit devenir Cancelled.");
        True(restore.IsCompletedSuccessfully, "L'exception tardive doit être observée et convertie en résultat.");
        Equal(1, logs.Count, "L'exception tardive doit être journalisée une fois.");
        True(logs[0].Contains(nameof(InvalidOperationException), StringComparison.Ordinal), "Le journal doit identifier le type d'erreur tardive.");
        True(!logs[0].Contains(secret, StringComparison.Ordinal), "Le journal tardif ne doit pas contenir le secret.");
        LauncherV2RuntimePresentation.ApplySession(shell, result);
        Equal("Compte", shell.Username, "Une exception tardive ne doit pas modifier WPF.");
        Equal(1, authentication.RestoreCalls, "Une exception tardive ne doit pas relancer la restauration.");
    }

    private static async Task CharacterizeFailingLogSinkAsync()
    {
        using TemporaryClient client = new();
        FakeLauncherAuthService authentication = new()
        {
            RestoreHandler = _ => Task.FromException<bool>(new InvalidOperationException("sensitive"))
        };
        LauncherRuntime runtime = CreateRuntime(
            client,
            authentication,
            _ => throw new IOException("log unavailable"));
        try
        {
            Task<LauncherSessionRestoreResult> restore = runtime.InitializeAsync();
            LauncherSessionRestoreResult result = await restore;
            Equal(LauncherSessionRestoreStatus.Unavailable, result.Status, "Un journal indisponible ne doit pas masquer le résultat.");
            True(restore.IsCompletedSuccessfully, "Une panne de journal ne doit pas faire fauter la restauration.");
        }
        finally
        {
            runtime.Dispose();
        }
    }

    private static async Task CharacterizeRestoreDoesNotDependOnUiContextAsync()
    {
        using TemporaryClient client = new();
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = new()
        {
            RestoreHandler = _ => completion.Task
        };
        LauncherRuntime runtime = CreateRuntime(client, authentication, _ => { });
        RecordingSynchronizationContext uiContext = new();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task<LauncherSessionRestoreResult> restore;
        try
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            restore = runtime.InitializeAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        completion.SetException(new InvalidOperationException("late failure"));
        LauncherSessionRestoreResult result = await restore.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(LauncherSessionRestoreStatus.Unavailable, result.Status, "La restauration tardive doit terminer hors du contexte UI.");
        Equal(0, uiContext.PostCalls, "Le coordinateur ne doit pas republier sa continuation sur un Dispatcher fermé.");
        runtime.Dispose();
    }

    private static LauncherRuntime CreateRuntime(
        TemporaryClient client,
        FakeLauncherAuthService authentication,
        Action<string> writeLog)
    {
        return new LauncherRuntime(new LauncherRuntimeDependencies
        {
            LoadSettings = () => client.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = () => "v1.1.0-test",
            WriteRuntimeLog = writeLog
        });
    }

    private static void WriteMarker(string path, object marker)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(marker));
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

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        internal int PostCalls { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCalls++;
        }
    }
}
