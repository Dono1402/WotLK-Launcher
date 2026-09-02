using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class AccountAvatarWpfTests
{
    private static double _observedWindowDpi;

    internal static async Task RunAsync(string? captureDirectory)
    {
        await using AccountAvatarTestServer server = await AccountAvatarTestServer.StartAsync();
        string root = AccountAvatarClientTests.NewRoot("wpf-runtime");
        string selectedImage = Path.Combine(root, "selected.jpg");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(selectedImage, CreateSelectedJpeg());
        try
        {
            await RunStaHarnessAsync(server, root, selectedImage, captureDirectory);
            Console.WriteLine($"Global account avatar WPF OK (window DPI={_observedWindowDpi:F0}).");
        }
        finally
        {
            AccountAvatarClientTests.TryDelete(root);
        }
    }

    private static async Task RunStaHarnessAsync(
        AccountAvatarTestServer server,
        string root,
        string selectedImage,
        string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunHarness(server, root, selectedImage, captureDirectory, completion))
        {
            IsBackground = true,
            Name = "AtlasAccountAvatarWpfRuntime"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static void RunHarness(
        AccountAvatarTestServer server,
        string root,
        string selectedImage,
        string? captureDirectory,
        TaskCompletionSource completion)
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
            LauncherShellV2? window = null;
            CancellationTokenSource? lifetime = null;
            LauncherSessionCoordinator? session = null;
            LauncherOperationCoordinator? operations = null;
            HttpClient? http = null;
            AvatarImageCache? cache = null;
            LauncherAccountCoordinator? account = null;
            AccountStateAdapter? adapter = null;
            AuthStateAdapter? authAdapter = null;
            AccountCommands? commands = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                lifetime = new CancellationTokenSource();
                FakeLauncherAuthService authentication = new()
                {
                    Session = FakeLauncherAuthService.CreateSession(
                        "Dono1402",
                        "dono@example.test",
                        avatar: AccountAvatarClientTests.Descriptor(1)),
                    RestoreResult = true,
                    EnsureFreshHandler = _ => Task.FromResult(true)
                };
                session = new LauncherSessionCoordinator(authentication, lifetime.Token, _ => { });
                AccountAvatarClientTests.Equal(
                    LauncherSessionRestoreStatus.Restored,
                    (await session.RestoreOnceAsync()).Status,
                    "Le harnais WPF doit restaurer sa session locale.");
                operations = new LauncherOperationCoordinator();
                http = new HttpClient(new TestBearerHandler("test-access-token"))
                {
                    Timeout = TimeSpan.FromSeconds(15)
                };
                AvatarMediaClient media = new(http, server.ApiBaseUri);
                cache = new AvatarImageCache(
                    media,
                    Path.Combine(root, "cache"),
                    lifetime.Token,
                    session.NotifyAuthenticatedRequestUnauthorized);
                account = new LauncherAccountCoordinator(
                    session,
                    authentication,
                    operations,
                    media,
                    cache,
                    () => authentication.Session?.Profile,
                    _ => { });
                AccountUiState accountState = new(AccountStateAdapter.Project(account.CurrentSnapshot, null));
                AvatarCropUiState cropState = new(AvatarCropUiState.Empty.Current);
                ShellUiState shellState = LauncherV2PreviewData.CreateShell(
                    GamePreviewScenario.Ready,
                    isAuthenticated: true);
                ProfileUiState profileState = LauncherV2PreviewData.CreateProfile(
                    ProfilePreviewScenario.SignedIn);
                GameUiState gameState = LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready);
                Task initialMediaEntered = server.DelayMediaVersion(1);
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
                adapter = new AccountStateAdapter(
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
                    new AvatarFileSelectionService(new FixedPicker(selectedImage)),
                    dispatcher);
                window.AttachAccount(commands);
                window.Show();
                await initialMediaEntered.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => server.MediaRequestCount(1) == 2,
                    "Shell/Profile et Compte doivent demander exactement les variantes 64 et 256 px.");
                AccountAvatarClientTests.False(shellState.HasProfileAvatar,
                    "Pendant le chargement, la barre doit conserver son initiale.");
                AccountAvatarClientTests.False(profileState.HasAvatar,
                    "Pendant le chargement, le menu doit conserver son initiale.");
                AccountAvatarClientTests.False(accountState.Current.HasProfileAvatar,
                    "Pendant le chargement, la page Compte doit conserver son initiale.");
                server.ReleaseMediaVersion(1);
                await WaitUntilAsync(
                    () => shellState.HasProfileAvatar
                        && profileState.HasAvatar
                        && accountState.Current.HasProfileAvatar,
                    "Les trois projections doivent recevoir l'avatar restauré.");
                AccountAvatarClientTests.True(
                    ReferenceEquals(shellState.ProfileAvatarImage, profileState.AvatarImage),
                    "Shell et ProfileMenu doivent partager la même ImageSource 64 px.");
                AccountAvatarClientTests.Equal(2, server.MediaRequestCount(1),
                    "Une variante 64 px partagée et une variante 256 px doivent suffire.");
                DpiScale dpi = VisualTreeHelper.GetDpi(window);
                _observedWindowDpi = dpi.PixelsPerInchX;
                AccountAvatarClientTests.Near(120, dpi.PixelsPerInchX, 0.5,
                    "Les captures 03A.4 doivent provenir d'une session Windows à 125 %.");
                await DelayAndPumpAsync(120);
                SaveCapture(window, captureDirectory, "01-global-avatar-game-1440x860.png");

                RaiseClick(Required<Button>(window, "ProfileButton"));
                await DelayAndPumpAsync(160);
                AccountAvatarClientTests.True(profileState.HasAvatar,
                    "ProfileMenu doit afficher la photo synchronisée.");
                AccountAvatarClientTests.Equal(
                    Visibility.Visible,
                    Required<System.Windows.Shapes.Ellipse>(window.ProfileOverlay, "MenuProfileAvatarImage").Visibility,
                    "Le masque circulaire du menu Profil doit être visible.");
                AccountAvatarClientTests.Equal(0, server.ProfileCalls,
                    "Ouvrir ProfileMenu ne doit pas relire le profil.");
                SaveCapture(window, captureDirectory, "02-global-avatar-profile-menu-1440x860.png");
                Button manage = Required<Button>(window.ProfileOverlay, "ManageAccountButton");
                AccountAvatarClientTests.True(manage.IsEnabled,
                    "Gérer mon compte doit être actif dans la V2 réelle authentifiée.");
                RaiseClick(manage);
                await WaitUntilAsync(
                    () => accountState.Current.IsAvatarBackendAvailable
                        && accountState.Current.HasProfileAvatar,
                    "Le profil réel et son avatar doivent être chargés.");
                AccountAvatarClientTests.Equal(
                    LauncherShellPage.Account,
                    window.CurrentPage,
                    "Le menu Profil doit ouvrir la page Compte réelle.");
                await WaitUntilAsync(
                    () => window.ProfileOverlay.IsFullyClosed,
                    "Le menu Profil doit terminer sa fermeture après la navigation.");
                AccountAvatarClientTests.True(
                    accountState.Current.AvatarImage is { IsFrozen: true },
                    "La page Compte doit recevoir l'image figée du cache.");
                AccountAvatarClientTests.False(
                    Required<TextBlock>(window.AccountPage, "SecuritySummaryText").Text.Contains(
                        "sessions",
                        StringComparison.OrdinalIgnoreCase),
                    "La page réelle ne doit pas présenter un nombre de sessions fictif.");
                AccountAvatarClientTests.Equal(
                    Visibility.Collapsed,
                    Required<StackPanel>(window.AccountPage, "SessionsPreviewList").Visibility,
                    "Les appareils fictifs doivent rester réservés au preview.");
                AccountAvatarClientTests.Equal(
                    Visibility.Visible,
                    Required<ItemsControl>(window.AccountPage, "SessionsRealList").Visibility,
                    "La page réelle doit réserver sa liste aux sessions Atlas réelles.");
                AccountAvatarClientTests.True(server.ProfileCalls >= 1,
                    "Gérer mon compte conserve l'actualisation explicite existante.");
                SaveCapture(window, captureDirectory, "03-global-avatar-account-1440x860.png");

                Button modify = Required<Button>(window.AccountPage, "ModifyAvatarButton");
                RaiseClick(modify);
                await WaitUntilAsync(() => cropState.IsOpen, "Le sélecteur simulé doit ouvrir le crop réel.");
                AvatarCropOverlayV2 cropOverlay = window.AvatarCropPreviewOverlay;
                AccountAvatarClientTests.True(
                    cropState.Current.AvatarImage is { IsFrozen: true },
                    "La preview locale doit être chargée et figée.");
                await DelayAndPumpAsync(180);
                Slider zoom = Required<Slider>(cropOverlay, "ZoomSlider");
                zoom.Value = Math.Min(1.7, zoom.Maximum);
                cropState.SetTransform(zoom.Value, 34, -22);
                await PumpAsync(DispatcherPriority.Render);
                AvatarNormalizedCrop sentCrop = cropState.Current.Layout.Crop;
                AccountAvatarClientTests.True(sentCrop.IsValid, "Le crop WPF réel doit rester normalisé.");
                SaveCapture(window, captureDirectory, "02-account-real-crop-test-server-1440x860.png");

                server.FailNextUpload("InvalidImage", StatusCodes.Status400BadRequest);
                RaiseClick(Required<Button>(cropOverlay, "SaveCropButton"));
                await WaitUntilAsync(
                    () => cropState.Current.Status == AvatarCropPreviewStatus.Error,
                    "Une erreur serveur doit rester affichée dans le crop réel.");
                AccountAvatarClientTests.Equal(
                    "Cette image ne peut pas être utilisée.",
                    cropState.Current.ErrorMessage,
                    "Le message InvalidImage doit rester stable et non technique.");
                AccountAvatarClientTests.True(Required<Button>(cropOverlay, "SaveCropButton").IsEnabled,
                    "Une erreur contrôlée doit permettre une nouvelle tentative.");
                SaveCapture(window, captureDirectory, "03-account-real-error-test-server-1440x860.png");

                server.ResetUploadGate();
                RaiseClick(Required<Button>(cropOverlay, "SaveCropButton"));
                await server.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => cropState.Current.Status == AvatarCropPreviewStatus.Processing,
                    "L'upload annulé doit atteindre le traitement serveur réel.");
                server.ResetProfileGate();
                RaiseClick(Required<Button>(cropOverlay, "CancelCropButton"));
                await server.ProfileEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => cropState.Current.Status == AvatarCropPreviewStatus.Reconciling,
                    "Une annulation ambiguë doit relire le profil serveur.");
                SaveCapture(window, captureDirectory, "04-account-real-cancelling-test-server-1440x860.png");
                server.ReleaseProfile();
                await WaitUntilAsync(
                    () => cropOverlay.IsFullyClosed
                        && cropState.Current.Status == AvatarCropPreviewStatus.Idle,
                    "Après annulation et réconciliation, le crop doit se fermer proprement.");
                AccountAvatarClientTests.Equal(
                    (ulong)1,
                    server.CurrentAvatar?.Version ?? 0,
                    "Une requête annulée avant publication ne doit pas inventer un nouvel avatar.");

                RaiseClick(modify);
                await WaitUntilAsync(() => cropState.IsOpen, "Le crop doit pouvoir être rouvert après annulation.");
                await DelayAndPumpAsync(180);
                zoom = Required<Slider>(cropOverlay, "ZoomSlider");
                zoom.Value = Math.Min(1.7, zoom.Maximum);
                cropState.SetTransform(zoom.Value, 34, -22);
                await PumpAsync(DispatcherPriority.Render);
                sentCrop = cropState.Current.Layout.Crop;
                server.ResetUploadGate();
                RaiseClick(Required<Button>(cropOverlay, "SaveCropButton"));
                await server.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => cropState.Current.Status == AvatarCropPreviewStatus.Processing,
                    "Après l'envoi des octets, le traitement serveur doit être indéterminé.");
                AccountAvatarClientTests.True(Required<ProgressBar>(cropOverlay, "UploadProgressBar").IsIndeterminate,
                    "Le traitement Atlas ne doit pas simuler un pourcentage.");
                AccountAvatarClientTests.True(!Required<Button>(cropOverlay, "SaveCropButton").IsEnabled,
                    "Un double upload doit être impossible.");
                SaveCapture(window, captureDirectory, "05-account-real-processing-test-server-1440x860.png");
                _ = server.DelayMediaVersion(2);
                server.ReleaseUpload();
                await WaitUntilAsync(
                    () => !cropState.IsOpen
                        && account.CurrentSnapshot.Avatar?.Version == 2
                        && server.CurrentAvatar?.Version == 2,
                    "Le succès doit fermer le crop et publier immédiatement le descripteur version 2.");
                await WaitUntilAsync(
                    () => cropOverlay.IsFullyClosed,
                    "Le crop doit terminer sa fermeture avant le retour à la page Compte.");
                await WaitUntilAsync(
                    () => server.MediaRequestCount(2) == 2,
                    "La version 2 différée doit démarrer ses variantes 64 et 256 px.");
                AccountAvatarClientTests.False(shellState.HasProfileAvatar,
                    "Un nouveau descripteur en cours de chargement doit réafficher l'initiale dans le Shell.");
                AccountAvatarClientTests.False(profileState.HasAvatar,
                    "Un nouveau descripteur en cours de chargement doit réafficher l'initiale dans ProfileMenu.");
                AccountAvatarClientTests.False(accountState.Current.HasProfileAvatar,
                    "Un nouveau descripteur en cours de chargement doit réafficher l'initiale dans Compte.");
                AccountAvatarClientTests.Near(sentCrop.X, server.LastCrop.X, 0.000001,
                    "cropX envoyé diffère du cadrage WPF.");
                AccountAvatarClientTests.Near(sentCrop.Y, server.LastCrop.Y, 0.000001,
                    "cropY envoyé diffère du cadrage WPF.");
                AccountAvatarClientTests.Near(sentCrop.Size, server.LastCrop.Size, 0.000001,
                    "cropSize envoyé diffère du cadrage WPF.");

                RaiseClick(Required<Button>(window, "ProfileButton"));
                await DelayAndPumpAsync(140);
                FocusManager.SetFocusedElement(window, manage);
                Keyboard.Focus(manage);
                await PumpAsync(DispatcherPriority.Input);
                IInputElement? profileFocusBeforeUpdate = FocusManager.GetFocusedElement(window)
                    ?? Keyboard.FocusedElement;
                AccountAvatarClientTests.True(profileState.IsOpen,
                    "Le menu Profil doit rester testable pendant un changement d'avatar.");
                AccountAvatarClientTests.True(ReferenceEquals(manage, profileFocusBeforeUpdate),
                    "Le contrôle Gérer mon compte doit recevoir le focus avant la mise à jour.");
                server.ResetUploadGate();
                AccountActionStartResult versionThreeUpload = account.TryUpload(new AvatarUploadRequest(
                    AccountAvatarClientTests.CreatePng(8, 8),
                    "image/png",
                    new AvatarNormalizedCrop(0, 0, 1)));
                AccountAvatarClientTests.True(versionThreeUpload.IsStarted,
                    "Le second upload doit démarrer sans refresh indépendant.");
                await server.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                server.ReleaseUpload();
                AccountAvatarClientTests.Equal(
                    AccountActionCompletionStatus.Succeeded,
                    (await versionThreeUpload.Completion!).Status,
                    "Le second upload doit publier la version 3.");
                await WaitUntilAsync(
                    () => account.CurrentSnapshot.Avatar?.Version == 3
                        && shellState.HasProfileAvatar
                        && profileState.HasAvatar
                        && accountState.Current.HasProfileAvatar,
                    "La version 3 doit remplacer la version 2 dans les trois projections.");
                BitmapSource shellAvatarV3 = shellState.ProfileAvatarImage!;
                BitmapSource accountAvatarV3 = accountState.Current.AvatarImage!;
                AccountAvatarClientTests.True(
                    ReferenceEquals(shellAvatarV3, profileState.AvatarImage),
                    "Le menu ouvert doit recevoir la même ImageSource 64 px que le Shell.");
                AccountAvatarClientTests.False(
                    ReferenceEquals(shellAvatarV3, accountAvatarV3),
                    "AccountView doit utiliser sa variante 256 px distincte.");
                AccountAvatarClientTests.True(profileState.IsOpen,
                    "La mise à jour de l'image ne doit pas fermer ProfileMenu.");
                AccountAvatarClientTests.True(
                    ReferenceEquals(
                        profileFocusBeforeUpdate,
                        FocusManager.GetFocusedElement(window) ?? Keyboard.FocusedElement),
                    "La mise à jour de l'image ne doit pas déplacer le focus du menu Profil.");
                AccountAvatarClientTests.Equal(2, server.MediaRequestCount(3),
                    "La version 3 doit demander une variante 64 px partagée et une variante 256 px.");
                SaveCapture(window, captureDirectory, "03b-global-avatar-updated-menu-1440x860.png");

                server.ReleaseMediaVersion(2);
                await DelayAndPumpAsync(240);
                AccountAvatarClientTests.True(
                    ReferenceEquals(shellAvatarV3, shellState.ProfileAvatarImage)
                        && ReferenceEquals(shellAvatarV3, profileState.AvatarImage)
                        && ReferenceEquals(accountAvatarV3, accountState.Current.AvatarImage),
                    "Les callbacks tardifs de la version 2 ne doivent jamais remplacer la version 3.");
                RaiseClick(Required<Button>(window, "ProfileButton"));
                await WaitUntilAsync(
                    () => window.ProfileOverlay.IsFullyClosed,
                    "ProfileMenu doit pouvoir se fermer normalement après la mise à jour.");

                LauncherSessionStartResult logoutA = session.TryLogout(CancellationToken.None);
                AccountAvatarClientTests.True(logoutA.IsStarted,
                    "Le changement de compte doit commencer par une déconnexion observée.");
                _ = await logoutA.Completion!;
                await WaitUntilAsync(
                    () => !account.CurrentSnapshot.IsAuthenticated
                        && !shellState.HasProfileAvatar
                        && !profileState.HasAvatar
                        && !accountState.Current.HasProfileAvatar,
                    "La déconnexion doit retirer immédiatement toutes les projections de l'avatar A.");

                authentication.LoginHandler = (_, _, _) => Task.FromResult(
                    FakeLauncherAuthService.CreateSession("Beta", "beta@example.test"));
                LauncherSessionStartResult loginWithoutAvatar = session.TryLogin("Beta", "valid-password");
                AccountAvatarClientTests.True(loginWithoutAvatar.IsStarted,
                    "Le compte B sans avatar doit pouvoir ouvrir sa session.");
                _ = await loginWithoutAvatar.Completion!;
                await WaitUntilAsync(
                    () => account.CurrentSnapshot.Username == "Beta",
                    "Le snapshot Compte doit basculer vers le compte B.");
                AccountAvatarClientTests.Equal("B", shellState.ProfileInitial,
                    "Le fallback du compte B doit utiliser sa propre initiale.");
                AccountAvatarClientTests.False(shellState.HasProfileAvatar
                    || profileState.HasAvatar
                    || accountState.Current.HasProfileAvatar,
                    "Le compte B sans avatar ne doit jamais afficher la photo du compte A.");

                LauncherSessionStartResult logoutB = session.TryLogout(CancellationToken.None);
                AccountAvatarClientTests.True(logoutB.IsStarted,
                    "Le compte B sans avatar doit pouvoir être déconnecté.");
                _ = await logoutB.Completion!;
                AccountAvatarClientTests.Equal(
                    LauncherShellPage.Game,
                    window.CurrentPage,
                    "Une session fermée doit retirer la page Compte réelle.");
                AvatarDescriptor avatarB = AccountAvatarClientTests.Descriptor(4);
                server.SetCurrentAvatar(avatarB);
                server.SetCurrentIdentity("BetaAvatar", "beta-avatar@example.test");
                authentication.LoginHandler = (_, _, _) => Task.FromResult(
                    FakeLauncherAuthService.CreateSession(
                        "BetaAvatar",
                        "beta-avatar@example.test",
                        avatar: avatarB));
                LauncherSessionStartResult loginWithAvatar = session.TryLogin(
                    "BetaAvatar",
                    "valid-password");
                AccountAvatarClientTests.True(loginWithAvatar.IsStarted,
                    "Le compte B avec avatar doit pouvoir ouvrir sa session.");
                _ = await loginWithAvatar.Completion!;
                await WaitUntilAsync(
                    () => account.CurrentSnapshot.Username == "BetaAvatar",
                    "Le snapshot doit publier l'identité du second compte avec avatar.");
                AccountAvatarClientTests.False(
                    ReferenceEquals(shellAvatarV3, shellState.ProfileAvatarImage),
                    "L'image A doit être retirée avant l'affichage éventuel de l'image B.");
                await WaitUntilAsync(
                    () => shellState.HasProfileAvatar
                        && profileState.HasAvatar
                        && accountState.Current.HasProfileAvatar,
                    "Le compte B doit charger sa propre image en arrière-plan.");
                AccountAvatarClientTests.False(
                    ReferenceEquals(shellAvatarV3, shellState.ProfileAvatarImage),
                    "Le compte B ne doit jamais réutiliser l'ImageSource du compte A.");

                RaiseClick(Required<Button>(window, "ProfileButton"));
                await PumpAsync(DispatcherPriority.Input);
                RaiseClick(Required<Button>(window.ProfileOverlay, "ManageAccountButton"));
                await WaitUntilAsync(
                    () => window.CurrentPage == LauncherShellPage.Account,
                    "Le second compte doit rouvrir explicitement sa page Compte.");
                await WaitUntilAsync(
                    () => accountState.Current.CanRemoveAvatar,
                    "Le profil du second compte doit terminer son actualisation avant suppression.");

                Button remove = Required<Button>(window.AccountPage, "RemoveAvatarButton");
                RaiseClick(remove);
                await PumpAsync(DispatcherPriority.Input);
                AccountAvatarClientTests.True(window.AccountPage.IsDeleteConfirmationOpen,
                    "Supprimer doit demander une confirmation.");
                AccountAvatarClientTests.True(
                    window.AccountPage.ContainsDeleteConfirmationFocus(Keyboard.FocusedElement as DependencyObject),
                    "Le focus doit rester dans la confirmation destructive.");
                window.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                });
                await PumpAsync(DispatcherPriority.Input);
                AccountAvatarClientTests.False(window.AccountPage.IsDeleteConfirmationOpen,
                    "Échap doit annuler la confirmation sans requête.");
                AccountAvatarClientTests.Equal(0, server.DeleteCalls,
                    "Annuler la confirmation ne doit pas appeler DELETE.");

                RaiseClick(remove);
                await PumpAsync(DispatcherPriority.Input);
                server.FailNextDelete("StorageFailed", StatusCodes.Status503ServiceUnavailable);
                RaiseClick(Required<Button>(window.AccountPage, "ConfirmDeleteAvatarButton"));
                await WaitUntilAsync(
                    () => accountState.Current.AvatarOperation == AvatarPreviewOperation.None
                        && accountState.Current.AvatarErrorMessage ==
                            "Le stockage des photos est temporairement indisponible.",
                    "Une erreur DELETE doit conserver l'avatar et afficher un message stable.");
                AccountAvatarClientTests.True(accountState.Current.HasProfileAvatar,
                    "Une suppression refusée ne doit pas retirer l'avatar local.");

                RaiseClick(remove);
                await PumpAsync(DispatcherPriority.Input);
                server.ResetDeleteGate();
                RaiseClick(Required<Button>(window.AccountPage, "ConfirmDeleteAvatarButton"));
                await server.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => accountState.Current.AvatarOperation == AvatarPreviewOperation.Removing,
                    "La suppression réelle doit afficher Removing.");
                await DelayAndPumpAsync(120);
                SaveCapture(window, captureDirectory, "06-account-real-removing-test-server-1440x860.png");
                server.ReleaseDelete();
                await WaitUntilAsync(
                    () => !accountState.Current.HasProfileAvatar
                        && accountState.Current.AvatarOperation == AvatarPreviewOperation.None,
                    "DELETE doit revenir immédiatement au fallback par initiale.");
                AccountAvatarClientTests.Equal("B", accountState.Current.Initial,
                    "Le fallback officiel doit être l'initiale du compte.");
                AccountAvatarClientTests.False(shellState.HasProfileAvatar || profileState.HasAvatar,
                    "DELETE doit retirer l'ancienne ImageSource de Shell et ProfileMenu.");
                await DelayAndPumpAsync(120);
                SaveCapture(window, captureDirectory, "04-global-avatar-fallback-1440x860.png");

                window.Width = 1080;
                window.Height = 680;
                await DelayAndPumpAsync(180);
                AccountAvatarClientTests.True(window.AccountPage.ScrollHost.ScrollableWidth <= 0.5,
                    "La page Compte réelle ne doit pas déborder horizontalement à 1080 x 680.");
                SaveCapture(window, captureDirectory, "07-account-real-fallback-test-server-1080x680.png");

                LauncherSessionStartResult logoutBeforeMissing = session.TryLogout(CancellationToken.None);
                AccountAvatarClientTests.True(logoutBeforeMissing.IsStarted,
                    "Le scénario média 404 doit partir d'une session propre.");
                _ = await logoutBeforeMissing.Completion!;
                AvatarDescriptor missingAvatar = AccountAvatarClientTests.Descriptor(404);
                authentication.LoginHandler = (_, _, _) => Task.FromResult(
                    FakeLauncherAuthService.CreateSession(
                        "MissingMedia",
                        "missing@example.test",
                        avatar: missingAvatar));
                int requestsBeforeMissing = server.TotalRequests;
                LauncherSessionStartResult missingLogin = session.TryLogin(
                    "MissingMedia",
                    "valid-password");
                AccountAvatarClientTests.True(missingLogin.IsStarted,
                    "Le scénario 404 doit conserver une session Atlas valide.");
                _ = await missingLogin.Completion!;
                await WaitUntilAsync(
                    () => server.TotalRequests >= requestsBeforeMissing + 2,
                    "Les variantes Shell et Compte doivent observer le 404 média.");
                await DelayAndPumpAsync(120);
                AccountAvatarClientTests.True(account.CurrentSnapshot.IsAuthenticated,
                    "Un média 404 ne doit pas fermer la session.");
                AccountAvatarClientTests.False(shellState.HasProfileAvatar
                    || profileState.HasAvatar
                    || accountState.Current.HasProfileAvatar,
                    "Un média 404 doit conserver le fallback dans les trois vues.");

                window.Close();
                await PumpAsync(DispatcherPriority.Background);
                int requestsAfterClose = server.TotalRequests;
                adapter.Dispose();
                authAdapter.Dispose();
                commands.Dispose();
                account.BeginShutdown();
                lifetime.Cancel();
                await account.WaitForIdleAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(50);
                AccountAvatarClientTests.Equal(requestsAfterClose, server.TotalRequests,
                    "Aucun callback WPF tardif ne doit déclencher de nouvelle requête après fermeture.");
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
                }
                catch
                {
                }
                commands?.Dispose();
                adapter?.Dispose();
                authAdapter?.Dispose();
                account?.Dispose();
                cache?.Dispose();
                operations?.Dispose();
                session?.Dispose();
                lifetime?.Cancel();
                lifetime?.Dispose();
                http?.Dispose();
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }
    }

    private static byte[] CreateSelectedJpeg()
    {
        using SkiaSharp.SKBitmap bitmap = new(1200, 800);
        bitmap.Erase(new SkiaSharp.SKColor(78, 138, 223));
        using SkiaSharp.SKCanvas canvas = new(bitmap);
        using SkiaSharp.SKPaint paint = new() { Color = new SkiaSharp.SKColor(231, 181, 82) };
        canvas.DrawCircle(600, 400, 260, paint);
        using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using SkiaSharp.SKData encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 94);
        return encoded.ToArray();
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

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority) =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);

    private sealed class FixedPicker(string path) : IAvatarFilePicker
    {
        public string? PickImagePath() => path;
    }

    private sealed class TestBearerHandler(string token) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

