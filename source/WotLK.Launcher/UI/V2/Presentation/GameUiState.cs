namespace WotLK.Launcher.UI.V2.Presentation;

public sealed class GameUiState : BindableUiState
{
    public string RealmLabel { get; init; } = "ROYAUME ARTHAS";

    public string Title { get; init; } = "Bienvenue en Norfendre";

    public string Subtitle { get; init; } = "Votre aventure vous attend";

    public string RealmStatus { get; init; } = "Royaume en ligne";

    public string ClientStatus { get; init; } = "Client prêt";

    public string ClientVersion { get; init; } = "3.4.3.54261";

    public string InstallPath { get; init; } = @"C:\Program Files (x86)\WotLK";

    public string Language { get; init; } = "Français";

    public bool IsClientReady { get; init; } = true;

    public double Progress { get; init; } = 100;

    public string NewsCategory { get; init; } = "DERNIÈRE NOTE DE MISE À JOUR";

    public string NewsVersion { get; init; } = "v1.1.0";

    public string NewsTitle { get; init; } = "Atlas Launcher 1.1";

    public string NewsSummary { get; init; } =
        "Une nouvelle expérience de lancement, plus claire et plus directe, pensée pour Arthas.";

    public string NewsDate { get; init; } = "30 août 2026";
}
