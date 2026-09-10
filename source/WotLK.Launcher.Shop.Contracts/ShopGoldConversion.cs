namespace WotLK.Launcher.Shop.Contracts;

// Amounts are integers in copper and euro cents. Fractions of a cent stay on the character.
public sealed record ShopGoldConversionRate(uint CopperPerEuroCent)
{
    // Atlas: one whole gold coin per euro cent, i.e. 100 gold = EUR 1.
    public static ShopGoldConversionRate Default { get; } = new(10_000);

    public ShopGoldConversionQuote Quote(uint offeredCopper)
    {
        if (CopperPerEuroCent == 0) throw new InvalidDataException("Invalid gold conversion rate.");
        long euroCents = offeredCopper / CopperPerEuroCent;
        uint debitedCopper = checked((uint)(euroCents * CopperPerEuroCent));
        return new(euroCents, debitedCopper, offeredCopper - debitedCopper);
    }
}

public sealed record ShopGoldConversionQuote(long CreditEuroCents, uint DebitedCopper, uint RemainingCopper);
