using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;

internal static class ChatNotificationTests
{
    internal static int Run()
    {
        try
        {
            Guid session = Guid.NewGuid();
            ChatWorkspaceSnapshot snapshot = Build(session, 42, 100);
            Sink sink = new();
            Queue<Action> dispatch = new();
            string? visible = null;
            using LauncherChatNotificationCoordinator coordinator = new(() => snapshot, sink,
                thread => thread == visible, dispatch.Enqueue, _ => { });
            coordinator.Observe(snapshot);
            Drain();
            Check(0, "Initial history must not trigger alerts.");
            Observe(Build(session, 42, 101)); Drain();
            Check(1, "New incoming message notifies once.");
            if (sink.Notices[0].Sound) throw new InvalidOperationException("Message sounds remain disabled.");
            Observe(Build(session, 42, 101)); Drain();
            Check(1, "Repeat snapshots and same-message updates must not repeat alerts.");
            Observe(Build(session, 42, 102, dnd: true)); Drain();
            Check(1, "DND suppresses messages.");
            Observe(Build(session, 42, 102)); Drain();
            Check(1, "Turning off DND must not replay suppressed messages.");
            visible = "17";
            Observe(Build(session, 42, 103)); Drain();
            Check(1, "Currently visible thread must not alert.");
            visible = null;
            Observe(Build(session, 42, 104, sender: 42)); Drain();
            Check(1, "Own messages never alert.");
            Observe(Build(session, 42, 105));
            snapshot = Build(session, 42, 105, dnd: true);
            Drain(); Check(1, "DND set before UI dispatch cancels a queued alert.");
            Observe(Build(session, 42, 106));
            snapshot = Build(Guid.NewGuid(), 77, 110);
            Drain(); Check(1, "An account switch cancels a queued alert.");
            coordinator.Observe(snapshot); Drain();
            Check(1, "New account gets a fresh baseline.");
            Observe(Build(snapshot.SessionId, 77, 111)); Drain();
            Check(2, "New account starts receiving its own alerts.");
            Observe(Build(snapshot.SessionId, 77, 112));
            snapshot = snapshot with { State = snapshot.State with { Threads = [] } };
            Drain(); Check(2, "Access revocation cancels a queued alert.");
            Console.WriteLine("Chat notification policy OK: 13 checks; baseline, deduplication, DND, active thread, sound off, account switch and access revocation.");
            return 0;

            void Observe(ChatWorkspaceSnapshot next) { snapshot = next; coordinator.Observe(snapshot); }
            void Drain() { while (dispatch.TryDequeue(out Action? action)) action(); }
            void Check(int count, string message) { if (sink.Notices.Count != count) throw new InvalidOperationException(message); }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static ChatWorkspaceSnapshot Build(Guid session, uint owner, long id, bool dnd = false, uint sender = 91) => new()
    {
        SessionId = session, OwnerAccountId = owner, IsAvailable = true, State = new()
        {
            Preferences = new() { DoNotDisturb = dnd }, Threads = [new()
            {
                Id = "17", UnreadCount = 1, LastReadMessageId = id - 1, LastMessage = new()
                { Id = id, ThreadId = "17", Sender = new() { AccountId = sender, Username = "Lyra" }, Body = "À Dalaran !" }
            }]
        }
    };
    private sealed class Sink : ILauncherDesktopNotificationSink
    {
        internal readonly List<(string Title, string Message, bool Sound)> Notices = [];
        public void ShowNotification(string title, string message, bool playSound) => Notices.Add((title, message, playSound));
    }
}
