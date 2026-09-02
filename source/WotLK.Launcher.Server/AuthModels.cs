using System.Text.Json.Serialization;

namespace WotLK.Launcher.Server;

public sealed record RegisterRequest(string Username, string Email, string Password);
public sealed record LoginRequest(string Username, string Password, string? DeviceName);
public sealed record EnrollExistingAccountRequest(
    string Username,
    string CurrentPassword,
    string Email);
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
    int Completion,
    Avatars.AvatarDescriptor? Avatar);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    AccountProfile Profile);

public enum AtlasLoginOutcome
{
    Succeeded,
    InvalidCredentials,
    AtlasProfileRequired
}

public sealed record AtlasLoginResult(
    AtlasLoginOutcome Outcome,
    AuthResponse? Response);

public enum AtlasEnrollmentOutcome
{
    Succeeded,
    InvalidCredentials,
    AlreadyEnrolled,
    NotEligible,
    EmailAlreadyUsed
}

public sealed record AtlasEnrollmentResult(
    AtlasEnrollmentOutcome Outcome,
    AuthResponse? Response);

public sealed record AtlasAuthErrorResponse(string Error, string Code);

public static class AtlasAuthErrorCodes
{
    public const string ProfileRequired = "AtlasProfileRequired";
    public const string ProfileRequiredMessage =
        "Ce compte n’est pas encore inscrit dans Atlas Launcher.";
    public const string EnrollmentNotAllowed = "AtlasEnrollmentNotAllowed";
    public const string EnrollmentNotAllowedMessage =
        "Ce compte ne peut pas être associé à Atlas.";
    public const string AlreadyEnrolled = "AtlasAlreadyEnrolled";
    public const string AlreadyEnrolledMessage =
        "Ce compte est déjà associé à Atlas.";
    public const string EmailAlreadyUsed = "AtlasEmailAlreadyUsed";
    public const string EmailAlreadyUsedMessage =
        "Cette adresse e-mail est déjà utilisée.";
}

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
