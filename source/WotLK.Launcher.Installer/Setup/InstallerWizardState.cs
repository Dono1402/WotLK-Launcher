using System.ComponentModel;

namespace WotLK.Launcher.Installer.Setup;

internal enum InstallerWizardStep
{
    Welcome,
    Destination,
    Options,
    Ready,
    Installing,
    Completed
}

internal enum InstallerPreviewScenario
{
    Welcome,
    Destination,
    Options,
    Ready,
    Installing,
    Completed,
    InvalidPath,
    InsufficientSpace,
    ExistingInstallation,
    InstallError
}

internal enum InstallerNoticeKind
{
    None,
    InvalidPath,
    InsufficientSpace,
    ExistingInstallation,
    InstallError
}

internal enum InstallerStepStatus
{
    Pending,
    Active,
    Completed
}

internal sealed record InstallerStepItem(
    int Number,
    string Label,
    InstallerStepStatus Status);

internal sealed record InstallerPhaseItem(
    string Label,
    InstallerStepStatus Status);

internal sealed record InstallerWizardViewState(
    InstallerPreviewScenario Scenario,
    InstallerWizardStep Step,
    string InstallPath,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcut,
    bool LaunchAfterInstall,
    InstallerNoticeKind Notice,
    double ProgressPercent,
    string ProgressPhase,
    string ProgressDetail,
    long RequiredBytes = 0,
    long? AvailableBytes = null,
    string? NoticeTitleOverride = null,
    string? NoticeMessageOverride = null,
    bool? CanPrimaryActionOverride = null,
    bool? CanCancelOverride = null,
    bool? CanCloseWindowOverride = null,
    bool CanOpenInstalledApps = false,
    InstallerWorkPhase ActiveWorkPhase = InstallerWorkPhase.InstallingFiles)
{
    internal const string ProductName = InstallerProduct.Name;
    internal const string ProductVersion = InstallerProduct.Version;
    internal static string DefaultInstallPath => InstallerProduct.GetDefaultInstallPath();

    public string HeaderEyebrow => Notice switch
    {
        InstallerNoticeKind.ExistingInstallation => "INSTALLATION BLOQUÉE",
        InstallerNoticeKind.InstallError => "ROLLBACK TERMINÉ",
        _ => $"ÉTAPE {(int)Step + 1} SUR 6"
    };

    public string HeaderTitle => Notice switch
    {
        InstallerNoticeKind.ExistingInstallation => "Ancienne installation détectée",
        InstallerNoticeKind.InstallError => "Installation interrompue",
        _ => Step switch
        {
            InstallerWizardStep.Welcome => "Bienvenue dans l’assistant d’installation",
            InstallerWizardStep.Destination => "Choisir le dossier d’installation",
            InstallerWizardStep.Options => "Options supplémentaires",
            InstallerWizardStep.Ready => "Prêt à installer",
            InstallerWizardStep.Installing => "Installation d’Atlas Launcher",
            _ => "Atlas Launcher est installé"
        }
    };

    public string HeaderSubtitle => Notice switch
    {
        InstallerNoticeKind.ExistingInstallation =>
            "Désinstalle l’ancien launcher avant de poursuivre cette nouvelle installation.",
        InstallerNoticeKind.InstallError =>
            "Aucun changement incomplet n’a été conservé sur cet ordinateur.",
        _ => Step switch
        {
            InstallerWizardStep.Welcome =>
                "Cet assistant va installer Atlas Launcher 1.1.2 sur cet ordinateur.",
            InstallerWizardStep.Destination =>
                "Sélectionne l’emplacement réservé au launcher. Le client WoW reste séparé.",
            InstallerWizardStep.Options =>
                "Choisis les raccourcis à créer pour accéder rapidement au launcher.",
            InstallerWizardStep.Ready =>
                "Vérifie les options ci-dessous avant de démarrer l’installation.",
            InstallerWizardStep.Installing =>
                "Les fichiers sont préparés puis enregistrés dans Windows.",
            _ => "L’installation s’est terminée avec succès."
        }
    };

    public string NoticeTitle => NoticeTitleOverride ?? Notice switch
    {
        InstallerNoticeKind.InvalidPath => "Ce dossier ne peut pas être utilisé",
        InstallerNoticeKind.InsufficientSpace => "Espace disque insuffisant",
        InstallerNoticeKind.ExistingInstallation => "Atlas Launcher doit être désinstallé",
        InstallerNoticeKind.InstallError => "Impossible de terminer l’installation",
        _ => string.Empty
    };

    public string NoticeMessage => NoticeMessageOverride ?? Notice switch
    {
        InstallerNoticeKind.InvalidPath =>
            "Choisis un dossier local absolu qui n’est ni une racine de disque, ni Windows, ni le client WoW.",
        InstallerNoticeKind.InsufficientSpace =>
            "Libère au moins 243 Mo supplémentaires ou sélectionne un autre disque.",
        InstallerNoticeKind.ExistingInstallation =>
            "Une installation WotLK Launcher existante est enregistrée dans Windows. Elle ne sera ni reprise ni supprimée automatiquement.",
        InstallerNoticeKind.InstallError =>
            "L’écriture des fichiers a échoué. Les fichiers temporaires, raccourcis et entrées Windows de cette tentative ont été retirés.",
        _ => string.Empty
    };

    public string RequiredSpaceText => RequiredBytes > 0
        ? InstallerPathValidator.FormatBytes(RequiredBytes) + " requis"
        : "285 Mo requis";

    public string AvailableSpaceText => AvailableBytes.HasValue
        ? InstallerPathValidator.FormatBytes(AvailableBytes.Value) + " disponibles"
        : Notice == InstallerNoticeKind.InsufficientSpace
            ? "42 Mo disponibles"
            : "186 Go disponibles";

    public string DesktopShortcutSummary => CreateDesktopShortcut ? "Oui" : "Non";

    public string StartMenuShortcutSummary => CreateStartMenuShortcut ? "Oui" : "Non";

    public string PrimaryActionLabel => Notice switch
    {
        InstallerNoticeKind.ExistingInstallation => "Réessayer",
        InstallerNoticeKind.InstallError => "Réessayer",
        _ => Step switch
        {
            InstallerWizardStep.Ready => "Installer",
            InstallerWizardStep.Installing => "Installation…",
            InstallerWizardStep.Completed => "Terminer",
            _ => "Suivant"
        }
    };

    public bool CanPrimaryAction => CanPrimaryActionOverride ?? (
        Notice == InstallerNoticeKind.InstallError
        || (Notice is not (
                InstallerNoticeKind.InvalidPath or InstallerNoticeKind.InsufficientSpace)
            && Step != InstallerWizardStep.Installing));

    public bool CanGoBack => Notice is not (
            InstallerNoticeKind.ExistingInstallation or InstallerNoticeKind.InstallError)
        && Step is (
            InstallerWizardStep.Destination
            or InstallerWizardStep.Options
            or InstallerWizardStep.Ready);

    public bool CanCancel => CanCancelOverride ?? (
        Notice == InstallerNoticeKind.InstallError
        || (Step != InstallerWizardStep.Installing
            && Step != InstallerWizardStep.Completed));

    public bool CanCloseWindow => CanCloseWindowOverride ?? (
        Notice == InstallerNoticeKind.InstallError
        || Step != InstallerWizardStep.Installing);

    public bool ShowBack => Step != InstallerWizardStep.Welcome
        && Step != InstallerWizardStep.Installing
        && Step != InstallerWizardStep.Completed
        && Notice != InstallerNoticeKind.ExistingInstallation
        && Notice != InstallerNoticeKind.InstallError;

    public bool ShowCancel => Step != InstallerWizardStep.Completed;

    public bool ShowWelcome => Step == InstallerWizardStep.Welcome;
    public bool ShowDestination => Step == InstallerWizardStep.Destination
        && Notice != InstallerNoticeKind.ExistingInstallation;
    public bool ShowOptions => Step == InstallerWizardStep.Options;
    public bool ShowReady => Step == InstallerWizardStep.Ready;
    public bool ShowInstalling => Step == InstallerWizardStep.Installing
        && Notice != InstallerNoticeKind.InstallError;
    public bool ShowCompleted => Step == InstallerWizardStep.Completed;
    public bool ShowExistingInstallation => Notice == InstallerNoticeKind.ExistingInstallation;
    public bool ShowInstallError => Notice == InstallerNoticeKind.InstallError;
    public bool ShowDestinationNotice => Notice is InstallerNoticeKind.InvalidPath
        or InstallerNoticeKind.InsufficientSpace;

    public IReadOnlyList<InstallerStepItem> Steps => CreateSteps(Step);

    public IReadOnlyList<InstallerPhaseItem> Phases => CreatePhases(ActiveWorkPhase);

    private static IReadOnlyList<InstallerStepItem> CreateSteps(InstallerWizardStep activeStep)
    {
        string[] labels =
        [
            "Bienvenue",
            "Dossier d’installation",
            "Options supplémentaires",
            "Prêt à installer",
            "Installation",
            "Terminé"
        ];

        int active = (int)activeStep;
        return labels.Select((label, index) => new InstallerStepItem(
            index + 1,
            label,
            index < active
                ? InstallerStepStatus.Completed
                : index == active
                    ? InstallerStepStatus.Active
                    : InstallerStepStatus.Pending)).ToArray();
    }

    private static IReadOnlyList<InstallerPhaseItem> CreatePhases(InstallerWorkPhase activePhase)
    {
        string[] labels =
        [
            "Préparation",
            "Création du dossier",
            "Installation des fichiers",
            "Création des raccourcis",
            "Enregistrement dans Windows",
            "Finalisation"
        ];
        int active = (int)activePhase;
        return labels.Select((label, index) => new InstallerPhaseItem(
            label,
            index < active
                ? InstallerStepStatus.Completed
                : index == active
                    ? InstallerStepStatus.Active
                    : InstallerStepStatus.Pending)).ToArray();
    }
}

