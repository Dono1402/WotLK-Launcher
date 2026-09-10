using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WotLK.Launcher;

internal sealed class LauncherAuthService : ILauncherAuthService
{
    internal const int AuthenticationJsonMaximumBytes = 128 * 1024;
    internal const int ScalarJsonMaximumBytes = 256 * 1024;
    internal const int CollectionJsonMaximumBytes = 2 * 1024 * 1024;
    internal const int ErrorJsonMaximumBytes = 64 * 1024;
    private static readonly TimeSpan JsonResponseReadTimeout = TimeSpan.FromSeconds(20);

    private static readonly Uri ApiBaseUri = AtlasNetwork.LauncherApiBaseUri;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly Func<StoredLauncherSession?> _loadSession;
    private readonly Action<StoredLauncherSession> _saveSession;
    private readonly Action _clearSession;
    private readonly Action _clearGameSingleSignOn;
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConditionalWeakTable<LauncherAuthSession, PreparedSessionGeneration>
        _preparedSessionGenerations = new();
    private readonly List<LauncherAuthSession> _preparedInteractiveSessions = [];
    private readonly List<SessionRevocationProof> _pendingRevocations = [];
    private LauncherAuthSession? _session;
    private LauncherAuthSession? _preparedRestoreSession;
    private long _generation;
    private bool _restoreSuppressed;
    private bool _disposed;

    public LauncherAuthService() : this(
        new HttpClient(AtlasNetwork.CreateHandler())
        {
            BaseAddress = ApiBaseUri,
            Timeout = TimeSpan.FromSeconds(20)
        },
        SecureSessionStore.Load,
        SecureSessionStore.Save,
        SecureSessionStore.Clear,
        GameSingleSignOn.Clear)
    {
    }

    internal LauncherAuthService(
        HttpClient http,
        Func<StoredLauncherSession?> loadSession,
        Action<StoredLauncherSession> saveSession,
        Action clearSession,
        Action clearGameSingleSignOn)
    {
        _http = http;
        _loadSession = loadSession;
        _saveSession = saveSession;
        _clearSession = clearSession;
        _clearGameSingleSignOn = clearGameSingleSignOn;
    }

    public LauncherAuthSession? Session
    {
        get { lock (_stateSync) return _session; }
    }

    public string? AccessToken => Session?.AccessToken;

    public bool IsAuthenticated =>
        Session is { } session && session.AccessExpiresAt > DateTimeOffset.UtcNow;

