using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ChatViewV2 : UserControl
{
    private int _scrollVersion;
    private bool _scrollPending;
    private bool _scrollToEndPending;
    private (Guid SessionId, uint? FriendAccountId, bool AtBottom, long ThroughMessageId)? _lastViewportRead;

    public ChatViewV2()
    {
        UiState = new ChatUiState();
        InitializeComponent();
        InitializeRichHost();
        Loaded += (_, _) => RestoreScroll(true, MessagesScrollViewer.VerticalOffset, null);
        Unloaded += (_, _) =>
        {
            ++_scrollVersion;
            _scrollPending = true;
            PublishViewportRead();
        };
        MessagesScrollViewer.ScrollChanged += MessagesScrollViewer_ScrollChanged;
    }

    public ChatUiState UiState { get; }

    public event EventHandler<ChatConversationRequestedEventArgs>? ConversationRequested;
    public event EventHandler<ChatSendRequestedEventArgs>? SendRequested;
    public event EventHandler<ChatLoadEarlierRequestedEventArgs>? LoadEarlierRequested;
    public event EventHandler<ChatViewportReadChangedEventArgs>? ViewportReadChanged;

    public void ApplyState(ChatViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Dispatcher.VerifyAccess();
        ChatViewState previous = UiState.Current;
        bool conversationChanged = previous.SessionId != state.SessionId || previous.OwnerAccountId != state.OwnerAccountId
            || previous.SelectedFriendAccountId != state.SelectedFriendAccountId;
        long? previousFirstId = UiState.Messages.IsDefaultOrEmpty ? null : UiState.Messages.Min(message => message.Id);
        long? previousLastId = UiState.Messages.IsDefaultOrEmpty ? null : UiState.Messages.Max(message => message.Id);
        double offset = MessagesScrollViewer.VerticalOffset;
        double extent = MessagesScrollViewer.ExtentHeight;
        bool wasAtBottom = (_scrollPending && _scrollToEndPending) || MessagesScrollViewer.ScrollableHeight - offset <= 36;
        _scrollPending = true;
        PublishViewportRead();
        UiState.ApplyState(state);
        long? firstId = UiState.Messages.IsDefaultOrEmpty ? null : UiState.Messages.Min(message => message.Id);
        long? lastId = UiState.Messages.IsDefaultOrEmpty ? null : UiState.Messages.Max(message => message.Id);
        bool prepended = !conversationChanged && previousFirstId is not null && firstId < previousFirstId && lastId == previousLastId;
        RestoreScroll(conversationChanged || (!prepended && wasAtBottom), offset, prepended ? extent : null);
    }

    public void SetLocale(string? locale)
    {
        Dispatcher.VerifyAccess();
        double offset = MessagesScrollViewer.VerticalOffset;
        bool atBottom = (_scrollPending && _scrollToEndPending) || MessagesScrollViewer.ScrollableHeight - offset <= 36;
        _scrollPending = true;
        PublishViewportRead();
        UiState.SetLocale(locale);
        RestoreScroll(atBottom, offset, null);
    }

    public void CompleteSend(Guid clientMessageId, bool success, string? errorMessage = null)
    {
        Dispatcher.VerifyAccess();
        UiState.CompleteSend(clientMessageId, success, errorMessage);
    }

    private void RestoreScroll(bool toEnd, double previousOffset, double? previousExtent)
    {
        int version = ++_scrollVersion;
        _scrollPending = true;
        _scrollToEndPending = toEnd;
        PublishViewportRead();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (version != _scrollVersion) return;
            MessagesScrollViewer.UpdateLayout();
            if (toEnd) MessagesScrollViewer.ScrollToEnd();
            else MessagesScrollViewer.ScrollToVerticalOffset(previousOffset
                + (previousExtent is double extent ? Math.Max(0, MessagesScrollViewer.ExtentHeight - extent) : 0));
            // Scroll commands and their layout pass are deferred by WPF. Confirm the
            // displayed cursor only after that pass, never when the data merely arrived.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (version != _scrollVersion) return;
                MessagesScrollViewer.UpdateLayout();
                _scrollPending = false;
                _scrollToEndPending = false;
                PublishViewportRead();
            });
        });
    }

    private void MessagesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MessagesScrollViewer)) PublishViewportRead();
    }

    private void PublishViewportRead()
    {
        ChatViewState state = UiState.Current;
        bool atBottom = !_scrollPending && state.SelectedFriendAccountId is not null && !UiState.Messages.IsDefaultOrEmpty
            && MessagesScrollViewer.ViewportHeight > 0
            && MessagesScrollViewer.ScrollableHeight - MessagesScrollViewer.VerticalOffset <= 1;
        long through = atBottom ? UiState.Messages.Max(message => message.Id) : 0;
        var viewport = (state.SessionId, state.SelectedFriendAccountId, atBottom, through);
        if (_lastViewportRead == viewport) return;
        _lastViewportRead = viewport;
        ViewportReadChanged?.Invoke(this, new(state.SessionId, state.SelectedFriendAccountId, atBottom, through));
    }

    private void ConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChatConversationRow conversation }
            && UiState.Current.Conversations.Any(item => item.FriendAccountId == conversation.AccountId)
            && conversation.AccountId != UiState.Current.SelectedFriendAccountId)
        {
            ConversationRequested?.Invoke(this, new(conversation.AccountId, conversation.Username));
        }
        e.Handled = true;
    }

    private void ComposerBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // IME-handled keys and Shift+Enter remain text input.
        e.Handled = HandleComposerKey(e.Key, Keyboard.Modifiers);
    }

    internal bool HandleComposerKey(Key key, ModifierKeys modifiers)
    {
        if (key != Key.Enter || modifiers != ModifierKeys.None) return false;
        RequestSend();
        return true;
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        RequestSend();
        e.Handled = true;
    }

    private void RequestSend()
    {
        if (SendRequested is not null && UiState.TryBeginSend() is ChatSendRequestedEventArgs request)
        {
            SendRequested.Invoke(this, request);
        }
    }

    private void LoadEarlierButton_Click(object sender, RoutedEventArgs e)
    {
        if (UiState.CanLoadEarlier && UiState.Current.SelectedFriendAccountId is uint accountId)
        {
            LoadEarlierRequested?.Invoke(this, new(accountId, UiState.Messages.Min(message => message.Id)));
        }
        e.Handled = true;
    }

}

public sealed class ChatViewportReadChangedEventArgs(Guid sessionId, uint? friendAccountId, bool atBottom, long throughMessageId) : EventArgs
{
    public Guid SessionId { get; } = sessionId;
    public uint? FriendAccountId { get; } = friendAccountId;
    public bool AtBottom { get; } = atBottom;
    public long ThroughMessageId { get; } = throughMessageId;
}
