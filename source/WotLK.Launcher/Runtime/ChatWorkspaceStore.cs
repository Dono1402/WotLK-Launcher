using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WotLK.Launcher.Runtime;

internal interface IChatWorkspaceStore
{
    Task<T?> LoadAsync<T>(uint ownerAccountId, CancellationToken cancellationToken) where T : class;
    Task SaveAsync<T>(uint ownerAccountId, T state, CancellationToken cancellationToken) where T : class;
}

internal interface IChatWorkspaceProtector
{
    byte[] Protect(byte[] plaintext, byte[] entropy);
    byte[] Unprotect(byte[] ciphertext, byte[] entropy);
}

internal sealed class ChatWorkspaceProtector : IChatWorkspaceProtector
{
    public byte[] Protect(byte[] plaintext, byte[] entropy)
        => ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext, byte[] entropy)
        => ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Account-scoped private drafts/outbox metadata. File bytes never enter this store.</summary>
internal sealed class ChatWorkspaceStore : IChatWorkspaceStore
{
    internal const int MaximumDocumentBytes = 16 * 1024 * 1024;
    private const int MaximumProtectedBytes = MaximumDocumentBytes + 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly string _root;
    private readonly string _environment;
    private readonly IChatWorkspaceProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal ChatWorkspaceStore(string root, Uri apiBaseUri, IChatWorkspaceProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (!apiBaseUri.IsAbsoluteUri || apiBaseUri.UserInfo.Length != 0 || apiBaseUri.Query.Length != 0
            || apiBaseUri.Fragment.Length != 0)
            throw new ArgumentException("A plain absolute API origin/path is required.", nameof(apiBaseUri));
        _environment = apiBaseUri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped).TrimEnd('/');
        string partition = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_environment))).ToLowerInvariant();
        _root = Path.Combine(Path.GetFullPath(root), partition);
        _protector = protector ?? new ChatWorkspaceProtector();
    }

    public async Task<T?> LoadAsync<T>(uint ownerAccountId, CancellationToken cancellationToken) where T : class
    {
        string path = GetPath(ownerAccountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumProtectedBytes)
                throw new InvalidDataException("Invalid Messages workspace size.");
            byte[] ciphertext = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            byte[] plaintext = _protector.Unprotect(ciphertext, Entropy(ownerAccountId));
            try
            {
                if (plaintext.Length > MaximumDocumentBytes) throw new InvalidDataException("Invalid Messages workspace size.");
                Envelope<T>? document = JsonSerializer.Deserialize<Envelope<T>>(plaintext, Json);
                if (document is null || document.Version != 1 || document.OwnerAccountId != ownerAccountId
                    || document.Environment != _environment || document.State is null)
                    throw new InvalidDataException("Invalid Messages workspace owner or version.");
                return document.State;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync<T>(uint ownerAccountId, T state, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(state);
        string path = GetPath(ownerAccountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(1, ownerAccountId, _environment, state), Json);
            byte[] ciphertext;
            try
            {
                if (plaintext.Length > MaximumDocumentBytes) throw new InvalidDataException("Messages workspace storage limit reached.");
                ciphertext = _protector.Protect(plaintext, Entropy(ownerAccountId));
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            if (ciphertext.Length > MaximumProtectedBytes) throw new InvalidDataException("Messages workspace storage limit reached.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }

    internal string GetPath(uint ownerAccountId)
    {
        if (ownerAccountId == 0) throw new ArgumentOutOfRangeException(nameof(ownerAccountId));
        return Path.Combine(_root, ownerAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "workspace.bin");
    }

    private byte[] Entropy(uint ownerAccountId)
        => Encoding.UTF8.GetBytes($"Atlas Messages workspace v1\n{_environment}\n{ownerAccountId}");

    private sealed record Envelope<T>(int Version, uint OwnerAccountId, string Environment, T State);
}
