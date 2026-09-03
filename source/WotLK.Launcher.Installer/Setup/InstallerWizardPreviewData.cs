namespace WotLK.Launcher.Installer.Setup;

internal static class InstallerWizardPreviewData
{
    internal static InstallerWizardViewState Create(InstallerPreviewScenario scenario)
    {
        InstallerWizardStep step = scenario switch
        {
            InstallerPreviewScenario.Welcome => InstallerWizardStep.Welcome,
            InstallerPreviewScenario.Destination => InstallerWizardStep.Destination,
            InstallerPreviewScenario.Options => InstallerWizardStep.Options,
            InstallerPreviewScenario.Ready => InstallerWizardStep.Ready,
            InstallerPreviewScenario.Installing => InstallerWizardStep.Installing,
            InstallerPreviewScenario.Completed => InstallerWizardStep.Completed,
            InstallerPreviewScenario.InvalidPath => InstallerWizardStep.Destination,
            InstallerPreviewScenario.InsufficientSpace => InstallerWizardStep.Destination,
            InstallerPreviewScenario.ExistingInstallation => InstallerWizardStep.Destination,
            InstallerPreviewScenario.InstallError => InstallerWizardStep.Installing,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        InstallerNoticeKind notice = scenario switch
        {
            InstallerPreviewScenario.InvalidPath => InstallerNoticeKind.InvalidPath,
            InstallerPreviewScenario.InsufficientSpace => InstallerNoticeKind.InsufficientSpace,
            InstallerPreviewScenario.ExistingInstallation => InstallerNoticeKind.ExistingInstallation,
            InstallerPreviewScenario.InstallError => InstallerNoticeKind.InstallError,
            _ => InstallerNoticeKind.None
        };

        string installPath = scenario switch
        {
            InstallerPreviewScenario.InvalidPath => @"Atlas Launcher\Test",
            InstallerPreviewScenario.InsufficientSpace => @"D:\Applications\Atlas Launcher",
            InstallerPreviewScenario.ExistingInstallation => @"C:\Program Files (x86)\WotLK Launcher",
            _ => InstallerWizardViewState.DefaultInstallPath
        };

        return new InstallerWizardViewState(
            scenario,
            step,
            installPath,
            CreateDesktopShortcut: true,
            CreateStartMenuShortcut: true,
            LaunchAfterInstall: true,
            notice,
            ProgressPercent: scenario == InstallerPreviewScenario.InstallError ? 58 : 64,
            ProgressPhase: "Installation des fichiers",
            ProgressDetail: scenario == InstallerPreviewScenario.InstallError
                ? "Rollback effectué · aucun fichier incomplet conservé"
                : "168 Mo sur 263 Mo");
    }
}
