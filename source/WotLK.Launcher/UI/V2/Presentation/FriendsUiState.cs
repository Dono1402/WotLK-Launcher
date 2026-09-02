using System.Collections.Immutable;
using System.Windows.Input;
using System.Windows.Media;
using WotLK.Launcher.UI.V2.Commands;

namespace WotLK.Launcher.UI.V2.Presentation;

public enum FriendsViewLoadState
{
    SignedOut,
    Idle,
    Loading,
    Loaded,
    Failed
}

public enum FriendsViewOperation
{
    None,
    Refreshing,
    SendingRequest,
    AcceptingRequest,
    RejectingRequest,
    CancellingRequest,
    RemovingFriend
}

public sealed record FriendUiItem(
    uint AccountId,
    string Username,
    string Initial,
    string AvatarColor,
    bool HasAvatarTheme,
    Guid? AvatarId,
    ulong? AvatarVersion,
    ImageSource? AvatarImage,
    bool HasAvatarImage,
    bool IsOnline,
    string PresenceText,
    string CharacterName,
    string CharacterDetails,
    bool HasCharacter,
    bool IsBusy,
    bool CanAccept,
    bool CanReject,
    bool CanCancel,
    bool CanRemove);

public sealed record FriendsViewState(
    bool IsPreview,
    bool IsRuntimeConnected,
    FriendsViewLoadState LoadState,
    ImmutableArray<FriendUiItem> Friends,
    ImmutableArray<FriendUiItem> IncomingRequests,
    ImmutableArray<FriendUiItem> OutgoingRequests,
    FriendsViewOperation Operation,
    string StatusMessage,
    string ErrorMessage,
    string NoticeMessage,
    bool CanRefresh,
    bool CanSendRequest,
    bool IsStale)
{
    public int OnlineCount => Friends.Count(friend => friend.IsOnline);

    public bool HasFriends => !Friends.IsDefaultOrEmpty;

    public bool HasIncomingRequests => !IncomingRequests.IsDefaultOrEmpty;

    public bool HasOutgoingRequests => !OutgoingRequests.IsDefaultOrEmpty;

    public bool IsLoading => LoadState == FriendsViewLoadState.Loading;

    public bool ShowsGlobalEmpty => LoadState == FriendsViewLoadState.Loaded
        && !HasFriends
        && !HasIncomingRequests
        && !HasOutgoingRequests;

    public bool ShowsError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool ShowsNotice => !string.IsNullOrWhiteSpace(NoticeMessage);
}

public sealed class FriendsUiState : BindableUiState
{
    private FriendsViewState _current;
    private bool _isOpen;
    private string _searchText = string.Empty;

    internal FriendsUiState(FriendsViewState? current = null)
    {
        _current = current ?? EmptyView;
    }

    public static FriendsViewState EmptyView { get; } = new(
        IsPreview: false,
        IsRuntimeConnected: false,
        LoadState: FriendsViewLoadState.SignedOut,
        Friends: ImmutableArray<FriendUiItem>.Empty,
        IncomingRequests: ImmutableArray<FriendUiItem>.Empty,
        OutgoingRequests: ImmutableArray<FriendUiItem>.Empty,
        Operation: FriendsViewOperation.None,
        StatusMessage: string.Empty,
        ErrorMessage: string.Empty,
        NoticeMessage: string.Empty,
        CanRefresh: false,
        CanSendRequest: false,
        IsStale: false);

    public FriendsViewState Current => _current;

    // Legacy preview/tests read this projection directly; Current remains the only stored state.
    public IReadOnlyList<FriendUiItem> Friends => _current.Friends;

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? string.Empty);
    }

    public ICommand RefreshCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand SendRequestCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand AcceptRequestCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand RejectRequestCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand CancelRequestCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand RemoveFriendCommand { get; private set; } = DisabledCommand.Instance;

    internal void AttachCommands(
        ICommand refresh,
        ICommand send,
        ICommand accept,
        ICommand reject,
        ICommand cancel,
        ICommand remove)
    {
        RefreshCommand = refresh ?? DisabledCommand.Instance;
        SendRequestCommand = send ?? DisabledCommand.Instance;
        AcceptRequestCommand = accept ?? DisabledCommand.Instance;
        RejectRequestCommand = reject ?? DisabledCommand.Instance;
        CancelRequestCommand = cancel ?? DisabledCommand.Instance;
        RemoveFriendCommand = remove ?? DisabledCommand.Instance;
        RaisePropertyChanged(string.Empty);
    }

    internal void AttachPreviewCommands()
    {
        AttachCommands(
            PreviewCommand.Instance,
            PreviewCommand.Instance,
            PreviewCommand.Instance,
            PreviewCommand.Instance,
            PreviewCommand.Instance,
            PreviewCommand.Instance);
    }

    internal void ApplyRuntimeView(FriendsViewState state)
    {
        _current = state ?? throw new ArgumentNullException(nameof(state));
        if (!state.IsRuntimeConnected)
        {
            _isOpen = false;
            _searchText = string.Empty;
        }
        RaisePropertyChanged(string.Empty);
    }

    internal void ClearSearchText()
    {
        SearchText = string.Empty;
    }

    internal void ShowLocalSearchError(string message)
    {
        _current = _current with
        {
            ErrorMessage = message,
            NoticeMessage = string.Empty
        };
        RaisePropertyChanged(string.Empty);
    }

    internal void ApplyAvatarImage(
        uint accountId,
        Guid avatarId,
        ulong version,
        ImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _current = _current with
        {
            Friends = ApplyAvatar(_current.Friends),
            IncomingRequests = ApplyAvatar(_current.IncomingRequests),
            OutgoingRequests = ApplyAvatar(_current.OutgoingRequests)
        };
        RaisePropertyChanged(string.Empty);

        ImmutableArray<FriendUiItem> ApplyAvatar(ImmutableArray<FriendUiItem> source)
        {
            return source
                .Select(item => item.AccountId == accountId
                    && item.AvatarId == avatarId
                    && item.AvatarVersion == version
                        ? item with { AvatarImage = image, HasAvatarImage = true }
                        : item)
                .ToImmutableArray();
        }
    }
}
