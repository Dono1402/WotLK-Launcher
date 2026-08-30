namespace WotLK.Launcher;

internal interface ILauncherAuthService : IDisposable
{
    LauncherAuthSession? Session { get; }

    string? AccessToken { get; }

    bool IsAuthenticated { get; }

    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);

    Task<bool> EnsureFreshAsync(CancellationToken cancellationToken = default);

    Task LoginAsync(string username, string password, CancellationToken cancellationToken = default);

    Task RegisterAsync(string username, string email, string password, CancellationToken cancellationToken = default);

    Task<GameTicket> CreateGameTicketAsync(CancellationToken cancellationToken = default);

    Task<EmailChangeResult> ChangeEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<LauncherProfile> RefreshProfileAsync(CancellationToken cancellationToken = default);

    Task<LauncherProfile> ChangeAvatarAsync(string? avatarKey, CancellationToken cancellationToken = default);

    Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LauncherDeviceSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default);

    Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LauncherFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default);

    Task<string> SendFriendRequestAsync(string username, CancellationToken cancellationToken = default);

    Task AcceptFriendAsync(uint accountId, CancellationToken cancellationToken = default);

    Task RemoveFriendAsync(uint accountId, CancellationToken cancellationToken = default);

    Task<LauncherServerStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LauncherNews>> GetNewsAsync(CancellationToken cancellationToken = default);

    Task<string> ResendVerificationAsync(CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);
}
