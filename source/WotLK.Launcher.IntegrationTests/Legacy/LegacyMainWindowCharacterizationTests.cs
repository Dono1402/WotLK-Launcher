using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Game;

internal static class LegacyMainWindowCharacterizationTests
{
    internal static Task<int> RunAsync()
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunOnDispatcher(completion))
        {
            IsBackground = true,
            Name = "Atlas legacy WPF characterization"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunOnDispatcher(TaskCompletionSource<int> completion)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunAllAsync();
                Console.WriteLine("Legacy launcher characterization OK (02A.0).");
                completion.SetResult(0);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
    }

    private static async Task RunAllAsync()
    {
        CharacterizeStartupRouting();
        CharacterizeConstructorOrderAndSingleInitialization();
        CharacterizeLocalShellPaths();
        CharacterizeLocalGameActionsAndPrimaryButtons();
        await CharacterizeRestoreBeforeInitialAnalysisAsync();
        await CharacterizeTimerResponsibilitiesAsync();
        await CharacterizeLegacyCompatibilityMatrixAsync();
        await CharacterizeCloseDuringOperationAsync();
    }

    private static void CharacterizeLocalShellPaths()
    {
        using LegacyTestEnvironment environment = new(initialPlayableClient: true);
        MainWindow window = new(environment.CreateDependencies());

        LegacyLocalPathSnapshot paths = window.CaptureLocalPathCharacterization();
        Equal(
            environment.Settings.InstallPath,
            paths.InstallPath,
            "Dossier doit continuer à prendre le chemin actif des paramètres legacy.");
        Equal(
            Path.Combine(LauncherSettings.SettingsDirectory, "launcher.log"),
            paths.LauncherLogPath,
            "Logs doit conserver exactement la convention historique du fichier launcher.log.");
        Equal(
            LauncherSettings.LauncherLogPath,
            paths.LauncherLogPath,
            "La propriété centralisée doit rester identique au helper legacy.");

        window.Close();
    }

    private static void CharacterizeStartupRouting()
    {
        Equal(
            LauncherStartupMode.Legacy,
            App.ResolveStartupMode([]),
            "Sans argument, le launcher doit sélectionner uniquement la fenêtre legacy.");
        Equal(
            LauncherStartupMode.UiV2,
            App.ResolveStartupMode(["--ui-v2"]),
            "--ui-v2 doit sélectionner uniquement la composition V2 réelle.");
        Equal(
            LauncherStartupMode.UiV2Preview,
            App.ResolveStartupMode(["--ui-v2", "--preview-state=Ready"]),
            "Un état preview explicite doit isoler les données fictives de la V2 réelle.");
        Equal(
            LauncherStartupMode.Legacy,
            App.ResolveStartupMode(["--preview-state=Ready"]),
            "Un argument preview isolé ne doit jamais activer la V2.");
        Equal(
            LauncherStartupMode.GrantGameDirectoryAccess,
            App.ResolveStartupMode(["--grant-game-access", "C:\\Atlas", "S-1-5-18"]),
            "Le mode d'élévation doit rester distinct du démarrage legacy.");
        Equal(
            LauncherStartupMode.UninstallGame,
            App.ResolveStartupMode(["--uninstall-game"]),
            "Le mode de désinstallation doit rester distinct du démarrage legacy.");
    }

    private static void CharacterizeConstructorOrderAndSingleInitialization()
    {
        using LegacyTestEnvironment environment = new(initialPlayableClient: false);
        MainWindow window = new(environment.CreateDependencies());

        Equal(1, environment.AuthenticationFactoryCalls, "Une seule authentification doit être créée.");
        Equal(1, environment.HttpFactoryCalls, "Un seul client HTTP autorisé doit être créé.");
        Equal(1, environment.SettingsLoadCalls, "Les paramètres doivent être chargés une fois.");
        Equal(1, environment.SettingsSaveCalls, "Les paramètres chargés doivent être sauvegardés une fois.");
        Equal(1, environment.PrepareDirectoryCalls, "La préparation du dossier doit être appelée une fois.");
        Equal(3, environment.Timers.Count, "Le constructeur legacy doit créer exactement trois timers.");

        AssertOrdered(
            environment.Observer.Events,
            LegacyStartupEvent.ComponentsInitialized,
            LegacyStartupEvent.AuthenticationCreated,
            LegacyStartupEvent.AuthorizedHttpClientCreated,
            LegacyStartupEvent.SettingsLoaded,
            LegacyStartupEvent.SettingsSaved,
            LegacyStartupEvent.GameDirectoryPrepared,
            LegacyStartupEvent.LauncherUpdateTimerCreated,
            LegacyStartupEvent.FriendRefreshTimerCreated,
            LegacyStartupEvent.ToastTimerCreated,
            LegacyStartupEvent.InitialGameActionSet,
            LegacyStartupEvent.GamePageSelected,
            LegacyStartupEvent.LoadedSubscribed,
            LegacyStartupEvent.FriendRefreshTimerStarted);
        Equal(
            1,
            environment.Observer.Count(LegacyStartupEvent.LoadedSubscribed),
            "Loaded ne doit être abonné qu'une fois.");

        FakeLegacyTimer launcherTimer = environment.TimerAt(TimeSpan.FromSeconds(30));
        FakeLegacyTimer friendTimer = environment.TimerAt(TimeSpan.FromSeconds(15));
        FakeLegacyTimer toastTimer = environment.TimerAt(TimeSpan.FromSeconds(8));
        Equal(0, launcherTimer.StartCalls, "Le timer 30 s reste arrêté si l'auto-update est désactivé.");
        Equal(1, friendTimer.StartCalls, "Le timer amis 15 s doit démarrer une seule fois.");
        Equal(0, toastTimer.StartCalls, "Le timer toast démarre uniquement à l'affichage d'un toast.");
        Equal(1, launcherTimer.SubscriptionAdds, "Le timer launcher doit avoir un seul handler.");
        Equal(1, friendTimer.SubscriptionAdds, "Le timer amis doit avoir un seul handler.");

        LegacyMainWindowSnapshot snapshot = window.CaptureCharacterizationSnapshot();
        Equal(GameAction.Install, snapshot.GameAction, "Un client absent doit sélectionner Install.");
        Equal("INSTALLER", snapshot.UpdateButtonLabel, "Le bouton principal legacy doit afficher INSTALLER.");
        Equal(0d, snapshot.Progress, "Un client absent doit conserver une progression nulle.");

        window.Close();
        Equal(1, environment.Authentication.DisposeCalls, "L'authentification doit être libérée une fois.");
        Equal(1, friendTimer.StopCalls, "Le timer amis doit être arrêté à la fermeture.");
        Equal(1, friendTimer.SubscriptionRemoves, "Le handler amis doit être retiré à la fermeture.");
    }

    private static void CharacterizeLocalGameActionsAndPrimaryButtons()
    {
        using LegacyTestEnvironment environment = new(initialPlayableClient: true);
        MainWindow window = new(environment.CreateDependencies());

        LegacyMainWindowSnapshot snapshot = window.CaptureCharacterizationSnapshot();
        Equal(GameAction.Play, snapshot.GameAction, "Un client local jouable doit sélectionner Play.");
        Equal("JOUER", snapshot.UpdateButtonLabel, "Play doit afficher JOUER.");
        Equal("JOUER", snapshot.HomeButtonLabel, "Les deux boutons doivent partager le même libellé.");
        Equal("Prêt à jouer", snapshot.HomeClientStatus, "Le statut local Play doit rester celui de la v1.1.0.");
        Equal(100d, snapshot.Progress, "Le client local jouable est présenté à 100 % au démarrage legacy.");
        Equal(
            "Client à jour",
            snapshot.ProgressText,
            "02A.0 fige le libellé legacy, même avant la future séparation de l'état de connaissance.");

        window.SetGameActionForCharacterization(GameAction.Update);
        snapshot = window.CaptureCharacterizationSnapshot();
        Equal("METTRE A JOUR", snapshot.UpdateButtonLabel, "Update doit afficher METTRE A JOUR.");
        Equal("Mise à jour disponible", snapshot.HomeClientStatus, "Update doit exposer son statut legacy.");

        window.SetGameActionForCharacterization(GameAction.Install);
        snapshot = window.CaptureCharacterizationSnapshot();
        Equal("INSTALLER", snapshot.UpdateButtonLabel, "Install doit afficher INSTALLER.");
        Equal("Installation requise", snapshot.HomeClientStatus, "Install doit exposer son statut legacy.");

        window.SetGameActionForCharacterization(GameAction.Update);
        window.SetBusyForCharacterization(true);
        snapshot = window.CaptureCharacterizationSnapshot();
        True(snapshot.UpdateButtonEnabled, "Le bouton principal reste actif pour annuler l'opération legacy.");
        Equal("ANNULER", snapshot.UpdateButtonLabel, "Le bouton principal devient ANNULER pendant une opération.");
        True(!snapshot.HomeButtonEnabled, "Le bouton Jouer secondaire est désactivé pendant une opération.");
        Equal("ANNULER", snapshot.HomeButtonLabel, "Le bouton accueil conserve le libellé ANNULER historique.");
        True(!snapshot.AddonsNavigationEnabled, "La navigation Addons est bloquée pendant une opération.");
        True(!snapshot.LauncherSelfUpdateEnabled, "L'auto-update manuel est bloqué pendant une opération.");
        True(snapshot.VerifyButtonEnabled, "Le bouton Vérifier reste visuellement actif, son handler refusant l'opération.");

        window.SetBusyForCharacterization(false);
        snapshot = window.CaptureCharacterizationSnapshot();
        Equal("METTRE A JOUR", snapshot.UpdateButtonLabel, "La sortie de busy restaure le GameAction autoritaire.");
        True(snapshot.HomeButtonEnabled, "La sortie de busy réactive le bouton Jouer.");
        window.Close();
    }

    private static async Task CharacterizeRestoreBeforeInitialAnalysisAsync()
    {
        using LegacyTestEnvironment environment = new(initialPlayableClient: true);
        environment.CreatePlayableClientFiles();
        environment.Authentication.RestoreResult = true;
        environment.Authentication.Session = FakeLauncherAuthService.CreateSession();

        MainWindow window = new(environment.CreateDependencies());
        await window.RestoreSessionAndAnalyzeForCharacterizationAsync();

        Equal(1, environment.Authentication.RestoreCalls, "La restauration de session doit être tentée une fois.");
        Equal(1, environment.Http.ManifestRequests, "L'analyse initiale doit obtenir le manifeste une fois.");
        AssertOrdered(
            environment.Observer.Events,
            LegacyStartupEvent.SessionRestoreStarted,
            LegacyStartupEvent.SessionRestoreCompleted,
            LegacyStartupEvent.InitialRemoteAnalysisStarted,
            LegacyStartupEvent.InitialRemoteAnalysisCompleted);
        True(
            environment.Observer.IndexOf(LegacyStartupEvent.SessionRestoreCompleted)
                < environment.Http.FirstManifestRequestEventIndex,
            "La restauration doit finir avant la première requête de manifeste.");
        Equal(
            GameAction.Play,
            window.CaptureCharacterizationSnapshot().GameAction,
            "Une analyse distante sans changement doit conserver Play.");
        window.Close();
    }

    private static async Task CharacterizeTimerResponsibilitiesAsync()
    {
        using LegacyTestEnvironment environment = new(
            initialPlayableClient: true,
            automaticLauncherUpdates: true);
        environment.CreatePlayableClientFiles();
        environment.Authentication.Session = FakeLauncherAuthService.CreateSession();
        MainWindow window = new(environment.CreateDependencies());

        await WaitUntilAsync(
            () => environment.Http.LauncherUpdateRequests >= 1,
            "La vérification initiale de mise à jour launcher n'a pas été observée.");
        environment.ResetOperationCounters();

        environment.TimerAt(TimeSpan.FromSeconds(15)).RaiseTick();
        await WaitUntilAsync(
            () => environment.Authentication.GetFriendsCalls == 1,
            "Le timer 15 s n'a pas actualisé les amis.");
        Equal(0, environment.Http.LauncherUpdateRequests, "Le timer amis ne doit pas vérifier le launcher.");
        Equal(0, environment.Http.ManifestRequests, "Le timer amis ne doit pas analyser le client.");

        environment.ResetOperationCounters();
        environment.TimerAt(TimeSpan.FromSeconds(30)).RaiseTick();
        await WaitUntilAsync(
            () => environment.Http.LauncherUpdateRequests == 1
                && environment.Http.ManifestRequests == 1,
            "Le timer 30 s n'a pas exécuté ses deux responsabilités historiques.");
        Equal(1, environment.Authentication.EnsureFreshCalls, "Le timer 30 s doit rafraîchir la session avant l'analyse.");
        Equal(0, environment.Authentication.GetFriendsCalls, "Le timer 30 s ne doit pas actualiser les amis.");
        Equal(1, environment.Observer.Count(LegacyStartupEvent.LauncherUpdateTimerTick), "Un tick 30 s doit être unique.");
        Equal(1, environment.Observer.Count(LegacyStartupEvent.FriendRefreshTimerTick), "Un tick 15 s doit être unique.");
        window.Close();
    }

    private static async Task CharacterizeLegacyCompatibilityMatrixAsync()
    {
        TaskCompletionSource manifestRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> releaseManifest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using LegacyTestEnvironment environment = new(initialPlayableClient: true);
        environment.CreatePlayableClientFiles();
        environment.Http.ManifestResponder = (_, _) =>
        {
            manifestRequested.TrySetResult();
            return releaseManifest.Task;
        };

        MainWindow window = new(environment.CreateDependencies());
        Task verification = window.RefreshGameActionForCharacterizationAsync();
        await manifestRequested.Task;

        LegacyMainWindowSnapshot snapshot = window.CaptureCharacterizationSnapshot();
        True(snapshot.IsRefreshingGameAction, "La vérification doit signaler son exécution en cours.");
        True(snapshot.HomeButtonEnabled, "Legacy autorise Play pendant une vérification non mutante.");
        True(snapshot.AddonsNavigationEnabled, "Legacy ne verrouille pas encore Addons pendant la vérification.");
        True(snapshot.VerifyButtonEnabled, "Legacy laisse le contrôle Vérifier actif pendant l'analyse.");

        releaseManifest.SetResult(RecordingHttpHandler.JsonResponse(new LauncherManifest
        {
            Version = "characterization",
            BaseUrl = "https://atlas.test/client/",
            Files = []
        }));
        await verification;
        True(
            !window.CaptureCharacterizationSnapshot().IsRefreshingGameAction,
            "La vérification doit libérer son drapeau dans finally.");
        window.Close();
    }

    private static async Task CharacterizeCloseDuringOperationAsync()
    {
        using LegacyTestEnvironment environment = new(initialPlayableClient: false);
        MainWindow window = new(environment.CreateDependencies());
        CancellationToken operationToken = window.AttachActiveOperationForCharacterization();
        True(!operationToken.IsCancellationRequested, "L'opération témoin doit commencer active.");

        window.Close();

        True(operationToken.IsCancellationRequested, "La fermeture doit annuler l'opération globale legacy.");
        Equal(0, environment.Authentication.DisposeCalls, "La fermeture doit attendre la confirmation de fin du bail.");
        window.CompleteActiveOperationForCharacterization();
        await WaitUntilAsync(
            () => environment.Authentication.DisposeCalls == 1,
            "La fermeture différée n'a pas libéré l'authentification.");
        Equal(
            1,
            environment.Observer.Count(LegacyStartupEvent.OperationCancellationRequested),
            "La fermeture ne doit demander l'annulation qu'une fois.");
        Equal(1, environment.Authentication.DisposeCalls, "L'authentification doit être libérée à la fermeture.");
        True(environment.Timers.All(timer => timer.StopCalls == 1), "Tous les timers doivent être arrêtés une fois.");
        True(environment.Timers.All(timer => timer.SubscriptionRemoves == 1), "Tous les handlers timer doivent être retirés.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        True(condition(), failureMessage);
    }

    private static void AssertOrdered(
        IReadOnlyList<LegacyStartupEvent> actual,
        params LegacyStartupEvent[] expected)
    {
        int previousIndex = -1;
        foreach (LegacyStartupEvent startupEvent in expected)
        {
            int index = IndexOf(actual, startupEvent, previousIndex + 1);
            True(index >= 0, $"Événement de démarrage absent ou désordonné : {startupEvent}.");
            previousIndex = index;
        }
    }

    private static int IndexOf(
        IReadOnlyList<LegacyStartupEvent> values,
        LegacyStartupEvent expected,
        int startIndex)
    {
        for (int index = startIndex; index < values.Count; index++)
        {
            if (values[index] == expected)
            {
                return index;
            }
        }

        return -1;
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

internal sealed class LegacyTestEnvironment : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AtlasLegacyCharacterization",
        Guid.NewGuid().ToString("N"));
    private readonly bool _initialPlayableClient;

    internal LegacyTestEnvironment(
        bool initialPlayableClient,
        bool automaticLauncherUpdates = false)
    {
        _initialPlayableClient = initialPlayableClient;
        Directory.CreateDirectory(_root);
        Settings = new LauncherSettings
        {
            InstallPath = _root,
            ManifestUrl = LauncherSettings.GetDefaultManifestUrl(),
            GameLocale = "frFR",
            AutomaticLauncherUpdates = automaticLauncherUpdates,
            CloseLauncherOnGameStart = false
        };
        Authentication = new FakeLauncherAuthService();
        Observer = new RecordingStartupObserver();
        Http = new RecordingHttpHandler(Observer);
    }

    internal LauncherSettings Settings { get; }

    internal FakeLauncherAuthService Authentication { get; }

    internal RecordingStartupObserver Observer { get; }

    internal RecordingHttpHandler Http { get; }

    internal List<FakeLegacyTimer> Timers { get; } = [];

    internal int AuthenticationFactoryCalls { get; private set; }

    internal int HttpFactoryCalls { get; private set; }

    internal int SettingsLoadCalls { get; private set; }

    internal int SettingsSaveCalls { get; private set; }

    internal int PrepareDirectoryCalls { get; private set; }

    internal LegacyMainWindowDependencies CreateDependencies()
    {
        return new LegacyMainWindowDependencies
        {
            CreateAuthentication = () =>
            {
                AuthenticationFactoryCalls++;
                return Authentication;
            },
            CreateAuthorizedHttpClient = _ =>
            {
                HttpFactoryCalls++;
                return new HttpClient(Http)
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };
            },
            LoadSettings = () =>
            {
                SettingsLoadCalls++;
                return Settings;
            },
            SaveSettings = _ => SettingsSaveCalls++,
            PrepareGameDirectory = _ => PrepareDirectoryCalls++,
            HasPlayableClient = _ => _initialPlayableClient,
            CreateTimer = (interval, priority) =>
            {
                FakeLegacyTimer timer = new(interval, priority);
                Timers.Add(timer);
                return timer;
            },
            PersistLogLine = _ => { },
            StartupObserver = Observer
        };
    }

    internal void CreatePlayableClientFiles()
    {
        string classicDirectory = Path.Combine(_root, "_classic_");
        Directory.CreateDirectory(classicDirectory);
        File.WriteAllBytes(Path.Combine(classicDirectory, "WowClassic.exe"), []);
        File.WriteAllBytes(Path.Combine(_root, GameInstallServices.GameLauncherFileName), []);
    }

    internal FakeLegacyTimer TimerAt(TimeSpan interval)
    {
        return Timers.Single(timer => timer.Interval == interval);
    }

    internal void ResetOperationCounters()
    {
        Http.ResetCounters();
        Authentication.ResetOperationCounters();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

internal sealed class RecordingStartupObserver : ILegacyStartupObserver
{
    private readonly object _sync = new();
    private readonly List<LegacyStartupEvent> _events = [];

    internal IReadOnlyList<LegacyStartupEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    public void Record(LegacyStartupEvent startupEvent)
    {
        lock (_sync)
        {
            _events.Add(startupEvent);
        }
    }

    internal int Count(LegacyStartupEvent startupEvent)
    {
        lock (_sync)
        {
            return _events.Count(value => value == startupEvent);
        }
    }

    internal int IndexOf(LegacyStartupEvent startupEvent)
    {
        lock (_sync)
        {
            return _events.IndexOf(startupEvent);
        }
    }
}

internal sealed class FakeLegacyTimer(
    TimeSpan interval,
    DispatcherPriority priority) : ILegacyDispatcherTimer
{
    private EventHandler? _tick;

    public event EventHandler? Tick
    {
        add
        {
            SubscriptionAdds++;
            _tick += value;
        }
        remove
        {
            SubscriptionRemoves++;
            _tick -= value;
        }
    }

    public TimeSpan Interval { get; } = interval;

    internal DispatcherPriority Priority { get; } = priority;

    public bool IsEnabled { get; private set; }

    internal int StartCalls { get; private set; }

    internal int StopCalls { get; private set; }

    internal int SubscriptionAdds { get; private set; }

    internal int SubscriptionRemoves { get; private set; }

    public void Start()
    {
        StartCalls++;
        IsEnabled = true;
    }

    public void Stop()
    {
        StopCalls++;
        IsEnabled = false;
    }

    internal void RaiseTick()
    {
        _tick?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class RecordingHttpHandler(RecordingStartupObserver observer) : HttpMessageHandler
{
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ManifestResponder { get; set; }

    internal int LauncherUpdateRequests { get; private set; }

    internal int ManifestRequests { get; private set; }

    internal int FirstManifestRequestEventIndex { get; private set; } = int.MaxValue;

    internal void ResetCounters()
    {
        LauncherUpdateRequests = 0;
        ManifestRequests = 0;
        FirstManifestRequestEventIndex = int.MaxValue;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string absoluteUri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (absoluteUri.Contains("launcher-update.json", StringComparison.OrdinalIgnoreCase))
        {
            LauncherUpdateRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        if (absoluteUri.Contains("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            ManifestRequests++;
            FirstManifestRequestEventIndex = Math.Min(
                FirstManifestRequestEventIndex,
                observer.Events.Count);
            if (ManifestResponder is not null)
            {
                return ManifestResponder(request, cancellationToken);
            }

            return Task.FromResult(JsonResponse(new LauncherManifest
            {
                Version = "characterization",
                BaseUrl = "https://atlas.test/client/",
                Files = []
            }));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    internal static HttpResponseMessage JsonResponse<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };
    }
}

internal sealed class FakeLauncherAuthService : ILauncherAuthService
{
    internal bool RestoreResult { get; set; }

    internal Func<CancellationToken, Task<bool>>? RestoreHandler { get; set; }

    public LauncherAuthSession? Session { get; set; }

    public string? AccessToken => Session?.AccessToken;

    public bool IsAuthenticated => Session is not null;

    internal int RestoreCalls { get; private set; }

    internal int EnsureFreshCalls { get; private set; }

    internal int GetFriendsCalls { get; private set; }

    internal int DisposeCalls { get; private set; }

    public Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        RestoreCalls++;
        return RestoreHandler?.Invoke(cancellationToken) ?? Task.FromResult(RestoreResult);
    }

    public Task<bool> EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        EnsureFreshCalls++;
        return Task.FromResult(Session is not null);
    }

    public Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task RegisterAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<GameTicket> CreateGameTicketAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new GameTicket(
            "HP-0000000000000000000000000000000000000000",
            DateTimeOffset.UtcNow.AddMinutes(1),
            "Dono1402",
            "1#1",
            1));
    }

    public Task<EmailChangeResult> ChangeEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        LauncherProfile profile = Session?.Profile ?? CreateProfile();
        return Task.FromResult(new EmailChangeResult(profile with { Email = email }, false, string.Empty));
    }

    public Task<LauncherProfile> RefreshProfileAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Session?.Profile ?? CreateProfile());
    }

    public Task<LauncherProfile> ChangeAvatarAsync(
        string? avatarKey,
        CancellationToken cancellationToken = default)
    {
        LauncherProfile profile = Session?.Profile ?? CreateProfile();
        return Task.FromResult(profile with { AvatarKey = avatarKey });
    }

    public Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LauncherDeviceSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<LauncherDeviceSession>>([]);
    }

    public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LauncherFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        GetFriendsCalls++;
        return Task.FromResult<IReadOnlyList<LauncherFriend>>([]);
    }

    public Task<string> SendFriendRequestAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult("OK");
    }

    public Task AcceptFriendAsync(uint accountId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task RemoveFriendAsync(uint accountId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<LauncherServerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new LauncherServerStatus(
            "Arthas",
            true,
            true,
            true,
            true,
            true,
            DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<LauncherNews>> GetNewsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<LauncherNews>>([]);
    }

    public Task<string> ResendVerificationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult("OK");
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        Session = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        DisposeCalls++;
    }

    internal void ResetOperationCounters()
    {
        EnsureFreshCalls = 0;
        GetFriendsCalls = 0;
    }

    internal static LauncherAuthSession CreateSession()
    {
        return new LauncherAuthSession(
            "access-token",
            DateTimeOffset.UtcNow.AddHours(1),
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(1),
            CreateProfile());
    }

    private static LauncherProfile CreateProfile()
    {
        return new LauncherProfile(
            1,
            "Dono1402",
            "dono@example.test",
            true,
            "gold",
            false,
            false,
            75);
    }
}
