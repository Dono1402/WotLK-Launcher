using System.Text.Json.Serialization;

namespace WotLK.Launcher.Server;

public sealed record RegisterRequest(string Username, string Email, string Password);
public sealed record LoginRequest(string Username, string Password, string? DeviceName);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ChangeEmailRequest(string Email);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ChangeAvatarRequest(string? AvatarKey);

public sealed record ChangeEmailResponse(
    AccountProfile Profile,
    bool VerificationEmailSent,
    string VerificationMessage);

public sealed record EmailVerificationChallenge(
    uint AccountId,
    string Username,
    string Email,
    string Token,
    byte[] TokenHash,
    DateTimeOffset ExpiresAt);

public enum EmailVerificationResult
{
    Verified,
    AlreadyVerified,
    Expired,
    Invalid
}

public sealed record LauncherSessionInfo(
    string Id,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    bool Current);

public sealed record LauncherStatusResponse(
    string Realm,
    bool Api,
    bool Authentication,
    bool RealmGateway,
    bool WorldGateway,
    bool WorldServer,
    DateTimeOffset CheckedAt);

public sealed record LauncherNewsItem(
    string Id,
    string Category,
    string Title,
    string Summary,
    DateTimeOffset PublishedAt);

public sealed record AccountProfile(
    uint AccountId,
    string Username,
    string Email,
    bool EmailVerified,
    string? AvatarKey,
    bool TwoFactorEnabled,
    bool RecoveryCodesGenerated,
    int Completion);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    AccountProfile Profile);

public sealed record GameTicketResponse(
    string Ticket,
    DateTimeOffset ExpiresAt,
    string Username,
    string GameAccount,
    uint AccountId);

public sealed record HermesTicketRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("ticket")] string Ticket,
    [property: JsonPropertyName("locale")] string Locale);

public sealed record HermesTicketRevokeRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("revoke")] bool Revoke = true);

public sealed record HermesTicketResponse(
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

public sealed record SessionTokens(
    string AccessToken,
    byte[] AccessHash,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    byte[] RefreshHash,
    DateTimeOffset RefreshExpiresAt);

public sealed record AuthenticatedAccount(uint AccountId, string Username);

public sealed class EmailVerificationCooldownException : Exception
{
    public EmailVerificationCooldownException(int retryAfterSeconds)
        : base("Un e-mail de validation vient déjà d'être envoyé.")
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int RetryAfterSeconds { get; }
}
