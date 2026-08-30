using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;

namespace WotLK.Launcher.UI.V2.Presentation;

internal static class LauncherV2RuntimePresentation
{
    internal static ShellUiState CreateShell(LauncherRuntime runtime)
    {
        return new ShellUiState
        {
            LauncherVersion = runtime.LauncherVersion,
            Username = "Compte",
            IsAuthenticated = false,
            RealmStatus = "Non vérifié",
            RealmState = RealmServiceState.Unknown,
            IsGameNavigationEnabled = true,
            IsNavigationEnabled = false
        };
    }

    internal static GameUiState CreateGame(GameClientLocalState localClient)
    {
        string installedVersion = string.IsNullOrWhiteSpace(localClient.InstalledVersion)
            ? "Inconnue"
            : localClient.InstalledVersion;
        string language = localClient.GameLocale == "enUS" ? "English" : "Français";

        if (!localClient.IsPlayable)
        {
            return new GameUiState
            {
                Scenario = GamePreviewScenario.NotInstalled,
                SemanticTone = GameSemanticTone.Warning,
                RealmStatus = "Royaume non vérifié",
                ClientStatus = "Client non installé",
                PrimaryActionLabel = "Installer",
                IsPrimaryActionEnabled = false,
                IsOptionsEnabled = false,
                IsVerifyEnabled = false,
                IsRetryEnabled = false,
                InstallBadgeText = "Non installé",
                ClientVersion = installedVersion,
                InstallPath = localClient.InstallPath,
                Language = language,
                IsClientReady = false,
                Progress = 0
            };
        }

        return new GameUiState
        {
            Scenario = GamePreviewScenario.Ready,
            SemanticTone = GameSemanticTone.Neutral,
            RealmStatus = "Royaume non vérifié",
            ClientStatus = "Client prêt",
            PrimaryActionLabel = "Jouer",
            IsPrimaryActionEnabled = false,
            IsOptionsEnabled = false,
            IsVerifyEnabled = false,
            IsRetryEnabled = false,
            InstallBadgeText = "Non vérifié",
            ClientVersion = installedVersion,
            InstallPath = localClient.InstallPath,
            Language = language,
            IsClientReady = true,
            Progress = 0
        };
    }

    internal static FriendsUiState CreateFriends()
    {
        return new FriendsUiState();
    }

    internal static GameCommands ConnectLocalActions(
        GameUiState gameState,
        ILauncherLocalActions localActions)
    {
        GameCommands commands = new(localActions, result => ApplyLocalActionResult(gameState, result));
        gameState.AttachLocalCommands(commands.OpenGameFolder, commands.OpenDiagnostic);
        return commands;
    }

    internal static void ApplySession(
        ShellUiState shellState,
        LauncherSessionRestoreResult restoreResult)
    {
        if (restoreResult.Status == LauncherSessionRestoreStatus.Restored
            && restoreResult.Session is not null)
        {
            shellState.ApplyAuthenticatedUser(restoreResult.Session.Profile.Username);
        }
    }

    private static void ApplyLocalActionResult(
        GameUiState gameState,
        LauncherLocalActionResult result)
    {
        if (result.Status == LauncherLocalActionStatus.Succeeded)
        {
            gameState.ClearNotification();
            return;
        }

        if (result.Status is LauncherLocalActionStatus.Busy
            or LauncherLocalActionStatus.ShuttingDown
            || string.IsNullOrWhiteSpace(result.UserMessage))
        {
            return;
        }

        GameSemanticTone tone = result.Status == LauncherLocalActionStatus.Failed
            ? GameSemanticTone.Error
            : GameSemanticTone.Warning;
        gameState.ShowNotification(result.UserMessage, tone);
    }
}
