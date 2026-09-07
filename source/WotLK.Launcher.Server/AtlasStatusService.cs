using System.Net.Sockets;
using MySqlConnector;

namespace WotLK.Launcher.Server;

public sealed class AtlasStatusService
{
    private readonly LauncherDatabase _database;
    private readonly ILogger<AtlasStatusService> _logger;
    private readonly Func<int, CancellationToken, Task<bool>> _probe;

    public AtlasStatusService(LauncherDatabase database, ILogger<AtlasStatusService> logger)
        : this(database, logger, ProbeAsync) { }

    internal AtlasStatusService(LauncherDatabase database, ILogger<AtlasStatusService> logger,
        Func<int, CancellationToken, Task<bool>> probe)
    {
        _database = database;
        _logger = logger;
        _probe = probe;
    }

    public async Task<LauncherStatusResponse> GetAsync(CancellationToken cancellationToken)
    {
        Task<bool> authentication = _probe(1119, cancellationToken);
        Task<bool> realm = _probe(8084, cancellationToken);
        Task<bool> worldGateway = _probe(8086, cancellationToken);
        Task<bool> worldServer = _probe(4000, cancellationToken);
        Task<ServerOnlinePlayerCount?> onlinePlayers = ReadOnlinePlayersAsync(cancellationToken);
        await Task.WhenAll(authentication, realm, worldGateway, worldServer, onlinePlayers);
        ServerOnlinePlayerCount? count = worldServer.Result ? onlinePlayers.Result : null;

        return new LauncherStatusResponse(
            "Arthas",
            true,
            authentication.Result,
            realm.Result,
            worldGateway.Result,
            worldServer.Result,
            DateTimeOffset.UtcNow,
            count?.Count,
            count?.Kind);
    }

    private async Task<ServerOnlinePlayerCount?> ReadOnlinePlayersAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { return await _database.GetOnlinePlayerCountAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Online player count unavailable (timeout).");
            return null;
        }
        catch (Exception error) when (error is MySqlException or InvalidOperationException or InvalidDataException or OverflowException)
        {
            _logger.LogWarning("Online player count unavailable ({ErrorType}).", error.GetType().Name);
            return null;
        }
    }

    private static async Task<bool> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
