namespace WotLK.Launcher.Installer.Setup;

internal sealed class InstallerWizardRuntime : IDisposable
{
    private readonly InstallerEnvironment _environment;
    private readonly InstallerEngine _engine;
    private readonly IInstallerSystemActions _systemActions;
    private readonly InstallerLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private InstallerInstallResult? _result;
    private bool _disposed;
    private int _installationStarted;
    private int _systemEffectCount;

    private InstallerWizardRuntime(
        InstallerEnvironment environment,
        InstallerEngine engine,
        IInstallerSystemActions systemActions,
        InstallerLog log,
        InstallerWizardUiState state)
    {
        _environment = environment;
        _engine = engine;
        _systemActions = systemActions;
        _log = log;
        State = state;
    }

    internal InstallerWizardUiState State { get; }

    internal int SystemEffectCount => Volatile.Read(ref _systemEffectCount);

    internal static InstallerWizardRuntime CreateProduction()
    {
        InstallerEnvironment environment = InstallerEnvironment.CreateProduction();
        InstallerLog log = new(environment.LogPath);
        EmbeddedInstallerPayloadSource payload = new();
        WindowsInstallerRegistry registry = new(log);
        WindowsInstallerShortcutService shortcuts = new();
        WindowsInstallerProcessInspector processes = new(log);
        WindowsInstallerSystemActions actions = new();
        InstallerPathValidator validator = new(environment);
        InstallerEngine engine = new(
            environment,
            payload,
            validator,
            registry,
            shortcuts,
            processes,
            log);
        InstallerPathValidationResult initialValidation = engine.ValidatePath(
            environment.DefaultInstallPath);
        InstallerWizardViewState initial = CreateStepState(
            InstallerWizardStep.Welcome,
            environment.DefaultInstallPath,
            createDesktopShortcut: true,
            createStartMenuShortcut: true,
            launchAfterInstall: true,
            engine.RequiredBytes,
            initialValidation.AvailableBytes);
        InstallerWizardUiState state = new(initial, isPreview: false);
        return new InstallerWizardRuntime(environment, engine, actions, log, state);
    }

    internal void Initialize()
    {
        ThrowIfDisposed();
        _log.Info(
            $"Assistant Atlas Launcher {InstallerProduct.Version} démarré; "
            + $"x64={Environment.Is64BitProcess}; élevé={WindowsInstallerSystemActions.IsCurrentProcessElevated()}.");
        ExistingInstallation existing = _engine.DetectExistingInstallation();
        if (existing.Status != ExistingInstallationStatus.None)
        {
            ShowExistingInstallation(existing);
        }
    }

    internal async Task MoveNextAsync()
    {
        ThrowIfDisposed();
        InstallerWizardViewState current = State.Current;
        if (!current.CanPrimaryAction)
        {
            return;
        }

        if (current.Notice == InstallerNoticeKind.ExistingInstallation)
        {
            ExistingInstallation existing = _engine.DetectExistingInstallation();
            if (existing.Status == ExistingInstallationStatus.None)
            {
                ApplyDestinationState(current.InstallPath);
            }
            else
            {
                ShowExistingInstallation(existing);
            }

            return;
        }

        if (current.Notice == InstallerNoticeKind.InstallError)
        {
            State.Replace(CreateStepState(
                InstallerWizardStep.Ready,
                current.InstallPath,
                current.CreateDesktopShortcut,
                current.CreateStartMenuShortcut,
                current.LaunchAfterInstall,
                current.RequiredBytes,
                current.AvailableBytes));
            return;
        }

        switch (current.Step)
        {
            case InstallerWizardStep.Welcome:
                ApplyDestinationState(current.InstallPath);
                break;
            case InstallerWizardStep.Destination:
                InstallerPathValidationResult validation = _engine.ValidatePath(current.InstallPath);
                if (!validation.IsValid)
                {
                    ApplyPathValidation(validation, current.InstallPath);
                    break;
                }

                State.Replace(CreateStepState(
                    InstallerWizardStep.Options,
                    validation.FullPath!,
                    current.CreateDesktopShortcut,
                    current.CreateStartMenuShortcut,
                    current.LaunchAfterInstall,
                    current.RequiredBytes,
                    validation.AvailableBytes));
                break;
            case InstallerWizardStep.Options:
                State.Replace(CreateStepState(
                    InstallerWizardStep.Ready,
                    current.InstallPath,
                    current.CreateDesktopShortcut,
                    current.CreateStartMenuShortcut,
                    current.LaunchAfterInstall,
                    current.RequiredBytes,
                    current.AvailableBytes));
                break;
            case InstallerWizardStep.Ready:
                await InstallAsync();
                break;
        }
    }

