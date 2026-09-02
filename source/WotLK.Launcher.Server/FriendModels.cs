namespace WotLK.Launcher.Server;

public sealed record CreateFriendRequest(string Username);

public sealed record LauncherFriend(
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
    Avatars.AvatarDescriptor? Avatar = null);

public enum FriendRequestOutcome
{
    Requested,
    Accepted,
    NotFound,
    Self,
    AlreadyPending,
    AlreadyFriends
}

public sealed record FriendRequestResult(
    FriendRequestOutcome Outcome,
    uint? TargetAccountId,
    string? TargetUsername);
