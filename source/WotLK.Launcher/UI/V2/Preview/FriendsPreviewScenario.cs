namespace WotLK.Launcher.UI.V2.Preview;

public enum FriendsPreviewScenario
{
    Empty,
    Populated,
    IncomingRequests,
    OutgoingRequests,
    AddFriend,
    AddFriendError,
    AvatarFallback,
    NetworkError,
    Avatars,
    MixedAvatars,
    AvatarChanged,
    NetworkStale,
    ManyFriends
}

internal static class FriendsPreviewArguments
{
    private const string Argument = "--preview-friends";

    internal static bool IsRequested(IEnumerable<string> arguments)
    {
        return arguments.Any(value =>
            string.Equals(value, Argument, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase));
    }

    internal static FriendsPreviewScenario ResolveScenario(IEnumerable<string> arguments)
    {
        string? value = arguments
            .FirstOrDefault(argument => argument.StartsWith(
                Argument + "=",
                StringComparison.OrdinalIgnoreCase))?
            [(Argument.Length + 1)..];
        return value?.Trim().ToLowerInvariant() switch
        {
            "empty" or "vide" => FriendsPreviewScenario.Empty,
            "incoming" or "incoming-requests" or "recues" =>
                FriendsPreviewScenario.IncomingRequests,
            "outgoing" or "outgoing-requests" or "envoyees" =>
                FriendsPreviewScenario.OutgoingRequests,
            "add" or "add-friend" => FriendsPreviewScenario.AddFriend,
            "add-error" or "add-friend-error" => FriendsPreviewScenario.AddFriendError,
            "fallback" or "avatar-fallback" => FriendsPreviewScenario.AvatarFallback,
            "network" or "network-error" => FriendsPreviewScenario.NetworkError,
            "avatars" => FriendsPreviewScenario.Avatars,
            "mixed" or "mixed-avatars" => FriendsPreviewScenario.MixedAvatars,
            "avatar-changed" or "avatar-change" => FriendsPreviewScenario.AvatarChanged,
            "stale" or "network-stale" => FriendsPreviewScenario.NetworkStale,
            "many" or "many-friends" or "100" => FriendsPreviewScenario.ManyFriends,
            _ => FriendsPreviewScenario.Populated
        };
    }
}
