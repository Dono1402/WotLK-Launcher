using System.Diagnostics;

namespace WotLK.Launcher.Game;

internal enum GameLaunchPhase
{
    Idle,
    WaitingForAuthentication,
    RequestingTicket,
    PreparingSso,
    StartingProcess,
    Started,
    Failed
}

internal enum GameLaunchOutcome
{
    Started,
    AlreadyRunning,
    AuthenticationRequired,
    NetworkUnavailable,
    ServiceUnavailable,
    TicketFailed,
    ExecutableMissing,
    AccessDenied,
    SsoFailed,
    StartFailed,
    Cancelled,
    Unknown
}

internal enum GameLaunchFailureCategory
{
    AuthenticationRequired,
    Network,
    Timeout,
    ServiceUnavailable,
    TicketRejected,
    ExecutableMissing,
    AccessDenied,
    Sso,
    Process,
    Cancelled,
    Unknown
}

internal enum GameTicketAcquisitionStatus
{
    Succeeded,
    AuthenticationRequired,
    NetworkUnavailable,
    ServiceUnavailable,
    TicketRejected,
    Cancelled,
    Unknown
}

internal sealed record GameTicketAcquisitionResult(
    GameTicketAcquisitionStatus Status,
    GameTicket? Ticket = null,
    Exception? Failure = null)
{
    internal static GameTicketAcquisitionResult Success(GameTicket ticket)
    {
        return new GameTicketAcquisitionResult(
            GameTicketAcquisitionStatus.Succeeded,
            ticket ?? throw new ArgumentNullException(nameof(ticket)));
    }
}

internal sealed record GameLaunchRequest(
    long AttemptId,
    string InstallPath,
    string GameLocale);

internal sealed record GameLaunchProgress(
    long AttemptId,
    GameLaunchPhase Phase);

internal sealed record GameLaunchResult(
    long AttemptId,
    GameLaunchOutcome Outcome,
    GameLaunchFailureCategory? FailureCategory = null,
    Exception? Failure = null)
{
    internal bool IsStarted => Outcome == GameLaunchOutcome.Started;
}

internal interface IGameLaunchSession
{
    Task<GameTicketAcquisitionResult> AcquireGameTicketAsync(
        CancellationToken cancellationToken);
}

internal interface IGameLaunchPlatform
{
    bool HasPlayableClient(string installRoot);

    bool IsGameRunning(string installRoot);

    bool FileExists(string path);

    string EnsureDefaultClientConfig(string installRoot, string locale);

    void WriteSingleSignOn(GameTicket ticket, string locale);
}

internal interface IGameProcessStarter
{
    bool Start(ProcessStartInfo startInfo);
}

internal interface IGameLaunchService
{
    Task<GameLaunchResult> LaunchAsync(
        GameLaunchRequest request,
        Action<GameLaunchProgress>? reportProgress,
        CancellationToken cancellationToken);
}
