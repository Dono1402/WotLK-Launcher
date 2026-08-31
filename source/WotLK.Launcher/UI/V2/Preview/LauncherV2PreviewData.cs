using WotLK.Launcher.Dashboard;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Commands;

namespace WotLK.Launcher.UI.V2.Preview;

public static class LauncherV2PreviewData
{
    private const string PreviewStatePrefix = "--preview-state=";

    public static GamePreviewScenario ResolveScenario(IEnumerable<string> arguments)
    {
        string? argument = arguments.FirstOrDefault(value =>
            value.StartsWith(PreviewStatePrefix, StringComparison.OrdinalIgnoreCase));

        if (argument is null)
        {
            return GamePreviewScenario.Ready;
        }

        string normalizedScenario = argument[PreviewStatePrefix.Length..]
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalizedScenario switch
        {
            "notinstalled" => GamePreviewScenario.NotInstalled,
            "updateavailable" => GamePreviewScenario.UpdateAvailable,
            "downloading" => GamePreviewScenario.Downloading,
            "installing" => GamePreviewScenario.Installing,
            "verifying" => GamePreviewScenario.Verifying,
            "error" => GamePreviewScenario.Error,
            "realmoffline" => GamePreviewScenario.RealmOffline,
            _ => GamePreviewScenario.Ready
        };
    }

    public static ShellUiState CreateShell(GamePreviewScenario scenario) => new()
    {
        IsNavigationEnabled = scenario is not GamePreviewScenario.Downloading
            and not GamePreviewScenario.Installing
    };

    internal static DashboardUiState CreateDashboard(GamePreviewScenario scenario)
    {
        DashboardRealmState realmState = scenario == GamePreviewScenario.RealmOffline
            ? DashboardRealmState.Offline
            : DashboardRealmState.Online;
        string realmLabel = realmState == DashboardRealmState.Offline ? "Hors ligne" : "En ligne";
        DashboardUiState state = new();
        state.ApplyView(new DashboardViewState(
            realmState,
            realmLabel,
            realmState == DashboardRealmState.Online ? "Arthas en ligne" : realmLabel,
            "Données fictives du mode de prévisualisation.",
            IsLoading: false,
            LatestPatchNoteVersion: "v1.1.0",
            LatestPatchNoteTitle: "Atlas Launcher 1.1",
            LatestPatchNoteSummary:
                "Une nouvelle expérience de lancement, plus claire et plus directe, pensée pour Arthas.",
            LatestPatchNoteMetaText: "30 août 2026",
            HasPatchNote: true,
            IsStale: false,
            CanOpenLatestPatchNote: false));
        state.AttachRefreshCommand(PreviewCommand.Instance);
        return state;
    }

    public static GameUiState CreateGame(GamePreviewScenario scenario)
    {
        GameUiState state = scenario switch
        {
            GamePreviewScenario.NotInstalled => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Warning,
                ClientStatus = "Client non installé",
                PrimaryActionLabel = "Installer",
                InstallBadgeText = "Non installé",
                ClientVersion = string.Empty,
                InstallPath = "Aucun dossier sélectionné",
                IsClientReady = false,
                Progress = 0
            },
            GamePreviewScenario.UpdateAvailable => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Warning,
                ClientStatus = "Mise à jour disponible",
                PrimaryActionLabel = "Mettre à jour",
                InstallBadgeText = "Mise à jour",
                AvailableClientVersion = "3.4.3.54289",
                IsClientReady = false,
                Progress = 0
            },
            GamePreviewScenario.Downloading => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Accent,
                ClientStatus = "Téléchargement en cours",
                PrimaryActionLabel = "Annuler",
                IsOptionsEnabled = false,
                InstallBadgeText = "42 %",
                IsClientReady = false,
                Progress = 42,
                ProgressTitle = "Téléchargement du client",
                ProgressPercentText = "42 %",
                ProgressPrimaryDetail = "2,10 Go / 5,00 Go",
                ProgressSecondaryDetail = "18,4 Mo/s · 2 min restantes"
            },
            GamePreviewScenario.Installing => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Accent,
                ClientStatus = "Installation en cours",
                PrimaryActionLabel = "Annuler",
                IsOptionsEnabled = false,
                InstallBadgeText = "78 %",
                IsClientReady = false,
                Progress = 78,
                ProgressTitle = "Application de la mise à jour",
                ProgressPercentText = "78 %",
                ProgressPrimaryDetail = "2 541 / 3 284 fichiers",
                ProgressSecondaryDetail = "Écriture des fichiers du client"
            },
            GamePreviewScenario.Verifying => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Accent,
                ClientStatus = "Analyse en arrière-plan",
                PrimaryActionLabel = "Lancer le jeu",
                InstallBadgeText = "En arrière-plan",
                IsClientReady = false,
                Progress = 0,
                IsProgressIndeterminate = true,
                ProgressTitle = "Vérification des fichiers",
                ProgressPrimaryDetail = "Analyse des fichiers locaux en cours",
                ProgressSecondaryDetail = "Tu peux jouer pendant l’analyse"
            },
            GamePreviewScenario.Error => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Error,
                ClientStatus = "Une erreur est survenue",
                PrimaryActionLabel = "Réessayer",
                InstallBadgeText = "Erreur",
                IsClientReady = false,
                Progress = 0,
                ErrorTitle = "Mise à jour interrompue",
                ErrorSummary = "Le téléchargement n’a pas pu être terminé. Tu peux réessayer ou ouvrir le diagnostic."
            },
            GamePreviewScenario.RealmOffline => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Success
            },
            _ => new GameUiState
            {
                Scenario = GamePreviewScenario.Ready,
                SemanticTone = GameSemanticTone.Success
            }
        };
        state.AttachPrimaryActionCommand(PreviewCommand.Instance);
        return state;
    }

    public static FriendsUiState CreateFriends()
    {
        FriendsUiState state = new();
        state.Friends.Add(new FriendUiItem(
            "warthoon",
            "Ophntfranck",
            "Mage · Niveau 12",
            "W",
            true,
            "En jeu sur Arthas"));
        state.Friends.Add(new FriendUiItem(
            "lyssara",
            "Lyssara",
            "Prêtresse · Niveau 32",
            "L",
            true,
            "Dans les Tarides"));
        state.Friends.Add(new FriendUiItem(
            "kaelorn",
            "Kaelorn",
            "Paladin · Niveau 28",
            "K",
            false,
            "Hors ligne · il y a 2 h"));
        state.Friends.Add(new FriendUiItem(
            "nerya",
            "Nerya",
            "Druide · Niveau 18",
            "N",
            false,
            "Hors ligne · hier"));
        return state;
    }
}
