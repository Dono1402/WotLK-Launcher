using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private LauncherChatWorkspace? _chatWorkspace;
    private long _richSnapshotSequence = -1;

    internal void AttachChatWorkspace(LauncherChatWorkspace workspace)
    {
        if (IsPreviewMode) throw new InvalidOperationException("Le preview ne peut pas recevoir la messagerie réelle.");
        ArgumentNullException.ThrowIfNull(workspace);
        if (_chatWorkspace is { } previous)
        {
            previous.SnapshotChanged -= ChatWorkspace_SnapshotChanged;
            previous.SetViewActive(false);
        }
        _chatWorkspace = workspace;
        _richSnapshotSequence = -1;
        workspace.SnapshotChanged += ChatWorkspace_SnapshotChanged;
        ChatView.MediaResolver = ResolveChatMediaAsync;
        ChatView.CanAcceptNativeDrop = () => IsChatDropAvailable;
        ApplyRichChatSnapshot(workspace.CurrentSnapshot);
        workspace.Start();
        RefreshChatViewActivation();
    }

    private void InitializeRichChatPresentation()
    {
        ChatView.RichActionRequested += ChatView_RichActionRequested;
        ChatView.FilesAddedRequested += ChatView_RichFilesAdded;
        AccountState.PropertyChanged += AccountState_RichChatChanged;
    }

    private void DetachRichChatPresentation()
    {
        _richCharacterLifetime.Cancel();
        _richCharacterLifetime.Dispose();
        if (_chatWorkspace is { } workspace)
        {
            workspace.SnapshotChanged -= ChatWorkspace_SnapshotChanged;
            workspace.SetViewActive(false);
        }
        _chatWorkspace = null;
        AccountState.PropertyChanged -= AccountState_RichChatChanged;
        ChatView.RichActionRequested -= ChatView_RichActionRequested;
        ChatView.FilesAddedRequested -= ChatView_RichFilesAdded;
        ChatView.DisposeRich();
    }

    internal bool IsChatThreadVisible(string threadId) => Dispatcher.CheckAccess() && IsChatActuallyActive
        && _chatWorkspace?.CurrentSnapshot is { IsLegacyFallback: false } snapshot && snapshot.SelectedThreadId == threadId;

    private bool IsChatDropAvailable => !_chatPresentationClosed && IsLoaded && IsVisible
        && WindowState != System.Windows.WindowState.Minimized && CurrentPage == LauncherShellPage.Chat
        && !AuthState.IsOpen && !FriendsState.IsOpen && !ProfileState.IsOpen && !AvatarCropState.IsOpen
        && !ActivityState.IsOpen && !PatchNoteState.IsOpen;

    private void AccountState_RichChatChanged(object? sender, PropertyChangedEventArgs e) => _chatWorkspace?.RefreshPresentation();

    private void ChatWorkspace_SnapshotChanged(object? sender, ChatWorkspaceSnapshotEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) Apply();
        else _ = Dispatcher.BeginInvoke((Action)Apply);
        void Apply()
        {
            if (!_chatPresentationClosed && ReferenceEquals(sender, _chatWorkspace)) ApplyRichChatSnapshot(e.Snapshot);
        }
    }

    private void ApplyRichChatSnapshot(ChatWorkspaceSnapshot snapshot)
    {
        if (_chatPresentationClosed || snapshot.Sequence < _richSnapshotSequence) return;
        EnsureRichCharacterSession(snapshot);
        _richSnapshotSequence = snapshot.Sequence;
        ChatView.SetRichMode(!snapshot.IsLegacyFallback);
        _chatCoordinator?.SetPollingEnabled(snapshot.IsLegacyFallback);
        if (snapshot.IsLegacyFallback)
        {
            if (_chatCoordinator is { } legacy) ApplyChatSnapshot(legacy.CurrentSnapshot);
            return;
        }
        ChatView.UiState.ApplyState(new ChatViewState { SessionId = snapshot.SessionId,
            OwnerAccountId = snapshot.OwnerAccountId, UnreadCount = snapshot.State.Threads.Sum(thread => Math.Max(0, thread.UnreadCount)) });
        ChatView.ApplyRichSnapshot(ProjectRichSnapshot(snapshot));
    }

    private object ProjectRichSnapshot(ChatWorkspaceSnapshot snapshot)
    {
        Dictionary<uint, FriendUiItem> friends = FriendsState.Current.Friends.ToDictionary(friend => friend.AccountId);
        ChatProfileDto Profile(ChatProfileDto profile)
        {
            friends.TryGetValue(profile.AccountId, out FriendUiItem? friend);
            bool nativeAvatar = profile.AccountId == snapshot.OwnerAccountId ? AccountState.Current.AvatarImage is not null
                : friend?.AvatarImage is BitmapSource;
            ChatProfileDto projected = profile with { AvatarUrl = profile.AvatarUrl is not null || nativeAvatar
                ? ChatViewV2.RichMediaOrigin + "avatars/" + profile.AccountId.ToString(CultureInfo.InvariantCulture) + "/"
                    + Uri.EscapeDataString(friend?.AvatarVersion?.ToString(CultureInfo.InvariantCulture) ?? profile.AvatarVersion ?? "0") : null };
            if (projected.Presence == "offline")
                return projected with { CharacterGuid = null, CharacterName = null, CharacterClass = null, ZoneName = null };
            if (friend is null) return projected;
            FriendCharacterUiItem? character = friend.AllCharacters.FirstOrDefault(item => item.IsOnline);
            return projected with {
                CharacterName = character?.Name ?? profile.CharacterName, CharacterClass = character?.ClassName ?? profile.CharacterClass,
                ZoneName = character?.ZoneName ?? profile.ZoneName };
        }
        ChatMessageDto Message(ChatMessageDto message) => message with { Sender = Profile(message.Sender) };
        ChatThreadDto Thread(ChatThreadDto thread) => thread with { Members = thread.Members.Select(member => member with
            { Profile = Profile(member.Profile) }).ToArray(), LastMessage = thread.LastMessage is { } last ? Message(last) : null,
            PinnedMessages = thread.PinnedMessages.Select(Message).ToArray() };
        object Upload(ChatWorkspaceUpload upload) => new { id = upload.LocalId, upload.ThreadId, upload.FileName,
            upload.ContentType, upload.Size, upload.Offset, isComplete = upload.Status == "complete", upload.Attachment,
            previewUrl = ChatViewV2.RichMediaOrigin + "attachments/" + Uri.EscapeDataString(upload.LocalId),
            upload.Status, error = upload.ErrorCode };
        object[] Attachments(IReadOnlyList<string> ids) => ids.Select(id => snapshot.Uploads.FirstOrDefault(upload =>
            upload.LocalId == id || upload.Attachment?.Id == id)).OfType<ChatWorkspaceUpload>().Select(Upload).ToArray();
        double Progress(ChatOutboxEntry entry)
        {
            ChatWorkspaceUpload[] uploads = snapshot.Uploads.Where(upload => entry.AttachmentIds.Contains(upload.LocalId)
                || upload.Attachment is { } attachment && entry.AttachmentIds.Contains(attachment.Id)).ToArray();
            long size = uploads.Sum(upload => upload.Size);
            return size == 0 ? 0 : Math.Clamp((double)uploads.Sum(upload => upload.Offset) / size, 0, 1);
        }
        return new { type = "snapshot", snapshot.SessionId, snapshot.OwnerAccountId, snapshot.Sequence,
            locale = LauncherLocalization.CurrentLocale, isActive = IsChatActuallyActive, snapshot.IsAvailable,
            snapshot.IsLoading, error = snapshot.ErrorCode,
            state = snapshot.State with { Self = Profile(snapshot.State.Self), Contacts = snapshot.State.Contacts.Select(Profile).ToArray(),
                Threads = snapshot.State.Threads.Select(Thread).ToArray() }, snapshot.SelectedThreadId,
            messages = snapshot.Messages.Select(Message).ToArray(), snapshot.HasEarlier, snapshot.IsLoadingEarlier,
            draft = new { body = snapshot.Draft?.Body ?? "", replyToMessageId = snapshot.Draft?.ReplyToMessageId,
                attachments = Attachments(snapshot.Draft?.AttachmentIds ?? []), card = snapshot.Draft?.Card },
            ownCharacters = OwnCharactersProjection,
            pending = snapshot.Outbox.Where(entry => entry.Status is not ("sent" or "cancelled")).Select(entry => new
            { entry.ClientMessageId, entry.ThreadId, entry.Body, entry.CreatedAt, entry.Status, error = entry.ErrorCode,
                entry.ReplyToMessageId, entry.Card, attachments = Attachments(entry.AttachmentIds), progress = Progress(entry),
                canCancel = !entry.WasSubmitted }).ToArray(), snapshot.Typing, mediaOrigin = ChatViewV2.RichMediaOrigin };
    }

    private async Task<ChatMediaStream?> ResolveChatMediaAsync(string resource, string? range, CancellationToken token)
    {
        LauncherChatWorkspace? workspace = _chatWorkspace;
        if (workspace is null) return null;
        ChatWorkspaceSnapshot snapshot = workspace.CurrentSnapshot;
        if (snapshot.OwnerAccountId == 0) return null;
        if (resource.StartsWith("avatars/", StringComparison.Ordinal))
        {
            string[] parts = resource.Split('/');
            if (parts.Length != 3 || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint account)) return null;
            bool authorized = account == snapshot.OwnerAccountId || snapshot.State.Contacts.Any(profile => profile.AccountId == account)
                || snapshot.State.Threads.Any(thread => thread.Members.Any(member => member.Profile.AccountId == account))
                || snapshot.Messages.Any(message => message.Sender.AccountId == account);
            if (!authorized) return null;
            ChatMediaStream? native = await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_chatWorkspace, workspace) || workspace.CurrentSnapshot.SessionId != snapshot.SessionId) return null;
                BitmapSource? bitmap = account == snapshot.OwnerAccountId ? AccountState.Current.AvatarImage
                    : FriendsState.Current.Friends.FirstOrDefault(friend => friend.AccountId == account)?.AvatarImage as BitmapSource;
                if (bitmap is null) return null;
                return EncodeRichAvatar(bitmap);
            });
            if (native is not null) return native;
        }
        ChatMediaStream? result = await workspace.OpenMediaAsync(resource, range, token).ConfigureAwait(false);
        if (!ReferenceEquals(_chatWorkspace, workspace) || workspace.CurrentSnapshot.SessionId != snapshot.SessionId)
        { result?.Dispose(); return null; }
        return result;
    }

    internal static ChatMediaStream? EncodeRichAvatar(BitmapSource bitmap)
    {
        try
        {
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            if (width is < 1 or > 4096 || height is < 1 or > 4096) return null;
            int stride = checked((width * bitmap.Format.BitsPerPixel + 7) / 8);
            byte[] pixels = new byte[checked(stride * height)];
            bitmap.CopyPixels(pixels, stride, 0);
            // A frozen decoder frame can still own thread-bound metadata.
            // Rebuild from pixels before handing it to the WPF PNG encoder.
            BitmapSource detached = BitmapSource.Create(width, height, 96, 96, bitmap.Format, bitmap.Palette, pixels, stride);
            using MemoryStream encoded = new();
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(detached));
            encoder.Save(encoded);
            if (encoded.Length > 4 * 1024 * 1024) return null;
            byte[] bytes = encoded.ToArray();
            return new ChatMediaStream(new MemoryStream(bytes, writable: false), "image/png", bytes.Length);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException or IOException)
        { return null; }
    }

    private async void ChatView_RichActionRequested(object? sender, ChatRichActionEventArgs request)
    {
        LauncherChatWorkspace? workspace = _chatWorkspace;
        if (workspace is null || !workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence)) return;
        try
        {
            JsonElement envelope = request.Action;
            string action = envelope.GetProperty("action").GetString() ?? "";
            JsonElement payload = envelope.GetProperty("payload");
            string Text(string name) => payload.GetProperty(name).GetString() ?? throw new JsonException();
            long Id(string name) => payload.GetProperty(name).Deserialize<long>(ChatJson.Options);
            T Read<T>() => payload.Deserialize<T>(ChatJson.Options) ?? throw new JsonException();
            object? result = null;
            switch (action)
            {
                case "selectThread": await workspace.OpenThreadAsync(Text("threadId")); break;
                case "createThread": result = await workspace.CreateThreadAsync(Read<ChatCreateThreadRequest>()); break;
                case "loadEarlier": await workspace.LoadEarlierAsync(Text("threadId"), Id("beforeId")); break;
                case "draft": await workspace.SaveDraftAsync(Read<ChatWorkspaceDraft>()); break;
                case "requestOwnCharacters": await RequestRichOwnCharactersAsync(workspace); result = OwnCharactersProjection; break;
                case "selectOwnCharacter": result = await SelectRichOwnCharacterAsync(workspace, Text("threadId"), Text("characterGuid")); break;
                case "openCharacterArmory": OpenRichCharacterArmory(payload.GetProperty("ownerAccountId").GetUInt32(), Text("characterGuid")); break;
                case "send":
                    ChatOutboxEntry entry = await workspace.QueueSendAsync(Text("threadId"), Read<ChatSendMessageRequest>());
                    result = new { entry.ClientMessageId, entry.Status }; break;
                case "threadSelf": result = await workspace.UpdateThreadSelfAsync(Text("threadId"), Read<ChatThreadSelfRequest>()); break;
                case "threadUpdate": result = await workspace.UpdateThreadAsync(Text("threadId"), Read<ChatThreadUpdateRequest>()); break;
                case "member": result = await workspace.UpdateMemberAsync(Text("threadId"), Read<ChatMemberRequest>()); break;
                case "editMessage": result = await workspace.EditMessageAsync(Text("threadId"), Id("messageId"), Read<ChatEditMessageRequest>()); break;
                case "deleteMessage": result = await workspace.DeleteMessageAsync(Text("threadId"), Id("messageId")); break;
                case "reaction": result = await workspace.ReactAsync(Text("threadId"), Id("messageId"), Read<ChatReactionRequest>()); break;
                case "pinMessage": result = await workspace.PinMessageAsync(Text("threadId"), Id("messageId"), payload.GetProperty("pinned").GetBoolean()); break;
                case "dismissPreview": result = await workspace.RemovePreviewAsync(Text("threadId"), Id("messageId"), Text("previewId")); break;
                case "cardResponse": result = await workspace.RespondToCardAsync(Text("threadId"), Id("messageId"), Read<ChatCardResponseRequest>()); break;
                case "read": if (IsChatActuallyActive) await workspace.MarkReadAsync(Text("threadId"), Id("throughMessageId")); break;
                case "typing": await workspace.SetTypingAsync(Text("threadId"), payload.GetProperty("isTyping").GetBoolean()); break;
                case "preferences": await workspace.SetPreferencesAsync(Read<ChatPreferencesRequest>()); break;
                case "cancelUpload": await workspace.CancelUploadAsync(Text("threadId"), Text("uploadId")); break;
                case "removeAttachment": await workspace.RemoveAttachmentAsync(Text("threadId"), Text("uploadId")); break;
                case "retryUpload": await workspace.RetryUploadAsync(Text("threadId"), Text("uploadId")); break;
                case "cancelSend": await workspace.CancelSendAsync(payload.GetProperty("clientMessageId").GetGuid()); break;
                case "retrySend": await workspace.RetrySendAsync(payload.GetProperty("clientMessageId").GetGuid()); break;
                case "openExternal": OpenRichExternal(Text("url")); break;
                case "openProfile": OpenRichProfile(payload.GetProperty("accountId").GetUInt32()); break;
                case "downloadAttachment": await SaveRichAttachmentAsync(workspace, request, Text("attachmentId"), false); break;
                case "openAttachment": await SaveRichAttachmentAsync(workspace, request, Text("attachmentId"), true); break;
                default: throw new ChatWorkspaceException("chat-invalid-request");
            }
            if (ReferenceEquals(workspace, _chatWorkspace) && workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence))
                ChatView.SendRichResult(request.RequestId, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (ReferenceEquals(workspace, _chatWorkspace) && workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence))
                ChatView.SendRichResult(request.RequestId, null, RichError(error));
        }
    }

    private async void ChatView_RichFilesAdded(object? sender, ChatFilesAddedEventArgs request)
    {
        LauncherChatWorkspace? workspace = _chatWorkspace;
        if (workspace is null || !(request.IsNativeDrop ? IsChatDropAvailable : IsChatActuallyActive)
            || !workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence)) return;
        try
        {
            await workspace.AddFilesAsync(request.ConversationId, request.Paths);
            if (ReferenceEquals(workspace, _chatWorkspace) && workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence))
                ChatView.SendRichResult(request.RequestId, new { accepted = true });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (ReferenceEquals(workspace, _chatWorkspace) && workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence))
                ChatView.SendRichResult(request.RequestId, null, RichError(error));
        }
    }

    private static string RichError(Exception error) => error switch
    {
        ChatWorkspaceException failure => failure.Code,
        LauncherChatV2ApiException failure => failure.Code,
        JsonException or InvalidOperationException or FormatException => "chat-invalid-request",
        IOException or UnauthorizedAccessException => "chat-file-unavailable",
        _ => "chat-unavailable"
    };

    private static void OpenRichExternal(string url)
    {
        if (url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? target)
            || target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0)
            throw new ChatWorkspaceException("chat-invalid-link");
        Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
    }

    private void OpenRichProfile(uint accountId)
    {
        if (_chatWorkspace?.CurrentSnapshot.OwnerAccountId == accountId) ProfileMenu_ManageProfileRequested(this, EventArgs.Empty);
        else if (FriendsState.Current.Friends.FirstOrDefault(friend => friend.AccountId == accountId) is { } friend)
            FriendsDrawer_PublicProfileRequested(this, new FriendPublicProfileRequestedEventArgs(friend.AccountId, friend.Username));
        else throw new ChatWorkspaceException("chat-profile-unavailable");
    }

    private async Task SaveRichAttachmentAsync(LauncherChatWorkspace workspace, ChatRichActionEventArgs request, string attachmentId, bool open)
    {
        ChatWorkspaceSnapshot snapshot = workspace.CurrentSnapshot;
        ChatAttachmentDto attachment = snapshot.Messages.Concat(snapshot.State.Threads.Select(thread => thread.LastMessage).OfType<ChatMessageDto>())
            .Concat(snapshot.State.Threads.SelectMany(thread => thread.PinnedMessages)).SelectMany(message => message.Attachments)
            .FirstOrDefault(item => item.Id == attachmentId) ?? throw new ChatWorkspaceException("chat-attachment-not-found");
        string name = Path.GetFileName(attachment.FileName);
        if (name != attachment.FileName || name.Length == 0) throw new ChatWorkspaceException("chat-invalid-file");
        _ = ChatAttachmentFileSource.ContentTypeForName(name);
        string path;
        if (open)
        {
            string directory = Path.Combine(Path.GetTempPath(), "Atlas", "Messages", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, name);
        }
        else
        {
            SaveFileDialog dialog = new() { FileName = name, AddExtension = true, DefaultExt = Path.GetExtension(name),
                Filter = "Fichier (*" + Path.GetExtension(name) + ")|*" + Path.GetExtension(name), OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }
        if (!workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence)) return;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using ChatMediaStream media = await workspace.OpenMediaAsync("attachments/" + attachment.Id, null, CancellationToken.None)
                ?? throw new ChatWorkspaceException("chat-attachment-not-found");
            if (media.StatusCode != 200 || media.Length != attachment.Size) throw new ChatWorkspaceException("chat-invalid-response");
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[64 * 1024];
                long copied = 0;
                while (true)
                {
                    int count = await media.Stream.ReadAsync(buffer);
                    if (count == 0) break;
                    if (!workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence)) throw new OperationCanceledException();
                    copied += count;
                    if (copied > attachment.Size || copied > ChatLimits.MaximumAttachmentBytes) throw new ChatWorkspaceException("chat-invalid-response");
                    await output.WriteAsync(buffer.AsMemory(0, count));
                }
                if (copied != attachment.Size) throw new ChatWorkspaceException("chat-invalid-response");
                await output.FlushAsync();
            }
            if (!workspace.AcceptsAction(request.SessionId, request.OwnerAccountId, request.Sequence)) throw new OperationCanceledException();
            File.Move(temporary, path, overwrite: !open);
            if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
