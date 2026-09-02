namespace WotLK.Launcher.Runtime;

internal enum LauncherSessionState
{
    Restoring,
    SignedOut,
    Authenticating,
    Registering,
    Enrolling,
    LoggingOut,
    Authenticated,
    Unavailable
}

internal enum LauncherSessionFailureCategory
{
    None,
    NoStoredSession,
    SessionExpired,
    InvalidCredentials,
    AtlasProfileRequired,
    EnrollmentNotAllowed,
    AlreadyEnrolled,
    Validation,
    UsernameAlreadyExists,
    EmailAlreadyExists,
    Unauthorized,
    Network,
    Timeout,
    ServiceUnavailable,
    ServerRejected,
    SecureStorage,
    AccountCreatedSignInRequired,
    Unknown
}

internal enum LauncherSessionOperationKind
{
    Restore,
    Login,
    Register,
    Enrollment,
    Logout
}

internal enum LauncherSessionStartStatus
{
    Started,
    Busy,
    ShuttingDown,
    RejectedByValidation,
    AlreadyAuthenticated,
    NotAuthenticated
}

internal enum LauncherSessionCompletionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Superseded
}

internal enum LauncherSessionRestoreStatus
{
    Restored,
    NoSession,
    Rejected,
    Unavailable,
    Cancelled
}

internal enum AtlasRequestPreparationStatus
{
    Ready,
    AuthenticationRequired,
    Unavailable,
    Cancelled,
    ShuttingDown
}

internal sealed record LauncherSessionRestoreResult(
    LauncherSessionRestoreStatus Status,
    LauncherAuthSession? Session);

internal sealed record AuthSessionSnapshot(
    long Sequence,
    long? AttemptId,
    LauncherSessionState State,
    LauncherSessionOperationKind? OperationKind,
    string Username,
    bool IsEmailVerified,
    LauncherSessionFailureCategory FailureCategory)
{
    internal static AuthSessionSnapshot Initial { get; } = new(
        Sequence: 0,
        AttemptId: null,
        State: LauncherSessionState.Restoring,
        OperationKind: LauncherSessionOperationKind.Restore,
        Username: string.Empty,
        IsEmailVerified: true,
        FailureCategory: LauncherSessionFailureCategory.None);

    internal bool IsAuthenticated => State == LauncherSessionState.Authenticated;

    internal bool IsRestoring => State == LauncherSessionState.Restoring;

    internal bool IsSubmitting => State is LauncherSessionState.Authenticating
        or LauncherSessionState.Registering
        or LauncherSessionState.Enrolling;

    internal bool IsLoggingOut => State == LauncherSessionState.LoggingOut;

    internal string DisplayInitial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();
}

internal sealed class AuthSessionSnapshotEventArgs : EventArgs
{
    internal AuthSessionSnapshotEventArgs(AuthSessionSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    internal AuthSessionSnapshot Snapshot { get; }
}

internal sealed record LauncherSessionCompletion(
    LauncherSessionCompletionStatus Status,
    AuthSessionSnapshot Snapshot);

internal sealed record LauncherSessionStartResult(
    LauncherSessionStartStatus Status,
    long? AttemptId,
    Task<LauncherSessionCompletion>? Completion)
{
    internal bool IsStarted => Status == LauncherSessionStartStatus.Started
        && AttemptId is not null
        && Completion is not null;

    internal static LauncherSessionStartResult Rejected(
        LauncherSessionStartStatus status,
        AuthSessionSnapshot snapshot)
    {
        return new LauncherSessionStartResult(
            status,
            null,
            Task.FromResult(new LauncherSessionCompletion(
                LauncherSessionCompletionStatus.Failed,
                snapshot)));
    }
}