internal sealed class InstallerWizardUiState : INotifyPropertyChanged
{
    private InstallerWizardViewState _current;

    internal InstallerWizardUiState(InstallerWizardViewState initial, bool isPreview = true)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        IsPreview = isPreview;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool IsPreview { get; }

    public InstallerWizardViewState Current
    {
        get => _current;
        private set
        {
            if (ReferenceEquals(_current, value) || _current == value)
            {
                return;
            }

            _current = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        }
    }

    internal void Replace(InstallerWizardViewState state) => Current = state;

    internal void MoveNext()
    {
        DemandPreview();
        if (!Current.CanPrimaryAction)
        {
            return;
        }

        if (Current.Notice == InstallerNoticeKind.ExistingInstallation)
        {
            Current = InstallerWizardPreviewData.Create(InstallerPreviewScenario.Destination);
            return;
        }

        if (Current.Notice == InstallerNoticeKind.InstallError)
        {
            Current = InstallerWizardPreviewData.Create(InstallerPreviewScenario.Ready) with
            {
                InstallPath = Current.InstallPath,
                CreateDesktopShortcut = Current.CreateDesktopShortcut,
                CreateStartMenuShortcut = Current.CreateStartMenuShortcut
            };
            return;
        }

        InstallerPreviewScenario next = Current.Step switch
        {
            InstallerWizardStep.Welcome => InstallerPreviewScenario.Destination,
            InstallerWizardStep.Destination => InstallerPreviewScenario.Options,
            InstallerWizardStep.Options => InstallerPreviewScenario.Ready,
            InstallerWizardStep.Ready => InstallerPreviewScenario.Installing,
            _ => Current.Scenario
        };

        if (next != Current.Scenario)
        {
            Current = PreserveSelections(InstallerWizardPreviewData.Create(next));
        }
    }

