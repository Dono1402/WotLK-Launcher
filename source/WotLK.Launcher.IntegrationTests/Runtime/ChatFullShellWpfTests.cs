using System.IO;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                    Dictionary<string, string> assetHashes = new(StringComparer.Ordinal);
                    foreach (string name in new[] { "index.html", "chat.css", "chat.js", "chat-search.js", "chat-render.js", "chat-media.js", "chat-media.css" })
                    {
                        using Stream embedded = ChatViewV2.OpenRichAsset(new Uri(ChatViewV2.RichOrigin + name)).Stream
                            ?? throw new InvalidOperationException("Missing embedded Messages asset: " + name);
                        assetHashes[name] = Convert.ToHexString(SHA256.HashData(embedded)).ToLowerInvariant();
                    }
                    await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "verification.json"), JsonSerializer.Serialize(new
                    {
                        status = "PASS", assertions = _checks,
                        method = "Fixed 1597.6x996.8 WPF shell, inactive offscreen WS_EX_NOACTIVATE; embedded WebView2 assets, synthetic snapshots and local media. No launcher runtime, authentication, backend or desktop input.",
                        assetHashes,
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
        byte[] audioBytes = SilentWave();
        using Stream videoFixture = typeof(ChatFullShellWpfTests).Assembly.GetManifestResourceStream("WotLK.Launcher.IntegrationTests.Runtime.Fixtures.player-video.webm")
            ?? throw new InvalidOperationException("Missing four-second synthetic VP8 fixture.");
        using MemoryStream videoBuffer = new();
        videoFixture.CopyTo(videoBuffer);
        byte[] videoBytes = videoBuffer.ToArray();
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
            ChatMediaStream? result = key switch
            {
                "attachments/fixture-image" or "avatars/42/1" or "avatars/91/1" or "uploads/fixture-image" => new(new MemoryStream(imageBytes, writable: false), "image/png", imageBytes.Length),
                "uploads/fixture-audio" => FixtureMedia(audioBytes, "audio/wav", range),
                "uploads/fixture-video" => FixtureMedia(videoBytes, "video/webm", range),
                _ => null
            };
            return Task.FromResult(result);
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
            await UntilScript(core, "document.fonts.status==='loaded'&&[...document.images].filter(i=>i.hasAttribute('src')).every(i=>i.complete&&i.naturalWidth>0)", "Fonts and fixture avatars with a source are decoded before the full-shell capture.");
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
            Check(dom.GetProperty("width").GetInt32() == 1597 && dom.GetProperty("height").GetInt32() == 872, "The native Messages viewport remains 1597 by 872 CSS pixels after removing its heading.");
            await UntilScript(core, "!document.querySelector('.page-heading,#page-subtitle,#composer-error')&&document.querySelector('.chat-layout').getBoundingClientRect().top<=24", "No Messages title, subtitle, reserved heading band or global composer error remains.");
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
            foreach (Point point in new[] { new Point(2, viewBounds.Top + 4), new Point(1594, viewBounds.Top + 4) })
            {
                Pixel actual = ReadPixel(direct, (int)point.X, (int)point.Y);
                Pixel backdrop = ReadPixel(baseline, (int)point.X, (int)point.Y);
                Pixel browser = ReadPixel(web, (int)point.X, (int)(point.Y - viewBounds.Top));
                Check(browser.A == 0, "Blank margin pixels above the panel in the real browser PNG have zero alpha.");
                Check(actual == backdrop, "Full-shell pixels immediately below the header exactly match the one native backdrop.");
                transparentSamples.Add(new { x = point.X, y = point.Y, actual, backdrop, browser });
            }
            List<object> marginShadowSamples = [];
            foreach (Point point in new[] { new Point(1000, viewBounds.Top + 4), new Point(1200, viewBounds.Top + 10), new Point(1500, viewBounds.Top + 15) })
            {
                Pixel actual = ReadPixel(direct, (int)point.X, (int)point.Y);
                Pixel backdrop = ReadPixel(baseline, (int)point.X, (int)point.Y);
                Pixel browser = ReadPixel(web, (int)point.X, (int)(point.Y - viewBounds.Top));
                Check(browser.A <= 8, "The upper margin contains only the panel's faint shadow, never an opaque band.");
                Check(RgbDifference(Over(browser, backdrop), actual) <= 6, "Upper-margin pixels remain the same native backdrop beneath the measured panel shadow.");
                marginShadowSamples.Add(new { x = point.X, y = point.Y, actual, backdrop, browser });
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

            await core.ExecuteScriptAsync("document.querySelector('.attachment-image').focus();document.querySelector('.attachment-image').click()");
            await UntilScript(core, "document.querySelector('#image-dialog')?.open&&document.querySelector('#image-dialog-image')?.complete&&document.querySelector('#image-dialog-image')?.naturalWidth>0", "The received image opens at full lightbox size inside the real WebView.");
            await Until(() => !view.IsRichComposerAcceptingFiles, "The open image lightbox revokes native file admission before any clipboard or picker read.");
            Check(!view.CanAcceptRichFileRequest(JsonSerializer.SerializeToElement(new { type = "action", requestId = Guid.NewGuid().ToString(), sessionId = Session, ownerAccountId = 42u, sequence = "10", action = "pasteImage", payload = new { threadId = "17" } }, ChatJson.Options)),
                "Native clipboard file requests cannot bypass the lightbox composer gate.");
            await UntilScript(core, "!document.querySelector('#image-dialog button,#image-dialog footer,#image-dialog .dialog-heading')&&document.querySelector('#image-dialog-image').getBoundingClientRect().height>500", "The enlarged image has no download button, close button or frame header.");
            // WebView DOM completion precedes the asynchronous WPF composition frame.
            await Task.Delay(500);
            await CaptureWebAsync(core, System.IO.Path.Combine(directory, $"chat-{language}-image-lightbox-webview.png"));
            await Layout(shell);
            Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-image-lightbox-wpf-direct.png"));
            await core.ExecuteScriptAsync("document.querySelector('#image-dialog').click()");
            await UntilScript(core, "!document.querySelector('#image-dialog').open&&document.activeElement===document.querySelector('.attachment-image')", "Lightbox background closes the image and restores the original preview focus.");
            await Until(() => view.IsRichComposerAcceptingFiles, "Closing the image lightbox restores native file admission for the same thread.");

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
            await ValidateConversationSearchAsync(core, shell, content, language, directory, imageBytes.Length);
            await ValidateFollowupMediaAsync(core, view, shell, content, language, directory, imageBytes.Length, audioBytes.Length, videoBytes.Length);
            Check(!shell.IsActive, "Navigation, captures, avatar and synthetic drops never activate the fixture.");
            return new { locale, shellWidth = content.ActualWidth, shellHeight = content.ActualHeight,
                view = new { x = viewBounds.X, y = viewBounds.Y, width = viewBounds.Width, height = viewBounds.Height }, dom,
                webPixelWidth = web.PixelWidth, webPixelHeight = web.PixelHeight,
                directPixelWidth = direct.PixelWidth, directPixelHeight = direct.PixelHeight,
                baselinePixelWidth = baseline.PixelWidth, baselinePixelHeight = baseline.PixelHeight,
                transparentSamples, marginShadowSamples,
                imageSample = new { x = imageX, y = imageY + (int)viewBounds.Top, browser = imageWeb, actual = imageDirect, backdrop = imageBackdrop },
                panelSample = new { x = 1200, y = 500, browser = panelWeb, actual = panelDirect, backdrop = panelBackdrop },
                captureMethod = "Direct WPF RenderTargetBitmap of actual full shell content, including WebView2CompositionControl, native header, single Citadel backdrop and profile overlay. No compositing or duplicated artwork. Pixel checks compare with the native backdrop and the independent transparent CoreWebView2 PNG."
            };
        }
        finally { view.DisposeRich(); shell.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    }

    private static async Task ValidateConversationSearchAsync(CoreWebView2 core, LauncherShellV2 shell,
        FrameworkElement content, string language, string directory, int imageLength)
    {
        await UntilScript(core, "typeof AtlasChatSearch?.create==='function'&&document.querySelector('#conversation-search').hidden",
            "Packaged search asset is available and does not add a permanent search control.");
        // The host correctly reports an inactive offscreen window. Exercise the
        // focused UI branch with synthetic DOM state, without activating an HWND.
        await Script(core, $"AtlasChat.receive({JsonSerializer.Serialize(Snapshot(language, imageLength), ChatJson.Options)});true");
        await Script(core, "(() => {const input=document.querySelector('#composer-input');input.value='Draft for native search';input.dispatchEvent(new Event('input',{bubbles:true}));input.focus({preventScroll:true});input.setSelectionRange(3,8);document.dispatchEvent(new KeyboardEvent('keydown',{key:'f',ctrlKey:true,bubbles:true,cancelable:true}));return true;})()");
        await UntilScript(core, "!document.querySelector('#conversation-search').hidden&&document.activeElement===document.querySelector('#conversation-search-input')",
            "Ctrl+F handled inside the inactive native WebView opens and focuses conversation search.");
        string query = JsonSerializer.Serialize(language == "en" ? "Icecrown" : "Citadelle");
        await Script(core, $"(() => {{const input=document.querySelector('#conversation-search-input');input.value={query};input.dispatchEvent(new Event('input',{{bubbles:true}}));return true;}})()");
        await UntilScript(core, "document.querySelectorAll('mark.search-match.is-current').length===1&&document.querySelector('#conversation-search').dataset.coverage==='complete'",
            "Native search highlights the localized fixture message and reports complete available history.");
        await UntilScript(core, "document.documentElement.scrollWidth===innerWidth&&document.querySelector('#composer-input').getBoundingClientRect().bottom<=innerHeight",
            "Search fits above the timeline without hiding the composer at the fixed launcher dimensions.");
        await Layout(shell);
        await CaptureWebAsync(core, System.IO.Path.Combine(directory, $"chat-{language}-search-webview.png"));
        Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-search-wpf-direct.png"));
        await Script(core, "document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}));true");
        await UntilScript(core, "(() => {const input=document.querySelector('#composer-input');return document.querySelector('#conversation-search').hidden&&!document.querySelector('mark.search-match')&&document.activeElement===input&&input.value==='Draft for native search'&&input.selectionStart===3&&input.selectionEnd===8;})()",
            "Escape clears search highlights and restores the draft, focus and selection inside the native WebView.");
        await Script(core, "(() => {const input=document.querySelector('#composer-input');input.value='';input.dispatchEvent(new Event('input',{bubbles:true}));return true;})()");
        Check(!shell.IsActive, "Search keyboard events target only WebView DOM and never activate the native fixture.");
    }

    internal static object Snapshot(string language, int imageLength)
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

    private static async Task ValidateFollowupMediaAsync(CoreWebView2 core, ChatViewV2 view, LauncherShellV2 shell, FrameworkElement content,
        string language, string directory, int imageLength, int audioLength, int videoLength)
    {
        JsonObject previews = JsonSerializer.SerializeToNode(Snapshot(language, imageLength), ChatJson.Options)!.AsObject();
        previews["sequence"] = "11";
        previews["draft"] = JsonSerializer.SerializeToNode(new
        {
            body = language == "en" ? "Three previews before sending." : "Trois aperçus avant l’envoi.",
            attachments = new[]
            {
                new { id = "fixture-image", fileName = "Portrait privé.png", contentType = "image/png", size = imageLength.ToString(), offset = imageLength.ToString(), status = "ready", isComplete = true, previewUrl = ChatViewV2.RichMediaOrigin + "uploads/fixture-image" },
                new { id = "fixture-video", fileName = "Souvenir privé.webm", contentType = "video/webm", size = videoLength.ToString(), offset = videoLength.ToString(), status = "ready", isComplete = true, previewUrl = ChatViewV2.RichMediaOrigin + "uploads/fixture-video" },
                new { id = "fixture-audio", fileName = "Note privée.wav", contentType = "audio/wav", size = audioLength.ToString(), offset = audioLength.ToString(), status = "ready", isComplete = true, previewUrl = ChatViewV2.RichMediaOrigin + "uploads/fixture-audio" }
            }
        }, ChatJson.Options);
        view.ApplyRichSnapshot(previews);
        await UntilScript(core, "document.querySelectorAll('.queued-file').length===3&&[...document.querySelectorAll('.queued-preview audio,.queued-preview video')].length===2&&[...document.querySelectorAll('.queued-preview audio,.queued-preview video')].every(m=>m.readyState>=1&&!m.error)", "The real WebView decodes queued WAV audio and WebM video through the local native media resolver.");
        await UntilScript(core, "[...document.querySelectorAll('.queued-preview audio,.queued-preview video')].every(m=>Math.abs(m.duration-4)<.02)", "Both native playback fixtures have a real four-second duration.");
        await UntilScript(core, "[...document.querySelectorAll('.queued-file')].every(n=>!n.innerText.includes('privé')&&!n.innerText.includes('privée'))&&!document.querySelector('.queued-file-name,.queued-file-status')", "Queued media show their preview and player times without file name, size or Ready metadata.");
        await Task.Delay(500);
        await Layout(shell);
        await CaptureWebAsync(core, System.IO.Path.Combine(directory, $"chat-{language}-queued-media-webview.png"));
        Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-queued-media-wpf-direct.png"));

        foreach (string kind in new[] { "audio", "video" })
        {
            await core.ExecuteScriptAsync($"window.atlasNativeViewerProbe=document.querySelector('.queued-file.is-{kind} {kind}');window.atlasNativeViewerProbe.volume=.37;window.atlasNativeViewerProbe.playbackRate=1.25;window.atlasNativeViewerProbe.muted=true");
            await CapturePlayerControlsAsync(core, directory, $"chat-{language}-{kind}-before-open");
            await core.ExecuteScriptAsync($"document.querySelector('.queued-file.is-{kind} .media-expand').click()");
            await UntilScript(core, $"document.querySelector('#image-dialog').open&&document.querySelector('#image-dialog').classList.contains('is-{kind}')&&document.querySelector('#media-dialog-player-host {kind}')===window.atlasNativeViewerProbe&&!window.atlasNativeViewerProbe.controls&&window.atlasNativeViewerProbe.readyState>=1&&!window.atlasNativeViewerProbe.error&&!!window.atlasNativeViewerProbe.closest('.media-player')?.querySelector('.media-play')",
                $"The native {kind} viewer preserves its decoded player and exposes custom controls.");
            await Until(() => !view.IsRichComposerAcceptingFiles, $"The enlarged {kind} viewer blocks native file admission.");
            await UntilScript(core, "(() => {const r=window.atlasNativeViewerProbe.closest('.media-player').getBoundingClientRect();return r.width>=480&&r.height>0&&r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight;})()",
                $"The enlarged {kind} player stays inside the fixed native Messages viewport.");
            await Task.Delay(500);
            await Layout(shell);
            await CaptureWebAsync(core, System.IO.Path.Combine(directory, $"chat-{language}-{kind}-viewer-webview.png"));
            Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-{kind}-viewer-wpf-direct.png"));
            await CapturePlayerControlsAsync(core, directory, $"chat-{language}-{kind}-viewer");
            await core.ExecuteScriptAsync("window.atlasNativeViewerProbe.closest('.media-player').querySelector('.media-volume-button').click()");
            await UntilScript(core, "(() => {const p=window.atlasNativeViewerProbe.closest('.media-player').querySelector('.media-volume-panel'),r=p.getBoundingClientRect();return p.matches(':popover-open')&&r.width>100&&r.left>=0&&r.top>=0&&r.right<=innerWidth&&r.bottom<=innerHeight;})()", "The custom volume popover remains visible inside the native viewport.");
            await CaptureWebAsync(core, System.IO.Path.Combine(directory, $"chat-{language}-{kind}-volume-webview.png"));
            await Task.Delay(200);
            await Layout(shell);
            Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-{kind}-volume-wpf-direct.png"));
            await core.ExecuteScriptAsync("window.atlasNativeViewerProbe.closest('.media-player').querySelector('.media-volume-button').click()");
            await core.ExecuteScriptAsync("document.querySelector('#image-dialog').click()");
            await UntilScript(core, $"!document.querySelector('#image-dialog').open&&document.querySelector('.queued-file.is-{kind} {kind}')===window.atlasNativeViewerProbe&&window.atlasNativeViewerProbe.readyState>=1&&!window.atlasNativeViewerProbe.error&&document.activeElement===document.querySelector('.queued-file.is-{kind} .media-expand')",
                $"Closing the native {kind} viewer restores the same decoded inline player and opener focus.");
            await Until(() => view.IsRichComposerAcceptingFiles, $"Closing the {kind} viewer restores native file admission.");
            await Task.Delay(500);
            await CapturePlayerControlsAsync(core, directory, $"chat-{language}-{kind}-restored");
            await core.ExecuteScriptAsync("window.atlasNativePauseCount=0;window.atlasNativeCountPause=()=>window.atlasNativePauseCount++;window.atlasNativeViewerProbe.loop=true;window.atlasNativeViewerProbe.addEventListener('pause',window.atlasNativeCountPause);window.atlasNativeViewerProbe.closest('.media-player').querySelector('.media-play').click()");
            await UntilScript(core, "!window.atlasNativeViewerProbe.paused&&window.atlasNativeViewerProbe.currentTime>0", "The custom Play command starts decoded muted fixture media.");
            await core.ExecuteScriptAsync($"document.querySelector('.queued-file.is-{kind} .media-expand').click()");
            await UntilScript(core, "document.querySelector('#image-dialog').open&&!window.atlasNativeViewerProbe.paused&&window.atlasNativePauseCount===0", "Opening the enlarged player preserves ongoing playback without a pause event.");
            await core.ExecuteScriptAsync("document.querySelector('#image-dialog').click()");
            await UntilScript(core, "!document.querySelector('#image-dialog').open&&!window.atlasNativeViewerProbe.paused&&window.atlasNativePauseCount===0", "Closing the enlarged player preserves ongoing playback without a pause event.");
            await core.ExecuteScriptAsync("window.atlasNativeViewerProbe.closest('.media-player').querySelector('.media-play').click()");
            await UntilScript(core, "window.atlasNativeViewerProbe.paused&&window.atlasNativePauseCount===1", "Only the explicit custom Pause command stops playback.");
            await core.ExecuteScriptAsync("window.atlasNativeViewerProbe.removeEventListener('pause',window.atlasNativeCountPause);window.atlasNativeViewerProbe.loop=false;delete window.atlasNativeCountPause;delete window.atlasNativePauseCount");
        }
        await Task.Delay(500);
        await Layout(shell);
        await CaptureWebAsync(core, System.IO.Path.Combine(directory, $"chat-{language}-restored-media-webview.png"));
        Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-restored-media-wpf-direct.png"));
        await core.ExecuteScriptAsync("delete window.atlasNativeViewerProbe");

        JsonObject cards = JsonSerializer.SerializeToNode(Snapshot(language, imageLength), ChatJson.Options)!.AsObject();
        cards["sequence"] = "12";
        cards["messages"] = JsonSerializer.SerializeToNode(new[]
        {
            new { id = "9007199254741010", clientMessageId = "10000000-0000-4000-8000-000000000010", threadId = "17", sender = new { accountId = 42, username = "Aster", presence = "offline", avatarUrl = RichAvatar(42) },
                body = language == "en" ? "My character for tonight." : "Mon personnage pour ce soir.", createdAt = "2026-09-07T08:03:00Z", version = "1",
                card = new { kind = "character", title = "Asterion", referenceId = "4294967295", fields = new { ownerAccountId = "42", characterGuid = "4294967295", level = "80", classId = "6", realmName = "Arthas" } } }
        }, ChatJson.Options);
        cards["pending"] = JsonSerializer.SerializeToNode(new[]
        {
            new { clientMessageId = "10000000-0000-4000-8000-000000000011", threadId = "17", body = language == "en" ? "This send was refused." : "Cet envoi a été refusé.",
                status = "failed", canCancel = true, errorCode = "chat-forbidden", createdAt = "2026-09-07T08:04:00Z" }
        }, ChatJson.Options);
        cards["state"]!["self"]!["presence"] = "dnd";
        view.ApplyRichSnapshot(cards);
        await UntilScript(core, "document.querySelector('.character-card-meta')?.textContent.includes('80')&&!!document.querySelector('[data-client-message-id] .message-state.is-failed')", "Character metadata and the error attached to its own failed send render in the native WebView.");
        await UntilScript(core, "[...document.querySelectorAll('.message-avatar .presence-dot')].length===2&&[...document.querySelectorAll('.message-avatar .presence-dot')].every(dot=>dot.dataset.presence==='dnd')", "Historical and pending self messages both use the current global status rather than the sender's old value.");
        await Task.Delay(500);
        await CaptureWebAsync(core, System.IO.Path.Combine(directory, $"chat-{language}-character-error-webview.png"));
        await Layout(shell);
        Capture(content, System.IO.Path.Combine(directory, $"chat-{language}-character-error-wpf-direct.png"));
        await UntilScript(core, "document.querySelector('.character-card-action')&&document.querySelector('#composer-box').getBoundingClientRect().bottom<=innerHeight&&document.documentElement.scrollWidth===innerWidth", "The character action and composer remain visible at the fixed native size.");
    }


    internal static byte[] SilentWave()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(64036); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(64000); writer.Write(new byte[64000]);
        return stream.ToArray();
    }
    internal static ChatMediaStream FixtureMedia(byte[] bytes, string contentType, string? range)
    {
        if (string.IsNullOrEmpty(range)) return new(new MemoryStream(bytes, writable: false), contentType, bytes.Length);
        if (!RangeHeaderValue.TryParse(range, out RangeHeaderValue? parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)
            throw new InvalidOperationException("Unexpected fixture media range.");
        RangeItemHeaderValue item = parsed.Ranges.Single();
        long start = item.From ?? Math.Max(0, bytes.Length - (item.To ?? 0));
        long end = item.From is null ? bytes.Length - 1 : Math.Min(bytes.Length - 1, item.To ?? bytes.Length - 1);
        if (start < 0 || start >= bytes.Length || end < start) return new(Stream.Null, contentType, 0, $"bytes */{bytes.Length}", 416);
        int count = checked((int)(end - start + 1));
        return new(new MemoryStream(bytes, checked((int)start), count, writable: false), contentType, count, $"bytes {start}-{end}/{bytes.Length}", 206);
    }
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
    private static async Task CapturePlayerControlsAsync(CoreWebView2 core, string directory, string scenario)
    {
        using JsonDocument remote = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"window.atlasNativeViewerProbe.closest('.media-player')\"}"));
        string objectId = remote.RootElement.GetProperty("result").GetProperty("objectId").GetString()!;
        using JsonDocument described = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.describeNode", JsonSerializer.Serialize(new { objectId })));
        int backendId = described.RootElement.GetProperty("node").GetProperty("backendNodeId").GetInt32();
        string rawTree = await core.CallDevToolsProtocolMethodAsync("Accessibility.getFullAXTree", "{}");
        using JsonDocument tree = JsonDocument.Parse(rawTree);
        Dictionary<string, JsonElement> byId = tree.RootElement.GetProperty("nodes").EnumerateArray().ToDictionary(node => node.GetProperty("nodeId").GetString()!);
        JsonElement playerNode = byId.Values.FirstOrDefault(node => node.TryGetProperty("backendDOMNodeId", out JsonElement id) && id.GetInt32() == backendId);
        List<JsonElement> descendants = [];
        Queue<string> queue = new();
        if (playerNode.ValueKind != JsonValueKind.Undefined && playerNode.TryGetProperty("childIds", out JsonElement childIds))
            foreach (JsonElement child in childIds.EnumerateArray()) queue.Enqueue(child.GetString()!);
        while (queue.TryDequeue(out string? id))
        {
            if (!byId.TryGetValue(id, out JsonElement current)) continue;
            descendants.Add(current);
            if (current.TryGetProperty("childIds", out JsonElement children))
                foreach (JsonElement child in children.EnumerateArray()) queue.Enqueue(child.GetString()!);
        }
        JsonElement[] visible = descendants.Where(node => !node.GetProperty("ignored").GetBoolean()).ToArray();
        string Role(JsonElement node) => node.TryGetProperty("role", out JsonElement role) ? role.GetProperty("value").GetString() ?? "" : "";
        JsonElement state = await Script(core, "(() => {const p=window.atlasNativeViewerProbe,s=p.closest('.media-player'),r=s.getBoundingClientRect();return {tag:p.tagName,currentTime:p.currentTime,duration:p.duration,paused:p.paused,ended:p.ended,rate:p.playbackRate,volume:p.volume,muted:p.muted,readyState:p.readyState,networkState:p.networkState,src:p.currentSrc,controls:p.controls,saveVisible:!s.querySelector('.media-save').hidden,inDialog:!!p.closest('dialog'),rect:{x:r.x,y:r.y,width:r.width,height:r.height}};})()");
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, scenario + "-controls.json"), JsonSerializer.Serialize(new
        {
            scenario, playerState = state, buttons = visible.Count(node => Role(node) == "button"), sliders = visible.Count(node => Role(node) == "slider"),
            playerNode = playerNode.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : playerNode,
            descendants
        }, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, scenario + "-ax-tree.json"), rawTree);
        await core.CallDevToolsProtocolMethodAsync("Runtime.releaseObject", JsonSerializer.Serialize(new { objectId }));
        Check(visible.Count(node => Role(node) == "button") >= 3 && visible.Any(node => Role(node) == "slider") && !state.GetProperty("controls").GetBoolean(),
            $"Custom media buttons and timeline remain accessible in the real WebView in {scenario}.");
        Check(!state.GetProperty("saveVisible").GetBoolean(), $"Draft player keeps saving in the context menu in {scenario}.");
        await core.ExecuteScriptAsync("window.atlasNativeViewerProbe.closest('.media-player').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:700,clientY:400}))");
        if (state.GetProperty("tag").GetString() == "AUDIO")
        {
            await UntilScript(core, "!document.querySelector('#context-menu').hidden&&document.querySelectorAll('#context-menu .menu-item').length===1&&/Enregistrer sous|Save as/.test(document.querySelector('#context-menu').textContent)",
                $"The draft audio context menu exposes Save as in {scenario}.");
            await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            await UntilScript(core, "document.querySelector('#context-menu').hidden", "Escape closes only the save context menu.");
        }
        else Check((await Script(core, "document.querySelector('#context-menu').hidden")).GetBoolean(), $"Video has no save context menu in {scenario}.");
        Check(state.GetProperty("rate").GetDouble() == 1.25 && Math.Abs(state.GetProperty("volume").GetDouble() - .37) < .001
            && state.GetProperty("muted").GetBoolean() && state.GetProperty("paused").GetBoolean(),
            $"The native player preserves the fixture playback rate, volume, mute and paused state in {scenario}.");
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
