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
                    Session = FakeLauncherAuthService.CreateSession("Dono1402", "dono@example.test"),
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
                window = new LauncherShellV2(
                    shellState,
                    LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
                    LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
                    LauncherV2PreviewData.CreateFriends(),
                    LauncherV2PreviewData.CreateProfile(ProfilePreviewScenario.SignedIn),
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
                    account,
                    cache,
                    dispatcher);
                commands = new AccountCommands(
                    account,
                    accountState,
                    cropState,
                    new AvatarFileSelectionService(new FixedPicker(selectedImage)),
                    dispatcher);
                window.AttachAccount(commands);
                window.Show();
                await DelayAndPumpAsync(180);

                RaiseClick(Required<Button>(window, "ProfileButton"));
                await DelayAndPumpAsync(160);
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
                    Required<Border>(window.AccountPage, "SessionsUnavailableCard").Visibility,
                    "La page réelle doit annoncer clairement la fonctionnalité Sessions différée.");
                SaveCapture(window, captureDirectory, "01-account-real-avatar-test-server-1440x860.png");

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
                server.ReleaseUpload();
                await WaitUntilAsync(
                    () => !cropState.IsOpen
                        && accountState.Current.HasProfileAvatar
                        && server.CurrentAvatar?.Version == 2,
                    "Le succès doit fermer le crop et publier le nouvel avatar.");
                await WaitUntilAsync(
                    () => cropOverlay.IsFullyClosed,
                    "Le crop doit terminer sa fermeture avant le retour à la page Compte.");
                AccountAvatarClientTests.Near(sentCrop.X, server.LastCrop.X, 0.000001,
                    "cropX envoyé diffère du cadrage WPF.");
                AccountAvatarClientTests.Near(sentCrop.Y, server.LastCrop.Y, 0.000001,
                    "cropY envoyé diffère du cadrage WPF.");
                AccountAvatarClientTests.Near(sentCrop.Size, server.LastCrop.Size, 0.000001,
                    "cropSize envoyé diffère du cadrage WPF.");

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
                AccountAvatarClientTests.Equal("D", accountState.Current.Initial,
                    "Le fallback officiel doit être l'initiale du compte.");

                window.Width = 1080;
                window.Height = 680;
                await DelayAndPumpAsync(180);
                AccountAvatarClientTests.True(window.AccountPage.ScrollHost.ScrollableWidth <= 0.5,
                    "La page Compte réelle ne doit pas déborder horizontalement à 1080 x 680.");
                SaveCapture(window, captureDirectory, "07-account-real-fallback-test-server-1080x680.png");

                window.Close();
                await PumpAsync(DispatcherPriority.Background);
                int requestsAfterClose = server.TotalRequests;
                adapter.Dispose();
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
    private TaskCompletionSource _uploadEntered = NewSignal();
    private TaskCompletionSource _uploadRelease = NewSignal();
    private TaskCompletionSource _deleteEntered = NewSignal();
    private TaskCompletionSource _deleteRelease = NewSignal();
    private TaskCompletionSource _profileEntered = NewSignal();
    private TaskCompletionSource? _profileRelease;
    private int _uploadBusy;
    private int _deleteBusy;
    private int _totalRequests;
    private int _deleteCalls;
    private string? _nextUploadFailureCode;
    private int _nextUploadFailureStatus;
    private string? _nextDeleteFailureCode;
    private int _nextDeleteFailureStatus;

    private AccountAvatarTestServer(WebApplication application, Uri baseUri)
    {
        _application = application;
        ApiBaseUri = new Uri(baseUri, "api/v1/");
        CurrentAvatar = AccountAvatarClientTests.Descriptor(1);
    }

    internal Uri ApiBaseUri { get; }
    internal AvatarDescriptor? CurrentAvatar { get; private set; }
    internal AvatarNormalizedCrop LastCrop { get; private set; }
    internal TaskCompletionSource UploadEntered => _uploadEntered;
    internal TaskCompletionSource DeleteEntered => _deleteEntered;
    internal TaskCompletionSource ProfileEntered => _profileEntered;
    internal int TotalRequests => Volatile.Read(ref _totalRequests);
    internal int DeleteCalls => Volatile.Read(ref _deleteCalls);

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
            TaskCompletionSource? release = server!._profileRelease;
            if (release is not null)
            {
                server._profileEntered.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
            }
            return Results.Json(new
            {
                accountId = 1,
                username = "Dono1402",
                email = "dono@example.test",
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
                server.CurrentAvatar = AccountAvatarClientTests.Descriptor(2);
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
        application.MapGet("/media/avatars/{avatarId}/{version:long}/{size:int}.png", (
            string avatarId,
            long version,
            int size) =>
        {
            AvatarDescriptor? current = server!.CurrentAvatar;
            if (current is null
                || !string.Equals(avatarId, current.AvatarId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || (ulong)version != current.Version
                || size is not (32 or 64 or 128 or 256))
            {
                return Results.NotFound();
            }
            return Results.File(AccountAvatarClientTests.CreatePng(size, size), "image/png");
        });
        await application.StartAsync();
        string address = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        server = new AccountAvatarTestServer(application, new Uri(address.EndsWith('/') ? address : address + "/"));
        return server;
    }

    internal void ResetUploadGate()
    {
        _uploadEntered = NewSignal();
        _uploadRelease = NewSignal();
    }

    internal void ReleaseUpload() => _uploadRelease.TrySetResult();

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

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
