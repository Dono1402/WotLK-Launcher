namespace WotLK.Launcher.Shop.Contracts;

public sealed record ShopCreateGoldConversion(string IdempotencyKey, uint CharacterGuid,
    uint OfferedCopper, long ExpectedCreditCents, string CatalogRevision);

public sealed record ShopGoldConversion(string Id, string IdempotencyKey, uint CharacterGuid,
    string CharacterName, uint OfferedCopper, long CreditEuroCents, string Status, string? Reason,
    uint? GoldBeforeCopper, uint? GoldAfterCopper, long? CreditBeforeCents, long? CreditAfterCents,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
{
    public void Validate()
    {
        if (!ShopFundingValidation.IsId(Id) || !ShopFundingValidation.IsId(IdempotencyKey)
            || CharacterGuid == 0 || string.IsNullOrWhiteSpace(CharacterName) || CharacterName.Length > 24
            || OfferedCopper == 0 || OfferedCopper % 10_000 != 0
            || CreditEuroCents is <= 0 or > ShopSnapshot.MaximumBalanceCents
            || Status is not ("pending" or "completed" or "rejected")
            || Reason is { Length: > 40 } || (Status == "rejected") != !string.IsNullOrWhiteSpace(Reason)
            || CreatedAtUtc == default || UpdatedAtUtc == default)
            throw new InvalidDataException("Invalid gold conversion.");
        if (Status == "completed")
        {
            if (GoldBeforeCopper is null || GoldAfterCopper is null || GoldBeforeCopper < OfferedCopper
                || GoldBeforeCopper - OfferedCopper != GoldAfterCopper
                || CreditBeforeCents is null or < 0 or > ShopSnapshot.MaximumBalanceCents
                || CreditAfterCents is null or < 0 or > ShopSnapshot.MaximumBalanceCents
                || CreditBeforeCents + CreditEuroCents != CreditAfterCents)
                throw new InvalidDataException("Invalid gold conversion receipt.");
        }
        else if (GoldBeforeCopper is not null || GoldAfterCopper is not null
            || CreditBeforeCents is not null || CreditAfterCents is not null)
            throw new InvalidDataException("An unfinished conversion cannot advertise a debit.");
    }
}

public sealed record ShopGoldConversions(bool Available, IReadOnlyList<ShopGoldConversion> Requests)
{
    public void Validate()
    {
        if (Requests is null || Requests.Count > 100) throw new InvalidDataException("Invalid conversion list.");
        HashSet<string> ids = [], keys = [];
        foreach (ShopGoldConversion item in Requests)
        {
            if (item is null || !ids.Add(item.Id) || !keys.Add(item.IdempotencyKey))
                throw new InvalidDataException("Invalid conversion identity.");
            item.Validate();
        }
        if (Requests.Count(r => r.Status == "pending") > 1)
            throw new InvalidDataException("Multiple pending conversions.");
    }
}
