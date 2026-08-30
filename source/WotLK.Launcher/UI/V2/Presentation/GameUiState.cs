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
    private bool _isVerifyEnabled = true;
    private string _installBadgeText = "À jour";
    private string _availableClientVersion = string.Empty;
    private bool _isClientReady = true;
    private double _progress = 100;
    private bool _isProgressIndeterminate;
    private string _progressTitle = string.Empty;
    private string _progressPercentText = string.Empty;
    private string _progressPrimaryDetail = string.Empty;
    private string _progressSecondaryDetail = string.Empty;
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

    public string RealmStatus { get; init; } = "Royaume en ligne";

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

    public bool IsOptionsEnabled { get; init; } = true;

    public bool IsVerifyEnabled
    {
        get => _isVerifyEnabled;
        init => _isVerifyEnabled = value;
    }

    public bool IsRetryEnabled { get; init; } = true;

    public ICommand OpenGameFolderCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand OpenDiagnosticCommand { get; private set; } = DisabledCommand.Instance;

    public ICommand VerifyCommand { get; private set; } = DisabledCommand.Instance;

    public string InstallBadgeText
    {
        get => _installBadgeText;
        init => _installBadgeText = value;
    }

    public string ClientVersion { get; init; } = "3.4.3.54261";

    public string AvailableClientVersion
    {
        get => _availableClientVersion;
        init => _availableClientVersion = value;
    }

    public string InstallPath { get; init; } = @"C:\Program Files (x86)\WotLK";

    public string Language { get; init; } = "Français";

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

    public string ErrorTitle { get; init; } = string.Empty;

    public string ErrorSummary { get; init; } = string.Empty;

    public string NotificationMessage => _notificationMessage;

    public GameSemanticTone NotificationTone => _notificationTone;

    public bool ShowsNotification => _showsNotification;

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

    internal void ApplyRuntimeView(GameViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        _scenario = viewState.Scenario;
        _semanticTone = viewState.SemanticTone;
        _clientStatus = viewState.ClientStatus;
        _primaryActionLabel = viewState.PrimaryActionLabel;
        _isPrimaryActionEnabled = viewState.IsPrimaryActionEnabled;
        _isVerifyEnabled = viewState.IsVerifyEnabled;
        _installBadgeText = viewState.InstallBadgeText;
        _availableClientVersion = viewState.AvailableClientVersion;
        _isClientReady = viewState.IsClientReady;
        _progress = viewState.Progress;
        _isProgressIndeterminate = viewState.IsProgressIndeterminate;
        _progressTitle = viewState.ProgressTitle;
        _progressPercentText = viewState.ProgressPercentText;
        _progressPrimaryDetail = viewState.ProgressPrimaryDetail;
        _progressSecondaryDetail = viewState.ProgressSecondaryDetail;
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
