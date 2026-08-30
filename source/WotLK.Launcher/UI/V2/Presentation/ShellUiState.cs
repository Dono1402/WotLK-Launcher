namespace WotLK.Launcher.UI.V2.Presentation;

public sealed class ShellUiState : BindableUiState
{
    private AdaptiveLayoutMode _layoutMode = AdaptiveLayoutMode.Wide;

    public string ProductName { get; init; } = "Atlas Launcher";

    public string GameName { get; init; } = "WotLK Classic";

    public string LauncherVersion { get; init; } = "v1.1.0";

    public string Username { get; init; } = "Dono1402";

    public string RealmStatus { get; init; } = "En ligne";

    public AdaptiveLayoutMode LayoutMode
    {
        get => _layoutMode;
        set => SetProperty(ref _layoutMode, value);
    }
}
