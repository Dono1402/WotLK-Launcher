namespace WotLK.Launcher.Game;

internal sealed record GameApplicationRegistration(
    string ConfigPath,
    string UninstallerPath);

internal interface IGameInstallPlatform
{
    void PrepareInstallRoot(string installRoot);

    IGameInstallRootLease AcquireInstallRootLease(
        string installRoot,
        GameInstallRootLeaseMode mode);

    void StopRunningGameProcesses(string installRoot);

    GameApplicationRegistration? RegisterGameApplication(
        string installRoot,
        string clientVersion,
        string gameLocale,
        IGameInstallRootLease? rootLease = null);
}
internal sealed class GameInstallPlatformAdapter : IGameInstallPlatform
{
    public void PrepareInstallRoot(string installRoot)
    {
        _ = GameInstallServices.PrepareGameInstallRoot(installRoot);
    }

    public IGameInstallRootLease AcquireInstallRootLease(
        string installRoot,
        GameInstallRootLeaseMode mode)
        => GameInstallServices.AcquireGameInstallRootLease(installRoot, mode);

    public void StopRunningGameProcesses(string installRoot)
    {
        GameInstallServices.StopRunningGameProcesses(installRoot);
    }

    public GameApplicationRegistration? RegisterGameApplication(
        string installRoot,
        string clientVersion,
        string gameLocale,
        IGameInstallRootLease? rootLease = null)
    {
        bool ownsLease = rootLease is null;
        rootLease ??= GameInstallServices.AcquireGameInstallRootLease(
            installRoot,
            GameInstallRootLeaseMode.ExistingClient);
        try
        {
            rootLease.Revalidate();
            if (!GameDirectoryAccess.CanWrite(installRoot))
            {
                return null;
            }

            string configPath = GameInstallServices.EnsureDefaultClientConfig(
                installRoot,
                gameLocale,
                rootLease);
            string uninstallerPath = GameInstallServices.RegisterInstalledGame(
                installRoot,
                clientVersion,
                rootLease);
            return new GameApplicationRegistration(configPath, uninstallerPath);
        }
        finally
        {
            if (ownsLease)
            {
                rootLease.Dispose();
            }
        }
    }
}
