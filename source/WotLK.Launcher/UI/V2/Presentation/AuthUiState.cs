using System.Windows.Input;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Preview;

namespace WotLK.Launcher.UI.V2.Presentation;

public enum AuthMode
{
    Login,
    Register
}

public enum AuthErrorKind
{
    None,
    Validation,
    InvalidCredentials,
    RegistrationRejected,
    UsernameAlreadyExists,
    EmailAlreadyExists,
    ServiceUnavailable
}

public sealed class AuthUiState : BindableUiState, IDisposable
{
    private readonly DelegateCommand _showLoginCommand;
    private readonly DelegateCommand _showRegisterCommand;
    private readonly DelegateCommand _submitCommand;
    private bool _isOpen;
    private AuthMode _mode;
    private bool _isBusy;
    private AuthErrorKind _errorKind;
    private string _errorMessage = string.Empty;
    private bool _isEmailWarningVisible;
    private bool _isFormValid;
    private string _loginUsername = string.Empty;
    private string _registerUsername = string.Empty;
    private string _registerEmail = string.Empty;
    private int _previewSubmissionCount;
    private int _disposeState;

    public AuthUiState()
    {
        _showLoginCommand = new DelegateCommand(
            () => SetMode(AuthMode.Login),
            () => IsOpen && !IsBusy && Mode != AuthMode.Login);
        _showRegisterCommand = new DelegateCommand(
            () => SetMode(AuthMode.Register),
            () => IsOpen && !IsBusy && Mode != AuthMode.Register);
        _submitCommand = new DelegateCommand(
            () => _previewSubmissionCount++,
            () => IsOpen && !IsBusy && IsFormValid);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public AuthMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                RaiseDerivedProperties();
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseDerivedProperties();
                RaiseCommandStates();
            }
        }
    }

    public AuthErrorKind ErrorKind
    {
        get => _errorKind;
        private set
        {
            if (SetProperty(ref _errorKind, value))
            {
                RaisePropertyChanged(nameof(IsErrorVisible));
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsEmailWarningVisible
    {
        get => _isEmailWarningVisible;
        private set => SetProperty(ref _isEmailWarningVisible, value);
    }

    public bool IsFormValid
    {
        get => _isFormValid;
        private set
        {
            if (SetProperty(ref _isFormValid, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string LoginUsername
    {
        get => _loginUsername;
        set => SetProperty(ref _loginUsername, value);
    }

    public string RegisterUsername
    {
        get => _registerUsername;
        set => SetProperty(ref _registerUsername, value);
    }

    public string RegisterEmail
    {
        get => _registerEmail;
        set => SetProperty(ref _registerEmail, value);
    }

    public string Title => Mode == AuthMode.Login ? "Connexion" : "Créer un compte";

    public string Description => Mode == AuthMode.Login
        ? "Retrouve ton compte Atlas et continue vers Arthas."
        : "Crée ton compte Atlas pour rejoindre le royaume Arthas.";

    public string PrimaryActionLabel => IsBusy
        ? Mode == AuthMode.Login ? "Connexion…" : "Création…"
        : Mode == AuthMode.Login ? "Se connecter" : "Créer mon compte";

    public bool IsFormEnabled => !IsBusy;

    public bool IsErrorVisible => ErrorKind != AuthErrorKind.None
        && !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool CanSubmit => IsOpen && !IsBusy && IsFormValid;

    public ICommand ShowLoginCommand => _showLoginCommand;

    public ICommand ShowRegisterCommand => _showRegisterCommand;

    public ICommand SubmitCommand => _submitCommand;

    internal int PreviewSubmissionCount => _previewSubmissionCount;

    internal void ApplyPreviewScenario(AuthPreviewScenario scenario)
    {
        IsOpen = true;
        Mode = scenario is AuthPreviewScenario.Register
            or AuthPreviewScenario.RegisterError
            or AuthPreviewScenario.RegisterValidation
            ? AuthMode.Register
            : AuthMode.Login;
        IsBusy = scenario == AuthPreviewScenario.Loading;
        IsEmailWarningVisible = scenario == AuthPreviewScenario.EmailWarning;
        ErrorKind = scenario switch
        {
            AuthPreviewScenario.LoginError => AuthErrorKind.InvalidCredentials,
            AuthPreviewScenario.RegisterError => AuthErrorKind.RegistrationRejected,
            AuthPreviewScenario.RegisterValidation => AuthErrorKind.Validation,
            AuthPreviewScenario.ServiceUnavailable => AuthErrorKind.ServiceUnavailable,
            _ => AuthErrorKind.None
        };
        ErrorMessage = scenario switch
        {
            AuthPreviewScenario.LoginError => "Identifiants incorrects.",
            AuthPreviewScenario.RegisterError => "Atlas n’a pas pu créer ce compte pour le moment.",
            AuthPreviewScenario.RegisterValidation => "Les deux mots de passe ne correspondent pas.",
            AuthPreviewScenario.ServiceUnavailable => "Atlas est temporairement indisponible. Réessaie dans quelques instants.",
            _ => string.Empty
        };
        RaiseDerivedProperties();
        RaiseCommandStates();
    }

    internal void SetFormValidity(bool isValid)
    {
        IsFormValid = isValid;
    }

    internal void ShowValidationError(string message)
    {
        ErrorKind = AuthErrorKind.Validation;
        ErrorMessage = message;
    }

    internal void ClearErrorAfterInput()
    {
        if (ErrorKind == AuthErrorKind.None)
        {
            return;
        }

        ErrorKind = AuthErrorKind.None;
        ErrorMessage = string.Empty;
    }

    internal void ResetAfterClose()
    {
        LoginUsername = string.Empty;
        RegisterUsername = string.Empty;
        RegisterEmail = string.Empty;
        IsBusy = false;
        IsEmailWarningVisible = false;
        IsFormValid = false;
        ErrorKind = AuthErrorKind.None;
        ErrorMessage = string.Empty;
        Mode = AuthMode.Login;
    }

    internal void PrepareForOpen()
    {
        _mode = AuthMode.Login;
        _isBusy = false;
        _isEmailWarningVisible = false;
        _isFormValid = false;
        _errorKind = AuthErrorKind.None;
        _errorMessage = string.Empty;
        _isOpen = true;
        RaisePropertyChanged(string.Empty);
        RaiseCommandStates();
    }

    internal void ApplySessionSnapshot(AuthSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _isBusy = snapshot.IsSubmitting;

        if (snapshot.IsAuthenticated)
        {
            _errorKind = AuthErrorKind.None;
            _errorMessage = string.Empty;
            _isEmailWarningVisible = false;
            _isFormValid = false;
            _isOpen = false;
        }
        else if (!snapshot.IsSubmitting
                 && snapshot.OperationKind is LauncherSessionOperationKind.Login
                     or LauncherSessionOperationKind.Register
                 && snapshot.FailureCategory != LauncherSessionFailureCategory.None)
        {
            (_errorKind, _errorMessage) = MapFailure(snapshot.FailureCategory);
            if (snapshot.FailureCategory
                == LauncherSessionFailureCategory.AccountCreatedSignInRequired)
            {
                _mode = AuthMode.Login;
                _loginUsername = snapshot.Username;
            }
        }

        RaisePropertyChanged(string.Empty);
        RaiseCommandStates();
    }

    internal void SetMode(AuthMode mode)
    {
        if (mode == AuthMode.Register && string.IsNullOrWhiteSpace(RegisterUsername))
        {
            RegisterUsername = LoginUsername.Trim();
        }

        Mode = mode;
        IsFormValid = false;
        IsEmailWarningVisible = false;
        ErrorKind = AuthErrorKind.None;
        ErrorMessage = string.Empty;
    }

    private void RaiseDerivedProperties()
    {
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(Description));
        RaisePropertyChanged(nameof(PrimaryActionLabel));
        RaisePropertyChanged(nameof(IsFormEnabled));
    }

    private void RaiseCommandStates()
    {
        RaisePropertyChanged(nameof(CanSubmit));
        _showLoginCommand.RaiseCanExecuteChanged();
        _showRegisterCommand.RaiseCanExecuteChanged();
        _submitCommand.RaiseCanExecuteChanged();
    }

    private static (AuthErrorKind Kind, string Message) MapFailure(
        LauncherSessionFailureCategory category)
    {
        return category switch
        {
            LauncherSessionFailureCategory.InvalidCredentials =>
                (AuthErrorKind.InvalidCredentials, "Identifiants incorrects."),
            LauncherSessionFailureCategory.UsernameAlreadyExists =>
                (AuthErrorKind.UsernameAlreadyExists, "Ce nom d’utilisateur est déjà utilisé."),
            LauncherSessionFailureCategory.EmailAlreadyExists =>
                (AuthErrorKind.EmailAlreadyExists, "Cette adresse e-mail est déjà utilisée."),
            LauncherSessionFailureCategory.Validation =>
                (AuthErrorKind.RegistrationRejected, "Ce nom d’utilisateur ou cette adresse e-mail est déjà utilisé."),
            LauncherSessionFailureCategory.AccountCreatedSignInRequired =>
                (AuthErrorKind.RegistrationRejected, "Compte créé. Connecte-toi pour continuer."),
            LauncherSessionFailureCategory.Network
                or LauncherSessionFailureCategory.Timeout
                or LauncherSessionFailureCategory.ServiceUnavailable =>
                (AuthErrorKind.ServiceUnavailable, "Service temporairement indisponible."),
            LauncherSessionFailureCategory.Unauthorized
                or LauncherSessionFailureCategory.SessionExpired =>
                (AuthErrorKind.InvalidCredentials, "Ta session n’est plus valide. Reconnecte-toi."),
            _ => (AuthErrorKind.ServiceUnavailable, "Une erreur inattendue est survenue. Réessaie dans quelques instants.")
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        ResetAfterClose();
        _showLoginCommand.Dispose();
        _showRegisterCommand.Dispose();
        _submitCommand.Dispose();
    }
}
