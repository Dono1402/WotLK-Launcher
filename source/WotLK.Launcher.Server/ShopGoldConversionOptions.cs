namespace WotLK.Launcher.Server;

internal sealed class ShopGoldConversionOptions
{
    public bool Enabled { get; set; }
    public uint RealmId { get; set; } = 1;
    internal bool StorageAvailable { get; set; }
    internal bool CanReadStorage => Enabled || StorageAvailable;
    internal void Validate(uint? ceiling)
    {
        if (RealmId == 0 || (Enabled && ceiling is < 14))
            throw new InvalidOperationException("Gold conversion requires schema 0014 and a valid realm.");
    }
}
