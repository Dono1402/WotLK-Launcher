using System.Collections.Immutable;
using System.Windows.Input;
using System.Windows.Media;
using WotLK.Launcher.Account;
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

public sealed record FriendCharacterUiItem(
    string Name,
    string ClassName,
    byte Level,
    string ZoneName,
    bool IsOnline,
    string PresenceText,
    byte ClassId = 0)
{
    public bool IsFeatured { get; init; }

    public string Details => $"{ClassName} · niveau {Level}";

    public bool HasZone => !string.IsNullOrWhiteSpace(ZoneName);

    public string ProfilePresenceText => IsOnline ? "En jeu" : PresenceText;

    public string ClassColor => ClassId switch
    {
        1 => "#C79C6E", 2 => "#F58CBA", 3 => "#ABD473", 4 => "#FFF569",
        5 => "#FFFFFF", 6 => "#F06A83", 7 => "#599FFF", 8 => "#69CCF0",
        9 => "#B39DDB", 11 => "#FF9C46", _ => "#C6CED8"
    };

    public string ClassIconPath => ClassId is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 11
        ? $"/WotLK.Launcher;component/Assets/Launcher/class-icons/{ClassId}.jpg"
        : string.Empty;

    public bool HasClassIcon => ClassIconPath.Length > 0;
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
    bool CanRemove,
    string StatusMessage = "",
    string Bio = "",
    string CharacterZone = "",
    ImmutableArray<FriendCharacterUiItem> Characters = default,
    bool IsLauncherOnline = false)
{
    internal AvatarDescriptor? AvatarDescriptor { get; init; }

    public string? Presence { get; init; }

    public string PresenceColor => Presence switch
    {
        "away" => "#E9B44C",
        "dnd" => "#EE6873",
        "offline" => "#8995A8",
        _ => IsOnline ? "#48C78E" : "#8995A8"
    };

    public string CharacterSummary => !HasCharacter
        ? string.Empty
        : string.IsNullOrWhiteSpace(CharacterDetails)
            ? CharacterName
            : $"{CharacterName} · {CharacterDetails}";

    public bool IsCharacterActive => IsOnline && Presence != "offline"
        && (AllCharacters.Any(character => character.IsOnline
            && string.Equals(character.Name, CharacterName, StringComparison.OrdinalIgnoreCase))
            || AllCharacters.IsEmpty && IsInGame);

    public string IdentityHint
    {
        get
        {
            bool english = Localization.LauncherLocalization.IsEnglish;
            string account = $"{(english ? "Atlas account" : "Compte Atlas")} : {Username}";
            if (!HasCharacter) return account;
            string label = IsCharacterActive
                ? (english ? "Active character" : "Personnage actif")
                : (english ? "Last character played" : "Dernier personnage joué");
            string character = english
                ? Localization.LauncherLocalization.TranslateFromFrench(CharacterSummary)
                : CharacterSummary;
            return $"{account}\n{label} : {character}";
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsInGame => AllCharacters.Any(character => character.IsOnline) || IsOnline && !IsLauncherOnline;

    public string ProfilePresenceText => Presence is "away" or "dnd" or "offline" ? PresenceText
        : IsInGame ? (Localization.LauncherLocalization.IsEnglish ? "In game" : "En jeu") : PresenceText;

    public bool HasBio => !string.IsNullOrWhiteSpace(Bio);

    public string DisplayStatusMessage => HasStatusMessage ? StatusMessage : "Aucun statut personnel";

    public string DisplayBio => HasBio ? Bio : "Aucune bio renseignée.";

    public bool HasCharacterZone => !string.IsNullOrWhiteSpace(CharacterZone);

    public ImmutableArray<FriendCharacterUiItem> AllCharacters => Characters.IsDefault
        ? ImmutableArray<FriendCharacterUiItem>.Empty
        : Characters;

    public bool HasCharacters => !Characters.IsDefaultOrEmpty;

    public FriendCharacterUiItem? FeaturedCharacter
    {
        get
        {
            FriendCharacterUiItem? character = AllCharacters.FirstOrDefault(item => item.IsOnline)
                ?? AllCharacters.FirstOrDefault(item => string.Equals(item.Name, CharacterName, StringComparison.OrdinalIgnoreCase))
                ?? AllCharacters.FirstOrDefault();
            return character is null ? null : character with { IsFeatured = true };
        }
    }

    public bool HasFeaturedCharacter => FeaturedCharacter is not null;

    public string FeaturedCharacterTitle => FeaturedCharacter?.IsOnline == true
        ? "PERSONNAGE ACTIF"
        : "DERNIER PERSONNAGE JOUÉ";

    public ImmutableArray<FriendCharacterUiItem> OtherCharacters => AllCharacters
        .Where(character => !string.Equals(character.Name, FeaturedCharacter?.Name, StringComparison.OrdinalIgnoreCase))
        .ToImmutableArray();

    public bool HasOtherCharacters => !OtherCharacters.IsDefaultOrEmpty;

    public override string ToString() => Username;
}

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

    public ImmutableArray<FriendUiItem> OnlineFriends => Friends
        .Where(friend => friend.IsOnline)
        .ToImmutableArray();

    public ImmutableArray<FriendUiItem> OfflineFriends => Friends
        .Where(friend => !friend.IsOnline)
        .ToImmutableArray();

    public string FriendsSummary => $"{Friends.Length} ami{(Friends.Length > 1 ? "s" : string.Empty)} · {OnlineCount} en ligne";

    public bool HasFriends => !Friends.IsDefaultOrEmpty;

    public bool HasOnlineFriends => OnlineCount > 0;

    public bool HasOfflineFriends => Friends.Length > OnlineCount;

    public bool HasIncomingRequests => !IncomingRequests.IsDefaultOrEmpty;

    public bool HasOutgoingRequests => !OutgoingRequests.IsDefaultOrEmpty;

    public bool IsLoading => LoadState == FriendsViewLoadState.Loading;
    public bool IsSendingRequest => Operation == FriendsViewOperation.SendingRequest;
    public bool IsWorking => IsLoading || Operation != FriendsViewOperation.None;

    public bool ShowsGlobalEmpty => LoadState == FriendsViewLoadState.Loaded
        && !HasFriends
        && !HasIncomingRequests
        && !HasOutgoingRequests;

    public bool ShowsError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool ShowsNotice => !string.IsNullOrWhiteSpace(NoticeMessage);

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool ShowsFeedback => ShowsError || HasStatusMessage;
}

public sealed class FriendsUiState : BindableUiState
{
    private FriendsViewState _current;
    private bool _isOpen;
    private string _searchText = string.Empty;
    private string _filterText = string.Empty;
    private bool _isOnlineGroupExpanded = true;
    private bool _isOfflineGroupExpanded = true;
    private uint? _selectedFriendAccountId;

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

    // This query only filters existing friends; SearchText remains the add-friend input.
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value ?? string.Empty)) return;
            if (HasFilter)
            {
                IsOnlineGroupExpanded = true;
                IsOfflineGroupExpanded = true;
            }
            RaisePropertyChanged(nameof(HasFilter));
            RaisePropertyChanged(nameof(ShowsFilter));
            RaisePropertyChanged(nameof(FilteredOnlineFriends));
            RaisePropertyChanged(nameof(FilteredOfflineFriends));
            RaisePropertyChanged(nameof(HasFilteredOnlineFriends));
            RaisePropertyChanged(nameof(HasFilteredOfflineFriends));
            RaisePropertyChanged(nameof(OnlineGroupLabel));
            RaisePropertyChanged(nameof(OfflineGroupLabel));
            RaisePropertyChanged(nameof(ShowsFilterEmpty));
            RaisePropertyChanged(nameof(ShowsGlobalEmpty));
        }
    }

    public bool HasFilter => !string.IsNullOrWhiteSpace(FilterText);

    public bool ShowsFilter => Current.HasFriends || HasFilter;

    public ImmutableArray<FriendUiItem> FilteredOnlineFriends => Current.OnlineFriends.Where(MatchesFilter).ToImmutableArray();

    public ImmutableArray<FriendUiItem> FilteredOfflineFriends => Current.OfflineFriends.Where(MatchesFilter).ToImmutableArray();

    public bool HasFilteredOnlineFriends => FilteredOnlineFriends.Length > 0;

    public bool HasFilteredOfflineFriends => FilteredOfflineFriends.Length > 0;

    public bool ShowsFilterEmpty => HasFilter && Current.LoadState == FriendsViewLoadState.Loaded
        && !HasFilteredOnlineFriends && !HasFilteredOfflineFriends;

    public bool ShowsGlobalEmpty => !HasFilter && Current.ShowsGlobalEmpty;

    public bool IsOnlineGroupExpanded
    {
        get => _isOnlineGroupExpanded;
        set => SetProperty(ref _isOnlineGroupExpanded, value);
    }

    public bool IsOfflineGroupExpanded
    {
        get => _isOfflineGroupExpanded;
        set => SetProperty(ref _isOfflineGroupExpanded, value);
    }

    public string OnlineGroupLabel => $"{I18n("En ligne", "Online")} ({FilteredOnlineFriends.Length})";

    public string OfflineGroupLabel => $"{I18n("Hors ligne", "Offline")} ({FilteredOfflineFriends.Length})";

    public string FilterHint => I18n("Rechercher un ami ou un personnage", "Find a friend or character");

    public string ClearFilterLabel => I18n("Effacer la recherche", "Clear search");

    public string NoFilterResultsLabel => I18n("Aucun ami ne correspond à cette recherche.", "No friends match this search.");

    public string MessageActionLabel => I18n("Envoyer un message", "Send a message");

    internal void RefreshLocalizedText()
    {
        foreach (string name in new[] { nameof(OnlineGroupLabel), nameof(OfflineGroupLabel),
            nameof(FilterHint), nameof(ClearFilterLabel), nameof(NoFilterResultsLabel), nameof(MessageActionLabel) })
        {
            RaisePropertyChanged(name);
        }
    }

    private bool MatchesFilter(FriendUiItem friend)
    {
        string query = FilterText.Trim();
        return query.Length == 0
            || friend.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
            || friend.CharacterName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || friend.AllCharacters.Any(character => character.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string I18n(string french, string english) =>
        Localization.LauncherLocalization.IsEnglish ? english : french;

    public FriendUiItem? SelectedFriend => _selectedFriendAccountId is uint accountId
        ? _current.Friends.FirstOrDefault(friend => friend.AccountId == accountId)
        : null;

    public bool IsFriendProfileOpen => SelectedFriend is not null;

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
            _filterText = string.Empty;
            _isOnlineGroupExpanded = true;
            _isOfflineGroupExpanded = true;
            _selectedFriendAccountId = null;
        }
        else if (_selectedFriendAccountId is uint accountId
                 && !state.Friends.Any(friend => friend.AccountId == accountId))
        {
            _selectedFriendAccountId = null;
        }
        RaisePropertyChanged(string.Empty);
    }

    internal void OpenFriendProfile(FriendUiItem friend)
    {
        ArgumentNullException.ThrowIfNull(friend);
        if (_current.Friends.Any(item => item.AccountId == friend.AccountId))
        {
            _selectedFriendAccountId = friend.AccountId;
            RaisePropertyChanged(nameof(SelectedFriend));
            RaisePropertyChanged(nameof(IsFriendProfileOpen));
        }
    }

    internal bool CloseFriendProfile()
    {
        if (_selectedFriendAccountId is null)
        {
            return false;
        }

        _selectedFriendAccountId = null;
        RaisePropertyChanged(nameof(SelectedFriend));
        RaisePropertyChanged(nameof(IsFriendProfileOpen));
        return true;
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
