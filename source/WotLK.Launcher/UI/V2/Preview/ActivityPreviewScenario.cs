namespace WotLK.Launcher.UI.V2.Preview;

public enum ActivityPreviewScenario
{
    Idle,
    GameDownload,
    GameInstall,
    GameVerify,
    GameRepair,
    Addon,
    AddonBatch,
    AddonRemove,
    SelfUpdate,
    Error,
    History,
    ManyHistory,
    QuickSuccess,
    Cancelling
}

internal static class ActivityPreviewArguments
{
    private const string Prefix = "--preview-activity=";

    internal static bool IsRequested(IEnumerable<string> arguments) =>
        arguments.Any(argument =>
            argument.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));

    internal static ActivityPreviewScenario ResolveScenario(IEnumerable<string> arguments)
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
            "gamedownload" or "gameupdate" => ActivityPreviewScenario.GameDownload,
            "gameinstall" => ActivityPreviewScenario.GameInstall,
            "gameverify" or "verify" => ActivityPreviewScenario.GameVerify,
            "gamerepair" or "repair" => ActivityPreviewScenario.GameRepair,
            "addon" => ActivityPreviewScenario.Addon,
            "addonbatch" or "batch" => ActivityPreviewScenario.AddonBatch,
            "addonremove" or "remove" => ActivityPreviewScenario.AddonRemove,
            "selfupdate" or "launcherupdate" => ActivityPreviewScenario.SelfUpdate,
            "error" => ActivityPreviewScenario.Error,
            "history" => ActivityPreviewScenario.History,
            "manyhistory" or "10" => ActivityPreviewScenario.ManyHistory,
            "quicksuccess" => ActivityPreviewScenario.QuickSuccess,
            "cancelling" or "canceling" => ActivityPreviewScenario.Cancelling,
            _ => ActivityPreviewScenario.Idle
        };
    }
}
