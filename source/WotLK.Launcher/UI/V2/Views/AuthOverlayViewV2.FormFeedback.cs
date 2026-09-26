using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class AuthOverlayViewV2
{
    private sealed record PasswordControl(PasswordBox Masked, TextBox Revealed, Button Eye);
    private PasswordControl[] PasswordControls = [];
    private bool _syncingPasswords;
    private CancellationTokenSource? _recoveryCancellation;
    internal Func<string, CancellationToken, Task<PasswordRecoveryResult>>? PasswordRecoveryRequested { get; set; }

    private void InitializePasswordControls()
    {
        PasswordControls = [new(LoginPasswordBox, LoginPasswordBoxRevealed, LoginPasswordBoxEye),
            new(RegisterPasswordBox, RegisterPasswordBoxRevealed, RegisterPasswordBoxEye),
            new(RegisterPasswordConfirmBox, RegisterPasswordConfirmBoxRevealed, RegisterPasswordConfirmBoxEye)];
        foreach (PasswordControl pair in PasswordControls)
        {
            KeyboardNavigation.SetTabIndex(pair.Revealed, KeyboardNavigation.GetTabIndex(pair.Masked));
            KeyboardNavigation.SetTabIndex(pair.Eye, KeyboardNavigation.GetTabIndex(pair.Masked));
        }
    }

    private Control VisibleInput(Control input)
    {
        PasswordControl? pair = PasswordControls.FirstOrDefault(pair => ReferenceEquals(pair.Masked, input));
        return pair?.Revealed.Visibility == Visibility.Visible ? pair.Revealed : input;
    }

    private static bool HasButtonAncestor(DependencyObject source)
    {
        for (DependencyObject? current = source; current is Visual; current = VisualTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }

    private void PasswordEye_Click(object sender, RoutedEventArgs e)
    {
        PasswordControl? pair = PasswordControls.FirstOrDefault(pair => ReferenceEquals(pair.Eye, sender));
        if (pair is null) return;
        bool reveal = pair.Revealed.Visibility != Visibility.Visible;
        _syncingPasswords = true;
        try
        {
            pair.Revealed.Text = reveal ? pair.Masked.Password : string.Empty;
            pair.Revealed.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            pair.Masked.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
            SetEyeLabel(pair, reveal);
        }
        finally { _syncingPasswords = false; }
        // Keep keyboard focus on the eye so Space toggles it again.
    }

    private static void SetEyeLabel(PasswordControl pair, bool revealed)
    {
        string label = revealed ? "Masquer le mot de passe" : "Afficher le mot de passe";
        if (LauncherLocalization.CurrentLocale == LauncherLocalization.EnglishLocale)
            label = revealed ? "Hide password" : "Show password";
        pair.Eye.ToolTip = label;
        AutomationProperties.SetName(pair.Eye, label);
    }

    private void RevealedPassword_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingPasswords || _applyingPreview) return;
        PasswordControl? pair = PasswordControls.FirstOrDefault(pair => ReferenceEquals(pair.Revealed, sender));
        if (pair is null || pair.Revealed.Visibility != Visibility.Visible) return;
        _syncingPasswords = true;
        try { pair.Masked.Password = pair.Revealed.Text; }
        finally { _syncingPasswords = false; }
    }

    private void SynchronizeMaskedPassword(object sender)
    {
        if (_syncingPasswords) return;
        PasswordControl? pair = PasswordControls.FirstOrDefault(pair => ReferenceEquals(pair.Masked, sender));
        if (pair?.Revealed.Visibility != Visibility.Visible) return;
        _syncingPasswords = true;
        try { pair.Revealed.Text = pair.Masked.Password; }
        finally { _syncingPasswords = false; }
    }

    private void ResetPasswordReveals()
    {
        _syncingPasswords = true;
        try
        {
            foreach (PasswordControl pair in PasswordControls)
            {
                pair.Revealed.Visibility = Visibility.Collapsed;
                pair.Masked.Visibility = Visibility.Visible;
                pair.Revealed.Clear();
                SetEyeLabel(pair, false);
            }
        }
        finally { _syncingPasswords = false; }
    }

    private string FieldKey(Control? input) => input?.Name switch
    {
        "LoginUsernameBox" or "RegisterUsernameBox" => "username",
        "RegisterEmailBox" or "RecoveryEmailBox" => "email",
        "RegisterPasswordConfirmBox" => "confirmation",
        _ => "password"
    };

    private void Field_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_applyingPreview || State is null || State.IsBusy || !IsOpen
            || sender is not Border { Tag: Control input } field
            || !field.IsVisible
            || e.NewFocus is DependencyObject next && IsDescendantOf(next, field)) return;
        ValidateField(FieldKey(input));
        if (State.Mode == AuthMode.Register && FieldKey(input) == "password" && RegisterPasswordConfirmBox.Password.Length > 0)
            ValidateField("confirmation");
    }

    internal static string EmailValidation(string email) => string.IsNullOrWhiteSpace(email)
        ? "Renseigne ton adresse e-mail."
        : email.Trim().Length > 254 || !new EmailAddressAttribute().IsValid(email.Trim())
            ? "Adresse e-mail invalide." : "";

    private void ValidateField(string field)
    {
        if (State is not { } state) return;
        // Required fields keep submission disabled without turning an untouched or
        // cleared form into a list of errors. Only validate values actually entered.
        bool hasValue = field switch
        {
            "username" => !string.IsNullOrWhiteSpace(state.Mode == AuthMode.Login ? state.LoginUsername : state.RegisterUsername),
            "email" => !string.IsNullOrWhiteSpace(state.Mode == AuthMode.Recovery ? state.RecoveryEmail : state.RegisterEmail),
            "password" => (state.Mode == AuthMode.Login ? LoginPasswordBox : RegisterPasswordBox).Password.Length > 0,
            _ => RegisterPasswordBox.Password.Length > 0 && RegisterPasswordConfirmBox.Password.Length > 0
        };
        if (!hasValue)
        {
            state.SetFieldError(field, "");
            return;
        }
        string error = field switch
        {
            "username" when state.Mode == AuthMode.Login => "",
            "username" => Regex.IsMatch(state.RegisterUsername.Trim(), "^[A-Za-z0-9_]{3,20}$")
                ? "" : "Le nom doit contenir 3 à 20 lettres, chiffres ou underscores.",
            "email" => EmailValidation(state.Mode == AuthMode.Recovery ? state.RecoveryEmail : state.RegisterEmail),
            "password" when state.Mode == AuthMode.Login => "",
            "password" => RegisterPasswordBox.Password.Length is >= 10 and <= 128
                ? "" : "Le mot de passe doit contenir entre 10 et 128 caractères.",
            _ => RegisterPasswordConfirmBox.Password == RegisterPasswordBox.Password ? ""
                : "Les deux mots de passe ne correspondent pas."
        };
        state.SetFieldError(field, error);
    }

    private void ValidateAllFields()
    {
        foreach (string field in State?.Mode == AuthMode.Login
                     ? new[] { "username", "password" }
                     : new[] { "username", "email", "password", "confirmation" }) ValidateField(field);
    }

    private void UpdateExtraPanels()
    {
        if (State is not { } state) return;
        bool success = state.IsRegistrationComplete;
        ModeSelector.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        LoginForm.Visibility = !success && state.Mode == AuthMode.Login ? Visibility.Visible : Visibility.Collapsed;
        RegisterForm.Visibility = !success && state.Mode == AuthMode.Register ? Visibility.Visible : Visibility.Collapsed;
        RecoveryForm.Visibility = !success && state.Mode == AuthMode.Recovery ? Visibility.Visible : Visibility.Collapsed;
        RegistrationSuccess.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        State?.SetMode(AuthMode.Recovery);
        UpdateExtraPanels();
        ValidateForPreview(false);
    }

    private async Task SubmitRecoveryAsync()
    {
        if (State is not { } state || !state.CanSubmit || _recoveryCancellation is not null) return;
        using CancellationTokenSource cancellation = new();
        _recoveryCancellation = cancellation;
        state.BeginRecovery();
        try
        {
            PasswordRecoveryResult result = PasswordRecoveryRequested is { } request
                ? await request(state.RecoveryEmail.Trim(), cancellation.Token)
                : PasswordRecoveryResult.Unavailable;
            if (!cancellation.IsCancellationRequested && ReferenceEquals(State, state) && state.IsOpen)
                state.CompleteRecovery(result.Accepted, result.Error);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            if (!cancellation.IsCancellationRequested && ReferenceEquals(State, state) && state.IsOpen)
                state.CompleteRecovery(false, PasswordRecoveryResult.Unavailable.Error);
        }
        finally
        {
            if (ReferenceEquals(_recoveryCancellation, cancellation)) _recoveryCancellation = null;
        }
    }

    private void CancelRecovery()
    {
        _recoveryCancellation?.Cancel();
        _recoveryCancellation = null;
    }
}