internal sealed class AccountAvatarTestServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly byte[] _avatarPng;
    private TaskCompletionSource _uploadEntered = NewSignal();
    private TaskCompletionSource _uploadRelease = NewSignal();
    private TaskCompletionSource _deleteEntered = NewSignal();
    private TaskCompletionSource _deleteRelease = NewSignal();
    private TaskCompletionSource _profileEntered = NewSignal();
    private TaskCompletionSource? _profileRelease;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _mediaReleases = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _mediaEntered = new();
    private readonly ConcurrentDictionary<ulong, int> _mediaRequests = new();
    private int _uploadBusy;
    private int _deleteBusy;
    private int _totalRequests;
    private int _deleteCalls;
    private int _profileCalls;
    private string? _nextUploadFailureCode;
    private int _nextUploadFailureStatus;
    private string? _nextDeleteFailureCode;
    private int _nextDeleteFailureStatus;

    private AccountAvatarTestServer(WebApplication application, Uri baseUri, byte[] avatarPng)
    {
        _application = application;
        _avatarPng = avatarPng;
        ApiBaseUri = new Uri(baseUri, "api/v1/");
        CurrentAvatar = AccountAvatarClientTests.Descriptor(1);
    }

    internal Uri ApiBaseUri { get; }
    internal AvatarDescriptor? CurrentAvatar { get; private set; }
    internal string CurrentUsername { get; private set; } = "Dono1402";
    internal string CurrentEmail { get; private set; } = "dono@example.test";
    internal AvatarNormalizedCrop LastCrop { get; private set; }
    internal TaskCompletionSource UploadEntered => _uploadEntered;
    internal TaskCompletionSource DeleteEntered => _deleteEntered;
    internal TaskCompletionSource ProfileEntered => _profileEntered;
    internal int TotalRequests => Volatile.Read(ref _totalRequests);
    internal int DeleteCalls => Volatile.Read(ref _deleteCalls);
    internal int ProfileCalls => Volatile.Read(ref _profileCalls);

    internal int MediaRequestCount(ulong version) =>
        _mediaRequests.TryGetValue(version, out int count) ? count : 0;

    internal static async Task<AccountAvatarTestServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(AccountAvatarTestServer).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication application = builder.Build();
        AccountAvatarTestServer? server = null;
        application.Use(async (context, next) =>
        {
            Interlocked.Increment(ref server!._totalRequests);
            if (!string.Equals(
                    context.Request.Headers.Authorization,
                    "Bearer test-access-token",
                    StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });
        application.MapGet("/api/v1/me", async (HttpContext context) =>
        {
            Interlocked.Increment(ref server!._profileCalls);
            TaskCompletionSource? release = server!._profileRelease;
            if (release is not null)
            {
                server._profileEntered.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
            }
            return Results.Json(new
            {
                accountId = 1,
                username = server.CurrentUsername,
                email = server.CurrentEmail,
                emailVerified = true,
                avatarKey = "gold",
                twoFactorEnabled = false,
                recoveryCodesGenerated = false,
                completion = 75,
                avatar = server.CurrentAvatar
            });
        });
        application.MapPost("/api/v1/me/avatar/photo", async (HttpContext context) =>
        {
            if (Interlocked.CompareExchange(ref server!._uploadBusy, 1, 0) != 0)
            {
                return Results.Json(new { code = "UploadInProgress" }, statusCode: StatusCodes.Status409Conflict);
            }
            try
            {
                IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);
                IFormFile image = form.Files.GetFile("image")
                    ?? throw new InvalidOperationException("Image multipart absente.");
                if (image.Length <= 0 || image.Length > AvatarMediaClient.MaximumUploadBytes)
                {
                    return Results.Json(new { code = "AvatarTooLarge" }, statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                server.LastCrop = new AvatarNormalizedCrop(
                    Parse(form["cropX"]!),
                    Parse(form["cropY"]!),
                    Parse(form["cropSize"]!));
                if (!server.LastCrop.IsValid)
                {
                    return Results.Json(new { code = "InvalidCrop" }, statusCode: StatusCodes.Status400BadRequest);
                }
                string? failureCode = Interlocked.Exchange(ref server._nextUploadFailureCode, null);
                int failureStatus = Interlocked.Exchange(ref server._nextUploadFailureStatus, 0);
                if (failureCode is not null)
                {
                    return Results.Json(new { code = failureCode }, statusCode: failureStatus);
                }
                server._uploadEntered.TrySetResult();
                await server._uploadRelease.Task.WaitAsync(context.RequestAborted);
                ulong nextVersion = (server.CurrentAvatar?.Version ?? 0) + 1;
                server.CurrentAvatar = AccountAvatarClientTests.Descriptor(nextVersion);
                return Results.Json(server.CurrentAvatar);
            }
            finally
            {
                Volatile.Write(ref server._uploadBusy, 0);
            }
        });
        application.MapDelete("/api/v1/me/avatar/photo", async (HttpContext context) =>
        {
            Interlocked.Increment(ref server!._deleteCalls);
            if (Interlocked.CompareExchange(ref server._deleteBusy, 1, 0) != 0)
            {
                return Results.Json(new { code = "UploadInProgress" }, statusCode: StatusCodes.Status409Conflict);
            }
            try
            {
                string? failureCode = Interlocked.Exchange(ref server._nextDeleteFailureCode, null);
                int failureStatus = Interlocked.Exchange(ref server._nextDeleteFailureStatus, 0);
                if (failureCode is not null)
                {
                    return Results.Json(new { code = failureCode }, statusCode: failureStatus);
                }
                server._deleteEntered.TrySetResult();
                await server._deleteRelease.Task.WaitAsync(context.RequestAborted);
                server.CurrentAvatar = null;
                return Results.NoContent();
            }
            finally
            {
                Volatile.Write(ref server._deleteBusy, 0);
            }
        });
        application.MapGet("/media/avatars/{avatarId}/{version:long}/{size:int}.png", async (
            string avatarId,
            long version,
            int size,
            HttpContext context) =>
        {
            AvatarDescriptor? current = server!.CurrentAvatar;
            if (current is null
                || !string.Equals(avatarId, current.AvatarId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || (ulong)version != current.Version
                || size is not (32 or 64 or 128 or 256))
            {
                return Results.NotFound();
            }
            ulong mediaVersion = checked((ulong)version);
            server._mediaRequests.AddOrUpdate(mediaVersion, 1, static (_, count) => count + 1);
            server._mediaEntered.GetOrAdd(mediaVersion, static _ => NewSignal()).TrySetResult();
            if (server._mediaReleases.TryGetValue(mediaVersion, out TaskCompletionSource? release))
            {
                await release.Task.WaitAsync(context.RequestAborted);
            }
            return Results.File(server._avatarPng, "image/png");
        });
        await application.StartAsync();
        string address = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        server = new AccountAvatarTestServer(
            application,
            new Uri(address.EndsWith('/') ? address : address + "/"),
            LoadPreviewAvatar());
        return server;
    }

    internal void ResetUploadGate()
    {
        _uploadEntered = NewSignal();
        _uploadRelease = NewSignal();
    }

    internal void ReleaseUpload() => _uploadRelease.TrySetResult();

    internal Task DelayMediaVersion(ulong version)
    {
        _mediaReleases[version] = NewSignal();
        return _mediaEntered.GetOrAdd(version, static _ => NewSignal()).Task;
    }

    internal void ReleaseMediaVersion(ulong version)
    {
        if (_mediaReleases.TryRemove(version, out TaskCompletionSource? release))
        {
            release.TrySetResult();
        }
    }

    internal void SetCurrentAvatar(AvatarDescriptor? avatar)
    {
        CurrentAvatar = avatar;
    }

    internal void SetCurrentIdentity(string username, string email)
    {
        CurrentUsername = username;
        CurrentEmail = email;
    }

    internal void FailNextUpload(string code, int statusCode)
    {
        Volatile.Write(ref _nextUploadFailureStatus, statusCode);
        Interlocked.Exchange(ref _nextUploadFailureCode, code);
    }

    internal void ResetProfileGate()
    {
        _profileEntered = NewSignal();
        _profileRelease = NewSignal();
    }

    internal void ReleaseProfile()
    {
        Interlocked.Exchange(ref _profileRelease, null)?.TrySetResult();
    }

    internal void ResetDeleteGate()
    {
        _deleteEntered = NewSignal();
        _deleteRelease = NewSignal();
    }

    internal void ReleaseDelete() => _deleteRelease.TrySetResult();

    internal void FailNextDelete(string code, int statusCode)
    {
        Volatile.Write(ref _nextDeleteFailureStatus, statusCode);
        Interlocked.Exchange(ref _nextDeleteFailureCode, code);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _uploadRelease.TrySetResult();
            _deleteRelease.TrySetResult();
            foreach (TaskCompletionSource release in _mediaReleases.Values)
            {
                release.TrySetResult();
            }
            ReleaseProfile();
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));
            await _application.StopAsync(stop.Token);
        }
        finally
        {
            await _application.DisposeAsync();
        }
    }

    private static double Parse(string value) =>
        double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] LoadPreviewAvatar()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                foreach (string relative in new[]
                {
                    Path.Combine("source", "WotLK.Launcher", "Assets", "Images", "AtlasProfilePreview.png"),
                    Path.Combine("WotLK.Launcher", "Assets", "Images", "AtlasProfilePreview.png")
                })
                {
                    string candidate = Path.Combine(directory.FullName, relative);
                    if (File.Exists(candidate))
                    {
                        return File.ReadAllBytes(candidate);
                    }
                }
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            "La photo locale AtlasProfilePreview.png est requise pour les captures WPF 03A.4.");
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
