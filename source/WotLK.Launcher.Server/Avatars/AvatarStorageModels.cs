namespace WotLK.Launcher.Server.Avatars;

internal static class AvatarVariantSizes
{
    internal static readonly int[] All = [32, 64, 128, 256];

    internal static bool IsSupported(int size) => Array.BinarySearch(All, size) >= 0;
}

internal readonly record struct AvatarStagingHandle(Guid OperationId)
{
    internal static AvatarStagingHandle Create()
        => new(Guid.NewGuid());

    internal void EnsureValid()
    {
        if (OperationId == Guid.Empty)
            throw new ArgumentException("L'identifiant de staging avatar est invalide.");
    }
}

internal readonly record struct AvatarStorageKey
{
    private AvatarStorageKey(Guid avatarId, ulong version)
    {
        AvatarId = avatarId;
        Version = version;
    }

    internal Guid AvatarId { get; }
    internal ulong Version { get; }
    internal string Value => $"avatars/{AvatarId:N}/{Version}";

    internal static AvatarStorageKey Create(Guid avatarId, ulong version)
    {
        if (avatarId == Guid.Empty)
            throw new ArgumentException("L'identifiant de l'avatar est invalide.", nameof(avatarId));
        if (version == 0)
            throw new ArgumentOutOfRangeException(nameof(version), "La version avatar doit etre positive.");
        return new AvatarStorageKey(avatarId, version);
    }

    internal static AvatarStorageKey Parse(string value)
    {
        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !string.Equals(parts[0], "avatars", StringComparison.Ordinal)
            || parts[1].Length != 32
            || !Guid.TryParseExact(parts[1], "N", out Guid avatarId)
            || !ulong.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong version))
        {
            throw new ArgumentException("La cle de stockage avatar est invalide.", nameof(value));
        }

        AvatarStorageKey key = Create(avatarId, version);
        if (!string.Equals(key.Value, value, StringComparison.Ordinal))
            throw new ArgumentException("La cle de stockage avatar n'est pas canonique.", nameof(value));
        return key;
    }
}

internal sealed record AvatarStoredVariant(
    int Size,
    string ContentType,
    long ByteLength,
    byte[] Sha256);

internal sealed record AvatarStagingEntry(
    AvatarStagingHandle Handle,
    DateTimeOffset LastModifiedAt);

internal sealed record AvatarPublishedEntry(
    AvatarStorageKey StorageKey,
    DateTimeOffset LastModifiedAt);

internal sealed record AvatarStorageInventory(
    IReadOnlyList<AvatarStagingEntry> Staging,
    IReadOnlyList<AvatarPublishedEntry> Published);

internal sealed class AvatarStorageException : Exception
{
    internal AvatarStorageException(string message)
        : base(message)
    {
    }

    internal AvatarStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
