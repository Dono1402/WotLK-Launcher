using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace WotLK.Launcher.Game;

internal sealed class GameLaunchService : IGameLaunchService
{
    private readonly IGameLaunchSession _session;
    private readonly IGameLaunchPlatform _platform;
    private readonly IGameProcessStarter _processStarter;

    internal GameLaunchService(
        IGameLaunchSession session,
        IGameLaunchPlatform platform,
        IGameProcessStarter processStarter)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
    }

    public async Task<GameLaunchResult> LaunchAsync(
        GameLaunchRequest request,
        Action<GameLaunchProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        GameLaunchPhase phase = GameLaunchPhase.Idle;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string installRoot = Path.GetFullPath(request.InstallPath);
            string launcherPath = GamePathPolicy.GetSafeTargetPath(
                installRoot,
                GameInstallServices.GameLauncherFileName);
            string classicPath = GamePathPolicy.GetSafeTargetPath(
                installRoot,
                GameInstallServices.ClassicDirectoryName);

            if (!_platform.HasPlayableClient(installRoot)
                || !_platform.FileExists(launcherPath))
            {
                return Failure(
                    request.AttemptId,
                    GameLaunchOutcome.ExecutableMissing,
                    GameLaunchFailureCategory.ExecutableMissing);
            }

            if (_platform.IsGameRunning(installRoot))
            {
                return new GameLaunchResult(
                    request.AttemptId,
                    GameLaunchOutcome.AlreadyRunning);
            }

            _platform.EnsureDefaultClientConfig(installRoot, request.GameLocale);
            cancellationToken.ThrowIfCancellationRequested();

            phase = GameLaunchPhase.RequestingTicket;
            reportProgress?.Invoke(new GameLaunchProgress(request.AttemptId, phase));
            GameTicketAcquisitionResult acquisition = await _session
                .AcquireGameTicketAsync(cancellationToken)
                .ConfigureAwait(false);
            if (acquisition.Status != GameTicketAcquisitionStatus.Succeeded
                || acquisition.Ticket is null)
            {
                return FromTicketFailure(request.AttemptId, acquisition);
            }

            cancellationToken.ThrowIfCancellationRequested();
            phase = GameLaunchPhase.PreparingSso;
            reportProgress?.Invoke(new GameLaunchProgress(request.AttemptId, phase));
            _platform.WriteSingleSignOn(acquisition.Ticket, request.GameLocale);

            cancellationToken.ThrowIfCancellationRequested();
            phase = GameLaunchPhase.StartingProcess;
            reportProgress?.Invoke(new GameLaunchProgress(request.AttemptId, phase));
            ProcessStartInfo startInfo = CreateStartInfo(
                launcherPath,
                installRoot,
                classicPath);
            if (!_processStarter.Start(startInfo))
            {
                return Failure(
                    request.AttemptId,
                    GameLaunchOutcome.StartFailed,
                    GameLaunchFailureCategory.Process);
            }

            return new GameLaunchResult(
                request.AttemptId,
                GameLaunchOutcome.Started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                request.AttemptId,
                GameLaunchOutcome.Cancelled,
                GameLaunchFailureCategory.Cancelled);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(
                request.AttemptId,
                GameLaunchOutcome.AccessDenied,
                GameLaunchFailureCategory.AccessDenied,
                exception);
        }
        catch (Win32Exception exception)
        {
            GameLaunchOutcome outcome = exception.NativeErrorCode is 2 or 3
                ? GameLaunchOutcome.ExecutableMissing
                : exception.NativeErrorCode == 5
                    ? GameLaunchOutcome.AccessDenied
                    : GameLaunchOutcome.StartFailed;
            GameLaunchFailureCategory category = outcome switch
            {
                GameLaunchOutcome.ExecutableMissing => GameLaunchFailureCategory.ExecutableMissing,
                GameLaunchOutcome.AccessDenied => GameLaunchFailureCategory.AccessDenied,
                _ => GameLaunchFailureCategory.Process
            };
            return Failure(request.AttemptId, outcome, category, exception);
        }
        catch (Exception exception) when (phase == GameLaunchPhase.PreparingSso)
        {
            return Failure(
                request.AttemptId,
                GameLaunchOutcome.SsoFailed,
                GameLaunchFailureCategory.Sso,
                exception);
        }
        catch (Exception exception) when (phase == GameLaunchPhase.StartingProcess)
        {
            return Failure(
                request.AttemptId,
                GameLaunchOutcome.StartFailed,
                GameLaunchFailureCategory.Process,
                exception);
        }
        catch (Exception exception)
        {
            return Failure(
                request.AttemptId,
                GameLaunchOutcome.Unknown,
                GameLaunchFailureCategory.Unknown,
                exception);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string launcherPath,
        string installRoot,
        string classicPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = launcherPath,
            WorkingDirectory = installRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add("Classic");
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(classicPath);
        startInfo.ArgumentList.Add("--portal");
        startInfo.ArgumentList.Add(GameInstallServices.PortalAddress);
        startInfo.ArgumentList.Add("--skipcertcheck");
        startInfo.ArgumentList.Add("-launcherlogin");
        startInfo.ArgumentList.Add("-uid");
        startInfo.ArgumentList.Add("wow_classic");
        return startInfo;
    }

    private static GameLaunchResult FromTicketFailure(
        long attemptId,
        GameTicketAcquisitionResult acquisition)
    {
        return acquisition.Status switch
        {
            GameTicketAcquisitionStatus.AuthenticationRequired => Failure(
                attemptId,
                GameLaunchOutcome.AuthenticationRequired,
                GameLaunchFailureCategory.AuthenticationRequired,
                acquisition.Failure),
            GameTicketAcquisitionStatus.NetworkUnavailable => Failure(
                attemptId,
                GameLaunchOutcome.NetworkUnavailable,
                acquisition.Failure is TaskCanceledException or TimeoutException
                    ? GameLaunchFailureCategory.Timeout
                    : GameLaunchFailureCategory.Network,
                acquisition.Failure),
            GameTicketAcquisitionStatus.ServiceUnavailable => Failure(
                attemptId,
                GameLaunchOutcome.ServiceUnavailable,
                GameLaunchFailureCategory.ServiceUnavailable,
                acquisition.Failure),
            GameTicketAcquisitionStatus.TicketRejected => Failure(
                attemptId,
                GameLaunchOutcome.TicketFailed,
                GameLaunchFailureCategory.TicketRejected,
                acquisition.Failure),
            GameTicketAcquisitionStatus.Cancelled => Failure(
                attemptId,
                GameLaunchOutcome.Cancelled,
                GameLaunchFailureCategory.Cancelled,
                acquisition.Failure),
            _ => Failure(
                attemptId,
                GameLaunchOutcome.Unknown,
                GameLaunchFailureCategory.Unknown,
                acquisition.Failure)
        };
    }

    private static GameLaunchResult Failure(
        long attemptId,
        GameLaunchOutcome outcome,
        GameLaunchFailureCategory category,
        Exception? exception = null)
    {
        return new GameLaunchResult(attemptId, outcome, category, exception);
    }
}

