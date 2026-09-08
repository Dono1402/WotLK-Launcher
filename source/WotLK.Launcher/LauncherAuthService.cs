using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace WotLK.Launcher;

internal sealed class LauncherAuthService : ILauncherAuthService
{
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
    private LauncherAuthSession? _session;
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
        long generation;
        StoredLauncherSession? stored;
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = _generation;
            stored = _restoreSuppressed ? null : _loadSession();
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

        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "auth/refresh",
            new { refreshToken = stored.RefreshToken },
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
        if (!IsCurrentGeneration(generation))
        {
            return new LauncherAuthRestoreAttempt(LauncherAuthRestoreOutcome.NoSession, null);
        }

        return new LauncherAuthRestoreAttempt(
            LauncherAuthRestoreOutcome.Restored,
            session);
    }

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        long generation = GetGeneration();
        LauncherAuthRestoreAttempt attempt = await PrepareRestoreAsync(
            cancellationToken).ConfigureAwait(false);
        if (attempt.Outcome != LauncherAuthRestoreOutcome.Restored
            || attempt.Session is null)
        {
            return false;
        }

        return TryCommitSession(attempt.Session, generation, clearGameSingleSignOn: false,
            replaceSession: true, cancellationToken);
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

            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                "auth/refresh",
                new { refreshToken = current.RefreshToken },
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateLocalSession(generation, cancellationToken);
                return false;
            }

            if (!IsCurrentGeneration(generation)) return false;
            LauncherAuthSession session = await ReadAuthSessionAsync(response, cancellationToken)
                .ConfigureAwait(false);
            return TryCommitSession(session, generation, clearGameSingleSignOn: false,
                replaceSession: false, cancellationToken);
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
        return await SendLoginAsync(
            username,
            password,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LauncherAuthSession> SendLoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "auth/login",
            new
            {
                username,
                password,
                deviceName = Environment.MachineName
            },
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
        if (!TryCommitSession(session, generation, clearGameSingleSignOn: true,
                replaceSession: true, cancellationToken))
            throw new OperationCanceledException("La tentative de connexion n'est plus actuelle.");
    }

    public async Task<LauncherAuthSession> PrepareRegistrationAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        return await SendRegistrationAsync(
            username,
            email,
            password,
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
        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
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
        if (!TryCommitSession(session, generation, clearGameSingleSignOn: true,
                replaceSession: true, cancellationToken))
            throw new OperationCanceledException("La tentative d'inscription n'est plus actuelle.");
    }

    public async Task<GameTicket> CreateGameTicketAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "game-ticket", out long generation);
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GameTicket>(
            JsonOptions,
            cancellationToken)
            ?? throw new LauncherAuthException("Le serveur n'a pas renvoyé de ticket de jeu.");
    }

    public async Task<EmailChangeResult> ChangeEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Patch,
            "me/email", out long generation);
        request.Content = JsonContent.Create(new { email });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        EmailChangeResponse result =
            await response.Content.ReadFromJsonAsync<EmailChangeResponse>(
                JsonOptions,
                cancellationToken)
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
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        LauncherProfile profile = await response.Content.ReadFromJsonAsync<LauncherProfile>(
            JsonOptions,
            cancellationToken)
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
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        LauncherProfile profile = await response.Content.ReadFromJsonAsync<LauncherProfile>(
            JsonOptions,
            cancellationToken)
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
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        LauncherProfile profile = await response.Content.ReadFromJsonAsync<LauncherProfile>(
            JsonOptions,
            cancellationToken)
            ?? throw new LauncherAuthException("Le profil renvoyé est invalide.");
        UpdateProfile(profile, generation, cancellationToken);
        return profile;
    }

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "me/password", out long generation);
        request.Content = JsonContent.Create(new { currentPassword, newPassword });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherDeviceSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "me/sessions", out long generation);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<LauncherDeviceSession>>(
            JsonOptions,
            cancellationToken)
            ?? [];
    }

    public async Task RevokeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            "me/sessions/" + Uri.EscapeDataString(sessionId), out long generation);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "friends", out long generation);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<LauncherFriend>>(
            JsonOptions,
            cancellationToken)
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
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        ApiMessage? result = await response.Content.ReadFromJsonAsync<ApiMessage>(
            JsonOptions,
            cancellationToken);
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
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
    }

    public async Task RemoveFriendAsync(
        uint accountId,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"friends/{accountId}", out long generation);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
    }

    public async Task<LauncherServerStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "status", out long generation);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<LauncherServerStatus>(
            JsonOptions,
            cancellationToken)
            ?? throw new LauncherAuthException("Le statut Atlas renvoyé est invalide.");
    }

    public async Task<IReadOnlyList<LauncherNews>> GetNewsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "news", out long generation);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<LauncherNews>>(
            JsonOptions,
            cancellationToken)
            ?? [];
    }

    public async Task<string> ResendVerificationAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "me/email/resend", out long generation);
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureAuthorizedSuccessAsync(response, generation, cancellationToken);
        ApiMessage? result = await response.Content.ReadFromJsonAsync<ApiMessage>(
            JsonOptions,
            cancellationToken);
        return string.IsNullOrWhiteSpace(result?.Message)
            ? "L'e-mail de validation a été envoyé."
            : result.Message;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        LauncherAuthSession? previous;
        Exception? cleanupFailure = null;
        lock (_stateSync)
        {
            previous = _session;
            try { InvalidateLocalSessionUnsafe(); }
            catch (Exception exception) { cleanupFailure = exception; }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previous is not null)
            {
                using HttpRequestMessage request = new(HttpMethod.Post, "auth/logout");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", previous.AccessToken);
                request.Content = JsonContent.Create(new { });
                using HttpResponseMessage response =
                    await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException)
        {
            // Remote revocation is best effort; local credentials are already invalidated.
        }
        catch (OperationCanceledException)
        {
            // A timeout or cancelled request must not leave a local session behind.
        }
        finally
        {
            if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    public void CommitSession(
        LauncherAuthSession session,
        bool clearGameSingleSignOn)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    private long GetGeneration()
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _generation;
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_stateSync) return !_disposed && _generation == generation;
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
        return await response.Content.ReadFromJsonAsync<LauncherAuthSession>(
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherAuthException("La réponse d'authentification est invalide.");
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

        try
        {
            ApiError? error = await response.Content.ReadFromJsonAsync<ApiError>(
                JsonOptions,
                cancellationToken);
            throw new LauncherAuthException(
                string.IsNullOrWhiteSpace(error?.Error) ? fallback : error.Error,
                response.StatusCode,
                error?.Code);
        }
        catch (JsonException)
        {
            throw new LauncherAuthException(fallback, response.StatusCode);
        }
    }

    private sealed record ApiError(string Error, string? Code);
    private sealed record ApiMessage(string Message);
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
