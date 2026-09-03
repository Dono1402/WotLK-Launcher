using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WotLK.Launcher.Runtime;

internal static class LauncherUpdateSecurityConstants
{
    internal const int SupportedSchemaVersion = 1;
    internal const int MaximumManifestBytes = 64 * 1024;
    internal const long MaximumPackageBytes = 1024L * 1024 * 1024;
    internal const string AllowedHost = "animeclub.fr";
    internal const string ManifestPath = "/wotlk/launcher/launcher-update.json";
    internal const string PackageRootPath = "/wotlk/launcher/releases/";
    internal const string PackageFileName = "WotLK-Launcher.exe";
    internal const string ProductionTrustResourceName =
        "WotLK.Launcher.Assets.Security.launcher-update-public-keys.json";

    internal static readonly Uri ManifestUri = new(
        "https://animeclub.fr/wotlk/launcher/launcher-update.json",
        UriKind.Absolute);
}

internal sealed class LauncherUpdateManifestTransportException : Exception
{
    internal LauncherUpdateManifestTransportException() : base("ManifestTransportRejected")
    {
    }
}

internal sealed class LauncherUpdateManifestSignatureException : Exception
{
    internal LauncherUpdateManifestSignatureException() : base("ManifestSignatureInvalid")
    {
    }
}

internal sealed class LauncherUpdateManifestUnsupportedException : Exception
{
    internal LauncherUpdateManifestUnsupportedException() : base("ManifestUnsupported")
    {
    }
}

internal sealed class LauncherUpdateManifestFormatException : Exception
{
    internal LauncherUpdateManifestFormatException() : base("ManifestInvalid")
    {
    }
}

internal sealed class LauncherUpdatePackageIntegrityException : Exception
{
    internal LauncherUpdatePackageIntegrityException() : base("PackageIntegrityFailed")
    {
    }
}

internal static class LauncherUpdateUriPolicy
{
    private static readonly StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;
    private static readonly Regex VersionSegmentPattern = new(
        "^[0-9]{1,5}(\\.[0-9]{1,5}){1,3}$",
        RegexOptions.CultureInvariant);

    internal static void RequireManifestUri(Uri uri)
    {
        RequireCommon(uri);
        if (!string.Equals(uri.AbsolutePath, LauncherUpdateSecurityConstants.ManifestPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new LauncherUpdateManifestTransportException();
        }
    }

    internal static Uri RequirePackageUri(string rawUrl, string expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !string.Equals(rawUrl, rawUrl.Trim(), StringComparison.Ordinal)
            || ContainsAmbiguousPathEncoding(rawUrl)
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new LauncherUpdateManifestTransportException();
        }

        RequireCommon(uri);
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new LauncherUpdateManifestTransportException();
        }

        string expectedPath = LauncherUpdateSecurityConstants.PackageRootPath
            + expectedVersion
            + "/"
            + LauncherUpdateSecurityConstants.PackageFileName;
        if (!VersionSegmentPattern.IsMatch(expectedVersion)
            || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal)
            || !string.Equals(rawUrl, uri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new LauncherUpdateManifestTransportException();
        }

        return uri;
    }

    internal static void RequirePackageUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string raw = uri.OriginalString;
        if (ContainsAmbiguousPathEncoding(raw))
        {
            throw new LauncherUpdateManifestTransportException();
        }

        RequireCommon(uri);
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.None);
        if (!string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || segments.Length != 6
            || segments[0].Length != 0
            || !string.Equals(segments[1], "wotlk", StringComparison.Ordinal)
            || !string.Equals(segments[2], "launcher", StringComparison.Ordinal)
            || !string.Equals(segments[3], "releases", StringComparison.Ordinal)
            || !VersionSegmentPattern.IsMatch(segments[4])
            || !string.Equals(
                segments[5],
                LauncherUpdateSecurityConstants.PackageFileName,
                StringComparison.Ordinal)
            || !string.Equals(raw, uri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new LauncherUpdateManifestTransportException();
        }
    }

    private static void RequireCommon(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, IgnoreCase)
            || !string.Equals(uri.IdnHost, LauncherUpdateSecurityConstants.AllowedHost, IgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new LauncherUpdateManifestTransportException();
        }
    }

    private static bool ContainsAmbiguousPathEncoding(string value) =>
        value.Contains('\\')
        || value.Contains("..", StringComparison.Ordinal)
        || value.Contains("%2e", IgnoreCase)
        || value.Contains("%2f", IgnoreCase)
        || value.Contains("%5c", IgnoreCase);
}

