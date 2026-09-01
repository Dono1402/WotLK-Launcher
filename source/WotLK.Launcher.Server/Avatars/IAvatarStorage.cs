namespace WotLK.Launcher.Server.Avatars;

internal interface IAvatarStorage
{
    Task<AvatarStagingHandle> BeginStagingAsync(CancellationToken cancellationToken);
    Task WriteOriginalAsync(AvatarStagingHandle handle, Stream source, CancellationToken cancellationToken);
    Task<Stream> OpenOriginalReadAsync(AvatarStagingHandle handle, CancellationToken cancellationToken);
    Task<AvatarStoredVariant> WriteVariantAsync(
        AvatarStagingHandle handle,
        int size,
        Stream png,
        CancellationToken cancellationToken);
    Task PublishAsync(AvatarStagingHandle handle, AvatarStorageKey storageKey, CancellationToken cancellationToken);
    Task<Stream> OpenVariantReadAsync(AvatarStorageKey storageKey, int size, CancellationToken cancellationToken);
    Task<bool> MoveToTrashAsync(AvatarStorageKey storageKey, CancellationToken cancellationToken);
    Task QuarantineAsync(AvatarStagingHandle handle, CancellationToken cancellationToken);
    Task DiscardStagingAsync(AvatarStagingHandle handle, CancellationToken cancellationToken);
    Task<AvatarStorageInventory> InspectAsync(CancellationToken cancellationToken);
}
