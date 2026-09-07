using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

internal static class PresenceRuntimeTests
{
    private static int _checks;
    internal static async Task<int> RunAsync()
    {
        _checks = 0;
        await IdleAndManualStatusAsync();
        await AcknowledgementAndFailureAsync();
        await AccountAndShutdownIsolationAsync();
        await UnauthorizedAsync();
        await HttpContractAsync();
        FriendsPresentation();
        Console.WriteLine($"Presence runtime PASS: {_checks} assertions. Inactivity boundary, manual status persistence, server acknowledgement, stale responses, account isolation, shutdown, authentication and bounded HTTP. Injected activity and clock; no desktop input or production network.");
        return 0;
    }

    private static async Task IdleAndManualStatusAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(start: false);
        Check(fixture.Api.Requests.IsEmpty, "Presence construction and session restoration do not publish before Start.");
        fixture.Presence.Start(); fixture.Presence.Start();
        await Until(() => fixture.Presence.CurrentSnapshot.IsAvailable, "Initial heartbeat completes.");
        await fixture.Presence.WaitForIdleAsync(TimeSpan.FromSeconds(3));
        Check(fixture.Time.Created == 1 && fixture.Api.Requests.Count == 1, "Repeated Start has one timer and one initial heartbeat.");
        Check(fixture.Presence.CurrentSnapshot.Status == "online", "Connected account takes its server-confirmed status.");
        fixture.Idle.Value = TimeSpan.FromSeconds(1199);
        await fixture.Presence.RefreshAsync();
        Check(fixture.Presence.CurrentSnapshot.Status == "online" && fixture.Api.Requests.Last().Request.IdleSeconds == 1199,
            "Twenty-minute boundary has not been reached at 1199 seconds.");
        fixture.Idle.Value = TimeSpan.FromSeconds(1200);
        fixture.Time.Timer!.Fire();
        await Until(() => fixture.Presence.CurrentSnapshot.IsAutomaticAway, "Timer transmits inactivity and renders automatic away.");
        await fixture.Presence.WaitForIdleAsync(TimeSpan.FromSeconds(3));
        Check(fixture.Api.Requests.Last().Request.IdleSeconds == 1200 && fixture.Presence.CurrentSnapshot.ManualStatus == "online",
            "Automatic away retains the manual online preference.");
        fixture.Idle.Value = TimeSpan.Zero;
        Check(await fixture.Presence.RefreshAsync() && fixture.Presence.CurrentSnapshot.Status == "online"
            && !fixture.Presence.CurrentSnapshot.IsAutomaticAway, "New activity restores automatic away to online.");
        foreach (string manual in new[] { "away", "dnd", "offline" })
        {
            Check(await fixture.Presence.SetStatusAsync(manual), "Manual status accepted: " + manual);
            fixture.Idle.Value = TimeSpan.FromHours(1); await fixture.Presence.RefreshAsync();
            fixture.Idle.Value = TimeSpan.Zero; await fixture.Presence.RefreshAsync();
            Check(fixture.Presence.CurrentSnapshot.Status == manual && !fixture.Presence.CurrentSnapshot.IsAutomaticAway,
                "Activity and inactivity do not override the manual status: " + manual);
            Check(fixture.Presence.CurrentSnapshot.DoNotDisturb == (manual == "dnd"), "Only DND suppresses notifications: " + manual);
        }
        fixture.Idle.Value = TimeSpan.FromSeconds(-1); await fixture.Presence.RefreshAsync();
        Check(fixture.Api.Requests.Last().Request.IdleSeconds == 0, "Invalid negative idle duration is clamped.");
    }

    private static async Task AcknowledgementAndFailureAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Gate gate = fixture.Api.BlockNext();
        Task<bool> changing = fixture.Presence.SetStatusAsync("dnd");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(fixture.Presence.CurrentSnapshot.IsUpdating && !fixture.Presence.CurrentSnapshot.DoNotDisturb
            && fixture.Presence.CurrentSnapshot.Status == "online", "Manual selection remains pending until the server acknowledges it.");
        Check(!await fixture.Presence.SetStatusAsync("away"), "Concurrent manual changes are rejected while a request is pending.");
        gate.Release.TrySetResult();
        Check(await changing && fixture.Presence.CurrentSnapshot.DoNotDisturb, "Successful server acknowledgement applies DND.");
        foreach (HttpStatusCode code in new[] { HttpStatusCode.NotFound, HttpStatusCode.NotImplemented })
        {
            fixture.Api.NextError = new LauncherPresenceApiException(code, "presence-unavailable");
            Check(!await fixture.Presence.SetStatusAsync("offline") && fixture.Presence.CurrentSnapshot.Status == "dnd"
                && !fixture.Presence.CurrentSnapshot.IsAvailable && fixture.Presence.CurrentSnapshot.ErrorCode == "presence-unavailable",
                "Old server cannot report a new status as successfully applied: " + (int)code);
        }
        Check(await fixture.Presence.RefreshAsync() && fixture.Presence.CurrentSnapshot.IsAvailable,
            "Heartbeat recovers when the server capability becomes available.");
        fixture.Api.NextError = new HttpRequestException("Synthetic unavailable transport.");
        Check(!await fixture.Presence.SetStatusAsync("online") && fixture.Presence.CurrentSnapshot.DoNotDisturb
            && !fixture.Presence.CurrentSnapshot.IsUpdating, "Network failure preserves the confirmed manual status and clears pending state.");
        await Throws<ArgumentException>(() => fixture.Presence.SetStatusAsync("invisible"), "Unsupported status is rejected before transport.");
    }

    private static async Task AccountAndShutdownIsolationAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Gate old = fixture.Api.BlockNext();
        Task<bool> oldRequest = fixture.Presence.SetStatusAsync("dnd");
        await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.TryLogout(CancellationToken.None).Completion!;
        Check(fixture.Presence.CurrentSnapshot.OwnerAccountId is null && !fixture.Presence.CurrentSnapshot.DoNotDisturb,
            "Signing out immediately clears the previous account's status.");
        LauncherAuthSession second = FakeLauncherAuthService.CreateSession();
        second = second with { Profile = second.Profile with { AccountId = 77 } };
        fixture.Auth.LoginHandler = (_, _, _) => Task.FromResult(second);
        await fixture.Session.TryLogin("PresenceFixture77", "synthetic-password").Completion!;
        await Until(() => fixture.Presence.CurrentSnapshot.OwnerAccountId == 77 && fixture.Presence.CurrentSnapshot.IsAvailable,
            "Second account gets its own confirmed presence.");
        old.Release.TrySetResult();
        Check(!await oldRequest && fixture.Presence.CurrentSnapshot.OwnerAccountId == 77
            && fixture.Presence.CurrentSnapshot.Status == "online", "Late server response cannot copy the old account's status into the new account.");
        Gate pending = fixture.Api.BlockNext();
        Task<bool> pendingRequest = fixture.Presence.SetStatusAsync("away");
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Presence.BeginShutdown();
        Check(!await fixture.Presence.WaitForIdleAsync(TimeSpan.FromMilliseconds(10)), "Shutdown wait reports a transport that has not finished.");
        Check(!await fixture.Presence.RefreshAsync() && fixture.Time.Timer!.Due == Timeout.InfiniteTimeSpan,
            "Shutdown disarms the timer and rejects new heartbeats.");
        pending.Release.TrySetResult();
        Check(!await pendingRequest && await fixture.Presence.WaitForIdleAsync(TimeSpan.FromSeconds(3)),
            "Late completion after shutdown is ignored and can be drained.");
    }

    private static async Task UnauthorizedAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Api.NextError = new LauncherPresenceApiException(HttpStatusCode.Unauthorized, "presence-request-failed");
        Check(!await fixture.Presence.RefreshAsync(), "Unauthorized heartbeat is unsuccessful.");
        await Until(() => !fixture.Session.CurrentSnapshot.IsAuthenticated, "Unauthorized presence invalidates the matching session.");
        Check(fixture.Presence.CurrentSnapshot.OwnerAccountId is null, "Unauthorized session clears its visible account presence.");
    }

    private static async Task HttpContractAsync()
    {
        using HttpFixture handler = new(); using HttpClient http = new(handler);
        LauncherPresenceApiClient client = new(http, new Uri("https://fixture.invalid/wotlk/api/v1/"));
        LauncherPresenceStateDto state = await client.UpdateAsync(new("away", 1200), CancellationToken.None);
        Check(handler.Uri?.AbsoluteUri == "https://fixture.invalid/wotlk/api/v1/me/presence" && handler.Method == HttpMethod.Put,
            "Presence uses the authenticated API's application prefix and PUT route.");
        using JsonDocument request = JsonDocument.Parse(handler.Body!);
        Check(request.RootElement.GetProperty("status").GetString() == "away" && request.RootElement.GetProperty("idleSeconds").GetInt32() == 1200
            && state.AccountId == 42, "Presence JSON preserves the explicit status, inactivity and account.");
        int before = handler.Calls;
        await Throws<ArgumentException>(() => client.UpdateAsync(new("game", 0), CancellationToken.None), "Unsupported wire status rejected.");
        await Throws<ArgumentException>(() => client.UpdateAsync(new(null, -1), CancellationToken.None), "Negative wire inactivity rejected.");
        Check(handler.Calls == before, "Invalid input causes no HTTP request.");
        foreach (HttpStatusCode code in new[] { HttpStatusCode.NotFound, HttpStatusCode.NotImplemented })
        {
            handler.Status = code;
            try { await client.UpdateAsync(new(null, 0), CancellationToken.None); throw new InvalidOperationException("Expected API rejection."); }
            catch (LauncherPresenceApiException error) { Check(error.Code == "presence-unavailable", "Missing capability is mapped for old servers: " + (int)code); }
        }
        handler.Status = HttpStatusCode.OK;
        foreach (string invalid in new[]
        {
            "{\"accountId\":0,\"status\":\"online\",\"manualStatus\":\"online\",\"version\":1}",
            "{\"accountId\":42,\"status\":\"online\",\"manualStatus\":\"away\",\"version\":1}",
            "{\"accountId\":42,\"status\":\"away\",\"manualStatus\":\"online\",\"isAutomaticAway\":false,\"version\":1}",
            "{\"accountId\":42,\"status\":\"online\",\"manualStatus\":\"online\",\"version\":0}"
        })
        {
            handler.Json = invalid;
            await Throws<InvalidDataException>(() => client.UpdateAsync(new(null, 0), CancellationToken.None), "Inconsistent presence response is rejected.");
        }
        handler.Json = new string(' ', 9000);
        await Throws<InvalidDataException>(() => client.UpdateAsync(new(null, 0), CancellationToken.None), "Presence response is bounded to 8192 bytes.");
    }

    private static void FriendsPresentation()
    {
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        FriendRuntimeItem friend = new(91, "PresenceFixture", null, null, FriendRelationship.Accepted,
            true, "Fixture", 80, 8, 0, null, IsLauncherOnline: true);
        foreach ((string status, string label, string color) in new[]
        {
            ("away", "Absent", "#E9B44C"), ("dnd", "Ne pas déranger", "#EE6873"), ("offline", "Hors ligne", "#8995A8")
        })
        {
            FriendsRuntimeSnapshot snapshot = FriendsRuntimeSnapshot.SignedOut with
            {
                IsAuthenticated = true, CurrentUserId = 42, LoadState = FriendsLoadState.Loaded,
                Friends = [friend with { Presence = status }]
            };
            FriendUiItem ui = FriendsStateAdapter.Project(snapshot).Friends.Single();
            Check(ui.Presence == status && ui.PresenceText == label && ui.ProfilePresenceText == label && ui.PresenceColor == color,
                "Friends list and profile share the explicit presence label and color: " + status);
            Check(ui.IsOnline == (status != "offline"), "Offline presence is placed outside the connected friends list.");
        }
        FriendRuntimeItem launcher = friend with { IsOnline = false, CharacterName = null, Presence = "online" };
        Check(FriendsStateAdapter.GetPresenceText(launcher) == "En ligne", "New connected status uses the requested online label.");
        Check(FriendsStateAdapter.GetPresenceText(launcher with { Presence = null }) == "Connecté au launcher",
            "Legacy server presence remains supported when no explicit status is sent.");
        try
        {
            LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
            Check(FriendsStateAdapter.GetPresenceText(friend with { Presence = "away" }) == "Away"
                && FriendsStateAdapter.GetPresenceText(friend with { Presence = "dnd" }) == "Do not disturb",
                "New friend status labels follow English locale.");
        }
        finally { LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale); }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Interlocked.Increment(ref _checks); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { Check(true, message); return; } throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition, string message)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (!condition()) { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(message); await Task.Delay(5); }
    }
    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Idle : ILauncherIdleTimeSource
    {
        internal TimeSpan Value;
        public TimeSpan GetIdleTime() => Value;
    }
    private sealed class Api(Func<uint> owner) : ILauncherPresenceApiClient
    {
        private readonly ConcurrentDictionary<uint, string> _manual = new();
        private Gate? _nextGate;
        internal Exception? NextError;
        internal ConcurrentQueue<(uint Owner, LauncherPresenceUpdateRequest Request)> Requests { get; } = new();
        internal Gate BlockNext() { Gate gate = new(); _nextGate = gate; return gate; }
        public async Task<LauncherPresenceStateDto> UpdateAsync(LauncherPresenceUpdateRequest request, CancellationToken token)
        {
            uint account = owner(); Requests.Enqueue((account, request));
            Gate? gate = Interlocked.Exchange(ref _nextGate, null);
            if (gate is not null) { gate.Entered.TrySetResult(); await gate.Release.Task; }
            Exception? error = Interlocked.Exchange(ref NextError, null);
            if (error is not null) throw error;
            if (request.Status is not null) _manual[account] = request.Status;
            string manual = _manual.GetOrAdd(account, "online");
            bool automatic = manual == "online" && request.IdleSeconds >= 1200;
            return new() { AccountId = account, ManualStatus = manual, Status = automatic ? "away" : manual,
                IsAutomaticAway = automatic, Version = 1 };
        }
    }
    private sealed class ManualTime : TimeProvider
    {
        internal int Created;
        internal ManualTimer? Timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Created++; return Timer = new(callback, state, dueTime); }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        internal TimeSpan Due = due;
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return !_disposed; }
        internal void Fire() { if (!_disposed && Due != Timeout.InfiniteTimeSpan) callback(state); }
        public void Dispose() { _disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        internal FakeLauncherAuthService Auth { get; }
        internal LauncherSessionCoordinator Session { get; }
        internal LauncherPresenceCoordinator Presence { get; }
        internal Idle Idle { get; } = new();
        internal ManualTime Time { get; } = new();
        internal Api Api { get; }
        private Fixture()
        {
            LauncherAuthSession session = FakeLauncherAuthService.CreateSession();
            Auth = new() { Session = session with { Profile = session.Profile with { AccountId = 42 } }, RestoreResult = true,
                EnsureFreshHandler = _ => Task.FromResult(true) };
            Session = new(Auth, _lifetime.Token, _ => { });
            Api = new(() => Auth.Session?.Profile.AccountId ?? 0);
            Presence = new(Session, Auth, Api, _lifetime.Token, _ => { }, Time, Idle);
        }
        internal static async Task<Fixture> CreateAsync(bool start = true)
        {
            Fixture fixture = new(); await fixture.Session.RestoreOnceAsync();
            if (start)
            {
                fixture.Presence.Start();
                await Until(() => fixture.Presence.CurrentSnapshot.IsAvailable, "Initial presence available.");
                await fixture.Presence.WaitForIdleAsync(TimeSpan.FromSeconds(3));
            }
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            Presence.BeginShutdown(); Session.BeginShutdown(); _lifetime.Cancel();
            await Presence.WaitForIdleAsync(TimeSpan.FromSeconds(3));
            Presence.Dispose(); Session.Dispose(); Auth.Dispose(); _lifetime.Dispose();
        }
    }
    private sealed class HttpFixture : HttpMessageHandler
    {
        internal int Calls;
        internal Uri? Uri;
        internal HttpMethod? Method;
        internal string? Body;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string Json = "{\"accountId\":42,\"status\":\"away\",\"manualStatus\":\"away\",\"isAutomaticAway\":false,\"version\":1}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri; Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(Status) { Content = new StringContent(Json, Encoding.UTF8, "application/json") };
        }
    }
}
