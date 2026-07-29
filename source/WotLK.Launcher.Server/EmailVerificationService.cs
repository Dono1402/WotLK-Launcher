namespace WotLK.Launcher.Server;

public enum EmailVerificationDispatchStatus
{
    Sent,
    AlreadyVerified,
    Cooldown,
    NotConfigured,
    Failed
}

public sealed record EmailVerificationDispatchResult(
    EmailVerificationDispatchStatus Status,
    int RetryAfterSeconds = 0);

public sealed class EmailVerificationService
{
    private readonly LauncherDatabase _database;
    private readonly BrevoEmailClient _brevo;
    private readonly LauncherServerOptions _options;
    private readonly ILogger<EmailVerificationService> _logger;

    public EmailVerificationService(
        LauncherDatabase database,
        BrevoEmailClient brevo,
        LauncherServerOptions options,
        ILogger<EmailVerificationService> logger)
    {
        _database = database;
        _brevo = brevo;
        _options = options;
        _logger = logger;
    }

    public async Task<EmailVerificationDispatchResult> SendAsync(
        uint accountId,
        CancellationToken cancellationToken)
    {
        if (!_brevo.IsConfigured)
            return new(EmailVerificationDispatchStatus.NotConfigured);

        EmailVerificationChallenge? challenge;
        try
        {
            challenge = await _database.CreateEmailVerificationAsync(
                accountId,
                _options.EmailVerificationExpiryHours,
                _options.EmailVerificationCooldownSeconds,
                cancellationToken);
        }
        catch (EmailVerificationCooldownException ex)
        {
            return new(
                EmailVerificationDispatchStatus.Cooldown,
                ex.RetryAfterSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Le jeton de validation du compte {AccountId} n'a pas pu être créé.",
                accountId);
            return new(EmailVerificationDispatchStatus.Failed);
        }

        if (challenge is null)
            return new(EmailVerificationDispatchStatus.AlreadyVerified);

        try
        {
            await _brevo.SendVerificationAsync(challenge, cancellationToken);
            if (_options.BrevoSandbox)
            {
                await CancelChallengeAsync(challenge);
                _logger.LogInformation(
                    "Test Brevo sandbox accepté pour le compte {AccountId}.",
                    accountId);
            }

            return new(EmailVerificationDispatchStatus.Sent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelChallengeAsync(challenge);
            throw;
        }
        catch (Exception ex)
        {
            await CancelChallengeAsync(challenge);
            _logger.LogWarning(
                ex,
                "Brevo n'a pas pu envoyer l'e-mail de validation du compte {AccountId}.",
                accountId);
            return new(EmailVerificationDispatchStatus.Failed);
        }
    }

    private async Task CancelChallengeAsync(EmailVerificationChallenge challenge)
    {
        try
        {
            await _database.CancelEmailVerificationAsync(
                challenge.AccountId,
                challenge.TokenHash,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Le jeton de validation non envoyé du compte {AccountId} n'a pas pu être supprimé.",
                challenge.AccountId);
        }
    }
}
