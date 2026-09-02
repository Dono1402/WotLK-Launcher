using System.Windows.Input;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Preview;

namespace WotLK.Launcher.UI.V2.Presentation;

public enum AuthMode
{
    Login,
    Register,
    EnrollmentPrompt,
    Enrollment
}

public enum AuthErrorKind
{
    None,
    Validation,
    InvalidCredentials,
    AtlasProfileRequired,
    RegistrationRejected,
    UsernameAlreadyExists,
    EmailAlreadyExists,
    ServiceUnavailable
}

public sealed class AuthUiState : BindableUiState, IDisposable
{
    private readonly DelegateCommand _showLoginCommand;
    private readonly DelegateCommand _showRegisterCommand;
    private readonly DelegateCommand _beginEnrollmentCommand;
    private readonly DelegateCommand _returnCommand;
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
    private string _enrollmentUsername = string.Empty;
    private string _enrollmentEmail = string.Empty;
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
        _beginEnrollmentCommand = new DelegateCommand(
            () => SetMode(AuthMode.Enrollment),
            () => IsOpen && !IsBusy && Mode == AuthMode.EnrollmentPrompt);
        _returnCommand = new DelegateCommand(
            ReturnFromEnrollment,
            () => IsOpen && !IsBusy && Mode is AuthMode.EnrollmentPrompt or AuthMode.Enrollment);
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
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                RaisePropertyChanged(nameof(IsErrorVisible));
            }
        }
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

    public string EnrollmentUsername
    {
        get => _enrollmentUsername;
        set => SetProperty(ref _enrollmentUsername, value);
    }

    public string EnrollmentEmail
    {
        get => _enrollmentEmail;
        set => SetProperty(ref _enrollmentEmail, value);
    }

    public string Title => Mode switch
    {
        AuthMode.Login => "Connexion",
        AuthMode.Register => "Créer un compte",
        _ => "Activer Atlas"
    };

    public string Description => Mode switch
    {
        AuthMode.Login => "Retrouve ton compte Atlas et continue vers Arthas.",
        AuthMode.Register => "Crée ton compte Atlas pour rejoindre le royaume Arthas.",
        AuthMode.EnrollmentPrompt => "Associe volontairement ton compte WoW à Atlas Launcher.",
        _ => "Confirme ton compte WoW et choisis l’adresse e-mail de ton profil Atlas."
    };

    public string PrimaryActionLabel => IsBusy
        ? Mode switch
        {
            AuthMode.Login => "Connexion…",
            AuthMode.Register => "Création…",
            _ => "Activation…"
        }
        : Mode switch
        {
            AuthMode.Login => "Se connecter",
            AuthMode.Register => "Créer mon compte",
            _ => "Activer Atlas"
        };

    public bool IsFormEnabled => !IsBusy;

    public bool IsErrorVisible => ErrorKind != AuthErrorKind.None
        && !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool CanSubmit => IsOpen && !IsBusy && IsFormValid;

    public bool IsModeSelectorVisible => Mode is AuthMode.Login or AuthMode.Register;

    public bool IsEnrollmentPromptVisible => Mode == AuthMode.EnrollmentPrompt;

    public bool IsEnrollmentFormVisible => Mode == AuthMode.Enrollment;

    public bool IsPrimaryActionVisible => Mode != AuthMode.EnrollmentPrompt;

    public ICommand ShowLoginCommand => _showLoginCommand;

    public ICommand ShowRegisterCommand => _showRegisterCommand;

    public ICommand BeginEnrollmentCommand => _beginEnrollmentCommand;

    public ICommand ReturnCommand => _returnCommand;

    public ICommand SubmitCommand => _submitCommand;

    internal int PreviewSubmissionCount => _previewSubmissionCount;

    internal void ApplyPreviewScenario(AuthPreviewScenario scenario)
    {
        IsOpen = true;
        Mode = scenario is AuthPreviewScenario.Register
            or AuthPreviewScenario.RegisterError
            or AuthPreviewScenario.RegisterValidation
                ? AuthMode.Register
            : scenario == AuthPreviewScenario.AtlasEnrollment
                ? AuthMode.EnrollmentPrompt
            : scenario == AuthPreviewScenario.AtlasEnrollmentError
                ? AuthMode.Enrollment
            : AuthMode.Login;
        IsBusy = scenario == AuthPreviewScenario.Loading;
        IsEmailWarningVisible = scenario == AuthPreviewScenario.EmailWarning;
        ErrorKind = scenario switch
        {
            AuthPreviewScenario.LoginError => AuthErrorKind.InvalidCredentials,
            AuthPreviewScenario.RegisterError => AuthErrorKind.RegistrationRejected,
            AuthPreviewScenario.RegisterValidation => AuthErrorKind.Validation,
            AuthPreviewScenario.ServiceUnavailable => AuthErrorKind.ServiceUnavailable,
            AuthPreviewScenario.AtlasEnrollmentError => AuthErrorKind.EmailAlreadyExists,
            _ => AuthErrorKind.None
        };
        ErrorMessage = scenario switch
        {
            AuthPreviewScenario.LoginError => "Identifiants incorrects.",
            AuthPreviewScenario.RegisterError => "Atlas n’a pas pu créer ce compte pour le moment.",
            AuthPreviewScenario.RegisterValidation => "Les deux mots de passe ne correspondent pas.",
            AuthPreviewScenario.ServiceUnavailable => "Atlas est temporairement indisponible. Réessaie dans quelques instants.",
            AuthPreviewScenario.AtlasEnrollmentError => "Cette adresse e-mail est déjà utilisée.",
            _ => string.Empty
        };
        if (scenario is AuthPreviewScenario.AtlasEnrollment
            or AuthPreviewScenario.AtlasEnrollmentError)
        {
            LoginUsername = "Dono1402";
            EnrollmentUsername = "Dono1402";
            EnrollmentEmail = scenario == AuthPreviewScenario.AtlasEnrollmentError
                ? "dono1402@example.test"
                : string.Empty;
        }
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
        EnrollmentUsername = string.Empty;
        EnrollmentEmail = string.Empty;
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
                     or LauncherSessionOperationKind.Enrollment
                 && snapshot.FailureCategory != LauncherSessionFailureCategory.None)
        {
            if (snapshot.FailureCategory == LauncherSessionFailureCategory.AtlasProfileRequired)
            {
                _mode = AuthMode.EnrollmentPrompt;
                _loginUsername = snapshot.Username;
                _enrollmentUsername = snapshot.Username;
                _errorKind = AuthErrorKind.None;
                _errorMessage = string.Empty;
                _isFormValid = false;
            }
            else
            {
                (_errorKind, _errorMessage) = MapFailure(snapshot.FailureCategory);
                if (snapshot.OperationKind == LauncherSessionOperationKind.Enrollment)
                {
                    _mode = AuthMode.Enrollment;
                }
                else if (snapshot.FailureCategory
                         == LauncherSessionFailureCategory.AccountCreatedSignInRequired)
                {
                    _mode = AuthMode.Login;
                    _loginUsername = snapshot.Username;
                }
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

        else if (mode == AuthMode.Enrollment)
        {
            if (string.IsNullOrWhiteSpace(EnrollmentUsername))
            {
                EnrollmentUsername = LoginUsername.Trim();
            }
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
        RaisePropertyChanged(nameof(IsModeSelectorVisible));
        RaisePropertyChanged(nameof(IsEnrollmentPromptVisible));
        RaisePropertyChanged(nameof(IsEnrollmentFormVisible));
        RaisePropertyChanged(nameof(IsPrimaryActionVisible));
    }

    private void RaiseCommandStates()
    {
        RaisePropertyChanged(nameof(CanSubmit));
        _showLoginCommand.RaiseCanExecuteChanged();
        _showRegisterCommand.RaiseCanExecuteChanged();
        _beginEnrollmentCommand.RaiseCanExecuteChanged();
        _returnCommand.RaiseCanExecuteChanged();
        _submitCommand.RaiseCanExecuteChanged();
    }

    private static (AuthErrorKind Kind, string Message) MapFailure(
        LauncherSessionFailureCategory category)
    {
        return category switch
        {
            LauncherSessionFailureCategory.InvalidCredentials =>
                (AuthErrorKind.InvalidCredentials, "Identifiants incorrects."),
            LauncherSessionFailureCategory.AtlasProfileRequired =>
                (AuthErrorKind.AtlasProfileRequired, AtlasAuthErrorMessage),
            LauncherSessionFailureCategory.EnrollmentNotAllowed =>
                (AuthErrorKind.RegistrationRejected, "Ce compte ne peut pas être associé à Atlas."),
            LauncherSessionFailureCategory.AlreadyEnrolled =>
                (AuthErrorKind.RegistrationRejected, "Ce compte est déjà associé à Atlas."),
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

    private const string AtlasAuthErrorMessage =
        "Ce compte n’est pas encore inscrit dans Atlas Launcher.";

    private void ReturnFromEnrollment()
    {
        SetMode(Mode == AuthMode.Enrollment
            ? AuthMode.EnrollmentPrompt
            : AuthMode.Login);
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
        _beginEnrollmentCommand.Dispose();
        _returnCommand.Dispose();
        _submitCommand.Dispose();
    }
}
