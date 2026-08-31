namespace WotLK.Launcher.UI.V2.Presentation;

using WotLK.Launcher.Runtime;

public sealed class ShellUiState : BindableUiState
{
    private AdaptiveLayoutMode _layoutMode = AdaptiveLayoutMode.Wide;
    private string _username = "Dono1402";
    private bool _isAuthenticated = true;
    private bool _isSessionRestoring;

    public string ProductName { get; init; } = "Atlas Launcher";

    public string GameName { get; init; } = "WotLK Classic";

    public string LauncherVersion { get; init; } = "v1.1.0";

    public string Username
    {
        get => _username;
        init => _username = value;
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        init => _isAuthenticated = value;
    }

    public bool IsSessionRestoring => _isSessionRestoring;

    public bool IsProfileActionEnabled => !_isSessionRestoring;

    public string ProfileInitial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();

    public string ProfileToolTip => IsSessionRestoring
        ? "Restauration de la session…"
        : IsAuthenticated
            ? $"Profil {Username}"
            : "Se connecter";

    public bool IsGameNavigationEnabled { get; init; } = true;

    public bool IsNavigationEnabled { get; init; } = true;

    public AdaptiveLayoutMode LayoutMode
    {
        get => _layoutMode;
        set => SetProperty(ref _layoutMode, value);
    }

    internal void ApplyAuthenticatedUser(string username)
    {
        string normalizedUsername = string.IsNullOrWhiteSpace(username) ? "Compte" : username.Trim();
        bool usernameChanged = SetProperty(ref _username, normalizedUsername, nameof(Username));
        bool authenticationChanged = SetProperty(ref _isAuthenticated, true, nameof(IsAuthenticated));
        if (usernameChanged || authenticationChanged)
        {
            RaisePropertyChanged(nameof(ProfileInitial));
            RaisePropertyChanged(nameof(ProfileToolTip));
        }
    }

    internal void ApplySessionSnapshot(AuthSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _isSessionRestoring = snapshot.IsRestoring;
        _isAuthenticated = snapshot.IsAuthenticated;
        _username = snapshot.IsAuthenticated && !string.IsNullOrWhiteSpace(snapshot.Username)
            ? snapshot.Username.Trim()
            : "Compte";
        RaisePropertyChanged(string.Empty);
    }
}
