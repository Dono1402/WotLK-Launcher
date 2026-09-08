using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using WotLK.Launcher;

internal static class LauncherAuthServiceConcurrencyTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);
    private static int _checks;

    internal static async Task<int> RunAsync()
    {
        _checks = 0;
        try
        {
            await RefreshAfterLogoutAsync();
            await RefreshAfterNewSessionAsync(HttpStatusCode.Unauthorized);
            await RefreshAfterNewSessionAsync(HttpStatusCode.OK);
            await CurrentRefreshUnauthorizedAsync();
            await CancelledUnauthorizedResponsesAsync();
            await ConcurrentRefreshAsync();
            await CancelledRefreshWaiterAsync();
            await LogoutFailuresAsync();
            await PreCancelledLogoutAsync();
            await LogoutAfterNewSessionAsync();
            await FailedLocalClearAsync();
            await ProfileResponseGuardsAsync();
            await LoginAndRegistrationGuardsAsync();
            await RestoreResponseGuardsAsync();
            await DisposeDuringRefreshAsync();
            Console.WriteLine($"Launcher auth service concurrency PASS: {_checks} assertions; real service with fake HTTP handler, in-memory session storage and SSO callback only; no windows, registry, real sessions, filesystem or server access.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task RefreshAfterLogoutAsync()
    {
        using Fixture fixture = new();
        LauncherAuthSession accountA = Session(101, fresh: false);
        fixture.Service.CommitSession(accountA, clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Task<bool> pendingRefresh = fixture.Service.EnsureFreshAsync();
        RequestSnapshot request = await Entered(refresh);
        using (JsonDocument body = JsonDocument.Parse(request.Body))
            Check(body.RootElement.GetProperty("refreshToken").GetString() == accountA.RefreshToken,
                "The refresh request captures the original account's refresh token.");

        Task pendingLogout = fixture.Service.LogoutAsync();
        RequestSnapshot logoutRequest = await Entered(logout);
        Check(!pendingLogout.IsCompleted, "The logout transport is deliberately still pending.");
        CheckLoggedOut(fixture, "Local logout precedes the remote response");
        Check(logoutRequest.BearerToken == accountA.AccessToken, "Remote logout uses the captured old access token after local credentials are cleared.");
        logout.Complete(new(HttpStatusCode.NoContent));
        await pendingLogout.WaitAsync(StepTimeout);
        refresh.Complete(Json(Session(101, fresh: true, revision: "late")));
        Check(!await pendingRefresh.WaitAsync(StepTimeout), "A successful refresh received after logout reports false.");
        CheckLoggedOut(fixture, "A late refresh cannot restore the logged-out account");
        Check(fixture.Store.SaveCalls == 1 && fixture.Store.ClearCalls == 1 && fixture.SsoClearCalls == 1,
            "The stale refresh neither persists credentials nor repeats logout side effects.");
    }

    private static async Task RefreshAfterNewSessionAsync(HttpStatusCode status)
    {
        using Fixture fixture = new();
        fixture.Service.CommitSession(Session(101, fresh: false), clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Task<bool> pending = fixture.Service.EnsureFreshAsync();
        await Entered(refresh);
        LauncherAuthSession accountB = Session(202, fresh: true);
        fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
        refresh.Complete(status == HttpStatusCode.OK ? Json(Session(101, fresh: true, revision: "late")) : new(status));
        Check(!await pending.WaitAsync(StepTimeout), $"A stale refresh with status {(int)status} reports false.");
        CheckCurrent(fixture, accountB, $"Refresh status {(int)status} cannot overwrite or clear a newer account");
        Check(fixture.Store.SaveCalls == 2 && fixture.Store.ClearCalls == 0 && fixture.SsoClearCalls == 0,
            "A stale refresh produces no persistence or SSO side effects after an explicit new session.");
    }

    private static async Task ConcurrentRefreshAsync()
    {
        using Fixture fixture = new();
        fixture.Service.CommitSession(Session(101, fresh: false), clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Task<bool> first = fixture.Service.EnsureFreshAsync();
        await Entered(refresh);
        Task<bool>[] others = Enumerable.Range(0, 7).Select(_ => fixture.Service.EnsureFreshAsync()).ToArray();
        Check(fixture.Handler.RequestCount == 1, "Concurrent refresh callers share one pending HTTP refresh.");
        LauncherAuthSession renewed = Session(101, fresh: true, revision: "renewed");
        refresh.Complete(Json(renewed));
        bool[] results = await Task.WhenAll(new[] { first }.Concat(others)).WaitAsync(StepTimeout);
        Check(results.All(result => result), "All callers reuse the accepted fresh session after waiting for the refresh gate.");
        Check(fixture.Handler.RequestCount == 1 && fixture.Store.SaveCalls == 2,
            "Concurrent callers make only one HTTP request and one accepted refresh save.");
        CheckCurrent(fixture, renewed, "The accepted refresh updates both memory and the refresh token store", requireSameInstance: false);
        Check(await fixture.Service.EnsureFreshAsync() && fixture.Handler.RequestCount == 1,
            "An already fresh access token is reused without another HTTP request.");
    }

    private static async Task CurrentRefreshUnauthorizedAsync()
    {
        using Fixture fixture = new();
        fixture.Service.CommitSession(Session(101, fresh: false), clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Task<bool> pending = fixture.Service.EnsureFreshAsync();
        await Entered(refresh);
        refresh.Complete(new(HttpStatusCode.Unauthorized));
        Check(!await pending.WaitAsync(StepTimeout), "A current rejected refresh still reports false.");
        CheckLoggedOut(fixture, "A current HTTP 401 invalidates its own account");
        Check(fixture.Store.SaveCalls == 1 && fixture.Store.ClearCalls == 1 && fixture.SsoClearCalls == 1,
            "A current 401 clears both local credential stores exactly once.");
        Check(!await fixture.Service.EnsureFreshAsync() && fixture.Handler.RequestCount == 1,
            "After current-session rejection, refresh does not restart without a new explicit session.");
    }

    private static async Task CancelledUnauthorizedResponsesAsync()
    {
        foreach (bool restore in new[] { false, true })
        {
            using Fixture fixture = new();
            LauncherAuthSession account = Session(101, fresh: false);
            if (restore) fixture.Store.Seed(new(account.RefreshToken, account.RefreshExpiresAt));
            else fixture.Service.CommitSession(account, clearGameSingleSignOn: false);
            Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            using CancellationTokenSource cancellation = new();
            Task pending = restore ? fixture.Service.PrepareRestoreAsync(cancellation.Token)
                : fixture.Service.EnsureFreshAsync(cancellation.Token);
            await Entered(refresh);
            // Release a 401 from an intentionally non-cooperative transport only
            // after the caller cancelled. No timing sleeps or scheduler races.
            cancellation.Cancel();
            refresh.Complete(new(HttpStatusCode.Unauthorized));
            await ThrowsAsync<OperationCanceledException>(pending,
                $"A cancelled {(restore ? "restore" : "refresh")} remains cancellation even if the transport returns 401.");
            Check(fixture.Store.Current?.RefreshToken == account.RefreshToken
                && fixture.Store.ClearCalls == 0 && fixture.SsoClearCalls == 0,
                "A cancelled 401 cannot clear persistent session or SSO credentials.");
            Check(restore ? fixture.Service.Session is null : ReferenceEquals(fixture.Service.Session, account),
                "A cancelled 401 does not change the previously visible session state.");
        }
    }

    private static async Task CancelledRefreshWaiterAsync()
    {
        using Fixture fixture = new();
        fixture.Service.CommitSession(Session(101, fresh: false), clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Task<bool> first = fixture.Service.EnsureFreshAsync();
        await Entered(refresh);
        using CancellationTokenSource cancellation = new();
        Task<bool> waiter = fixture.Service.EnsureFreshAsync(cancellation.Token);
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(waiter, "Cancelling one refresh waiter cancels that wait only.");
        Check(!first.IsCompleted && fixture.Handler.RequestCount == 1, "Cancelling a waiter leaves the active refresh intact.");
        refresh.Complete(Json(Session(101, fresh: true, revision: "survives-waiter")));
        Check(await first.WaitAsync(StepTimeout) && fixture.Service.Session is not null,
            "The uncancelled refresh still completes successfully.");
    }

    private static async Task LogoutFailuresAsync()
    {
        foreach (string failure in new[] { "network", "timeout", "cancellation" })
        {
            using Fixture fixture = new();
            fixture.Service.CommitSession(Session(101, fresh: true), clearGameSingleSignOn: false);
            Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
            using CancellationTokenSource cancellation = new();
            Task pending = fixture.Service.LogoutAsync(cancellation.Token);
            await Entered(logout);
            CheckLoggedOut(fixture, $"Local cleanup is immediate while remote logout may fail through {failure}");
            if (failure == "cancellation") cancellation.Cancel();
            logout.Fail(failure switch
            {
                "network" => new HttpRequestException("Synthetic transport failure."),
                "timeout" => new TaskCanceledException("Synthetic HTTP timeout."),
                _ => new OperationCanceledException("Synthetic caller cancellation.", cancellation.Token)
            });
            await pending.WaitAsync(StepTimeout);
            CheckLoggedOut(fixture, $"Remote {failure} does not undo local logout");
            Check(fixture.Store.ClearCalls == 1 && fixture.SsoClearCalls == 1,
                "Remote logout failures are swallowed without repeating local cleanup.");
        }
    }

    private static async Task PreCancelledLogoutAsync()
    {
        using Fixture fixture = new();
        fixture.Service.CommitSession(Session(101, fresh: true), clearGameSingleSignOn: false);
        Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        logout.Complete(new(HttpStatusCode.NoContent));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await fixture.Service.LogoutAsync(cancellation.Token).WaitAsync(StepTimeout);
        CheckLoggedOut(fixture, "An already cancelled caller token cannot prevent local logout");
        Check(fixture.Store.ClearCalls == 1 && fixture.SsoClearCalls == 1,
            "An already cancelled logout still clears both local credential stores exactly once.");
    }

    private static async Task LogoutAfterNewSessionAsync()
    {
        using Fixture fixture = new();
        LauncherAuthSession accountA = Session(101, fresh: true);
        fixture.Service.CommitSession(accountA, clearGameSingleSignOn: false);
        Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Task pending = fixture.Service.LogoutAsync();
        RequestSnapshot request = await Entered(logout);
        CheckLoggedOut(fixture, "Logout clears A before a new account is accepted");
        LauncherAuthSession accountB = Session(202, fresh: true);
        fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
        logout.Complete(new(HttpStatusCode.NoContent));
        await pending.WaitAsync(StepTimeout);
        Check(request.BearerToken == accountA.AccessToken, "A pending logout targets account A's captured token.");
        CheckCurrent(fixture, accountB, "A late logout response cannot erase account B");
        Check(fixture.Store.ClearCalls == 1 && fixture.Store.SaveCalls == 2 && fixture.SsoClearCalls == 1,
            "Logout performs no second cleanup after its remote response.");
    }

    private static async Task FailedLocalClearAsync()
    {
        using Fixture fixture = new();
        fixture.Service.CommitSession(Session(101, fresh: true), clearGameSingleSignOn: false);
        fixture.Store.ClearFailure = new IOException("Synthetic protected-store clear failure.");
        Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        logout.Complete(new(HttpStatusCode.NoContent));
        IOException error = await ThrowsAsync<IOException>(fixture.Service.LogoutAsync(),
            "A failure clearing persistent credentials remains visible to the caller.");
        Check(ReferenceEquals(error, fixture.Store.ClearFailure), "The original local storage error is propagated.");
        Check(fixture.Service.Session is null && fixture.Service.AccessToken is null && !fixture.Service.IsAuthenticated,
            "A failed persistent clear never leaves the in-memory account authenticated.");
        Check(fixture.Store.ClearCalls == 1 && fixture.SsoClearCalls == 1,
            "Game SSO cleanup is still attempted when the session store clear throws.");
        LauncherAuthRestoreAttempt restore = await fixture.Service.PrepareRestoreAsync().WaitAsync(StepTimeout);
        Check(restore.Outcome == LauncherAuthRestoreOutcome.NoSession && restore.Session is null && fixture.Handler.RequestCount == 1,
            "An undeleted token cannot be automatically restored during the same process after local logout failed to clear disk.");
    }

    private static async Task ProfileResponseGuardsAsync()
    {
        foreach (string operation in new[] { "refresh", "email", "social", "avatar" })
        {
            // A normal response must still be applied: stale-response rejection is
            // not allowed to disable the feature or to make this test pass vacuously.
            using (Fixture current = new())
            {
                LauncherAuthSession account = Session(101, fresh: true);
                current.Service.CommitSession(account, clearGameSingleSignOn: false);
                Exchange response = QueueProfile(current, operation);
                Task pending = StartProfile(current.Service, operation);
                RequestSnapshot request = await Entered(response);
                LauncherProfile changed = account.Profile with { Email = "changed@example.test", StatusMessage = "Updated fixture", AvatarKey = "blue" };
                response.Complete(ProfileJson(operation, changed));
                await pending.WaitAsync(StepTimeout);
                Check(current.Service.Session?.Profile == changed && request.BearerToken == account.AccessToken,
                    $"The current {operation} response updates only its authenticated account.");
            }

            foreach ((bool replaceAccount, HttpStatusCode status) in new[]
                { (false, HttpStatusCode.OK), (true, HttpStatusCode.OK), (true, HttpStatusCode.Unauthorized) })
            {
                using Fixture fixture = new();
                LauncherAuthSession accountA = Session(101, fresh: true);
                fixture.Service.CommitSession(accountA, clearGameSingleSignOn: false);
                Exchange response = QueueProfile(fixture, operation);
                Task pending = StartProfile(fixture.Service, operation);
                await Entered(response);
                LauncherAuthSession? accountB = replaceAccount ? Session(202, fresh: true) : null;
                if (accountB is not null) fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
                else
                {
                    Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
                    logout.Complete(new(HttpStatusCode.NoContent));
                    await fixture.Service.LogoutAsync().WaitAsync(StepTimeout);
                }
                response.Complete(status == HttpStatusCode.OK
                    ? ProfileJson(operation, accountA.Profile with { Username = "LateOldProfile", StatusMessage = "Must never apply" })
                    : Json(new { error = "Synthetic old account rejection." }, status));
                await ThrowsAsync<OperationCanceledException>(pending,
                    $"A stale {operation} response ({(int)status}) is reported as cancellation, not applied to an obsolete session or exposed as a new account's authorization error.");
                if (accountB is not null) CheckCurrent(fixture, accountB, $"Late {operation} preserves new account B");
                else CheckLoggedOut(fixture, $"Late {operation} preserves logout");
                Check(fixture.Store.SaveCalls == (replaceAccount ? 2 : 1),
                    "An obsolete profile response cannot persist old authentication state.");
            }
        }
    }

    private static async Task LoginAndRegistrationGuardsAsync()
    {
        foreach (bool register in new[] { false, true })
        {
            using (Fixture current = new())
            {
                Exchange response = current.Handler.Enqueue(HttpMethod.Post, register ? "accounts" : "auth/login");
                Task pending = StartLogin(current.Service, register);
                await Entered(response);
                LauncherAuthSession accepted = Session(101, fresh: true);
                response.Complete(Json(accepted, register ? HttpStatusCode.Created : HttpStatusCode.OK));
                await pending.WaitAsync(StepTimeout);
                CheckCurrent(current, accepted, "A current login or registration remains usable", requireSameInstance: false);
                Check(current.Store.SaveCalls == 1 && current.SsoClearCalls == 1,
                    "A current login or registration commits once and clears the previous game SSO once.");
            }
            foreach (bool replaceAccount in new[] { false, true })
            {
                using Fixture fixture = new();
                Exchange response = fixture.Handler.Enqueue(HttpMethod.Post, register ? "accounts" : "auth/login");
                Task pending = StartLogin(fixture.Service, register);
                await Entered(response);
                LauncherAuthSession? accountB = replaceAccount ? Session(202, fresh: true) : null;
                if (accountB is not null) fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
                else await fixture.Service.LogoutAsync().WaitAsync(StepTimeout);
                response.Complete(Json(Session(101, fresh: true), register ? HttpStatusCode.Created : HttpStatusCode.OK));
                await ThrowsAsync<OperationCanceledException>(pending,
                    $"A stale {(register ? "registration" : "login")} wrapper cannot implicitly commit its response.");
                if (accountB is not null) CheckCurrent(fixture, accountB, "A late authentication wrapper preserves account B");
                else CheckLoggedOut(fixture, "A late authentication wrapper cannot undo logout");
                Check(fixture.SsoClearCalls == (replaceAccount ? 0 : 1), "A stale authentication wrapper does not clear game SSO for a newer state.");
            }
        }
    }

    private static async Task RestoreResponseGuardsAsync()
    {
        using (Fixture current = new())
        {
            LauncherAuthSession accepted = Session(101, fresh: true);
            current.Store.Seed(new(accepted.RefreshToken, accepted.RefreshExpiresAt));
            Exchange response = current.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Task<bool> pending = current.Service.RestoreAsync();
            await Entered(response);
            response.Complete(Json(accepted));
            Check(await pending.WaitAsync(StepTimeout), "A current stored-session restoration succeeds.");
            CheckCurrent(current, accepted, "A current restoration commits the accepted session", requireSameInstance: false);
            Check(current.SsoClearCalls == 0, "Restoring the current session does not clear game SSO unnecessarily.");
        }
        foreach (bool replaceAccount in new[] { false, true })
        {
            using Fixture fixture = new();
            LauncherAuthSession old = Session(101, fresh: true);
            fixture.Store.Seed(new(old.RefreshToken, old.RefreshExpiresAt));
            Exchange response = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Task<bool> pending = fixture.Service.RestoreAsync();
            await Entered(response);
            LauncherAuthSession? accountB = replaceAccount ? Session(202, fresh: true) : null;
            if (accountB is not null) fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
            else await fixture.Service.LogoutAsync().WaitAsync(StepTimeout);
            response.Complete(Json(old));
            Check(!await pending.WaitAsync(StepTimeout), "A stale restoration reports false and cannot commit its response.");
            if (accountB is not null) CheckCurrent(fixture, accountB, "A late restoration preserves new account B");
            else CheckLoggedOut(fixture, "A late restoration cannot undo logout");
        }
    }

    private static async Task DisposeDuringRefreshAsync()
    {
        using Fixture fixture = new();
        LauncherAuthSession initial = Session(101, fresh: false);
        fixture.Service.CommitSession(initial, clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Task<bool> pending = fixture.Service.EnsureFreshAsync();
        await Entered(refresh);
        fixture.Service.Dispose();
        Check(fixture.Service.Session is null && fixture.Service.AccessToken is null,
            "Disposal immediately invalidates in-memory credentials.");
        refresh.Complete(Json(Session(101, fresh: true, revision: "late-after-dispose")));
        bool rejected;
        try { rejected = !await pending.WaitAsync(StepTimeout); }
        catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "A refresh received after disposal is rejected or cancelled, with no disposed refresh-gate failure.");
        Check(fixture.Store.Current?.RefreshToken == initial.RefreshToken && fixture.Store.SaveCalls == 1
            && fixture.Store.ClearCalls == 0 && fixture.SsoClearCalls == 0,
            "Disposal preserves persisted sign-in while preventing any late response from saving credentials.");
    }

    private static Exchange QueueProfile(Fixture fixture, string operation) => fixture.Handler.Enqueue(
        operation == "refresh" ? HttpMethod.Get : HttpMethod.Patch,
        operation switch { "refresh" => "me", "email" => "me/email", "social" => "me/social-profile", _ => "me/avatar" });

    private static Task StartProfile(LauncherAuthService service, string operation) => operation switch
    {
        "refresh" => service.RefreshProfileAsync(),
        "email" => service.ChangeEmailAsync("changed@example.test"),
        "social" => service.UpdateSocialProfileAsync("Updated fixture", "Synthetic biography"),
        _ => service.ChangeAvatarAsync("blue")
    };

    private static Task StartLogin(LauncherAuthService service, bool register) => register
        ? service.RegisterAsync("FixtureUser", "fixture@example.test", "synthetic-password")
        : service.LoginAsync("FixtureUser", "synthetic-password");

    private static HttpResponseMessage ProfileJson(string operation, LauncherProfile profile) => operation == "email"
        ? Json(new EmailChangeResponse(profile, true, "Synthetic verification notice")) : Json(profile);

    private static LauncherAuthSession Session(uint id, bool fresh, string revision = "initial") => new(
        $"fixture-access-{id}-{revision}", DateTimeOffset.UtcNow.AddMinutes(fresh ? 30 : 1),
        $"fixture-refresh-{id}-{revision}", DateTimeOffset.UtcNow.AddDays(10),
        new(id, $"Fixture{id}", $"fixture{id}@example.test", true, "gold", false, false, 100));

    private static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };
    private static Task<RequestSnapshot> Entered(Exchange exchange) => exchange.Entered.Task.WaitAsync(StepTimeout);

    private static void CheckLoggedOut(Fixture fixture, string scenario)
    {
        Check(fixture.Service.Session is null && fixture.Service.AccessToken is null && !fixture.Service.IsAuthenticated,
            scenario + ": in-memory authentication is empty.");
        Check(fixture.Store.Current is null, scenario + ": persistent authentication is empty.");
    }

    private static void CheckCurrent(Fixture fixture, LauncherAuthSession expected, string scenario, bool requireSameInstance = true)
    {
        Check(requireSameInstance ? ReferenceEquals(fixture.Service.Session, expected) : fixture.Service.Session == expected,
            scenario + ": the expected account and profile remain current.");
        Check(fixture.Service.AccessToken == expected.AccessToken && fixture.Service.IsAuthenticated,
            scenario + ": the current access token remains usable.");
        Check(fixture.Store.Current?.RefreshToken == expected.RefreshToken,
            scenario + ": only the current refresh token remains persisted.");
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<TException> ThrowsAsync<TException>(Task pending, string message) where TException : Exception
    {
        _checks++;
        try { await pending.WaitAsync(StepTimeout); }
        catch (TException expected) { return expected; }
        catch (Exception other) { throw new InvalidOperationException(message + " Unexpected exception: " + other.GetType().Name, other); }
        throw new InvalidOperationException(message + " No exception was raised.");
    }

    private sealed class Fixture : IDisposable
    {
        private int _ssoClearCalls;
        internal ScriptedHandler Handler { get; } = new();
        internal MemoryStore Store { get; } = new();
        internal LauncherAuthService Service { get; }
        internal int SsoClearCalls => Volatile.Read(ref _ssoClearCalls);
        internal Fixture()
        {
            HttpClient http = new(Handler)
            {
                BaseAddress = new Uri("https://launcher-auth-fixture.invalid/api/v1/"),
                Timeout = Timeout.InfiniteTimeSpan
            };
            Service = new LauncherAuthService(http, Store.Load, Store.Save, Store.Clear,
                () => Interlocked.Increment(ref _ssoClearCalls));
        }
        public void Dispose() { Service.Dispose(); Handler.AbortPending(); }
    }

    private sealed class MemoryStore
    {
        private readonly object _sync = new();
        private StoredLauncherSession? _current;
        private int _saveCalls, _clearCalls;
        internal StoredLauncherSession? Current { get { lock (_sync) return _current; } }
        internal int SaveCalls { get { lock (_sync) return _saveCalls; } }
        internal int ClearCalls { get { lock (_sync) return _clearCalls; } }
        internal IOException? ClearFailure { get; set; }
        internal void Seed(StoredLauncherSession value) { lock (_sync) _current = value; }
        internal StoredLauncherSession? Load() { lock (_sync) return _current; }
        internal void Save(StoredLauncherSession value) { lock (_sync) { _saveCalls++; _current = value; } }
        internal void Clear()
        {
            lock (_sync)
            {
                _clearCalls++;
                if (ClearFailure is not null) throw ClearFailure;
                _current = null;
            }
        }
    }

    private sealed record RequestSnapshot(HttpMethod Method, string Path, string? BearerToken, string Body);

    private sealed class Exchange(HttpMethod method, string path)
    {
        internal HttpMethod Method { get; } = method;
        internal string Path { get; } = "/api/v1/" + path;
        internal TaskCompletionSource<RequestSnapshot> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(HttpResponseMessage response) => Response.TrySetResult(response);
        internal void Fail(Exception error) => Response.TrySetException(error);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Exchange> _queue = new();
        private readonly ConcurrentBag<Exchange> _all = [];
        private int _requestCount;
        internal int RequestCount => Volatile.Read(ref _requestCount);
        internal Exchange Enqueue(HttpMethod method, string path)
        {
            Exchange exchange = new(method, path);
            _queue.Enqueue(exchange);
            _all.Add(exchange);
            return exchange;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (!_queue.TryDequeue(out Exchange? exchange)) throw new InvalidOperationException("An unexpected synthetic HTTP request was made.");
            string path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method != exchange.Method || path != exchange.Path)
                throw new InvalidOperationException($"Unexpected synthetic HTTP route: {request.Method} {path}.");
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(CancellationToken.None);
            exchange.Entered.TrySetResult(new(request.Method, path, request.Headers.Authorization?.Parameter, body));
            // Intentionally ignore transport cancellation. The service must reject
            // stale responses even when an underlying transport returns them late.
            return await exchange.Response.Task.ConfigureAwait(false);
        }
        internal void AbortPending()
        {
            foreach (Exchange exchange in _all) exchange.Response.TrySetCanceled();
        }
    }
}
