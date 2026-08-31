namespace WotLK.Launcher.Game;

internal sealed record GameApplicationRegistration(
    string ConfigPath,
    string UninstallerPath);

internal interface IGameInstallPlatform
{
    void StopRunningGameProcesses(string installRoot);

    GameApplicationRegistration? RegisterGameApplication(
        string installRoot,
        string clientVersion,
        string gameLocale);
}
internal sealed class GameInstallPlatformAdapter : IGameInstallPlatform
{
    public void StopRunningGameProcesses(string installRoot)
    {
        GameInstallServices.StopRunningGameProcesses(installRoot);
    }

    public GameApplicationRegistration? RegisterGameApplication(
        string installRoot,
        string clientVersion,
        string gameLocale)
    {
        if (!GameDirectoryAccess.CanWrite(installRoot))
        {
            return null;
        }

        string configPath = GameInstallServices.EnsureDefaultClientConfig(
            installRoot,
            gameLocale);
        string uninstallerPath = GameInstallServices.RegisterInstalledGame(
            installRoot,
            clientVersion);
        return new GameApplicationRegistration(configPath, uninstallerPath);
    }
}
