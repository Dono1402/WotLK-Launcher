namespace WotLK.Launcher.Chat;

public sealed record LauncherPresenceUpdateRequest(string? Status, int IdleSeconds);

public sealed record LauncherPresenceStateDto
{
    public uint AccountId { get; init; }
    public string Status { get; init; } = "offline";
    public string ManualStatus { get; init; } = "online";
    public bool IsAutomaticAway { get; init; }
    public long Version { get; init; }
}

public static class LauncherPresenceStatus
{
    public const int AutomaticAwaySeconds = 20 * 60;
    public static bool IsValid(string? status) => status is "online" or "away" or "dnd" or "offline";
    public static string Resolve(string manual, bool connected, bool idle) =>
        manual == "offline" || !connected ? "offline" : manual == "online" && idle ? "away" : manual;
}
