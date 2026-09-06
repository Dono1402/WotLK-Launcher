using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static class AccountSecuritySessionWpfTests
{
    private const string CurrentSessionId = "wpf-current";
    private const string OtherSessionId = "wpf-other";

    internal static async Task RunAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = Signal();
        Thread thread = new(() => RunHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasAccountSecuritySessionWpf"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(75));
    }

    private static void RunHarness(TaskCompletionSource completion, string? captureDirectory)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(dispatcher));
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
            LauncherShellV2? window = null;
            CancellationTokenSource? lifetime = null;
            LauncherSessionCoordinator? session = null;
            LauncherOperationCoordinator? operations = null;
            AvatarImageCache? cache = null;
            LauncherAccountCoordinator? account = null;
            AccountStateAdapter? accountAdapter = null;
            AuthStateAdapter? authAdapter = null;
            AccountCommands? commands = null;
            string cacheRoot = AccountAvatarClientTests.NewRoot("security-session-wpf");
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                lifetime = new CancellationTokenSource();
                LauncherProfile profile = FakeLauncherAuthService.CreateProfile(
                    "Dono1402",
                    "dono-unverified@example.test",
                    emailVerified: false);
                FakeLauncherAuthService authentication = new()
                {
                    Session = FakeLauncherAuthService.CreateSession(
                        profile.Username,
                        profile.Email,
                        profile.EmailVerified),
                    RestoreResult = true,
                    EnsureFreshHandler = _ => Task.FromResult(true),
                    SessionsHandler = _ => Task.FromResult<IReadOnlyList<LauncherDeviceSession>>(
                    [
                        DeviceSession(CurrentSessionId, "Atlas Launcher - Ce PC", current: true),
                        DeviceSession(OtherSessionId, "Atlas Launcher - Portable", current: false)
                    ])
                };
                session = new LauncherSessionCoordinator(authentication, lifetime.Token, _ => { });
                Equal(
                    LauncherSessionRestoreStatus.Restored,
                    (await session.RestoreOnceAsync()).Status,
                    "Le harnais WPF requiert une session restaurée.");
                operations = new LauncherOperationCoordinator();
                StubAvatarMediaClient media = new()
                {
                    ProfileResult = new AvatarProfileReadResult(profile, SupportsProfilePhotos: true)
                };
                cache = new AvatarImageCache(media, cacheRoot, lifetime.Token);
                account = new LauncherAccountCoordinator(
                    session,
                    authentication,
                    operations,
                    media,
                    cache,
                    () => authentication.Session?.Profile,
                    _ => { });
                AccountUiState accountState = new(
                    AccountStateAdapter.Project(account.CurrentSnapshot, avatarImage: null));
                AvatarCropUiState cropState = new(AvatarCropUiState.Empty.Current);
                ShellUiState shellState = LauncherV2PreviewData.CreateShell(
                    GamePreviewScenario.Ready,
                    isAuthenticated: true);
                ProfileUiState profileState = LauncherV2PreviewData.CreateProfile(
                    ProfilePreviewScenario.SignedIn);
                GameUiState gameState = LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready);
                window = new LauncherShellV2(
                    shellState,
                    gameState,
                    LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
                    LauncherV2PreviewData.CreateFriends(),
                    profileState,
                    LauncherV2PreviewData.CreateSettings(),
                    accountState,
                    cropState)
                {
                    Width = 1440,
                    Height = 860,
                    Left = -20000,
                    Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                accountAdapter = new AccountStateAdapter(
                    accountState,
                    cropState,
                    shellState,
                    profileState,
                    account,
                    cache,
                    dispatcher);
                authAdapter = new AuthStateAdapter(
                    window.AuthState,
                    shellState,
                    gameState,
                    session,
                    dispatcher);
                commands = new AccountCommands(
                    account,
                    accountState,
                    cropState,
                    new AvatarFileSelectionService(new NullAvatarPicker()),
                    dispatcher);
                window.AttachAccount(commands);
                window.Show();
                await PumpAsync(DispatcherPriority.Loaded);
                DpiScale dpi = VisualTreeHelper.GetDpi(window);
                True(Math.Abs(dpi.PixelsPerInchX - 120) <= 0.5,
                    "Le test WPF 03A.5 doit provenir de la session Windows réelle à 125 %.");
                True(Math.Abs(window.ActualWidth - 1440) <= 0.5
                    && Math.Abs(window.ActualHeight - 860) <= 0.5,
                    "La capture Sécurité doit utiliser 1440 x 860 DIPs.");

                RaiseClick(Required<Button>(window, "ProfileButton"));
                await PumpAsync(DispatcherPriority.Input);
                RaiseClick(Required<Button>(window.ProfileOverlay, "ManageAccountButton"));
                await WaitUntilAsync(
                    () => window.CurrentPage == LauncherShellPage.Account
                        && accountState.Current.SessionsState == AccountSessionsViewState.Loaded
                        && window.ProfileOverlay.IsFullyClosed,
                    "La page Compte réelle doit charger les sessions Atlas.");

                RaiseClick(Required<Button>(window.AccountPage, "SecurityTabButton"));
                await DelayAndPumpAsync(100);
                Equal(AccountSection.Security, window.AccountPage.SelectedSection,
                    "L'onglet Sécurité réel doit être accessible.");
                TextBlock emailStatus = Required<TextBlock>(window.AccountPage, "SecurityEmailStatus");
                TextBlock emailAddress = Required<TextBlock>(window.AccountPage, "SecurityEmailAddress");
                Equal("(Non vérifiée)", emailStatus.Text,
                    "Le statut e-mail doit venir du profil réel.");
                Point addressPosition = emailAddress.TranslatePoint(new Point(), window.AccountPage);
                Point statusPosition = emailStatus.TranslatePoint(new Point(), window.AccountPage);
                double emailStatusGap = statusPosition.X - addressPosition.X - emailAddress.ActualWidth;
                True(emailStatusGap >= 0 && emailStatusGap <= 12,
                    "Le statut doit suivre directement l'adresse e-mail avec un petit espacement.");
                True(Math.Abs(statusPosition.Y + emailStatus.ActualHeight / 2
                        - addressPosition.Y - emailAddress.ActualHeight / 2) <= 1,
                    "L'adresse et son statut doivent rester centrés sur la même ligne.");
                True(Required<Button>(window.AccountPage, "ResendVerificationButton").IsEnabled,
                    "Le renvoi doit être actif pour une adresse non vérifiée.");
                TextBlock twoFactor = FindVisuals<TextBlock>(Required<StackPanel>(window.AccountPage, "SecurityPanel"))
                    .Single(text => string.Equals(text.Text, "À venir", StringComparison.Ordinal));
                True(twoFactor.IsVisible && !twoFactor.Focusable
                        && !FindButtons(VisualTreeHelper.GetParent(twoFactor)).Any(),
                    "La double authentification doit rester signalée À venir sans action cliquable.");
                SaveCapture(window, captureDirectory, "00-account-security-unverified-1440x860.png");

                RaiseClick(Required<Button>(window.AccountPage, "ModifyEmailButton"));
                await PumpAsync(DispatcherPriority.Input);
                True(accountState.Current.IsEmailEditorOpen
                    && window.AccountPage.ContainsSensitiveEditorFocus(
                        Keyboard.FocusedElement as DependencyObject),
                    "Le formulaire e-mail réel doit s'ouvrir avec son focus modal.");
                TextBox newEmail = Required<TextBox>(window.AccountPage, "NewEmailBox");
                Equal(string.Empty, newEmail.Text,
                    "La nouvelle adresse e-mail doit commencer vide.");
                True(Required<Border>(window.AccountPage, "NewEmailField").BorderThickness.Left >= 1,
                    "Le champ e-mail doit afficher un rectangle de saisie explicite.");
                RaiseMouseDown(Required<Grid>(window.AccountPage, "EmailEditorLayer"));
                await PumpAsync(DispatcherPriority.Input);
                False(accountState.Current.IsEmailEditorOpen,
                    "Un clic hors du formulaire e-mail doit le fermer.");

                RaiseClick(Required<Button>(window.AccountPage, "ModifyEmailButton"));
                await PumpAsync(DispatcherPriority.Input);
                window.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                });
                await PumpAsync(DispatcherPriority.Input);
                False(accountState.Current.IsEmailEditorOpen,
                    "Échap doit fermer le formulaire e-mail.");

                RaiseClick(Required<Button>(window.AccountPage, "ModifyPasswordButton"));
                await PumpAsync(DispatcherPriority.Input);
                True(accountState.Current.IsPasswordEditorOpen,
                    "Le formulaire mot de passe doit s'ouvrir dans AccountViewV2.");
                True(window.AccountPage.ContainsSensitiveEditorFocus(
                        Keyboard.FocusedElement as DependencyObject),
                    "Le focus doit entrer dans le formulaire sensible.");
                PasswordBox currentPassword = Required<PasswordBox>(window.AccountPage, "CurrentPasswordBoxV2");
                PasswordBox newPassword = Required<PasswordBox>(window.AccountPage, "NewPasswordBoxV2");
                PasswordBox confirmation = Required<PasswordBox>(window.AccountPage, "ConfirmPasswordBoxV2");
                True(Required<Border>(window.AccountPage, "CurrentPasswordField").BorderThickness.Left >= 1
                     && Required<Border>(window.AccountPage, "NewPasswordField").BorderThickness.Left >= 1
                     && Required<Border>(window.AccountPage, "ConfirmPasswordField").BorderThickness.Left >= 1,
                    "Les trois mots de passe doivent afficher des rectangles de saisie explicites.");
                RaiseMouseDown(Required<Grid>(window.AccountPage, "PasswordEditorLayer"));
                await PumpAsync(DispatcherPriority.Input);
                False(accountState.Current.IsPasswordEditorOpen,
                    "Un clic hors du formulaire mot de passe doit le fermer.");

                RaiseClick(Required<Button>(window.AccountPage, "ModifyPasswordButton"));
                await PumpAsync(DispatcherPriority.Input);
                currentPassword.Password = "CurrentSecret-03A5";
                newPassword.Password = "ReplacementSecret-03A5";
                confirmation.Password = "ReplacementSecret-03A5";

                Required<Button>(window, "GameNavigationButton").Focus();
                await PumpAsync(DispatcherPriority.Input);
                True(window.AccountPage.ContainsSensitiveEditorFocus(
                        Keyboard.FocusedElement as DependencyObject),
                    "Le focus ne doit pas sortir du formulaire sensible.");

                TaskCompletionSource passwordEntered = Signal();
                TaskCompletionSource passwordRelease = Signal();
                authentication.ChangePasswordHandler = async (_, _, _) =>
                {
                    passwordEntered.TrySetResult();
                    await passwordRelease.Task;
                };
                RaiseClick(Required<Button>(window.AccountPage, "ConfirmPasswordChangeButton"));
                await passwordEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => accountState.Current.AccountOperation
                        == AccountOperationViewState.ChangingPassword,
                    "Le formulaire doit afficher son état occupé.");
                False(currentPassword.IsEnabled || newPassword.IsEnabled || confirmation.IsEnabled,
                    "Les champs secrets doivent être désactivés pendant la requête.");
                False(Required<Button>(window.AccountPage, "ConfirmPasswordChangeButton").IsEnabled,
                    "Une deuxième validation doit être impossible.");
                Equal(Visibility.Visible,
                    Required<StackPanel>(window.AccountPage, "PasswordBusyPanel").Visibility,
                    "L'indicateur de chargement mot de passe doit être visible.");
                SaveCapture(window, captureDirectory, "01-account-security-password-busy-1440x860.png");
                passwordRelease.TrySetResult();
                await WaitUntilAsync(
                    () => accountState.Current.AccountNotice == AccountNoticeViewState.PasswordChanged
                        && !accountState.Current.IsPasswordEditorOpen,
                    "Le succès doit fermer le formulaire et publier sa notification.");
                Equal(string.Empty, currentPassword.Password,
                    "Le mot de passe actuel doit être effacé après succès.");
                Equal(string.Empty, newPassword.Password,
                    "Le nouveau mot de passe doit être effacé après succès.");
                Equal(string.Empty, confirmation.Password,
                    "La confirmation doit être effacée après succès.");

                RaiseClick(Required<Button>(window.AccountPage, "ModifyPasswordButton"));
                authentication.ChangePasswordHandler = (_, _, _) => Task.FromException(
                    new LauncherAuthException("raw credential failure", System.Net.HttpStatusCode.Unauthorized));
                authentication.RefreshProfileHandler = _ => Task.FromResult(profile);
                currentPassword.Password = "WrongCurrentSecret";
                newPassword.Password = "ReplacementSecret-03A5";
                confirmation.Password = "ReplacementSecret-03A5";
                RaiseClick(Required<Button>(window.AccountPage, "ConfirmPasswordChangeButton"));
                await WaitUntilAsync(
                    () => accountState.Current.AccountErrorMessage
                        == "Le mot de passe actuel est incorrect.",
                    "L'erreur d'identifiants doit rester courte et structurée.");
                Equal(Visibility.Visible,
                    Required<Border>(window.AccountPage, "PasswordEditorErrorBanner").Visibility,
                    "L'erreur mot de passe doit rester dans le formulaire.");
                True(string.IsNullOrEmpty(currentPassword.Password)
                    && string.IsNullOrEmpty(newPassword.Password)
                    && string.IsNullOrEmpty(confirmation.Password),
                    "Une erreur d'identifiants doit effacer tous les PasswordBox.");
                SaveCapture(window, captureDirectory, "01b-account-security-password-error-1440x860.png");

                currentPassword.Password = "TemporaryCurrent";
                newPassword.Password = "TemporaryReplacement";
                confirmation.Password = "TemporaryReplacement";
                RaiseClick(Required<Button>(window.AccountPage, "SessionsTabButton"));
                await PumpAsync(DispatcherPriority.Input);
                False(accountState.Current.IsPasswordEditorOpen,
                    "Changer d'onglet doit fermer le formulaire mot de passe.");
                True(string.IsNullOrEmpty(currentPassword.Password)
                    && string.IsNullOrEmpty(newPassword.Password)
                    && string.IsNullOrEmpty(confirmation.Password),
                    "Changer de vue doit effacer tous les PasswordBox.");

                Equal(AccountSection.Sessions, window.AccountPage.SelectedSection,
                    "L'onglet Sessions réel doit être accessible.");
                Equal(Visibility.Collapsed,
                    Required<StackPanel>(window.AccountPage, "SessionsPreviewList").Visibility,
                    "Les lieux fictifs doivent rester exclus du mode réel.");
                Equal(2, accountState.Current.Sessions.Length,
                    "Les deux sessions réelles doivent être affichées.");
                AccountSessionViewState current = accountState.Current.Sessions.Single(item => item.IsCurrent);
                False(current.CanRevoke,
                    "La session courante ne doit pas proposer de révocation individuelle.");
                Button revoke = FindButtons(window.AccountPage)
                    .Single(button => string.Equals(button.Tag as string, OtherSessionId, StringComparison.Ordinal));
                True(revoke.IsEnabled,
                    "Une autre session doit pouvoir être déconnectée.");
                TextBlock disconnectOthers = FindVisuals<TextBlock>(Required<StackPanel>(window.AccountPage, "SessionsPanel"))
                    .Single(text => string.Equals(text.Text, "Déconnexion groupée · À venir", StringComparison.Ordinal));
                True(disconnectOthers.IsVisible && !disconnectOthers.Focusable
                        && !FindButtons(VisualTreeHelper.GetParent(disconnectOthers)).Any(),
                    "La déconnexion groupée doit rester signalée À venir sans action cliquable.");

                TaskCompletionSource revokeEntered = Signal();
                TaskCompletionSource revokeRelease = Signal();
                authentication.RevokeSessionHandler = async (id, _) =>
                {
                    Equal(OtherSessionId, id,
                        "Le bouton doit cibler uniquement la session choisie.");
                    revokeEntered.TrySetResult();
                    await revokeRelease.Task;
                };
                RaiseClick(revoke);
                await revokeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => accountState.Current.Sessions.Any(item =>
                        item.Id == OtherSessionId && item.IsRevoking),
                    "La session ciblée doit seule afficher Déconnexion.");
                window.Width = 1080;
                window.Height = 680;
                await DelayAndPumpAsync(150);
                True(Math.Abs(window.ActualWidth - 1080) <= 0.5
                    && Math.Abs(window.ActualHeight - 680) <= 0.5,
                    "La capture Sessions doit utiliser 1080 x 680 DIPs.");
                True(window.AccountPage.ScrollHost.ScrollableWidth <= 0.5,
                    "La page Sessions ne doit pas déborder horizontalement à 1080 x 680.");
                SaveCapture(window, captureDirectory, "02-account-sessions-revoking-1080x680.png");
                revokeRelease.TrySetResult();
                await WaitUntilAsync(
                    () => accountState.Current.Sessions.Length == 1
                        && accountState.Current.AccountNotice
                            == AccountNoticeViewState.SessionRevoked,
                    "La réussite doit retirer uniquement l'autre session.");

                window.Close();
                await PumpAsync(DispatcherPriority.Background);
                True(string.IsNullOrEmpty(currentPassword.Password)
                    && string.IsNullOrEmpty(newPassword.Password)
                    && string.IsNullOrEmpty(confirmation.Password),
                    "La fermeture du launcher doit laisser les champs secrets vides.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    if (window?.IsVisible == true)
                    {
                        window.Close();
                    }
                    commands?.Dispose();
                    accountAdapter?.Dispose();
                    authAdapter?.Dispose();
                    account?.BeginShutdown();
                    lifetime?.Cancel();
                    if (account is not null)
                    {
                        await account.WaitForIdleAsync(TimeSpan.FromSeconds(2));
                    }
                    account?.Dispose();
                    cache?.Dispose();
                    operations?.Dispose();
                    session?.Dispose();
                    lifetime?.Dispose();
                    AccountAvatarClientTests.TryDelete(cacheRoot);
                    application?.Shutdown();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
                catch (Exception cleanupException)
                {
                    failure ??= cleanupException;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            }
        }
    }

    private static LauncherDeviceSession DeviceSession(string id, string name, bool current)
    {
        DateTimeOffset now = new(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);
        return new LauncherDeviceSession(
            id,
            name,
            now.AddDays(-10),
            current ? now : now.AddHours(-2),
            now.AddDays(20),
            current);
    }

    private static void LoadV2Resources(Application application)
    {
        if (application.Resources.MergedDictionaries.Count > 0)
        {
            return;
        }
        foreach (string path in new[]
        {
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(path, UriKind.Relative)
            });
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(message);
            }
            await DelayAndPumpAsync(35);
        }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root) => FindVisuals<Button>(root);

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            foreach (T child in FindVisuals<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private static void SaveCapture(FrameworkElement visual, string? directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        visual.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static T Required<T>(FrameworkElement root, string name)
        where T : class
    {
        return root.FindName(name) as T
            ?? throw new InvalidOperationException($"Contrôle WPF absent : {name}.");
    }

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static void RaiseMouseDown(UIElement element)
    {
        element.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = element
        });
    }

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority) =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }

    private sealed class NullAvatarPicker : IAvatarFilePicker
    {
        public string? PickImagePath() => null;
    }
}
