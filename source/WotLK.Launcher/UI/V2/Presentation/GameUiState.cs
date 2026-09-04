using System.Windows.Input;
using WotLK.Launcher.UI.V2.Commands;

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
    Launching,
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
    private GamePreviewScenario _scenario = GamePreviewScenario.Ready;
    private GameSemanticTone _semanticTone = GameSemanticTone.Success;
    private string _clientStatus = "Client prêt";
    private string _primaryActionLabel = "Jouer";
    private bool _isPrimaryActionEnabled = true;
    private bool _isOptionsEnabled = true;
    private bool _isVerifyEnabled = true;
    private bool _isRetryEnabled = true;
    private string _installBadgeText = "À jour";
    private string _clientVersion = "3.4.3.54261";
    private string _availableClientVersion = string.Empty;
    private string _installPath = @"C:\Program Files (x86)\WotLK";
    private string _language = "Français";
    private bool _isClientReady = true;
    private double _progress = 100;
    private bool _isProgressIndeterminate;
    private string _progressTitle = string.Empty;
    private string _progressPercentText = string.Empty;
    private string _progressPrimaryDetail = string.Empty;
    private string _progressSecondaryDetail = string.Empty;
    private string _errorTitle = string.Empty;
    private string _errorSummary = string.Empty;
    private string _primaryActionUnavailableReason = string.Empty;
    private bool _isLaunchInProgress;
    private bool _isGameRunning;
    private string _notificationMessage = string.Empty;
    private GameSemanticTone _notificationTone = GameSemanticTone.Neutral;
    private bool _showsNotification;

    public GamePreviewScenario Scenario
    {
        get => _scenario;
        init => _scenario = value;
    }

    public GameSemanticTone SemanticTone
    {
        get => _semanticTone;
        init => _semanticTone = value;
    }

    public string RealmLabel { get; init; } = "ROYAUME ARTHAS";

    public string Title { get; init; } = "Bienvenue en Norfendre";

    public string Subtitle { get; init; } = "Votre aventure vous attend";

    public string ClientStatus
    {
        get => _clientStatus;
        init => _clientStatus = value;
    }

    public string PrimaryActionLabel
    {
        get => _primaryActionLabel;
        init => _primaryActionLabel = value;
    }

    public bool IsPrimaryActionEnabled
    {
        get => _isPrimaryActionEnabled;
        init => _isPrimaryActionEnabled = value;
    }

    public bool IsOptionsEnabled
    {
        get => _isOptionsEnabled;
        init => _isOptionsEnabled = value;
    }

    public bool IsVerifyEnabled
    {
        get => _isVerifyEnabled;
        init => _isVerifyEnabled = value;
    }

    public bool IsRetryEnabled
    {
        get => _isRetryEnabled;
        init => _isRetryEnabled = value;
    }

    public ICommand PrimaryActionCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand OpenGameFolderCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand OpenDiagnosticCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand VerifyCommand { get; private set; } = DisabledCommand.Instance;

    public string InstallBadgeText
    {
        get => _installBadgeText;
        init => _installBadgeText = value;
    }

    public string ClientVersion
    {
        get => _clientVersion;
        init => _clientVersion = value;
    }

    public string AvailableClientVersion
    {
        get => _availableClientVersion;
        init => _availableClientVersion = value;
    }

    public string InstallPath
    {
        get => _installPath;
        init => _installPath = value;
    }

    public string Language
    {
        get => _language;
        init => _language = value;
    }

    public bool IsClientReady
    {
        get => _isClientReady;
        init => _isClientReady = value;
    }

    public double Progress
    {
        get => _progress;
        init => _progress = value;
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        init => _isProgressIndeterminate = value;
    }

    public string ProgressTitle
    {
        get => _progressTitle;
        init => _progressTitle = value;
    }

    public string ProgressPercentText
    {
        get => _progressPercentText;
        init => _progressPercentText = value;
    }

    public string ProgressPrimaryDetail
    {
        get => _progressPrimaryDetail;
        init => _progressPrimaryDetail = value;
    }

    public string ProgressSecondaryDetail
    {
        get => _progressSecondaryDetail;
        init => _progressSecondaryDetail = value;
    }

    public string ErrorTitle
    {
        get => _errorTitle;
        init => _errorTitle = value;
    }

    public string ErrorSummary
    {
        get => _errorSummary;
        init => _errorSummary = value;
    }

    public string PrimaryActionUnavailableReason
    {
        get => _primaryActionUnavailableReason;
        init => _primaryActionUnavailableReason = value;
    }

    public bool IsLaunchInProgress
    {
        get => _isLaunchInProgress;
        init => _isLaunchInProgress = value;
    }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        init => _isGameRunning = value;
    }

    public string NotificationMessage => _notificationMessage;

    public GameSemanticTone NotificationTone => _notificationTone;

    public bool ShowsNotification => _showsNotification;

    public bool ShowsReadyInstallation => Scenario is GamePreviewScenario.Ready or GamePreviewScenario.RealmOffline;

    public bool ShowsNotInstalled => Scenario == GamePreviewScenario.NotInstalled;

    public bool ShowsUpdateAvailable => Scenario == GamePreviewScenario.UpdateAvailable;

    public bool ShowsProgress => Scenario is GamePreviewScenario.Downloading
        or GamePreviewScenario.Installing
        or GamePreviewScenario.Verifying;

    public bool ShowsError => Scenario == GamePreviewScenario.Error;

    internal void AttachLocalCommands(ICommand openGameFolder, ICommand openDiagnostic)
    {
        OpenGameFolderCommand = openGameFolder ?? throw new ArgumentNullException(nameof(openGameFolder));
        OpenDiagnosticCommand = openDiagnostic ?? throw new ArgumentNullException(nameof(openDiagnostic));
        RaisePropertyChanged(nameof(OpenGameFolderCommand));
        RaisePropertyChanged(nameof(OpenDiagnosticCommand));
    }

    internal void AttachVerifyCommand(ICommand verifyCommand)
    {
        VerifyCommand = verifyCommand ?? throw new ArgumentNullException(nameof(verifyCommand));
        RaisePropertyChanged(nameof(VerifyCommand));
    }

    internal void AttachPrimaryActionCommand(ICommand primaryActionCommand)
    {
        PrimaryActionCommand = primaryActionCommand
            ?? throw new ArgumentNullException(nameof(primaryActionCommand));
        RaisePropertyChanged(nameof(PrimaryActionCommand));
    }

    internal void ApplyRuntimeView(GameViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        _scenario = viewState.Scenario;
        _semanticTone = viewState.SemanticTone;
        _clientStatus = viewState.ClientStatus;
        _primaryActionLabel = viewState.PrimaryActionLabel;
        _isPrimaryActionEnabled = viewState.IsPrimaryActionEnabled;
        _isOptionsEnabled = viewState.IsOptionsEnabled;
        _isVerifyEnabled = viewState.IsVerifyEnabled;
        _isRetryEnabled = viewState.IsRetryEnabled;
        _installBadgeText = viewState.InstallBadgeText;
        _clientVersion = viewState.ClientVersion;
        _availableClientVersion = viewState.AvailableClientVersion;
        _installPath = viewState.InstallPath;
        _language = viewState.Language;
        _isClientReady = viewState.IsClientReady;
        _progress = viewState.Progress;
        _isProgressIndeterminate = viewState.IsProgressIndeterminate;
        _progressTitle = viewState.ProgressTitle;
        _progressPercentText = viewState.ProgressPercentText;
        _progressPrimaryDetail = viewState.ProgressPrimaryDetail;
        _progressSecondaryDetail = viewState.ProgressSecondaryDetail;
        _errorTitle = viewState.ErrorTitle;
        _errorSummary = viewState.ErrorSummary;
        _primaryActionUnavailableReason = viewState.PrimaryActionUnavailableReason;
        _isLaunchInProgress = viewState.IsLaunchInProgress;
        _isGameRunning = viewState.IsGameRunning;
        RaisePropertyChanged(string.Empty);
    }

    internal void ShowNotification(string message, GameSemanticTone tone)
    {
        _notificationMessage = message;
        _notificationTone = tone;
        _showsNotification = !string.IsNullOrWhiteSpace(message);
        RaisePropertyChanged(nameof(NotificationMessage));
        RaisePropertyChanged(nameof(NotificationTone));
        RaisePropertyChanged(nameof(ShowsNotification));
    }

    internal void ClearNotification()
    {
        _notificationMessage = string.Empty;
        _notificationTone = GameSemanticTone.Neutral;
        _showsNotification = false;
        RaisePropertyChanged(nameof(NotificationMessage));
        RaisePropertyChanged(nameof(NotificationTone));
        RaisePropertyChanged(nameof(ShowsNotification));
    }
}
