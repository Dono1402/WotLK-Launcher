namespace WotLK.Launcher.Server.Avatars;

internal sealed record AvatarCleanupPlan(
    IReadOnlyList<AvatarStagingEntry> StaleStaging,
    IReadOnlyList<AvatarAssetRecord> AbandonedPending,
    IReadOnlyList<AvatarPublishedEntry> OrphanedMedia,
    IReadOnlyList<AvatarAssetRecord> PurgeableAssets);

internal sealed class AvatarCleanupInspector
{
    private readonly IAvatarRepository _repository;
    private readonly IAvatarStorage _storage;

    internal AvatarCleanupInspector(IAvatarRepository repository, IAvatarStorage storage)
    {
        _repository = repository;
        _storage = storage;
    }

    internal async Task<AvatarCleanupPlan> InspectAsync(
        DateTimeOffset now,
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default)
    {
        if (staleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleAfter));

        AvatarRepositoryInventory repository = await _repository.InspectAsync(cancellationToken);
        AvatarStorageInventory storage = await _storage.InspectAsync(cancellationToken);
        DateTimeOffset cutoff = now - staleAfter;
        Dictionary<AvatarStorageKey, AvatarRepositoryAssetState> databaseByStorage =
            repository.Assets.ToDictionary(item => item.Asset.StorageKey);

        AvatarStagingEntry[] staleStaging = storage.Staging
            .Where(item => item.LastModifiedAt <= cutoff)
            .OrderBy(item => item.LastModifiedAt)
            .ToArray();
        AvatarAssetRecord[] abandonedPending = repository.Assets
            .Where(item => !item.IsActive
                && item.Asset.Status == AvatarAssetStatus.Pending
                && item.Asset.UpdatedAt <= cutoff)
            .Select(item => item.Asset)
            .OrderBy(item => item.UpdatedAt)
            .ToArray();
        AvatarPublishedEntry[] orphanedMedia = storage.Published
            .Where(item => IsOrphanedMedia(item, databaseByStorage))
            .OrderBy(item => item.LastModifiedAt)
            .ToArray();
        AvatarAssetRecord[] purgeableAssets = repository.Assets
            .Where(item => !item.IsActive
                && item.Asset.Status is AvatarAssetStatus.Retired or AvatarAssetStatus.Deleted
                && item.Asset.UpdatedAt <= cutoff)
            .Select(item => item.Asset)
            .OrderBy(item => item.UpdatedAt)
            .ToArray();

        return new AvatarCleanupPlan(
            staleStaging,
            abandonedPending,
            orphanedMedia,
            purgeableAssets);
    }

    private static bool IsOrphanedMedia(
        AvatarPublishedEntry published,
        IReadOnlyDictionary<AvatarStorageKey, AvatarRepositoryAssetState> databaseByStorage)
    {
        if (!databaseByStorage.TryGetValue(published.StorageKey, out AvatarRepositoryAssetState? state))
            return true;

        return !state.IsActive || state.Asset.Status != AvatarAssetStatus.Ready;
    }
}
