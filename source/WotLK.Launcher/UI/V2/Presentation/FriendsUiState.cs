using System.Collections.ObjectModel;

namespace WotLK.Launcher.UI.V2.Presentation;

public sealed class FriendsUiState : BindableUiState
{
    private bool _isOpen;

    public ObservableCollection<FriendUiItem> Friends { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public int OnlineCount => Friends.Count(friend => friend.IsOnline);
}

public sealed record FriendUiItem(
    string Username,
    string CharacterName,
    string CharacterDetails,
    string Initial,
    bool IsOnline,
    string PresenceText);
