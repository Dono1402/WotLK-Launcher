using System.Globalization;
using System.Text.RegularExpressions;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal sealed class ShopManualFundingOptions
{
    public bool Enabled { get; set; }
    public string PayPalMeName { get; set; } = "";
    public uint[] AdministratorAccountIds { get; set; } = [];
    public long MinimumCents { get; set; } = 100;
    public long MaximumCents { get; set; } = 5_000;
    public long DailyMaximumCents { get; set; } = 10_000;
    // Set only after the configured schema has been migrated and verified.
    internal bool StorageAvailable { get; set; }
    internal bool PurchaseStorageAvailable { get; set; }
    internal bool CanReadStorage => Enabled || StorageAvailable;

    internal void Validate(uint? maximumSchemaVersion)
    {
        new ShopManualFunding(Enabled, false, MinimumCents, MaximumCents, DailyMaximumCents, 0, 0, []).Validate();
        if (AdministratorAccountIds is null || AdministratorAccountIds.Length > 32 || AdministratorAccountIds.Contains(0u))
            throw new InvalidOperationException("AtlasShop:ManualPayPal administrator account IDs are invalid.");
        if (Enabled && (maximumSchemaVersion is < 11 || AdministratorAccountIds.Length == 0
            || !Regex.IsMatch(PayPalMeName, "\\A[A-Za-z0-9]{2,20}\\z", RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("Manual PayPal funding requires schema 0011, a PayPal.Me name and explicit administrator account IDs.");
    }

    internal bool CanAdminister(uint accountId) => CanReadStorage && AdministratorAccountIds.Contains(accountId);
    internal string PaymentUrl(long amountCents) => "https://paypal.me/" + PayPalMeName + "/"
        + (amountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "EUR";
}
