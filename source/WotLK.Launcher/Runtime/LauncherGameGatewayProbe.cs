using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WotLK.Launcher.Runtime;

internal interface ILauncherGameGatewayProbe
{
    Task<int?> MeasureAsync(CancellationToken cancellationToken);
}

internal sealed class TcpLauncherGameGatewayProbe : ILauncherGameGatewayProbe
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly Func<TcpClient, IPAddress[], int, CancellationToken, ValueTask> _connect;

    // Hermes InstancePort is the gateway used by the game world connection.
    internal TcpLauncherGameGatewayProbe(string host = "animeclub.fr", int port = 8086, TimeSpan? timeout = null)
        : this(host, port, timeout, static (client, addresses, targetPort, token) => client.ConnectAsync(addresses, targetPort, token))
    {
    }

    internal TcpLauncherGameGatewayProbe(string host, int port, TimeSpan? timeout,
        Func<TcpClient, IPAddress[], int, CancellationToken, ValueTask> connect)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("A gateway host is required.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _host = host;
        _port = port;
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<int?> MeasureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(_host, out IPAddress? address)
                ? [address] : await Dns.GetHostAddressesAsync(_host, timeout.Token).ConfigureAwait(false);
            using TcpClient client = new();
            Stopwatch watch = Stopwatch.StartNew();
            await _connect(client, addresses, _port, timeout.Token).ConfigureAwait(false);
            return Math.Max(1, checked((int)Math.Ceiling(watch.Elapsed.TotalMilliseconds)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }
}
