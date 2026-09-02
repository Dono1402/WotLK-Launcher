namespace WotLK.Launcher.UI.V2.Preview;

public enum AddonsPreviewScenario
{
    Default,
    Updates,
    Detail,
    Installing,
    Empty,
    Error,
    Many,
    GameRunning
}

public static class AddonsPreviewArguments
{
    private const string Prefix = "--preview-addons=";

    public static bool IsRequested(IEnumerable<string> arguments) =>
        arguments.Any(argument =>
            argument.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));

    public static AddonsPreviewScenario ResolveScenario(IEnumerable<string> arguments)
    {
        string? argument = arguments.FirstOrDefault(value =>
            value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
        string normalized = argument is null
            ? string.Empty
            : argument[Prefix.Length..]
                .Trim()
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
        return normalized switch
        {
            "updates" => AddonsPreviewScenario.Updates,
            "detail" => AddonsPreviewScenario.Detail,
            "installing" => AddonsPreviewScenario.Installing,
            "empty" => AddonsPreviewScenario.Empty,
            "error" => AddonsPreviewScenario.Error,
            "many" or "50" => AddonsPreviewScenario.Many,
            "gamerunning" or "reload" => AddonsPreviewScenario.GameRunning,
            _ => AddonsPreviewScenario.Default
        };
    }
}
