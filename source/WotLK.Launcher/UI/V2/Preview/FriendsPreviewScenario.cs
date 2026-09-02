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
    NetworkError
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
            _ => FriendsPreviewScenario.Populated
        };
    }
}
