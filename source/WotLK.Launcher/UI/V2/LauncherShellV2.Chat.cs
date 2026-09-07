using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private LauncherChatCoordinator? _chatCoordinator;
    private long _chatSnapshotSequence = -1;
    private bool _chatPresentationClosed;

    internal ChatViewV2 ChatPage => ChatView;

    internal void AttachChat(LauncherChatCoordinator coordinator)
    {
        if (IsPreviewMode) throw new InvalidOperationException("Le preview ne peut pas recevoir la messagerie réelle.");
        ArgumentNullException.ThrowIfNull(coordinator);
        if (_chatCoordinator is { } previous)
        {
            previous.SnapshotChanged -= ChatCoordinator_SnapshotChanged;
            previous.SetViewActive(false);
        }
        _chatCoordinator = coordinator;
        _chatSnapshotSequence = -1;
        coordinator.SnapshotChanged += ChatCoordinator_SnapshotChanged;
        ApplyChatSnapshot(coordinator.CurrentSnapshot);
        coordinator.Start();
        RefreshChatViewActivation();
    }

    private void InitializeChatPresentation()
    {
        InitializeRichChatPresentation();
        ChatView.SetLocale(LauncherLocalization.CurrentLocale);
        ChatView.ViewportReadChanged += ChatView_ViewportReadChanged;
        LauncherLocalization.LocaleChanged += ChatLocaleChanged;
        FriendsState.PropertyChanged += FriendsState_ChatAndProfileChanged;
        AuthState.PropertyChanged += ChatOverlayChanged;
        ProfileState.PropertyChanged += ChatOverlayChanged;
        AvatarCropState.PropertyChanged += ChatOverlayChanged;
        ActivityState.PropertyChanged += ChatOverlayChanged;
        PatchNoteState.PropertyChanged += ChatOverlayChanged;
        Activated += ChatWindowActivityChanged;
        Deactivated += ChatWindowActivityChanged;
        StateChanged += ChatWindowActivityChanged;
        IsVisibleChanged += ChatWindowVisibilityChanged;
        Loaded += ChatWindowLoaded;
    }

    private void DetachChatPresentation()
    {
        _chatPresentationClosed = true;
        DetachRichChatPresentation();
        ChatView.ViewportReadChanged -= ChatView_ViewportReadChanged;
        LauncherLocalization.LocaleChanged -= ChatLocaleChanged;
        FriendsState.PropertyChanged -= FriendsState_ChatAndProfileChanged;
        AuthState.PropertyChanged -= ChatOverlayChanged;
        ProfileState.PropertyChanged -= ChatOverlayChanged;
        AvatarCropState.PropertyChanged -= ChatOverlayChanged;
        ActivityState.PropertyChanged -= ChatOverlayChanged;
        PatchNoteState.PropertyChanged -= ChatOverlayChanged;
        Activated -= ChatWindowActivityChanged;
        Deactivated -= ChatWindowActivityChanged;
        StateChanged -= ChatWindowActivityChanged;
        IsVisibleChanged -= ChatWindowVisibilityChanged;
        Loaded -= ChatWindowLoaded;
        if (_chatCoordinator is { } coordinator)
        {
            coordinator.SnapshotChanged -= ChatCoordinator_SnapshotChanged;
            coordinator.SetViewActive(false);
        }
        _chatCoordinator = null;
        ChatView.ApplyState(new());
    }

    private void ChatCoordinator_SnapshotChanged(object? sender, ChatRuntimeSnapshotEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) Apply();
        else _ = Dispatcher.BeginInvoke((Action)Apply);
        void Apply()
        {
            if (!_chatPresentationClosed && ReferenceEquals(sender, _chatCoordinator)) ApplyChatSnapshot(e.Snapshot);
        }
    }

    private void ApplyChatSnapshot(ChatRuntimeSnapshot snapshot)
    {
        if (_chatWorkspace is not null && !_chatWorkspace.CurrentSnapshot.IsLegacyFallback) return;
        if (_chatPresentationClosed || snapshot.Sequence < _chatSnapshotSequence) return;
        _chatSnapshotSequence = snapshot.Sequence;
        ChatStrings strings = ChatView.UiState.Strings;
        ChatView.ApplyState(new ChatViewState
        {
            SessionId = snapshot.SessionId,
            OwnerAccountId = snapshot.OwnerAccountId,
            Conversations = snapshot.Conversations.Select(conversation => new ChatConversationUiItem(
                conversation.FriendAccountId, conversation.FriendUsername, conversation.LastMessage.Body,
                conversation.LastMessage.CreatedAt, conversation.UnreadCount)).ToImmutableArray(),
            UnreadCount = snapshot.UnreadCount,
            SelectedFriendAccountId = snapshot.SelectedFriendAccountId,
            SelectedFriendUsername = snapshot.SelectedFriendUsername,
            Messages = snapshot.Messages.Select(message => new ChatMessageUiItem(message.Id, message.SenderAccountId,
                message.SenderUsername, message.Body, message.Origin, message.CreatedAt)).ToImmutableArray(),
            IsAvailable = snapshot.IsAvailable,
            IsLoading = snapshot.IsLoading,
            IsLoadingEarlier = snapshot.IsLoadingEarlier,
            IsSending = snapshot.IsSending,
            HasEarlier = snapshot.HasEarlier,
            CanSend = snapshot.CanSend,
            ErrorMessage = strings.ErrorForCode(snapshot.ErrorCode)
        });
    }

    private void ChatLocaleChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) Apply();
        else _ = Dispatcher.BeginInvoke((Action)Apply);
        void Apply()
        {
            if (_chatPresentationClosed) return;
            ChatView.SetLocale(LauncherLocalization.CurrentLocale);
            if (_chatWorkspace is { } workspace) workspace.RefreshPresentation();
            else if (_chatCoordinator is { } coordinator) ApplyChatSnapshot(coordinator.CurrentSnapshot);
        }
    }

    private void MessagesNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellState.IsNavigationEnabled || _overlayCoordinator.Current != ShellOverlayKind.None) return;
        if (!IsPreviewMode && !ShellState.IsAuthenticated) return;
        OpenChatPage();
        if (_chatWorkspace is { } workspace && !workspace.CurrentSnapshot.IsLegacyFallback) _ = workspace.RefreshAsync();
        else if (_chatCoordinator is { } coordinator) _ = coordinator.RefreshAsync();
    }

    private async void FriendsDrawer_MessageRequested(object? sender, FriendPublicProfileRequestedEventArgs e)
    {
        FriendUiItem? friend = FriendsState.Current.Friends.FirstOrDefault(item => item.AccountId == e.AccountId);
        if (friend is null || (!IsPreviewMode && !ShellState.IsAuthenticated)) return;
        SuppressFriendsFocusRestore();
        _overlayCoordinator.CloseFriends();
        _overlayCoordinator.CloseProfile();
        OpenChatPage();
        if (_chatWorkspace is { } workspace && !workspace.CurrentSnapshot.IsLegacyFallback)
        {
            try { await workspace.OpenDirectThreadAsync(friend.AccountId); }
            catch (Exception) { /* The workspace publishes a localized operation failure. */ }
        }
        else if (_chatCoordinator is { } coordinator) await coordinator.OpenConversationAsync(friend.AccountId, friend.Username);
        else ChatView.ApplyState(new ChatViewState { SelectedFriendAccountId = friend.AccountId, SelectedFriendUsername = friend.Username });
    }

    private void OpenChatPage()
    {
        NavigateTo(LauncherShellPage.Chat);
    }

    private async void ChatView_ConversationRequested(object? sender, ChatConversationRequestedEventArgs e)
    {
        if (_chatCoordinator is { } coordinator && CurrentPage == LauncherShellPage.Chat)
            await coordinator.OpenConversationAsync(e.AccountId, e.Username);
    }

    private async void ChatView_SendRequested(object? sender, ChatSendRequestedEventArgs e)
    {
        LauncherChatCoordinator? coordinator = _chatCoordinator;
        if (coordinator is null || CurrentPage != LauncherShellPage.Chat || !ShellState.IsAuthenticated)
        {
            ChatView.CompleteSend(e.ClientMessageId, false);
            return;
        }
        ChatSendCompletion completion = await coordinator.SendAsync(e.AccountId, e.ClientMessageId, e.Body, e.SessionId);
        if (_chatPresentationClosed || !ReferenceEquals(coordinator, _chatCoordinator)) return;
        ApplyChatSnapshot(coordinator.CurrentSnapshot);
        ChatView.CompleteSend(completion.ClientMessageId, completion.Success, ChatView.UiState.Strings.ErrorForCode(completion.ErrorCode));
    }

    private async void ChatView_LoadEarlierRequested(object? sender, ChatLoadEarlierRequestedEventArgs e)
    {
        if (_chatCoordinator is { } coordinator && CurrentPage == LauncherShellPage.Chat)
            await coordinator.LoadEarlierAsync(e.AccountId, e.BeforeId);
    }

    private void ChatView_ViewportReadChanged(object? sender, ChatViewportReadChangedEventArgs e)
    {
        if (!_chatPresentationClosed)
            _chatCoordinator?.SetThreadAtBottom(e.SessionId, e.FriendAccountId, e.AtBottom, e.ThroughMessageId);
    }

    private void FriendsState_ChatAndProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(FriendsUiState.Current)) RefreshOpenFriendProfile();
        _chatWorkspace?.RefreshPresentation();
        RefreshChatViewActivation();
    }

    private void ChatOverlayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ProfileState) && string.IsNullOrEmpty(e.PropertyName))
            _chatWorkspace?.RefreshPresentation();
        RefreshChatViewActivation();
    }
    private void ChatWindowActivityChanged(object? sender, EventArgs e) => RefreshChatViewActivation();
    private void ChatWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshChatViewActivation();
    private void ChatWindowLoaded(object sender, RoutedEventArgs e) => RefreshChatViewActivation();

    private bool IsChatActuallyActive => !_chatPresentationClosed && IsLoaded && IsActive && IsVisible
        && WindowState != WindowState.Minimized && CurrentPage == LauncherShellPage.Chat
        && !AuthState.IsOpen && !FriendsState.IsOpen && !ProfileState.IsOpen && !AvatarCropState.IsOpen
        && !ActivityState.IsOpen && !PatchNoteState.IsOpen;

    private void RefreshChatViewActivation()
    {
        bool active = IsChatActuallyActive;
        bool legacy = _chatWorkspace is null || _chatWorkspace.CurrentSnapshot.IsLegacyFallback;
        _chatCoordinator?.SetViewActive(active && legacy);
        _chatWorkspace?.SetViewActive(active && !legacy);
        ChatView.SetRichActive(active && !legacy);
    }
}
