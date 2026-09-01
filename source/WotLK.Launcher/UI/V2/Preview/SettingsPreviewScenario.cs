namespace WotLK.Launcher.UI.V2.Preview;

public enum SettingsPreviewScenario
{
    General,
    Game,
    Updates,
    Notifications,
    Appearance,
    Diagnostic,
    Dirty,
    Saving,
    Saved,
    SaveError
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
        string? value = arguments
            .FirstOrDefault(argument => argument.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase))?
            [(Argument.Length + 1)..];
        return value?.ToLowerInvariant() switch
        {
            "game" or "jeu" => SettingsPreviewScenario.Game,
            "updates" or "mises-a-jour" => SettingsPreviewScenario.Updates,
            "notifications" => SettingsPreviewScenario.Notifications,
            "appearance" or "apparence" => SettingsPreviewScenario.Appearance,
            "diagnostic" => SettingsPreviewScenario.Diagnostic,
            "dirty" or "modified" => SettingsPreviewScenario.Dirty,
            "saving" => SettingsPreviewScenario.Saving,
            "saved" => SettingsPreviewScenario.Saved,
            "save-error" or "error" => SettingsPreviewScenario.SaveError,
            _ => SettingsPreviewScenario.General
        };
    }
}
