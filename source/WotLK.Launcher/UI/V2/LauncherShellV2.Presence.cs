using System.Windows;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private LauncherPresenceCoordinator? _presence;
    private long _presenceSequence=-1;
    internal void AttachPresence(LauncherPresenceCoordinator presence)
    {
        if(_presence is not null)throw new InvalidOperationException("Presence is already attached.");
        _presence=presence;
        presence.SnapshotChanged+=PresenceChanged;
        ProfileMenu.PresenceRequested+=PresenceRequested;
        Closed+=DetachPresence;
        ApplyPresenceSnapshot(presence.CurrentSnapshot);
        presence.Start();
    }
    private void PresenceChanged(object? sender,LauncherPresenceSnapshotEventArgs args)
    {
        if(Dispatcher.CheckAccess())ApplyPresenceSnapshot(args.Snapshot);
        else if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(()=>ApplyPresenceSnapshot(args.Snapshot)));
    }
    private void ApplyPresenceSnapshot(LauncherPresenceSnapshot snapshot)
    {
        if(_presence is null || snapshot.Sequence<_presenceSequence || snapshot.Sequence<_presence.CurrentSnapshot.Sequence)return;
        _presenceSequence=snapshot.Sequence;ProfileState.ApplyPresence(snapshot);
    }
    private async void PresenceRequested(object? sender,string status)
    {
        if(_presence is not null)await _presence.SetStatusAsync(status);
    }
    private void DetachPresence(object? sender,EventArgs args)
    {
        if(_presence is not null)_presence.SnapshotChanged-=PresenceChanged;
        ProfileMenu.PresenceRequested-=PresenceRequested;Closed-=DetachPresence;_presence=null;
    }
}
