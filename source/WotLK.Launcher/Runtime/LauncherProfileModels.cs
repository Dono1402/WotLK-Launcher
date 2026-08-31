namespace WotLK.Launcher.Runtime;

internal enum ProfileLogoutStartStatus
{
    Started,
    Busy,
    ShuttingDown,
    NotAuthenticated,
    RejectedByCompatibility
}

internal sealed record ProfileRuntimeSnapshot(
    long Sequence,
    long SessionSequence,
    long? LogoutAttemptId,
    LauncherSessionState SessionState,
    string Username,
    bool IsEmailVerified,
    bool CanLogout,
    string LogoutUnavailableReason,
    LauncherSessionFailureCategory FailureCategory)
{
    internal bool IsAuthenticated => SessionState is LauncherSessionState.Authenticated
        or LauncherSessionState.LoggingOut;

    internal bool IsLoggingOut => SessionState == LauncherSessionState.LoggingOut;

    internal string DisplayInitial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();
}

internal sealed class ProfileRuntimeSnapshotEventArgs : EventArgs
{
    internal ProfileRuntimeSnapshotEventArgs(ProfileRuntimeSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    internal ProfileRuntimeSnapshot Snapshot { get; }
}

internal sealed record ProfileLogoutStartResult(
    ProfileLogoutStartStatus Status,
    long? AttemptId,
    Task<LauncherSessionCompletion>? Completion)
{
    internal bool IsStarted => Status == ProfileLogoutStartStatus.Started
        && AttemptId is not null
        && Completion is not null;
}

internal interface ILauncherProfileRuntime
{
    event EventHandler<ProfileRuntimeSnapshotEventArgs>? SnapshotChanged;

    ProfileRuntimeSnapshot CurrentSnapshot { get; }

    ProfileLogoutStartResult TryLogout();
}
