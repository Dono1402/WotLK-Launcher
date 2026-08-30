namespace WotLK.Launcher.Game;

internal enum GameAction
{
    Install,
    Update,
    Play
}

internal enum GameUpdateKnowledge
{
    Unknown,
    Checking,
    Known,
    Unavailable
}

internal sealed record GameClientLocalState(
    string InstallPath,
    string GameLocale,
    bool IsPlayable,
    string? InstalledVersion,
    GameAction Action,
    GameUpdateKnowledge UpdateKnowledge);
