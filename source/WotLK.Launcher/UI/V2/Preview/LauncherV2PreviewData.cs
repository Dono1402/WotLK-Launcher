using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Preview;

public static class LauncherV2PreviewData
{
    public static ShellUiState CreateShell() => new();

    public static GameUiState CreateGame() => new();

    public static FriendsUiState CreateFriends()
    {
        FriendsUiState state = new();
        state.Friends.Add(new FriendUiItem(
            "warthoon",
            "Ophntfranck",
            "Mage · Niveau 12",
            "W",
            true,
            "En jeu sur Arthas"));
        state.Friends.Add(new FriendUiItem(
            "lyssara",
            "Lyssara",
            "Prêtresse · Niveau 32",
            "L",
            true,
            "Dans les Tarides"));
        state.Friends.Add(new FriendUiItem(
            "kaelorn",
            "Kaelorn",
            "Paladin · Niveau 28",
            "K",
            false,
            "Hors ligne · il y a 2 h"));
        state.Friends.Add(new FriendUiItem(
            "nerya",
            "Nerya",
            "Druide · Niveau 18",
            "N",
            false,
            "Hors ligne · hier"));
        return state;
    }
}