    internal void MoveBack()
    {
        ThrowIfDisposed();
        InstallerWizardViewState current = State.Current;
        if (!current.CanGoBack)
        {
            return;
        }

        InstallerWizardStep previous = current.Step switch
        {
            InstallerWizardStep.Destination => InstallerWizardStep.Welcome,
            InstallerWizardStep.Options => InstallerWizardStep.Destination,
            InstallerWizardStep.Ready => InstallerWizardStep.Options,
            _ => current.Step
        };
        State.Replace(CreateStepState(
            previous,
            current.InstallPath,
            current.CreateDesktopShortcut,
            current.CreateStartMenuShortcut,
            current.LaunchAfterInstall,
            current.RequiredBytes,
            current.AvailableBytes));
    }

    internal void SetInstallPath(string path) => ApplyDestinationState(path);

    internal void ToggleDesktopShortcut()
    {
        ThrowIfDisposed();
        State.Replace(State.Current with
        {
            CreateDesktopShortcut = !State.Current.CreateDesktopShortcut
        });
    }

    internal void ToggleStartMenuShortcut()
    {
        ThrowIfDisposed();
        State.Replace(State.Current with
        {
            CreateStartMenuShortcut = !State.Current.CreateStartMenuShortcut
        });
    }

    internal void ToggleLaunchAfterInstall()
    {
        ThrowIfDisposed();
        State.Replace(State.Current with
        {
            LaunchAfterInstall = !State.Current.LaunchAfterInstall
        });
    }

    internal void OpenInstalledApps()
    {
        ThrowIfDisposed();
        if (!State.Current.CanOpenInstalledApps)
        {
            return;
        }

        try
        {
            _systemActions.OpenInstalledApps();
            Interlocked.Increment(ref _systemEffectCount);
        }
        catch (Exception exception)
        {
            _log.Error("Impossible d'ouvrir Applications installées", exception);
            ShowExistingInstallation(new ExistingInstallation(
                ExistingInstallationStatus.StaleRegistration,
                State.Current.InstallPath,
                "Windows n'a pas pu ouvrir Applications installées. Ouvre cette page depuis les Paramètres, puis réessaie.",
                RegistrySubKey: null));
        }
    }

