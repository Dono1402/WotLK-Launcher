using MySqlConnector;

namespace WotLK.Launcher.Server;

internal sealed class ChatGameInboxWorker(LauncherDatabase database, ILogger<ChatGameInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Production's unchanged schema ceiling 5 keeps this worker completely inactive.
        if (!database.ChatAvailable) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int count = await database.ProcessChatGameInboxAsync(32, stoppingToken);
                if (count == 32) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (MySqlException exception)
            {
                // Never write message bodies, player names, connection strings or SQL to logs.
                logger.LogWarning("Atlas chat inbox processing postponed (database error {ErrorNumber}).", exception.Number);
            }
            catch (InvalidOperationException)
            {
                logger.LogWarning("Atlas chat inbox processing postponed because its configuration is unavailable.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