internal sealed class ProductionGameLaunchPlatform : IGameLaunchPlatform
{
    public bool HasPlayableClient(string installRoot) =>
        GameInstallServices.HasPlayableClient(installRoot);

    public bool IsGameRunning(string installRoot) =>
        GameInstallServices.IsGameRunning(installRoot);

    public bool FileExists(string path) => File.Exists(path);

    public string EnsureDefaultClientConfig(string installRoot, string locale) =>
        GameInstallServices.EnsureDefaultClientConfig(installRoot, locale);

    public void WriteSingleSignOn(GameTicket ticket, string locale) =>
        GameSingleSignOn.Write(ticket, locale);
}

internal sealed class ProductionGameProcessStarter : IGameProcessStarter
{
    public bool Start(ProcessStartInfo startInfo) => Process.Start(startInfo) is not null;
}

internal sealed class LegacyGameLaunchSession : IGameLaunchSession
{
    private readonly ILauncherAuthService _authentication;

    internal LegacyGameLaunchSession(ILauncherAuthService authentication)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    public async Task<GameTicketAcquisitionResult> AcquireGameTicketAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _authentication.EnsureFreshAsync(cancellationToken).ConfigureAwait(false))
            {
                return new GameTicketAcquisitionResult(
                    GameTicketAcquisitionStatus.AuthenticationRequired);
            }

            GameTicket ticket = await _authentication
                .CreateGameTicketAsync(cancellationToken)
                .ConfigureAwait(false);
            return GameTicketAcquisitionResult.Success(ticket);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            return new GameTicketAcquisitionResult(
                GameTicketAcquisitionStatus.Cancelled,
                Failure: exception);
        }
        catch (TaskCanceledException exception)
        {
            return new GameTicketAcquisitionResult(
                GameTicketAcquisitionStatus.NetworkUnavailable,
                Failure: exception);
        }
        catch (HttpRequestException exception)
        {
            return new GameTicketAcquisitionResult(
                exception.StatusCode is >= HttpStatusCode.InternalServerError
                    ? GameTicketAcquisitionStatus.ServiceUnavailable
                    : GameTicketAcquisitionStatus.NetworkUnavailable,
                Failure: exception);
        }
        catch (LauncherAuthException exception)
        {
            return new GameTicketAcquisitionResult(
                exception.StatusCode == HttpStatusCode.Unauthorized
                    ? GameTicketAcquisitionStatus.AuthenticationRequired
                    : exception.StatusCode is >= HttpStatusCode.InternalServerError
                        ? GameTicketAcquisitionStatus.ServiceUnavailable
                        : GameTicketAcquisitionStatus.TicketRejected,
                Failure: exception);
        }
        catch (CryptographicException exception)
        {
            return new GameTicketAcquisitionResult(
                GameTicketAcquisitionStatus.TicketRejected,
                Failure: exception);
        }
        catch (Exception exception)
        {
            return new GameTicketAcquisitionResult(
                GameTicketAcquisitionStatus.Unknown,
                Failure: exception);
        }
    }
}

