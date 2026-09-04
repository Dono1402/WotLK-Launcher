namespace WotLK.Launcher.Server.Avatars;

internal static class AvatarLimits
{
    internal const long MaximumFileBytes = 25L * 1024 * 1024;
    internal const long MaximumMultipartBodyBytes = MaximumFileBytes + (64L * 1024);
}

internal enum AvatarAssetStatus : byte
{
    Pending = 0,
    Ready = 1,
    Retired = 2,
    Deleted = 3
}

public sealed record AvatarDescriptor(
    Guid AvatarId,
    ulong Version,
    string Url32,
    string Url64,
    string Url128,
    string Url256)
{
    internal static AvatarDescriptor Create(Guid avatarId, ulong version)
    {
        string root = $"/media/avatars/{avatarId:N}/{version}";
        return new AvatarDescriptor(
            avatarId,
            version,
            $"{root}/32.png",
            $"{root}/64.png",
            $"{root}/128.png",
            $"{root}/256.png");
    }
}

public sealed record AvatarApiError(
    string Code,
    string Message,
    string OperationId);

internal sealed record AvatarAssetRecord(
    Guid Id,
    uint OwnerAccountId,
    ulong Version,
    AvatarAssetStatus Status,
    AvatarStorageKey StorageKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record AvatarMediaRecord(
    Guid AvatarId,
    ulong Version,
    AvatarStorageKey StorageKey,
    int Size,
    string ContentType,
    long ByteLength,
    byte[] Sha256);

internal sealed record AvatarPublicationResult(
    AvatarDescriptor Current,
    AvatarAssetRecord? Retired);

internal sealed record AvatarDeletionResult(
    bool HadActiveAvatar,
    AvatarAssetRecord? DeletedAsset);

internal sealed record AvatarRepositoryAssetState(
    AvatarAssetRecord Asset,
    bool IsActive);

internal sealed record AvatarRepositoryInventory(
    IReadOnlyList<AvatarRepositoryAssetState> Assets);

internal sealed record AvatarCommandResult(
    int StatusCode,
    AvatarDescriptor? Avatar,
    AvatarApiError? Error)
{
    internal static AvatarCommandResult Success(AvatarDescriptor avatar)
        => new(StatusCodes.Status200OK, avatar, null);

    internal static AvatarCommandResult NoContent()
        => new(StatusCodes.Status204NoContent, null, null);

    internal static AvatarCommandResult Failure(
        int statusCode,
        string code,
        string message,
        Guid operationId)
        => new(
            statusCode,
            null,
            new AvatarApiError(code, message, operationId.ToString("N")));
}

internal sealed record AvatarMediaContent(
    AvatarMediaRecord Metadata,
    Stream Content);
