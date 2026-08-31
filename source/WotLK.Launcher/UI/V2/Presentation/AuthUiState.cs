using System.Windows.Input;
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
        _showLoginCommand.RaiseCanExecuteChanged();
        _showRegisterCommand.RaiseCanExecuteChanged();
        _submitCommand.RaiseCanExecuteChanged();
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