internal sealed class DelegateGameLaunchPlatform : IGameLaunchPlatform
{
    private readonly Func<string, bool> _hasPlayableClient;
    private readonly Func<string, bool> _isGameRunning;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string, string> _ensureDefaultClientConfig;
    private readonly Action<GameTicket, string> _writeSingleSignOn;

    internal DelegateGameLaunchPlatform(
        Func<string, bool> hasPlayableClient,
        Func<string, bool> isGameRunning,
        Func<string, bool> fileExists,
        Func<string, string, string> ensureDefaultClientConfig,
        Action<GameTicket, string> writeSingleSignOn)
    {
        _hasPlayableClient = hasPlayableClient;
        _isGameRunning = isGameRunning;
        _fileExists = fileExists;
        _ensureDefaultClientConfig = ensureDefaultClientConfig;
        _writeSingleSignOn = writeSingleSignOn;
    }

    public bool HasPlayableClient(string installRoot) => _hasPlayableClient(installRoot);

    public bool IsGameRunning(string installRoot) => _isGameRunning(installRoot);

    public bool FileExists(string path) => _fileExists(path);

    public string EnsureDefaultClientConfig(string installRoot, string locale) =>
        _ensureDefaultClientConfig(installRoot, locale);

    public void WriteSingleSignOn(GameTicket ticket, string locale) =>
        _writeSingleSignOn(ticket, locale);
}

internal sealed class DelegateGameProcessStarter : IGameProcessStarter
{
    private readonly Func<ProcessStartInfo, Process?> _start;

    internal DelegateGameProcessStarter(Func<ProcessStartInfo, Process?> start)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public bool Start(ProcessStartInfo startInfo) => _start(startInfo) is not null;
}
