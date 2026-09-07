using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class ChatFullShellWpfTests
{
    private static int _checks;
    private static readonly Guid Session = Guid.Parse("9cad51dc-e38c-4db6-8e2d-a2f86d7abc11");

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            async Task ExecuteAsync()
            {
                Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                string originalLocale = LauncherLocalization.CurrentLocale;
                try
                {
                    _checks = 0;
                    string directory = System.IO.Path.GetFullPath(captureDirectory ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atlas-chat-shell-" + Guid.NewGuid().ToString("N")));
                    Directory.CreateDirectory(directory);
                    foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
                    List<object> cases = [];
                    foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
                        cases.Add(await ValidateAsync(locale, directory));
                    await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "verification.json"), JsonSerializer.Serialize(new
                    {
                        status = "PASS", assertions = _checks,
                        method = "Fixed 1597.6x996.8 WPF shell, inactive offscreen WS_EX_NOACTIVATE; embedded WebView2 assets, synthetic snapshots and local media. No launcher runtime, authentication, backend or desktop input.",
                        cases
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"Chat full shell WPF PASS: {_checks} assertions; fixed 1597.6x996.8, FR/EN, transparent native WebView, local image, native avatar/profile overlay and routed file drop. Offscreen inactive fixture only.");
                    completion.TrySetResult(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); completion.TrySetResult(1); }
                finally { LauncherLocalization.SetLocale(originalLocale); application.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
        }) { IsBackground = true, Name = "AtlasChatFullShellOffscreenFixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }

    private static async Task<object> ValidateAsync(string locale, string directory)
    {
        LauncherLocalization.SetLocale(locale);
        string language = locale == LauncherLocalization.EnglishLocale ? "en" : "fr";
        using Stream asset = Application.GetResourceStream(new Uri("/WotLK.Launcher;component/Assets/Images/AtlasProfilePreview.png", UriKind.Relative))!.Stream;
        using MemoryStream media = new();
        asset.CopyTo(media);
        byte[] imageBytes = media.ToArray();
        BitmapSource avatar = Decode(imageBytes);
        ProfileUiState profile = LauncherV2PreviewData.CreateProfile(ProfilePreviewScenario.SignedIn, avatar);
        profile.ApplyAccountIdentity("Aster", true);
        profile.ApplyPresence(new(1, 42, "online", "online", false, true, false, null));
        ShellUiState shellState = LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready, isAuthenticated: true);
        shellState.ApplyAuthenticatedUser("Aster");
        shellState.ApplyProfileAvatar(avatar);
        LauncherShellV2 shell = new(shellState,
            LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateFriends(), profile,
            new SettingsUiState(SettingsUiState.Empty.Current with { IsRuntimeConnected = true }),
            new AccountUiState(AccountUiState.Empty.Current with { IsRuntimeConnected = true, Username = "Aster", Initial = "A", AvatarImage = avatar }),
            new AvatarCropUiState(AvatarCropUiState.Empty.Current))
        {
            Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false, ShowActivated = false
        };
        shell.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
        shell.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(shell).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
        };
        ChatViewV2 view = shell.ChatPage;
        view.RichUserDataFolder = System.IO.Path.Combine(directory, "webview-" + language + "-" + Guid.NewGuid().ToString("N"));
        List<string> mediaRequests = [];
        view.MediaResolver = (key, range, cancellation) =>
        {
            mediaRequests.Add(key);
            return Task.FromResult<ChatMediaStream?>(key is "attachments/fixture-image" or "avatars/42/1" or "avatars/91/1"
                ? new(new MemoryStream(imageBytes, writable: false), "image/png", imageBytes.Length) : null);
        };
        view.ApplyRichSnapshot(Snapshot(language, imageBytes.Length));
        view.SetRichMode(true);
        List<ChatFilesAddedEventArgs> drops = [];
        view.FilesAddedRequested += (_, args) => drops.Add(args);
        view.CanAcceptNativeDrop = () => view.IsVisible && shell.CurrentOverlay == ShellOverlayKind.None;
        shell.Show();
        try
        {
            await Layout(shell);
            ((Button)shell.FindName("MessagesNavigationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => view.IsVisible && view.RichBrowser?.CoreWebView2 is not null, "Messages navigation opens the native chat WebView.");
            CoreWebView2 core = view.RichBrowser!.CoreWebView2;
            await UntilScript(core, "document.querySelector('#thread-title')?.textContent==='Lyra'", "Native snapshot is rendered.");
            await UntilScript(core, "[...document.querySelectorAll('.attachment-image img')].some(i=>i.complete&&i.naturalWidth>0)", "Local attachment image is decoded by the real WebView.");
            await UntilScript(core, "document.fonts.status==='loaded'&&[...document.images].every(i=>i.complete&&i.naturalWidth>0)", "Fonts and fixture avatars are decoded before the full-shell capture.");
            await Until(() => view.IsRichComposerAcceptingFiles, "Composer grants native file admission.");
            await Task.Delay(600);
            await Layout(shell);
            FrameworkElement content = (FrameworkElement)shell.Content;
            Rect viewBounds = Bounds(view, content);
            JsonElement dom = await Script(core, "(() => {const rect=s=>{const r=document.querySelector(s).getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height};}; return {width:innerWidth,height:innerHeight,dpr:devicePixelRatio,html:getComputedStyle(document.documentElement).backgroundColor,body:getComputedStyle(document.body).backgroundColor,colorScheme:getComputedStyle(document.documentElement).colorScheme,image:rect('.attachment-image img')};})()");
            Console.WriteLine($"Full shell {language}: WPF {content.ActualWidth}x{content.ActualHeight}; chat {viewBounds}; DOM {dom.GetRawText()}");
            Check(Math.Abs(shell.Width - 1597.6) < .01 && Math.Abs(shell.Height - 996.8) < .01 && shell.ResizeMode == ResizeMode.CanMinimize,
                "Product shell retains its fixed dimensions and CanMinimize resize mode.");
            Check(((FrameworkElement)shell.FindName("MaximizeWindowButton")).Visibility == Visibility.Collapsed, "Fixed shell has no maximize button.");
            Check(!shell.IsActive && !shell.ShowActivated && !shell.ShowInTaskbar && shell.Left < -10000 && shell.Top < -10000,
                "Fixture remains inactive and outside the desktop.");
            Check((GetWindowLong(new WindowInteropHelper(shell).Handle, -20) & 0x08000000) != 0, "Native no-activation flag remains set.");
            Check(view.RichBrowser.DefaultBackgroundColor.A == 0, "Native WebView default canvas is transparent.");
            Check(dom.GetProperty("html").GetString() == "rgba(0, 0, 0, 0)" && dom.GetProperty("body").GetString() == "rgba(0, 0, 0, 0)", "HTML and body allow the native backdrop through.");
            Check(((FrameworkElement)shell.FindName("SecondaryBackdrop")).IsVisible, "The native Citadel backdrop is visible behind the message page.");
            Check(mediaRequests.Contains("attachments/fixture-image"), "Image comes from the native media resolver.");
            Check(((Ellipse)shell.FindName("ShellProfileAvatarImage")).Fill is ImageBrush { ImageSource: not null }, "Real header avatar image is present.");
            await UntilScript(core, "!document.querySelector('#share-game-button').hidden", "The character Armory gamepad is visible beside attach and send.");

            byte[] webBytes = await CaptureWebAsync(core, System.IO.Path.Combine(directory, $"chat-{language}-webview.png"));
            BitmapSource web = Decode(webBytes);
            BitmapSource direct = Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-shell-wpf-direct.png"));
            view.Visibility = Visibility.Hidden;
            await Layout(shell);
            BitmapSource baseline = Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-native-backdrop.png"));
            view.Visibility = Visibility.Visible;
            await Layout(shell);
            await Until(() => view.IsRichComposerAcceptingFiles, "Composer remains available after capture baseline.");
            List<object> transparentSamples = [];
            foreach (Point point in new[] { new Point(1000, 150), new Point(1200, 130), new Point(1200, 175) })
            {
                Pixel actual = ReadPixel(direct, (int)point.X, (int)point.Y);
                Pixel backdrop = ReadPixel(baseline, (int)point.X, (int)point.Y);
                Pixel browser = ReadPixel(web, (int)point.X, (int)(point.Y - viewBounds.Top));
                Check(browser.A == 0, "Blank heading pixels in the real browser PNG have zero alpha.");
                Check(actual == backdrop, "Full-shell pixels below the header and around Messages exactly match the one native backdrop.");
                transparentSamples.Add(new { x = point.X, y = point.Y, actual, backdrop, browser });
            }
            JsonElement imageRect = dom.GetProperty("image");
            int imageX = (int)(imageRect.GetProperty("x").GetDouble() + imageRect.GetProperty("width").GetDouble() / 2);
            int imageY = (int)(imageRect.GetProperty("y").GetDouble() + imageRect.GetProperty("height").GetDouble() / 2);
            Pixel imageWeb = ReadPixel(web, imageX, imageY);
            Pixel imageDirect = ReadPixel(direct, imageX, imageY + (int)viewBounds.Top);
            Pixel imageBackdrop = ReadPixel(baseline, imageX, imageY + (int)viewBounds.Top);
            Check(imageWeb.A == 255 && RgbDifference(imageWeb, imageDirect) <= 12,
                "WPF RenderTargetBitmap directly contains the decoded WebView image pixels at the measured native coordinates.");
            Check(RgbDifference(imageDirect, imageBackdrop) > 70, "Image proof is distinct from the native background, not an empty composition layer.");
            Pixel panelWeb = ReadPixel(web, 1200, 500 - (int)viewBounds.Top);
            Pixel panelDirect = ReadPixel(direct, 1200, 500);
            Pixel panelBackdrop = ReadPixel(baseline, 1200, 500);
            Check(panelWeb.A is > 100 and < 230, "Message panel is visibly tinted while remaining translucent.");
            Check(RgbDifference(Over(panelWeb, panelBackdrop), panelDirect) <= 9, "Direct full-shell panel pixels are the WebView tint over the same native Citadel image.");

            string dropFile = System.IO.Path.Combine(directory, "native-drop-fixture.png");
            await File.WriteAllBytesAsync(dropFile, imageBytes);
            RaiseDrop(view, dropFile);
            Check(drops.Count == 1 && drops[0].IsNativeDrop && drops[0].Paths.SequenceEqual([dropFile]), "Real WPF routed FileDrop reaches native ingestion exactly once.");
            Check(drops[0].SessionId == Session && drops[0].OwnerAccountId == 42 && drops[0].ConversationId == "17", "File drop keeps the displayed account and thread.");
            ((Button)shell.FindName("ProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            await Layout(shell);
            Check(shell.ProfileOverlay.IsOpen && shell.ProfileOverlay.IsVisible, "Native avatar opens its profile overlay over Messages.");
            Check(((Ellipse)shell.ProfileOverlay.FindName("MenuProfileAvatarImage")).Fill is ImageBrush { ImageSource: not null }, "Profile menu retains its native avatar image.");
            RaiseDrop(view, dropFile);
            Check(drops.Count == 1, "Profile overlay blocks native file admission.");
            BitmapSource profileCapture = Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-profile-wpf-direct.png"));
            Button profileClose = (Button)shell.ProfileOverlay.FindName("CloseProfileButton");
            Rect closeBounds = Bounds(profileClose, content);
            DependencyObject? hit = content.InputHitTest(new Point(closeBounds.X + closeBounds.Width / 2, closeBounds.Y + closeBounds.Height / 2)) as DependencyObject;
            while (hit is not null && !ReferenceEquals(hit, profileClose)) hit = VisualTreeHelper.GetParent(hit);
            Check(ReferenceEquals(hit, profileClose), "Native profile close button wins hit testing over the WebView surface.");
            Check(RgbDifference(ReadPixel(profileCapture, 1200, 200), ReadPixel(direct, 1200, 200)) > 20,
                "Direct full-shell capture includes the native profile layer above browser content.");
            profileClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            await Layout(shell);
            Check(!shell.ProfileOverlay.IsOpen, "Profile overlay closes through its real button.");
            Check(!shell.IsActive, "Navigation, captures, avatar and synthetic drops never activate the fixture.");
            return new { locale, shellWidth = content.ActualWidth, shellHeight = content.ActualHeight,
                view = new { x = viewBounds.X, y = viewBounds.Y, width = viewBounds.Width, height = viewBounds.Height }, dom,
                webPixelWidth = web.PixelWidth, webPixelHeight = web.PixelHeight,
                directPixelWidth = direct.PixelWidth, directPixelHeight = direct.PixelHeight,
                baselinePixelWidth = baseline.PixelWidth, baselinePixelHeight = baseline.PixelHeight,
                transparentSamples,
                imageSample = new { x = imageX, y = imageY + (int)viewBounds.Top, browser = imageWeb, actual = imageDirect, backdrop = imageBackdrop },
                panelSample = new { x = 1200, y = 500, browser = panelWeb, actual = panelDirect, backdrop = panelBackdrop },
                captureMethod = "Direct WPF RenderTargetBitmap of actual full shell content, including WebView2CompositionControl, native header, single Citadel backdrop and profile overlay. No compositing or duplicated artwork. Pixel checks compare with the native backdrop and the independent transparent CoreWebView2 PNG."
            };
        }
        finally { view.DisposeRich(); shell.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    }

    private static object Snapshot(string language, int imageLength)
    {
        bool en = language == "en";
        ChatProfileDto self = new() { AccountId = 42, Username = "Aster", Presence = "online", AvatarUrl = RichAvatar(42), AvatarVersion = "1" };
        ChatProfileDto friend = new() { AccountId = 91, Username = "Lyra", Presence = "online", AvatarUrl = RichAvatar(91), AvatarVersion = "1", CharacterName = "Lyralune", CharacterClass = "Mage", ZoneName = "Dalaran", CharacterGuid = 911 };
        ChatMessageDto[] messages =
        [
            new() { Id = 9007199254741001, ThreadId = "17", Sender = friend, Body = en ? "Ready for Icecrown tonight?" : "On se retrouve à la Citadelle ce soir ?", CreatedAt = DateTimeOffset.Parse("2026-09-07T08:00:00Z"), Version = 1 },
            new() { Id = 9007199254741002, ThreadId = "17", Sender = self, Body = en ? "Absolutely. I'll bring the flasks!" : "Avec plaisir. Je prépare les flacons !", CreatedAt = DateTimeOffset.Parse("2026-09-07T08:01:00Z"), Version = 1 },
            new() { Id = 9007199254741003, ThreadId = "17", Sender = friend, Body = en ? "A little souvenir from our last adventure." : "Un petit souvenir de notre dernière aventure.", CreatedAt = DateTimeOffset.Parse("2026-09-07T08:02:00Z"), Version = 1,
                Attachments = [new() { Id = "fixture-image", FileName = "portrait.png", Kind = "image", ContentType = "image/png", Size = imageLength, Url = ChatViewV2.RichMediaOrigin + "attachments/fixture-image" }] }
        ];
        return new
        {
            type = "snapshot", sessionId = Session, ownerAccountId = 42u, sequence = 10L, locale = language, isActive = true,
            isAvailable = true, isLoading = false,
            state = new ChatStateDto { Self = self, Contacts = [friend], Capabilities = ["markdown", "replies", "reactions", "attachments", "cards"],
                Threads = [new() { Id = "17", Title = "Lyra", Kind = "direct", CanSend = true, Members = [new() { Profile = self }, new() { Profile = friend }], LastMessage = messages[^1] }] },
            selectedThreadId = "17", messages,
            draft = new { body = "", attachments = Array.Empty<object>() }, ownCharacters = new { status = "ready", characters = Array.Empty<object>() },
            pending = Array.Empty<object>(), typing = Array.Empty<object>(), hasEarlier = false, isLoadingEarlier = false
        };
    }

    private static string RichAvatar(uint id) => ChatViewV2.RichMediaOrigin + $"avatars/{id}/1";
    private static void RaiseDrop(ChatViewV2 view, string file)
    {
        DataObject data = new(DataFormats.FileDrop, new[] { file });
        foreach (RoutedEvent routedEvent in new[] { DragDrop.PreviewDragEnterEvent, DragDrop.PreviewDropEvent })
        {
            DragEventArgs args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [data, DragDropKeyStates.None, DragDropEffects.Copy, view.RichBrowser!, new Point(40, 40)], culture: null)!;
            args.RoutedEvent = routedEvent;
            view.RichBrowser!.RaiseEvent(args);
            Check(args.Handled, "Native file drag is consumed by the WPF preview handler.");
        }
    }
    private static BitmapSource Decode(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        BitmapSource bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        bitmap.Freeze();
        return bitmap;
    }
    private static async Task<byte[]> CaptureWebAsync(CoreWebView2 core, string path)
    {
        using MemoryStream stream = new();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        byte[] bytes = stream.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        return bytes;
    }
    private static BitmapSource Capture(FrameworkElement content, string path)
    {
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path); encoder.Save(stream);
        return bitmap;
    }
    private readonly record struct Pixel(byte B, byte G, byte R, byte A);
    private static Pixel ReadPixel(BitmapSource bitmap, int x, int y)
    {
        FormatConvertedBitmap converted = new(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return new(pixel[0], pixel[1], pixel[2], pixel[3]);
    }
    private static int RgbDifference(Pixel left, Pixel right) => Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
    private static Pixel Over(Pixel foreground, Pixel background)
    {
        double alpha = foreground.A / 255d;
        return new((byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)), 255);
    }
    private static async Task Layout(Window window) { window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static async Task<JsonElement> Script(CoreWebView2 core, string script) { using JsonDocument document = JsonDocument.Parse(await core.ExecuteScriptAsync(script)); return document.RootElement.Clone(); }
    private static async Task UntilScript(CoreWebView2 core, string script, string message)
    {
        for (int attempt = 0; attempt < 250; attempt++) { if (await core.ExecuteScriptAsync(script) == "true") { Check(true, message); return; } await Task.Delay(40); }
        throw new InvalidOperationException(message);
    }
    private static async Task Until(Func<bool> predicate, string message)
    {
        for (int attempt = 0; attempt < 500; attempt++) { if (predicate()) { Check(true, message); return; } await Task.Delay(40); }
        throw new InvalidOperationException(message);
    }
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
