using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Views;

internal static class ChatRichHostWpfTests
{
    private static int _checks;
    private static readonly Guid Session = Guid.Parse("9cad51dc-e38c-4db6-8e2d-a2f86d7abc11");
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        try
        {
            ValidateOrigins();
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                _ = ExecuteAsync();
                Dispatcher.Run();
                async Task ExecuteAsync()
                {
                    Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    try
                    {
                        foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
                        await ValidateBrowserAsync(captureDirectory);
                        completion.TrySetResult();
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                    finally { application.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }
            }) { IsBackground = true, Name = "AtlasChatRichOffscreenFixture" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
            Console.WriteLine($"Chat rich host WebView2 OK: {_checks} assertions; isolated inactive offscreen fixture, native bridge, embedded assets, CSP, authenticated media routing, session isolation. No user launcher or desktop interaction.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void ValidateOrigins()
    {
        True(ChatViewV2.IsRichDocument("https://animeclub.fr/atlas-messages/"), "Exact local document accepted.");
        foreach (string source in new[] { "https://animeclub.fr/", "https://animeclub.fr/atlas-messages/?x=1", "http://animeclub.fr/atlas-messages/",
            "https://animeclub.fr.attacker.invalid/atlas-messages/", "https://a@animeclub.fr/atlas-messages/", "file:///C:/test.html" })
            True(!ChatViewV2.IsRichDocument(source), "Foreign document rejected: " + source);
        True(ChatViewV2.IsRichFrame("https://www.youtube.com/embed/abcdefghijk"), "YouTube fixed embed accepted.");
        True(ChatViewV2.IsRichFrame("https://player.vimeo.com/video/123456"), "Vimeo fixed embed accepted.");
        foreach (string source in new[] { "https://youtube.com/watch?v=abcdefghijk", "https://www.youtube.com/redirect", "https://127.0.0.1/video/123", "javascript:alert(1)" })
            True(!ChatViewV2.IsRichFrame(source), "Arbitrary iframe navigation rejected.");
        foreach (string resource in new[] { "", "chat.css", "chat.js", "chat-render.js", "vendor/markdown-it-14.1.0.min.js", "fonts/Inter-Regular.ttf" })
        {
            var asset = ChatViewV2.OpenRichAsset(new Uri(ChatViewV2.RichOrigin + resource));
            using Stream? stream = asset.Stream;
            True(stream is not null && stream.Length > 0, "Embedded asset present: " + resource);
        }
        True(ChatViewV2.OpenRichAsset(new Uri("https://animeclub.fr/Assets/Security/launcher-update-public-keys.json")).Stream is null,
            "Only chat resources exposed by local origin.");
        True(ChatViewV2.RichContentSecurityPolicy.Contains("connect-src 'none'", StringComparison.Ordinal), "Page cannot directly contact APIs.");
    }

    private static object Snapshot(Guid? session = null, string selected = "17", long sequence = 10) => new
    {
        type = "snapshot", sessionId = session ?? Session, ownerAccountId = 42u, sequence, locale = "fr", isActive = true,
        isAvailable = true, isLoading = false, state = new ChatStateDto
        {
            Self = new() { AccountId = 42, Username = "Aster" }, Contacts = [new() { AccountId = 91, Username = "Lyra" }],
            Capabilities = ["markdown", "replies", "reactions", "attachments"],
            Threads = [new() { Id = "17", Title = "Lyra", Kind = "direct", CanSend = true,
                Members = [new() { Profile = new() { AccountId = 42, Username = "Aster" } }, new() { Profile = new() { AccountId = 91, Username = "Lyra" } }] }]
        },
        selectedThreadId = selected, messages = new ChatMessageDto[]
        {
            new() { Id = 9007199254741001, ThreadId = "17", Sender = new() { AccountId = 91, Username = "Lyra" },
                Body = "**Bienvenue** à Dalaran. <img src=x onerror=alert(1)>", CreatedAt = DateTimeOffset.Parse("2026-09-07T08:00:00Z"), Version = 1 }
        },
        draft = new { body = "", attachments = Array.Empty<object>() }, pending = Array.Empty<object>(), typing = Array.Empty<object>(),
        hasEarlier = false, isLoadingEarlier = false
    };

    private static async Task ValidateBrowserAsync(string? captureDirectory)
    {
        string directory = captureDirectory ?? Path.Combine(Path.GetTempPath(), "atlas-chat-rich-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ChatViewV2 view = new() { RichUserDataFolder = Path.Combine(directory, "webview-data-" + Guid.NewGuid().ToString("N")) };
        List<ChatRichActionEventArgs> actions = [];
        List<string> mediaRequests = [];
        view.RichActionRequested += (_, args) => actions.Add(args);
        view.MediaResolver = (key, range, ct) =>
        {
            mediaRequests.Add(key);
            return Task.FromResult<ChatMediaStream?>(key == "attachments/fixture-image"
                ? new(new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aC9sAAAAASUVORK5CYII=")), "image/png") : null);
        };
        view.ApplyRichSnapshot(Snapshot());
        view.SetRichMode(true);
        view.SetRichActive(true);
        Window window = new() { Content = view, Width = 1470, Height = 900, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
        };
        window.Show();
        try
        {
            await Until(() => view.RichBrowser?.CoreWebView2 is not null, "WebView initializes.");
            CoreWebView2 core = view.RichBrowser!.CoreWebView2;
            await UntilScript(core, "document.querySelector('#thread-title')?.textContent==='Lyra'", "Native snapshot rendered in embedded page.");
            True(!window.IsActive && window.Left < -10000 && !window.ShowInTaskbar, "Fixture cannot activate or appear on user desktop.");
            using (JsonDocument result = JsonDocument.Parse(await core.ExecuteScriptAsync("({body:document.querySelector('#message-list').textContent,strong:document.querySelector('#message-list strong')?.textContent,images:document.querySelectorAll('#message-list .message-content img').length})")))
            {
                True(result.RootElement.GetProperty("body").GetString()!.Contains("<img src=x onerror=alert(1)>", StringComparison.Ordinal), "User HTML stays text.");
                True(result.RootElement.GetProperty("strong").GetString() == "Bienvenue", "Markdown bold rendered.");
                True(result.RootElement.GetProperty("images").GetInt32() == 0, "User HTML cannot create images.");
            }
            True(actions.All(action => action.Action.GetProperty("action").GetString() != "read"), "Inactive offscreen page never marks messages read.");
            await Post("draft", new { threadId = "17", body = "Un brouillon", replyToMessageId = (string?)null });
            await Until(() => actions.Count == 1, "Real WebView message delivered to native bridge.");
            True(actions[0].SessionId == Session && actions[0].OwnerAccountId == 42 && actions[0].Sequence == 10, "Native bridge preserves identity.");
            await Post("reaction", new { threadId = "17", messageId = "9007199254741001", emoji = "❤️", active = true });
            await Until(() => actions.Count == 2, "Reaction action reaches native bridge.");
            True(actions[1].Action.GetProperty("payload").GetProperty("messageId").GetString() == "9007199254741001", "Long ID crosses real bridge without rounding.");
            await Post("read", new { threadId = "17", throughMessageId = "9007199254741001" });
            await Post("draft", new { threadId = "18", body = "Wrong thread" });
            await Post("draft", new { threadId = "17", body = "Wrong owner" }, owner: 88);
            await Post("draft", new { threadId = "17", body = "Future snapshot" }, sequence: "999");
            await Post("draft", new { threadId = "17", body = "Old account" }, session: Guid.NewGuid());
            await Task.Delay(150);
            True(actions.Count == 2, "Read while inactive and mismatched thread/account/session/sequence rejected.");
            await core.ExecuteScriptAsync("window.__mediaOk=false;const image=document.createElement('img');image.onload=()=>window.__mediaOk=true;image.src='https://atlas-chat-media.invalid/attachments/fixture-image';document.body.append(image);");
            await UntilScript(core, "window.__mediaOk===true", "Image read through native media resolver.");
            True(mediaRequests.SequenceEqual(["attachments/fixture-image"]), "Native resolver receives constrained relative media key.");
            await core.ExecuteScriptAsync("window.__blockedFetch=false;fetch('https://atlas-chat-media.invalid/attachments/arbitrary').catch(()=>window.__blockedFetch=true)");
            await UntilScript(core, "window.__blockedFetch===true", "CSP blocks direct fetch from top page.");
            True(mediaRequests.Count == 1, "Blocked JS fetch never reaches authenticated native client.");
            await core.ExecuteScriptAsync("document.body.lastElementChild.tagName==='IMG'&&document.body.lastElementChild.remove()");
            await using (FileStream capture = File.Create(Path.Combine(directory, "native-chat-large.png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
            window.Width = 1080; window.Height = 680;
            await Task.Delay(200);
            await using (FileStream capture = File.Create(Path.Combine(directory, "native-chat-compact.png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
            Guid replacement = Guid.NewGuid();
            view.ApplyRichSnapshot(Snapshot(replacement, sequence: 11));
            await Post("draft", new { threadId = "17", body = "Stale page" });
            await Task.Delay(100);
            True(actions.Count == 2, "Old session cannot mutate after native account switch.");
            view.SetRichMode(false);
            True(((FrameworkElement)view.FindName("PageGrid")).Visibility == Visibility.Visible, "Legacy compatibility view restored explicitly.");

            async Task Post(string action, object payload, Guid? session = null, uint owner = 42, string sequence = "10")
            {
                string envelope = JsonSerializer.Serialize(new { type = "action", requestId = Guid.NewGuid().ToString(), sessionId = session ?? Session,
                    ownerAccountId = owner, sequence, action, payload }, ChatJson.Options);
                await core.ExecuteScriptAsync("chrome.webview.postMessage(" + envelope + ")");
            }
        }
        finally { view.DisposeRich(); window.Close(); }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(35);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message); await Task.Delay(30); }
        True(true, message);
    }
    private static async Task UntilScript(CoreWebView2 core, string script, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(25);
        while (await core.ExecuteScriptAsync(script) != "true") { if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message); await Task.Delay(30); }
        True(true, message);
    }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); ++_checks; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
}
