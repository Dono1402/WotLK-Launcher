namespace WotLK.Launcher.UI.V2.Presentation;

public enum GamePreviewScenario
{
    Ready,
    NotInstalled,
    UpdateAvailable,
    Downloading,
    Installing,
    Verifying,
    Error,
    RealmOffline
}

public enum GameSemanticTone
{
    Neutral,
    Success,
    Accent,
    Warning,
    Error
}

public sealed class GameUiState : BindableUiState
{
    public GamePreviewScenario Scenario { get; init; } = GamePreviewScenario.Ready;

    public GameSemanticTone SemanticTone { get; init; } = GameSemanticTone.Success;

    public string RealmLabel { get; init; } = "ROYAUME ARTHAS";

    public string Title { get; init; } = "Bienvenue en Norfendre";

    public string Subtitle { get; init; } = "Votre aventure vous attend";

    public string RealmStatus { get; init; } = "Royaume en ligne";

    public string ClientStatus { get; init; } = "Client prêt";

    public string PrimaryActionLabel { get; init; } = "Jouer";

    public bool IsPrimaryActionEnabled { get; init; } = true;

    public bool IsOptionsEnabled { get; init; } = true;

    public bool AreLocalActionsEnabled { get; init; } = true;

    public string InstallBadgeText { get; init; } = "À jour";

    public string ClientVersion { get; init; } = "3.4.3.54261";

    public string AvailableClientVersion { get; init; } = string.Empty;

    public string InstallPath { get; init; } = @"C:\Program Files (x86)\WotLK";

    public string Language { get; init; } = "Français";

    public bool IsClientReady { get; init; } = true;

    public double Progress { get; init; } = 100;

    public bool IsProgressIndeterminate { get; init; }

    public string ProgressTitle { get; init; } = string.Empty;

    public string ProgressPercentText { get; init; } = string.Empty;

    public string ProgressPrimaryDetail { get; init; } = string.Empty;

    public string ProgressSecondaryDetail { get; init; } = string.Empty;

    public string ErrorTitle { get; init; } = string.Empty;

    public string ErrorSummary { get; init; } = string.Empty;

    public string NewsCategory { get; init; } = "DERNIÈRE NOTE DE MISE À JOUR";

    public string NewsVersion { get; init; } = "v1.1.0";

    public string NewsTitle { get; init; } = "Atlas Launcher 1.1";

    public string NewsSummary { get; init; } =
        "Une nouvelle expérience de lancement, plus claire et plus directe, pensée pour Arthas.";

    public string NewsDate { get; init; } = "30 août 2026";

    public bool ShowsReadyInstallation => Scenario is GamePreviewScenario.Ready or GamePreviewScenario.RealmOffline;

    public bool ShowsNotInstalled => Scenario == GamePreviewScenario.NotInstalled;

    public bool ShowsUpdateAvailable => Scenario == GamePreviewScenario.UpdateAvailable;

    public bool ShowsProgress => Scenario is GamePreviewScenario.Downloading
        or GamePreviewScenario.Installing
        or GamePreviewScenario.Verifying;

    public bool ShowsError => Scenario == GamePreviewScenario.Error;
}
