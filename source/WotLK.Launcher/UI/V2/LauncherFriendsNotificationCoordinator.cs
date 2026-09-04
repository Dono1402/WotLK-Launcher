using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;

namespace WotLK.Launcher.UI.V2;

internal interface ILauncherDesktopNotificationSink
{
    void ShowNotification(string title, string message, bool playSound);
}

internal sealed class LauncherFriendsNotificationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly LauncherFriendsCoordinator? _friends;
    private readonly ILauncherSettingsRuntime _settings;
    private readonly ILauncherDesktopNotificationSink _notifications;
    private readonly Action<string> _writeLog;
    private Dictionary<uint, bool> _friendPresence = [];
    private HashSet<uint> _incomingRequestIds = [];
    private uint? _currentUserId;
    private bool _hasBaseline;
    private int _disposeState;

    internal LauncherFriendsNotificationCoordinator(
        LauncherFriendsCoordinator friends,
        ILauncherSettingsRuntime settings,
        ILauncherDesktopNotificationSink notifications,
        Action<string> writeLog)
        : this(settings, notifications, writeLog)
    {
        _friends = friends ?? throw new ArgumentNullException(nameof(friends));
        _friends.SnapshotChanged += Friends_SnapshotChanged;
        Observe(_friends.CurrentSnapshot);
    }

    internal LauncherFriendsNotificationCoordinator(
        ILauncherSettingsRuntime settings,
        ILauncherDesktopNotificationSink notifications,
        Action<string> writeLog)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
    }

    internal void Observe(FriendsRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        NotificationMessage? presenceNotification = null;
        NotificationMessage? requestNotification = null;
        lock (_sync)
        {
            if (!snapshot.IsAuthenticated
                || snapshot.CurrentUserId is null
                || snapshot.LoadState == FriendsLoadState.SignedOut)
            {
                ResetBaselineUnsafe();
                return;
            }

            if (snapshot.LoadState != FriendsLoadState.Loaded
                || snapshot.OperationState != FriendsOperationState.None)
            {
                return;
            }

            Dictionary<uint, bool> currentPresence = snapshot.Friends
                .Where(friend => friend.Relationship == FriendRelationship.Accepted)
                .ToDictionary(friend => friend.AccountId, friend => friend.IsOnline);
            HashSet<uint> currentIncoming = snapshot.IncomingRequests
                .Where(request => request.Relationship == FriendRelationship.Incoming)
                .Select(request => request.AccountId)
                .ToHashSet();

            if (!_hasBaseline || _currentUserId != snapshot.CurrentUserId)
            {
                SetBaselineUnsafe(
                    snapshot.CurrentUserId.Value,
                    currentPresence,
                    currentIncoming);
                return;
            }

            FriendRuntimeItem[] newlyOnline = snapshot.Friends
                .Where(friend => friend.Relationship == FriendRelationship.Accepted
                    && friend.IsOnline
                    && _friendPresence.TryGetValue(friend.AccountId, out bool wasOnline)
                    && !wasOnline)
                .OrderBy(friend => friend.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            FriendRuntimeItem[] newRequests = snapshot.IncomingRequests
                .Where(request => request.Relationship == FriendRelationship.Incoming
                    && !_incomingRequestIds.Contains(request.AccountId))
                .OrderBy(request => request.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            SetBaselineUnsafe(
                snapshot.CurrentUserId.Value,
                currentPresence,
                currentIncoming);

            if (_settings.CurrentSnapshot.FriendPresenceNotifications
                && newlyOnline.Length > 0)
            {
                presenceNotification = newlyOnline.Length == 1
                    ? new NotificationMessage(
                        LauncherLocalization.Text("Ami connecté"),
                        $"{newlyOnline[0].Username} {LauncherLocalization.Text("est maintenant en ligne.")}",
                        PlaySound: true)
                    : new NotificationMessage(
                        LauncherLocalization.Text("Ami connecté"),
                        LauncherLocalization.Text("Plusieurs amis viennent de se connecter."),
                        PlaySound: true);
            }

            if (newRequests.Length > 0)
            {
                requestNotification = newRequests.Length == 1
                    ? new NotificationMessage(
                        LauncherLocalization.Text("Nouvelle demande d’ami"),
                        $"{newRequests[0].Username} {LauncherLocalization.Text("souhaite t’ajouter à ses amis.")}",
                        PlaySound: false)
                    : new NotificationMessage(
                        LauncherLocalization.Text("Nouvelle demande d’ami"),
                        LauncherLocalization.Text("Plusieurs nouvelles demandes d’amis sont arrivées."),
                        PlaySound: false);
            }
        }

        PublishSafely(presenceNotification);
        PublishSafely(requestNotification);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        if (_friends is not null)
        {
            _friends.SnapshotChanged -= Friends_SnapshotChanged;
        }

        lock (_sync)
        {
            ResetBaselineUnsafe();
        }
    }

    private void Friends_SnapshotChanged(object? sender, FriendsRuntimeSnapshotEventArgs e) =>
        Observe(e.Snapshot);

    private void SetBaselineUnsafe(
        uint currentUserId,
        Dictionary<uint, bool> friendPresence,
        HashSet<uint> incomingRequestIds)
    {
        _currentUserId = currentUserId;
        _friendPresence = friendPresence;
        _incomingRequestIds = incomingRequestIds;
        _hasBaseline = true;
    }

    private void ResetBaselineUnsafe()
    {
        _currentUserId = null;
        _friendPresence = [];
        _incomingRequestIds = [];
        _hasBaseline = false;
    }

    private void PublishSafely(NotificationMessage? notification)
    {
        if (notification is null || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        try
        {
            _notifications.ShowNotification(
                notification.Title,
                notification.Message,
                notification.PlaySound);
        }
        catch (Exception exception)
        {
            try
            {
                _writeLog(
                    "Notification d'amis V2 non affichée: category="
                    + exception.GetType().Name
                    + ".");
            }
            catch
            {
                // Notification failures never interrupt the launcher runtime.
            }
        }
    }

    private sealed record NotificationMessage(
        string Title,
        string Message,
        bool PlaySound);
}
