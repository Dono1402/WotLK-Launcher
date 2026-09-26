using System.Threading.Channels;

namespace WotLK.Launcher.Server;

// A bounded queue gives the same immediate response for existing and unknown accounts.
// No account lookup or mail-provider timing is exposed by the public request endpoint.
internal sealed class PasswordRecoveryService(
    LauncherDatabase database, BrevoEmailClient email, LauncherServerOptions options,
    ILogger<PasswordRecoveryService> logger) : BackgroundService
{
    private readonly Channel<string> _requests = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    internal bool TryQueue(string address) => database.PasswordRecoveryAvailable && email.IsConfigured
        && !options.BrevoSandbox && Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && _requests.Writer.TryWrite(address.Trim());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (string address in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            EmailVerificationChallenge? challenge = null;
            try
            {
                challenge = await database.CreatePasswordResetAsync(address, stoppingToken);
                if (challenge is not null) await email.SendPasswordResetAsync(challenge, stoppingToken);
            }
            catch (Exception exception)
            {
                // Do not log the address, URL, token or provider response.
                logger.LogWarning("Password recovery dispatch failed ({ExceptionType}).", exception.GetType().Name);
                if (challenge is not null)
                {
                    try
                    {
                        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
                        await database.CancelPasswordResetAsync(challenge.TokenHash, cleanup.Token);
                    }
                    catch { logger.LogWarning("Could not invalidate an unsent password recovery challenge."); }
                }
                if (stoppingToken.IsCancellationRequested) break;
            }
        }
    }
}
