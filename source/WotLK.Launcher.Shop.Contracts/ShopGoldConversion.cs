namespace WotLK.Launcher.Shop.Contracts;

// Amounts are integers in copper and euro cents. Fractions of a cent stay on the character.
public sealed record ShopGoldConversionRate(uint CopperPerEuroCent)
{
    public ShopGoldConversionQuote Quote(uint offeredCopper)
    {
        if (CopperPerEuroCent == 0) throw new InvalidDataException("Invalid gold conversion rate.");
        long euroCents = offeredCopper / CopperPerEuroCent;
        uint debitedCopper = checked((uint)(euroCents * CopperPerEuroCent));
        return new(euroCents, debitedCopper, offeredCopper - debitedCopper);
    }
}

public sealed record ShopGoldConversionQuote(long CreditEuroCents, uint DebitedCopper, uint RemainingCopper);
