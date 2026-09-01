using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WotLK.Launcher;

internal sealed class LauncherAuthService : ILauncherAuthService
{
    private static readonly Uri ApiBaseUri = AtlasNetwork.LauncherApiBaseUri;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http = new(AtlasNetwork.CreateHandler())
    {
        BaseAddress = ApiBaseUri,
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public LauncherAuthSession? Session { get; private set; }

    public string? AccessToken => Session?.AccessToken;

    public bool IsAuthenticated =>
        Session is not null && Session.AccessExpiresAt > DateTimeOffset.UtcNow;

    public async Task<LauncherAuthRestoreAttempt> PrepareRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        StoredLauncherSession? stored = SecureSessionStore.Load();
        if (stored is null)
        {
            return new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.NoSession,
                null);
        }

        if (stored.RefreshExpiresAt <= DateTimeOffset.UtcNow)
        {
            InvalidateLocalSession();
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
            InvalidateLocalSession();
            return new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.Rejected,
                null);
        }

        LauncherAuthSession session = await ReadAuthSessionAsync(
            response,
            cancellationToken).ConfigureAwait(false);
        return new LauncherAuthRestoreAttempt(
            LauncherAuthRestoreOutcome.Restored,
            session);
    }

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        LauncherAuthRestoreAttempt attempt = await PrepareRestoreAsync(
            cancellationToken).ConfigureAwait(false);
        if (attempt.Outcome != LauncherAuthRestoreOutcome.Restored
            || attempt.Session is null)
        {
            return false;
        }

        CommitSession(attempt.Session, clearGameSingleSignOn: false);
        return true;
    }

    public async Task<bool> EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        if (Session is null)
            return false;
        if (Session.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return true;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (Session is null)
                return false;
            if (Session.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return true;

            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                "auth/refresh",
                new { refreshToken = Session.RefreshToken },
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Session = null;
                SecureSessionStore.Clear();
                GameSingleSignOn.Clear();
                return false;
            }

            LauncherAuthSession session = await ReadAuthSessionAsync(response, cancellationToken);
            CommitSession(session, clearGameSingleSignOn: false);
            return true;
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
            clearGameSingleSignOnBeforeReadingResponse: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LauncherAuthSession> SendLoginAsync(
        string username,
        string password,
        bool clearGameSingleSignOnBeforeReadingResponse,
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

        if (clearGameSingleSignOnBeforeReadingResponse)
        {
            GameSingleSignOn.Clear();
        }

        return await ReadAuthSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        LauncherAuthSession session = await SendLoginAsync(
            username,
            password,
            clearGameSingleSignOnBeforeReadingResponse: true,
            cancellationToken).ConfigureAwait(false);
        CommitSession(session, clearGameSingleSignOn: false);
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
            clearGameSingleSignOnBeforeReadingResponse: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LauncherAuthSession> SendRegistrationAsync(
        string username,
        string email,
        string password,
        bool clearGameSingleSignOnBeforeReadingResponse,
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
        if (clearGameSingleSignOnBeforeReadingResponse)
        {
            GameSingleSignOn.Clear();
        }

        return await ReadAuthSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task RegisterAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        LauncherAuthSession session = await SendRegistrationAsync(
            username,
            email,
            password,
            clearGameSingleSignOnBeforeReadingResponse: true,
            cancellationToken).ConfigureAwait(false);
        CommitSession(session, clearGameSingleSignOn: false);
    }

    public async Task<GameTicket> CreateGameTicketAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "game-ticket");
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
            "me/email");
        request.Content = JsonContent.Create(new { email });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        EmailChangeResponse result =
            await response.Content.ReadFromJsonAsync<EmailChangeResponse>(
                JsonOptions,
                cancellationToken)
            ?? throw new LauncherAuthException("Le profil renvoyé est invalide.");
        Session = Session! with { Profile = result.Profile };
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
            "me");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        LauncherProfile profile = await response.Content.ReadFromJsonAsync<LauncherProfile>(
            JsonOptions,
            cancellationToken)
            ?? throw new LauncherAuthException("Le profil renvoyé est invalide.");
        Session = Session! with { Profile = profile };
        return profile;
    }

    public async Task<LauncherProfile> ChangeAvatarAsync(
        string? avatarKey,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Patch,
            "me/avatar");
        request.Content = JsonContent.Create(new { avatarKey });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        LauncherProfile profile = await response.Content.ReadFromJsonAsync<LauncherProfile>(
            JsonOptions,
            cancellationToken)
            ?? throw new LauncherAuthException("Le profil renvoyé est invalide.");
        Session = Session! with { Profile = profile };
        return profile;
    }

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "me/password");
        request.Content = JsonContent.Create(new { currentPassword, newPassword });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherDeviceSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "me/sessions");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
            "me/sessions/" + Uri.EscapeDataString(sessionId));
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "friends");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
            "friends/requests");
        request.Content = JsonContent.Create(new { username });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
            $"friends/{accountId}/accept");
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RemoveFriendAsync(
        uint accountId,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"friends/{accountId}");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<LauncherServerStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "status");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
            "news");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<LauncherNews>>(
            JsonOptions,
            cancellationToken)
            ?? [];
    }

    public async Task<string> ResendVerificationAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "me/email/resend");
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        ApiMessage? result = await response.Content.ReadFromJsonAsync<ApiMessage>(
            JsonOptions,
            cancellationToken);
        return string.IsNullOrWhiteSpace(result?.Message)
            ? "L'e-mail de validation a été envoyé."
            : result.Message;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (Session is not null)
        {
            try
            {
                using HttpRequestMessage request = CreateAuthorizedRequest(
                    HttpMethod.Post,
                    "auth/logout");
                request.Content = JsonContent.Create(new { });
                using HttpResponseMessage response =
                    await _http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Local logout must remain possible while Atlas is unavailable.
            }
        }

        InvalidateLocalSession();
    }

    public void CommitSession(
        LauncherAuthSession session,
        bool clearGameSingleSignOn)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (clearGameSingleSignOn)
        {
            GameSingleSignOn.Clear();
        }

        SecureSessionStore.Save(new StoredLauncherSession(
            session.RefreshToken,
            session.RefreshExpiresAt));
        Session = session;
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
        _http.Dispose();
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path)
    {
        if (Session is null)
            throw new LauncherAuthException("Connecte-toi au launcher pour continuer.");

        HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Session.AccessToken);
        return request;
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
        Session = null;
        SecureSessionStore.Clear();
        GameSingleSignOn.Clear();
    }

    private static async Task EnsureSuccessAsync(
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
                response.StatusCode);
        }
        catch (JsonException)
        {
            throw new LauncherAuthException(fallback, response.StatusCode);
        }
    }

    private sealed record ApiError(string Error);
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
    Account.AvatarDescriptor? Avatar = null);

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
    DateTimeOffset? LastSeenAt)
{
    public string Initial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();

    public string CharacterText => string.IsNullOrWhiteSpace(CharacterName)
        ? "Aucun personnage créé"
        : $"{CharacterName} · {GetClassName(ClassId)} niveau {Level}";

    public string PresenceText => Online
        ? "En jeu"
        : LastSeenAt is null
            ? "Hors ligne"
            : $"Hors ligne · vu le {LastSeenAt.Value.ToLocalTime():dd/MM à HH:mm}";

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
    DateTimeOffset CheckedAt);

internal sealed record LauncherNews(
    string Id,
    string Category,
    string Title,
    string Summary,
    DateTimeOffset PublishedAt)
{
    public string PublishedText => PublishedAt.ToLocalTime().ToString("dd MMMM yyyy");
}

internal sealed class LauncherAuthException : Exception
{
    public LauncherAuthException(string message, HttpStatusCode? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }

    internal HttpStatusCode? StatusCode { get; }
}