    public async Task<LauncherAuthRestoreAttempt> PrepareRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation = GetGeneration();
            return await PrepareRestoreCoreAsync(generation, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<LauncherAuthRestoreAttempt> PrepareRestoreCoreAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        StoredLauncherSession? stored;
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_generation != generation)
                return new LauncherAuthRestoreAttempt(
                    LauncherAuthRestoreOutcome.NoSession,
                    null);
            stored = _restoreSuppressed
                ? null
                : _preparedRestoreSession is { } prepared
                    ? new StoredLauncherSession(
                        prepared.RefreshToken,
                        prepared.RefreshExpiresAt)
                    : _loadSession();
        }
        if (stored is null)
        {
            return new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.NoSession,
                null);
        }

        if (stored.RefreshExpiresAt <= DateTimeOffset.UtcNow)
        {
            InvalidateLocalSession(generation, cancellationToken);
            return new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.Rejected,
                null);
        }

        using HttpRequestMessage refreshRequest = new(HttpMethod.Post, "auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = stored.RefreshToken })
        };
        using HttpResponseMessage response = await SendResponseAsync(
            refreshRequest,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            InvalidateLocalSession(generation, cancellationToken);
            return new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.Rejected,
                null);
        }

        LauncherAuthSession session = await ReadAuthSessionAsync(
            response,
            cancellationToken).ConfigureAwait(false);
        bool rejected;
        lock (_stateSync)
        {
            rejected = _disposed || _generation != generation;
            if (!rejected)
            {
                // Prepare/commit is split by the session coordinator. Keep the newly
                // rotated proof in memory during that gap so logout can revoke it even
                // on legacy schemas where the predecessor hash is no longer stored.
                _preparedRestoreSession = session;
                TrackPreparedSessionUnsafe(session, generation);
            }
        }

        if (rejected)
        {
            await RevokeServerSessionBestEffortAsync(session).ConfigureAwait(false);
            return new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.NoSession,
                null);
        }

        return new LauncherAuthRestoreAttempt(
            LauncherAuthRestoreOutcome.Restored,
            session);
    }

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation = GetGeneration();
            LauncherAuthRestoreAttempt attempt = await PrepareRestoreCoreAsync(
                generation,
                cancellationToken).ConfigureAwait(false);
            if (attempt.Outcome != LauncherAuthRestoreOutcome.Restored
                || attempt.Session is null)
            {
                return false;
            }

            try
            {
                if (TryCommitSession(
                        attempt.Session,
                        generation,
                        clearGameSingleSignOn: false,
                        replaceSession: true,
                        cancellationToken))
                {
                    return true;
                }
            }
            catch
            {
                await RevokeServerSessionBestEffortAsync(attempt.Session).ConfigureAwait(false);
                throw;
            }

            await RevokeServerSessionBestEffortAsync(attempt.Session).ConfigureAwait(false);
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<bool> EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_stateSync)
        {
            if (_disposed || _session is null) return false;
            generation = _generation;
            if (_session.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return true;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LauncherAuthSession current;
            lock (_stateSync)
            {
                if (_disposed || _generation != generation || _session is null) return false;
                current = _session;
                if (current.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return true;
            }

            using HttpRequestMessage refreshRequest = new(HttpMethod.Post, "auth/refresh")
            {
                Content = JsonContent.Create(new { refreshToken = current.RefreshToken })
            };
            using HttpResponseMessage response = await SendResponseAsync(
                refreshRequest,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateLocalSession(generation, cancellationToken);
                return false;
            }

            LauncherAuthSession session = await ReadAuthSessionAsync(response, cancellationToken)
                .ConfigureAwait(false);
            if (session.Profile.AccountId != current.Profile.AccountId)
            {
                await RevokeServerSessionBestEffortAsync(session).ConfigureAwait(false);
                throw new LauncherAuthException(
                    "La session actualisée ne correspond pas au compte actuel.");
            }

            try
            {
                if (TryCommitSession(
                        session,
                        generation,
                        clearGameSingleSignOn: false,
                        replaceSession: false,
                        cancellationToken))
                {
                    return true;
                }
            }
            catch
            {
                await RevokeServerSessionBestEffortAsync(session).ConfigureAwait(false);
                throw;
            }

            await RevokeServerSessionBestEffortAsync(session).ConfigureAwait(false);
            return false;
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
            // HttpClient may dispose its pending-request cancellation source while a
            // handler is completing. Shutdown must not revive or fault the old refresh.
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<LauncherAuthSession> PrepareLoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        long generation = GetGeneration();
        LauncherAuthSession session = await SendLoginAsync(
            username,
            password,
            cancellationToken).ConfigureAwait(false);
        return await TrackPreparedInteractiveSessionAsync(
            session,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LauncherAuthSession> SendLoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/login")
        {
            Content = JsonContent.Create(new
            {
                username,
                password,
                deviceName = Environment.MachineName
            })
        };
        using HttpResponseMessage response = await SendResponseAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new LauncherAuthException(
                "Nom d'utilisateur ou mot de passe incorrect.",
                response.StatusCode);

        return await ReadAuthSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        long generation = GetGeneration();
        LauncherAuthSession session = await SendLoginAsync(
            username,
            password,
            cancellationToken).ConfigureAwait(false);
        await CommitInteractiveSessionAsync(
            session,
            generation,
            "La tentative de connexion n'est plus actuelle.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LauncherAuthSession> PrepareRegistrationAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        long generation = GetGeneration();
        LauncherAuthSession session = await SendRegistrationAsync(
            username,
            email,
            password,
            cancellationToken).ConfigureAwait(false);
        return await TrackPreparedInteractiveSessionAsync(
            session,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LauncherAuthSession> SendRegistrationAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "accounts")
        {
            Content = JsonContent.Create(new { username, email, password })
        };
        request.Headers.Add("X-Atlas-Device", Environment.MachineName);
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAuthSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task RegisterAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        long generation = GetGeneration();
        LauncherAuthSession session = await SendRegistrationAsync(
            username,
            email,
            password,
            cancellationToken).ConfigureAwait(false);
        await CommitInteractiveSessionAsync(
            session,
            generation,
            "La tentative d'inscription n'est plus actuelle.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GameTicket> CreateGameTicketAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "game-ticket", out long generation);
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        GameTicket ticket = await ReadAuthorizedJsonResponseAsync<GameTicket>(
            response,
            generation,
            ScalarJsonMaximumBytes,
            "La réponse du ticket de jeu",
            cancellationToken).ConfigureAwait(false)
            ?? throw new LauncherAuthException("Le serveur n'a pas renvoyé de ticket de jeu.");
        if (ticket.AccountId != GetCurrentAccountId(generation, cancellationToken))
            throw new LauncherAuthException(
                "Le ticket de jeu renvoyé ne correspond pas au compte actuel.");
        return ticket;
    }

    public async Task<EmailChangeResult> ChangeEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Patch,
            "me/email", out long generation);
        request.Content = JsonContent.Create(new { email });
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        EmailChangeResponse result =
            await ReadAuthorizedJsonResponseAsync<EmailChangeResponse>(
                response,
                generation,
                ScalarJsonMaximumBytes,
                "La réponse du changement d'adresse e-mail",
                cancellationToken).ConfigureAwait(false)
            ?? throw new LauncherAuthException("Le profil renvoyé est invalide.");
        UpdateProfile(result.Profile, generation, cancellationToken);
        return new EmailChangeResult(
            result.Profile,
            result.VerificationEmailSent,
            result.VerificationMessage);
    }

    public async Task<LauncherProfile> RefreshProfileAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "me", out long generation);
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        LauncherProfile profile = await ReadAuthorizedJsonResponseAsync<LauncherProfile>(
            response,
            generation,
            ScalarJsonMaximumBytes,
            "Le profil social Atlas",
            cancellationToken).ConfigureAwait(false)
            ?? throw new LauncherAuthException("Le profil renvoyé est invalide.");
        UpdateProfile(profile, generation, cancellationToken);
        return profile;
    }

    public async Task<LauncherProfile> UpdateSocialProfileAsync(
        string statusMessage,
        string bio,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Patch,
            "me/social-profile", out long generation);
        request.Content = JsonContent.Create(new { statusMessage, bio });
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        LauncherProfile profile = await ReadAuthorizedJsonResponseAsync<LauncherProfile>(
            response,
            generation,
            ScalarJsonMaximumBytes,
            "Le profil Atlas",
            cancellationToken).ConfigureAwait(false)
            ?? throw new LauncherAuthException("Le profil renvoyé est invalide.");
        UpdateProfile(profile, generation, cancellationToken);
        return profile;
    }

    public async Task<LauncherProfile> ChangeAvatarAsync(
        string? avatarKey,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Patch,
            "me/avatar", out long generation);
        request.Content = JsonContent.Create(new { avatarKey });
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        LauncherProfile profile = await ReadAuthorizedJsonResponseAsync<LauncherProfile>(
            response,
            generation,
            ScalarJsonMaximumBytes,
            "Le profil d'avatar Atlas",
            cancellationToken).ConfigureAwait(false)
            ?? throw new LauncherAuthException("Le profil renvoyé est invalide.");
        UpdateProfile(profile, generation, cancellationToken);
        return profile;
    }

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpRequestMessage request = CreateAuthorizedRequest(
                HttpMethod.Post,
                "me/password", out long generation);
            uint expectedAccountId = GetCurrentAccountId(generation, cancellationToken);
            request.Content = JsonContent.Create(new { currentPassword, newPassword });
            using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await EnsureAuthorizedSuccessAsync(
                response,
                generation,
                cancellationToken).ConfigureAwait(false);
            LauncherAuthSession replacement = await ReadAuthSessionContentAsync(
                response,
                cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfSessionChanged(generation, cancellationToken);
                if (replacement.Profile.AccountId != expectedAccountId)
                {
                    throw new LauncherAuthException(
                        "La session renvoyée ne correspond pas au compte actuel.");
                }
                if (!TryCommitSession(
                        replacement,
                        generation,
                        clearGameSingleSignOn: true,
                        replaceSession: true,
                        cancellationToken))
                {
                    throw new OperationCanceledException(
                        "La session du changement de mot de passe n'est plus actuelle.");
                }
            }
            catch
            {
                await RevokeServerSessionBestEffortAsync(replacement).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<IReadOnlyList<LauncherDeviceSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "me/sessions", out long generation);
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await ReadAuthorizedJsonResponseAsync<List<LauncherDeviceSession>>(
            response,
            generation,
            CollectionJsonMaximumBytes,
            "La liste des sessions Atlas",
            cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public async Task RevokeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            "me/sessions/" + Uri.EscapeDataString(sessionId), out long generation);
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "friends", out long generation);
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await ReadAuthorizedJsonResponseAsync<List<LauncherFriend>>(
            response,
            generation,
            CollectionJsonMaximumBytes,
            "La liste d'amis Atlas",
            cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public async Task<string> SendFriendRequestAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "friends/requests", out long generation);
        request.Content = JsonContent.Create(new { username });
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        ApiMessage? result = await ReadAuthorizedJsonResponseAsync<ApiMessage>(
            response,
            generation,
            ScalarJsonMaximumBytes,
            "La réponse de demande d'ami",
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result?.Message)
            ? "Demande d'ami envoyée."
            : result.Message;
    }

    public async Task AcceptFriendAsync(
        uint accountId,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"friends/{accountId}/accept", out long generation);
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
    }

    public async Task RemoveFriendAsync(
        uint accountId,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"friends/{accountId}", out long generation);
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
    }

    public async Task<LauncherServerStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "status", out long generation);
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await ReadAuthorizedJsonResponseAsync<LauncherServerStatus>(
            response,
            generation,
            ScalarJsonMaximumBytes,
            "Le statut Atlas",
            cancellationToken).ConfigureAwait(false)
            ?? throw new LauncherAuthException("Le statut Atlas renvoyé est invalide.");
    }

    public async Task<IReadOnlyList<LauncherNews>> GetNewsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "news", out long generation);
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await ReadAuthorizedJsonResponseAsync<List<LauncherNews>>(
            response,
            generation,
            CollectionJsonMaximumBytes,
            "Les actualités Atlas",
            cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public async Task<string> ResendVerificationAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "me/email/resend", out long generation);
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await SendResponseAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        ApiMessage? result = await ReadAuthorizedJsonResponseAsync<ApiMessage>(
            response,
            generation,
            ScalarJsonMaximumBytes,
            "La réponse de validation de l'adresse e-mail",
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result?.Message)
            ? "L'e-mail de validation a été envoyé."
            : result.Message;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            List<SessionRevocationProof> revocations = [];
            StoredLauncherSession? stored = null;
            lock (_stateSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                bool storedProofWasRotated = _preparedRestoreSession is not null;
                AddRevocationProof(revocations, _session);
                AddRevocationProof(revocations, _preparedRestoreSession);
                foreach (LauncherAuthSession prepared in _preparedInteractiveSessions)
                    AddRevocationProof(revocations, prepared);
                foreach (SessionRevocationProof pending in _pendingRevocations)
                    AddRevocationProof(revocations, pending);

                try
                {
                    if (!storedProofWasRotated) stored = _loadSession();
                }
                catch (Exception exception)
                {
                    failure = CombineFailure(failure, exception);
                }

                if (!storedProofWasRotated
                    && stored is not null
                    && !revocations.Any(item =>
                        string.Equals(
                            item.RefreshToken,
                            stored.RefreshToken,
                            StringComparison.Ordinal)))
                {
                    revocations.Add(new SessionRevocationProof(null, stored.RefreshToken));
                }

                try { InvalidateLocalSessionUnsafe(); }
                catch (Exception exception)
                {
                    failure = CombineFailure(failure, exception);
                }
            }

            foreach (SessionRevocationProof revocation in revocations)
            {
                try
                {
                    await RevokeServerSessionAsync(revocation).ConfigureAwait(false);
                    RemovePendingRevocationProof(revocation);
                }
                catch (Exception exception)
                {
                    // Every captured session gets an attempt even if an earlier
                    // family could not be reached. Local logout already won.
                    RetainPendingRevocationProof(revocation);
                    failure = CombineFailure(failure, exception);
                }
            }

            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task CommitInteractiveSessionAsync(
        LauncherAuthSession session,
        long generation,
        string staleMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            if (TryCommitSession(
                    session,
                    generation,
                    clearGameSingleSignOn: true,
                    replaceSession: true,
                    cancellationToken))
            {
                return;
            }
        }
        catch
        {
            _ = await RevokeServerSessionBestEffortAsync(session).ConfigureAwait(false);
            throw;
        }

        await RevokeServerSessionBestEffortAsync(session).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(staleMessage);
    }

    private async Task RevokeServerSessionAsync(SessionRevocationProof session)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken)
            && string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            return;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/logout");
        if (!string.IsNullOrWhiteSpace(session.AccessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }

        request.Content = JsonContent.Create(new { refreshToken = session.RefreshToken });
        using HttpResponseMessage response = await SendResponseAsync(request, CancellationToken.None)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> RevokeServerSessionBestEffortAsync(LauncherAuthSession session)
    {
        SessionRevocationProof proof = new(session.AccessToken, session.RefreshToken);
        try
        {
            await RevokeServerSessionAsync(proof).ConfigureAwait(false);
            RemoveConfirmedRevocationProof(proof);
            return true;
        }
        catch (Exception)
        {
            // A stale response must remain cancelled even when its compensating
            // network cleanup cannot be confirmed. Retain the proof so a later
            // explicit logout can retry it with every other captured family.
            RetainPendingRevocationProof(proof);
            return false;
        }
    }

    private static void AddRevocationProof(
        List<SessionRevocationProof> revocations,
        LauncherAuthSession? session)
    {
        if (session is null) return;
        AddRevocationProof(
            revocations,
            new SessionRevocationProof(session.AccessToken, session.RefreshToken));
    }

    private static void AddRevocationProof(
        List<SessionRevocationProof> revocations,
        SessionRevocationProof proof)
    {
        if ((string.IsNullOrWhiteSpace(proof.AccessToken)
                && string.IsNullOrWhiteSpace(proof.RefreshToken))
            || revocations.Any(item => IsSameRevocationProof(item, proof)))
        {
            return;
        }

        revocations.Add(proof);
    }

    private void RetainPendingRevocationProof(SessionRevocationProof proof)
    {
        lock (_stateSync)
        {
            if (!_disposed) AddRevocationProof(_pendingRevocations, proof);
        }
    }

    private void RemovePendingRevocationProof(SessionRevocationProof proof)
    {
        lock (_stateSync)
        {
            _pendingRevocations.RemoveAll(item => IsSameRevocationProof(item, proof));
        }
    }

    private void RemoveConfirmedRevocationProof(SessionRevocationProof proof)
    {
        lock (_stateSync)
        {
            _pendingRevocations.RemoveAll(item => IsSameRevocationProof(item, proof));
            if (_preparedRestoreSession is not null
                && IsSameRevocationProof(
                    new SessionRevocationProof(
                        _preparedRestoreSession.AccessToken,
                        _preparedRestoreSession.RefreshToken),
                    proof))
            {
                if (_preparedSessionGenerations.TryGetValue(
                        _preparedRestoreSession,
                        out PreparedSessionGeneration? restoreState))
                {
                    restoreState.Active = false;
                }
                _preparedRestoreSession = null;
            }

            foreach (LauncherAuthSession prepared in _preparedInteractiveSessions)
            {
                if (IsSameRevocationProof(
                        new SessionRevocationProof(
                            prepared.AccessToken,
                            prepared.RefreshToken),
                        proof)
                    && _preparedSessionGenerations.TryGetValue(
                        prepared,
                        out PreparedSessionGeneration? preparedState))
                {
                    preparedState.Active = false;
                }
            }
            _preparedInteractiveSessions.RemoveAll(prepared =>
                IsSameRevocationProof(
                    new SessionRevocationProof(
                        prepared.AccessToken,
                        prepared.RefreshToken),
                    proof));
        }
    }

    private static bool IsSameRevocationProof(
        SessionRevocationProof left,
        SessionRevocationProof right)
    {
        if (!string.IsNullOrWhiteSpace(left.RefreshToken)
            && !string.IsNullOrWhiteSpace(right.RefreshToken))
        {
            return string.Equals(
                left.RefreshToken,
                right.RefreshToken,
                StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(left.AccessToken)
            && !string.IsNullOrWhiteSpace(right.AccessToken)
            && string.Equals(left.AccessToken, right.AccessToken, StringComparison.Ordinal);
    }

    private static Exception CombineFailure(Exception? previous, Exception next)
        => previous is null ? next : new AggregateException(previous, next);

    public void CommitSession(
        LauncherAuthSession session,
        bool clearGameSingleSignOn)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparedSessionGenerations.TryGetValue(
                    session,
                    out PreparedSessionGeneration? prepared)
                && (!prepared.Active || prepared.Generation != _generation))
            {
                throw new OperationCanceledException(
                    "La session préparée n'est plus actuelle.");
            }
            CommitSessionUnsafe(session, clearGameSingleSignOn, replaceSession: true);
        }
    }

    private bool TryCommitSession(LauncherAuthSession session, long generation,
        bool clearGameSingleSignOn, bool replaceSession, CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || _generation != generation) return false;
            CommitSessionUnsafe(session, clearGameSingleSignOn, replaceSession);
            return true;
        }
    }

    private void CommitSessionUnsafe(LauncherAuthSession session, bool clearGameSingleSignOn,
        bool replaceSession)
    {
        // The generation check and persistence share the logout lock. Checking only before
        // Save would allow logout to delete the file and a late refresh to recreate it.
        if (clearGameSingleSignOn) _clearGameSingleSignOn();
        _saveSession(new StoredLauncherSession(session.RefreshToken, session.RefreshExpiresAt));
        DeactivatePreparedSessionsUnsafe(session);
        _preparedRestoreSession = null;
        if (replaceSession) _generation++;
        _session = session;
        _restoreSuppressed = false;
    }

    public void Dispose()
    {
        lock (_stateSync)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _session = null;
            DeactivatePreparedSessionsUnsafe();
            _preparedRestoreSession = null;
            _pendingRevocations.Clear();
        }

        // An in-flight refresh still owns its lease and must be able to release it.
        // No wait handle is allocated for this managed semaphore.
        _http.Dispose();
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path, out long generation)
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null)
                throw new LauncherAuthException("Connecte-toi au launcher pour continuer.");

            generation = _generation;
            HttpRequestMessage request = new(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.AccessToken);
            return request;
        }
    }

    private Task<HttpResponseMessage> SendResponseAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

    private void UpdateProfile(LauncherProfile profile, long generation, CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || _generation != generation || _session is null)
                throw new OperationCanceledException("La session du profil n'est plus actuelle.");
            if (_session.Profile.AccountId != profile.AccountId)
                throw new LauncherAuthException("Le profil renvoyé ne correspond pas à la session.");
            _session = _session with { Profile = profile };
        }
    }

    private async Task EnsureAuthorizedSuccessAsync(HttpResponseMessage response, long generation,
        CancellationToken cancellationToken)
    {
        ThrowIfSessionChanged(generation, cancellationToken);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Parsing an error body can also await. Never let an old 401 escape to
            // a caller that would interpret it as a rejection of the new account.
            ThrowIfSessionChanged(generation, cancellationToken);
        }
    }

    private void ThrowIfSessionChanged(long generation, CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || _generation != generation || _session is null)
                throw new OperationCanceledException("La session de la requête n'est plus actuelle.");
        }
    }

    private uint GetCurrentAccountId(long generation, CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || _generation != generation || _session is null)
                throw new OperationCanceledException("La session de la requête n'est plus actuelle.");
            return _session.Profile.AccountId;
        }
    }

    private long GetGeneration()
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _generation;
        }
    }

    private async Task<LauncherAuthSession> TrackPreparedInteractiveSessionAsync(
        LauncherAuthSession session,
        long generation,
        CancellationToken cancellationToken)
    {
        List<LauncherAuthSession> superseded = [];
        bool rejected;
        lock (_stateSync)
        {
            rejected = cancellationToken.IsCancellationRequested
                || _disposed
                || _generation != generation;
            if (!rejected)
            {
                HashSet<string> distinctRefreshTokens = new(StringComparer.Ordinal);
                List<LauncherAuthSession> duplicateRepresentations = [];
                foreach (LauncherAuthSession prepared in _preparedInteractiveSessions)
                {
                    if (_preparedSessionGenerations.TryGetValue(
                            prepared,
                            out PreparedSessionGeneration? preparedState))
                    {
                        preparedState.Active = false;
                    }

                    if (string.Equals(
                            prepared.RefreshToken,
                            session.RefreshToken,
                            StringComparison.Ordinal))
                    {
                        duplicateRepresentations.Add(prepared);
                    }
                    else if (distinctRefreshTokens.Add(prepared.RefreshToken))
                    {
                        superseded.Add(prepared);
                    }
                }

                foreach (LauncherAuthSession duplicate in duplicateRepresentations)
                {
                    _preparedInteractiveSessions.RemoveAll(
                        item => ReferenceEquals(item, duplicate));
                }
                TrackPreparedSessionUnsafe(session, generation);
                _preparedInteractiveSessions.Add(session);
            }
        }

        if (rejected)
        {
            await RevokeServerSessionBestEffortAsync(session).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(
                "La tentative d'authentification n'est plus actuelle.");
        }

        // A later prepare replaces every earlier uncommitted family. Revoke the
        // replaced families before exposing the new candidate to its caller.
        foreach (LauncherAuthSession prepared in superseded)
        {
            bool revoked = await RevokeServerSessionBestEffortAsync(prepared)
                .ConfigureAwait(false);
            if (revoked)
            {
                lock (_stateSync)
                {
                    _preparedInteractiveSessions.RemoveAll(
                        item => ReferenceEquals(item, prepared));
                }
            }
        }

        PreparedSessionGeneration? tracked = null;
        lock (_stateSync)
        {
            bool isTracked = _preparedSessionGenerations.TryGetValue(session, out tracked);
            rejected = cancellationToken.IsCancellationRequested
                || _disposed
                || _generation != generation
                || !isTracked
                || !tracked!.Active;
            if (rejected)
            {
                if (tracked is not null) tracked.Active = false;
                _preparedInteractiveSessions.RemoveAll(item => ReferenceEquals(item, session));
            }
        }

        if (rejected)
        {
            _ = await RevokeServerSessionBestEffortAsync(session).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(
                "La tentative d'authentification n'est plus actuelle.");
        }

        return session;
    }

    private void TrackPreparedSessionUnsafe(
        LauncherAuthSession session,
        long generation)
    {
        _preparedSessionGenerations.Remove(session);
        _preparedSessionGenerations.Add(
            session,
            new PreparedSessionGeneration(generation));
    }

    private void DeactivatePreparedSessionsUnsafe(
        LauncherAuthSession? committedSession = null)
    {
        if (_preparedRestoreSession is not null
            && _preparedSessionGenerations.TryGetValue(
                _preparedRestoreSession,
                out PreparedSessionGeneration? restoreState))
        {
            restoreState.Active = false;
        }

        if (committedSession is not null
            && _preparedRestoreSession is not null
            && !string.Equals(
                committedSession.RefreshToken,
                _preparedRestoreSession.RefreshToken,
                StringComparison.Ordinal)
            && !_preparedInteractiveSessions.Any(item =>
                string.Equals(
                    item.RefreshToken,
                    _preparedRestoreSession.RefreshToken,
                    StringComparison.Ordinal)))
        {
            _preparedInteractiveSessions.Add(_preparedRestoreSession);
        }

        foreach (LauncherAuthSession prepared in _preparedInteractiveSessions)
        {
            if (_preparedSessionGenerations.TryGetValue(
                    prepared,
                    out PreparedSessionGeneration? preparedState))
            {
                preparedState.Active = false;
            }
        }

        if (committedSession is null)
        {
            _preparedInteractiveSessions.Clear();
        }
        else
        {
            // Keep any distinct candidate whose cleanup was not confirmed so a
            // later explicit logout can retry it alongside the committed family.
            _preparedInteractiveSessions.RemoveAll(item =>
                string.Equals(
                    item.RefreshToken,
                    committedSession.RefreshToken,
                    StringComparison.Ordinal));
        }
    }

    private bool IsDisposed()
    {
        lock (_stateSync) return _disposed;
    }

    private async Task<LauncherAuthSession> ReadAuthSessionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAuthSessionContentAsync(response, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LauncherAuthSession> ReadAuthSessionContentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        LauncherAuthSession? session = await ReadJsonResponseAsync<LauncherAuthSession>(
            response.Content,
            AuthenticationJsonMaximumBytes,
            "La réponse d'authentification",
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (session is null
            || !IsAtlasToken(session.AccessToken, "atl_access")
            || !IsAtlasToken(session.RefreshToken, "atl_refresh")
            || session.AccessExpiresAt <= now
            || session.RefreshExpiresAt < session.AccessExpiresAt
            || session.Profile is null
            || session.Profile.AccountId == 0
            || string.IsNullOrWhiteSpace(session.Profile.Username)
            || string.IsNullOrWhiteSpace(session.Profile.Email))
        {
            throw new LauncherAuthException("La réponse d'authentification est invalide.");
        }

        return session;
    }

    private async Task<T?> ReadAuthorizedJsonResponseAsync<T>(
        HttpResponseMessage response,
        long generation,
        int maximumBytes,
        string resourceName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadJsonResponseAsync<T>(
                response.Content,
                maximumBytes,
                resourceName,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // ResponseHeadersRead returns before the body is consumed. A logout or
            // account switch during that await must make the old payload unobservable.
            ThrowIfSessionChanged(generation, cancellationToken);
        }
    }

    private static async Task<T?> ReadJsonResponseAsync<T>(
        HttpContent content,
        int maximumBytes,
        string resourceName,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource readTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(JsonResponseReadTimeout);
            byte[] payload = await BoundedJsonHttpContent.ReadAsync(
                content,
                maximumBytes,
                resourceName,
                readTimeout.Token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            BoundedJsonHttpContent.RejectDuplicateProperties(
                document.RootElement,
                resourceName);
            return document.RootElement.Deserialize<T>(JsonOptions);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new LauncherAuthException(
                resourceName + " n'a pas été reçue dans le délai autorisé.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or JsonException
                or NotSupportedException)
        {
            throw new LauncherAuthException(resourceName + " est invalide.");
        }
    }

    private static bool IsAtlasToken(string? token, string kind)
    {
        string prefix = kind + "-";
        return token is not null
            && token.Length == prefix.Length + 43
            && token.StartsWith(prefix, StringComparison.Ordinal)
            && token[prefix.Length..].All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_');
    }

    public void InvalidateLocalSession()
    {
        lock (_stateSync) InvalidateLocalSessionUnsafe();
    }

    private void InvalidateLocalSession(long generation, CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_disposed && _generation == generation) InvalidateLocalSessionUnsafe();
        }
    }

    private void InvalidateLocalSessionUnsafe()
    {
        _generation++;
        _session = null;
        DeactivatePreparedSessionsUnsafe();
        _preparedRestoreSession = null;
        // Even if the file cannot be cleared, this service must not restore it again.
        _restoreSuppressed = true;
        try { _clearSession(); }
        finally { _clearGameSingleSignOn(); }
    }

    internal static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string fallback = response.StatusCode switch
        {
            HttpStatusCode.Conflict => "Ce nom d'utilisateur ou cette adresse e-mail est déjà utilisé.",
            HttpStatusCode.TooManyRequests => "Trop de tentatives. Attends une minute puis réessaie.",
            HttpStatusCode.Unauthorized => "Ta session n'est plus valide. Reconnecte-toi.",
            _ => "Atlas n'a pas pu traiter la demande."
        };

        ApiError? error;
        try
        {
            error = await ReadJsonResponseAsync<ApiError>(
                response.Content,
                ErrorJsonMaximumBytes,
                "La réponse d'erreur Atlas",
                cancellationToken).ConfigureAwait(false);
        }
        catch (LauncherAuthException)
        {
            throw new LauncherAuthException(fallback, response.StatusCode);
        }

        throw new LauncherAuthException(
            string.IsNullOrWhiteSpace(error?.Error) ? fallback : error.Error,
            response.StatusCode,
            error?.Code);
    }

    private sealed record ApiError(string Error, string? Code);
    private sealed record ApiMessage(string Message);
    private sealed record SessionRevocationProof(string? AccessToken, string? RefreshToken);

    private sealed class PreparedSessionGeneration(long generation)
    {
        internal long Generation { get; } = generation;
        internal bool Active { get; set; } = true;
    }
}