    internal void MoveBack()
    {
        DemandPreview();
        if (!Current.CanGoBack)
        {
            return;
        }

        InstallerPreviewScenario previous = Current.Step switch
        {
            InstallerWizardStep.Destination => InstallerPreviewScenario.Welcome,
            InstallerWizardStep.Options => InstallerPreviewScenario.Destination,
            InstallerWizardStep.Ready => InstallerPreviewScenario.Options,
            _ => Current.Scenario
        };
        Current = PreserveSelections(InstallerWizardPreviewData.Create(previous));
    }

    internal void ToggleDesktopShortcut()
    {
        DemandPreview();
        Current = Current with { CreateDesktopShortcut = !Current.CreateDesktopShortcut };
    }

    internal void ToggleStartMenuShortcut()
    {
        DemandPreview();
        Current = Current with { CreateStartMenuShortcut = !Current.CreateStartMenuShortcut };
    }

    internal void ToggleLaunchAfterInstall()
    {
        DemandPreview();
        Current = Current with { LaunchAfterInstall = !Current.LaunchAfterInstall };
    }

    internal void SelectPreviewFolder()
    {
        DemandPreview();
        Current = Current with
        {
            InstallPath = @"D:\Applications\Atlas Launcher",
            Notice = InstallerNoticeKind.None
        };
    }

    internal void SetPreviewPath(string path)
    {
        DemandPreview();
        Current = Current with { InstallPath = path };
    }

    private InstallerWizardViewState PreserveSelections(InstallerWizardViewState candidate) =>
        candidate with
        {
            InstallPath = Current.InstallPath,
            CreateDesktopShortcut = Current.CreateDesktopShortcut,
            CreateStartMenuShortcut = Current.CreateStartMenuShortcut,
            LaunchAfterInstall = Current.LaunchAfterInstall
        };

    private void DemandPreview()
    {
        if (!IsPreview)
        {
            throw new InvalidOperationException("Une transition fictive a été appelée en mode réel.");
        }
    }
}
