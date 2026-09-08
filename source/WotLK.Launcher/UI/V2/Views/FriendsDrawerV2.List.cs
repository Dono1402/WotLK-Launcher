using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public sealed record FriendDrawerRow(string Kind, uint AccountId = 0, FriendUiItem? Friend = null);

public partial class FriendsDrawerV2
{
    private readonly ObservableCollection<FriendDrawerRow> _visibleRows = [];
    private bool _rowsRefreshQueued;
    internal IReadOnlyList<FriendDrawerRow> VisibleRows => _visibleRows;
    internal ItemsControl ListHost => FriendsList;

    private void FriendsList_ScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (args.VerticalChange != 0 || args.HorizontalChange != 0) CloseFriendActionsPopup();
    }

    private static void FriendsStateChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        FriendsDrawerV2 drawer = (FriendsDrawerV2)value;
        if (args.OldValue is FriendsUiState previous)
            PropertyChangedEventManager.RemoveHandler(previous, drawer.ListStateChanged, string.Empty);
        if (args.NewValue is FriendsUiState current)
            PropertyChangedEventManager.AddHandler(current, drawer.ListStateChanged, string.Empty);
        drawer.RefreshVisibleRows();
    }

    private void ListStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_rowsRefreshQueued || Dispatcher.HasShutdownStarted) return;
        _rowsRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _rowsRefreshQueued = false;
            RefreshVisibleRows();
        }));
    }

    private void RefreshVisibleRows()
    {
        List<FriendDrawerRow> next = [];
        if (State is { } state)
        {
            AddGroup("incoming", state.Current.IncomingRequests, true);
            AddGroup("outgoing", state.Current.OutgoingRequests, true);
            AddGroup("online", state.FilteredOnlineFriends, state.IsOnlineGroupExpanded);
            AddGroup("offline", state.FilteredOfflineFriends, state.IsOfflineGroupExpanded);
        }
        // Keep one collection and reuse row identities: updates do not reset the scroll viewer.
        for (int index = 0; index < next.Count; index++)
        {
            FriendDrawerRow row = next[index];
            if (index >= _visibleRows.Count) _visibleRows.Add(row);
            else if (_visibleRows[index].Kind == row.Kind && _visibleRows[index].AccountId == row.AccountId)
            {
                if (_visibleRows[index] != row) _visibleRows[index] = row;
            }
            else
            {
                int existing = -1;
                for (int candidate = index + 1; candidate < _visibleRows.Count; candidate++)
                    if (_visibleRows[candidate].Kind == row.Kind && _visibleRows[candidate].AccountId == row.AccountId)
                    { existing = candidate; break; }
                if (existing >= 0) { _visibleRows.Move(existing, index); if (_visibleRows[index] != row) _visibleRows[index] = row; }
                else _visibleRows.Insert(index, row);
            }
        }
        while (_visibleRows.Count > next.Count) _visibleRows.RemoveAt(_visibleRows.Count - 1);

        void AddGroup(string kind, IReadOnlyList<FriendUiItem> friends, bool expanded)
        {
            if (friends.Count == 0) return;
            next.Add(new(kind + "-header"));
            if (expanded)
                foreach (FriendUiItem friend in friends) next.Add(new(kind, friend.AccountId, friend));
        }
    }
}
