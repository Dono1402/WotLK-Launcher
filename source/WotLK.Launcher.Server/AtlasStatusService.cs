using System.Diagnostics;
using System.Net.Sockets;

namespace WotLK.Launcher.Server;

public sealed class AtlasStatusService
{
    public async Task<LauncherStatusResponse> GetAsync(CancellationToken cancellationToken)
    {
        Task<bool> authentication = ProbeAsync(1119, cancellationToken);
        Task<bool> realm = ProbeAsync(8084, cancellationToken);
        Task<bool> worldGateway = ProbeAsync(8086, cancellationToken);
        Task<bool> worldServer = ProbeAsync(4000, cancellationToken);
        await Task.WhenAll(authentication, realm, worldGateway, worldServer);

        return new LauncherStatusResponse(
            "Arthas",
            true,
            authentication.Result,
            realm.Result,
            worldGateway.Result,
            worldServer.Result,
            DateTimeOffset.UtcNow);
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
        catch (Exception ex) when (
            ex is SocketException
            or OperationCanceledException)
        {
            return false;
        }
    }
}
