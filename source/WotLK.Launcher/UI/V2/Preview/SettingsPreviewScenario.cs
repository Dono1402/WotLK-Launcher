namespace WotLK.Launcher.UI.V2.Preview;

public enum SettingsPreviewScenario
{
    Default
}

internal static class SettingsPreviewArguments
{
    private const string Argument = "--preview-settings";

    internal static bool IsRequested(IEnumerable<string> arguments)
    {
        return arguments.Any(value =>
            string.Equals(value, Argument, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase));
    }

    internal static SettingsPreviewScenario ResolveScenario(IEnumerable<string> arguments)
    {
        return SettingsPreviewScenario.Default;
    }
}
