using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class AccountPreviewTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizeStartupIsolation();
        CharacterizePreviewStates();
        await ValidateWpfLayoutsInteractionsAndCapturesAsync(captureDirectory);
        Console.WriteLine("Account WPF preview OK (03A.4 avatar and 03A.5 security/session scenarios, isolated presentation only).");
        return 0;
    }

    private static void CharacterizeStartupIsolation()
    {
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-account=profile"]),
            "preview-account sans --ui-v2 doit être refusé avant composition.");
        Equal(
            LauncherStartupMode.UiV2AccountPreview,
            App.ResolveStartupMode(["--ui-v2", "--preview-account=crop"]),
            "preview-account doit utiliser sa branche isolée.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-account", "--preview-auth=login"]),
            "Compte et Auth preview ne doivent pas être combinés.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-account", "--preview-settings"]),
            "Compte et Paramètres preview ne doivent pas être combinés.");
        Equal(LauncherStartupMode.UiV2, App.ResolveStartupMode([]), "Le lancement sans argument doit ouvrir la V2 réelle.");
        Equal(LauncherStartupMode.UiV2, App.ResolveStartupMode(["--ui-v2"]), "La V2 réelle doit rester distincte.");
    }

    private static void CharacterizePreviewStates()
    {
        Equal(AccountPreviewScenario.Profile, Resolve("--preview-account"), "Le scénario Compte par défaut est incorrect.");
        Equal(AccountPreviewScenario.Profile, Resolve("--preview-account=avatar"), "Le scénario avatar est absent.");
        Equal(AccountPreviewScenario.Fallback, Resolve("--preview-account=fallback"), "Le fallback est absent.");
        Equal(AccountPreviewScenario.AvatarChanged, Resolve("--preview-account=changed"), "Le changement d'avatar est absent.");
        Equal(AccountPreviewScenario.AvatarDeleted, Resolve("--preview-account=deleted"), "Le scénario après suppression est absent.");
        Equal(AccountPreviewScenario.Crop, Resolve("--preview-account=crop"), "Le recadrage est absent.");
        Equal(AccountPreviewScenario.Uploading, Resolve("--preview-account=uploading"), "L'envoi est absent.");
        Equal(AccountPreviewScenario.UploadError, Resolve("--preview-account=upload-error"), "L'erreur d'envoi est absente.");
        Equal(AccountPreviewScenario.Removing, Resolve("--preview-account=removing"), "La suppression est absente.");
        Equal(AccountPreviewScenario.Security, Resolve("--preview-account=security"), "La sécurité est absente.");
        Equal(AccountPreviewScenario.Sessions, Resolve("--preview-account=sessions"), "Les sessions sont absentes.");
        Equal(AccountPreviewScenario.PasswordChange, Resolve("--preview-account=password-change"), "Le formulaire mot de passe est absent.");
        Equal(AccountPreviewScenario.PasswordError, Resolve("--preview-account=password-error"), "L'erreur mot de passe est absente.");
        Equal(AccountPreviewScenario.EmailUnverified, Resolve("--preview-account=email-unverified"), "L'e-mail non vérifié est absent.");
        Equal(AccountPreviewScenario.EmailChange, Resolve("--preview-account=email-change"), "Le formulaire e-mail est absent.");
        Equal(AccountPreviewScenario.SessionRevoke, Resolve("--preview-account=session-revoke"), "La révocation de session est absente.");
        Equal(AccountPreviewScenario.SessionRevokeError, Resolve("--preview-account=session-revoke-error"), "L'erreur de révocation est absente.");

        AccountUiState profile = LauncherV2PreviewData.CreateAccount(AccountPreviewScenario.Profile);
        True(profile.Current.IsPreview, "Le compte fictif doit être explicitement marqué preview.");
        True(profile.Current.HasProfileAvatar, "Le profil principal doit montrer une photo fictive.");
        True(profile.Current.AvatarImage is { IsFrozen: true }, "La ressource avatar fictive doit être chargée et figée localement.");

        AccountUiState fallback = LauncherV2PreviewData.CreateAccount(AccountPreviewScenario.Fallback);
        True(!fallback.Current.HasProfileAvatar, "Le fallback doit utiliser l'initiale.");
        Equal("D", fallback.Current.Initial, "L'initiale fictive est incorrecte.");

        AvatarCropUiState crop = LauncherV2PreviewData.CreateAvatarCrop(AccountPreviewScenario.Crop);
        True(crop.IsOpen, "Le scénario crop doit ouvrir l'overlay.");
        Equal(AvatarCropPreviewStatus.Idle, crop.Current.Status, "Le crop normal ne doit pas être occupé.");
        Equal(
            AvatarCropPreviewStatus.Uploading,
            LauncherV2PreviewData.CreateAvatarCrop(AccountPreviewScenario.Uploading).Current.Status,
            "Le scénario uploading doit être occupé.");
        Equal(
            AvatarCropPreviewStatus.Error,
            LauncherV2PreviewData.CreateAvatarCrop(AccountPreviewScenario.UploadError).Current.Status,
            "Le scénario upload-error doit exposer une erreur.");

        AccountUiState removing = LauncherV2PreviewData.CreateAccount(AccountPreviewScenario.Removing);
        Equal(AvatarPreviewOperation.Removing, removing.Current.AvatarOperation, "Le scénario removing est incohérent.");
        True(removing.Current.AvatarStatusMessage.Length > 0, "La suppression doit avoir un retour visuel.");

        static AccountPreviewScenario Resolve(string argument) =>
            AccountPreviewArguments.ResolveScenario(["--ui-v2", argument]);
    }

    private static async Task ValidateWpfLayoutsInteractionsAndCapturesAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasAccountPreviewWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(75));
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
                await ValidateProfileMenuNavigationAsync(captureDirectory);
                await ValidateRuntimeNavigationActivationAsync();
                await ValidateAccountScenariosAndCapturesAsync(captureDirectory);
                await ValidateCropInteractionAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }
    }

    private static async Task ValidateProfileMenuNavigationAsync(string? captureDirectory)
    {
        LauncherShellV2 window = CreateWindow(1440, 860, AccountPreviewScenario.Profile, directAccountPreview: false);
        window.Show();
        try
        {
            await DelayAndPumpAsync(160);
            Equal(LauncherShellPage.Game, window.CurrentPage, "Le preview standard doit commencer sur Jeu.");
            RaiseClick(Required<Button>(window, "ProfileButton"));
            await DelayAndPumpAsync(180);
            True(window.ProfileState.IsOpen, "Le bouton profil doit ouvrir son menu.");
            Button manageProfile = Required<Button>(window.ProfileOverlay, "ManageProfileButton");
            Button manageAccount = Required<Button>(window.ProfileOverlay, "ManageAccountButton");
            True(manageProfile.IsEnabled, "Gérer mon profil doit être actif dans le preview.");
            True(manageAccount.IsEnabled, "Gérer mon compte doit être actif dans le preview.");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                SavePng(window, Path.Combine(captureDirectory, "00-profile-menu-manage-account-1440x860.png"));
            }
            RaiseClick(manageProfile);
            await DelayAndPumpAsync(180);
            Equal(LauncherShellPage.Account, window.CurrentPage, "Gérer mon profil doit ouvrir AccountViewV2.");
            Equal(AccountSection.Profile, window.AccountPage.SelectedSection,
                "Gérer mon profil doit ouvrir directement la personnalisation.");
            Equal("Mon profil", Required<TextBlock>(window.AccountPage, "PageTitle").Text,
                "L'accès Profil doit être identifié comme une personnalisation.");
            Equal(Visibility.Visible, window.AccountPage.Visibility, "AccountViewV2 doit être visible.");
            True(!window.ProfileState.IsOpen, "Le menu profil doit se fermer après navigation.");

            RaiseClick(Required<Button>(window, "ProfileButton"));
            await DelayAndPumpAsync(180);
            RaiseClick(manageAccount);
            await DelayAndPumpAsync(180);
            Equal(AccountSection.Security, window.AccountPage.SelectedSection,
                "Gérer mon compte doit ouvrir directement la sécurité.");
            Equal("Mon compte", Required<TextBlock>(window.AccountPage, "PageTitle").Text,
                "L'accès Compte doit conserver son identité dédiée.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateRuntimeNavigationActivationAsync()
    {
        AccountUiState accountState = new(AccountUiState.Empty.Current);
        LauncherShellV2 window = new(
            LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready, isAuthenticated: true),
            LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateProfile(ProfilePreviewScenario.SignedIn),
            LauncherV2PreviewData.CreateSettings(),
            accountState,
            new AvatarCropUiState(AvatarCropUiState.Empty.Current))
        {
            Width = 1440,
            Height = 860,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        window.Show();
        try
        {
            await PumpAsync(DispatcherPriority.DataBind);
            Button manageProfile = Required<Button>(window.ProfileOverlay, "ManageProfileButton");
            Button manageAccount = Required<Button>(window.ProfileOverlay, "ManageAccountButton");
            True(!manageProfile.IsEnabled && !manageAccount.IsEnabled,
                "La navigation Compte doit attendre la restauration de session.");

            AccountViewState authenticated = LauncherV2PreviewData
                .CreateAccount(AccountPreviewScenario.Profile)
                .Current with
            {
                IsPreview = false,
                IsRuntimeConnected = true
            };
            accountState.ApplyRuntimeView(authenticated);
            await PumpAsync(DispatcherPriority.DataBind);
            True(manageProfile.IsEnabled && manageAccount.IsEnabled,
                "La restauration de session doit activer les deux accès sans recréer la fenêtre.");

            RaiseClick(Required<Button>(window, "ProfileButton"));
            RaiseClick(manageAccount);
            Equal(LauncherShellPage.Account, window.CurrentPage,
                "Le compte restauré doit être accessible immédiatement.");
            Equal(AccountSection.Security, window.AccountPage.SelectedSection,
                "L'accès Compte restauré doit cibler la sécurité.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateAccountScenariosAndCapturesAsync(string? captureDirectory)
    {
        (AccountPreviewScenario Scenario, double Width, double Height, string FileName)[] scenarios =
        [
            (AccountPreviewScenario.Profile, 1440, 860, "01-account-profile-1440x860.png"),
            (AccountPreviewScenario.Fallback, 1080, 680, "02-account-fallback-1080x680.png"),
            (AccountPreviewScenario.AvatarChanged, 1440, 860, "02b-account-avatar-changed-1440x860.png"),
            (AccountPreviewScenario.AvatarDeleted, 1440, 860, "02c-account-avatar-deleted-1440x860.png"),
            (AccountPreviewScenario.Security, 1440, 860, "03-account-security-1440x860.png"),
            (AccountPreviewScenario.Sessions, 1440, 860, "04-account-sessions-1440x860.png"),
            (AccountPreviewScenario.PasswordChange, 1440, 860, "04b-account-password-change-1440x860.png"),
            (AccountPreviewScenario.PasswordError, 1080, 680, "04c-account-password-error-1080x680.png"),
            (AccountPreviewScenario.EmailUnverified, 1440, 860, "04d-account-email-unverified-1440x860.png"),
            (AccountPreviewScenario.EmailChange, 1440, 860, "04e-account-email-change-1440x860.png"),
            (AccountPreviewScenario.SessionRevoke, 1080, 680, "04f-account-session-revoke-1080x680.png"),
            (AccountPreviewScenario.SessionRevokeError, 1440, 860, "04g-account-session-revoke-error-1440x860.png"),
            (AccountPreviewScenario.Crop, 1440, 860, "05-account-crop-1440x860.png"),
            (AccountPreviewScenario.Uploading, 1440, 860, "06-account-uploading-1440x860.png"),
            (AccountPreviewScenario.UploadError, 1440, 860, "07-account-upload-error-1440x860.png"),
            (AccountPreviewScenario.Removing, 1440, 860, "08-account-removing-1440x860.png"),
            (AccountPreviewScenario.Crop, 1080, 680, "09-account-crop-1080x680.png")
        ];

        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
        }

        foreach ((AccountPreviewScenario scenario, double width, double height, string fileName) in scenarios)
        {
            LauncherShellV2 window = CreateWindow(width, height, scenario, directAccountPreview: true);
            window.Show();
            try
            {
                await DelayAndPumpAsync(220);
                ValidateCommonContract(window, scenario);
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

        LauncherShellV2 wideWindow = CreateWindow(1920, 1080, AccountPreviewScenario.Profile, directAccountPreview: true);
        wideWindow.Show();
        try
        {
            await DelayAndPumpAsync(120);
            FrameworkElement content = Required<Grid>(wideWindow.AccountPage, "ContentFrame");
            True(content.ActualWidth <= 1221, "Le contenu Compte ne doit pas s'étirer excessivement à 1920 px.");
        }
        finally
        {
            wideWindow.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateCropInteractionAsync()
    {
        LauncherShellV2 window = CreateWindow(1440, 860, AccountPreviewScenario.Crop, directAccountPreview: true);
        window.Show();
        try
        {
            await DelayAndPumpAsync(180);
            AvatarCropOverlayV2 overlay = window.AvatarCropPreviewOverlay;
            Slider zoom = Required<Slider>(overlay, "ZoomSlider");
            zoom.Value = 1.62;
            await PumpAsync(DispatcherPriority.Input);
            Equal(1.62, window.AvatarCropState.Current.Zoom, "Le zoom fictif doit mettre à jour l'état local.");
            window.AvatarCropState.SetTransform(1.62, 24, -19);
            await PumpAsync(DispatcherPriority.Render);
            var layout = window.AvatarCropState.Current.Layout;
            Rect expectedViewbox = new(
                layout.PixelCrop.X,
                layout.PixelCrop.Y,
                layout.PixelCrop.Size,
                layout.PixelCrop.Size);
            ImageBrush editor = Required<ImageBrush>(overlay, "CropEditorBrush");
            ImageBrush preview = Required<ImageBrush>(overlay, "Preview128Brush");
            Equal(expectedViewbox, editor.Viewbox, "Le déplacement fictif doit mettre à jour le cadrage principal.");
            Equal(editor.Viewbox, preview.Viewbox, "Le cadrage principal et l'aperçu doivent rester identiques.");

            RaiseClick(Required<Button>(overlay, "SaveCropButton"));
            await PumpAsync(DispatcherPriority.Input);
            Equal(AvatarCropPreviewStatus.Uploading, window.AvatarCropState.Current.Status, "Le bouton doit lancer l'envoi fictif local.");
            True(!Required<Button>(overlay, "SaveCropButton").IsEnabled, "Un second envoi fictif doit être impossible.");
            True(!Required<Button>(overlay, "CloseCropButton").IsEnabled, "La fermeture interne doit être neutralisée pendant l'envoi fictif.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static void ValidateCommonContract(LauncherShellV2 window, AccountPreviewScenario scenario)
    {
        True(window.IsPreviewMode, "AccountViewV2 doit rester dans une fenêtre preview.");
        True(!window.HasRealAuthenticationAttached, "Le preview Compte ne doit attacher aucun service réel.");
        True(window.AccountState.Current.IsPreview, "L'état Compte doit être fictif.");
        Equal(LauncherShellPage.Account, window.CurrentPage, "Le preview Compte doit ouvrir sa page directement.");
        Equal(Visibility.Visible, window.AccountPage.Visibility, "AccountViewV2 doit être visible.");
        Equal(Visibility.Collapsed, Required<GameViewV2>(window, "GameView").Visibility, "GameView ne doit pas rester derrière Compte.");
        Equal(ScrollBarVisibility.Disabled, window.AccountPage.ScrollHost.HorizontalScrollBarVisibility, "Aucune barre horizontale n'est autorisée.");
        True(window.AccountPage.ScrollHost.ScrollableWidth <= 0.5, "Le compte ne doit pas déborder horizontalement.");

        AccountSection expectedSection = scenario switch
        {
            AccountPreviewScenario.Security
                or AccountPreviewScenario.PasswordChange
                or AccountPreviewScenario.PasswordError
                or AccountPreviewScenario.EmailUnverified
                or AccountPreviewScenario.EmailChange => AccountSection.Security,
            AccountPreviewScenario.Sessions
                or AccountPreviewScenario.SessionRevoke
                or AccountPreviewScenario.SessionRevokeError => AccountSection.Sessions,
            _ => AccountSection.Profile
        };
        Equal(expectedSection, window.AccountPage.SelectedSection, "L'onglet initial est incorrect.");

        bool expectsCrop = scenario is AccountPreviewScenario.Crop
            or AccountPreviewScenario.Uploading
            or AccountPreviewScenario.UploadError;
        Equal(expectsCrop, window.AvatarCropState.IsOpen, "Ouverture de l'overlay incohérente.");
        Equal(
            expectsCrop ? Visibility.Visible : Visibility.Collapsed,
            window.AvatarCropPreviewOverlay.Visibility,
            "Visibilité du crop incohérente.");
        if (expectsCrop)
        {
            ScrollViewer dialogScroll = Required<ScrollViewer>(window.AvatarCropPreviewOverlay, "DialogScrollViewer");
            Equal(ScrollBarVisibility.Disabled, dialogScroll.HorizontalScrollBarVisibility, "Le crop ne doit jamais défiler horizontalement.");
            True(dialogScroll.ScrollableWidth <= 0.5, "Le crop ne doit pas déborder horizontalement.");
            Rect saveBounds = BoundsInAncestor(Required<Button>(window.AvatarCropPreviewOverlay, "SaveCropButton"), window);
            True(saveBounds.Bottom <= window.ActualHeight + 0.5, "L'action principale du crop doit rester visible.");
        }

        bool expectsAvatar = scenario is not (
            AccountPreviewScenario.Fallback or AccountPreviewScenario.AvatarDeleted);
        Equal(expectsAvatar, window.ShellState.HasProfileAvatar,
            "La barre supérieure doit suivre le scénario avatar isolé.");
        Equal(expectsAvatar, window.ProfileState.HasAvatar,
            "ProfileMenu doit suivre le scénario avatar isolé.");
        Equal(expectsAvatar, window.AccountState.Current.HasProfileAvatar,
            "AccountView doit suivre le scénario avatar isolé.");
        if (expectsAvatar)
        {
            True(ReferenceEquals(
                    window.ShellState.ProfileAvatarImage,
                    window.ProfileState.AvatarImage),
                "Le preview doit partager la même ImageSource locale entre Shell et ProfileMenu.");
            True(ReferenceEquals(
                    window.ShellState.ProfileAvatarImage,
                    window.AccountState.Current.AvatarImage),
                "Le preview isolé doit projeter une seule ressource locale dans les trois vues.");
        }

        if (scenario is AccountPreviewScenario.Fallback or AccountPreviewScenario.AvatarDeleted)
        {
            Equal(Visibility.Collapsed, Required<System.Windows.Shapes.Ellipse>(window.AccountPage, "ProfileAvatarImage").Visibility, "Le fallback doit masquer la photo.");
            True(!Required<Button>(window.AccountPage, "RemoveAvatarButton").IsEnabled, "Le fallback ne doit pas proposer de suppression.");
        }

        if (scenario == AccountPreviewScenario.Uploading)
        {
            True(window.AvatarCropPreviewOverlay.IsBusy, "L'envoi fictif doit être occupé.");
            Equal("Envoi…", Required<TextBlock>(window.AvatarCropPreviewOverlay, "SaveCropLabel").Text, "Le libellé d'envoi est incorrect.");
        }

        if (scenario == AccountPreviewScenario.UploadError)
        {
            Equal(Visibility.Visible, Required<Border>(window.AvatarCropPreviewOverlay, "CropErrorBanner").Visibility, "L'erreur d'upload doit être visible.");
        }

        if (scenario == AccountPreviewScenario.Removing)
        {
            Equal(Visibility.Visible, Required<Border>(window.AccountPage, "AvatarOperationBanner").Visibility, "La suppression fictive doit être visible.");
            True(!Required<Button>(window.AccountPage, "ModifyAvatarButton").IsEnabled, "Les actions avatar doivent être bloquées pendant la suppression.");
        }

        if (scenario is AccountPreviewScenario.PasswordChange or AccountPreviewScenario.PasswordError)
        {
            Equal(Visibility.Visible, Required<Grid>(window.AccountPage, "PasswordEditorLayer").Visibility,
                "Le scénario mot de passe doit ouvrir son formulaire local.");
            True(window.AccountPage.IsSensitiveEditorOpen,
                "Le focus doit reconnaître le formulaire mot de passe comme modal.");
        }

        if (scenario == AccountPreviewScenario.PasswordError)
        {
            Equal(Visibility.Visible, Required<Border>(window.AccountPage, "PasswordEditorErrorBanner").Visibility,
                "Le scénario mot de passe en erreur doit afficher un message contrôlé.");
        }

        if (scenario == AccountPreviewScenario.EmailChange)
        {
            Equal(Visibility.Visible, Required<Grid>(window.AccountPage, "EmailEditorLayer").Visibility,
                "Le scénario e-mail doit ouvrir son formulaire local.");
        }

        if (scenario == AccountPreviewScenario.SessionRevokeError)
        {
            Equal(Visibility.Visible, Required<Border>(window.AccountPage, "SessionsErrorBanner").Visibility,
                "Le scénario session en erreur doit afficher un message contrôlé.");
        }
    }

    private static LauncherShellV2 CreateWindow(
        double width,
        double height,
        AccountPreviewScenario scenario,
        bool directAccountPreview)
    {
        LauncherShellV2 window = directAccountPreview
            ? new LauncherShellV2(GamePreviewScenario.Ready, scenario)
            : new LauncherShellV2(GamePreviewScenario.Ready);
        window.Width = width;
        window.Height = height;
        window.Left = -20000;
        window.Top = -20000;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        return window;
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

    private static void SavePng(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Rect BoundsInAncestor(FrameworkElement element, Visual ancestor)
    {
        return element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private static T Required<T>(FrameworkElement root, string name)
        where T : class
    {
        return root.FindName(name) as T
            ?? throw new InvalidOperationException($"Contrôle WPF absent : {name}.");
    }

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority) =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }
}
