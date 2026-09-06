using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WotLK.Launcher.Runtime;

internal sealed record LauncherArmoryLocalConfiguration(
    string NodePath, string ServerPath, bool IsPackaged = false, string? ClientRoot = null,
    string? DataRoot = null, string? VendorRoot = null, string? AssetRoot = null, string? MetadataRoot = null,
    string? WebViewInstallerPath = null);

internal sealed class LauncherArmoryLocalHost : IDisposable
{
    private Process? _process;
    private CancellationTokenSource? _requestsLifetime;
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly HashSet<long> _activeRequests = [];
    private readonly object _requestGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    internal Uri? Origin { get; private set; }

    internal static LauncherArmoryLocalConfiguration LoadConfiguration() => LoadConfiguration(null);

    internal static LauncherArmoryLocalConfiguration LoadConfiguration(string? clientRoot)
    {
#if ATLAS_LOCAL_CLIENT
        string file = Path.Combine(AppContext.BaseDirectory, "armory-local.json");
        if (!File.Exists(file)) return LauncherArmoryPackage.LoadConfiguration(clientRoot);
        LauncherArmoryLocalConfiguration config = JsonSerializer.Deserialize<LauncherArmoryLocalConfiguration>(File.ReadAllText(file))
            ?? throw new InvalidOperationException("Missing local armory configuration.");
        if (!Path.IsPathFullyQualified(config.NodePath) || !Path.IsPathFullyQualified(config.ServerPath)
            || !File.Exists(config.NodePath) || !File.Exists(config.ServerPath)
            || !string.Equals(Path.GetFileName(config.NodePath), "node.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(config.ServerPath), "launcher-server.cjs", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid local armory configuration.");
        return config;
#else
        return LauncherArmoryPackage.LoadConfiguration(clientRoot);
#endif
    }

    internal async Task StartAsync(uint accountId, LauncherArmoryLocalConfiguration configuration, CancellationToken cancellationToken,
        Func<LauncherArmoryDataRequest, CancellationToken, Task<JsonElement>>? readData = null)
    {
        if (accountId == 0 || _process is not null) throw new InvalidOperationException("Invalid armory startup.");
        if (configuration.IsPackaged && readData is null) throw new InvalidOperationException("The public armory requires an authenticated data source.");
        _requestsLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken sessionToken = _requestsLifetime.Token;
        ProcessStartInfo start = new(configuration.NodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(configuration.ServerPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        start.ArgumentList.Add(configuration.ServerPath);
        start.Environment["ATLAS_ARMORY_ACCOUNT_ID"] = accountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["ATLAS_ARMORY_BRIDGE_KEY"] = Key;
        if (configuration.IsPackaged)
        {
            foreach (string key in new[] { "NODE_OPTIONS", "NODE_PATH", "PLAYWRIGHT_MODULE", "ARMORY_EXPORT_DIR", "ATLAS_ARMORY_CONFIG" })
                start.Environment.Remove(key);
            start.Environment["ATLAS_ARMORY_SOURCE"] = "rpc";
            start.Environment["ATLAS_ARMORY_CLIENT_ROOT"] = configuration.ClientRoot ?? string.Empty;
            start.Environment["ATLAS_ARMORY_DATA_ROOT"] = configuration.DataRoot!;
            start.Environment["ATLAS_ARMORY_VENDOR_ROOT"] = configuration.VendorRoot!;
            start.Environment["ATLAS_ARMORY_ASSET_ROOT"] = configuration.AssetRoot!;
            start.Environment["ATLAS_ARMORY_METADATA_ROOT"] = configuration.MetadataRoot!;
        }
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) =>
        {
            const string requestPrefix = "ATLAS_ARMORY_REQUEST ";
            if (configuration.IsPackaged && args.Data?.StartsWith(requestPrefix, StringComparison.Ordinal) == true)
            {
                if (args.Data.Length <= 4096) _ = HandleRequestAsync(args.Data[requestPrefix.Length..], readData!, sessionToken);
                return;
            }
            const string prefix = "ATLAS_ARMORY_READY ";
            if (args.Data?.StartsWith(prefix, StringComparison.Ordinal) != true) return;
            try
            {
                using JsonDocument json = JsonDocument.Parse(args.Data[prefix.Length..]);
                if (json.RootElement.TryGetProperty("port", out JsonElement value)
                    && value.TryGetInt32(out int port) && port is > 0 and <= 65535) _ready.TrySetResult(port);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { }
        };
        _process.ErrorDataReceived += (_, _) => { };
        _process.Exited += (_, _) => _ready.TrySetException(new InvalidOperationException("Local armory stopped."));
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        try
        {
            int port = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            Origin = new Uri($"http://127.0.0.1:{port}/");
        }
        catch { Dispose(); throw; }
    }

    private async Task HandleRequestAsync(string message,
        Func<LauncherArmoryDataRequest, CancellationToken, Task<JsonElement>> readData, CancellationToken cancellationToken)
    {
        LauncherArmoryDataRequest? request;
        try { request = JsonSerializer.Deserialize<LauncherArmoryDataRequest>(message, JsonOptions); }
        catch (JsonException) { return; }
        if (request?.IsValid != true || cancellationToken.IsCancellationRequested) return;
        lock (_requestGate)
        {
            if (_activeRequests.Count >= 4 || !_activeRequests.Add(request.Id)) return;
        }
        try
        {
            string response;
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                JsonElement result = await readData(request, timeout.Token).ConfigureAwait(false);
                if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid armory data.");
                response = JsonSerializer.Serialize(new { id = request.Id, result });
                if (System.Text.Encoding.UTF8.GetByteCount(response) > LauncherArmoryApiClient.MaximumResponseBytes)
                    throw new InvalidDataException("Armory data is too large.");
            }
            catch (UnauthorizedAccessException) { response = JsonSerializer.Serialize(new { id = request.Id, error = "unauthorized" }); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            { response = JsonSerializer.Serialize(new { id = request.Id, error = "unavailable" }); }
            await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Process? process = _process;
                if (process is not null && !process.HasExited)
                    await process.StandardInput.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            finally { _inputGate.Release(); }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException) { }
        finally { lock (_requestGate) _activeRequests.Remove(request.Id); }
    }

    internal bool Owns(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
           && Origin is not null && uri.Scheme == Origin.Scheme && uri.Host == Origin.Host
           && uri.Port == Origin.Port && string.IsNullOrEmpty(uri.UserInfo);

    public void Dispose()
    {
        _requestsLifetime?.Cancel();
        Process? process = Interlocked.Exchange(ref _process, null);
        Origin = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                // Serialize shutdown with data replies; no response can be sent to a later session.
                if (_inputGate.Wait(100))
                {
                    try { process.StandardInput.WriteLine("shutdown"); process.StandardInput.Close(); }
                    finally { _inputGate.Release(); }
                }
                if (!process.WaitForExit(1500)) process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        finally { process.Dispose(); }
    }
}
