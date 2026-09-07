using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ChatViewV2
{
    private bool _nativeDropShown;
    private (string? Session, string? Owner, string? Thread)? _nativeDropTarget;
    private (string? Session, string? Owner, string? Thread)? _richComposerTarget;
    private bool _richComposerAcceptsFiles;
    private long _richComposerSequence = -1;

    internal bool IsRichComposerAcceptingFiles => !_richDisposed && _richMode && _richReady
        && _richComposerAcceptsFiles && _richComposerTarget == CurrentNativeDropTarget && HasWritableDropThread(_richSnapshot);

    private bool IsRichDropAvailable => !_richDisposed && _richMode && _richReady && IsLoaded && IsVisible
        && !_richPickerOpen && CanAcceptNativeDrop?.Invoke() == true && IsRichComposerAcceptingFiles;

    private void ResetRichComposerState()
    {
        _richComposerTarget = null;
        _richComposerAcceptsFiles = false;
        _richComposerSequence = long.TryParse(NodeText(_richSnapshot?["sequence"]), out long sequence) ? sequence : -1;
        _nativeDropTarget = null;
        PublishNativeDropState(false);
    }

    internal bool TryApplyRichComposerState(JsonElement envelope)
    {
        Dispatcher.VerifyAccess();
        if (!TryValidateRichAction(envelope, out ChatRichActionEventArgs? request) || request is null
            || envelope.GetProperty("action").GetString() != "composerState" || request.Sequence < _richComposerSequence) return false;
        JsonElement payload = envelope.GetProperty("payload");
        if (!payload.TryGetProperty("threadId", out JsonElement thread) || thread.ValueKind != JsonValueKind.String
            || thread.GetString() != NodeText(_richSnapshot?["selectedThreadId"])
            || !payload.TryGetProperty("acceptsFiles", out JsonElement accepts)
            || accepts.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        _richComposerTarget = CurrentNativeDropTarget;
        _richComposerSequence = request.Sequence;
        _richComposerAcceptsFiles = accepts.GetBoolean();
        if (!_richComposerAcceptsFiles) { _nativeDropTarget = null; PublishNativeDropState(false); }
        return true;
    }

    internal bool CanAcceptRichFileRequest(JsonElement envelope) => IsRichComposerAcceptingFiles && !_richPickerOpen
        && TryValidateRichAction(envelope, out _) && envelope.GetProperty("payload").TryGetProperty("threadId", out JsonElement thread)
        && thread.ValueKind == JsonValueKind.String && thread.GetString() == NodeText(_richSnapshot?["selectedThreadId"]);

    internal static bool HasWritableDropThread(JsonObject? snapshot)
    {
        string? selected = NodeText(snapshot?["selectedThreadId"]);
        return !string.IsNullOrEmpty(selected) && snapshot?["state"]?["threads"] is JsonArray threads
            && threads.Any(thread => NodeText(thread?["id"]) == selected && thread?["canSend"]?.GetValue<bool>() == true);
    }

    private void RichNativeDragOver(object sender, DragEventArgs args)
    {
        if (!args.Data.GetDataPresent(DataFormats.FileDrop)) return;
        bool accepted = IsRichDropAvailable && (args.AllowedEffects & DragDropEffects.Copy) != 0;
        if (args.RoutedEvent == DragDrop.PreviewDragEnterEvent)
            _nativeDropTarget = accepted ? CurrentNativeDropTarget : null;
        accepted = accepted && _nativeDropTarget == CurrentNativeDropTarget;
        args.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
        PublishNativeDropState(accepted);
    }

    private void RichNativeDragLeave(object sender, DragEventArgs args)
    {
        _nativeDropTarget = null;
        PublishNativeDropState(false);
    }

    private void RichNativeDrop(object sender, DragEventArgs args)
    {
        if (!args.Data.GetDataPresent(DataFormats.FileDrop)) return;
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        bool sameTarget = _nativeDropTarget is not null && _nativeDropTarget == CurrentNativeDropTarget;
        _nativeDropTarget = null;
        PublishNativeDropState(false);
        if (!sameTarget || !IsRichDropAvailable || (args.AllowedEffects & DragDropEffects.Copy) == 0) return;
        if (args.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (SubmitNativeDrop(paths)) args.Effects = DragDropEffects.Copy;
    }

    private (string? Session, string? Owner, string? Thread) CurrentNativeDropTarget
        => (NodeText(_richSnapshot?["sessionId"]), NodeText(_richSnapshot?["ownerAccountId"]), NodeText(_richSnapshot?["selectedThreadId"]));

    internal bool SubmitNativeDrop(IReadOnlyList<string> paths)
    {
        Dispatcher.VerifyAccess();
        if (!IsRichDropAvailable || _richSnapshot is null) return false;
        string requestId = "native-drop-" + Guid.NewGuid().ToString("N");
        try
        {
            IReadOnlyList<string> accepted = ValidateNativeDropPaths(paths);
            if (accepted.Count == 0) return false;
            Guid session = Guid.Parse(NodeText(_richSnapshot["sessionId"])!);
            uint owner = uint.Parse(NodeText(_richSnapshot["ownerAccountId"])!, CultureInfo.InvariantCulture);
            long sequence = long.Parse(NodeText(_richSnapshot["sequence"])!, CultureInfo.InvariantCulture);
            string thread = NodeText(_richSnapshot["selectedThreadId"])!;
            FilesAddedRequested?.Invoke(this, new(requestId, session, owner, sequence, thread, accepted, isNativeDrop: true));
            return true;
        }
        catch (Exception error) when (error is ChatWorkspaceException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            SendRichResult(requestId, null, error is ChatWorkspaceException failure ? failure.Code : "chat-file-unavailable");
            return false;
        }
    }

    internal static IReadOnlyList<string> ValidateNativeDropPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count > ChatLimits.MaximumAttachmentsPerMessage) throw new ChatWorkspaceException("chat-too-many-attachments");
        List<string> accepted = [];
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || Directory.Exists(path))
                throw new ChatWorkspaceException("chat-file-unavailable");
            _ = ChatAttachmentFileSource.ContentTypeForName(Path.GetFileName(path));
            accepted.Add(path);
        }
        return accepted;
    }

    private void PublishNativeDropState(bool active)
    {
        if (_nativeDropShown == active) return;
        _nativeDropShown = active;
        if (_richDisposed || !_richReady) return;
        _richBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "dropState", active,
            sessionId = NodeText(_richSnapshot?["sessionId"]), ownerAccountId = _richSnapshot?["ownerAccountId"]?.DeepClone()
        }, ChatJson.Options));
    }
}
