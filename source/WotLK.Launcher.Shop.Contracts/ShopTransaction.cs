namespace WotLK.Launcher.Shop.Contracts;

// A historical amount is signed and always belongs to exactly one wallet.
// Reading these records never applies a balance change. Null snapshot History
// means unavailable; an empty list means available with no recorded operations.
public sealed record ShopTransaction(string Id, DateTimeOffset OccurredAtUtc, string Kind,
    string Currency, long AmountCents, string Status, ShopText Description,
    string? CharacterName = null, long? BalanceAfterCents = null, uint? GoldCopper = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 120
            || OccurredAtUtc.Year is < 2000 or > 2200 || OccurredAtUtc.Offset != TimeSpan.Zero
            || Kind is not ("top-up" or "conversion" or "purchase" or "refund")
            || Currency is not ("eur" or "credits")
            || AmountCents == 0 || AmountCents is < -ShopSnapshot.MaximumBalanceCents or > ShopSnapshot.MaximumBalanceCents
            || Status is not ("completed" or "pending" or "failed" or "cancelled")
            || Description is null || string.IsNullOrWhiteSpace(Description.Fr) || Description.Fr.Length > 200
            || string.IsNullOrWhiteSpace(Description.En) || Description.En.Length > 200
            || (CharacterName is not null && (string.IsNullOrWhiteSpace(CharacterName) || CharacterName.Length > 24))
            || BalanceAfterCents is < 0 or > ShopSnapshot.MaximumBalanceCents
            || (Status != "completed" && BalanceAfterCents is not null)
            || (Kind == "purchase" ? AmountCents > 0 : AmountCents < 0)
            || (Kind == "top-up" && Currency != "eur")
            || (Kind == "conversion" && (Currency != "credits" || GoldCopper is null or 0))
            || (Kind != "conversion" && GoldCopper is not null))
            throw new InvalidDataException("Invalid shop transaction.");
    }
}
