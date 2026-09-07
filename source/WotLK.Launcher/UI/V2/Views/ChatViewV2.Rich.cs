using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ChatViewV2
{
    internal const string RichOrigin = "https://animeclub.fr/atlas-messages/";
    internal const string RichMediaOrigin = "https://atlas-chat-media.invalid/";
    internal const string RichContentSecurityPolicy = "default-src 'none'; base-uri 'none'; object-src 'none'; "
        + "script-src https://animeclub.fr/atlas-messages/; style-src https://animeclub.fr/atlas-messages/ 'unsafe-inline'; "
        + "img-src https://atlas-chat-media.invalid/ data: blob:; media-src https://atlas-chat-media.invalid/ blob:; "
        + "font-src https://animeclub.fr/atlas-messages/; connect-src 'none'; "
        + "frame-src https://www.youtube.com/embed/ https://www.youtube-nocookie.com/embed/ https://player.vimeo.com/video/; "
        + "form-action 'none'; frame-ancestors 'none'";
    private WebView2CompositionControl? _richBrowser;
    private CoreWebView2Environment? _richEnvironment;
    private JsonObject? _richSnapshot;
    private CancellationTokenSource _richMediaLifetime = new();
    private readonly CancellationTokenSource _richLifetime = new();
    private readonly ConcurrentDictionary<Guid, OwnedChatResponseStream> _richStreams = new();
    private readonly HashSet<string> _richClipboardFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _richMode, _richInitializing, _richReady, _richActive, _richDisposed, _richPickerOpen;
    private Window? _richWindow;

    public event EventHandler<ChatRichActionEventArgs>? RichActionRequested;
    public event EventHandler<ChatFilesAddedEventArgs>? FilesAddedRequested;
    public Func<string, string?, CancellationToken, Task<ChatMediaStream?>>? MediaResolver { get; set; }
    internal Func<bool>? CanAcceptNativeDrop { get; set; }
    internal WebView2CompositionControl? RichBrowser => _richBrowser;
    internal string? RichUserDataFolder { get; set; }

    private void InitializeRichHost()
    {
        Loaded += (_, _) =>
        {
            Window? window = Window.GetWindow(this);
            if (!ReferenceEquals(window, _richWindow))
            {
                DetachRichWindow();
                _richWindow = window;
                if (window is not null)
                {
                    window.Activated += RichWindow_ActivityChanged;
                    window.Deactivated += RichWindow_ActivityChanged;
                    window.StateChanged += RichWindow_ActivityChanged;
                }
            }
            if (_richMode) _ = EnsureRichBrowserAsync();
            PublishRichSnapshot();
        };
        Unloaded += (_, _) => PublishRichSnapshot();
        IsVisibleChanged += (_, _) => PublishRichSnapshot();
    }

    public void SetRichMode(bool enabled)
    {
        Dispatcher.VerifyAccess();
        if (_richDisposed) return;
        _richMode = enabled;
        PageGrid.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        RichHost.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) RichStatus.Visibility = Visibility.Collapsed;
        if (enabled && IsLoaded) _ = EnsureRichBrowserAsync();
        PublishRichSnapshot();
    }

    public void SetRichActive(bool active)
    {
        Dispatcher.VerifyAccess();
        _richActive = active;
        PublishRichSnapshot();
    }

    public void ApplyRichSnapshot(object snapshot)
    {
        Dispatcher.VerifyAccess();
        if (_richDisposed) return;
        JsonObject next = JsonSerializer.SerializeToNode(snapshot, ChatJson.Options)?.AsObject()
            ?? throw new ArgumentException("A snapshot object is required.", nameof(snapshot));
        bool composerIdentityChanged = _richSnapshot is null
            || NodeText(_richSnapshot["sessionId"]) != NodeText(next["sessionId"])
            || NodeText(_richSnapshot["ownerAccountId"]) != NodeText(next["ownerAccountId"])
            || NodeText(_richSnapshot["selectedThreadId"]) != NodeText(next["selectedThreadId"]);
        if (_richSnapshot is not null &&
            (NodeText(_richSnapshot["sessionId"]) != NodeText(next["sessionId"])
                || NodeText(_richSnapshot["ownerAccountId"]) != NodeText(next["ownerAccountId"])
                || NodeText(_richSnapshot["selectedThreadId"]) != NodeText(next["selectedThreadId"])
                || HasRevokedRichThread(_richSnapshot, next)))
        {
            _richMediaLifetime.Cancel();
            _richMediaLifetime.Dispose();
            _richMediaLifetime = new();
            foreach (OwnedChatResponseStream stream in _richStreams.Values) stream.Dispose();
        }
        _richSnapshot = next;
        if (composerIdentityChanged) ResetRichComposerState();
        PublishRichSnapshot();
    }

    public void SendRichResult(string requestId, object? payload = null, string? error = null)
    {
        Dispatcher.VerifyAccess();
        if (_richDisposed || !_richReady) return;
        _richBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "result", requestId, payload, error,
            sessionId = NodeText(_richSnapshot?["sessionId"]), ownerAccountId = _richSnapshot?["ownerAccountId"]?.DeepClone()
        }, ChatJson.Options));
    }

    private bool IsRichActuallyActive => _richMode && _richActive && IsLoaded && IsVisible
        && _richWindow is { IsActive: true, WindowState: not WindowState.Minimized };
    private static string? NodeText(JsonNode? value) => value?.ToString();
    private static bool HasRevokedRichThread(JsonObject previous, JsonObject next)
    {
        if (previous["state"]?["threads"] is not JsonArray before || next["state"]?["threads"] is not JsonArray after) return false;
        HashSet<string?> remaining = after.Select(thread => NodeText(thread?["id"])).ToHashSet(StringComparer.Ordinal);
        return before.Any(thread => !remaining.Contains(NodeText(thread?["id"])));
    }
    private void RichWindow_ActivityChanged(object? sender, EventArgs args) => PublishRichSnapshot();

    private void PublishRichSnapshot()
    {
        if (_richDisposed || !_richReady || _richSnapshot is null) return;
        JsonObject copy = (JsonObject)_richSnapshot.DeepClone();
        copy["type"] = "snapshot";
        copy["isActive"] = IsRichActuallyActive;
        copy["mediaOrigin"] = RichMediaOrigin;
        _richBrowser?.CoreWebView2?.PostWebMessageAsJson(copy.ToJsonString(ChatJson.Options));
    }

    private async Task EnsureRichBrowserAsync()
    {
        if (_richDisposed || _richInitializing || _richBrowser is not null) return;
        _richInitializing = true;
        RichStatus.Text = LauncherLocalization.IsEnglish ? "Loading messages…" : "Chargement des messages…";
        RichStatus.Visibility = Visibility.Visible;
        try
        {
            WebView2CompositionControl browser = _richBrowser = new()
            {
                // The native Citadel backdrop spans the shell and the message page.
                // Let transparent document pixels reveal that same artwork.
                DefaultBackgroundColor = System.Drawing.Color.Transparent,
                AllowDrop = true,
                AllowExternalDrop = false
            };
            // CompositionControl does not reliably forward Explorer drops to
            // Chromium. WPF owns file drops; paths never enter web messages.
            browser.PreviewDragEnter += RichNativeDragOver;
            browser.PreviewDragOver += RichNativeDragOver;
            browser.PreviewDragLeave += RichNativeDragLeave;
            browser.PreviewDrop += RichNativeDrop;
            RichHost.Children.Add(browser);
            string cache = RichUserDataFolder ?? Path.GetFullPath(Path.Combine(LauncherBuildFlavor.GetAvatarCacheRoot(), "..", "chat-webview"));
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(userDataFolder: cache);
            _richLifetime.Token.ThrowIfCancellationRequested();
            _richEnvironment = environment;
            await browser.EnsureCoreWebView2Async(environment);
            _richLifetime.Token.ThrowIfCancellationRequested();
            CoreWebView2 core = browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.AddWebResourceRequestedFilter("https://animeclub.fr/*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.AddWebResourceRequestedFilter(RichMediaOrigin + "*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += RichWebResourceRequested;
            core.NavigationStarting += (_, args) => args.Cancel = !IsRichDocument(args.Uri);
            core.FrameNavigationStarting += (_, args) => args.Cancel = !IsRichFrame(args.Uri);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.WebMessageReceived += RichWebMessageReceived;
            core.NavigationCompleted += (_, args) =>
            {
                if (_richDisposed) return;
                if (!args.IsSuccess) ShowRichFailure();
            };
            core.ProcessFailed += (_, _) => { if (!_richDisposed) { _richReady = false; ShowRichFailure(); } };
            core.Navigate(RichOrigin);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_richDisposed) ShowRichFailure(); }
        finally { _richInitializing = false; }
    }

    private void ShowRichFailure()
    {
        RichStatus.Text = LauncherLocalization.IsEnglish
            ? "Messages could not be loaded. Reopen the launcher to try again."
            : "La page Messages n’a pas pu être chargée. Rouvrez le launcher pour réessayer.";
        RichStatus.Visibility = _richMode ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static bool IsRichDocument(string source) => Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == "https" && uri.IdnHost == "animeclub.fr" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.AbsolutePath is "/atlas-messages/" or "/atlas-messages/index.html" && uri.Query.Length == 0;

    internal static bool IsRichFrame(string source)
    {
        if (source == "about:blank") return true;
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        return (uri.IdnHost is "www.youtube.com" or "www.youtube-nocookie.com" && uri.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal))
            || (uri.IdnHost == "player.vimeo.com" && uri.AbsolutePath.StartsWith("/video/", StringComparison.Ordinal));
    }

    private async void RichWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_richEnvironment is not { } environment) return;
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out Uri? uri))
        { args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", ""); return; }
        if (uri.IdnHost == "animeclub.fr")
        {
            (Stream? stream, string type) = OpenRichAsset(uri);
            args.Response = environment.CreateWebResourceResponse(stream, stream is null ? 404 : 200,
                stream is null ? "Not Found" : "OK", "Content-Type: " + type + "\r\nCache-Control: no-store\r\n"
                + "X-Content-Type-Options: nosniff\r\nReferrer-Policy: strict-origin-when-cross-origin\r\n"
                + "Content-Security-Policy: " + RichContentSecurityPolicy);
            return;
        }
        using CoreWebView2Deferral deferral = args.GetDeferral();
        CancellationToken token = _richMediaLifetime.Token;
        ChatMediaStream? media = null;
        try
        {
            if (_richDisposed || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || uri.IdnHost != "atlas-chat-media.invalid" || args.Request.Method != "GET" || MediaResolver is null)
                throw new InvalidOperationException("Invalid media request.");
            string? range = args.Request.Headers.Contains("Range") ? args.Request.Headers.GetHeader("Range") : null;
            media = await MediaResolver(uri.PathAndQuery.TrimStart('/'), range, token);
            token.ThrowIfCancellationRequested();
            if (media is null) throw new IOException("Media unavailable.");
            string headers = "Content-Type: " + HeaderValue(media.ContentType) + "\r\nCache-Control: no-store\r\n"
                + "X-Content-Type-Options: nosniff\r\nAccess-Control-Allow-Origin: https://animeclub.fr\r\nAccept-Ranges: bytes";
            if (media.Length is long length) headers += "\r\nContent-Length: " + length.ToString(CultureInfo.InvariantCulture);
            if (media.ContentRange is not null) headers += "\r\nContent-Range: " + HeaderValue(media.ContentRange);
            Guid key = Guid.NewGuid();
            OwnedChatResponseStream owned = new(media, () => _richStreams.TryRemove(key, out _));
            _richStreams[key] = owned;
            args.Response = environment.CreateWebResourceResponse(owned, media.StatusCode, media.StatusCode == 206 ? "Partial Content" : "OK", headers);
            media = null;
        }
        catch (Exception)
        {
            media?.Dispose();
            if (!_richDisposed) args.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
        }
    }

    private static string HeaderValue(string value) => value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    internal static (Stream? Stream, string ContentType) OpenRichAsset(Uri uri)
    {
        if (uri.Scheme != "https" || uri.IdnHost != "animeclub.fr" || !uri.IsDefaultPort || uri.Query.Length != 0
            || !uri.AbsolutePath.StartsWith("/atlas-messages/", StringComparison.Ordinal)) return (null, "text/plain");
        string relative = uri.AbsolutePath["/atlas-messages/".Length..];
        if (relative.Length == 0) relative = "index.html";
        if (relative.Contains('%') || relative.Contains("..", StringComparison.Ordinal)) return (null, "text/plain");
        string type = Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
            ".woff2" => "font/woff2", ".ttf" => "font/ttf", _ => "text/plain; charset=utf-8"
        };
        return (typeof(ChatViewV2).Assembly.GetManifestResourceStream("WotLK.Launcher.Assets.Chat." + relative.Replace('/', '.')), type);
    }

    private void RichWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_richDisposed || !IsRichDocument(args.Source) || args.WebMessageAsJson.Length > 65536) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(args.WebMessageAsJson, new JsonDocumentOptions { MaxDepth = 24 });
            JsonElement envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("action", out JsonElement actionElement)
                || actionElement.ValueKind != JsonValueKind.String) return;
            string action = actionElement.GetString()!;
            if (action == "ready")
            {
                _richReady = true;
                ResetRichComposerState();
                RichStatus.Visibility = Visibility.Collapsed;
                PublishRichSnapshot();
                return;
            }
            if (!TryValidateRichAction(envelope, out ChatRichActionEventArgs? request) || request is null) return;
            if (action == "composerState")
            {
                SendRichResult(request.RequestId, new { accepted = TryApplyRichComposerState(envelope) });
                return;
            }
            if (action == "read" && !IsRichActuallyActive) return;
            if (action is "pickFiles" or "dropFiles" or "pasteImage")
            {
                if (!IsRichActuallyActive || !CanAcceptRichFileRequest(envelope))
                { SendRichResult(request.RequestId, new { accepted = false }); return; }
                string threadId = envelope.GetProperty("payload").GetProperty("threadId").GetString()!;
                IReadOnlyList<string> paths = action switch
                {
                    "dropFiles" => args.AdditionalObjects.OfType<CoreWebView2File>().Take(ChatLimits.MaximumAttachmentsPerMessage).Select(file => file.Path).ToArray(),
                    "pasteImage" => PasteRichImage(),
                    _ => PickRichFiles()
                };
                // A modal picker can outlive the session or selected conversation.
                if (paths.Count != 0 && CanAcceptRichFileRequest(envelope))
                    FilesAddedRequested?.Invoke(this, new(request.RequestId, request.SessionId, request.OwnerAccountId, request.Sequence, threadId, paths));
                else SendRichResult(request.RequestId, new { accepted = false });
                return;
            }
            RichActionRequested?.Invoke(this, request);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException
            or ArgumentException or IOException or System.Runtime.InteropServices.COMException) { }
    }

    internal bool TryValidateRichAction(JsonElement action, out ChatRichActionEventArgs? request)
    {
        request = null;
        if (!_richMode || _richSnapshot is null || !action.TryGetProperty("type", out JsonElement type) || type.GetString() != "action"
            || !action.TryGetProperty("requestId", out JsonElement requestId) || requestId.ValueKind != JsonValueKind.String || requestId.GetString() is not { Length: > 0 and <= 128 } id
            || !action.TryGetProperty("sessionId", out JsonElement session) || !Guid.TryParse(session.GetString(), out Guid sessionId)
            || sessionId.ToString() != NodeText(_richSnapshot["sessionId"])
            || !action.TryGetProperty("ownerAccountId", out JsonElement owner) || !owner.TryGetUInt32(out uint ownerId)
            || ownerId.ToString(CultureInfo.InvariantCulture) != NodeText(_richSnapshot["ownerAccountId"])
            || !action.TryGetProperty("sequence", out JsonElement sequenceElement)
            || !long.TryParse(sequenceElement.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
            || !long.TryParse(NodeText(_richSnapshot["sequence"]), out long currentSequence) || sequence > currentSequence
            || !action.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object) return false;
        if (payload.TryGetProperty("threadId", out JsonElement threadId)
            && action.GetProperty("action").GetString() is not ("selectThread" or "threadSelf" or "member")
            && threadId.GetString() != NodeText(_richSnapshot["selectedThreadId"])) return false;
        request = new(id, sessionId, ownerId, sequence, action.Clone());
        return true;
    }

    private IReadOnlyList<string> PickRichFiles()
    {
        _richPickerOpen = true;
        try
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = LauncherLocalization.IsEnglish ? "Add files" : "Ajouter des fichiers", Multiselect = true, CheckFileExists = true,
                Filter = (LauncherLocalization.IsEnglish ? "Images, documents, audio and video" : "Images, documents, audio et vidéo")
                    + "|" + ChatAttachmentFormats.NativeFileDialogFilterPattern
            };
            return dialog.ShowDialog(_richWindow) == true ? dialog.FileNames.Take(ChatLimits.MaximumAttachmentsPerMessage).ToArray() : [];
        }
        finally { _richPickerOpen = false; }
    }

    private IReadOnlyList<string> PasteRichImage()
    {
        // Prefer original copied files: converting a GIF to a bitmap loses its animation.
        if (Clipboard.ContainsFileDropList())
            return Clipboard.GetFileDropList().Cast<string>()
                .Where(File.Exists).Take(ChatLimits.MaximumAttachmentsPerMessage).ToArray();
        if (!Clipboard.ContainsImage()) return [];
        BitmapSource? bitmap = Clipboard.GetImage();
        if (bitmap is null || bitmap.PixelWidth > 16384 || bitmap.PixelHeight > 16384
            || (long)bitmap.PixelWidth * bitmap.PixelHeight > 64000000) return [];
        string root = Path.Combine(Path.GetTempPath(), "AtlasChatClipboard");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "capture-" + Guid.NewGuid().ToString("N") + ".png");
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) encoder.Save(file);
        _richClipboardFiles.Add(path);
        return [path];
    }

    private void DetachRichWindow()
    {
        if (_richWindow is null) return;
        _richWindow.Activated -= RichWindow_ActivityChanged;
        _richWindow.Deactivated -= RichWindow_ActivityChanged;
        _richWindow.StateChanged -= RichWindow_ActivityChanged;
        _richWindow = null;
    }

    public void DisposeRich()
    {
        Dispatcher.VerifyAccess();
        if (_richDisposed) return;
        _richDisposed = true;
        _richLifetime.Cancel();
        _richMediaLifetime.Cancel();
        foreach (OwnedChatResponseStream stream in _richStreams.Values) stream.Dispose();
        _richBrowser?.Dispose();
        _richBrowser = null;
        _richSnapshot = null;
        MediaResolver = null;
        CanAcceptNativeDrop = null;
        DetachRichWindow();
        _richLifetime.Dispose();
        _richMediaLifetime.Dispose();
        // The workspace copies selected files into its own persistent upload spool.
        foreach (string path in _richClipboardFiles)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _richClipboardFiles.Clear();
    }

    private sealed class OwnedChatResponseStream(ChatMediaStream media, Action release) : Stream
    {
        private int _disposed;
        public override bool CanRead => _disposed == 0 && media.Stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => media.Length ?? throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed != 0) return 0;
            try { int read = media.Stream.Read(buffer, offset, count); if (read == 0) Dispose(); return read; }
            catch { Dispose(); throw; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_disposed != 0) return 0;
            try { int read = await media.Stream.ReadAsync(buffer, ct).ConfigureAwait(false); if (read == 0) Dispose(); return read; }
            catch { Dispose(); throw; }
        }
        protected override void Dispose(bool disposing)
        { if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) { media.Dispose(); release(); } base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
