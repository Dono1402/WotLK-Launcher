using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace WotLK.Launcher;

internal static class AtlasNetwork
{
    private const string AtlasHost = "animeclub.fr";
    private static readonly IPAddress AtlasIpv4 = IPAddress.Parse("152.228.225.7");

    public static SocketsHttpHandler CreateHandler()
        => new()
        {
            ConnectCallback = ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        DnsEndPoint endpoint = context.DnsEndPoint;
        if (string.Equals(endpoint.Host, AtlasHost, StringComparison.OrdinalIgnoreCase))
            return await ConnectAsync(AtlasIpv4, endpoint.Port, cancellationToken);

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(
            endpoint.Host,
            cancellationToken);
        Exception? lastError = null;
        foreach (IPAddress address in addresses.OrderBy(
                     address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            try
            {
                return await ConnectAsync(address, endpoint.Port, cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                lastError = ex;
                if (cancellationToken.IsCancellationRequested)
                    throw;
            }
        }

        throw new HttpRequestException(
            $"Impossible de joindre {endpoint.Host}:{endpoint.Port}.",
            lastError);
    }

    private static async Task<Stream> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(address, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
