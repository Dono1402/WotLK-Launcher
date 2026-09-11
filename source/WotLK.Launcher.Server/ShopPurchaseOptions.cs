namespace WotLK.Launcher.Server;

internal sealed class ShopPurchaseOptions
{
    public bool RenameEnabled { get; set; }
    public uint RealmId { get; set; } = 1;
    internal bool StorageAvailable { get; set; }
    internal bool CanReadStorage => RenameEnabled || StorageAvailable;
    internal void Validate(uint? ceiling)
    {
        if (RealmId == 0 || (RenameEnabled && ceiling is < 12))
            throw new InvalidOperationException("Shop rename purchases require schema 0012.");
    }
}
