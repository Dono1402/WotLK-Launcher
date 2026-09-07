using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;

namespace WotLK.Launcher.UI.V2;

internal sealed class LauncherChatNotificationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly LauncherChatWorkspace? _workspace;
    private readonly Func<ChatWorkspaceSnapshot> _current;
    private readonly ILauncherDesktopNotificationSink _sink;
    private readonly Func<string, bool> _isThreadVisible;
    private readonly Action<Action> _dispatch;
    private readonly Action<string> _log;
    private readonly Func<bool> _isGlobalDoNotDisturb;
    private readonly Dictionary<string, long> _lastIds = new(StringComparer.Ordinal);
    private Guid _session;
    private uint _owner;
    private bool _baseline;
    private int _disposed;

    internal LauncherChatNotificationCoordinator(LauncherChatWorkspace workspace,
        ILauncherDesktopNotificationSink sink, Func<string, bool> isThreadVisible,
        Action<Action> dispatch, Action<string> log, Func<bool>? isGlobalDoNotDisturb = null)
        : this(() => workspace.CurrentSnapshot, sink, isThreadVisible, dispatch, log, isGlobalDoNotDisturb)
    {
        _workspace = workspace;
        workspace.SnapshotChanged += Changed;
        Observe(workspace.CurrentSnapshot);
    }

    internal LauncherChatNotificationCoordinator(Func<ChatWorkspaceSnapshot> current,
        ILauncherDesktopNotificationSink sink, Func<string, bool> isThreadVisible,
        Action<Action> dispatch, Action<string> log, Func<bool>? isGlobalDoNotDisturb = null)
    {
        _current = current; _sink = sink; _isThreadVisible = isThreadVisible; _dispatch = dispatch; _log = log;
        _isGlobalDoNotDisturb = isGlobalDoNotDisturb ?? (static () => false);
    }

    private void Changed(object? sender, ChatWorkspaceSnapshotEventArgs args) => Observe(args.Snapshot);

    internal void Observe(ChatWorkspaceSnapshot snapshot)
    {
        List<(ChatThreadDto Thread, ChatMessageDto Message)> incoming = [];
        lock (_sync)
        {
            if (_disposed != 0) return;
            if (_session != snapshot.SessionId || _owner != snapshot.OwnerAccountId)
            { _lastIds.Clear(); _baseline = false; _session = snapshot.SessionId; _owner = snapshot.OwnerAccountId; }
            if (snapshot.OwnerAccountId == 0 || snapshot.IsLegacyFallback || !snapshot.IsAvailable) return;
            foreach (ChatThreadDto thread in snapshot.State.Threads)
            {
                if (thread.LastMessage is not { } message) continue;
                _lastIds.TryGetValue(thread.Id, out long previous);
                _lastIds[thread.Id] = Math.Max(previous, message.Id);
                if (_baseline && message.Id > previous && message.Sender.AccountId != snapshot.OwnerAccountId
                    && message.DeletedAt is null && thread.UnreadCount > 0 && message.Id > thread.LastReadMessageId)
                    incoming.Add((thread, message));
            }
            _baseline = true;
        }
        if (snapshot.State.Preferences.DoNotDisturb || _isGlobalDoNotDisturb() || incoming.Count == 0) return;
        // Check visibility and preferences on the UI thread again: a queued alert may
        // outlive a navigation, a DND click, or an account change.
        _dispatch(() =>
        {
            ChatWorkspaceSnapshot current = _current();
            if (Volatile.Read(ref _disposed) != 0 || current.SessionId != snapshot.SessionId
                || current.OwnerAccountId != snapshot.OwnerAccountId || current.State.Preferences.DoNotDisturb
                || _isGlobalDoNotDisturb()) return;
            var visible = incoming.Where(item => !_isThreadVisible(item.Thread.Id)
                && current.State.Threads.Any(thread => thread.Id == item.Thread.Id && thread.LastReadMessageId < item.Message.Id)).ToArray();
            if (visible.Length == 0) return;
            bool english = LauncherLocalization.IsEnglish;
            string title = visible.Length == 1 ? visible[0].Message.Sender.Username : english ? "New messages" : "Nouveaux messages";
            string body = visible.Length > 1 ? english ? "New messages in your conversations." : "De nouveaux messages dans vos conversations."
                : Preview(visible[0].Message, english);
            try { _sink.ShowNotification(title, body, playSound: false); }
            catch (Exception error) { try { _log("Chat notification unavailable: " + error.GetType().Name); } catch { } }
        });
    }

    private static string Preview(ChatMessageDto message, bool english)
    {
        string body = message.Body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (body.Length == 0) body = message.Attachments.Count != 0 ? english ? "Shared a file." : "A partagé un fichier."
            : english ? "Shared an Atlas card." : "A partagé une carte Atlas.";
        return body.Length > 180 ? body[..177] + "…" : body;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_workspace is not null) _workspace.SnapshotChanged -= Changed;
        lock (_sync) _lastIds.Clear();
    }
}
