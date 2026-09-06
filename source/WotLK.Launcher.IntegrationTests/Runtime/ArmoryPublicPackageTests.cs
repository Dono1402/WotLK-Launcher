using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WotLK.Launcher.Runtime;

internal static class ArmoryPublicPackageTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequiredPaths =
    [
        "node/node.exe", "app/launcher-server.cjs", "vendor/wow-export/src/js/casc/casc-source-local.js",
        "metadata/manifest.json", "assets/Fonts/Inter-Regular.ttf",
        "prerequisites/MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
    ];

    internal static async Task<int> RunAsync()
    {
        string tempParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AtlasArmoryPublicPackageTests"));
        string tempRoot = Path.Combine(tempParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            await ValidateApiClientAsync();
            ValidatePackages(tempRoot);
            await ValidateWebViewAsync(tempRoot);
            await ValidateHostGuardsAsync(tempRoot);
            Console.WriteLine("Public armory package OK: authenticated-client route allowlist, invalid JSON and RPC numbers, HTTP errors/cancellation and exact 4 MiB boundaries; manifest hashes, idempotence, isolated repair, traversal and malformed archive rejection; injected WebView availability, installation serialization, failures and cancellation. Fake HTTP and payloads only; no installer process, UI, game, production or user-settings access.");
            return 0;
        }
        finally
        {
            string resolved = Path.GetFullPath(tempRoot);
            if (!resolved.StartsWith(tempParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe public-armory test cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task ValidateApiClientAsync()
    {
        List<(string Method, string Uri, string? Token)> observed = [];
        using CallbackHandler routes = new((request, _) =>
        {
            observed.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter));
            return Task.FromResult(Response(new StringContent("{\"characters\":[],\"kept\":true}")));
        });
        using HttpClient client = new(routes);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "fake-disposable-armory-token");
        LauncherArmoryApiClient api = new(client, new Uri("https://armory-fixture.invalid/api/v1/"));
        JsonElement roster = await api.ReadAsync(new(1, "roster"), CancellationToken.None);
        Require(roster.GetProperty("kept").GetBoolean(), "Returned JSON must survive disposal of its source document.");
        await api.ReadAsync(new(2, "catalog", 42), CancellationToken.None);
        await api.ReadAsync(new(9007199254740991, "catalog", uint.MaxValue), CancellationToken.None);
        Require(observed.Select(value => value.Uri).SequenceEqual(new[]
        {
            "https://armory-fixture.invalid/api/v1/armory/characters",
            "https://armory-fixture.invalid/api/v1/armory/characters/42/catalog",
            "https://armory-fixture.invalid/api/v1/armory/characters/4294967295/catalog"
        }) && observed.All(value => value.Method == "GET" && value.Token == "fake-disposable-armory-token"),
            "Only roster and GUID catalog GET routes may use the supplied authenticated HTTP client.");

        LauncherArmoryDataRequest[] rejected =
        [
            new(0, "roster"), new(-1, "roster"), new(9007199254740992, "roster"), new(long.MaxValue, "roster"),
            new(1, "../accounts"), new(1, "sql"), new(1, "ROSTER"), new(1, "roster", 1),
            new(1, "catalog"), new(1, "catalog", 0), new(1, null!)
        ];
        foreach (LauncherArmoryDataRequest request in rejected)
            await ThrowsAsync<ArgumentException>(() => api.ReadAsync(request, CancellationToken.None));
        Require(observed.Count == 3, "Rejected RPC requests must make no HTTP call.");
        foreach (string json in new[]
        {
            "{\"id\":9223372036854775808,\"operation\":\"roster\"}",
            "{\"id\":1,\"operation\":\"catalog\",\"characterId\":4294967296}",
            "{\"id\":1,\"operation\":\"catalog\",\"characterId\":-1}",
            "{\"id\":1.5,\"operation\":\"roster\"}", "{\"id\":\"bad\",\"operation\":\"roster\"}"
        }) Throws<JsonException>(() => JsonSerializer.Deserialize<LauncherArmoryDataRequest>(json, WebJson));

        foreach (string body in new[] { "[]", "null", "1", "true", "\"text\"" })
            await ReadThrowsAsync<InvalidDataException>(() => new StringContent(body));
        await ReadThrowsAsync<JsonException>(() => new StringContent("{broken"));
        string deep = string.Concat(Enumerable.Repeat("{\"x\":", 34)) + "0" + new string('}', 34);
        await ReadThrowsAsync<JsonException>(() => new StringContent(deep));
        await ReadThrowsAsync<UnauthorizedAccessException>(() => new StringContent("{}"), HttpStatusCode.Unauthorized);
        await ReadThrowsAsync<HttpRequestException>(() => new StringContent("{}"), HttpStatusCode.ServiceUnavailable);

        CountingStream headerStream = new(Encoding.UTF8.GetBytes("{}"));
        await ReadThrowsAsync<InvalidDataException>(() =>
        {
            StreamContent content = new(headerStream);
            content.Headers.ContentLength = (long)LauncherArmoryApiClient.MaximumResponseBytes + 1;
            return content;
        });
        Require(headerStream.BytesRead == 0 && headerStream.Disposed, "An oversized Content-Length must reject and dispose the body before reading it.");

        int maximum = LauncherArmoryApiClient.MaximumResponseBytes;
        string prefix = "{\"padding\":\"";
        string suffix = "\"}";
        byte[] boundary = Encoding.UTF8.GetBytes(prefix + new string('x', maximum - prefix.Length - suffix.Length) + suffix);
        CountingStream exactStream = new(boundary);
        JsonElement exact = await ReadResponseAsync(() => new StreamContent(exactStream));
        Require(exact.GetProperty("padding").GetString()!.Length == maximum - prefix.Length - suffix.Length
            && exactStream.BytesRead == maximum && exactStream.Disposed, "A valid response of exactly 4 MiB must be accepted without a Content-Length header.");
        CountingStream oversized = new(boundary.Concat(new byte[] { (byte)' ' }).ToArray());
        await ReadThrowsAsync<InvalidDataException>(() => new StreamContent(oversized));
        Require(oversized.BytesRead == maximum + 1 && oversized.Disposed, "A chunked body must be rejected immediately after the 4 MiB limit is crossed.");

        using CancellationTokenSource cancelled = new();
        CountingStream interrupted = new(boundary, () => cancelled.Cancel());
        await ThrowsAsync<OperationCanceledException>(() => ReadResponseAsync(() => new StreamContent(interrupted), cancellationToken: cancelled.Token));
        Require(interrupted.Disposed, "Cancellation during a streamed body must dispose the response stream.");
    }

    private static async Task<JsonElement> ReadResponseAsync(Func<HttpContent> content,
        HttpStatusCode status = HttpStatusCode.OK, CancellationToken cancellationToken = default)
    {
        using CallbackHandler handler = new((_, _) => Task.FromResult(Response(content(), status)));
        using HttpClient client = new(handler);
        return await new LauncherArmoryApiClient(client, new Uri("https://armory-fixture.invalid/api/v1/"))
            .ReadAsync(new(1, "roster"), cancellationToken);
    }

    private static async Task ReadThrowsAsync<T>(Func<HttpContent> content, HttpStatusCode status = HttpStatusCode.OK) where T : Exception
        => await ThrowsAsync<T>(() => ReadResponseAsync(content, status));

    private static HttpResponseMessage Response(HttpContent content, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = content };

    private static void ValidatePackages(string tempRoot)
    {
        Dictionary<string, byte[]> files = FixtureFiles();
        byte[] zip = CreateArchive(files);
        string cache = Path.Combine(tempRoot, "valid");
        string installed;
        using (MemoryStream payload = new(zip)) installed = LauncherArmoryPackage.Extract(payload, cache);
        string revision = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        Require(installed == Path.Combine(cache, revision), "Runtime extraction must be isolated under its payload SHA256 revision.");
        AssertFiles(installed, files);
        string node = Path.Combine(installed, "node", "node.exe");
        DateTime fixedWriteTime = new(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(node, fixedWriteTime);
        using (MemoryStream payload = new(zip))
            Require(LauncherArmoryPackage.Extract(payload, cache) == installed, "An intact cached payload must reuse its directory.");
        Require(File.GetLastWriteTimeUtc(node) == fixedWriteTime, "Verified cache reuse must not rewrite executable payloads.");

        byte[] tampered = files["node/node.exe"].Select(value => (byte)(value ^ 1)).ToArray();
        File.WriteAllBytes(node, tampered);
        string repaired;
        using (MemoryStream payload = new(zip)) repaired = LauncherArmoryPackage.Extract(payload, cache);
        Require(repaired != installed && Path.GetDirectoryName(repaired) == cache
            && File.ReadAllBytes(node).SequenceEqual(tampered), "A corrupted cached runtime must be repaired in an isolated directory without overwriting the original.");
        AssertFiles(repaired, files);
        File.Delete(Path.Combine(installed, "metadata", "manifest.json"));
        using (MemoryStream payload = new(zip)) AssertFiles(LauncherArmoryPackage.Extract(payload, cache), files);
        string otherCache = Path.Combine(tempRoot, "second-account-cache");
        using (MemoryStream payload = new(zip))
        {
            string isolated = LauncherArmoryPackage.Extract(payload, otherCache);
            Require(isolated != installed && Path.GetDirectoryName(isolated) == otherCache, "A caller-supplied cache root must remain isolated.");
            AssertFiles(isolated, files);
        }

        int invalidCase = 0;
        foreach (string path in new[] { "../escaped.bin", "/absolute.bin", "C:/absolute.bin", "a\\b", "a//b", "a/./b", "a/../b", "a/file:stream" })
            Reject(CreateArchive(files, manifest => manifest["files"]![0]!["path"] = path));
        Reject(CreateArchive(files, manifest => manifest["schemaVersion"] = 2));
        Reject(CreateArchive(files, manifest => manifest["files"] = null));
        Reject(CreateArchive(files, manifest => manifest["files"]![0] = null));
        Reject(CreateArchive(files, manifest => manifest["files"]![0]!["path"] = null));
        Reject(CreateArchive(files, manifest => manifest["files"]![0]!["sha256"] = null));
        Reject(CreateArchive(files, manifest => manifest["files"]![0]!["sha256"] = new string('z', 64)));
        Reject(CreateArchive(files, manifest => manifest["files"]![0]!["sha256"] = "00"));
        Reject(CreateArchive(files, manifest => manifest["files"]![0]!["size"] = -1));
        Reject(CreateArchive(files, manifest => manifest["files"]![0]!["size"] = long.MaxValue));
        Reject(CreateArchive(files, manifest =>
        {
            for (int index = 0; index < 3; index++) manifest["files"]![index]!["size"] = 320 * 1024 * 1024;
        }));
        Reject(CreateArchive(files, manifest => manifest["files"]!.AsArray().RemoveAt(0)));
        Reject(CreateArchive(files, manifest =>
        {
            JsonNode duplicate = manifest["files"]![0]!.DeepClone();
            duplicate["path"] = "NODE/NODE.EXE";
            manifest["files"]!.AsArray().Add(duplicate);
        }));
        Dictionary<string, byte[]> omitted = new(files);
        omitted.Remove(RequiredPaths[0]);
        Reject(CreateArchive(files, archiveFiles: omitted));
        Dictionary<string, byte[]> corrupt = new(files) { [RequiredPaths[0]] = tampered };
        Reject(CreateArchive(files, archiveFiles: corrupt));
        Reject(CreateArchive(files, manifest => manifest["files"]![0]!["size"] = files[RequiredPaths[0]].Length + 1));
        Reject(CreateArchive(files, includeManifest: false));
        Reject(CreateArchive(files, rawManifest: string.Empty));
        Reject(CreateArchive(files, rawManifest: new string(' ', 4 * 1024 * 1024 + 1)));
        Reject(CreateArchive(files, rawManifest: "{broken"), allowJsonException: true);
        using (CountingStream nonSeekable = new(zip))
            Throws<ArgumentException>(() => LauncherArmoryPackage.Extract(nonSeekable, Path.Combine(tempRoot, "nonseekable")));
        Require(!File.Exists(Path.Combine(tempRoot, "escaped.bin")), "Invalid archive paths must not escape the fixture cache.");
        return;

        void Reject(byte[] archive, bool allowJsonException = false)
        {
            string badCache = Path.Combine(tempRoot, "invalid-" + invalidCase++);
            using MemoryStream payload = new(archive);
            try { LauncherArmoryPackage.Extract(payload, badCache); }
            catch (InvalidDataException) { VerifyNoPartial(); return; }
            catch (JsonException) when (allowJsonException) { VerifyNoPartial(); return; }
            throw new InvalidOperationException("A malformed runtime archive was accepted.");

            void VerifyNoPartial() => Require(!Directory.Exists(badCache) || !Directory.EnumerateFileSystemEntries(badCache).Any(),
                "Failed runtime validation must clean its own incomplete staging directory.");
        }
    }

    private static Dictionary<string, byte[]> FixtureFiles() => RequiredPaths.ToDictionary(path => path,
        path => Encoding.UTF8.GetBytes("Inert test content; never execute. " + path), StringComparer.Ordinal);

    private static byte[] CreateArchive(Dictionary<string, byte[]> files, Action<JsonObject>? mutate = null,
        Dictionary<string, byte[]>? archiveFiles = null, bool includeManifest = true, string? rawManifest = null)
    {
        JsonObject manifest = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1,
            files = files.Select(file => new { path = file.Key, size = file.Value.Length, sha256 = Convert.ToHexString(SHA256.HashData(file.Value)) }).ToArray()
        }, WebJson)!.AsObject();
        mutate?.Invoke(manifest);
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeManifest) Add("manifest.json", Encoding.UTF8.GetBytes(rawManifest ?? manifest.ToJsonString(WebJson)));
            foreach (var file in archiveFiles ?? files) Add(file.Key, file.Value);
            void Add(string path, byte[] bytes)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero);
                using Stream stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private static void AssertFiles(string directory, Dictionary<string, byte[]> files)
    {
        foreach (var file in files)
            Require(File.ReadAllBytes(Path.Combine(directory, file.Key)).SequenceEqual(file.Value),
                "Every extracted runtime file must match the manifest's original bytes.");
    }

    private static async Task ValidateWebViewAsync(string tempRoot)
    {
        string installer = Path.Combine(tempRoot, "fake-never-executed-installer.exe");
        await File.WriteAllTextAsync(installer, "Inert fixture. Installation is always injected.");
        LauncherArmoryLocalConfiguration config = new("unused-node", "unused-server", IsPackaged: true, WebViewInstallerPath: installer);
        foreach (string? version in new string?[] { null, "", "bad", "145.0.9999.0", "146.0.3855.99" })
            Require(!LauncherWebViewRuntime.IsSupported(version), "Missing, malformed and older WebView versions must be unsupported.");
        foreach (string version in new[] { "146.0.3856.0", "147.0.1.0", "148.0.1.0 beta" })
            Require(LauncherWebViewRuntime.IsSupported(version), "Compatible WebView versions must be recognized.");

        int installs = 0, notifications = 0, reads = 0;
        await LauncherWebViewRuntime.EnsureAvailableAsync(config, CancellationToken.None, () => notifications++,
            () => { reads++; return "147.0.1.0"; }, _ => { installs++; return Task.FromResult(0); });
        Require(installs == 0 && notifications == 0 && reads == 1, "A compatible installed runtime must never run the installer.");
        await LauncherWebViewRuntime.EnsureAvailableAsync(config with { IsPackaged = false }, CancellationToken.None,
            () => throw new InvalidOperationException("Unexpected local installation notification."),
            () => throw new InvalidOperationException("Unexpected local runtime probe."),
            _ => throw new InvalidOperationException("Unexpected local installation."));

        string? installedVersion = null;
        reads = 0;
        await LauncherWebViewRuntime.EnsureAvailableAsync(config, CancellationToken.None, () => notifications++,
            () => { reads++; return installedVersion; }, path =>
            {
                Require(path == installer, "Only the provided bundled installer path may be passed to the injected installer.");
                installs++;
                installedVersion = "147.0.1.0";
                return Task.FromResult(3010);
            });
        Require(installs == 1 && notifications == 1 && reads == 3, "Missing runtime installation must run once and confirm the installed version afterward.");

        await ThrowsAsync<InvalidOperationException>(() => LauncherWebViewRuntime.EnsureAvailableAsync(
            config with { WebViewInstallerPath = Path.Combine(tempRoot, "missing.exe") }, CancellationToken.None,
            getVersion: () => null, install: _ => throw new InvalidOperationException("Missing installer must never be invoked.")));
        await ThrowsAsync<InvalidOperationException>(() => LauncherWebViewRuntime.EnsureAvailableAsync(config, CancellationToken.None,
            getVersion: () => "145.0.1.0", install: _ => Task.FromResult(1603)));
        await ThrowsAsync<IOException>(() => LauncherWebViewRuntime.EnsureAvailableAsync(config, CancellationToken.None,
            getVersion: () => null, install: _ => throw new IOException("Injected installer failure.")));

        using CancellationTokenSource preCancelled = new();
        preCancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => LauncherWebViewRuntime.EnsureAvailableAsync(config, preCancelled.Token,
            getVersion: () => null, install: _ => throw new InvalidOperationException("Cancelled installation must not start.")));

        installedVersion = null;
        int simultaneousInstalls = 0;
        TaskCompletionSource<int> releaseInstall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource waitingCancellation = new();
        Func<string, Task<int>> heldInstall = _ => { simultaneousInstalls++; return releaseInstall.Task; };
        Task first = LauncherWebViewRuntime.EnsureAvailableAsync(config, CancellationToken.None, getVersion: () => installedVersion, install: heldInstall);
        Task second = LauncherWebViewRuntime.EnsureAvailableAsync(config, CancellationToken.None, getVersion: () => installedVersion, install: heldInstall);
        Task waiting = LauncherWebViewRuntime.EnsureAvailableAsync(config, waitingCancellation.Token, getVersion: () => installedVersion, install: heldInstall);
        try
        {
            waitingCancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => waiting);
            Require(simultaneousInstalls == 1, "Concurrent callers must share the serialized installation gate.");
            installedVersion = "147.0.1.0";
            releaseInstall.TrySetResult(0);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Require(simultaneousInstalls == 1, "A waiter must recheck the runtime and skip a second installation.");
        }
        finally { installedVersion = "147.0.1.0"; releaseInstall.TrySetResult(0); }

        using CancellationTokenSource runningCancellation = new();
        TaskCompletionSource<int> runningInstall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = LauncherWebViewRuntime.EnsureAvailableAsync(config, runningCancellation.Token,
            getVersion: () => null, install: _ => runningInstall.Task);
        runningCancellation.Cancel();
        Require(!running.IsCompleted, "Cancellation must not abandon an installer that has already started.");
        runningInstall.TrySetResult(0);
        await ThrowsAsync<OperationCanceledException>(() => running);
        // The previous failure/cancellation must have released the shared gate.
        installedVersion = null;
        await LauncherWebViewRuntime.EnsureAvailableAsync(config, CancellationToken.None,
            getVersion: () => installedVersion, install: _ => { installedVersion = "147.0.1.0"; return Task.FromResult(0); })
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task ValidateHostGuardsAsync(string tempRoot)
    {
        LauncherArmoryLocalConfiguration config = new(Path.Combine(tempRoot, "never-start-node.exe"),
            Path.Combine(tempRoot, "never-start-server.cjs"), IsPackaged: true);
        using LauncherArmoryLocalHost first = new();
        using LauncherArmoryLocalHost second = new();
        Require(first.Key.Length == 64 && first.Key.All(Uri.IsHexDigit) && first.Key != second.Key,
            "Each host must have an independent random bridge key.");
        Require(!first.Owns("http://127.0.0.1:12345/") && !first.Owns("invalid"), "A host that has not started owns no navigation origin.");
        await ThrowsAsync<InvalidOperationException>(() => first.StartAsync(0, config, CancellationToken.None));
        await ThrowsAsync<InvalidOperationException>(() => first.StartAsync(1, config, CancellationToken.None));
        Require(first.Origin is null, "Invalid account and unauthenticated packaged startup must reject before starting a process.");
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class CountingStream(byte[] bytes, Action? afterFirstRead = null) : Stream
    {
        private int _offset;
        internal int BytesRead => _offset;
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int size = Math.Min(Math.Min(count, 4096), bytes.Length - _offset);
            bytes.AsSpan(_offset, size).CopyTo(buffer.AsSpan(offset, size));
            bool first = _offset == 0 && size > 0;
            _offset += size;
            if (first) afterFirstRead?.Invoke();
            return size;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] scratch = new byte[buffer.Length];
            int read = Read(scratch, 0, scratch.Length);
            scratch.AsSpan(0, read).CopyTo(buffer.Span);
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
