using System.Net;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;

internal static class LauncherSessionGenerationTests
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        await KeepCurrentSessionContractsAsync();
        await DiscardFormerRefreshAsync();
        await DiscardFormerTicketsAsync();
        await PreserveCancellationAsync();
        await RejectRequestsDuringLogoutAsync();
        await FinishLocallySuccessfulLogoutAsync();
        await PreservePreparedLoginContractsAsync();
        Console.WriteLine($"Launcher session generation PASS: {checks} assertions; fake authentication and deferred tasks only, no HTTP, WPF, storage or game process.");
        return 0;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        void Unchanged(Fixture fixture, AuthSessionSnapshot expected, LauncherAuthSession? expectedSession, int invalidations)
        {
            Check(fixture.Coordinator.CurrentSnapshot == expected && ReferenceEquals(fixture.Authentication.Session, expectedSession),
                "A superseded request must not replace the current identity or its session snapshot.");
            Check(fixture.Authentication.InvalidateLocalSessionCalls == invalidations,
                "A superseded or cancelled request must not clear the current credentials.");
        }

        async Task KeepCurrentSessionContractsAsync()
        {
            foreach (bool ticket in new[] { false, true })
            {
                await using Fixture fixture = await Fixture.CreateAsync();
                AuthSessionSnapshot before = fixture.Coordinator.CurrentSnapshot;
                fixture.Authentication.EnsureFreshHandler = _ =>
                {
                    fixture.Authentication.Session = fixture.Authentication.Session! with { AccessToken = "renewed-fixture-access" };
                    return Task.FromResult(true);
                };
                object result = await RequestAsync(fixture, ticket);
                Check(IsSuccess(result), "A token rotation within the same login must still allow request preparation and ticket acquisition.");
                Check(fixture.Coordinator.CurrentSnapshot == before && fixture.Authentication.Session?.AccessToken == "renewed-fixture-access",
                    "Token rotation must not be confused with a new login generation.");
            }

            foreach (bool ticket in new[] { false, true })
            foreach (bool unauthorized in new[] { false, true })
            {
                await using Fixture fixture = await Fixture.CreateAsync();
                fixture.Authentication.EnsureFreshHandler = _ => unauthorized
                    ? Task.FromException<bool>(new LauncherAuthException("Synthetic refusal", HttpStatusCode.Unauthorized))
                    : Task.FromResult(false);
                object result = await RequestAsync(fixture, ticket);
                Check(IsAuthenticationRequired(result), "A refusal for the current login must still require authentication.");
                Check(fixture.Coordinator.CurrentSnapshot.State == LauncherSessionState.SignedOut
                    && fixture.Authentication.Session is null && fixture.Authentication.InvalidateLocalSessionCalls == 1
                    && fixture.Authentication.CreateGameTicketCalls == 0,
                    "A current refresh refusal expires the session once and never starts a game-ticket request.");
            }

            foreach (bool ticket in new[] { false, true })
            {
                await using Fixture fixture = await Fixture.CreateAsync();
                AuthSessionSnapshot before = fixture.Coordinator.CurrentSnapshot;
                LauncherAuthSession? credentials = fixture.Authentication.Session;
                fixture.Authentication.EnsureFreshHandler = _ => Task.FromException<bool>(new TaskCanceledException("Synthetic timeout"));
                object result = await RequestAsync(fixture, ticket);
                Check(result is AtlasRequestPreparationStatus.Unavailable
                    or GameTicketAcquisitionResult { Status: GameTicketAcquisitionStatus.NetworkUnavailable, Ticket: null },
                    "A current network timeout keeps its existing unavailable result and is not treated as credential expiry.");
                Unchanged(fixture, before, credentials, 0);
            }
        }

        async Task DiscardFormerRefreshAsync()
        {
            foreach (bool ticket in new[] { false, true })
            foreach (string nextIdentity in new[] { "signed-out", "same-account", "another-account" })
            foreach (string reply in new[] { "true", "false", "unauthorized" })
            {
                await using Fixture fixture = await Fixture.CreateAsync();
                TaskCompletionSource<bool> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Authentication.EnsureFreshHandler = _ => response.Task;
                Task<object> pending = RequestAsync(fixture, ticket);
                Check(fixture.Authentication.EnsureFreshCalls == 1, "The former request must already be waiting for its refresh response.");
                await fixture.LogoutAsync();
                if (nextIdentity != "signed-out") await fixture.LoginAsync(nextIdentity == "same-account" ? "FixtureA" : "FixtureB");
                AuthSessionSnapshot current = fixture.Coordinator.CurrentSnapshot;
                LauncherAuthSession? credentials = fixture.Authentication.Session;
                int invalidations = fixture.Authentication.InvalidateLocalSessionCalls;
                if (reply == "unauthorized") response.SetException(new LauncherAuthException("Late synthetic refusal", HttpStatusCode.Unauthorized));
                else response.SetResult(reply == "true");
                Check(IsCancelled(await pending.WaitAsync(TimeSpan.FromSeconds(3))),
                    "A refresh from a former login must never return Ready or a game ticket, including for the same account.");
                Check(fixture.Authentication.CreateGameTicketCalls == 0, "A former refresh must not issue a game ticket using a later login.");
                Unchanged(fixture, current, credentials, invalidations);
            }
        }

        async Task DiscardFormerTicketsAsync()
        {
            foreach (string nextIdentity in new[] { "signed-out", "same-account", "another-account" })
            foreach (bool unauthorized in new[] { false, true })
            {
                await using Fixture fixture = await Fixture.CreateAsync();
                TaskCompletionSource<GameTicket> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Authentication.GameTicketHandler = _ => response.Task;
                Task<GameTicketAcquisitionResult> pending = fixture.Coordinator.AcquireGameTicketAsync(CancellationToken.None);
                Check(fixture.Authentication.CreateGameTicketCalls == 1, "The old ticket request must already be in flight.");
                await fixture.LogoutAsync();
                if (nextIdentity != "signed-out") await fixture.LoginAsync(nextIdentity == "same-account" ? "FixtureA" : "FixtureB");
                AuthSessionSnapshot current = fixture.Coordinator.CurrentSnapshot;
                LauncherAuthSession? credentials = fixture.Authentication.Session;
                int invalidations = fixture.Authentication.InvalidateLocalSessionCalls;
                if (unauthorized) response.SetException(new LauncherAuthException("Late synthetic ticket refusal", HttpStatusCode.Unauthorized));
                else response.SetResult(Ticket());
                Check(IsCancelled(await pending.WaitAsync(TimeSpan.FromSeconds(3))),
                    "A ticket or 401 from a former login must be discarded without exposing a playable ticket.");
                Unchanged(fixture, current, credentials, invalidations);
            }
        }

        async Task PreserveCancellationAsync()
        {
            foreach (bool ticket in new[] { false, true })
            foreach (string stage in new[] { "before", "refresh-false", "refresh-unauthorized" })
            {
                await using Fixture fixture = await Fixture.CreateAsync();
                using CancellationTokenSource cancellation = new();
                AuthSessionSnapshot before = fixture.Coordinator.CurrentSnapshot;
                LauncherAuthSession? credentials = fixture.Authentication.Session;
                TaskCompletionSource<bool> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Authentication.EnsureFreshHandler = _ => response.Task;
                if (stage == "before") cancellation.Cancel();
                Task<object> pending = RequestAsync(fixture, ticket, cancellation.Token);
                cancellation.Cancel();
                if (stage == "refresh-unauthorized") response.SetException(new LauncherAuthException("Cancelled synthetic refusal", HttpStatusCode.Unauthorized));
                else response.SetResult(false);
                Check(IsCancelled(await pending.WaitAsync(TimeSpan.FromSeconds(3))), "Cancellation wins over a completed refresh success or refusal.");
                Check(fixture.Authentication.CreateGameTicketCalls == 0
                    && (stage != "before" || fixture.Authentication.EnsureFreshCalls == 0),
                    "An already cancelled request must not refresh credentials, and no cancelled refresh may start a ticket.");
                Unchanged(fixture, before, credentials, 0);
            }

            foreach (bool unauthorized in new[] { false, true })
            {
                await using Fixture fixture = await Fixture.CreateAsync();
                using CancellationTokenSource cancellation = new();
                AuthSessionSnapshot before = fixture.Coordinator.CurrentSnapshot;
                LauncherAuthSession? credentials = fixture.Authentication.Session;
                TaskCompletionSource<GameTicket> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Authentication.GameTicketHandler = _ => response.Task;
                Task<GameTicketAcquisitionResult> pending = fixture.Coordinator.AcquireGameTicketAsync(cancellation.Token);
                cancellation.Cancel();
                if (unauthorized) response.SetException(new LauncherAuthException("Cancelled synthetic ticket refusal", HttpStatusCode.Unauthorized));
                else response.SetResult(Ticket());
                Check(IsCancelled(await pending.WaitAsync(TimeSpan.FromSeconds(3))), "Cancellation must discard even a successfully returned game ticket.");
                Unchanged(fixture, before, credentials, 0);
            }
        }

        async Task RejectRequestsDuringLogoutAsync()
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            TaskCompletionSource response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Authentication.LogoutHandler = async _ =>
            {
                await response.Task;
                fixture.Authentication.InvalidateLocalSession();
            };
            LauncherSessionStartResult logout = fixture.Coordinator.TryLogout(CancellationToken.None);
            Check(fixture.Coordinator.CurrentSnapshot.IsLoggingOut && fixture.Authentication.IsAuthenticated,
                "The synthetic logout deliberately leaves credentials present while its remote operation waits.");
            Check(IsAuthenticationRequired(await RequestAsync(fixture, false)) && IsAuthenticationRequired(await RequestAsync(fixture, true))
                && fixture.Authentication.EnsureFreshCalls == 0 && fixture.Authentication.CreateGameTicketCalls == 0,
                "The LoggingOut snapshot must prevent new authenticated preparations or tickets even before credentials are cleared.");
            response.SetResult();
            Check((await logout.Completion!.WaitAsync(TimeSpan.FromSeconds(3))).Status == LauncherSessionCompletionStatus.Succeeded,
                "Rejecting work during logout must preserve successful logout completion.");
        }

        async Task FinishLocallySuccessfulLogoutAsync()
        {
            foreach (bool shutdown in new[] { false, true })
            {
                await using Fixture fixture = await Fixture.CreateAsync();
                using CancellationTokenSource cancellation = new();
                TaskCompletionSource response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Authentication.LogoutHandler = async _ =>
                {
                    fixture.Authentication.InvalidateLocalSession();
                    await response.Task;
                };
                LauncherSessionStartResult logout = fixture.Coordinator.TryLogout(cancellation.Token);
                AuthSessionSnapshot pendingSnapshot = fixture.Coordinator.CurrentSnapshot;
                Check(logout.IsStarted && pendingSnapshot.IsLoggingOut && fixture.Authentication.Session is null,
                    "Local credentials must already be cleared while the remote logout is pending.");
                if (shutdown) fixture.Coordinator.BeginShutdown();
                else cancellation.Cancel();
                response.SetResult();
                LauncherSessionCompletion result = await logout.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
                if (shutdown)
                {
                    Check(result.Status == LauncherSessionCompletionStatus.Superseded
                        && fixture.Coordinator.CurrentSnapshot == pendingSnapshot,
                        "A late successful local logout must not publish a session transition after shutdown.");
                }
                else
                {
                    Check(result.Status == LauncherSessionCompletionStatus.Succeeded
                        && result.Snapshot.State == LauncherSessionState.SignedOut
                        && fixture.Coordinator.CurrentSnapshot.State == LauncherSessionState.SignedOut,
                        "Caller cancellation after successful local cleanup must finish SignedOut, not leave LoggingOut.");
                    await fixture.LoginAsync("FixtureB");
                    Check(fixture.Coordinator.CurrentSnapshot.IsAuthenticated
                        && fixture.Authentication.Session?.Profile.Username == "FixtureB",
                        "Finishing an already cleared logout must allow a new login.");
                }
            }

            await using Fixture failedCleanup = await Fixture.CreateAsync();
            using CancellationTokenSource failureCancellation = new();
            TaskCompletionSource cleanupFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
            failedCleanup.Authentication.LogoutHandler = async _ =>
            {
                failedCleanup.Authentication.Session = null;
                await cleanupFailure.Task;
                throw new IOException("Synthetic persistent credential deletion failure");
            };
            LauncherSessionStartResult failedLogout = failedCleanup.Coordinator.TryLogout(failureCancellation.Token);
            failureCancellation.Cancel();
            cleanupFailure.SetResult();
            LauncherSessionCompletion failure = await failedLogout.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
            Check(failure.Status == LauncherSessionCompletionStatus.Failed
                && failure.Snapshot.State == LauncherSessionState.SignedOut
                && failure.Snapshot.FailureCategory == LauncherSessionFailureCategory.SecureStorage,
                "An explicit persistent cleanup failure must remain visible even after in-memory cleanup and caller cancellation.");
        }

        async Task PreservePreparedLoginContractsAsync()
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            await fixture.LogoutAsync();
            TaskCompletionSource<LauncherAuthSession> oldLogin = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Authentication.LoginHandler = (_, _, _) => oldLogin.Task;
            LauncherSessionStartResult pending = fixture.Coordinator.TryLogin("FixtureA", "synthetic-password");
            Check(fixture.Coordinator.CancelInteractiveAttempt(), "An interactive login can still be cancelled independently of token refresh.");
            await fixture.LoginAsync("FixtureB");
            AuthSessionSnapshot current = fixture.Coordinator.CurrentSnapshot;
            LauncherAuthSession? credentials = fixture.Authentication.Session;
            int commits = fixture.Authentication.CommitSessionCalls;
            oldLogin.SetResult(Fixture.SessionFor("FixtureA"));
            Check((await pending.Completion!.WaitAsync(TimeSpan.FromSeconds(3))).Status == LauncherSessionCompletionStatus.Superseded
                && fixture.Authentication.CommitSessionCalls == commits,
                "The coordinator must retain ownership of prepared login commits and ignore a cancelled attempt's late result.");
            Unchanged(fixture, current, credentials, fixture.Authentication.InvalidateLocalSessionCalls);

            TaskCompletionSource<LauncherAuthRestoreAttempt> restore = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using FakeLauncherAuthService authentication = new() { PrepareRestoreHandler = _ => restore.Task };
            using LauncherSessionCoordinator coordinator = new(authentication, CancellationToken.None, _ => { });
            Task<LauncherSessionRestoreResult> restoring = coordinator.RestoreOnceAsync();
            coordinator.BeginShutdown();
            restore.SetResult(new(LauncherAuthRestoreOutcome.Restored, Fixture.SessionFor("FixtureA")));
            Check((await restoring.WaitAsync(TimeSpan.FromSeconds(3))).Status == LauncherSessionRestoreStatus.Cancelled
                && authentication.CommitSessionCalls == 0 && authentication.Session is null,
                "Shutdown must still discard prepared restore credentials before CommitSession is called.");
        }
    }

    private static async Task<object> RequestAsync(Fixture fixture, bool ticket, CancellationToken cancellationToken = default)
    {
        if (ticket) return await fixture.Coordinator.AcquireGameTicketAsync(cancellationToken);
        return await fixture.Coordinator.PrepareAuthenticatedRequestAsync(cancellationToken);
    }

    private static bool IsCancelled(object result) => result is AtlasRequestPreparationStatus.Cancelled
        or GameTicketAcquisitionResult { Status: GameTicketAcquisitionStatus.Cancelled, Ticket: null };
    private static bool IsAuthenticationRequired(object result) => result is AtlasRequestPreparationStatus.AuthenticationRequired
        or GameTicketAcquisitionResult { Status: GameTicketAcquisitionStatus.AuthenticationRequired, Ticket: null };
    private static bool IsSuccess(object result) => result is AtlasRequestPreparationStatus.Ready
        or GameTicketAcquisitionResult { Status: GameTicketAcquisitionStatus.Succeeded, Ticket: not null };
    private static GameTicket Ticket() => new("synthetic-ticket", DateTimeOffset.UtcNow.AddMinutes(1), "FixtureA", "1#1", 42);

    private sealed class Fixture : IAsyncDisposable
    {
        internal FakeLauncherAuthService Authentication { get; } = new()
        {
            Session = SessionFor("FixtureA"), RestoreResult = true, EnsureFreshHandler = _ => Task.FromResult(true)
        };
        internal LauncherSessionCoordinator Coordinator { get; }
        private Fixture() => Coordinator = new(Authentication, CancellationToken.None, _ => { });
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            if ((await fixture.Coordinator.RestoreOnceAsync().WaitAsync(TimeSpan.FromSeconds(3))).Status != LauncherSessionRestoreStatus.Restored)
                throw new InvalidOperationException("Synthetic fixture restoration failed.");
            return fixture;
        }
        internal static LauncherAuthSession SessionFor(string username)
        {
            LauncherAuthSession session = FakeLauncherAuthService.CreateSession(username);
            return session with { AccessToken = username + "-fixture-access", RefreshToken = username + "-fixture-refresh",
                Profile = session.Profile with { AccountId = username == "FixtureA" ? 42u : 84u } };
        }
        internal async Task LogoutAsync()
        {
            LauncherSessionStartResult logout = Coordinator.TryLogout(CancellationToken.None);
            if (!logout.IsStarted || (await logout.Completion!.WaitAsync(TimeSpan.FromSeconds(3))).Status != LauncherSessionCompletionStatus.Succeeded)
                throw new InvalidOperationException("Synthetic logout failed.");
        }
        internal async Task LoginAsync(string username)
        {
            Authentication.LoginHandler = (_, _, _) => Task.FromResult(SessionFor(username));
            LauncherSessionStartResult login = Coordinator.TryLogin(username, "synthetic-password");
            if (!login.IsStarted || (await login.Completion!.WaitAsync(TimeSpan.FromSeconds(3))).Status != LauncherSessionCompletionStatus.Succeeded)
                throw new InvalidOperationException("Synthetic login failed.");
        }
        public async ValueTask DisposeAsync()
        {
            Coordinator.BeginShutdown();
            await Coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(3));
            Coordinator.Dispose();
            Authentication.Dispose();
        }
    }
}
