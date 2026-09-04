using System.Collections.Immutable;
using WotLK.Launcher.Account;

namespace WotLK.Launcher.Runtime;

internal enum FriendRelationship
{
    Accepted,
    Incoming,
    Outgoing
}

internal enum FriendsLoadState
{
    SignedOut,
    Idle,
    Loading,
    Loaded,
    Failed
}

internal enum FriendsSearchState
{
    Idle,
    Sending,
    Succeeded,
    Failed
}

internal enum FriendsOperationState
{
    None,
    Refreshing,
    SendingRequest,
    AcceptingRequest,
    RejectingRequest,
    CancellingRequest,
    RemovingFriend
}

internal enum FriendsErrorCategory
{
    None,
    Validation,
    Self,
    UserNotFound,
    RelationNotFound,
    AlreadyPending,
    AlreadyFriends,
    Unauthorized,
    Forbidden,
    Network,
    Timeout,
    ServiceUnavailable,
    ServerRejected,
    Unknown
}

internal enum FriendsNoticeKind
{
    None,
    RequestSent,
    FriendshipAccepted,
    RequestAccepted,
    RequestRejected,
    RequestCancelled,
    FriendRemoved
}

internal sealed record FriendRuntimeItem(
    uint AccountId,
    string Username,
    string? AvatarKey,
    AvatarDescriptor? Avatar,
    FriendRelationship Relationship,
    bool IsOnline,
    string? CharacterName,
    byte? Level,
    byte? ClassId,
    uint? ZoneId,
    DateTimeOffset? LastSeenAt,
    string StatusMessage = "",
    string Bio = "",
    ImmutableArray<FriendCharacterRuntimeItem> Characters = default);

internal sealed record FriendCharacterRuntimeItem(
    string Name,
    byte Level,
    byte ClassId,
    uint ZoneId,
    bool IsOnline,
    DateTimeOffset? LastSeenAt);

internal sealed record FriendsRuntimeError(
    FriendsOperationState Operation,
    FriendsErrorCategory Category)
{
    internal static FriendsRuntimeError None { get; } = new(
        FriendsOperationState.None,
        FriendsErrorCategory.None);
}

internal sealed record FriendsRuntimeSnapshot(
    long Sequence,
    long? OperationId,
    uint? CurrentUserId,
    bool IsAuthenticated,
    FriendsLoadState LoadState,
    ImmutableArray<FriendRuntimeItem> Friends,
    ImmutableArray<FriendRuntimeItem> IncomingRequests,
    ImmutableArray<FriendRuntimeItem> OutgoingRequests,
    FriendsSearchState SearchState,
    FriendsOperationState OperationState,
    uint? TargetAccountId,
    string TargetUsername,
    FriendsRuntimeError ErrorState,
    FriendsNoticeKind Notice,
    bool IsAutomaticRefresh,
    bool IsStale)
{
    internal static FriendsRuntimeSnapshot SignedOut { get; } = new(
        Sequence: 0,
        OperationId: null,
        CurrentUserId: null,
        IsAuthenticated: false,
        LoadState: FriendsLoadState.SignedOut,
        Friends: ImmutableArray<FriendRuntimeItem>.Empty,
        IncomingRequests: ImmutableArray<FriendRuntimeItem>.Empty,
        OutgoingRequests: ImmutableArray<FriendRuntimeItem>.Empty,
        SearchState: FriendsSearchState.Idle,
        OperationState: FriendsOperationState.None,
        TargetAccountId: null,
        TargetUsername: string.Empty,
        ErrorState: FriendsRuntimeError.None,
        Notice: FriendsNoticeKind.None,
        IsAutomaticRefresh: false,
        IsStale: false);

    internal bool IsBusy => OperationState != FriendsOperationState.None;
}

internal sealed class FriendsRuntimeSnapshotEventArgs : EventArgs
{
    internal FriendsRuntimeSnapshotEventArgs(FriendsRuntimeSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    internal FriendsRuntimeSnapshot Snapshot { get; }
}

internal enum FriendsActionStartStatus
{
    Started,
    Busy,
    ShuttingDown,
    NotAuthenticated,
    InvalidRequest
}

internal enum FriendsActionCompletionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Superseded
}

internal sealed record FriendsActionCompletion(
    FriendsActionCompletionStatus Status,
    FriendsRuntimeSnapshot Snapshot);

internal sealed record FriendsActionStartResult(
    FriendsActionStartStatus Status,
    long? OperationId,
    Task<FriendsActionCompletion>? Completion)
{
    internal bool IsStarted => Status == FriendsActionStartStatus.Started
        && OperationId is not null
        && Completion is not null;

    internal static FriendsActionStartResult Rejected(FriendsActionStartStatus status) =>
        new(status, null, null);
}
