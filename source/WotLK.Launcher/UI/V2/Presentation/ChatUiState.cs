using System.Collections.Immutable;
using System.Globalization;

namespace WotLK.Launcher.UI.V2.Presentation;

public sealed record ChatConversationUiItem(
    uint FriendAccountId,
    string FriendUsername,
    string LastMessage,
    DateTimeOffset? LastMessageAt,
    int UnreadCount);

public sealed record ChatMessageUiItem(
    long Id,
    uint SenderAccountId,
    string SenderUsername,
    string Body,
    string Origin,
    DateTimeOffset CreatedAt);

public sealed record ChatViewState
{
    // A local session generation, never an access token. Renew it after sign-out/sign-in.
    public Guid SessionId { get; init; }
    public uint OwnerAccountId { get; init; }
    public ImmutableArray<ChatConversationUiItem> Conversations { get; init; } = [];
    public int UnreadCount { get; init; }
    public uint? SelectedFriendAccountId { get; init; }
    public string SelectedFriendUsername { get; init; } = string.Empty;
    public ImmutableArray<ChatMessageUiItem> Messages { get; init; } = [];
    public bool IsAvailable { get; init; }
    public bool IsLoading { get; init; }
    public bool IsLoadingEarlier { get; init; }
    public bool IsSending { get; init; }
    public bool HasEarlier { get; init; }
    public bool CanSend { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

public sealed class ChatConversationRequestedEventArgs(uint accountId, string username) : EventArgs
{
    public uint AccountId { get; } = accountId;
    public string Username { get; } = username;
}

public sealed class ChatSendRequestedEventArgs(uint accountId, string body, Guid clientMessageId, Guid sessionId) : EventArgs
{
    public uint AccountId { get; } = accountId;
    public string Body { get; } = body;
    public Guid ClientMessageId { get; } = clientMessageId;
    public Guid SessionId { get; } = sessionId;
}

public sealed class ChatLoadEarlierRequestedEventArgs(uint accountId, long beforeId) : EventArgs
{
    public uint AccountId { get; } = accountId;
    public long BeforeId { get; } = beforeId;
}

public sealed record ChatConversationRow(ChatConversationUiItem Item, bool IsSelected, ChatStrings Strings)
{
    public uint AccountId => Item.FriendAccountId;
    public string Username => Item.FriendUsername;
    public string Initial => ChatStrings.Initial(Username);
    public string Preview => string.IsNullOrWhiteSpace(Item.LastMessage) ? Strings.NewConversation : Item.LastMessage;
    public bool HasUnread => Item.UnreadCount > 0;
    public string UnreadText => Item.UnreadCount > 99 ? "99+" : Math.Max(0, Item.UnreadCount).ToString(CultureInfo.InvariantCulture);
    public string DateText => Item.LastMessageAt is DateTimeOffset date ? Strings.FormatDate(date) : string.Empty;
    public string AccessibleName => HasUnread ? $"{Username}, {Item.UnreadCount} {Strings.UnreadMessages}" : Username;
}

public sealed record ChatMessageRow(ChatMessageUiItem Item, bool IsOwn, ChatStrings Strings)
{
    public long Id => Item.Id;
    public string Username => Item.SenderUsername;
    public string Body => Item.Body;
    public string DateText => Strings.FormatDate(Item.CreatedAt);
    public string FullDateText => Item.CreatedAt.ToLocalTime().ToString("F", Strings.Culture);
    public string OriginText => Item.Origin?.ToLowerInvariant() switch
    {
        "launcher" => "Launcher",
        "game" or "in-game" or "ingame" => Strings.InGame,
        _ => Strings.OtherOrigin
    };
}

public sealed class ChatUiState : BindableUiState
{
    public const int MaximumMessageLength = 1000;
    private ChatViewState _current = new();
    private ChatStrings _strings = new(false);
    private ImmutableArray<ChatConversationRow> _conversations = [];
    private ImmutableArray<ChatMessageRow> _messages = [];
    private readonly Dictionary<uint, string> _drafts = [];
    private readonly Dictionary<Guid, PendingSend> _pendingSends = [];
    private readonly Dictionary<uint, PendingSend> _retrySends = [];
    private readonly Dictionary<uint, string?> _sendErrors = [];
    private string _draft = string.Empty;

    public ChatViewState Current => _current;
    public ChatStrings Strings => _strings;
    public ImmutableArray<ChatConversationRow> Conversations => _conversations;
    public ImmutableArray<ChatMessageRow> Messages => _messages;
    public bool HasConversations => !_conversations.IsDefaultOrEmpty;
    public int UnreadCount => Math.Max(0, _current.UnreadCount);
    public bool HasUnread => UnreadCount > 0;
    public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString(CultureInfo.InvariantCulture);
    public bool ShowsNoConversations => !HasConversations && !_current.IsLoading;
    public bool HasSelection => _current.SelectedFriendAccountId is > 0;
    public bool ShowsNoSelection => !HasSelection;
    public bool ShowsNoMessages => HasSelection && _messages.IsDefaultOrEmpty && !_current.IsLoading;
    public bool ShowsLoading => _current.IsLoading;
    public bool ShowsUnavailable => !_current.IsAvailable;
    public bool HasEarlier => HasSelection && _current.HasEarlier && !_messages.IsDefaultOrEmpty;
    public bool CanLoadEarlier => HasEarlier && _current.IsAvailable && !_current.IsLoading && !_current.IsLoadingEarlier;
    public string EarlierText => _current.IsLoadingEarlier ? _strings.Loading : _strings.LoadEarlier;
    public string SelectedUsername => _current.SelectedFriendUsername;
    public string SelectedInitial => ChatStrings.Initial(SelectedUsername);
    public bool CanEditDraft => HasSelection && _current.OwnerAccountId > 0 && _current.SessionId != Guid.Empty;
    public bool IsSending => _current.IsSending || _pendingSends.Values.Any(send => send.AccountId == _current.SelectedFriendAccountId);
    public bool CanSend => CanEditDraft && _current.IsAvailable && _current.CanSend && !IsSending
        && NormalizeBody(_draft).Length is > 0 and <= MaximumMessageLength;
    public string SendText => IsSending ? _strings.Sending : _strings.Send;
    public string DraftLengthText => $"{NormalizeBody(_draft).Length} / {MaximumMessageLength}";
    public string ErrorMessage => _current.SelectedFriendAccountId is uint accountId && _sendErrors.TryGetValue(accountId, out string? error)
        ? error ?? _strings.SendFailed : _current.ErrorMessage;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string Draft
    {
        get => _draft;
        set
        {
            value ??= string.Empty;
            if (!CanEditDraft || !SetProperty(ref _draft, value)) return;
            uint accountId = _current.SelectedFriendAccountId!.Value;
            if (_draft.Length == 0) _drafts.Remove(accountId);
            else _drafts[accountId] = _draft;
            _sendErrors.Remove(accountId);
            if (_retrySends.TryGetValue(accountId, out PendingSend? retry) && retry.Body != NormalizeBody(_draft))
                _retrySends.Remove(accountId);
            RaisePropertyChanged(string.Empty);
        }
    }

    public void ApplyState(ChatViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool sessionChanged = state.OwnerAccountId != _current.OwnerAccountId || state.SessionId != _current.SessionId;
        bool messagesChanged = sessionChanged || state.SelectedFriendAccountId != _current.SelectedFriendAccountId
            || !SafeMessages(state.Messages).SequenceEqual(SafeMessages(_current.Messages));
        bool conversationsChanged = sessionChanged || state.SelectedFriendAccountId != _current.SelectedFriendAccountId
            || !SafeConversations(state.Conversations).SequenceEqual(SafeConversations(_current.Conversations));
        if (sessionChanged)
        {
            _drafts.Clear();
            _pendingSends.Clear();
            _retrySends.Clear();
            _sendErrors.Clear();
        }
        _current = state;
        _draft = state.SelectedFriendAccountId is uint accountId && _drafts.TryGetValue(accountId, out string? draft)
            ? draft : string.Empty;
        if (messagesChanged) RebuildMessages();
        if (conversationsChanged) RebuildConversations();
        RaisePropertyChanged(string.Empty);
    }

    public void SetLocale(string? locale)
    {
        bool english = locale?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
        if (_strings.IsEnglish == english) return;
        _strings = new ChatStrings(english);
        RebuildConversations();
        RebuildMessages();
        RaisePropertyChanged(string.Empty);
    }

    public ChatSendRequestedEventArgs? TryBeginSend()
    {
        if (!CanSend) return null;
        uint accountId = _current.SelectedFriendAccountId!.Value;
        string body = NormalizeBody(_draft);
        Guid clientMessageId = _retrySends.TryGetValue(accountId, out PendingSend? retry) && retry.Body == body
            ? retry.ClientMessageId : Guid.NewGuid();
        PendingSend send = new(accountId, _current.SessionId, clientMessageId, body, _draft);
        _pendingSends.Add(clientMessageId, send);
        _sendErrors.Remove(accountId);
        RaisePropertyChanged(string.Empty);
        return new(accountId, body, clientMessageId, _current.SessionId);
    }

    public void CompleteSend(Guid clientMessageId, bool success, string? errorMessage = null)
    {
        if (!_pendingSends.Remove(clientMessageId, out PendingSend? send) || send.SessionId != _current.SessionId) return;
        if (success)
        {
            _retrySends.Remove(send.AccountId);
            _sendErrors.Remove(send.AccountId);
            if (_drafts.TryGetValue(send.AccountId, out string? currentDraft) && currentDraft == send.DraftSnapshot)
            {
                _drafts.Remove(send.AccountId);
                if (_current.SelectedFriendAccountId == send.AccountId) _draft = string.Empty;
            }
        }
        else
        {
            _retrySends[send.AccountId] = send;
            _sendErrors[send.AccountId] = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
        }
        RaisePropertyChanged(string.Empty);
    }

    private void RebuildMessages() => _messages = SafeMessages(_current.Messages).OrderBy(message => message.Id)
        .Select(message => new ChatMessageRow(message, message.SenderAccountId == _current.OwnerAccountId, _strings)).ToImmutableArray();

    private void RebuildConversations() => _conversations = SafeConversations(_current.Conversations)
        .Select(conversation => new ChatConversationRow(conversation, conversation.FriendAccountId == _current.SelectedFriendAccountId, _strings))
        .ToImmutableArray();

    private static ImmutableArray<ChatMessageUiItem> SafeMessages(ImmutableArray<ChatMessageUiItem> messages) => messages.IsDefault ? [] : messages;
    private static ImmutableArray<ChatConversationUiItem> SafeConversations(ImmutableArray<ChatConversationUiItem> conversations) => conversations.IsDefault ? [] : conversations;
    private static string NormalizeBody(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    private sealed record PendingSend(uint AccountId, Guid SessionId, Guid ClientMessageId, string Body, string DraftSnapshot);
}

public sealed record ChatStrings(bool IsEnglish)
{
    public CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "fr-FR");
    public string Title => IsEnglish ? "Messages" : "Messages";
    public string Subtitle => IsEnglish ? "Your Atlas conversations" : "Vos conversations Atlas";
    public string Conversations => IsEnglish ? "CONVERSATIONS" : "CONVERSATIONS";
    public string Back => IsEnglish ? "Back" : "Retour";
    public string Send => IsEnglish ? "Send" : "Envoyer";
    public string Sending => IsEnglish ? "Sending…" : "Envoi…";
    public string Loading => IsEnglish ? "Loading…" : "Chargement…";
    public string LoadEarlier => IsEnglish ? "Load earlier messages" : "Charger les messages précédents";
    public string NewConversation => IsEnglish ? "Start the conversation" : "Commencer la conversation";
    public string NoConversations => IsEnglish ? "No conversations yet" : "Aucune conversation pour le moment";
    public string NoConversationsHint => IsEnglish ? "Choose Send a message from a friend's menu." : "Choisissez Envoyer un message dans le menu d’un ami.";
    public string ChooseConversation => IsEnglish ? "Choose a conversation" : "Choisissez une conversation";
    public string ChooseConversationHint => IsEnglish ? "Your messages will appear here." : "Vos messages s’afficheront ici.";
    public string NoMessages => IsEnglish ? "No messages yet" : "Aucun message pour le moment";
    public string NoMessagesHint => IsEnglish ? "Write the first message to this friend." : "Écrivez le premier message à cet ami.";
    public string ComposerPlaceholder => IsEnglish ? "Write a message…" : "Écrire un message…";
    public string ComposerName => IsEnglish ? "Message text" : "Texte du message";
    public string ComposerHint => IsEnglish ? "Enter to send · Shift+Enter for a new line" : "Entrée pour envoyer · Maj+Entrée pour une nouvelle ligne";
    public string Unavailable => IsEnglish ? "Messaging is unavailable for now. Your draft is kept." : "La messagerie est indisponible pour le moment. Votre brouillon est conservé.";
    public string SendFailed => IsEnglish ? "The message could not be sent. Your draft is kept." : "Le message n’a pas pu être envoyé. Votre brouillon est conservé.";
    public string UnreadMessages => IsEnglish ? "unread messages" : "messages non lus";
    public string InGame => IsEnglish ? "In game" : "En jeu";
    public string OtherOrigin => IsEnglish ? "Other source" : "Autre origine";
    public string AtlasAccount => IsEnglish ? "Atlas account" : "Compte Atlas";
    public string ErrorForCode(string? code) => code switch
    {
        null or "" => string.Empty,
        "chat-not-friends" => IsEnglish ? "This account is no longer in your friends list." : "Ce compte ne fait plus partie de vos amis.",
        "chat-rate-limited" => IsEnglish ? "Please wait a minute before sending another message. Your draft is kept." : "Patientez une minute avant d’envoyer un autre message. Votre brouillon est conservé.",
        "chat-invalid-message" or "chat-invalid-request" => IsEnglish ? "Check the message text (1 to 1,000 characters). Your draft is kept." : "Vérifiez le texte du message (1 à 1 000 caractères). Votre brouillon est conservé.",
        "chat-unavailable" or "chat-unauthorized" => Unavailable,
        _ => SendFailed
    };
    public string FormatDate(DateTimeOffset date) => date.ToLocalTime().ToString(IsEnglish ? "MMM d · HH:mm" : "dd MMM · HH:mm", Culture);
    internal static string Initial(string? username) => string.IsNullOrWhiteSpace(username)
        ? "?" : StringInfo.GetNextTextElement(username.Trim()).ToUpperInvariant();
}