    internal void FinishAndLaunchIfRequested()
    {
        ThrowIfDisposed();
        if (_result is null || !State.Current.LaunchAfterInstall)
        {
            _log.Info("Assistant fermé sans lancement automatique.");
            _log.Flush();
            return;
        }

        _log.Info("Installation finalisée; lancement non élevé demandé via Explorer.");
        _log.Flush();
        try
        {
            _systemActions.LaunchUnelevated(_result.LauncherPath, _result.InstallPath);
            Interlocked.Increment(ref _systemEffectCount);
        }
        catch (Exception exception)
        {
            _log.Error("Le launcher n'a pas pu être démarré automatiquement", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _log.Dispose();
    }

    private async Task InstallAsync()
    {
        if (Interlocked.CompareExchange(ref _installationStarted, 1, 0) != 0)
        {
            return;
        }

        InstallerWizardViewState current = State.Current;
        State.Replace(current with
        {
            Scenario = InstallerPreviewScenario.Installing,
            Step = InstallerWizardStep.Installing,
            Notice = InstallerNoticeKind.None,
            ProgressPercent = 0,
            ProgressPhase = "Préparation",
            ProgressDetail = "Validation de l'installation",
            CanPrimaryActionOverride = false,
            CanCancelOverride = false,
            CanCloseWindowOverride = false,
            ActiveWorkPhase = InstallerWorkPhase.Preparation
        });

        Progress<InstallerProgress> progress = new(value =>
        {
            if (_disposed)
            {
                return;
            }

            State.Replace(State.Current with
            {
                ProgressPercent = value.Percent,
                ProgressPhase = GetPhaseLabel(value.Phase),
                ProgressDetail = value.Detail,
                ActiveWorkPhase = value.Phase
            });
        });

        try
        {
            Interlocked.Increment(ref _systemEffectCount);
            _result = await _engine.InstallAsync(
                new InstallerRequest(
                    current.InstallPath,
                    current.CreateDesktopShortcut,
                    current.CreateStartMenuShortcut),
                progress,
                _lifetime.Token);
            State.Replace(CreateStepState(
                InstallerWizardStep.Completed,
                _result.InstallPath,
                _result.DesktopShortcutCreated,
                _result.StartMenuShortcutCreated,
                current.LaunchAfterInstall,
                current.RequiredBytes,
                current.AvailableBytes));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (InstallerOperationException exception)
        {
            string message = exception.InnerException is InvalidOperationException
                or UnauthorizedAccessException
                ? exception.InnerException.Message
                : exception.Message;
            State.Replace(current with
            {
                Scenario = InstallerPreviewScenario.InstallError,
                Step = InstallerWizardStep.Installing,
                Notice = InstallerNoticeKind.InstallError,
                NoticeMessageOverride = message,
                CanPrimaryActionOverride = true,
                CanCancelOverride = true,
                CanCloseWindowOverride = true
            });
        }
        finally
        {
            Volatile.Write(ref _installationStarted, 0);
        }
    }

    private void ApplyDestinationState(string path)
    {
        ThrowIfDisposed();
        InstallerPathValidationResult validation = _engine.ValidatePath(path);
        ApplyPathValidation(validation, path);
    }

    private void ApplyPathValidation(
        InstallerPathValidationResult validation,
        string requestedPath)
    {
        InstallerWizardViewState current = State.Current;
        InstallerNoticeKind notice = validation.Error == InstallerPathError.InsufficientSpace
            ? InstallerNoticeKind.InsufficientSpace
            : validation.IsValid
                ? InstallerNoticeKind.None
                : InstallerNoticeKind.InvalidPath;
        State.Replace(CreateStepState(
            InstallerWizardStep.Destination,
            requestedPath,
            current.CreateDesktopShortcut,
            current.CreateStartMenuShortcut,
            current.LaunchAfterInstall,
            validation.RequiredBytes,
            validation.AvailableBytes) with
        {
            Scenario = validation.IsValid
                ? InstallerPreviewScenario.Destination
                : notice == InstallerNoticeKind.InsufficientSpace
                    ? InstallerPreviewScenario.InsufficientSpace
                    : InstallerPreviewScenario.InvalidPath,
            Notice = notice,
            NoticeMessageOverride = validation.Message,
            CanPrimaryActionOverride = validation.IsValid
        });
    }

    private void ShowExistingInstallation(ExistingInstallation existing)
    {
        InstallerWizardViewState current = State.Current;
        State.Replace(CreateStepState(
            InstallerWizardStep.Destination,
            existing.InstallLocation ?? current.InstallPath,
            current.CreateDesktopShortcut,
            current.CreateStartMenuShortcut,
            current.LaunchAfterInstall,
            _engine.RequiredBytes,
            current.AvailableBytes) with
        {
            Scenario = InstallerPreviewScenario.ExistingInstallation,
            Notice = InstallerNoticeKind.ExistingInstallation,
            NoticeMessageOverride = existing.Message,
            CanPrimaryActionOverride = true,
            CanOpenInstalledApps = true
        });
    }

    private static InstallerWizardViewState CreateStepState(
        InstallerWizardStep step,
        string installPath,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        bool launchAfterInstall,
        long requiredBytes,
        long? availableBytes)
    {
        InstallerPreviewScenario scenario = step switch
        {
            InstallerWizardStep.Welcome => InstallerPreviewScenario.Welcome,
            InstallerWizardStep.Destination => InstallerPreviewScenario.Destination,
            InstallerWizardStep.Options => InstallerPreviewScenario.Options,
            InstallerWizardStep.Ready => InstallerPreviewScenario.Ready,
            InstallerWizardStep.Installing => InstallerPreviewScenario.Installing,
            _ => InstallerPreviewScenario.Completed
        };
        return new InstallerWizardViewState(
            scenario,
            step,
            installPath,
            createDesktopShortcut,
            createStartMenuShortcut,
            launchAfterInstall,
            InstallerNoticeKind.None,
            ProgressPercent: 0,
            ProgressPhase: "Préparation",
            ProgressDetail: string.Empty,
            requiredBytes,
            availableBytes);
    }

    private static string GetPhaseLabel(InstallerWorkPhase phase) => phase switch
    {
        InstallerWorkPhase.Preparation => "Préparation",
        InstallerWorkPhase.CreatingDirectory => "Création du dossier",
        InstallerWorkPhase.InstallingFiles => "Installation des fichiers",
        InstallerWorkPhase.CreatingShortcuts => "Création des raccourcis",
        InstallerWorkPhase.RegisteringWindows => "Enregistrement dans Windows",
        _ => "Finalisation"
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
