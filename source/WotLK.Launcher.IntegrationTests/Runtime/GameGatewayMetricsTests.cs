using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

internal static class GameGatewayMetricsTests
{
    internal static async Task<int> RunAsync()
    {
        await MeasureOnlyTheTcpGatewayAsync();
        await PreserveCancellationAndBoundTimeoutAsync();
        await PreserveConfirmedDisplayDuringRefreshAsync();
        await ProjectLiveMetricsAndClearFailuresAsync();
        await IgnoreMetricsReturningAfterLogoutAsync();
        ValidateOptionalContractAndTranslations();
        await GameGatewayMetricsWpfTests.RunAsync();
        Console.WriteLine("Game gateway metrics OK: real loopback TCP success/refusal, cancellation and bounded timeout, stable confirmed status during asynchronous refresh, actual offline/degraded/failure/recovery updates, independent API timing, zero/unknown/invalid counts, bot semantics, failure/logout cleanup and FR/EN text. No production network or visible UI.");
        return 0;
    }

    private static async Task MeasureOnlyTheTcpGatewayAsync()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
        TcpLauncherGameGatewayProbe probe = new("127.0.0.1", port, TimeSpan.FromSeconds(1));
        int? measured = await probe.MeasureAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        using TcpClient peer = await accepted.WaitAsync(TimeSpan.FromSeconds(2));
        Require(measured is >= 1 and <= 1000, "A real loopback TCP handshake must produce a bounded positive measurement.");
        byte[] payload = new byte[1];
        Require(await peer.GetStream().ReadAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0,
            "The latency probe must disconnect without writing game or HTTP payloads.");
        listener.Stop();
        Require(await probe.MeasureAsync(CancellationToken.None) is null, "A refused TCP connection must remain unknown, never zero ms.");
    }

    private static async Task PreserveCancellationAndBoundTimeoutAsync()
    {
        using CancellationTokenSource alreadyCancelled = new();
        alreadyCancelled.Cancel();
        await ExpectCancellationAsync(() => new TcpLauncherGameGatewayProbe("127.0.0.1", 1)
            .MeasureAsync(alreadyCancelled.Token));

        TaskCompletionSource entered = NewSignal();
        bool observedCancellation = false;
        async ValueTask PendingConnect(TcpClient client, IPAddress[] addresses, int port, CancellationToken token)
        {
            Require(addresses.Length == 1 && IPAddress.IsLoopback(addresses[0]), "The deterministic pending connector must remain loopback-only.");
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { observedCancellation = token.IsCancellationRequested; }
        }
        TcpLauncherGameGatewayProbe pending = new("127.0.0.1", 1, TimeSpan.FromSeconds(1), PendingConnect);
        using CancellationTokenSource caller = new();
        Task<int?> measurement = pending.MeasureAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        caller.Cancel();
        await ExpectCancellationAsync(() => measurement);
        Require(observedCancellation, "Cancellation must reach an in-flight TCP connector.");

        observedCancellation = false;
        Stopwatch watch = Stopwatch.StartNew();
        TcpLauncherGameGatewayProbe timeout = new("127.0.0.1", 1, TimeSpan.FromMilliseconds(60), PendingConnect);
        Require(await timeout.MeasureAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)) is null,
            "An internal timeout must yield unknown latency without cancelling its caller.");
        Require(observedCancellation && watch.Elapsed < TimeSpan.FromSeconds(2), "The probe must cancel its pending connector within the configured bound.");
    }

    private static async Task ProjectLiveMetricsAndClearFailuresAsync()
    {
        MutableProbe probe = new() { Result = 47 };
        LauncherServerStatus current = Status(23, "excluding-random-bots");
        FakeLauncherAuthService authentication = new()
        {
            Session = FakeLauncherAuthService.CreateSession(),
            StatusHandler = async token => { await Task.Delay(120, token); return current; },
            NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>([])
        };
        using LauncherDashboardCoordinator dashboard = new(authentication, CancellationToken.None, _ => { }, gatewayProbe: probe);
        await RefreshAsync(dashboard);
        Require(dashboard.CurrentSnapshot.OnlinePlayers == 23 && dashboard.CurrentSnapshot.GatewayLatencyMilliseconds == 47,
            "The count must come from the API payload, while latency comes from TCP rather than the delayed API GET.");
        DashboardViewState view = DashboardStateAdapter.Project(dashboard.CurrentSnapshot);
        Require(view.OnlinePlayersText == "23" && view.GatewayLatencyText == "47 ms"
            && view.OnlinePlayersToolTip.Contains("exclus", StringComparison.Ordinal), "Measured values and identified-bot semantics must reach the view.");

        current = Status(0, "characters");
        await RefreshAsync(dashboard);
        Require(DashboardStateAdapter.Project(dashboard.CurrentSnapshot).OnlinePlayersText == "0", "A measured empty realm must show zero.");
        Require(DashboardStateAdapter.Project(dashboard.CurrentSnapshot).OnlinePlayersToolTip.Contains("inclure des bots", StringComparison.Ordinal),
            "Without bot metadata the tooltip must not promise a human-only count.");

        current = Status(7);
        probe.Result = null;
        await RefreshAsync(dashboard);
        Require(dashboard.CurrentSnapshot.OnlinePlayers == 7 && dashboard.CurrentSnapshot.GatewayLatencyMilliseconds is null,
            "A failed gateway measurement must clear only its own previous latency.");
        probe.Result = -5;
        current = Status(-1, "characters");
        await RefreshAsync(dashboard);
        Require(dashboard.CurrentSnapshot.OnlinePlayers is null && dashboard.CurrentSnapshot.OnlinePlayerCountKind is null
            && dashboard.CurrentSnapshot.GatewayLatencyMilliseconds is null, "Negative measurements must be rejected.");

        current = Status(99) with { WorldServer = false };
        probe.Result = 31;
        await RefreshAsync(dashboard);
        Require(dashboard.CurrentSnapshot.OnlinePlayers is null, "An offline world must not display even a supplied stale online count.");

        current = Status(12);
        await RefreshAsync(dashboard);
        TaskCompletionSource<LauncherServerStatus> deferred = new(TaskCreationOptions.RunContinuationsAsynchronously);
        authentication.StatusHandler = _ => deferred.Task;
        Require(dashboard.TryRefresh() == DashboardRefreshStartStatus.Started, "The deferred refresh must begin.");
        Require(dashboard.CurrentSnapshot.IsLoading && dashboard.CurrentSnapshot.OnlinePlayers == 12
            && dashboard.CurrentSnapshot.GatewayLatencyMilliseconds == 31, "A pending refresh must retain the last confirmed metrics.");
        deferred.SetException(new HttpRequestException("fixture unavailable"));
        Require(await dashboard.WaitForIdleAsync(TimeSpan.FromSeconds(2)), "The failed refresh must settle.");
        view = DashboardStateAdapter.Project(dashboard.CurrentSnapshot);
        Require(view.OnlinePlayersText == "—" && view.GatewayLatencyText == "—", "An API failure must not leave either metric apparently current.");

        authentication.EnsureFreshHandler = _ => Task.FromResult(false);
        await RefreshAsync(dashboard);
        Require(dashboard.CurrentSnapshot.FailureCategory == DashboardFailureCategory.Unauthorized
            && dashboard.CurrentSnapshot.OnlinePlayers is null && dashboard.CurrentSnapshot.GatewayLatencyMilliseconds is null,
            "Session expiry must also clear metrics.");
    }

    private static async Task PreserveConfirmedDisplayDuringRefreshAsync()
    {
        MutableProbe probe = new() { Result = 47 };
        FakeLauncherAuthService authentication = new()
        {
            Session = FakeLauncherAuthService.CreateSession(),
            StatusHandler = _ => Task.FromResult(Status(23, "excluding-random-bots")),
            NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>([])
        };
        using LauncherDashboardCoordinator dashboard = new(authentication, CancellationToken.None, _ => { }, gatewayProbe: probe);
        await RefreshAsync(dashboard);

        await RoundAsync(Status(25, "excluding-random-bots"), 42, DashboardRealmState.Online);
        await RoundAsync(Status(99) with { WorldServer = false }, 31, DashboardRealmState.Offline);
        Require(dashboard.CurrentSnapshot.OnlinePlayers is null, "A confirmed world outage must clear its online count.");
        await RoundAsync(Status(6) with { RealmGateway = false }, null, DashboardRealmState.Offline);
        Require(dashboard.CurrentSnapshot.GatewayLatencyMilliseconds is null, "A failed TCP probe must replace the retained latency when the refresh finishes.");
        await RoundAsync(Status(5) with { Authentication = false }, 54, DashboardRealmState.Degraded);
        await RoundAsync(null, 40, DashboardRealmState.Unavailable, new HttpRequestException("fixture unavailable"));
        Require(dashboard.CurrentSnapshot.FailureCategory == DashboardFailureCategory.Network
            && dashboard.CurrentSnapshot.OnlinePlayers is null && dashboard.CurrentSnapshot.GatewayLatencyMilliseconds is null,
            "A completed network failure must remain visible and clear metrics rather than keep a previous green status.");
        await RoundAsync(null, 40, DashboardRealmState.Unavailable, new TaskCanceledException("fixture timeout"));
        Require(dashboard.CurrentSnapshot.FailureCategory == DashboardFailureCategory.Timeout,
            "A timeout must replace the previous failure once the new attempt ends.");
        await RoundAsync(Status(9), 22, DashboardRealmState.Online);
        Require(dashboard.CurrentSnapshot.FailureCategory == DashboardFailureCategory.None && !dashboard.CurrentSnapshot.IsStale,
            "A confirmed recovery must clear the failure state.");

        async Task RoundAsync(LauncherServerStatus? response, int? latency, DashboardRealmState expected, Exception? failure = null)
        {
            DashboardSnapshot before = dashboard.CurrentSnapshot;
            DashboardViewState beforeView = DashboardStateAdapter.Project(before);
            TaskCompletionSource<LauncherServerStatus> status = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<IReadOnlyList<LauncherNews>> notes = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<int?> measured = new(TaskCreationOptions.RunContinuationsAsynchronously);
            authentication.StatusHandler = _ => status.Task;
            authentication.NewsHandler = _ => notes.Task;
            probe.Handler = _ => measured.Task;
            Require(dashboard.TryRefresh() == DashboardRefreshStartStatus.Started, "The deferred refresh must start.");
            try
            {
                AssertPendingDisplay();
                Require(dashboard.TryRefresh() == DashboardRefreshStartStatus.Busy && !dashboard.CanRefresh,
                    "Keeping a confirmed status must not allow overlapping refreshes.");
                if (failure is null) status.SetResult(response!);
                else status.SetException(failure);
                await Task.Yield();
                AssertPendingDisplay();
                notes.SetResult([]);
                await Task.Yield();
                AssertPendingDisplay();
                measured.SetResult(latency);
                Require(await dashboard.WaitForIdleAsync(TimeSpan.FromSeconds(2)), "All three deferred observations must settle.");
                Require(dashboard.CurrentSnapshot.RealmState == expected && !dashboard.CurrentSnapshot.IsLoading,
                    "The completed observation must replace the preserved status.");
            }
            finally
            {
                status.TrySetResult(response ?? Status(0));
                notes.TrySetResult([]);
                measured.TrySetResult(latency);
                await dashboard.WaitForIdleAsync(TimeSpan.FromSeconds(2));
            }

            void AssertPendingDisplay()
            {
                DashboardSnapshot pending = dashboard.CurrentSnapshot;
                DashboardViewState view = DashboardStateAdapter.Project(pending);
                Require(pending.IsLoading && view.IsLoading, "The command and tooltip must still expose that a refresh is in progress.");
                Require(view.RealmState == beforeView.RealmState && view.RealmStatusLabel == beforeView.RealmStatusLabel
                    && view.RealmStatusWideLabel == beforeView.RealmStatusWideLabel,
                    "The status label and its color state must not flash while API, notes or TCP observations are pending.");
                Require(view.OnlinePlayersText == beforeView.OnlinePlayersText && view.GatewayLatencyText == beforeView.GatewayLatencyText,
                    "Confirmed metrics must not flash to dashes during an ordinary refresh.");
                Require(pending.FailureCategory == before.FailureCategory && pending.IsStale == before.IsStale
                    && pending.LastSuccessfulRefreshAt == before.LastSuccessfulRefreshAt,
                    "Beginning a retry must neither hide the last failure nor invent a fresh successful observation.");
            }
        }
    }

    private static async Task IgnoreMetricsReturningAfterLogoutAsync()
    {
        TaskCompletionSource<LauncherServerStatus> deferred = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = new()
        {
            Session = FakeLauncherAuthService.CreateSession(),
            StatusHandler = _ => deferred.Task,
            NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>([])
        };
        using LauncherDashboardCoordinator dashboard = new(authentication, CancellationToken.None, _ => { }, gatewayProbe: new MutableProbe { Result = 40 });
        Require(dashboard.TryRefresh() == DashboardRefreshStartStatus.Started, "The pre-logout refresh must begin.");
        authentication.Session = null;
        dashboard.ApplySignedOutSession();
        deferred.SetResult(Status(18));
        Require(await dashboard.WaitForIdleAsync(TimeSpan.FromSeconds(2)), "The late response must be observed.");
        Require(dashboard.CurrentSnapshot.FailureCategory == DashboardFailureCategory.NoSession
            && dashboard.CurrentSnapshot.OnlinePlayers is null && dashboard.CurrentSnapshot.GatewayLatencyMilliseconds is null,
            "A response from a signed-out generation must not restore metrics.");
    }

    private static void ValidateOptionalContractAndTranslations()
    {
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        LauncherServerStatus legacy = JsonSerializer.Deserialize<LauncherServerStatus>(
            """{"realm":"Arthas","api":true,"authentication":true,"realmGateway":true,"worldGateway":true,"worldServer":true,"checkedAt":"2026-09-06T00:00:00Z"}""", json)!;
        Require(legacy.OnlinePlayers is null && legacy.OnlinePlayerCountKind is null, "An older API without metrics must remain unknown and compatible.");
        foreach ((string french, string english) in new[]
        {
            ("joueurs en ligne", "players online"), ("latence", "latency"),
            ("Le nombre de joueurs est indisponible.", "The player count is unavailable."),
            ("Personnages connectés au royaume. Les comptes de bots identifiés sont exclus.", "Characters online in this realm. Identified bot accounts are excluded."),
            ("Personnages connectés au royaume. Cette mesure peut inclure des bots.", "Characters online in this realm. This count may include bots."),
            ("Temps de connexion à la passerelle du jeu (TCP).", "Connection time to the game gateway (TCP)."),
            ("La latence de la passerelle du jeu est indisponible.", "Game gateway latency is unavailable.")
        }) Require(LauncherLocalization.TranslateFromFrench(french) == english, "Every metrics label and tooltip must have its English equivalent.");
    }

    private static LauncherServerStatus Status(int? count, string? kind = null) =>
        new("Arthas", true, true, true, true, true, DateTimeOffset.UtcNow, count, kind);

    private static async Task RefreshAsync(LauncherDashboardCoordinator dashboard)
    {
        Require(dashboard.TryRefresh() == DashboardRefreshStartStatus.Started, "The refresh must begin.");
        Require(await dashboard.WaitForIdleAsync(TimeSpan.FromSeconds(3)), "The refresh must settle.");
    }

    private static async Task ExpectCancellationAsync(Func<Task<int?>> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Caller cancellation must propagate.");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class MutableProbe : ILauncherGameGatewayProbe
    {
        internal int? Result { get; set; }
        internal Func<CancellationToken, Task<int?>>? Handler { get; set; }
        public Task<int?> MeasureAsync(CancellationToken cancellationToken) => Handler?.Invoke(cancellationToken) ?? Task.FromResult(Result);
    }
}