internal sealed record LauncherAuthSession(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    LauncherProfile Profile);

internal sealed record LauncherProfile(
    uint AccountId,
    string Username,
    string Email,
    bool EmailVerified,
    string? AvatarKey,
    bool TwoFactorEnabled,
    bool RecoveryCodesGenerated,
    int Completion,
    Account.AvatarDescriptor? Avatar = null,
    string StatusMessage = "",
    string Bio = "");

internal sealed record EmailChangeResult(
    LauncherProfile Profile,
    bool VerificationEmailSent,
    string VerificationMessage);

internal sealed record EmailChangeResponse(
    LauncherProfile Profile,
    bool VerificationEmailSent,
    string VerificationMessage);

internal sealed record GameTicket(
    string Ticket,
    DateTimeOffset ExpiresAt,
    string Username,
    string GameAccount,
    uint AccountId);

internal sealed record LauncherDeviceSession(
    string Id,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    bool Current)
{
    public bool CanRevoke => !Current;

    public string LastSeenText =>
        Current ? "Session actuelle" : $"Dernière activité {LastSeenAt.ToLocalTime():dd/MM/yyyy HH:mm}";

    public string ExpiresText => $"Expire le {ExpiresAt.ToLocalTime():dd/MM/yyyy}";
}

internal sealed record LauncherFriend(
    uint AccountId,
    string Username,
    string? AvatarKey,
    string Relationship,
    bool Online,
    string? CharacterName,
    byte? Level,
    byte? ClassId,
    uint? ZoneId,
    DateTimeOffset? LastSeenAt,
    Account.AvatarDescriptor? Avatar = null,
    string StatusMessage = "",
    string Bio = "",
    IReadOnlyList<LauncherFriendCharacter>? Characters = null,
    bool LauncherOnline = false,
    DateTimeOffset? LauncherLastSeenAt = null,
    string? Presence = null)
{
    public string Initial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();

    public string CharacterText => string.IsNullOrWhiteSpace(CharacterName)
        ? "Aucun personnage créé"
        : $"{CharacterName} · {GetClassName(ClassId)} niveau {Level}";

    public string PresenceText
    {
        get
        {
            if (Online) return "En jeu";
            if (LauncherOnline) return "Connecté au launcher";
            DateTimeOffset? lastSeen = LastSeenAt;
            if (LauncherLastSeenAt is DateTimeOffset launcherSeen
                && (lastSeen is null || launcherSeen > lastSeen.Value)) lastSeen = launcherSeen;
            return lastSeen is null
                ? "Hors ligne"
                : $"Hors ligne · vu le {lastSeen.Value.ToLocalTime():dd/MM à HH:mm}";
        }
    }

    private static string GetClassName(byte? classId) => classId switch
    {
        1 => "Guerrier",
        2 => "Paladin",
        3 => "Chasseur",
        4 => "Voleur",
        5 => "Prêtre",
        6 => "Chevalier de la mort",
        7 => "Chaman",
        8 => "Mage",
        9 => "Démoniste",
        11 => "Druide",
        _ => "Personnage"
    };
}

internal sealed record LauncherServerStatus(
    string Realm,
    bool Api,
    bool Authentication,
    bool RealmGateway,
    bool WorldGateway,
    bool WorldServer,
    DateTimeOffset CheckedAt,
    int? OnlinePlayers = null,
    string? OnlinePlayerCountKind = null);

internal sealed record LauncherNews(
    string Id,
    string Category,
    string Title,
    string Summary,
    DateTimeOffset PublishedAt,
    LauncherNewsSection[]? Sections = null)
{
    public string PublishedText => PublishedAt.ToLocalTime().ToString("dd MMMM yyyy");
}

internal sealed record LauncherFriendCharacter(
    string Name,
    byte Level,
    byte ClassId,
    uint ZoneId,
    bool Online,
    DateTimeOffset? LastSeenAt);

internal sealed record LauncherNewsSection(
    string Title,
    string[] Items);

internal sealed class LauncherAuthException : Exception
{
    public LauncherAuthException(
        string message,
        HttpStatusCode? statusCode = null,
        string? code = null) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    internal HttpStatusCode? StatusCode { get; }

    internal string? Code { get; }
}
