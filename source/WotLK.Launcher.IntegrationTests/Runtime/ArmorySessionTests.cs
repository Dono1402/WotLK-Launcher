using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;

internal static class ArmorySessionTests
{
    internal static async Task<int> RunAsync()
    {
        await SuccessfulRefreshKeepsSessionAsync();
        await RejectedRefreshExpiresSessionAsync();
        await UnauthorizedResponsesExpireSessionAsync();
        await LateRefreshCannotExpireReconnectedSessionAsync();
        await LateResponsesCannotExpireReconnectedSessionAsync();
        await CancellationDoesNotExpireSessionAsync();
        await InvalidAccountAndUnavailableServiceKeepSessionAsync();
        Console.WriteLine("Armory session OK: rejected refresh, profile/API 401, same-account and different-account reconnect races, cancellation, successful token renewal and non-authentication failures. Fake authentication and HTTP only.");
        return 0;
    }

    private static async Task SuccessfulRefreshKeepsSessionAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthSessionSnapshot before = fixture.Runtime.Session.CurrentSnapshot;
        fixture.Authentication.EnsureFreshHandler = _ =>
        {
            fixture.Authentication.Session = fixture.Authentication.Session! with { AccessToken = "renewed-disposable-token" };
            return Task.FromResult(true);
        };
        Require(await fixture.Runtime.GetArmoryAccountAsync(CancellationToken.None) == 42,
            "A token refresh within the same login must preserve the account lookup.");
        JsonElement data = await fixture.Runtime.GetArmoryDataAsync(42, new(1, "roster"), CancellationToken.None);
        Require(data.GetProperty("characters").GetArrayLength() == 0, "A valid armory response must remain usable.");
        AssertSessionUnchanged(fixture, before);
    }

    private static async Task RejectedRefreshExpiresSessionAsync()
    {
        foreach (bool lookup in new[] { true, false })
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            fixture.Authentication.EnsureFreshHandler = _ =>
            {
                // The real authentication service clears its session when auth/refresh returns 401.
                fixture.Authentication.Session = null;
                return Task.FromResult(false);
            };
            if (lookup)
                Require(await fixture.Runtime.GetArmoryAccountAsync(CancellationToken.None) is null,
                    "A rejected refresh must not open the armory.");
            else await ThrowsAsync<UnauthorizedAccessException>(() => ReadAsync(fixture, lookup));
            AssertExpired(fixture);
            Require(fixture.Http.Requests.Count == 0, "A rejected refresh must stop before profile or armory HTTP.");
        }
        foreach (bool lookup in new[] { true, false })
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            fixture.Authentication.EnsureFreshHandler = _ => Task.FromException<bool>(
                new LauncherAuthException("Fixture refresh rejected", HttpStatusCode.Unauthorized));
            await ThrowsAsync<LauncherAuthException>(() => ReadAsync(fixture, lookup));
            AssertExpired(fixture);
        }
    }

    private static async Task UnauthorizedResponsesExpireSessionAsync()
    {
        foreach (string operation in new[] { "profile", "roster", "catalog" })
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            fixture.Http.OnSend = (_, _) => Task.FromResult(Response(HttpStatusCode.Unauthorized));
            if (operation == "profile")
                await ThrowsAsync<AvatarMediaException>(() => fixture.Runtime.GetArmoryAccountAsync(CancellationToken.None));
            else
                await ThrowsAsync<UnauthorizedAccessException>(() => fixture.Runtime.GetArmoryDataAsync(42,
                    operation == "roster" ? new(1, "roster") : new(1, "catalog", 100), CancellationToken.None));
            AssertExpired(fixture);
            Require(fixture.Http.Requests.Count == 1, "A 401 must expire only the request's own session without retrying HTTP.");
        }
    }

    private static async Task LateRefreshCannotExpireReconnectedSessionAsync()
    {
        foreach (bool lookup in new[] { true, false })
        foreach (uint nextAccount in new uint[] { 42, 84 })
        foreach (bool accepted in new[] { false, true })
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            TaskCompletionSource<bool> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Authentication.EnsureFreshHandler = _ => refresh.Task;
            Task pending = ReadAsync(fixture, lookup);
            AuthSessionSnapshot connected = await fixture.ReconnectAsync(nextAccount);
            refresh.SetResult(accepted);
            if (lookup)
            {
                await pending;
                Require(((Task<uint?>)pending).Result is null, "A stale account lookup must never open a later login's armory.");
            }
            else await ThrowsAsync<UnauthorizedAccessException>(() => pending);
            AssertSessionUnchanged(fixture, connected, nextAccount);
            Require(fixture.Http.Requests.Count == 0, "A refresh from a former login must never start an HTTP request using the new session.");
        }
    }

    private static async Task LateResponsesCannotExpireReconnectedSessionAsync()
    {
        foreach (bool lookup in new[] { true, false })
        foreach (uint nextAccount in new uint[] { 42, 84 })
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.OK })
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<HttpResponseMessage> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Http.OnSend = (_, _) => { entered.TrySetResult(); return reply.Task; };
            Task pending = ReadAsync(fixture, lookup);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            AuthSessionSnapshot connected = await fixture.ReconnectAsync(nextAccount);
            reply.SetResult(status == HttpStatusCode.Unauthorized ? Response(status)
                : lookup ? ProfileResponse(Fixture.CreateSession(42).Profile) : Response(status));
            if (lookup && status == HttpStatusCode.OK)
            {
                await pending;
                Require(((Task<uint?>)pending).Result is null, "A completed profile response from a former login must be discarded.");
            }
            else if (lookup) await ThrowsAsync<AvatarMediaException>(() => pending);
            else await ThrowsAsync<UnauthorizedAccessException>(() => pending);
            AssertSessionUnchanged(fixture, connected, nextAccount);
            int invalidations = fixture.Authentication.InvalidateLocalSessionCalls;
            fixture.Runtime.Session.NotifyAuthenticatedRequestUnauthorized(connected.Sequence - 1);
            Require(fixture.Authentication.InvalidateLocalSessionCalls == invalidations,
                "The coordinator must reject a stale generation before clearing any credentials.");
            AssertSessionUnchanged(fixture, connected, nextAccount);
        }
    }

    private static async Task CancellationDoesNotExpireSessionAsync()
    {
        foreach (bool lookup in new[] { true, false })
        foreach (string stage in new[] { "before", "refresh", "response" })
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            AuthSessionSnapshot before = fixture.Runtime.Session.CurrentSnapshot;
            using CancellationTokenSource cancellation = new();
            TaskCompletionSource<bool> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<HttpResponseMessage> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (stage == "before") cancellation.Cancel();
            if (stage == "refresh") fixture.Authentication.EnsureFreshHandler = _ => refresh.Task;
            if (stage == "response") fixture.Http.OnSend = (_, _) => reply.Task;
            Task pending = ReadAsync(fixture, lookup, cancellation.Token);
            cancellation.Cancel();
            if (stage == "refresh") refresh.SetResult(false);
            if (stage == "response") reply.SetResult(Response(HttpStatusCode.Unauthorized));
            await ThrowsAsync<OperationCanceledException>(() => pending);
            AssertSessionUnchanged(fixture, before);
            Require(fixture.Authentication.InvalidateLocalSessionCalls == 0,
                "Cancellation must not clear valid credentials, even if a refused response is already completing.");
        }
        await using Fixture timeout = await Fixture.CreateAsync();
        AuthSessionSnapshot active = timeout.Runtime.Session.CurrentSnapshot;
        timeout.Authentication.EnsureFreshHandler = _ => Task.FromException<bool>(new TaskCanceledException("Fixture timeout"));
        await ThrowsAsync<TaskCanceledException>(() => timeout.Runtime.GetArmoryDataAsync(42, new(1, "roster"), CancellationToken.None));
        AssertSessionUnchanged(timeout, active);
    }

    private static async Task InvalidAccountAndUnavailableServiceKeepSessionAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthSessionSnapshot before = fixture.Runtime.Session.CurrentSnapshot;
        foreach (uint account in new uint[] { 0, 84 })
            await ThrowsAsync<UnauthorizedAccessException>(() => fixture.Runtime.GetArmoryDataAsync(account, new(1, "roster"), CancellationToken.None));
        Require(fixture.Authentication.EnsureFreshCalls == 0 && fixture.Http.Requests.Count == 0,
            "A request for another account must fail before refreshing credentials or calling HTTP.");
        fixture.Http.OnSend = (_, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable));
        await ThrowsAsync<HttpRequestException>(() => ReadAsync(fixture, lookup: false));
        await ThrowsAsync<AvatarMediaException>(() => ReadAsync(fixture, lookup: true));
        AssertSessionUnchanged(fixture, before);
        Require(fixture.Authentication.InvalidateLocalSessionCalls == 0, "A 503 must not be treated as an expired session.");
    }

    private static Task ReadAsync(Fixture fixture, bool lookup, CancellationToken cancellationToken = default)
        => lookup ? fixture.Runtime.GetArmoryAccountAsync(cancellationToken)
            : fixture.Runtime.GetArmoryDataAsync(42, new(1, "roster"), cancellationToken);

    private static void AssertExpired(Fixture fixture)
    {
        AuthSessionSnapshot snapshot = fixture.Runtime.Session.CurrentSnapshot;
        Require(snapshot.State == LauncherSessionState.SignedOut
            && snapshot.FailureCategory == LauncherSessionFailureCategory.SessionExpired
            && fixture.Authentication.Session is null && fixture.Authentication.InvalidateLocalSessionCalls == 1,
            "A current-session refusal must clear credentials and publish SessionExpired exactly once.");
    }

    private static void AssertSessionUnchanged(Fixture fixture, AuthSessionSnapshot expected, uint account = 42)
        => Require(ReferenceEquals(fixture.Runtime.Session.CurrentSnapshot, expected)
            && expected.IsAuthenticated && fixture.Authentication.Session?.Profile.AccountId == account,
            "A current or newly reconnected session must retain its exact generation and credentials.");

    private static HttpResponseMessage Response(HttpStatusCode status)
        => new(status) { Content = new StringContent("{\"characters\":[]}") };

    private static HttpResponseMessage ProfileResponse(LauncherProfile profile)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(profile)) };

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryClient _client = new();
        internal FakeLauncherAuthService Authentication { get; } = new() { RestoreResult = true, Session = CreateSession(42) };
        internal CallbackHandler Http { get; } = new();
        internal LauncherRuntime Runtime { get; }

        private Fixture()
        {
            Http.OnSend = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
                ? ProfileResponse(Authentication.Session!.Profile) : Response(HttpStatusCode.OK));
            Runtime = new LauncherRuntime(new LauncherRuntimeDependencies
            {
                LoadSettings = () => _client.Settings,
                CreateAuthentication = () => Authentication,
                GameClientStateReader = new GameClientStateReader(),
                GetLauncherVersion = () => "armory-session-test",
                CreateAuthorizedHttpClient = _ => new HttpClient(Http),
                AvatarApiBaseUri = new Uri("https://armory-session.invalid/api/v1/"),
                GetAvatarCacheRoot = () => Path.Combine(_client.Root, "avatars"),
                EnableSelfUpdate = false,
                CreateLauncherSelfUpdateTimer = interval => new RuntimeCompositionSelfUpdateTimer(interval),
                CreateLauncherSelfUpdateClient = _ => new RuntimeCompositionSelfUpdateClient(),
                LauncherSelfUpdateFinalizer = new RuntimeCompositionSelfUpdateFinalizer(),
                CreateGameVerificationService = (_, _) => new RuntimeVerificationStub(),
                CreateGameMaintenanceService = (_, _) => new RuntimeMaintenanceStub(),
                CreateGameLaunchService = _ => new FakeGameLaunchService(),
                CreateGameProcessMonitor = () => new FakeGameProcessMonitor(),
                HasPlayableClient = _ => false,
                IsGameRunning = _ => false
            });
        }

        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                Require((await fixture.Runtime.Session.RestoreOnceAsync()).Status == LauncherSessionRestoreStatus.Restored,
                    "The disposable session fixture must restore through the real coordinator.");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal async Task<AuthSessionSnapshot> ReconnectAsync(uint account)
        {
            LauncherSessionStartResult logout = Runtime.Session.TryLogout(CancellationToken.None);
            Require(logout.IsStarted && (await logout.Completion!).Status == LauncherSessionCompletionStatus.Succeeded,
                "The old fixture session must log out through the real coordinator.");
            Authentication.LoginHandler = (_, _, _) => Task.FromResult(CreateSession(account));
            LauncherSessionStartResult login = Runtime.Session.TryLogin("Armory" + account, "disposable-password");
            Require(login.IsStarted && (await login.Completion!).Status == LauncherSessionCompletionStatus.Succeeded,
                "The fixture must reconnect through the real coordinator.");
            return Runtime.Session.CurrentSnapshot;
        }

        internal static LauncherAuthSession CreateSession(uint account)
        {
            LauncherAuthSession session = FakeLauncherAuthService.CreateSession("Armory" + account);
            return session with { Profile = session.Profile with { AccountId = account, AvatarKey = null } };
        }

        public async ValueTask DisposeAsync()
        {
            Runtime.BeginShutdown();
            await Runtime.WaitForShutdownAsync(TimeSpan.FromSeconds(2));
            Runtime.Dispose();
            _client.Dispose();
        }
    }

    private sealed class CallbackHandler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; }
            = (_, _) => throw new InvalidOperationException("Unexpected fixture HTTP.");
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return OnSend(request, cancellationToken);
        }
    }
}