internal static class LauncherUpdateManifestCanonicalizer
{
    internal const string Domain = "atlas-launcher-update-manifest-v1";

    internal static byte[] CreatePayload(LauncherUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireCanonicalText(manifest.KeyId);
        RequireCanonicalText(manifest.Version);
        RequireCanonicalText(manifest.Sha256);
        RequireCanonicalText(manifest.Url);
        RequireCanonicalText(manifest.PublishedAt);

        string canonical = string.Join(
            '\n',
            Domain,
            "schemaVersion=" + manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            "keyId=" + manifest.KeyId,
            "version=" + manifest.Version,
            "size=" + manifest.Size.ToString(CultureInfo.InvariantCulture),
            "sha256=" + manifest.Sha256,
            "url=" + manifest.Url,
            "publishedAt=" + manifest.PublishedAt,
            string.Empty);
        return Encoding.UTF8.GetBytes(canonical);
    }

    private static void RequireCanonicalText(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Any(character => character is '\r' or '\n' || character > 0x7f))
        {
            throw new LauncherUpdateManifestFormatException();
        }
    }
}

internal sealed class LauncherUpdateTrustStore
{
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    private LauncherUpdateTrustStore(IReadOnlyDictionary<string, byte[]> keys)
    {
        _keys = keys;
    }

    internal static LauncherUpdateTrustStore FromSubjectPublicKeys(
        IEnumerable<KeyValuePair<string, byte[]>> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Dictionary<string, byte[]> validated = new(StringComparer.Ordinal);
        foreach ((string keyId, byte[] subjectPublicKeyInfo) in keys)
        {
            ValidateKeyId(keyId);
            ArgumentNullException.ThrowIfNull(subjectPublicKeyInfo);
            byte[] copy = subjectPublicKeyInfo.ToArray();
            ValidateP256PublicKey(copy);
            if (!validated.TryAdd(keyId, copy))
            {
                throw new InvalidDataException("DuplicateLauncherUpdateKeyId");
            }
        }

        return new LauncherUpdateTrustStore(validated);
    }

    internal static LauncherUpdateTrustStore LoadEmbeddedProduction()
    {
        Assembly assembly = typeof(LauncherUpdateTrustStore).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
            LauncherUpdateSecurityConstants.ProductionTrustResourceName)
            ?? throw new InvalidDataException("LauncherUpdateTrustResourceMissing");
        ProductionTrustDocument document = JsonSerializer.Deserialize<ProductionTrustDocument>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("LauncherUpdateTrustResourceEmpty");
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException("LauncherUpdateTrustResourceUnsupported");
        }

        IEnumerable<KeyValuePair<string, byte[]>> keys = document.Keys.Select(key =>
        {
            if (key.KeyId.StartsWith("atlas-test-", StringComparison.Ordinal))
            {
                throw new InvalidDataException("LauncherUpdateTestKeyForbiddenInProductionTrust");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(key.SubjectPublicKeyInfo);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("LauncherUpdateTrustKeyInvalid", exception);
            }

            return new KeyValuePair<string, byte[]>(key.KeyId, bytes);
        });
        return FromSubjectPublicKeys(keys);
    }

    internal bool TryGetSubjectPublicKeyInfo(string keyId, out byte[] subjectPublicKeyInfo)
    {
        if (_keys.TryGetValue(keyId, out byte[]? stored))
        {
            subjectPublicKeyInfo = stored.ToArray();
            return true;
        }

        subjectPublicKeyInfo = [];
        return false;
    }

    internal int Count => _keys.Count;

    private static void ValidateKeyId(string keyId)
    {
        if (!Regex.IsMatch(
                keyId ?? string.Empty,
                "^[a-z0-9][a-z0-9._-]{2,63}$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("LauncherUpdateTrustKeyIdInvalid");
        }
    }

    private static void ValidateP256PublicKey(byte[] subjectPublicKeyInfo)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int bytesRead);
            ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
            if (bytesRead != subjectPublicKeyInfo.Length
                || key.KeySize != 256
                || !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("LauncherUpdateTrustKeyMustBeP256");
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("LauncherUpdateTrustKeyInvalid", exception);
        }
    }

    private sealed class ProductionTrustDocument
    {
        public int SchemaVersion { get; set; }

        public List<ProductionTrustKey> Keys { get; set; } = [];
    }

    private sealed class ProductionTrustKey
    {
        public string KeyId { get; set; } = string.Empty;

        public string SubjectPublicKeyInfo { get; set; } = string.Empty;
    }
}

