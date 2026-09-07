using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
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
            await ValidateAvatarRoutesAsync();
            ValidateOwnCharacterProjection();
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
                        await ValidateOwnRosterBridgeAsync();
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
        string directory = Path.GetFullPath(captureDirectory ?? Path.Combine(Path.GetTempPath(), "atlas-chat-rich-" + Guid.NewGuid().ToString("N")));
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
            await Until(() => view.IsRichComposerAcceptingFiles, "Real composer publishes its initial native file permission.");
            True(!window.IsActive && window.Left < -10000 && !window.ShowInTaskbar, "Fixture cannot activate or appear on user desktop.");
            ValidateNativeDrops(view, directory);
            BitmapSource decoded = await Task.Run(() =>
            {
                using MemoryStream bytes = new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aC9sAAAAASUVORK5CYII="));
                BitmapSource frame = BitmapDecoder.Create(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                return frame;
            });
            using (ChatMediaStream? avatar = LauncherShellV2.EncodeRichAvatar(decoded))
                True(avatar is { ContentType: "image/png", Length: > 0 }, "Worker-decoded frozen avatar becomes a usable native PNG.");
            using (JsonDocument result = JsonDocument.Parse(await core.ExecuteScriptAsync("({body:document.querySelector('#message-list').textContent,strong:document.querySelector('#message-list strong')?.textContent,images:document.querySelectorAll('#message-list .message-content img').length})")))
            {
                True(result.RootElement.GetProperty("body").GetString()!.Contains("<img src=x onerror=alert(1)>", StringComparison.Ordinal), "User HTML stays text.");
                True(result.RootElement.GetProperty("strong").GetString() == "Bienvenue", "Markdown bold rendered.");
                True(result.RootElement.GetProperty("images").GetInt32() == 0, "User HTML cannot create images.");
            }
            True(actions.All(action => action.Action.GetProperty("action").GetString() != "read"), "Inactive offscreen page never marks messages read.");
            await Post("composerState", new { threadId = "17", acceptsFiles = false });
            await Until(() => !view.IsRichComposerAcceptingFiles, "Real WebView composer state closes native file permission while editing.");
            await Post("composerState", new { threadId = "17", acceptsFiles = true });
            await Until(() => view.IsRichComposerAcceptingFiles, "Real WebView cancellation restores native file permission.");
            True(actions.Count == 0, "Composer state is handled by the native host and never reaches workspace commands.");
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

    private static void ValidateNativeDrops(ChatViewV2 view, string directory)
    {
        string image = Path.Combine(directory, "native-drop-fixture.png"), text = Path.Combine(directory, "native-drop-fixture.txt");
        File.WriteAllBytes(image, [137, 80, 78, 71, 13, 10, 26, 10]);
        File.WriteAllText(text, "Only the native fixture owns this file.");
        List<ChatFilesAddedEventArgs> received = [];
        bool allowed = true;
        view.CanAcceptNativeDrop = () => allowed;
        view.FilesAddedRequested += Receive;
        try
        {
            True(view.RichBrowser!.AllowDrop && !view.RichBrowser.AllowExternalDrop, "WPF owns Explorer files without Chromium file navigation.");
            Drop([image, text, image]);
            True(received.Count == 1 && received[0].Paths.SequenceEqual([image, text]) && received[0].IsNativeDrop,
                "Real WPF FileDrop routed events deliver image and text exactly once with duplicate normalization.");
            True(received[0].SessionId == Session && received[0].OwnerAccountId == 42 && received[0].ConversationId == "17",
                "Native drop is scoped to the displayed identity and selected thread while the window remains inactive.");
            True(view.TryApplyRichComposerState(ComposerState(false)), "Editing composer closes native file admission.");
            Drop([image]);
            True(received.Count == 1, "Editing cannot start a hidden upload through the native WPF drop path.");
            foreach (string action in new[] { "pasteImage", "pickFiles", "dropFiles" })
                True(!view.CanAcceptRichFileRequest(FileRequest(action)), "Editing closes the shared file/clipboard/picker gate before any source is read.");
            foreach (JsonElement invalidState in new[] { ComposerState(true, owner: 88), ComposerState(true, session: Guid.NewGuid()),
                ComposerState(true, thread: "18"), ComposerState(true, sequence: 999), ComposerState("true") })
                True(!view.TryApplyRichComposerState(invalidState) && !view.IsRichComposerAcceptingFiles,
                    "Wrong identity, thread, sequence or non-boolean composer state cannot reopen native file admission.");
            True(view.TryApplyRichComposerState(ComposerState(true)), "Cancelling edit reopens native file admission.");
            foreach (string action in new[] { "pasteImage", "pickFiles", "dropFiles" })
                True(view.CanAcceptRichFileRequest(FileRequest(action)), "Cancelled edit restores the shared file/clipboard/picker gate.");
            Drop([image]);
            True(received.Count == 2, "A new native drop after cancelling edit reaches workspace ingestion.");
            Drop([Path.Combine(directory, "missing.png")]);
            Drop([directory]);
            string executable = Path.Combine(directory, "not-allowed.exe"); File.WriteAllText(executable, "fixture");
            Drop([executable]);
            Drop(Enumerable.Repeat(image, 11).ToArray());
            True(received.Count == 2, "Missing files, directories, executable types and excessive counts are refused before workspace ingestion.");
            allowed = false; Drop([image]); allowed = true;
            True(received.Count == 2, "Native overlay/hidden-page gate rejects a drop.");
            JsonObject invalid = JsonSerializer.SerializeToNode(Snapshot(), ChatJson.Options)!.AsObject();
            invalid["state"]!["threads"] = new JsonArray();
            view.ApplyRichSnapshot(invalid); Drop([image]);
            True(received.Count == 2, "A removed thread cannot receive files.");
            view.ApplyRichSnapshot(Snapshot());
            DataObject pending = new(DataFormats.FileDrop, new[] { image });
            Raise(DragDrop.PreviewDragEnterEvent, pending);
            Guid replacementSession = Guid.NewGuid();
            view.ApplyRichSnapshot(Snapshot(replacementSession, sequence: 11));
            True(!view.IsRichComposerAcceptingFiles, "A replacement session waits for its own composer state.");
            True(!view.TryApplyRichComposerState(ComposerState(true, session: replacementSession, sequence: 10)),
                "A composer signal older than the replacement snapshot cannot authorize files.");
            Raise(DragDrop.PreviewDropEvent, pending);
            True(received.Count == 2, "A drag started before a login replacement cannot deliver files into the next session.");
            view.ApplyRichSnapshot(Snapshot());
            True(!view.IsRichComposerAcceptingFiles, "Returning to a previous identity does not reuse its former file permission.");
            True(view.TryApplyRichComposerState(ComposerState(true)), "Current identity can acknowledge its composer again.");
            Raise(DragDrop.PreviewDragEnterEvent, pending);
            view.ApplyRichSnapshot(Snapshot(selected: "18"));
            True(!view.IsRichComposerAcceptingFiles, "Changing thread clears the composer permission.");
            Raise(DragDrop.PreviewDropEvent, pending);
            True(received.Count == 2, "A changed conversation invalidates the captured drop target.");
            view.ApplyRichSnapshot(Snapshot());
            True(!view.IsRichComposerAcceptingFiles, "Returning to a previous thread still requires the current composer signal.");
            True(view.TryApplyRichComposerState(ComposerState(true)), "Final fixture restores the initial composer permission.");
        }
        finally { view.FilesAddedRequested -= Receive; view.CanAcceptNativeDrop = null; }

        void Receive(object? sender, ChatFilesAddedEventArgs args) => received.Add(args);
        JsonElement ComposerState(object acceptsFiles, uint owner = 42, Guid? session = null, string thread = "17", long sequence = 10)
            => JsonSerializer.SerializeToElement(new { type = "action", requestId = Guid.NewGuid().ToString(), sessionId = session ?? Session,
                ownerAccountId = owner, sequence = sequence.ToString(), action = "composerState", payload = new { threadId = thread, acceptsFiles } }, ChatJson.Options);
        JsonElement FileRequest(string action) => JsonSerializer.SerializeToElement(new { type = "action", requestId = Guid.NewGuid().ToString(), sessionId = Session,
            ownerAccountId = 42u, sequence = "10", action, payload = new { threadId = "17" } }, ChatJson.Options);
        void Drop(string[] paths)
        {
            DataObject data = new(DataFormats.FileDrop, paths);
            Raise(DragDrop.PreviewDragEnterEvent, data);
            Raise(DragDrop.PreviewDragOverEvent, data);
            Raise(DragDrop.PreviewDropEvent, data);
        }
        void Raise(RoutedEvent routedEvent, DataObject data)
        {
            // WPF's constructor is internal; build the same event object OLE
            // supplies, then exercise its actual routed event handlers.
            DragEventArgs args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: [data, DragDropKeyStates.None, DragDropEffects.Copy, view.RichBrowser!, new Point(40, 40)], culture: null)!;
            args.RoutedEvent = routedEvent;
            view.RichBrowser!.RaiseEvent(args);
            True(args.Handled, "WPF native file drag is consumed by the registered preview handler.");
        }
    }

    private static async Task ValidateAvatarRoutesAsync()
    {
        foreach (string api in new[] { "https://fixture.invalid/api/v1/", "https://fixture.invalid/wotlk/api/v1/", "https://fixture.invalid/wotlk/api/v2/chat/" })
        {
            string prefix = api.Contains("/wotlk/", StringComparison.Ordinal) ? "/wotlk" : "";
            List<Uri> requests = [];
            using HttpClient http = new(new AvatarHandler(request =>
            {
                requests.Add(request.RequestUri!);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([137, 80, 78, 71]) };
            }));
            LauncherChatV2ApiClient client = new(http, new Uri(api));
            foreach (string path in new[] { "/media/avatars/fixture/7/64.png", "media/avatars/fixture/7/64.png", "https://fixture.invalid" + prefix + "/media/avatars/fixture/7/64.png" })
            {
                using ChatMediaStream media = await client.OpenAvatarAsync(path, CancellationToken.None);
                True(requests[^1].AbsolutePath == prefix + "/media/avatars/fixture/7/64.png", "Avatar fallback preserves the authenticated application's prefix.");
            }
            int count = requests.Count;
            foreach (string invalid in new[] { "https://foreign.invalid/media/avatars/x", "https://user@fixture.invalid" + prefix + "/media/avatars/x", "/api/v1/profile", "/media/avatars/../secret", "/media/avatars/x?token=hidden", "/media/avatars/x#fragment", "file:///C:/avatar.png" })
            {
                bool rejected = false;
                try { using ChatMediaStream _ = await client.OpenAvatarAsync(invalid, CancellationToken.None); }
                catch (ArgumentException) { rejected = true; }
                True(rejected && requests.Count == count, "An avatar resource cannot change the authorized origin or escape the media route.");
            }
        }
    }

    private static JsonElement OwnRoster(uint guid = 17, string name = "Personnage de test") => JsonSerializer.SerializeToElement(new
    {
        characters = new[] { new { character = new { guid, name, level = 40, classId = 8, race = 1, gender = 0 },
            snapshot = new { privateField = "must not enter the chooser" }, values = new { privateStatistic = 123 }, equipment = new[] { 999 } } }
    });

    private static void ValidateOwnCharacterProjection()
    {
        IReadOnlyList<ChatOwnCharacter> characters = LauncherShellV2.ParseOwnCharacters(OwnRoster(uint.MaxValue));
        True(characters.Single().Guid == uint.MaxValue.ToString() && characters[0].Name == "Personnage de test", "Roster chooser preserves the full owned character GUID and its name.");
        string projected = JsonSerializer.Serialize(characters, ChatJson.Options);
        True(!projected.Contains("private", StringComparison.Ordinal) && !projected.Contains("equipment", StringComparison.Ordinal), "Only display metadata enters the roster chooser.");
        foreach (JsonElement invalid in new[] { OwnRoster(0), OwnRoster(1, "bad\nname"), JsonSerializer.SerializeToElement(new { characters = new[] { OwnRoster().GetProperty("characters")[0], OwnRoster().GetProperty("characters")[0] } }) })
        {
            bool rejected = false;
            try { _ = LauncherShellV2.ParseOwnCharacters(invalid); }
            catch (JsonException) { rejected = true; }
            True(rejected, "Malformed or duplicated owned character identifiers are rejected.");
        }
    }

    private static async Task ValidateOwnRosterBridgeAsync()
    {
        AccountUiState state = new(AccountUiState.Empty.Current with { IsRuntimeConnected = true, Username = "RosterFixture" });
        using ArmoryViewV2 view = new() { Visibility = Visibility.Collapsed };
        int reads = 0;
        view.Configure(_ => Task.FromResult<uint?>(42), state, readData: (owner, request, token) =>
        {
            True(owner == 42 && request.Operation == "roster" && request.CharacterId is null, "Picker reads the authenticated own-account roster without a supplied character ID.");
            reads++;
            return Task.FromResult(OwnRoster());
        });
        JsonElement roster = await view.ReadOwnCharactersForChatAsync(42, CancellationToken.None);
        True(roster.GetProperty("characters").GetArrayLength() == 1 && reads == 1, "Own roster can be read without opening the armory WebView or local helper.");
        bool refused = false;
        try { await view.ReadOwnCharactersForChatAsync(84, CancellationToken.None); }
        catch (UnauthorizedAccessException) { refused = true; }
        True(refused && reads == 1, "Another account cannot be substituted by a chooser request.");
    }

    private sealed class AvatarHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
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
