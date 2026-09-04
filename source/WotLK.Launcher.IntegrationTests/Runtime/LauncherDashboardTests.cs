using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class LauncherDashboardTests
{
    internal static async Task<int> RunAsync(string? captureDirectory = null)
    {
        MapFiveServicesWithoutFalseOffline();
        SelectLatestPatchNoteWithLegacyOrdering();
        ProjectCategorizedPatchNotesWithLegacyFallback();
        await RefuseRequestsWithoutSessionAsync();
        await LoadOnceAfterConcurrentSessionRestoreAsync();
        await PublishLoadingAndRejectConcurrentRefreshAsync();
        await ClassifyUnavailableResponsesAsync();
        await PreserveSuccessfulDataAfterFailureAsync();
        await HandleEmptyPatchNoteFeedAsync();
        await StopLateCallbacksAndObserveIgnoredCancellationAsync();
        await RuntimeShutdownWaitsForDashboardAsync();
        await KeepDashboardOrthogonalToGameAndOperationsAsync();
        await CharacterizeRefreshCommandAsync();
        await ValidateWpfProjectionAndAtomicLifecycleAsync(captureDirectory);
        Console.WriteLine("Launcher dashboard OK (02E).");
        return 0;
    }

    private static void MapFiveServicesWithoutFalseOffline()
    {
        Equal(DashboardRealmState.Online, Map(), "Les cinq services en ligne doivent produire Online.");
        Equal(DashboardRealmState.Degraded, Map(api: false), "Une API secondaire indisponible doit produire Degraded.");
        Equal(DashboardRealmState.Degraded, Map(authentication: false), "L'authentification secondaire indisponible doit produire Degraded.");
        Equal(DashboardRealmState.Offline, Map(realmGateway: false), "La passerelle royaume hors ligne doit produire Offline.");
        Equal(DashboardRealmState.Offline, Map(worldGateway: false), "La passerelle monde hors ligne doit produire Offline.");
        Equal(DashboardRealmState.Offline, Map(worldServer: false), "Le monde hors ligne doit produire Offline.");
        Equal(
            DashboardRealmState.Unavailable,
            LauncherDashboardCoordinator.MapRealmState(Status(realm: string.Empty)),
            "Une réponse inutilisable ne doit pas devenir Offline.");
    }

    private static void SelectLatestPatchNoteWithLegacyOrdering()
    {
        DateTimeOffset newestDate = new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
        LauncherNews firstAtSameDate = News("atlas-launcher-1-2-0", newestDate, "Première");
        LauncherNews secondAtSameDate = News("atlas-launcher-9-9-9", newestDate, "Deuxième");
        LauncherNews older = News("older", newestDate.AddDays(-1), "Ancienne");
        LauncherNews? latest = LauncherDashboardCoordinator.SelectLatestPatchNote(
            [older, firstAtSameDate, secondAtSameDate]);

        Equal(firstAtSameDate, latest, "Deux dates identiques doivent conserver l'ordre reçu comme le tri legacy.");
        Equal("v1.2.0", LauncherDashboardCoordinator.ExtractPatchNoteVersion(firstAtSameDate), "La version officielle doit provenir de l'identifiant existant.");
        Equal(string.Empty, LauncherDashboardCoordinator.ExtractPatchNoteVersion(older), "Une version absente ne doit pas être inventée.");
        Equal<LauncherNews?>(null, LauncherDashboardCoordinator.SelectLatestPatchNote([]), "Une liste vide ne doit créer aucune note.");

        LauncherNews emptySummary = News("atlas-launcher-1-3-0", newestDate.AddHours(1), "Sans résumé", string.Empty);
        Equal(string.Empty, emptySummary.Summary, "Le résumé vide du contrat doit rester caractérisable.");
        DashboardViewState emptySummaryView = DashboardStateAdapter.Project(Snapshot(
            sequence: 1,
            DashboardRealmState.Online,
            title: emptySummary.Title,
            hasPatchNote: true) with
        {
            LatestPatchNoteSummary = string.Empty
        });
        Equal("Aucun résumé disponible.", emptySummaryView.LatestPatchNoteSummary, "La présentation doit traiter un résumé vide sans inventer une autre note.");
        True(emptySummaryView.CanOpenLatestPatchNote, "Une note réelle doit pouvoir être ouverte dans le lecteur léger.");
    }

    private static void ProjectCategorizedPatchNotesWithLegacyFallback()
    {
        DateTimeOffset publishedAt = new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        LauncherNews structured = new(
            "atlas-launcher-1-2-0",
            "Launcher",
            "Atlas Launcher 1.2",
            "Les changements importants de cette version.",
            publishedAt,
            [
                new LauncherNewsSection("Launcher", ["Navigation simplifiée.", "Suivi des opérations."]),
                new LauncherNewsSection("Jeu", ["Lancement plus clair."])
            ]);
        LauncherNews legacy = new(
            "atlas-launcher-1-1-0",
            "Addons",
            "Atlas Launcher 1.1",
            "Catalogue des addons amélioré.",
            publishedAt.AddDays(-1));
        DashboardViewState projected = DashboardStateAdapter.Project(Snapshot(
            sequence: 2,
            DashboardRealmState.Online,
            title: structured.Title,
            hasPatchNote: true) with
        {
            PatchNotes = ImmutableArray.Create(structured, legacy)
        });

        Equal(2, projected.PatchNotes.Length, "Toutes les versions doivent rester disponibles dans l'onglet.");
        Equal(2, projected.PatchNotes[0].Sections.Length, "Les catégories structurées doivent rester séparées.");
        Equal("Jeu", projected.PatchNotes[0].Sections[1].Title, "La catégorie Jeu a été perdue.");
        Equal(2, projected.PatchNotes[0].Sections[0].Items.Length, "Chaque changement doit devenir un point distinct.");
        True(projected.PatchNotes[0].HasIntro, "Le résumé d'une note structurée doit rester une introduction.");
        Equal(1, projected.PatchNotes[1].Sections.Length, "Une note historique doit conserver un fallback unique.");
        Equal("Addons", projected.PatchNotes[1].Sections[0].Title, "Le fallback doit reprendre la catégorie historique.");
        Equal("Catalogue des addons amélioré.", projected.PatchNotes[1].Sections[0].Items[0],
            "Le résumé historique doit devenir un point lisible.");

        DashboardViewState localClient = DashboardStateAdapter.Project(Snapshot(
            sequence: 3,
            DashboardRealmState.Online,
            title: structured.Title,
            hasPatchNote: true) with
        {
            PatchNotes = ImmutableArray.Create(structured)
        }, includeLocalDraft: true);
        True(localClient.PatchNotes[0].IsDraft, "Le client local doit afficher le brouillon avant les notes publiées.");
        Equal("Non publiée", localClient.PatchNotes[0].PublishedText,
            "Le brouillon local ne doit jamais paraître déjà publié.");
        True(localClient.PatchNotes[0].Sections.SelectMany(section => section.Items).All(item => !item.Contains(".cs", StringComparison.OrdinalIgnoreCase)),
            "Le brouillon utilisateur ne doit pas contenir de détail de code.");
    }

    private static async Task RefuseRequestsWithoutSessionAsync()
    {
        FakeLauncherAuthService authentication = new();
        using CancellationTokenSource lifetime = new();
        using LauncherDashboardCoordinator coordinator = CreateCoordinator(authentication, lifetime.Token);

        True(!coordinator.CanRefresh, "Sans session, la commande doit être désactivée.");
        Equal(DashboardRefreshStartStatus.NoSession, coordinator.TryRefresh(), "Le refus sans session doit être immédiat.");
        await coordinator.InitializeAfterSessionRestoreAsync(
            new LauncherSessionRestoreResult(LauncherSessionRestoreStatus.NoSession, null));

        Equal(0, authentication.EnsureFreshCalls, "Aucune fraîcheur de session ne doit être demandée sans session.");
        Equal(0, authentication.GetStatusCalls, "Le statut authentifié ne doit pas être appelé sans session.");
        Equal(0, authentication.GetNewsCalls, "Les notes authentifiées ne doivent pas être appelées sans session.");
        Equal(DashboardRealmState.Unavailable, coordinator.CurrentSnapshot.RealmState, "L'absence de session doit rester neutre.");
        Equal(DashboardFailureCategory.NoSession, coordinator.CurrentSnapshot.FailureCategory, "La cause sans session doit être explicite.");
    }

    private static async Task LoadOnceAfterConcurrentSessionRestoreAsync()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        FakeLauncherAuthService authentication = new()
        {
            RestoreResult = true,
            Session = FakeLauncherAuthService.CreateSession(),
            StatusHandler = _ => Task.FromResult(Status()),
            NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>(
                [News("atlas-launcher-1-1-0", DateTimeOffset.UtcNow, "Initiale")])
        };
        using LauncherRuntime runtime = CreateRuntime(client, authentication);

        LauncherSessionRestoreResult[] results = await Task.WhenAll(
            runtime.InitializeAsync(),
            runtime.InitializeAsync(),
            runtime.InitializeAsync());

        True(results.All(result => result.Status == LauncherSessionRestoreStatus.Restored), "La restauration partagée doit réussir.");
        Equal(1, authentication.RestoreCalls, "La session doit être restaurée une fois.");
        Equal(1, authentication.EnsureFreshCalls, "Le chargement initial du dashboard doit être unique.");
        Equal(1, authentication.GetStatusCalls, "Le statut initial doit être chargé une fois.");
        Equal(1, authentication.GetNewsCalls, "Les notes initiales doivent être chargées une fois.");
        Equal(DashboardRealmState.Online, runtime.Dashboard.CurrentSnapshot.RealmState, "Le chargement initial réel doit publier Online.");

        TaskCompletionSource<LauncherServerStatus> status = NewCompletion<LauncherServerStatus>();
        TaskCompletionSource<IReadOnlyList<LauncherNews>> notes = NewCompletion<IReadOnlyList<LauncherNews>>();
        authentication.StatusHandler = _ => status.Task;
        authentication.NewsHandler = _ => notes.Task;
        Task firstAuthenticationSignal = runtime.Dashboard.RefreshAfterAuthenticationAsync();
        Task secondAuthenticationSignal = runtime.Dashboard.RefreshAfterAuthenticationAsync();
        Equal(2, authentication.GetStatusCalls, "Deux signaux d'authentification simultanés doivent partager un seul statut.");
        Equal(2, authentication.GetNewsCalls, "Deux signaux d'authentification simultanés doivent partager une seule note.");
        status.SetResult(Status());
        notes.SetResult([]);
        await Task.WhenAll(firstAuthenticationSignal, secondAuthenticationSignal);
    }

    private static async Task PublishLoadingAndRejectConcurrentRefreshAsync()
    {
        TaskCompletionSource<LauncherServerStatus> status = NewCompletion<LauncherServerStatus>();
        TaskCompletionSource<IReadOnlyList<LauncherNews>> notes = NewCompletion<IReadOnlyList<LauncherNews>>();
        FakeLauncherAuthService authentication = Authenticated(
            token => status.Task,
            token => notes.Task);
        using CancellationTokenSource lifetime = new();
        using LauncherDashboardCoordinator coordinator = CreateCoordinator(authentication, lifetime.Token);
        List<long> sequences = [];
        coordinator.SnapshotChanged += (_, args) => sequences.Add(args.Snapshot.Sequence);

        Equal(DashboardRefreshStartStatus.Started, coordinator.TryRefresh(), "Le premier refresh doit démarrer.");
        Equal(DashboardRealmState.Loading, coordinator.CurrentSnapshot.RealmState, "Loading doit être publié immédiatement.");
        True(!coordinator.CanRefresh, "La commande doit être désactivée pendant le refresh.");
        Equal(DashboardRefreshStartStatus.Busy, coordinator.TryRefresh(), "Le second refresh doit être refusé sans attente.");
        Equal(1, authentication.GetStatusCalls, "Un double clic ne doit pas dupliquer le statut.");
        Equal(1, authentication.GetNewsCalls, "Un double clic ne doit pas dupliquer les notes.");

        status.SetResult(Status());
        notes.SetResult([News("atlas-launcher-1-1-0", new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero), "Dernière")]);
        True(await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2)), "Le refresh doit terminer.");

        DashboardSnapshot snapshot = coordinator.CurrentSnapshot;
        Equal(DashboardRealmState.Online, snapshot.RealmState, "Le statut final doit être Online.");
        Equal("Dernière", snapshot.LatestPatchNoteTitle, "La vraie note doit être publiée.");
        Equal("v1.1.0", snapshot.LatestPatchNoteVersion, "La version doit être extraite de l'identifiant réel.");
        True(coordinator.CanRefresh, "La commande doit être réactivée après succès.");
        True(sequences.SequenceEqual(sequences.Order()), "Les séquences doivent être croissantes.");
        True(sequences.Zip(sequences.Skip(1)).All(pair => pair.First < pair.Second), "Les séquences doivent être strictement croissantes.");
    }

    private static async Task ClassifyUnavailableResponsesAsync()
    {
        await AssertFailureAsync(
            new HttpRequestException("offline"),
            DashboardFailureCategory.Network,
            "Une panne réseau doit produire Network/Unavailable.");
        await AssertFailureAsync(
            new TaskCanceledException("timeout"),
            DashboardFailureCategory.Timeout,
            "Un timeout doit produire Timeout/Unavailable.");
        await AssertFailureAsync(
            new LauncherAuthException("Ta session n'est plus valide.", HttpStatusCode.Unauthorized),
            DashboardFailureCategory.Unauthorized,
            "Un vrai 401 doit rester distinct.");

        FakeLauncherAuthService refreshTimeout = Authenticated(
            _ => Task.FromResult(Status()),
            _ => Task.FromResult<IReadOnlyList<LauncherNews>>([]));
        refreshTimeout.EnsureFreshHandler = _ => Task.FromException<bool>(new TaskCanceledException("refresh timeout"));
        using (CancellationTokenSource refreshLifetime = new())
        using (LauncherDashboardCoordinator refreshCoordinator = CreateCoordinator(refreshTimeout, refreshLifetime.Token))
        {
            refreshCoordinator.TryRefresh();
            await refreshCoordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
            Equal(DashboardFailureCategory.Timeout, refreshCoordinator.CurrentSnapshot.FailureCategory, "Le timeout de fraîcheur doit rester un timeout.");
            Equal(0, refreshTimeout.GetStatusCalls, "Un jeton non rafraîchi ne doit pas appeler le statut.");
            Equal(0, refreshTimeout.GetNewsCalls, "Un jeton non rafraîchi ne doit pas appeler les notes.");
        }

        FakeLauncherAuthService invalid = Authenticated(
            _ => Task.FromResult(Status(realm: string.Empty)),
            _ => Task.FromResult<IReadOnlyList<LauncherNews>>([]));
        using CancellationTokenSource lifetime = new();
        using LauncherDashboardCoordinator coordinator = CreateCoordinator(invalid, lifetime.Token);
        Equal(DashboardRefreshStartStatus.Started, coordinator.TryRefresh(), "La réponse invalide doit être analysée.");
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        Equal(DashboardRealmState.Unavailable, coordinator.CurrentSnapshot.RealmState, "Une réponse invalide ne doit pas être Offline.");
        Equal(DashboardFailureCategory.InvalidResponse, coordinator.CurrentSnapshot.FailureCategory, "La réponse invalide doit être catégorisée.");
    }

    private static async Task PreserveSuccessfulDataAfterFailureAsync()
    {
        DateTimeOffset firstDate = new(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        FakeLauncherAuthService authentication = Authenticated(
            _ => Task.FromResult(Status()),
            _ => Task.FromResult<IReadOnlyList<LauncherNews>>(
                [News("atlas-launcher-1-1-0", firstDate, "Conservée")])) ;
        using CancellationTokenSource lifetime = new();
        using LauncherDashboardCoordinator coordinator = CreateCoordinator(authentication, lifetime.Token);
        Equal(DashboardRefreshStartStatus.Started, coordinator.TryRefresh(), "Le succès initial doit démarrer.");
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        DashboardSnapshot successful = coordinator.CurrentSnapshot;

        authentication.StatusHandler = _ => Task.FromException<LauncherServerStatus>(new HttpRequestException("network"));
        authentication.NewsHandler = _ => Task.FromException<IReadOnlyList<LauncherNews>>(new HttpRequestException("network"));
        Equal(DashboardRefreshStartStatus.Started, coordinator.TryRefresh(), "Le refresh en erreur doit démarrer.");
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        DashboardSnapshot stale = coordinator.CurrentSnapshot;

        Equal(DashboardRealmState.Unavailable, stale.RealmState, "Le réseau indisponible ne doit pas afficher un ancien vert comme actuel.");
        Equal(DashboardRealmState.Online, stale.LastKnownRealmState, "Le dernier état fiable doit être conservé.");
        Equal("Conservée", stale.LatestPatchNoteTitle, "La dernière note réussie ne doit pas être effacée.");
        Equal(successful.LastSuccessfulRefreshAt, stale.LastSuccessfulRefreshAt, "Une erreur ne doit pas rajeunir les données.");
        True(stale.IsStale && stale.HasRetainedDataAfterFailure, "La conservation doit être signalée.");
        True(authentication.Session is not null, "Une panne réseau ne doit pas effacer la session.");

        authentication.StatusHandler = _ => Task.FromResult(Status(api: false));
        authentication.NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>(
            [News("atlas-launcher-1-2-0", firstDate.AddDays(1), "Nouvelle")]);
        Equal(DashboardRefreshStartStatus.Started, coordinator.TryRefresh(), "Une nouvelle tentative doit être possible.");
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        Equal(DashboardRealmState.Degraded, coordinator.CurrentSnapshot.RealmState, "Le retry doit publier le nouvel état.");
        Equal("Nouvelle", coordinator.CurrentSnapshot.LatestPatchNoteTitle, "Le retry doit remplacer la note conservée.");
        True(!coordinator.CurrentSnapshot.IsStale, "Le succès suivant doit effacer le marqueur stale.");
    }

    private static async Task HandleEmptyPatchNoteFeedAsync()
    {
        FakeLauncherAuthService authentication = Authenticated(
            _ => Task.FromResult(Status()),
            _ => Task.FromResult<IReadOnlyList<LauncherNews>>([]));
        using CancellationTokenSource lifetime = new();
        using LauncherDashboardCoordinator coordinator = CreateCoordinator(authentication, lifetime.Token);
        coordinator.TryRefresh();
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));

        DashboardSnapshot snapshot = coordinator.CurrentSnapshot;
        True(!snapshot.HasPatchNote, "Une liste vide ne doit pas fabriquer une note.");
        DashboardViewState view = DashboardStateAdapter.Project(snapshot);
        Equal("Aucune note de mise à jour disponible.", view.LatestPatchNoteTitle, "L'état vide doit être explicite.");
        Equal(string.Empty, view.LatestPatchNoteVersion, "Aucune version ne doit être inventée.");
    }

    private static async Task StopLateCallbacksAndObserveIgnoredCancellationAsync()
    {
        TaskCompletionSource<LauncherServerStatus> status = NewCompletion<LauncherServerStatus>();
        TaskCompletionSource<IReadOnlyList<LauncherNews>> notes = NewCompletion<IReadOnlyList<LauncherNews>>();
        CancellationToken statusToken = default;
        CancellationToken notesToken = default;
        FakeLauncherAuthService authentication = Authenticated(
            token =>
            {
                statusToken = token;
                return status.Task;
            },
            token =>
            {
                notesToken = token;
                return notes.Task;
            });
        using CancellationTokenSource lifetime = new();
        LauncherDashboardCoordinator coordinator = CreateCoordinator(authentication, lifetime.Token);
        int notifications = 0;
        coordinator.SnapshotChanged += (_, _) => notifications++;
        coordinator.TryRefresh();
        int afterLoading = notifications;

        coordinator.BeginShutdown();
        lifetime.Cancel();
        True(statusToken.IsCancellationRequested && notesToken.IsCancellationRequested, "La fermeture doit annuler le token de cycle de vie partagé.");
        Equal(DashboardRefreshStartStatus.ShuttingDown, coordinator.TryRefresh(), "Aucune requête ne doit démarrer pendant la fermeture.");
        status.SetResult(Status());
        notes.SetResult([News("late", DateTimeOffset.UtcNow, "Tardive")]);
        True(await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2)), "Une dépendance ignorant l'annulation doit rester observée.");
        Equal(afterLoading, notifications, "Un callback tardif ne doit plus publier de snapshot.");
        Equal(1, authentication.GetStatusCalls, "La fermeture ne doit pas relancer le statut.");
        Equal(1, authentication.GetNewsCalls, "La fermeture ne doit pas relancer les notes.");
        coordinator.Dispose();
        coordinator.Dispose();
    }

    private static async Task RuntimeShutdownWaitsForDashboardAsync()
    {
        using TemporaryClient client = new();
        TaskCompletionSource<LauncherServerStatus> status = NewCompletion<LauncherServerStatus>();
        TaskCompletionSource<IReadOnlyList<LauncherNews>> notes = NewCompletion<IReadOnlyList<LauncherNews>>();
        FakeLauncherAuthService authentication = Authenticated(_ => status.Task, _ => notes.Task);
        using LauncherRuntime runtime = CreateRuntime(client, authentication);
        Equal(DashboardRefreshStartStatus.Started, runtime.Dashboard.TryRefresh(), "Le refresh témoin doit démarrer.");

        Task<bool> shutdown = runtime.WaitForShutdownAsync(TimeSpan.FromSeconds(2));
        status.SetResult(Status());
        notes.SetResult([]);

        True(await shutdown, "LauncherRuntime doit observer la fin du dashboard pendant la fermeture.");
        True(!runtime.Dashboard.HasActiveRefresh, "La tâche dashboard doit être libérée après la fermeture.");
        Equal(DashboardRefreshStartStatus.ShuttingDown, runtime.Dashboard.TryRefresh(), "Aucun refresh ne doit redémarrer après fermeture.");
    }

    private static async Task KeepDashboardOrthogonalToGameAndOperationsAsync()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        FakeLauncherAuthService authentication = new()
        {
            RestoreResult = true,
            Session = FakeLauncherAuthService.CreateSession(),
            StatusHandler = _ => Task.FromResult(Status(worldServer: false)),
            NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>([])
        };
        using LauncherRuntime runtime = CreateRuntime(client, authentication);
        GameRuntimeSnapshot before = runtime.Game.CurrentSnapshot;

        await runtime.InitializeAsync();

        Equal(GameAction.Play, before.Action, "Le client témoin doit être jouable.");
        Equal(before.Action, runtime.Game.CurrentSnapshot.Action, "RealmOffline ne doit pas modifier GameAction.");
        Equal(DashboardRealmState.Offline, runtime.Dashboard.CurrentSnapshot.RealmState, "Le monde explicitement hors ligne doit être rouge.");
        True(runtime.Operations.IsIdle, "Le dashboard ne doit prendre aucun bail de maintenance.");

        LauncherOperationStartResult repair = runtime.Operations.TryBegin(
            LauncherOperationKind.GameRepair,
            canUserCancel: true);
        True(repair.IsStarted, "Le bail témoin GameRepair doit démarrer.");
        LauncherOperationLease repairLease = repair.Lease
            ?? throw new InvalidOperationException("Le bail GameRepair est absent.");
        authentication.StatusHandler = _ => Task.FromResult(Status());
        Equal(DashboardRefreshStartStatus.Started, runtime.Dashboard.TryRefresh(), "Le dashboard en lecture seule peut s'actualiser pendant une réparation.");
        await runtime.Dashboard.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        True(runtime.Operations.IsCurrent(repairLease), "Un changement de statut ne doit ni terminer ni annuler la réparation.");
        True(!repairLease.CancellationToken.IsCancellationRequested, "Le dashboard ne doit pas annuler le bail jeu.");
        repairLease.Dispose();

        FieldInfo[] fields = typeof(LauncherDashboardCoordinator).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        True(fields.All(field => field.FieldType != typeof(LauncherOperationCoordinator)), "Le dashboard ne doit pas dépendre du coordinateur d'opérations.");
        True(fields.All(field => !typeof(System.Threading.Timer).IsAssignableFrom(field.FieldType)), "Aucun Timer ne doit être créé.");
        True(fields.All(field => field.FieldType != typeof(PeriodicTimer)), "Aucun PeriodicTimer ne doit être créé.");
        True(fields.All(field => field.FieldType.FullName != "System.Windows.Threading.DispatcherTimer"), "Aucun DispatcherTimer ne doit être créé.");

        DashboardUiState preview = LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready);
        string previewTitle = preview.Current.LatestPatchNoteTitle;
        preview.RefreshCommand.Execute(null);
        Equal(previewTitle, preview.Current.LatestPatchNoteTitle, "Le refresh preview doit rester un no-op fictif.");
    }

    private static async Task CharacterizeRefreshCommandAsync()
    {
        TaskCompletionSource<LauncherServerStatus> status = NewCompletion<LauncherServerStatus>();
        TaskCompletionSource<IReadOnlyList<LauncherNews>> notes = NewCompletion<IReadOnlyList<LauncherNews>>();
        FakeLauncherAuthService authentication = Authenticated(_ => status.Task, _ => notes.Task);
        using CancellationTokenSource lifetime = new();
        using LauncherDashboardCoordinator coordinator = CreateCoordinator(authentication, lifetime.Token);
        using RefreshDashboardCommand command = new(coordinator);

        True(command.Command.CanExecute(null), "Actualiser doit être disponible avec une session.");
        command.Command.Execute(null);
        True(!command.Command.CanExecute(null), "Actualiser doit être désactivé pendant l'appel.");
        status.SetException(new HttpRequestException("offline"));
        notes.SetException(new HttpRequestException("offline"));
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        True(command.Command.CanExecute(null), "Actualiser doit être réactivé après erreur.");
        command.Dispose();
        True(!command.Command.CanExecute(null), "La commande disposée doit rester inactive.");
    }

    private static async Task ValidateWpfProjectionAndAtomicLifecycleAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasDashboardWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static void RunWpfHarness(
        TaskCompletionSource completion,
        string? captureDirectory)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        dispatcher.UnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        };
        _ = RunAsync();
        Dispatcher.Run();
        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }

        async Task RunAsync()
        {
            Application? application = null;
            LauncherShellV2? window = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                FakeDashboardRuntime runtime = new(Snapshot(
                    sequence: 0,
                    DashboardRealmState.Unknown,
                    title: string.Empty,
                    hasPatchNote: false));
                DashboardUiState dashboard = new();
                int propertyNotifications = 0;
                dashboard.PropertyChanged += (_, _) => propertyNotifications++;
                using DashboardStateAdapter adapter = new(dashboard, runtime, dispatcher);
                using RefreshDashboardCommand command = new(runtime);
                dashboard.AttachRefreshCommand(command.Command);
                int afterConstruction = propertyNotifications;
                GameUiState game = LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready);
                string originalClientStatus = game.ClientStatus;
                SettingsViewState runtimeSettings = LauncherV2PreviewData.CreateSettings().Current with
                {
                    IsRuntimeConnected = true,
                    AreDeferredControlsEnabled = false
                };
                window = new LauncherShellV2(
                    LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready),
                    game,
                    dashboard,
                    LauncherV2PreviewData.CreateFriends(),
                    new ProfileUiState(),
                    new SettingsUiState(runtimeSettings))
                {
                    Width = 1440,
                    Height = 860,
                    Left = -20000,
                    Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false
                };
                window.Show();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                TextBlock realmText = Required<TextBlock>(window, "RealmStatusText");
                Ellipse realmDot = Required<Ellipse>(window, "RealmStatusDot");
                GameViewV2 gameView = Required<GameViewV2>(window, "GameView");
                Button noteAction = Required<Button>(gameView, "LatestPatchNoteAction");
                Border hero = Required<Border>(gameView, "HeroCard");
                Border gameContent = Required<Border>(gameView, "ContentFrame");
                True(!noteAction.IsEnabled, "Le lecteur doit rester indisponible sans note réelle.");
                True(gameView.FindName("OptionsButton") is null,
                    "Le raccourci Options ne doit plus occuper l’action principale de la page Jeu.");
                True(gameView.FindName("NewsCard") is null && gameView.FindName("InstallCard") is null,
                    "Les anciennes cartes Actualité et Installation doivent être retirées.");
                True(Math.Abs(hero.ActualHeight - gameContent.ActualHeight) <= 0.5,
                    "Le visuel Jeu doit occuper toute la hauteur utile de l’onglet.");

                LauncherNews visibleNote = new(
                    "atlas-launcher-1-1-0",
                    "Launcher",
                    "Vraie note",
                    "Résumé réel",
                    new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero),
                    [
                        new LauncherNewsSection("Launcher", ["Navigation simplifiée.", "Téléchargements plus lisibles."]),
                        new LauncherNewsSection("Jeu", ["L'état du client est plus clair."])
                    ]);
                runtime.Publish(Snapshot(1, DashboardRealmState.Online, "Vraie note", true) with
                {
                    PatchNotes = ImmutableArray.Create(visibleNote)
                });
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                window.UpdateLayout();
                Equal("Arthas en ligne", realmText.Text, "Le mode large doit afficher Arthas en ligne.");
                EqualBrush(application, "AtlasV2.Brush.Success", realmDot.Fill, "Online doit utiliser le vert.");
                Equal(afterConstruction + 1, propertyNotifications, "Un snapshot doit produire une notification atomique groupée.");
                Equal(originalClientStatus, game.ClientStatus, "Le royaume ne doit pas modifier le client.");
                True(noteAction.IsEnabled, "Une note réelle doit activer le bouton Mises à jour.");
                Equal(
                    "Mises à jour",
                    ((TextBlock)((StackPanel)noteAction.Content).Children[1]).Text,
                    "Le bouton secondaire doit identifier clairement les mises à jour.");
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    Directory.CreateDirectory(captureDirectory);
                    SavePng(window, System.IO.Path.Combine(captureDirectory, "game-immersive-1440x860.png"));
                    window.Width = 1080;
                    window.Height = 680;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    SavePng(window, System.IO.Path.Combine(captureDirectory, "game-immersive-1080x680.png"));
                    window.Width = 1920;
                    window.Height = 1080;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    SavePng(window, System.IO.Path.Combine(captureDirectory, "game-immersive-1920x1080.png"));
                    window.Width = 1440;
                    window.Height = 860;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                }
                Button patchNotesNavigation = Required<Button>(window, "PatchNotesNavigationButton");
                RaiseClick(patchNotesNavigation);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                window.UpdateLayout();
                Equal(LauncherShellPage.PatchNotes, window.CurrentPage, "L'onglet doit ouvrir les notes de mise à jour.");
                Equal(Visibility.Visible, window.PatchNotesPage.Visibility, "La page des notes doit être visible.");
                Equal(1, window.PatchNotesPage.ListHost.Items.Count, "La liste complète projetée doit alimenter la page.");
                Equal(ScrollBarVisibility.Disabled, window.PatchNotesPage.ScrollHost.HorizontalScrollBarVisibility,
                    "La page des notes ne doit jamais défiler horizontalement.");
                True(window.PatchNotesPage.ScrollHost.ScrollableWidth <= 0.5,
                    "La page des notes ne doit pas déborder horizontalement à 1440 px.");
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    Directory.CreateDirectory(captureDirectory);
                    SavePng(window, System.IO.Path.Combine(captureDirectory, "patch-notes-1440x860.png"));
                }

                window.Width = 1080;
                window.Height = 680;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Equal("Notes", Required<TextBlock>(window, "PatchNotesNavigationLabel").Text,
                    "Le libellé compact doit préserver les commandes de fenêtre.");
                True(window.PatchNotesPage.ScrollHost.ScrollableWidth <= 0.5,
                    "La page des notes ne doit pas déborder horizontalement à 1080 px.");
                True(Required<Button>(window, "CloseWindowButton").IsVisible,
                    "La commande Fermer doit rester accessible à 1080 px.");
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    SavePng(window, System.IO.Path.Combine(captureDirectory, "patch-notes-1080x680.png"));
                }

                window.Width = 1920;
                window.Height = 1080;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                True(Required<Grid>(window.PatchNotesPage, "ContentFrame").ActualWidth <= 1181,
                    "Le contenu des notes ne doit pas s'étirer excessivement à 1920 px.");
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    SavePng(window, System.IO.Path.Combine(captureDirectory, "patch-notes-1920x1080.png"));
                }

                window.Width = 1440;
                window.Height = 860;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                RaiseClick(Required<Button>(window, "GameNavigationButton"));
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                Equal(LauncherShellPage.Game, window.CurrentPage, "Jeu doit rester accessible depuis les notes.");
                Keyboard.Focus(noteAction);
                RaiseClick(noteAction);
                await DelayAndPumpAsync(190);
                PatchNoteOverlayV2 patchNote = window.PatchNoteReaderOverlay;
                Equal(ShellOverlayKind.PatchNote, window.CurrentOverlay, "La note doit s’ouvrir comme overlay léger unique.");
                True(!patchNote.IsFullyClosed && patchNote.IsHitTestVisible, "L’overlay ouvert doit intercepter son contenu.");
                Equal("Vraie note", Required<TextBlock>(patchNote, "PatchNoteTitleText").Text, "Le titre réel doit alimenter l’overlay.");
                Equal("Résumé réel", Required<TextBlock>(patchNote, "PatchNoteBodyText").Text, "Le corps doit réutiliser le résumé réel du dashboard.");
                Equal("v1.1.0", Required<TextBlock>(patchNote, "PatchNoteVersionText").Text, "La version réelle doit être visible.");
                True(patchNote.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject), "Le focus doit rester dans le lecteur.");
                Keyboard.Focus(gameView.PrimaryActionFocusTarget);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                True(patchNote.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject), "Le focus ne doit pas passer derrière le lecteur.");
                RaisePreviewKey(window, Key.Escape);
                await DelayAndPumpAsync(190);
                True(patchNote.IsFullyClosed && !window.PatchNoteState.IsOpen, "Échap doit retirer le lecteur et son hit-test.");
                Equal(noteAction, Keyboard.FocusedElement, "Le focus doit revenir à l’action patch note.");

                RaiseClick(noteAction);
                await DelayAndPumpAsync(190);
                RaiseMouseDown(Required<Border>(patchNote, "Scrim"));
                await DelayAndPumpAsync(190);
                True(patchNote.IsFullyClosed, "Un clic extérieur doit fermer le lecteur.");

                RaiseClick(noteAction);
                await DelayAndPumpAsync(190);
                RaiseClick(Required<Button>(patchNote, "CloseButton"));
                await DelayAndPumpAsync(190);
                True(patchNote.IsFullyClosed, "Le bouton X doit fermer le lecteur.");
                dashboard.SetWideRealmLabel(false);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal("En ligne", realmText.Text, "Le mode compact doit afficher En ligne.");
                dashboard.SetWideRealmLabel(true);

                Exception? backgroundPublishFailure = null;
                await Task.Run(() =>
                {
                    try
                    {
                        runtime.Publish(Snapshot(2, DashboardRealmState.Degraded, "Dégradée", true));
                    }
                    catch (Exception ex)
                    {
                        backgroundPublishFailure = ex;
                    }
                });
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                window.UpdateLayout();
                True(backgroundPublishFailure is null, "Une actualisation en arrière-plan ne doit pas notifier les boutons WPF hors Dispatcher.");
                Equal("Services dégradés", realmText.Text, "Le texte Degraded est incorrect.");
                EqualBrush(application, "AtlasV2.Brush.Gold", realmDot.Fill, "Degraded doit utiliser l'or.");

                runtime.Publish(Snapshot(3, DashboardRealmState.Offline, "Hors ligne", true));
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                window.UpdateLayout();
                Equal("Hors ligne", realmText.Text, "Le texte Offline est incorrect.");
                EqualBrush(application, "AtlasV2.Brush.Danger", realmDot.Fill, "Offline doit utiliser le rouge.");

                runtime.Publish(Snapshot(4, DashboardRealmState.Unavailable, "Conservée", true, isStale: true));
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                window.UpdateLayout();
                Equal("Statut indisponible", realmText.Text, "Le texte Unavailable est incorrect.");
                EqualBrush(application, "AtlasV2.Brush.TextMuted", realmDot.Fill, "Unavailable doit rester neutre.");
                True(dashboard.Current.LatestPatchNoteMetaText.Contains("données conservées", StringComparison.Ordinal), "La conservation après erreur doit être visible.");

                runtime.Publish(Snapshot(5, DashboardRealmState.Loading, "Conservée", true, isLoading: true));
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal("Actualisation…", realmText.Text, "Loading doit être visible.");
                True(!dashboard.RefreshCommand.CanExecute(null), "Le bouton doit être désactivé pendant Loading.");

                DashboardViewState beforeStaleSequence = dashboard.Current;
                runtime.Publish(Snapshot(3, DashboardRealmState.Offline, "Obsolète", true));
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal(beforeStaleSequence, dashboard.Current, "Une séquence obsolète doit être ignorée.");

                DashboardViewState beforeDispose = dashboard.Current;
                adapter.Dispose();
                runtime.Publish(Snapshot(6, DashboardRealmState.Online, "Après fermeture", true));
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal(beforeDispose, dashboard.Current, "Un callback après désinscription ne doit pas modifier WPF.");
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
            finally
            {
                if (window is not null)
                {
                    window.DataContext = null;
                    window.Content = null;
                    window.Close();
                }

                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static async Task AssertFailureAsync(
        Exception exception,
        DashboardFailureCategory expected,
        string message)
    {
        FakeLauncherAuthService authentication = Authenticated(
            _ => Task.FromException<LauncherServerStatus>(exception),
            _ => Task.FromException<IReadOnlyList<LauncherNews>>(exception));
        using CancellationTokenSource lifetime = new();
        using LauncherDashboardCoordinator coordinator = CreateCoordinator(authentication, lifetime.Token);
        coordinator.TryRefresh();
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        Equal(DashboardRealmState.Unavailable, coordinator.CurrentSnapshot.RealmState, message);
        Equal(expected, coordinator.CurrentSnapshot.FailureCategory, message);
    }

    private static LauncherDashboardCoordinator CreateCoordinator(
        FakeLauncherAuthService authentication,
        CancellationToken token)
    {
        return new LauncherDashboardCoordinator(authentication, token, _ => { });
    }

    private static LauncherRuntime CreateRuntime(
        TemporaryClient client,
        FakeLauncherAuthService authentication)
    {
        return new LauncherRuntime(new LauncherRuntimeDependencies
        {
            LoadSettings = () => client.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = () => "v1.1.0-test"
        });
    }

    private static FakeLauncherAuthService Authenticated(
        Func<CancellationToken, Task<LauncherServerStatus>> status,
        Func<CancellationToken, Task<IReadOnlyList<LauncherNews>>> notes)
    {
        return new FakeLauncherAuthService
        {
            Session = FakeLauncherAuthService.CreateSession(),
            StatusHandler = status,
            NewsHandler = notes
        };
    }

    private static DashboardRealmState Map(
        bool api = true,
        bool authentication = true,
        bool realmGateway = true,
        bool worldGateway = true,
        bool worldServer = true)
    {
        return LauncherDashboardCoordinator.MapRealmState(Status(
            api,
            authentication,
            realmGateway,
            worldGateway,
            worldServer));
    }

    private static LauncherServerStatus Status(
        bool api = true,
        bool authentication = true,
        bool realmGateway = true,
        bool worldGateway = true,
        bool worldServer = true,
        string realm = "Arthas")
    {
        return new LauncherServerStatus(
            realm,
            api,
            authentication,
            realmGateway,
            worldGateway,
            worldServer,
            new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero));
    }

    private static LauncherNews News(
        string id,
        DateTimeOffset publishedAt,
        string title,
        string summary = "Résumé réel")
    {
        return new LauncherNews(id, "Launcher", title, summary, publishedAt);
    }

    private static DashboardSnapshot Snapshot(
        long sequence,
        DashboardRealmState state,
        string title,
        bool hasPatchNote,
        bool isStale = false,
        bool isLoading = false)
    {
        ImmutableArray<LauncherNews> patchNotes = hasPatchNote
            ? ImmutableArray.Create(News(
                "atlas-launcher-1-1-0",
                new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero),
                title))
            : ImmutableArray<LauncherNews>.Empty;
        return new DashboardSnapshot(
            sequence,
            isLoading,
            state,
            state.ToString(),
            new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero),
            isStale ? DashboardFailureCategory.Network : DashboardFailureCategory.None,
            patchNotes,
            hasPatchNote ? "atlas-launcher-1-1-0" : null,
            hasPatchNote ? "Launcher" : null,
            title,
            hasPatchNote ? "Résumé réel" : string.Empty,
            hasPatchNote ? "v1.1.0" : string.Empty,
            hasPatchNote ? new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero) : null,
            hasPatchNote,
            isStale,
            isStale,
            state is DashboardRealmState.Online or DashboardRealmState.Degraded or DashboardRealmState.Offline
                ? state
                : DashboardRealmState.Online,
            "En ligne");
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static T Required<T>(FrameworkElement scope, string name)
        where T : FrameworkElement
    {
        return scope.FindName(name) as T
            ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");
    }

    private static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static void RaisePreviewKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("La source WPF du contrôle est absente.");
        target.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static void RaiseMouseDown(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = target
        });
    }

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await Dispatcher.CurrentDispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle);
    }

    private static void EqualBrush(
        Application application,
        string resourceKey,
        Brush actual,
        string message)
    {
        Brush expected = (Brush)application.FindResource(resourceKey);
        Equal(expected.ToString(), actual.ToString(), message);
    }

    private static void SavePng(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void LoadV2Resources(Application application)
    {
        string[] resourcePaths =
        [
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        ];
        foreach (string path in resourcePaths)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(path, UriKind.Relative)
            });
        }
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

    private sealed class FakeDashboardRuntime : ILauncherDashboardRuntime
    {
        internal FakeDashboardRuntime(DashboardSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
        }

        public event EventHandler? AvailabilityChanged;

        public event EventHandler<DashboardSnapshotEventArgs>? SnapshotChanged;

        public DashboardSnapshot CurrentSnapshot { get; private set; }

        public bool CanRefresh => !CurrentSnapshot.IsLoading;

        public DashboardRefreshStartStatus TryRefresh()
        {
            return CanRefresh ? DashboardRefreshStartStatus.Started : DashboardRefreshStartStatus.Busy;
        }

        internal void Publish(DashboardSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, new DashboardSnapshotEventArgs(snapshot));
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
