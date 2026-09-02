namespace WotLK.Launcher.Server;

internal static class AtlasEnrollmentEligibility
{
    // AzerothCore has no durable Atlas-user marker on account rows. The Atlas
    // profile remains authoritative; this denylist is only a defense in depth
    // against the currently known Playerbots account family.
    private static readonly string[] DeniedUsernamePrefixes = ["RNDBOT"];

    internal static bool IsEligible(string username)
    {
        return !DeniedUsernamePrefixes.Any(prefix =>
            username.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