internal interface ILauncherUpdateManifestVerifier
{
    void Verify(LauncherUpdateManifest manifest);
}

internal static class LauncherUpdateManifestJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static LauncherUpdateManifest ParseStrict(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("LauncherUpdateManifestRootMustBeObject");
        }

        HashSet<string> propertyNames = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
            {
                throw new JsonException("LauncherUpdateManifestDuplicateProperty");
            }
        }

        return document.RootElement.Deserialize<LauncherUpdateManifest>(Options)
            ?? throw new JsonException("LauncherUpdateManifestEmpty");
    }
}

internal sealed class LauncherUpdateManifestVerifier(
    LauncherUpdateTrustStore trustStore) : ILauncherUpdateManifestVerifier
{
    private static readonly Regex KeyIdPattern = new(
        "^[a-z0-9][a-z0-9._-]{2,63}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(
        "^[0-9]{1,5}(\\.[0-9]{1,5}){1,3}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);

    public void Verify(LauncherUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != LauncherUpdateSecurityConstants.SupportedSchemaVersion)
        {
            throw new LauncherUpdateManifestUnsupportedException();
        }

        string keyId = manifest.KeyId ?? string.Empty;
        if (!KeyIdPattern.IsMatch(keyId)
            || string.IsNullOrWhiteSpace(manifest.Signature)
            || !trustStore.TryGetSubjectPublicKeyInfo(
                keyId,
                out byte[] subjectPublicKeyInfo))
        {
            throw new LauncherUpdateManifestSignatureException();
        }

        byte[] payload = LauncherUpdateManifestCanonicalizer.CreatePayload(manifest);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            throw new LauncherUpdateManifestSignatureException();
        }

        bool valid;
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int bytesRead);
            valid = bytesRead == subjectPublicKeyInfo.Length
                && key.KeySize == 256
                && key.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            valid = false;
        }

        if (!valid)
        {
            throw new LauncherUpdateManifestSignatureException();
        }

        ValidateSignedFields(manifest);
    }

    private static void ValidateSignedFields(LauncherUpdateManifest manifest)
    {
        if (!VersionPattern.IsMatch(manifest.Version)
            || !Version.TryParse(manifest.Version, out _)
            || manifest.Size <= 0
            || manifest.Size > LauncherUpdateSecurityConstants.MaximumPackageBytes
            || !Sha256Pattern.IsMatch(manifest.Sha256)
            || !DateTimeOffset.TryParseExact(
                manifest.PublishedAt,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            throw new LauncherUpdateManifestFormatException();
        }

        _ = LauncherUpdateUriPolicy.RequirePackageUri(manifest.Url, manifest.Version);
    }
}

internal static class LauncherUpdatePackageIntegrity
{
    internal static async Task ValidateAsync(
        string path,
        LauncherUpdateManifest manifest,
        Func<string, CancellationToken, Task<string>> computeSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(computeSha256);
        FileInfo candidate = new(path);
        if (!candidate.Exists || candidate.Length != manifest.Size)
        {
            throw new LauncherUpdatePackageIntegrityException();
        }

        string actualHash = await computeSha256(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            byte[] expected = Convert.FromHexString(manifest.Sha256);
            byte[] actual = Convert.FromHexString(actualHash);
            if (expected.Length != actual.Length
                || !CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new LauncherUpdatePackageIntegrityException();
            }
        }
        catch (FormatException)
        {
            throw new LauncherUpdatePackageIntegrityException();
        }
    }
}
