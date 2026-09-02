using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Server;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class AuthOverlayPreviewTests
{
    private static uint _observedWindowDpi;

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizeLegacyContractAndValidation();
        RouteEveryPreviewArgumentBeforeComposition();
        KeepPreviewPresentationIsolated();
        CharacterizePresentationStatesAndCommands();
        await ValidateWpfInteractionResponsiveLayoutAndCapturesAsync(captureDirectory);
        Console.WriteLine($"Auth overlay WPF preview OK (02F.1, window DPI={_observedWindowDpi}).");
        if (_observedWindowDpi != 120)
        {
            Console.WriteLine("Validation DPI réelle 125 % restante à effectuer manuellement.");
        }

        return 0;
    }

    private static void CharacterizeLegacyContractAndValidation()
    {
        string[] loginProperties = typeof(LoginRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Equal(
            "DeviceName,Password,Username",
            string.Join(',', loginProperties),
            "La connexion legacy doit accepter le nom d'utilisateur, pas l'e-mail.");

        string[] registerProperties = typeof(RegisterRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Equal(
            "Email,Password,Username",
            string.Join(',', registerProperties),
            "L'inscription réseau doit rester limitée à nom, e-mail et mot de passe.");

        True(!AuthPreviewValidation.Login(string.Empty, hasPassword: true).IsValid, "Le nom est obligatoire à la connexion.");
        True(!AuthPreviewValidation.Login("Dono1402", hasPassword: false).IsValid, "Le mot de passe est obligatoire à la connexion.");
        True(AuthPreviewValidation.Login("Dono1402", hasPassword: true).IsValid, "Une connexion complète doit être valide visuellement.");

        True(!Register("ab", "dono@example.test", 10, true, true).IsValid, "Le nom doit contenir au moins trois caractères.");
        True(!Register("dono-1402", "dono@example.test", 10, true, true).IsValid, "Le tiret ne fait pas partie du contrat legacy.");
        True(Register("dono_1402", "dono@example.test", 10, true, true).IsValid, "L'underscore doit être accepté.");
        True(!Register("Dono1402", "not-an-email", 10, true, true).IsValid, "Une adresse invalide doit être refusée.");
        True(!Register("Dono1402", "dono@example.test", 9, true, true).IsValid, "Neuf caractères doivent être refusés.");
        True(Register("Dono1402", "dono@example.test", 128, true, true).IsValid, "Cent vingt-huit caractères doivent être acceptés.");
        True(!Register("Dono1402", "dono@example.test", 10, true, false).IsValid, "Une confirmation différente doit être refusée.");
        True(!Register("Dono1402", "dono@example.test", 10, false, false).IsValid, "La confirmation est obligatoire.");
    }

    private static void RouteEveryPreviewArgumentBeforeComposition()
    {
        Equal(LauncherStartupMode.UiV2AuthPreview, App.ResolveStartupMode(["--ui-v2", "--preview-auth"]), "Le preview sans valeur doit ouvrir Login.");
        Equal(LauncherStartupMode.UiV2AuthPreview, App.ResolveStartupMode(["--ui-v2", "--preview-auth=register"]), "Le preview Register doit être isolé.");
        Equal(LauncherStartupMode.InvalidArguments, App.ResolveStartupMode(["--preview-auth=login"]), "preview-auth sans --ui-v2 doit être refusé.");
        Equal(LauncherStartupMode.UiV2Preview, App.ResolveStartupMode(["--ui-v2", "--preview-state=Ready"]), "preview-state doit rester inchangé.");
        Equal(LauncherStartupMode.Legacy, App.ResolveStartupMode([]), "Le lancement sans argument doit rester legacy.");

        Dictionary<string, AuthPreviewScenario> scenarios = new(StringComparer.OrdinalIgnoreCase)
        {
            ["--preview-auth"] = AuthPreviewScenario.Login,
            ["--preview-auth=login"] = AuthPreviewScenario.Login,
            ["--preview-auth=register"] = AuthPreviewScenario.Register,
            ["--preview-auth=loading"] = AuthPreviewScenario.Loading,
            ["--preview-auth=login-error"] = AuthPreviewScenario.LoginError,
            ["--preview-auth=register-error"] = AuthPreviewScenario.RegisterError,
            ["--preview-auth=register-validation"] = AuthPreviewScenario.RegisterValidation,
            ["--preview-auth=email-warning"] = AuthPreviewScenario.EmailWarning,
            ["--preview-auth=service-unavailable"] = AuthPreviewScenario.ServiceUnavailable,
            ["--preview-auth=atlas-enrollment"] = AuthPreviewScenario.AtlasEnrollment,
            ["--preview-auth=atlas-enrollment-error"] = AuthPreviewScenario.AtlasEnrollmentError
        };

        foreach ((string argument, AuthPreviewScenario expected) in scenarios)
        {
            Equal(expected, AuthPreviewArguments.ResolveScenario([argument]), $"Routage incorrect pour {argument}.");
        }
    }

    private static void KeepPreviewPresentationIsolated()
    {
        Type[] previewTypes =
        [
            typeof(AuthUiState),
            typeof(AuthOverlayViewV2),
            typeof(ShellOverlayCoordinator),
            typeof(AuthPreviewArguments)
        ];
        Type[] forbiddenTypes =
        [
            typeof(LauncherRuntime),
            typeof(LauncherAuthService),
            typeof(HttpClient),
            typeof(Process),
            typeof(System.Threading.Timer),
            typeof(PeriodicTimer),
            typeof(DispatcherTimer)
        ];

        foreach (Type previewType in previewTypes)
        {
            FieldInfo[] fields = previewType.GetFields(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (FieldInfo field in fields)
            {
                True(
                    forbiddenTypes.All(forbidden => !forbidden.IsAssignableFrom(field.FieldType)),
                    $"{previewType.Name} ne doit conserver aucune dépendance métier ({field.Name}).");
            }
        }

        using AuthUiState state = LauncherV2PreviewData.CreateAuth(AuthPreviewScenario.Login);
        True(state.IsOpen, "Le preview Login doit être ouvert sans session.");
        True(state.SubmitCommand.GetType().Assembly == typeof(AuthUiState).Assembly, "La commande doit rester locale au launcher.");
    }

    private static void CharacterizePresentationStatesAndCommands()
    {
        using AuthUiState state = LauncherV2PreviewData.CreateAuth(AuthPreviewScenario.Login);
        state.LoginUsername = "Dono1402";
        state.SetFormValidity(true);
        True(state.SubmitCommand.CanExecute(null), "La soumission fictive complète doit être active.");
        state.SubmitCommand.Execute(null);
        Equal(1, state.PreviewSubmissionCount, "La commande fictive ne doit compter qu'un envoi.");

        state.ShowRegisterCommand.Execute(null);
        Equal(AuthMode.Register, state.Mode, "Connexion vers Inscription doit fonctionner.");
        Equal("Dono1402", state.RegisterUsername, "Le legacy recopie le nom lors du passage à l'inscription.");
        state.ShowLoginCommand.Execute(null);
        Equal(AuthMode.Login, state.Mode, "Le retour à Connexion doit fonctionner.");

        using AuthUiState loading = LauncherV2PreviewData.CreateAuth(AuthPreviewScenario.Loading);
        loading.SetFormValidity(true);
        True(!loading.SubmitCommand.CanExecute(null), "Loading doit interdire tout double envoi.");
        loading.SubmitCommand.Execute(null);
        loading.SubmitCommand.Execute(null);
        Equal(0, loading.PreviewSubmissionCount, "Loading ne doit accepter aucune validation.");

        using AuthUiState warning = LauncherV2PreviewData.CreateAuth(AuthPreviewScenario.EmailWarning);
        warning.SetFormValidity(true);
        True(warning.IsEmailWarningVisible, "L'avertissement e-mail doit être visible.");
        True(warning.SubmitCommand.CanExecute(null), "L'e-mail non vérifié ne doit pas bloquer l'action.");

        using AuthUiState registrationError = LauncherV2PreviewData.CreateAuth(AuthPreviewScenario.RegisterError);
        Equal(AuthErrorKind.RegistrationRejected, registrationError.ErrorKind, "L'inscription refusée doit être distincte d'une validation locale.");
        True(!registrationError.ErrorMessage.Contains("http", StringComparison.OrdinalIgnoreCase), "L'erreur ne doit exposer aucune URL.");

        using AuthUiState unavailable = LauncherV2PreviewData.CreateAuth(AuthPreviewScenario.ServiceUnavailable);
        Equal(AuthErrorKind.ServiceUnavailable, unavailable.ErrorKind, "L'indisponibilité doit rester distincte des identifiants incorrects.");
        True(!unavailable.ErrorMessage.Contains("exception", StringComparison.OrdinalIgnoreCase), "Aucune exception ne doit être affichée.");

        using AuthUiState enrollment = LauncherV2PreviewData.CreateAuth(AuthPreviewScenario.AtlasEnrollment);
        Equal(AuthMode.EnrollmentPrompt, enrollment.Mode, "Le preview doit ouvrir l'état d'enrolement dedie.");
        Equal(AuthErrorKind.None, enrollment.ErrorKind, "L'enrolement requis ne doit pas etre une erreur rouge.");
        True(enrollment.BeginEnrollmentCommand.CanExecute(null), "L'activation explicite doit etre disponible.");
        enrollment.BeginEnrollmentCommand.Execute(null);
        Equal(AuthMode.Enrollment, enrollment.Mode, "Activer Atlas doit ouvrir le formulaire dedie.");
        Equal("Dono1402", enrollment.EnrollmentUsername, "Le nom saisi doit etre reutilise.");
        enrollment.ReturnCommand.Execute(null);
        Equal(AuthMode.EnrollmentPrompt, enrollment.Mode, "Retour depuis le formulaire doit revenir a l'explication.");
        enrollment.ReturnCommand.Execute(null);
        Equal(AuthMode.Login, enrollment.Mode, "Retour depuis l'explication doit revenir a la connexion.");

        using AuthUiState enrollmentError = LauncherV2PreviewData.CreateAuth(AuthPreviewScenario.AtlasEnrollmentError);
        Equal(AuthMode.Enrollment, enrollmentError.Mode, "L'erreur preview doit rester dans le formulaire d'activation.");
        Equal(AuthErrorKind.EmailAlreadyExists, enrollmentError.ErrorKind, "L'e-mail utilise doit etre distingue.");
    }

    private static async Task ValidateWpfInteractionResponsiveLayoutAndCapturesAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasAuthPreviewWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static void RunWpfHarness(TaskCompletionSource completion, string? captureDirectory)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        dispatcher.UnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        };

        _ = RunAsync();
        Dispatcher.Run();
        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }

        async Task RunAsync()
        {
            Application? application = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                await ValidateKeyboardLifecycleAndOverlayExclusionAsync();
                await ValidateEnrollmentPreviewInteractionAsync();
                await ValidateAllRequestedLayoutsAndCaptureAsync(captureDirectory);
                await ValidateMaximizedLayoutAsync();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
            finally
            {
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateKeyboardLifecycleAndOverlayExclusionAsync()
    {
        LauncherShellV2 window = CreateWindow(AuthPreviewScenario.Login, 1080, 680, activate: true);
        AuthUiState state = window.AuthState;
        AuthOverlayViewV2 overlay = window.AuthenticationOverlay;
        try
        {
            await ShowAndSettleAsync(window);
            RecordDpi(window);
            TextBox username = Required<TextBox>(overlay, "LoginUsernameBox");
            PasswordBox password = Required<PasswordBox>(overlay, "LoginPasswordBox");
            Button profile = Required<Button>(window, "ProfileButton");
            Button close = Required<Button>(overlay, "CloseButton");

            Style expectedFieldFocus = (Style)Application.Current.FindResource("AtlasV2.FocusVisual.Field");
            Equal(expectedFieldFocus, username.FocusVisualStyle, "Le champ doit utiliser le focus visuel cyan du design system.");
            True(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(username)), "Le champ doit exposer un nom accessible.");
            True(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(close)), "Fermer doit exposer un nom accessible.");

            overlay.FocusFirstControl();
            await PumpAsync(DispatcherPriority.Input);
            Equal(username, Keyboard.FocusedElement, "Le focus initial doit viser le nom d'utilisateur.");

            username.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Equal(password, Keyboard.FocusedElement, "Tab doit avancer vers le mot de passe.");
            password.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            Equal(username, Keyboard.FocusedElement, "Shift+Tab doit revenir au nom d'utilisateur.");
            close.Focus();
            close.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            True(overlay.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject), "Le cycle Tab ne doit pas sortir de l'overlay.");

            int beforeEnter = state.PreviewSubmissionCount;
            RaisePreviewKey(overlay, Key.Enter);
            Equal(beforeEnter + 1, state.PreviewSubmissionCount, "Entrée doit valider exactement une fois.");

            state.ShowRegisterCommand.Execute(null);
            await PumpAsync(DispatcherPriority.Input);
            Equal(AuthMode.Register, state.Mode, "Le sélecteur doit ouvrir l'inscription.");
            Equal(Required<TextBox>(overlay, "RegisterUsernameBox"), Keyboard.FocusedElement, "Le focus doit suivre le changement de formulaire.");
            state.ShowLoginCommand.Execute(null);
            await PumpAsync(DispatcherPriority.Input);
            Equal(AuthMode.Login, state.Mode, "Le sélecteur doit revenir à la connexion.");

            window.SetFriendsDrawerOpenForPreview();
            True(!window.FriendsState.IsOpen, "Les amis doivent être refusés pendant l'authentification.");
            Equal(ShellOverlayKind.Authentication, window.CurrentOverlay, "L'authentification doit rester l'unique overlay.");

            RaisePreviewKey(overlay, Key.Escape);
            await DelayAndPumpAsync(220);
            True(overlay.IsFullyClosed, "Échap doit fermer et retirer le voile du hit-test.");
            True(overlay.ArePasswordFieldsEmpty, "Tous les PasswordBox doivent être vidés à la fermeture.");
            Equal(string.Empty, state.LoginUsername, "Le nom fictif doit être vidé à la fermeture complète.");
            IInputElement? returnedFocus = Keyboard.FocusedElement
                ?? FocusManager.GetFocusedElement(window);
            Equal(profile, returnedFocus, "Le focus doit revenir au bouton Compte.");

            window.SetFriendsDrawerOpenForPreview();
            await DelayAndPumpAsync(220);
            True(window.FriendsState.IsOpen, "Le drawer Amis doit pouvoir s'ouvrir après la fermeture de l'auth.");
            window.OpenAuthenticationForPreview(AuthPreviewScenario.Login);
            await DelayAndPumpAsync(220);
            True(state.IsOpen && !window.FriendsState.IsOpen, "Ouvrir l'auth doit fermer les amis.");
            True(window.FriendsOverlay.Visibility == Visibility.Collapsed, "Aucun second voile ne doit rester visible.");

            state.IsOpen = false;
            window.OpenAuthenticationForPreview(AuthPreviewScenario.Login);
            state.IsOpen = false;
            window.OpenAuthenticationForPreview(AuthPreviewScenario.Login);
            await DelayAndPumpAsync(220);
            True(state.IsOpen && overlay.Visibility == Visibility.Visible && overlay.IsHitTestVisible, "Des animations opposées ne doivent pas laisser un état intermédiaire.");
        }
        finally
        {
            AuthOverlayViewV2 retainedOverlay = overlay;
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
            state.IsOpen = true;
            await PumpAsync(DispatcherPriority.DataBind);
            True(retainedOverlay.IsFullyClosed, "Aucune modification WPF ne doit survenir après la fermeture de la fenêtre.");
        }
    }

    private static async Task ValidateEnrollmentPreviewInteractionAsync()
    {
        LauncherShellV2 window = CreateWindow(
            AuthPreviewScenario.AtlasEnrollment,
            1440,
            860,
            activate: true);
        try
        {
            await ShowAndSettleAsync(window);
            AuthOverlayViewV2 overlay = window.AuthenticationOverlay;
            Button begin = Required<Button>(overlay, "BeginEnrollmentButton");
            Equal(AuthMode.EnrollmentPrompt, window.AuthState.Mode, "Le preview doit afficher l'explication dediee.");
            overlay.FocusFirstControl();
            await PumpAsync(DispatcherPriority.Input);
            Equal(begin, Keyboard.FocusedElement, "Le focus initial du parcours doit viser Activer Atlas.");

            begin.Command.Execute(begin.CommandParameter);
            await PumpAsync(DispatcherPriority.Input);
            Equal(AuthMode.Enrollment, window.AuthState.Mode, "Activer Atlas doit ouvrir le formulaire preview.");
            TextBox username = Required<TextBox>(overlay, "EnrollmentUsernameBox");
            TextBox email = Required<TextBox>(overlay, "EnrollmentEmailBox");
            PasswordBox password = Required<PasswordBox>(overlay, "EnrollmentPasswordBox");
            True(username.IsReadOnly && !username.IsTabStop, "Le nom deja valide doit etre en lecture seule.");
            Equal(email, Keyboard.FocusedElement, "Le focus doit viser l'e-mail.");

            email.Text = "preview@example.test";
            password.Password = "preview-password";
            overlay.ValidateForPreview(showErrors: true);
            True(window.AuthState.SubmitCommand.CanExecute(null), "Le formulaire preview complet doit etre validable.");
            window.AuthState.ReturnCommand.Execute(null);
            await PumpAsync(DispatcherPriority.Input);
            Equal(AuthMode.EnrollmentPrompt, window.AuthState.Mode, "Retour doit revenir a l'explication.");

            RaisePreviewKey(overlay, Key.Escape);
            await DelayAndPumpAsync(220);
            True(overlay.IsFullyClosed, "Echap doit fermer le parcours d'enrolement.");
            True(overlay.ArePasswordFieldsEmpty, "Le mot de passe d'enrolement doit etre efface.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateAllRequestedLayoutsAndCaptureAsync(string? captureDirectory)
    {
        (string FileName, AuthPreviewScenario Scenario, int Width, int Height)[] cases =
        [
            ("01-connexion-1080x680.png", AuthPreviewScenario.Login, 1080, 680),
            ("02-inscription-1440x860.png", AuthPreviewScenario.Register, 1440, 860),
            ("03-connexion-chargement-1440x860.png", AuthPreviewScenario.Loading, 1440, 860),
            ("04-identifiants-incorrects-1440x860.png", AuthPreviewScenario.LoginError, 1440, 860),
            ("05-email-non-verifie-1920x1080.png", AuthPreviewScenario.EmailWarning, 1920, 1080),
            ("06-validation-inscription-1080x680.png", AuthPreviewScenario.RegisterValidation, 1080, 680),
            ("07-activation-atlas-1440x860.png", AuthPreviewScenario.AtlasEnrollment, 1440, 860),
            ("08-activation-atlas-erreur-1440x860.png", AuthPreviewScenario.AtlasEnrollmentError, 1440, 860)
        ];

        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
        }

        foreach ((string fileName, AuthPreviewScenario scenario, int width, int height) in cases)
        {
            LauncherShellV2 window = CreateWindow(scenario, width, height, activate: false);
            try
            {
                await ShowAndSettleAsync(window);
                RecordDpi(window);
                ValidateLayout(window, width, height, scenario);
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    SavePng(window, Path.Combine(captureDirectory, fileName));
                }
            }
            finally
            {
                window.Close();
                await PumpAsync(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateMaximizedLayoutAsync()
    {
        LauncherShellV2 window = CreateWindow(AuthPreviewScenario.Login, 1440, 860, activate: false);
        try
        {
            await ShowAndSettleAsync(window);
            window.WindowState = WindowState.Maximized;
            await DelayAndPumpAsync(120);
            Border card = Required<Border>(window.AuthenticationOverlay, "AuthCard");
            Button close = Required<Button>(window, "CloseWindowButton");
            True(card.ActualWidth <= 500.5, "La carte ne doit pas s'étirer en fenêtre maximisée.");
            True(close.IsVisible && close.IsHitTestVisible, "Fermer doit rester accessible en fenêtre maximisée.");
            AssertCentered(window, card);
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static LauncherShellV2 CreateWindow(
        AuthPreviewScenario scenario,
        int width,
        int height,
        bool activate)
    {
        LauncherShellV2 window = new(GamePreviewScenario.Ready, scenario)
        {
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = activate
        };
        True(
            !window.HasRealAuthenticationAttached,
            "Le preview-auth ne doit jamais appeler ou attacher les usines d'authentification réelles.");
        return window;
    }

    private static async Task ShowAndSettleAsync(LauncherShellV2 window)
    {
        window.Show();
        if (window.ShowActivated)
        {
            window.Activate();
        }

        await DelayAndPumpAsync(220);
        window.UpdateLayout();
    }

    private static void ValidateLayout(
        LauncherShellV2 window,
        int expectedWidth,
        int expectedHeight,
        AuthPreviewScenario scenario)
    {
        AuthOverlayViewV2 overlay = window.AuthenticationOverlay;
        Border card = Required<Border>(overlay, "AuthCard");
        Border errorBanner = Required<Border>(overlay, "ErrorBanner");
        ScrollViewer scroll = Required<ScrollViewer>(overlay, "AuthScrollViewer");
        Button primary = Required<Button>(overlay, "PrimaryAuthButton");
        Button overlayClose = Required<Button>(overlay, "CloseButton");
        Button minimize = Required<Button>(window, "MinimizeWindowButton");
        Button maximize = Required<Button>(window, "MaximizeWindowButton");
        Button windowClose = Required<Button>(window, "CloseWindowButton");

        Equal((double)expectedWidth, window.ActualWidth, "La largeur WPF demandée doit être respectée.");
        Equal((double)expectedHeight, window.ActualHeight, "La hauteur WPF demandée doit être respectée.");
        True(card.ActualWidth is >= 420 and <= 501, "La carte doit rester entre 420 et 500 DIPs.");
        True(card.ActualHeight <= window.ActualHeight - 64 - 24, "La carte ne doit pas être coupée par le viewport.");
        Button beginEnrollment = Required<Button>(overlay, "BeginEnrollmentButton");
        bool enrollmentPrompt = scenario == AuthPreviewScenario.AtlasEnrollment;
        True(
            enrollmentPrompt
                ? beginEnrollment.IsVisible && beginEnrollment.ActualHeight >= 43
                : primary.IsVisible && primary.ActualHeight >= 45,
            "L'action principale du scenario doit rester visible.");
        True(overlayClose.IsVisible && overlayClose.IsHitTestVisible, "La fermeture de l'overlay doit rester accessible.");
        True(minimize.IsVisible && maximize.IsVisible && windowClose.IsVisible, "Les commandes de fenêtre doivent rester visibles.");
        True(scroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed, "Aucune barre horizontale ne doit apparaître.");
        True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "Le contenu ne doit pas déborder horizontalement.");
        AssertCentered(window, card);

        FrameworkElement visiblePrimary = enrollmentPrompt ? beginEnrollment : primary;
        GeneralTransform primaryTransform = visiblePrimary.TransformToAncestor(card);
        Rect primaryBounds = primaryTransform.TransformBounds(new Rect(visiblePrimary.RenderSize));
        True(
            primaryBounds.Top >= -0.5 && primaryBounds.Bottom <= card.ActualHeight + 0.5,
            $"L'action principale doit être visible sans défilement initial ({scenario}, "
            + $"primary={primaryBounds.Top:F1}-{primaryBounds.Bottom:F1}, card={card.ActualHeight:F1}, "
            + $"viewport={scroll.ViewportHeight:F1}, extent={scroll.ExtentHeight:F1}).");

        if (scenario == AuthPreviewScenario.Loading)
        {
            True(!window.AuthState.SubmitCommand.CanExecute(null), "Le bouton Loading doit rester désactivé.");
            Equal("Connexion…", window.AuthState.PrimaryActionLabel, "Le libellé Loading est incorrect.");
        }

        if (scenario == AuthPreviewScenario.EmailWarning)
        {
            True(window.AuthState.IsEmailWarningVisible, "La bannière e-mail doit être présente.");
            True(window.AuthState.SubmitCommand.CanExecute(null), "La bannière e-mail ne doit pas bloquer la connexion.");
        }

        if (scenario == AuthPreviewScenario.RegisterValidation)
        {
            Equal(AuthErrorKind.Validation, window.AuthState.ErrorKind, "L'erreur de validation doit rester distincte.");
        }

        if (scenario == AuthPreviewScenario.AtlasEnrollmentError)
        {
            Equal(AuthErrorKind.EmailAlreadyExists, window.AuthState.ErrorKind, "L'erreur d'enrolement doit rester distincte.");
            True(errorBanner.IsVisible, "La bannière d'erreur d'enrolement doit être visible dans le preview WPF.");
        }
    }

    private static void AssertCentered(Window window, FrameworkElement element)
    {
        Point origin = element.TransformToAncestor(window).Transform(new Point(0, 0));
        double elementCenter = origin.X + element.ActualWidth / 2;
        double windowCenter = window.ActualWidth / 2;
        True(Math.Abs(elementCenter - windowCenter) <= 2, "La carte doit rester centrée horizontalement.");
    }

    private static void RecordDpi(Window window)
    {
        uint dpi = GetDpiForWindow(new WindowInteropHelper(window).Handle);
        if (_observedWindowDpi == 0)
        {
            _observedWindowDpi = dpi;
        }
        else
        {
            Equal(_observedWindowDpi, dpi, "Toutes les captures doivent utiliser la même session DPI réelle.");
        }
    }

    private static void SavePng(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        int width = Math.Max(1, (int)Math.Round(visual.ActualWidth));
        int height = Math.Max(1, (int)Math.Round(visual.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static void RaisePreviewKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("La source WPF du contrôle est absente.");
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(args);
    }

    private static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);
    }

    private static AuthFormValidation Register(
        string username,
        string email,
        int passwordLength,
        bool hasConfirmation,
        bool passwordsMatch)
    {
        return AuthPreviewValidation.Register(
            username,
            email,
            passwordLength,
            hasConfirmation,
            passwordsMatch);
    }

    private static T Required<T>(FrameworkElement scope, string name)
        where T : FrameworkElement
    {
        return scope.FindName(name) as T
            ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");
    }

    private static void LoadV2Resources(Application application)
    {
        string[] resourcePaths =
        [
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        ];
        foreach (string path in resourcePaths)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(path, UriKind.Relative)
            });
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; actuel={actual}.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
