using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
            await PasswordAfterRefreshAsync();
            await RefreshDuringBodyCompensationAsync();
            await PasswordReplacementCompensationAsync();
            await LogoutFailuresAsync();
            await PreCancelledLogoutAsync();
            await LogoutAfterNewSessionAsync();
            await FailedLocalClearAsync();
            await ProfileResponseGuardsAsync();
            await AuthorizedBodyResponseGuardsAsync();
            await LoginAndRegistrationGuardsAsync();
            await PreparedAuthenticationLogoutSerializationAsync();
            await ConcurrentPreparedAuthenticationCleanupAsync();
            await FailedCompensatingLogoutAsync();
            await RestoreResponseGuardsAsync();
            await RestoreDuringBodyCompensationAsync();
            await RestoreLogoutSerializationAsync();
            await JsonResponseBoundsAsync();
            await MalformedAuthResponsesAsync();
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
        LauncherAuthSession accountA = Session(101, fresh: false) with
        {
            AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        fixture.Service.CommitSession(accountA, clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Task<bool> pendingRefresh = fixture.Service.EnsureFreshAsync();
        RequestSnapshot request = await Entered(refresh);
        using (JsonDocument body = JsonDocument.Parse(request.Body))
            Check(body.RootElement.GetProperty("refreshToken").GetString() == accountA.RefreshToken,
                "The refresh request captures the original account's refresh token.");

        Task pendingLogout = fixture.Service.LogoutAsync();
        Check(!pendingLogout.IsCompleted && fixture.Handler.RequestCount == 1,
            "Logout waits behind the in-flight refresh instead of revoking a token that is being rotated.");
        LauncherAuthSession renewed = Session(101, fresh: true, revision: "renewed-before-logout");
        refresh.Complete(Json(renewed));
        Check(await pendingRefresh.WaitAsync(StepTimeout),
            "The refresh completes before serialized logout captures credentials.");

        RequestSnapshot logoutRequest = await Entered(logout);
        Check(!pendingLogout.IsCompleted, "The logout transport is deliberately still pending.");
        CheckLoggedOut(fixture, "Local logout precedes the remote response");
        Check(logoutRequest.BearerToken == renewed.AccessToken,
            "Remote logout uses the renewed access token even when the original access token was expired.");
        using (JsonDocument body = JsonDocument.Parse(logoutRequest.Body))
        {
            Check(body.RootElement.GetProperty("refreshToken").GetString() == renewed.RefreshToken,
                "Remote logout proves ownership with the renewed refresh token.");
        }
        logout.Complete(new(HttpStatusCode.NoContent));
        await pendingLogout.WaitAsync(StepTimeout);
        CheckLoggedOut(fixture, "A completed serialized logout leaves no local credentials");
        Check(fixture.Store.SaveCalls == 2 && fixture.Store.ClearCalls == 1 && fixture.SsoClearCalls == 1,
            "Refresh persistence and logout cleanup each run exactly once.");
    }

    private static async Task RefreshAfterNewSessionAsync(HttpStatusCode status)
    {
        using Fixture fixture = new();
        fixture.Service.CommitSession(Session(101, fresh: false), clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Exchange? compensation = status == HttpStatusCode.OK
            ? fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout")
            : null;
        Task<bool> pending = fixture.Service.EnsureFreshAsync();
        await Entered(refresh);
        LauncherAuthSession accountB = Session(202, fresh: true);
        fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
        LauncherAuthSession late = Session(101, fresh: true, revision: "late");
        refresh.Complete(status == HttpStatusCode.OK ? Json(late) : new(status));
        if (compensation is not null)
        {
            RequestSnapshot cleanup = await Entered(compensation);
            Check(cleanup.BearerToken == late.AccessToken,
                "A successful refresh received after an account switch is compensatingly revoked.");
            compensation.Complete(new(HttpStatusCode.NoContent));
        }
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

    private static async Task PasswordAfterRefreshAsync()
    {
        using Fixture fixture = new();
        LauncherAuthSession original = Session(101, fresh: false);
        fixture.Service.CommitSession(original, clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Exchange password = fixture.Handler.Enqueue(HttpMethod.Post, "me/password");
        Task<bool> pendingRefresh = fixture.Service.EnsureFreshAsync();
        await Entered(refresh);

        Task pendingPassword = fixture.Service.ChangePasswordAsync(
            "current-password",
            "replacement-password");
        Check(!pendingPassword.IsCompleted && fixture.Handler.RequestCount == 1,
            "Password change waits for the active refresh-token rotation.");

        LauncherAuthSession renewed = Session(101, fresh: true, revision: "renewed-for-password");
        refresh.Complete(Json(renewed));
        Check(await pendingRefresh.WaitAsync(StepTimeout),
            "The active refresh succeeds before password replacement.");
        RequestSnapshot passwordRequest = await Entered(password);
        Check(passwordRequest.BearerToken == renewed.AccessToken,
            "Password replacement authenticates with the renewed access token.");
        using (JsonDocument body = JsonDocument.Parse(passwordRequest.Body))
        {
            Check(body.RootElement.GetProperty("currentPassword").GetString() == "current-password"
                && body.RootElement.GetProperty("newPassword").GetString() == "replacement-password",
                "Password replacement sends both expected password fields.");
        }

        LauncherAuthSession replacement = Session(101, fresh: true, revision: "password-replacement");
        password.Complete(Json(replacement));
        await pendingPassword.WaitAsync(StepTimeout);
        CheckCurrent(fixture, replacement,
            "Password replacement commits the new server-issued session",
            requireSameInstance: false);
        Check(fixture.Store.SaveCalls == 3 && fixture.Store.ClearCalls == 0
            && fixture.SsoClearCalls == 1,
            "Password replacement persists one new token pair and clears stale game SSO once.");
    }

    private static async Task RefreshDuringBodyCompensationAsync()
    {
        using Fixture fixture = new();
        LauncherAuthSession accountA = Session(101, fresh: false) with
        {
            AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        fixture.Service.CommitSession(accountA, clearGameSingleSignOn: false);
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Exchange compensation = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Task<bool> pending = fixture.Service.EnsureFreshAsync();
        await Entered(refresh);
        LauncherAuthSession rotated = Session(
            101,
            fresh: true,
            revision: "refresh-body-race");
        GatedJsonContent body = new(rotated);
        refresh.Complete(Response(body));
        await body.ReadStarted.Task.WaitAsync(StepTimeout);

        LauncherAuthSession accountB = Session(
            202,
            fresh: true,
            revision: "refresh-body-race-winner");
        fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
        body.Release();
        RequestSnapshot cleanup = await Entered(compensation);
        Check(cleanup.BearerToken == rotated.AccessToken,
            "A refresh rotated before an account switch is revoked after its delayed body is validated.");
        compensation.Complete(new(HttpStatusCode.NoContent));
        Check(!await pending.WaitAsync(StepTimeout),
            "A refresh made stale during body consumption reports false after compensation.");
        CheckCurrent(fixture, accountB,
            "A delayed refresh body cannot replace the account that won the generation race");
    }

    private static async Task PasswordReplacementCompensationAsync()
    {
        using (Fixture switched = new())
        {
            LauncherAuthSession accountA = Session(101, fresh: true);
            switched.Service.CommitSession(accountA, clearGameSingleSignOn: false);
            Exchange password = switched.Handler.Enqueue(HttpMethod.Post, "me/password");
            Exchange compensation = switched.Handler.Enqueue(HttpMethod.Post, "auth/logout");
            Task pending = switched.Service.ChangePasswordAsync(
                "current-password",
                "replacement-password");
            await Entered(password);
            LauncherAuthSession replacement = Session(
                101,
                fresh: true,
                revision: "password-body-race");
            GatedJsonContent body = new(replacement);
            password.Complete(Response(body));
            await body.ReadStarted.Task.WaitAsync(StepTimeout);
            LauncherAuthSession accountB = Session(
                202,
                fresh: true,
                revision: "password-body-race-winner");
            switched.Service.CommitSession(accountB, clearGameSingleSignOn: false);
            body.Release();
            RequestSnapshot cleanup = await Entered(compensation);
            Check(cleanup.BearerToken == replacement.AccessToken,
                "A password replacement made stale during its body read is compensatingly revoked.");
            compensation.Complete(new(HttpStatusCode.NoContent));
            await ThrowsAsync<OperationCanceledException>(pending,
                "A stale password replacement is cancellation rather than an exposed old-account session.");
            CheckCurrent(switched, accountB,
                "The password body race preserves the replacement account");
        }

        using (Fixture failedStore = new())
        {
            LauncherAuthSession current = Session(
                101,
                fresh: true,
                revision: "password-save-source");
            failedStore.Service.CommitSession(current, clearGameSingleSignOn: false);
            IOException saveFailure = new("Synthetic protected-store save failure.");
            failedStore.Store.SaveFailure = saveFailure;
            Exchange password = failedStore.Handler.Enqueue(HttpMethod.Post, "me/password");
            Exchange compensation = failedStore.Handler.Enqueue(HttpMethod.Post, "auth/logout");
            Task pending = failedStore.Service.ChangePasswordAsync(
                "current-password",
                "replacement-password");
            await Entered(password);
            LauncherAuthSession replacement = Session(
                101,
                fresh: true,
                revision: "password-save-failure");
            password.Complete(Json(replacement));
            RequestSnapshot cleanup = await Entered(compensation);
            Check(cleanup.BearerToken == replacement.AccessToken,
                "A password replacement that cannot be persisted is revoked on the server.");
            compensation.Complete(new(HttpStatusCode.NoContent));
            IOException actual = await ThrowsAsync<IOException>(pending,
                "The original session-store failure remains visible after replacement cleanup.");
            Check(ReferenceEquals(actual, saveFailure),
                "Password replacement preserves the original persistence exception.");
            CheckCurrent(failedStore, current,
                "A failed replacement save leaves the prior session current");
        }
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
            RequestSnapshot request = await Entered(logout);
            CheckLoggedOut(fixture, $"Local cleanup is immediate while remote logout may fail through {failure}");
            if (failure == "cancellation") cancellation.Cancel();
            Exception expected = failure switch
            {
                "network" => new HttpRequestException("Synthetic transport failure."),
                "timeout" => new TaskCanceledException("Synthetic HTTP timeout."),
                _ => new OperationCanceledException("Synthetic caller cancellation.", cancellation.Token)
            };
            logout.Fail(expected);
            Exception actual = await ThrowsAsync<Exception>(pending,
                $"Remote logout {failure} remains visible to the caller after local cleanup.");
            Check(ReferenceEquals(actual, expected),
                "Logout propagates the original remote failure without replacing it.");
            CheckLoggedOut(fixture, $"Remote {failure} does not undo local logout");
            Check(fixture.Store.ClearCalls == 1 && fixture.SsoClearCalls == 1,
                "Remote logout failures do not repeat local cleanup.");

            Exchange retry = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
            Task retryPending = fixture.Service.LogoutAsync();
            RequestSnapshot retryRequest = await Entered(retry);
            Check(retryRequest.BearerToken == request.BearerToken
                && retryRequest.Body == request.Body,
                $"A second logout retries the exact unconfirmed proof after {failure}.");
            retry.Complete(new(HttpStatusCode.NoContent));
            await retryPending.WaitAsync(StepTimeout);
            int requestCount = fixture.Handler.RequestCount;
            await fixture.Service.LogoutAsync().WaitAsync(StepTimeout);
            Check(fixture.Handler.RequestCount == requestCount,
                "A confirmed 2xx logout removes its proof from the in-memory retry queue.");
        }


        using (Fixture throttled = new())
        {
            LauncherAuthSession session = Session(
                101,
                fresh: true,
                revision: "logout-429-retry");
            throttled.Service.CommitSession(session, clearGameSingleSignOn: false);
            Exchange first = throttled.Handler.Enqueue(HttpMethod.Post, "auth/logout");
            Task firstPending = throttled.Service.LogoutAsync();
            RequestSnapshot firstRequest = await Entered(first);
            first.Complete(Json(
                new { error = "Synthetic logout throttle." },
                HttpStatusCode.TooManyRequests));
            LauncherAuthException rejection = await ThrowsAsync<LauncherAuthException>(
                firstPending,
                "A throttled logout remains visible after local credentials are cleared.");
            Check(rejection.StatusCode == HttpStatusCode.TooManyRequests,
                "Logout preserves HTTP 429 while retaining the revocation proof.");

            Exchange retry = throttled.Handler.Enqueue(HttpMethod.Post, "auth/logout");
            Task retryPending = throttled.Service.LogoutAsync();
            RequestSnapshot retryRequest = await Entered(retry);
            Check(retryRequest.BearerToken == firstRequest.BearerToken
                && retryRequest.Body == firstRequest.Body,
                "The next logout retries the same proof after HTTP 429 without reauthentication.");
            retry.Complete(new(HttpStatusCode.NoContent));
            await retryPending.WaitAsync(StepTimeout);
            CheckLoggedOut(throttled,
                "A retried throttled logout stays locally logged out after remote confirmation");
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

    private static async Task AuthorizedBodyResponseGuardsAsync()
    {
        using (Fixture ticketFixture = new())
        {
            LauncherAuthSession accountA = Session(101, fresh: true);
            ticketFixture.Service.CommitSession(accountA, clearGameSingleSignOn: false);
            Exchange ticketExchange = ticketFixture.Handler.Enqueue(
                HttpMethod.Post,
                "game-ticket");
            Task<GameTicket> pending = ticketFixture.Service.CreateGameTicketAsync();
            await Entered(ticketExchange);
            GatedJsonContent body = new(new GameTicket(
                "synthetic-ticket-a",
                DateTimeOffset.UtcNow.AddMinutes(1),
                accountA.Profile.Username,
                accountA.Profile.Username + "#1",
                accountA.Profile.AccountId));
            ticketExchange.Complete(Response(body));
            await body.ReadStarted.Task.WaitAsync(StepTimeout);
            LauncherAuthSession accountB = Session(202, fresh: true);
            ticketFixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
            body.Release();
            await ThrowsAsync<OperationCanceledException>(pending,
                "A ticket body from account A is not exposed after account B becomes current.");
            CheckCurrent(ticketFixture, accountB,
                "The delayed game-ticket body preserves account B");
        }

        using (Fixture listFixture = new())
        {
            LauncherAuthSession accountA = Session(101, fresh: true);
            listFixture.Service.CommitSession(accountA, clearGameSingleSignOn: false);
            Exchange newsExchange = listFixture.Handler.Enqueue(HttpMethod.Get, "news");
            Task<IReadOnlyList<LauncherNews>> pending = listFixture.Service.GetNewsAsync();
            await Entered(newsExchange);
            GatedJsonContent body = new(new[]
            {
                new LauncherNews(
                    "old-account-news",
                    "fixture",
                    "Old account payload",
                    "Must not escape the body race.",
                    DateTimeOffset.UtcNow)
            });
            newsExchange.Complete(Response(body));
            await body.ReadStarted.Task.WaitAsync(StepTimeout);
            LauncherAuthSession accountB = Session(202, fresh: true);
            listFixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
            body.Release();
            await ThrowsAsync<OperationCanceledException>(pending,
                "A legacy news collection from account A is not exposed after the session switch.");
            CheckCurrent(listFixture, accountB,
                "The delayed authorized collection preserves account B");
        }

        using (Fixture mismatchedTicket = new())
        {
            LauncherAuthSession current = Session(101, fresh: true);
            mismatchedTicket.Service.CommitSession(current, clearGameSingleSignOn: false);
            Exchange ticketExchange = mismatchedTicket.Handler.Enqueue(
                HttpMethod.Post,
                "game-ticket");
            Task<GameTicket> pending = mismatchedTicket.Service.CreateGameTicketAsync();
            await Entered(ticketExchange);
            ticketExchange.Complete(Json(new GameTicket(
                "synthetic-ticket-b",
                DateTimeOffset.UtcNow.AddMinutes(1),
                "Fixture202",
                "Fixture202#1",
                202)));
            await ThrowsAsync<LauncherAuthException>(pending,
                "A game ticket whose account id differs from the current session is rejected.");
            CheckCurrent(mismatchedTicket, current,
                "A mismatched game ticket cannot change the current session");
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
                Exchange compensation = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
                Task pending = StartLogin(fixture.Service, register);
                await Entered(response);
                LauncherAuthSession? accountB = replaceAccount ? Session(202, fresh: true) : null;
                if (accountB is not null) fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
                else await fixture.Service.LogoutAsync().WaitAsync(StepTimeout);
                LauncherAuthSession late = Session(
                    101,
                    fresh: true,
                    revision: $"late-wrapper-{register}-{replaceAccount}");
                response.Complete(Json(
                    late,
                    register ? HttpStatusCode.Created : HttpStatusCode.OK));
                RequestSnapshot compensationRequest = await Entered(compensation);
                Check(compensationRequest.BearerToken == late.AccessToken,
                    "A stale authentication wrapper compensates its returned server session.");
                compensation.Complete(new(HttpStatusCode.NoContent));
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
        using (Fixture fixture = new())
        {
            LauncherAuthSession old = Session(101, fresh: true);
            fixture.Store.Seed(new(old.RefreshToken, old.RefreshExpiresAt));
            Exchange response = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Exchange compensation = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
            Task<bool> pending = fixture.Service.RestoreAsync();
            await Entered(response);
            LauncherAuthSession accountB = Session(202, fresh: true);
            fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
            response.Complete(Json(old));
            RequestSnapshot cleanup = await Entered(compensation);
            Check(cleanup.BearerToken == old.AccessToken,
                "A direct restore compensates a rotated response made stale before commit.");
            compensation.Complete(new(HttpStatusCode.NoContent));
            Check(!await pending.WaitAsync(StepTimeout), "A stale restoration reports false and cannot commit its response.");
            CheckCurrent(fixture, accountB, "A late restoration preserves new account B");
        }
    }

    private static async Task RestoreDuringBodyCompensationAsync()
    {
        using Fixture fixture = new();
        LauncherAuthSession stored = Session(
            101,
            fresh: true,
            revision: "restore-body-stored");
        fixture.Store.Seed(new(stored.RefreshToken, stored.RefreshExpiresAt));
        Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
        Exchange failedCompensation = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Exchange currentLogout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Exchange retryCompensation = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Task<LauncherAuthRestoreAttempt> pending = fixture.Service.PrepareRestoreAsync();
        await Entered(refresh);
        LauncherAuthSession rotated = Session(
            101,
            fresh: true,
            revision: "restore-body-rotated");
        GatedJsonContent body = new(rotated);
        refresh.Complete(Response(body));
        await body.ReadStarted.Task.WaitAsync(StepTimeout);

        LauncherAuthSession accountB = Session(
            202,
            fresh: true,
            revision: "restore-body-winner");
        fixture.Service.CommitSession(accountB, clearGameSingleSignOn: false);
        body.Release();
        RequestSnapshot firstCleanup = await Entered(failedCompensation);
        Check(firstCleanup.BearerToken == rotated.AccessToken,
            "A restore rotated before the generation changed is immediately compensated after body validation.");
        failedCompensation.Fail(new IOException("Synthetic delayed-restore cleanup failure."));
        LauncherAuthRestoreAttempt attempt = await pending.WaitAsync(StepTimeout);
        Check(attempt.Outcome == LauncherAuthRestoreOutcome.NoSession
            && attempt.Session is null,
            "A delayed stale restore is never exposed as a prepared session when cleanup fails.");
        CheckCurrent(fixture, accountB,
            "The stale prepared restore preserves account B");

        Task logout = fixture.Service.LogoutAsync();
        RequestSnapshot currentRequest = await Entered(currentLogout);
        Check(currentRequest.BearerToken == accountB.AccessToken,
            "Explicit logout still revokes current account B before pending compensation proofs.");
        currentLogout.Complete(new(HttpStatusCode.NoContent));
        RequestSnapshot retryRequest = await Entered(retryCompensation);
        Check(retryRequest.BearerToken == rotated.AccessToken
            && retryRequest.Body == firstCleanup.Body,
            "Explicit logout retries the exact unconfirmed delayed-restore proof.");
        retryCompensation.Complete(new(HttpStatusCode.NoContent));
        await logout.WaitAsync(StepTimeout);
        CheckLoggedOut(fixture,
            "Delayed restore cleanup converges after its retained proof is retried");
    }

    private static async Task PreparedAuthenticationLogoutSerializationAsync()
    {
        foreach (bool register in new[] { false, true })
        {
            string operation = register ? "registration" : "login";
            using (Fixture current = new())
            {
                Exchange authentication = current.Handler.Enqueue(
                    HttpMethod.Post,
                    register ? "accounts" : "auth/login");
                Task<LauncherAuthSession> pending = StartPreparedAuthentication(
                    current.Service,
                    register);
                await Entered(authentication);
                LauncherAuthSession accepted = Session(
                    101,
                    fresh: true,
                    revision: "prepared-" + operation);
                authentication.Complete(Json(accepted));
                LauncherAuthSession prepared = await pending.WaitAsync(StepTimeout);
                current.Service.CommitSession(prepared, clearGameSingleSignOn: true);
                CheckCurrent(
                    current,
                    accepted,
                    $"A current prepared {operation} commits normally",
                    requireSameInstance: false);
            }

            using (Fixture afterPrepare = new())
            {
                LauncherAuthSession stored = Session(
                    303,
                    fresh: true,
                    revision: "stored-before-prepared-" + operation);
                afterPrepare.Store.Seed(new(
                    stored.RefreshToken,
                    stored.RefreshExpiresAt));
                Exchange authentication = afterPrepare.Handler.Enqueue(
                    HttpMethod.Post,
                    register ? "accounts" : "auth/login");
                Exchange logout = afterPrepare.Handler.Enqueue(
                    HttpMethod.Post,
                    "auth/logout");
                Exchange storedLogout = afterPrepare.Handler.Enqueue(
                    HttpMethod.Post,
                    "auth/logout");
                Task<LauncherAuthSession> pending = StartPreparedAuthentication(
                    afterPrepare.Service,
                    register);
                await Entered(authentication);
                LauncherAuthSession accepted = Session(
                    101,
                    fresh: true,
                    revision: "prepared-before-logout-" + operation);
                authentication.Complete(Json(accepted));
                LauncherAuthSession prepared = await pending.WaitAsync(StepTimeout);

                Task pendingLogout = afterPrepare.Service.LogoutAsync();
                RequestSnapshot logoutRequest = await Entered(logout);
                Check(logoutRequest.BearerToken == prepared.AccessToken,
                    $"Logout captures the access token of a prepared {operation}.");
                using (JsonDocument body = JsonDocument.Parse(logoutRequest.Body))
                {
                    Check(body.RootElement.GetProperty("refreshToken").GetString()
                        == prepared.RefreshToken,
                        $"Logout captures the refresh token of a prepared {operation}.");
                }
                logout.Complete(new(HttpStatusCode.NoContent));
                RequestSnapshot storedRequest = await Entered(storedLogout);
                Check(storedRequest.BearerToken is null,
                    $"Logout also attempts a distinct stored family beside the prepared {operation}.");
                using (JsonDocument body = JsonDocument.Parse(storedRequest.Body))
                {
                    Check(body.RootElement.GetProperty("refreshToken").GetString()
                        == stored.RefreshToken,
                        "The distinct stored family is revoked with its refresh proof.");
                }
                storedLogout.Complete(new(HttpStatusCode.NoContent));
                await pendingLogout.WaitAsync(StepTimeout);

                await ThrowsAsync<OperationCanceledException>(
                    Task.Run(() => afterPrepare.Service.CommitSession(
                        prepared,
                        clearGameSingleSignOn: true)),
                    $"A prepared {operation} cannot commit after logout advanced the session generation.");
                CheckLoggedOut(
                    afterPrepare,
                    $"Logout remains authoritative over a prepared {operation}");
                Check(afterPrepare.Store.SaveCalls == 0,
                    $"A rejected prepared {operation} never persists credentials.");
            }

            using (Fixture duringRequest = new())
            {
                Exchange authentication = duringRequest.Handler.Enqueue(
                    HttpMethod.Post,
                    register ? "accounts" : "auth/login");
                Exchange compensation = duringRequest.Handler.Enqueue(
                    HttpMethod.Post,
                    "auth/logout");
                Task<LauncherAuthSession> pending = StartPreparedAuthentication(
                    duringRequest.Service,
                    register);
                await Entered(authentication);
                await duringRequest.Service.LogoutAsync().WaitAsync(StepTimeout);
                LauncherAuthSession late = Session(
                    101,
                    fresh: true,
                    revision: "late-after-logout-" + operation);
                authentication.Complete(Json(late));
                RequestSnapshot compensationRequest = await Entered(compensation);
                Check(compensationRequest.BearerToken == late.AccessToken,
                    $"A late {operation} response is revoked with its returned access token.");
                using (JsonDocument body = JsonDocument.Parse(compensationRequest.Body))
                {
                    Check(body.RootElement.GetProperty("refreshToken").GetString()
                        == late.RefreshToken,
                        $"A late {operation} response is revoked with its returned refresh proof.");
                }
                Check(!pending.IsCompleted,
                    $"The stale {operation} is not rejected until compensating logout has been attempted.");
                compensation.Complete(new(HttpStatusCode.NoContent));
                await ThrowsAsync<OperationCanceledException>(
                    pending,
                    $"A {operation} response received after logout is rejected before it is exposed for commit.");
                CheckLoggedOut(
                    duringRequest,
                    $"A late {operation} response cannot resurrect logout");
                Check(duringRequest.Store.SaveCalls == 0,
                    $"A late {operation} response never persists credentials.");
            }
        }
    }

    private static async Task ConcurrentPreparedAuthenticationCleanupAsync()
    {
        using Fixture fixture = new();
        LauncherAuthSession currentSession = Session(
            303,
            fresh: true,
            revision: "current-before-concurrent-prepares");
        fixture.Service.CommitSession(currentSession, clearGameSingleSignOn: false);
        Exchange login = fixture.Handler.Enqueue(HttpMethod.Post, "auth/login");
        Exchange registration = fixture.Handler.Enqueue(HttpMethod.Post, "accounts");
        Exchange supersededLogout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Exchange currentAccountLogout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Exchange preparedLogout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");

        Task<LauncherAuthSession> firstPending = fixture.Service.PrepareLoginAsync(
            "FixtureUser",
            "synthetic-password");
        await Entered(login);
        Task<LauncherAuthSession> secondPending = fixture.Service.PrepareRegistrationAsync(
            "FixtureUser2",
            "fixture2@example.test",
            "synthetic-password");
        await Entered(registration);
        Check(!firstPending.IsCompleted && !secondPending.IsCompleted,
            "Two interactive prepares can be in flight concurrently without committing either family.");

        LauncherAuthSession firstSession = Session(
            101,
            fresh: true,
            revision: "concurrent-prepare-first");
        login.Complete(Json(firstSession));
        LauncherAuthSession firstPrepared = await firstPending.WaitAsync(StepTimeout);
        Check(firstPrepared == firstSession,
            "The first concurrent prepare can be exposed while the second response remains pending.");

        LauncherAuthSession secondSession = Session(
            202,
            fresh: true,
            revision: "concurrent-prepare-second");
        registration.Complete(Json(secondSession, HttpStatusCode.Created));
        RequestSnapshot supersededRequest = await Entered(supersededLogout);
        Check(supersededRequest.BearerToken == firstSession.AccessToken,
            "The later prepare compensates the earlier uncommitted family.");
        using (JsonDocument body = JsonDocument.Parse(supersededRequest.Body))
        {
            Check(body.RootElement.GetProperty("refreshToken").GetString()
                == firstSession.RefreshToken,
                "The superseded family is revoked with both returned proofs.");
        }
        Check(!secondPending.IsCompleted,
            "The replacement prepare is not exposed until superseded-family cleanup has been attempted.");
        supersededLogout.Complete(new(HttpStatusCode.NoContent));
        LauncherAuthSession secondPrepared = await secondPending.WaitAsync(StepTimeout);
        Check(secondPrepared == secondSession,
            "The later prepared family remains the sole committable candidate.");

        await ThrowsAsync<OperationCanceledException>(
            Task.Run(() => fixture.Service.CommitSession(
                firstPrepared,
                clearGameSingleSignOn: true)),
            "A superseded prepared session cannot be committed after its compensating revocation.");

        Task logout = fixture.Service.LogoutAsync();
        RequestSnapshot currentRequest = await Entered(currentAccountLogout);
        Check(currentRequest.BearerToken == currentSession.AccessToken,
            "Logout captures the current family alongside interactive prepares.");
        currentAccountLogout.Complete(new(HttpStatusCode.NoContent));
        RequestSnapshot preparedRequest = await Entered(preparedLogout);
        Check(preparedRequest.BearerToken == secondSession.AccessToken,
            "Logout also captures the remaining distinct prepared family.");
        using (JsonDocument body = JsonDocument.Parse(preparedRequest.Body))
        {
            Check(body.RootElement.GetProperty("refreshToken").GetString()
                == secondSession.RefreshToken,
                "The remaining prepared family is revoked with its refresh proof.");
        }
        preparedLogout.Complete(new(HttpStatusCode.NoContent));
        await logout.WaitAsync(StepTimeout);
        CheckLoggedOut(fixture,
            "Concurrent prepares converge to no orphaned local or server session after logout");
    }

    private static async Task FailedCompensatingLogoutAsync()
    {
        using Fixture fixture = new();
        Exchange login = fixture.Handler.Enqueue(HttpMethod.Post, "auth/login");
        Exchange compensation = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Task<LauncherAuthSession> pending = fixture.Service.PrepareLoginAsync(
            "FixtureUser",
            "synthetic-password");
        await Entered(login);
        await fixture.Service.LogoutAsync().WaitAsync(StepTimeout);
        LauncherAuthSession late = Session(
            101,
            fresh: true,
            revision: "failed-compensation");
        login.Complete(Json(late));
        await Entered(compensation);
        compensation.Fail(new IOException("Synthetic compensating logout failure."));
        await ThrowsAsync<OperationCanceledException>(
            pending,
            "A best-effort compensation failure cannot replace stale-authentication cancellation.");
        CheckLoggedOut(fixture,
            "Failed compensating transport cannot resurrect a late authentication response");
        Exchange retryLateLogout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Task retryLate = fixture.Service.LogoutAsync();
        RequestSnapshot retryLateRequest = await Entered(retryLateLogout);
        Check(retryLateRequest.BearerToken == late.AccessToken,
            "A later explicit logout retries the unconfirmed late-response family.");
        retryLateLogout.Complete(new(HttpStatusCode.NoContent));
        await retryLate.WaitAsync(StepTimeout);

        using Fixture retryFixture = new();
        Exchange firstLogin = retryFixture.Handler.Enqueue(HttpMethod.Post, "auth/login");
        Exchange secondLogin = retryFixture.Handler.Enqueue(HttpMethod.Post, "auth/login");
        Exchange failedSupersededLogout = retryFixture.Handler.Enqueue(
            HttpMethod.Post,
            "auth/logout");
        Exchange committedLogout = retryFixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");
        Exchange retrySupersededLogout = retryFixture.Handler.Enqueue(
            HttpMethod.Post,
            "auth/logout");

        Task<LauncherAuthSession> firstPrepare = retryFixture.Service.PrepareLoginAsync(
            "FixtureUser",
            "synthetic-password");
        await Entered(firstLogin);
        LauncherAuthSession first = Session(
            101,
            fresh: true,
            revision: "failed-superseded-first");
        firstLogin.Complete(Json(first));
        LauncherAuthSession firstPrepared = await firstPrepare.WaitAsync(StepTimeout);

        Task<LauncherAuthSession> secondPrepare = retryFixture.Service.PrepareLoginAsync(
            "FixtureUser2",
            "synthetic-password");
        await Entered(secondLogin);
        LauncherAuthSession second = Session(
            202,
            fresh: true,
            revision: "failed-superseded-second");
        secondLogin.Complete(Json(second));
        await Entered(failedSupersededLogout);
        failedSupersededLogout.Fail(
            new IOException("Synthetic superseded-session cleanup failure."));
        LauncherAuthSession secondPrepared = await secondPrepare.WaitAsync(StepTimeout);
        retryFixture.Service.CommitSession(
            secondPrepared,
            clearGameSingleSignOn: true);
        await ThrowsAsync<OperationCanceledException>(
            Task.Run(() => retryFixture.Service.CommitSession(
                firstPrepared,
                clearGameSingleSignOn: true)),
            "A superseded candidate stays non-committable after its first cleanup attempt fails.");

        Task retryLogout = retryFixture.Service.LogoutAsync();
        RequestSnapshot committedRequest = await Entered(committedLogout);
        Check(committedRequest.BearerToken == second.AccessToken,
            "Logout revokes the committed replacement family first.");
        committedLogout.Complete(new(HttpStatusCode.NoContent));
        RequestSnapshot retryRequest = await Entered(retrySupersededLogout);
        Check(retryRequest.BearerToken == first.AccessToken,
            "Logout retries a superseded prepared family whose earlier compensation was not confirmed.");
        retrySupersededLogout.Complete(new(HttpStatusCode.NoContent));
        await retryLogout.WaitAsync(StepTimeout);
        CheckLoggedOut(retryFixture,
            "A later logout drains committed and previously unconfirmed prepared families");
    }

    private static async Task RestoreLogoutSerializationAsync()
    {
        using (Fixture fixture = new())
        {
            LauncherAuthSession stored = Session(101, fresh: true, revision: "stored-before-restore");
            LauncherAuthSession restored = Session(101, fresh: true, revision: "rotated-by-restore");
            fixture.Store.Seed(new(stored.RefreshToken, stored.RefreshExpiresAt));
            Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");

            Task<bool> pendingRestore = fixture.Service.RestoreAsync();
            await Entered(refresh);
            Task pendingLogout = fixture.Service.LogoutAsync();
            Check(!pendingLogout.IsCompleted && fixture.Handler.RequestCount == 1,
                "Logout waits until an in-flight direct restoration has committed its rotated credentials.");

            refresh.Complete(Json(restored));
            Check(await pendingRestore.WaitAsync(StepTimeout),
                "The direct restore commits atomically before the serialized logout begins.");
            RequestSnapshot request = await Entered(logout);
            Check(request.BearerToken == restored.AccessToken,
                "Logout following direct restore uses the newly issued access token.");
            using (JsonDocument body = JsonDocument.Parse(request.Body))
            {
                Check(body.RootElement.GetProperty("refreshToken").GetString() == restored.RefreshToken,
                    "Logout following direct restore revokes the newly issued refresh token.");
            }
            CheckLoggedOut(fixture, "Serialized logout clears a directly restored session");
            logout.Complete(new(HttpStatusCode.NoContent));
            await pendingLogout.WaitAsync(StepTimeout);
        }

        using (Fixture fixture = new())
        {
            LauncherAuthSession stored = Session(101, fresh: true, revision: "stored-before-prepare");
            LauncherAuthSession prepared = Session(101, fresh: true, revision: "rotated-by-prepare");
            fixture.Store.Seed(new(stored.RefreshToken, stored.RefreshExpiresAt));
            Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Exchange logout = fixture.Handler.Enqueue(HttpMethod.Post, "auth/logout");

            Task<LauncherAuthRestoreAttempt> pendingPrepare = fixture.Service.PrepareRestoreAsync();
            await Entered(refresh);
            Task pendingLogout = fixture.Service.LogoutAsync();
            Check(!pendingLogout.IsCompleted && fixture.Handler.RequestCount == 1,
                "Logout waits until a prepared restore has finished rotating its stored token.");

            refresh.Complete(Json(prepared));
            LauncherAuthRestoreAttempt attempt = await pendingPrepare.WaitAsync(StepTimeout);
            Check(attempt.Outcome == LauncherAuthRestoreOutcome.Restored
                && attempt.Session == prepared,
                "The prepared restore returns its rotated credentials to the coordinator.");
            RequestSnapshot request = await Entered(logout);
            Check(request.BearerToken == prepared.AccessToken,
                "A logout before restore commit uses the access token returned by the prepared rotation.");
            using (JsonDocument body = JsonDocument.Parse(request.Body))
            {
                Check(body.RootElement.GetProperty("refreshToken").GetString() == prepared.RefreshToken,
                    "Logout before restore commit revokes the newly rotated refresh token, including on legacy schemas without token history.");
            }
            CheckLoggedOut(fixture, "Logout supersedes a prepared but uncommitted restore");
            logout.Complete(new(HttpStatusCode.NoContent));
            await pendingLogout.WaitAsync(StepTimeout);
            Check(fixture.Handler.RequestCount == 2,
                "The superseded stored predecessor is not sent as a second family on legacy schemas without refresh history.");
            Check(fixture.Store.SaveCalls == 0 && fixture.Store.ClearCalls == 1
                && fixture.SsoClearCalls == 1,
                "Prepared credentials are never persisted after logout supersedes the coordinator attempt.");
        }
    }

    private static async Task JsonResponseBoundsAsync()
    {
        Check(LauncherAuthService.ErrorJsonMaximumBytes
                < LauncherAuthService.AuthenticationJsonMaximumBytes
            && LauncherAuthService.AuthenticationJsonMaximumBytes
                <= LauncherAuthService.ScalarJsonMaximumBytes
            && LauncherAuthService.ScalarJsonMaximumBytes
                < LauncherAuthService.CollectionJsonMaximumBytes,
            "JSON response limits reserve more room for compatible list endpoints while bounding errors and authentication payloads tightly.");

        using (Fixture declared = new())
        {
            LauncherAuthSession stored = Session(
                101,
                fresh: true,
                revision: "declared-oversize-source");
            declared.Store.Seed(new(stored.RefreshToken, stored.RefreshExpiresAt));
            Exchange refresh = declared.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Task<LauncherAuthRestoreAttempt> pending = declared.Service.PrepareRestoreAsync();
            await Entered(refresh);
            ByteArrayContent oversized = new(new byte[
                LauncherAuthService.AuthenticationJsonMaximumBytes + 1]);
            Check(oversized.Headers.ContentLength
                    == LauncherAuthService.AuthenticationJsonMaximumBytes + 1,
                "The declared-length fixture advertises a payload one byte beyond the authentication limit.");
            refresh.Complete(Response(oversized));
            await ThrowsAsync<LauncherAuthException>(pending,
                "An oversized Content-Length authentication response is rejected before deserialization.");
            Check(declared.Service.Session is null && declared.Store.SaveCalls == 0,
                "A declared oversized response cannot become an in-memory or persisted session.");
        }

        using (Fixture chunked = new())
        {
            LauncherAuthSession current = Session(
                101,
                fresh: true,
                revision: "chunked-oversize-current");
            chunked.Service.CommitSession(current, clearGameSingleSignOn: false);
            Exchange news = chunked.Handler.Enqueue(HttpMethod.Get, "news");
            Task<IReadOnlyList<LauncherNews>> pending = chunked.Service.GetNewsAsync();
            await Entered(news);
            UnknownLengthContent oversized = new(new byte[
                LauncherAuthService.CollectionJsonMaximumBytes + 1]);
            Check(oversized.Headers.ContentLength is null,
                "The streaming oversize fixture has no declared Content-Length.");
            news.Complete(Response(oversized));
            await ThrowsAsync<LauncherAuthException>(pending,
                "An oversized chunked-style collection response is rejected while streaming.");
            CheckCurrent(chunked, current,
                "A rejected streaming collection response preserves the authenticated session");
        }

        using (Fixture duplicate = new())
        {
            LauncherAuthSession stored = Session(
                101,
                fresh: true,
                revision: "duplicate-property-source");
            duplicate.Store.Seed(new(stored.RefreshToken, stored.RefreshExpiresAt));
            Exchange refresh = duplicate.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Task<LauncherAuthRestoreAttempt> pending = duplicate.Service.PrepareRestoreAsync();
            await Entered(refresh);
            string validJson = JsonSerializer.Serialize(
                stored,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            string duplicateJson = "{\"AccessToken\":"
                + JsonSerializer.Serialize(stored.AccessToken)
                + ","
                + validJson[1..];
            refresh.Complete(RawJson(duplicateJson));
            await ThrowsAsync<LauncherAuthException>(pending,
                "Case-variant duplicate JSON properties are rejected before DTO binding.");
            Check(duplicate.Service.Session is null && duplicate.Store.SaveCalls == 0,
                "A duplicate authentication property cannot select an ambiguous token.");
        }

        using (Fixture malformed = new())
        {
            LauncherAuthSession stored = Session(
                101,
                fresh: true,
                revision: "malformed-json-source");
            malformed.Store.Seed(new(stored.RefreshToken, stored.RefreshExpiresAt));
            Exchange refresh = malformed.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Task<LauncherAuthRestoreAttempt> pending = malformed.Service.PrepareRestoreAsync();
            await Entered(refresh);
            refresh.Complete(RawJson("{\"accessToken\":"));
            await ThrowsAsync<LauncherAuthException>(pending,
                "Syntactically malformed successful JSON is rejected as an authentication error.");
        }

        using (Fixture error = new())
        {
            Exchange login = error.Handler.Enqueue(HttpMethod.Post, "auth/login");
            Task<LauncherAuthSession> pending = error.Service.PrepareLoginAsync(
                "FixtureUser",
                "synthetic-password");
            await Entered(login);
            ByteArrayContent oversized = new(new byte[
                LauncherAuthService.ErrorJsonMaximumBytes + 1]);
            login.Complete(Response(oversized, HttpStatusCode.BadRequest));
            LauncherAuthException rejection = await ThrowsAsync<LauncherAuthException>(
                pending,
                "An oversized JSON error body is bounded and replaced with the safe status fallback.");
            Check(rejection.StatusCode == HttpStatusCode.BadRequest,
                "Bounding an error body preserves its HTTP status for the caller.");
        }
    }

    private static async Task MalformedAuthResponsesAsync()
    {
        LauncherAuthSession valid = Session(
            101,
            fresh: true,
            revision: "malformed-response-template");
        (string Scenario, object Body)[] malformedResponses =
        [
            ("empty object", new { }),
            ("null access token", valid with { AccessToken = null! }),
            ("wrong access-token format", valid with { AccessToken = "not-an-atlas-token" }),
            ("null refresh token", valid with { RefreshToken = null! }),
            ("wrong refresh-token format", valid with { RefreshToken = "not-an-atlas-token" }),
            ("expired access token", valid with
            {
                AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            }),
            ("refresh deadline before access deadline", valid with
            {
                RefreshExpiresAt = valid.AccessExpiresAt.AddTicks(-1)
            }),
            ("null profile", valid with { Profile = null! }),
            ("zero account id", valid with
            {
                Profile = valid.Profile with { AccountId = 0 }
            }),
            ("blank profile username", valid with
            {
                Profile = valid.Profile with { Username = " " }
            }),
            ("blank profile email", valid with
            {
                Profile = valid.Profile with { Email = " " }
            })
        ];

        foreach ((string scenario, object body) in malformedResponses)
        {
            using Fixture fixture = new();
            LauncherAuthSession stored = Session(
                101,
                fresh: true,
                revision: "malformed-restore-source-" + scenario);
            fixture.Store.Seed(new(stored.RefreshToken, stored.RefreshExpiresAt));
            Exchange refresh = fixture.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Task<LauncherAuthRestoreAttempt> pending = fixture.Service.PrepareRestoreAsync();
            await Entered(refresh);
            refresh.Complete(Json(body));
            await ThrowsAsync<LauncherAuthException>(pending,
                $"A successful restore response with {scenario} is rejected before it can become a prepared session.");
            Check(fixture.Service.Session is null
                && fixture.Store.Current?.RefreshToken == stored.RefreshToken
                && fixture.Store.SaveCalls == 0,
                $"Restore content with {scenario} cannot overwrite memory or persistent credentials.");
        }

        using (Fixture equality = new())
        {
            LauncherAuthSession stored = Session(
                101,
                fresh: true,
                revision: "equal-deadline-source");
            equality.Store.Seed(new(stored.RefreshToken, stored.RefreshExpiresAt));
            Exchange refresh = equality.Handler.Enqueue(HttpMethod.Post, "auth/refresh");
            Task<LauncherAuthRestoreAttempt> pending = equality.Service.PrepareRestoreAsync();
            await Entered(refresh);
            DateTimeOffset sharedDeadline = DateTimeOffset.UtcNow.AddMinutes(10);
            LauncherAuthSession bounded = stored with
            {
                AccessExpiresAt = sharedDeadline,
                RefreshExpiresAt = sharedDeadline
            };
            refresh.Complete(Json(bounded));
            LauncherAuthRestoreAttempt accepted = await pending.WaitAsync(StepTimeout);
            Check(accepted.Outcome == LauncherAuthRestoreOutcome.Restored
                && accepted.Session == bounded,
                "A server rotation capped at its absolute deadline may return equal access and refresh expirations.");
        }

        using (Fixture fixture = new())
        {
            LauncherAuthSession current = Session(101, fresh: true, revision: "malformed-password-source");
            fixture.Service.CommitSession(current, clearGameSingleSignOn: false);
            Exchange password = fixture.Handler.Enqueue(HttpMethod.Post, "me/password");
            Task pending = fixture.Service.ChangePasswordAsync("current-password", "replacement-password");
            await Entered(password);
            password.Complete(Json(new { }));
            await ThrowsAsync<LauncherAuthException>(pending,
                "An empty successful password response is rejected before replacing the current session.");
            CheckCurrent(fixture, current,
                "Malformed password content preserves the existing authenticated session");
            Check(fixture.SsoClearCalls == 0,
                "Malformed password content does not clear game SSO or commit credentials.");
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

    private static Task<LauncherAuthSession> StartPreparedAuthentication(
        LauncherAuthService service,
        bool register)
        => register
            ? service.PrepareRegistrationAsync(
                "FixtureUser",
                "fixture@example.test",
                "synthetic-password")
            : service.PrepareLoginAsync("FixtureUser", "synthetic-password");

    private static HttpResponseMessage ProfileJson(string operation, LauncherProfile profile) => operation == "email"
        ? Json(new EmailChangeResponse(profile, true, "Synthetic verification notice")) : Json(profile);

    private static LauncherAuthSession Session(uint id, bool fresh, string revision = "initial") => new(
        Token("access", id, revision), DateTimeOffset.UtcNow.AddMinutes(fresh ? 30 : 1),
        Token("refresh", id, revision), DateTimeOffset.UtcNow.AddDays(10),
        new(id, $"Fixture{id}", $"fixture{id}@example.test", true, "gold", false, false, 100));

    private static string Token(string kind, uint id, string revision)
        => $"atl_{kind}-" + Convert.ToBase64String(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{kind}:{id}:{revision}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };
    private static HttpResponseMessage RawJson(
        string body,
        HttpStatusCode status = HttpStatusCode.OK)
        => Response(
            new StringContent(body, Encoding.UTF8, "application/json"),
            status);
    private static HttpResponseMessage Response(
        HttpContent content,
        HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = content };
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
        internal IOException? SaveFailure { get; set; }
        internal IOException? ClearFailure { get; set; }
        internal void Seed(StoredLauncherSession value) { lock (_sync) _current = value; }
        internal StoredLauncherSession? Load() { lock (_sync) return _current; }
        internal void Save(StoredLauncherSession value)
        {
            lock (_sync)
            {
                _saveCalls++;
                if (SaveFailure is not null) throw SaveFailure;
                _current = value;
            }
        }
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

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => stream.WriteAsync(payload).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(payload, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class GatedJsonContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal GatedJsonContent(object body)
        {
            _payload = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType());
            Headers.ContentLength = _payload.Length;
            Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        }

        internal TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        protected override Task<Stream> CreateContentReadStreamAsync()
            => OpenAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken)
            => OpenAsync();

        private async Task<Stream> OpenAsync()
        {
            ReadStarted.TrySetResult();
            // Model a non-cooperative transport that finishes an already received
            // body after the session generation has changed.
            await _release.Task.ConfigureAwait(false);
            return new MemoryStream(_payload, writable: false);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            await using Stream source = await OpenAsync().ConfigureAwait(false);
            await source.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }

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
