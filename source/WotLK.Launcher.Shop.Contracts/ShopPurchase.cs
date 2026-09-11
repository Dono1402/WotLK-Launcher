namespace WotLK.Launcher.Shop.Contracts;

public sealed record ShopCreateOrder(string IdempotencyKey, string OfferId, uint CharacterGuid,
    string Currency, long ExpectedAmountCents, string CatalogRevision);

public sealed record ShopOrder(string Id, string OfferId, uint CharacterGuid, string CharacterName,
    string Currency, long AmountCents, string Status, string? Reason, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string IdempotencyKey)
{
    public void Validate()
    {
        if (!ShopFundingValidation.IsId(Id) || !ShopFundingValidation.IsId(IdempotencyKey) || OfferId != "character-rename" || CharacterGuid == 0
            || string.IsNullOrWhiteSpace(CharacterName) || CharacterName.Length > 24
            || Currency is not ("eur" or "credits") || AmountCents is <= 0 or > ShopSnapshot.MaximumBalanceCents
            || Status is not ("pending" or "delivered" or "rejected" or "refunded")
            || Reason is not (null or "cancelled" or "character-unavailable" or "rename-already-pending")
            || CreatedAtUtc.Year is < 2000 or > 2200 || UpdatedAtUtc.Year is < 2000 or > 2200
            || CreatedAtUtc.Offset != TimeSpan.Zero || UpdatedAtUtc.Offset != TimeSpan.Zero || UpdatedAtUtc < CreatedAtUtc)
            throw new InvalidDataException("Invalid shop order.");
    }
}

public sealed record ShopPurchases(bool RenameAvailable, IReadOnlyList<ShopOrder> Orders)
{
    public void Validate()
    {
        if (Orders is null || Orders.Count > 100 || Orders.Any(o => o is null) || Orders.Select(o => o.Id).Distinct().Count() != Orders.Count)
            throw new InvalidDataException("Invalid shop orders.");
        foreach (ShopOrder order in Orders) order.Validate();
    }
}
