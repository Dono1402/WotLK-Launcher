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

internal static partial class ChatRichHostWpfTests
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
            ValidatePresenceProjection();
            ValidateAttachmentSaveSources();
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
        foreach (string resource in new[] { "", "chat.css", "chat.js", "chat-render.js", "chat-media.js", "chat-media.css", "vendor/markdown-it-14.1.0.min.js", "fonts/Inter-Regular.ttf" })
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
            Self = new() { AccountId = 42, Username = "Aster", Presence = "online" }, Contacts = [new() { AccountId = 91, Username = "Lyra", Presence = "away" }],
            Capabilities = ["markdown", "replies", "reactions", "attachments"],
            Threads = [new() { Id = "17", Title = "Lyra", Kind = "direct", CanSend = true,
                Members = [new() { Profile = new() { AccountId = 42, Username = "Aster" } }, new() { Profile = new() { AccountId = 91, Username = "Lyra" } }] }]
        },
        selectedThreadId = selected, messages = new ChatMessageDto[]
        {
            new() { Id = 9007199254741001, ThreadId = "17", Sender = new() { AccountId = 91, Username = "Lyra", Presence = "offline" },
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
        byte[] activityWave = ChatFullShellWpfTests.SilentWave();
        view.RichActionRequested += (_, args) => actions.Add(args);
        view.MediaResolver = (key, range, ct) =>
        {
            mediaRequests.Add(key);
            return Task.FromResult<ChatMediaStream?>(key switch
            {
                "attachments/fixture-image" => new(new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aC9sAAAAASUVORK5CYII=")), "image/png"),
                "attachments/native-activity-wave" => ChatFullShellWpfTests.FixtureMedia(activityWave, "audio/wav", range),
                _ => null
            });
        };
        view.ApplyRichSnapshot(Snapshot());
        view.SetRichMode(true);
        view.SetRichActive(true);
        Window window = new() { Content = view, Width = 1597.6, Height = 872.8, Left = -20000, Top = -20000,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanMinimize,
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
            await UntilScript(core, "!document.querySelector('.page-heading,#page-subtitle,#composer-error')&&innerWidth===1597&&innerHeight===872", "Embedded page keeps its fixed viewport without a redundant heading or global composer error.");
            await UntilScript(core, "document.querySelector('#thread-avatar .presence-dot')?.dataset.presence==='away'&&document.querySelector('.message-avatar .presence-dot')?.dataset.presence==='away'", "Native WebView uses current contact presence in both the header and old message avatar.");
            await Until(() => view.IsRichComposerAcceptingFiles, "Real composer publishes its initial native file permission.");
            True(!window.IsActive && window.Left < -10000 && !window.ShowInTaskbar, "Fixture cannot activate or appear on user desktop.");
            await ValidateMediaActivationAsync(view, window, core, directory);
            True(mediaRequests.Count > 0 && mediaRequests.All(key => key == "attachments/native-activity-wave"),
                "Background playback uses only its authorized relative native fixture key.");
            mediaRequests.Clear();
            await ValidateLocalMediaSeekingAsync(view, window, core, directory);
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
            await using (FileStream capture = File.Create(Path.Combine(directory, "native-chat-fixed.png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
            Guid replacement = Guid.NewGuid();
            view.ApplyRichSnapshot(Snapshot(replacement, sequence: 11));
            await Post("draft", new { threadId = "17", body = "Stale page" });
            await Task.Delay(100);
            True(actions.Count == 2, "Old session cannot mutate after native account switch.");
            JsonObject recovery = JsonSerializer.SerializeToNode(Snapshot(replacement, sequence: 12), ChatJson.Options)!.AsObject();
            recovery["draft"]!["body"] = "Brouillon conservé après incident";
            view.ApplyRichSnapshot(recovery);
            await UntilScript(core, "document.querySelector('#composer-input')?.value==='Brouillon conservé après incident'", "Draft is present before renderer recovery.");
            // Crash only the renderer owned by this isolated offscreen fixture.
            _ = core.CallDevToolsProtocolMethodAsync("Page.crash", "{}").ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
            System.Windows.Controls.Button retry = (System.Windows.Controls.Button)view.FindName("RichRetryButton");
            await Until(() => retry.IsVisible, "Renderer failure offers an in-place retry.");
            view.SetRichMode(false);
            True(!retry.IsVisible, "Leaving rich mode hides its retry feedback.");
            view.SetRichMode(true);
            True(retry.IsVisible && !((System.Windows.Controls.ProgressBar)view.FindName("RichLoadingIndicator")).IsIndeterminate,
                "Returning to a failed renderer retains retry without a false loading indicator.");
            retry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Until(() => view.RichBrowser?.CoreWebView2 is { } current && !ReferenceEquals(current, core), "Retry recreates a fresh browser control.");
            core = view.RichBrowser!.CoreWebView2;
            await UntilScript(core, "document.querySelector('#thread-title')?.textContent==='Lyra'&&document.querySelector('#composer-input')?.value==='Brouillon conservé après incident'", "Recreated renderer restores the conversation and saved draft.");
            True(!retry.IsVisible && actions.Count == 2, "Recovery clears its notice and does not replay business actions.");
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

    private static async Task ValidateMediaActivationAsync(ChatViewV2 view, Window window, CoreWebView2 core, string directory)
    {
        List<object> playbackEvidence = [];
        async Task Record(string stage)
        {
            using JsonDocument state = JsonDocument.Parse(await core.ExecuteScriptAsync("({time:window.atlasNativeActivityPlayer.currentTime,paused:window.atlasNativeActivityPlayer.paused,muted:window.atlasNativeActivityPlayer.muted,readyState:window.atlasNativeActivityPlayer.readyState,pauseEvents:window.atlasNativeActivityPauses,scriptPauseCalls:window.atlasNativeActivityPauseCalls,isActive:window.atlasNativeSnapshotProbe?.isActive,isMediaActive:window.atlasNativeSnapshotProbe?.isMediaActive,visibility:document.visibilityState,focused:document.hasFocus()})"));
            playbackEvidence.Add(new { stage, nativeWindowState = window.WindowState.ToString(), nativeWindowActive = window.IsActive, state = state.RootElement.Clone() });
            await File.WriteAllTextAsync(Path.Combine(directory, "native-playback-activity.json"), JsonSerializer.Serialize(playbackEvidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        await core.ExecuteScriptAsync("window.atlasNativeSnapshotProbe=null;window.atlasNativeSnapshotListener=event=>{if(event.data?.type==='snapshot')window.atlasNativeSnapshotProbe=event.data;};chrome.webview.addEventListener('message',window.atlasNativeSnapshotListener)");
        view.SetRichActive(false, true);
        await UntilScript(core, "window.atlasNativeSnapshotProbe?.isActive===false&&window.atlasNativeSnapshotProbe.isMediaActive===true",
            "Native read activity stays false while background media is allowed in the selected Messages page.");
        await core.ExecuteScriptAsync("window.atlasNativeActivityPlayer=document.createElement('audio');window.atlasNativeActivityPlayer.muted=true;window.atlasNativeActivityPlayer.preload='none';window.atlasNativeActivityPlayer.src='https://atlas-chat-media.invalid/attachments/native-activity-wave';window.atlasNativeActivityPauses=0;window.atlasNativeActivityPlayer.addEventListener('pause',()=>window.atlasNativeActivityPauses++);window.atlasNativeActivityShell=AtlasChatMedia.create(window.atlasNativeActivityPlayer);window.atlasNativeActivityShell.style.cssText='position:fixed;left:20px;top:20px;width:250px';document.body.append(window.atlasNativeActivityShell)");
        // Every PCM sample is zero, so this produces no sound even unmuted.
        // Muted media behaves differently when the native window is hidden;
        // use the ordinary audio playback state while the PCM remains silent.
        await core.ExecuteScriptAsync("window.atlasNativeActivityPlayer.muted=false;window.atlasNativeActivityPauseCalls=[];window.atlasNativeActivityPlayer.pause=function(){window.atlasNativeActivityPauseCalls.push(new Error().stack);return HTMLMediaElement.prototype.pause.call(this)}");
        await UntilScript(core, "window.atlasNativeActivityPlayer.readyState>=3&&Math.abs(window.atlasNativeActivityPlayer.duration-4)<.02", "The inactive native window decodes real four-second silent audio.");
        await UntilScript(core, "window.atlasNativeActivityPlayer.preload==='metadata'&&window.atlasNativeActivityPlayer.paused&&window.atlasNativeActivityPlayer.currentTime===0", "The visible audio shell prepares through the native resolver before any click, focus, load or playback.");
        await core.ExecuteScriptAsync("window.atlasNativeActivityShell.querySelector('.media-play').click()");
        await UntilScript(core, "!window.atlasNativeActivityPlayer.paused&&window.atlasNativeActivityPlayer.currentTime>.03", "Custom Play starts real audio while the native window has no focus.");
        await Record("playing-inactive");
        await core.ExecuteScriptAsync("window.atlasNativeActivityBefore=window.atlasNativeActivityPlayer.currentTime");
        await core.ExecuteScriptAsync("window.atlasNativeSnapshotProbe=null");
        window.WindowState = WindowState.Minimized;
        await UntilScript(core, "window.atlasNativeSnapshotProbe?.isActive===false&&window.atlasNativeSnapshotProbe.isMediaActive===true",
            "Minimizing the inactive fixture does not revoke the separate native media activity signal.");
        await UntilScript(core, "!window.atlasNativeActivityPlayer.paused&&window.atlasNativeActivityPauses===0&&window.atlasNativeActivityPlayer.currentTime>window.atlasNativeActivityBefore+.08", "Actual native playback time advances while minimized with no pause event.");
        await Record("playing-minimized");
        window.WindowState = WindowState.Normal;
        await core.ExecuteScriptAsync("window.atlasNativeActivityBefore=window.atlasNativeActivityPlayer.currentTime");
        await core.ExecuteScriptAsync("window.atlasNativeSnapshotProbe=null");
        window.Hide();
        await UntilScript(core, "window.atlasNativeSnapshotProbe?.isActive===false&&window.atlasNativeSnapshotProbe.isMediaActive===true",
            "Hiding the selected Messages window keeps playback eligible without marking messages read.");
        await Record("after-hide");
        try { await UntilScript(core, "!window.atlasNativeActivityPlayer.paused&&window.atlasNativeActivityPauses===0&&window.atlasNativeActivityPlayer.currentTime>window.atlasNativeActivityBefore+.08", "Actual native playback time advances while the window is hidden with no pause event."); }
        catch { await Record("hidden-progress-failed"); throw; }
        await Record("playing-hidden");
        window.Show();
        await UntilScript(core, "!window.atlasNativeActivityPlayer.paused&&window.atlasNativeActivityPauses===0", "Showing the inactive native window preserves ongoing playback.");
        await Record("playing-restored-inactive");
        view.SetRichActive(false, false);
        await UntilScript(core, "window.atlasNativeSnapshotProbe?.isActive===false&&window.atlasNativeSnapshotProbe.isMediaActive===false",
            "Leaving the selected Messages page revokes native media activity.");
        await UntilScript(core, "window.atlasNativeActivityPlayer.paused&&window.atlasNativeActivityPauses===1", "Leaving Messages actually stops native playback with one pause event.");
        await Record("paused-after-leaving-messages");
        await core.ExecuteScriptAsync("AtlasChatMedia.dispose(window.atlasNativeActivityShell);window.atlasNativeActivityShell.remove();delete window.atlasNativeActivityShell;delete window.atlasNativeActivityPlayer;delete window.atlasNativeActivityPauses;delete window.atlasNativeActivityBefore;delete window.atlasNativeActivityPauseCalls");
        view.SetRichActive(true);
        await UntilScript(core, "window.atlasNativeSnapshotProbe?.isActive===false&&window.atlasNativeSnapshotProbe.isMediaActive===true",
            "The legacy SetRichActive overload retains its media fallback while window focus still gates read activity.");
        view.ApplyRichSnapshot(Snapshot(Guid.Empty));
        await UntilScript(core, "window.atlasNativeSnapshotProbe?.sessionId==='00000000-0000-0000-0000-000000000000'&&window.atlasNativeSnapshotProbe.isMediaActive===false",
            "An invalid native session cannot retain media activity.");
        JsonObject noOwner = JsonSerializer.SerializeToNode(Snapshot(), ChatJson.Options)!.AsObject();
        noOwner["ownerAccountId"] = 0;
        view.ApplyRichSnapshot(noOwner);
        await UntilScript(core, "String(window.atlasNativeSnapshotProbe?.ownerAccountId)==='0'&&window.atlasNativeSnapshotProbe.isMediaActive===false",
            "An unauthenticated native owner cannot retain media activity.");
        view.ApplyRichSnapshot(Snapshot());
        await UntilScript(core, "window.atlasNativeSnapshotProbe?.isActive===false&&window.atlasNativeSnapshotProbe.isMediaActive===true",
            "Restoring the valid native identity restores only the permitted background media signal.");
        True(!window.IsActive && !window.ShowInTaskbar && window.Left < -10000 && window.Top < -10000,
            "Media-activity checks keep the fixture inactive and outside the desktop.");
        await core.ExecuteScriptAsync("chrome.webview.removeEventListener('message',window.atlasNativeSnapshotListener);delete window.atlasNativeSnapshotListener;delete window.atlasNativeSnapshotProbe");
    }

    private static void ValidateAttachmentSaveSources()
    {
        ChatAttachmentDto image = new() { Id = "sent-image", FileName = "portrait.png", ContentType = "image/png", Kind = "image", Size = 12 };
        ChatWorkspaceUpload draft = new() { LocalId = "local-audio", ThreadId = "17", FileName = "note.wav", ContentType = "audio/wav", Size = 20, Status = "uploading" };
        ChatWorkspaceSnapshot snapshot = new() { SessionId = Session, OwnerAccountId = 42, SelectedThreadId = "17",
            State = new() { Threads = [new() { Id = "17" }] },
            Messages = [new() { Id = 1, ThreadId = "17", Attachments = [image] }], Uploads = [draft],
            Draft = new() { ThreadId = "17", AttachmentIds = [draft.LocalId] } };
        True(LauncherShellV2.ResolveRichAttachmentForSave(snapshot, image.Id, null) == image, "Sent non-video attachments keep native Save As support.");
        ChatAttachmentDto local = LauncherShellV2.ResolveRichAttachmentForSave(snapshot, null, draft.LocalId);
        True(local.Id == draft.LocalId && local.FileName == draft.FileName && local.Size == draft.Size && local.Kind == "audio",
            "A current local draft resolves by opaque upload ID before upload completion, without exposing its source path.");
        ChatAttachmentDto completed = new() { Id = "remote-audio", FileName = draft.FileName, ContentType = draft.ContentType, Kind = "audio", Size = draft.Size };
        True(LauncherShellV2.ResolveRichAttachmentForSave(snapshot with { Uploads = [draft with { Attachment = completed, Status = "complete" }] }, null, draft.LocalId) == local,
            "Finishing the upload while Save As is open does not change the native local save source.");
        True(LauncherShellV2.ResolveRichAttachmentForSave(snapshot with { Uploads = [draft with { Attachment = completed }],
            Draft = snapshot.Draft with { AttachmentIds = [completed.Id] } }, null, draft.LocalId) == local,
            "A draft using the completed server attachment ID still resolves its authorized local upload.");
        void Reject(ChatWorkspaceSnapshot current, string? attachmentId, string? uploadId, string expected, string description)
        {
            bool rejected = false;
            try { LauncherShellV2.ResolveRichAttachmentForSave(current, attachmentId, uploadId); }
            catch (ChatWorkspaceException error) { rejected = error.Message == expected; }
            True(rejected, description);
        }
        Reject(snapshot, image.Id, draft.LocalId, "chat-invalid-request", "A save request cannot combine attachment and upload IDs.");
        Reject(snapshot, null, null, "chat-invalid-request", "A save request requires one opaque source ID.");
        Reject(snapshot, "unknown", null, "chat-attachment-not-found", "Unknown sent media cannot open a save dialog.");
        Reject(snapshot, null, "unknown", "chat-attachment-not-found", "Unknown local media cannot open a save dialog.");
        Reject(snapshot with { SelectedThreadId = "18" }, null, draft.LocalId, "chat-attachment-not-found", "Draft saving stays within the selected conversation.");
        Reject(snapshot with { State = new() }, null, draft.LocalId, "chat-attachment-not-found", "A revoked conversation cannot save its former local draft.");
        Reject(snapshot with { Draft = snapshot.Draft with { AttachmentIds = [] } }, null, draft.LocalId, "chat-attachment-not-found", "Removed uploads cannot be saved from a stale draft menu.");
        Reject(snapshot with { Messages = [snapshot.Messages[0] with { DeletedAt = DateTimeOffset.UtcNow }] }, image.Id, null,
            "chat-attachment-not-found", "Deleted messages cannot be saved through a stale menu.");
        foreach (ChatAttachmentDto video in new[] { image with { Kind = "VIDEO" }, image with { ContentType = " video/unknown; codecs=x" }, image with { FileName = "movie.MKV" } })
            Reject(snapshot with { Messages = [snapshot.Messages[0] with { Attachments = [video] }] }, image.Id, null,
                "chat-forbidden", "Native saving and external opening both reject video classified by kind, MIME or registered extension.");
        Reject(snapshot with { Uploads = [draft with { FileName = "clip.webm", ContentType = "audio/wav" }] }, null, draft.LocalId,
            "chat-forbidden", "The local draft path cannot bypass the video extension restriction.");
        Reject(snapshot with { Uploads = [draft with { Attachment = completed with { ContentType = "video/mp4" } }] }, null, draft.LocalId,
            "chat-forbidden", "Completed draft metadata cannot bypass the video MIME restriction.");
        foreach (ChatAttachmentDto invalid in new[] { image with { FileName = "../portrait.png" }, image with { FileName = "bad|portrait.png" }, image with { Size = 0 } })
            Reject(snapshot with { Messages = [snapshot.Messages[0] with { Attachments = [invalid] }] }, image.Id, null,
                "chat-invalid-file", "Unsafe filenames and invalid lengths never reach a native save dialog.");
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

    private static void ValidatePresenceProjection()
    {
        ChatProfileDto oldSelf = new() { AccountId = 42, Username = "Aster", Presence = "online", CharacterGuid = 942, CharacterName = "Asterion", CharacterClass = "Mage", ZoneName = "Dalaran" };
        foreach (string status in new[] { "online", "away", "dnd", "offline" })
        {
            LauncherPresenceSnapshot current = new(7, 42, status, status, false, true, false, null);
            ChatProfileDto projected = LauncherShellV2.ProjectRichPresence(oldSelf, 42, null, current);
            True(projected.Presence == status, "Rich self presence follows each confirmed global status rather than historical message metadata.");
            if (status == "offline") True(projected.CharacterGuid is null && projected.CharacterName is null && projected.ZoneName is null,
                "Appearing offline clears historical game-location metadata in rich self profiles.");
        }
        foreach (LauncherPresenceSnapshot foreign in new[] { new LauncherPresenceSnapshot(8, 84, "dnd", "dnd", false, true, false, null), new LauncherPresenceSnapshot(8, 42, "dnd", "dnd", false, false, false, null) })
            True(LauncherShellV2.ProjectRichPresence(oldSelf, 42, null, foreign).Presence == "online", "Unconfirmed or foreign-account global presence cannot overwrite the displayed account.");
        FriendUiItem friend = new(91, "Lyra", "L", "#123456", false, null, null, null, false, false, "Hors ligne", "", "", false, false, false, false, false, true)
        { Presence = "offline" };
        ChatProfileDto oldFriend = oldSelf with { AccountId = 91, Username = "Lyra" };
        foreach (string status in new[] { "online", "away", "dnd", "offline" })
        {
            ChatProfileDto projected = LauncherShellV2.ProjectRichPresence(oldFriend, 42, friend with { Presence = status, IsOnline = status != "offline" }, null);
            True(projected.Presence == status, "Rich friend profiles use the same current status as the Friends drawer.");
            True(projected.CharacterName is null && projected.CharacterGuid is null, "No historical character remains when the current friend source has no active character.");
        }
        True(LauncherShellV2.ProjectRichPresence(oldFriend, 42, friend with { AccountId = 92, Presence = "dnd" }, null).Presence == "online",
            "A different friend entry cannot overwrite the message author's presence.");
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
