using System.Globalization;
using System.Text.RegularExpressions;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed record ShopCreditChange(long BeforeCents, long AfterCents, uint CharacterGuid, uint DebitedCopper);

internal sealed partial class ShopUiState
{
    private ShopSnapshot? _previewSnapshot;
    private ShopCharacterRow? _conversionCharacter;
    private uint? _conversionCharacterId;
    private ShopCreditChange? _lastConversion;
    internal object? ConversionSnapshot => _snapshot;
    public bool HasConversionReceipt => _lastConversion is not null;
    internal event Action<ShopCreditChange>? CreditGranted;
    public bool IsConversionOpen { get; private set; }
    public bool IsConversionPreview => _previewSnapshot is not null;
    public string EuroBalance => _snapshot?.EuroBalanceCents is long cents ? FormatEuros(cents) : "—";
    public string EuroWalletLabel => L("Portefeuille en euros", "Euro wallet");
    public string PriceWalletsLabel => L("Crédits Atlas / Euros", "Atlas credits / Euros");
    public string SelectedAmount => _price is null ? "—" : FormatEuros(_price.Price.Amount);
    public string ConvertAction => L("Convertir", "Convert");
    public string ConversionSourceLabel => L("Personnage source", "Source character");
    public string AvailableGoldLabel => L("Or disponible", "Available gold");
    public string GoldAmountLabel => L("Montant à convertir", "Amount to convert");
    public string GoldRemainingLabel => L("Or restant", "Remaining gold");
    public string CreditReceivedLabel => L("Crédits Atlas reçus", "Atlas credits received");
    public string CurrentBalanceLabel => HasConversionReceipt ? L("Solde précédent", "Previous balance") : L("Solde actuel", "Current balance");
    public string NewBalanceLabel => L("Nouveau solde", "New balance");
    public string CancelLabel => HasConversionReceipt ? L("Terminé", "Done") : L("Annuler", "Cancel");
    public string CloseLabel => L("Fermer", "Close");
    public string BackToShopLabel => L("Retour à la boutique", "Back to shop");
    public string ConversionSubtitle => L("Choisissez un personnage et le montant d’or à convertir.", "Choose a character and the amount of gold to convert.");
    public string ConversionModeHint => IsConversionPreview
        ? L("Prévisualisation · personnages et soldes fictifs", "Preview · example characters and balances")
        : L("La conversion ouvrira une fois le service disponible sur le royaume.", "Conversion will open once the service is available on the realm.");
    public string SeparateWalletHint => L("Le portefeuille en euros reste indépendant de vos Crédits Atlas.", "Your euro wallet is separate from your Atlas credits.");
    public string ConversionGold
    {
        get => _conversionGold;
        set { if (_conversionGold == value) return; _lastConversion = null; _conversionGold = value; Changed(); }
    }
    public ShopCharacterRow? SelectedConversionCharacter
    {
        get => _conversionCharacter;
        set
        {
            if (ReferenceEquals(value, _conversionCharacter)) return;
            _lastConversion = null;
            _conversionCharacter = value is not null && Characters.Contains(value) ? value : null;
            _conversionCharacterId = _conversionCharacter?.Character.Guid;
            if (AvailableCopper is uint available && RequestedCopper is uint requested && requested > available)
                _conversionGold = FormatGoldNumber(available);
            Changed();
        }
    }
    public uint? AvailableCopper => _conversionCharacter?.Character is { Online: false, GoldCopper: uint gold } ? gold : null;
    public bool CanPreviewConversion => !_disposed && !IsLoading && AvailableCopper is not null;
    public bool HasConvertibleGold => CanPreviewConversion && AvailableCopper >= 10000 && AvailableCopper >= (_snapshot?.GoldConversion.CopperPerEuroCent ?? uint.MaxValue);
    internal uint? RequestedCopper => TryParseGold(_conversionGold, out uint copper) ? copper : null;
    public ShopGoldConversionQuote? ConversionQuote => _snapshot is not null && RequestedCopper is uint copper
        ? _snapshot.GoldConversion.Quote(copper) : null;
    public bool HasValidConversionAmount => CanPreviewConversion && ConversionQuote is { CreditEuroCents: > 0 }
        && RequestedCopper <= AvailableCopper
        && _snapshot?.CreditBalanceEuroCents is long balance && balance <= ShopSnapshot.MaximumBalanceCents - ConversionQuote.CreditEuroCents;
    public bool CanConvert => IsConversionOpen && IsConversionPreview && HasValidConversionAmount;
    public string ConversionMaximum => AvailableCopper is uint available ? FormatGoldNumber(available) : "—";
    public string ConversionCredit => _lastConversion is { } receipt ? FormatEuros(receipt.AfterCents - receipt.BeforeCents) : ConversionQuote is { } quote ? FormatEuros(quote.CreditEuroCents) : "—";
    public string ConversionBalanceBefore => _lastConversion is { } receipt ? FormatEuros(receipt.BeforeCents) : CreditBalance;
    public string ConversionDebit => ConversionQuote is { } quote ? FormatGoldNumber(quote.DebitedCopper) : "—";
    public string ConversionRemainder => ConversionQuote is { } quote ? FormatGoldNumber(quote.RemainingCopper) : "—";
    public string ConversionGoldAfter => _lastConversion is not null ? ConversionMaximum : HasValidConversionAmount ? FormatGoldNumber(AvailableCopper!.Value - ConversionQuote!.DebitedCopper) : "—";
    public string ConversionBalanceAfter => _lastConversion is not null ? CreditBalance : HasValidConversionAmount ? FormatEuros(_snapshot!.CreditBalanceEuroCents!.Value + ConversionQuote!.CreditEuroCents) : "—";
    public string ConversionRateNumber => _snapshot is null ? "—" : FormatGoldNumber(_snapshot.GoldConversion.CopperPerEuroCent * 100L);
    public string ConversionRate => ConversionRateNumber + " = " + FormatEuros(100);
    public double ConversionPercent => AvailableCopper is >= 10000 && RequestedCopper is uint copper
        ? Math.Clamp(copper * 100d / (AvailableCopper.Value / 10000 * 10000), 0, 100) : 0;
    public string ConversionHint => _lastConversion is not null ? L("Conversion effectuée. Vous pouvez saisir un nouveau montant.", "Conversion complete. You can enter another amount.")
        : _conversionCharacter is null ? L("Choisissez un personnage.", "Choose a character.")
        : AvailableCopper is null ? L("Or indisponible pour ce personnage connecté. Déconnectez-le puis actualisez la boutique.", "Gold is unavailable for this online character. Log out of the character, then refresh the shop.")
        : AvailableCopper < 10000 ? L("Ce personnage ne possède pas de pièce d’or entière.", "This character has no whole gold coins.")
        : RequestedCopper is null ? L("Saisissez un nombre entier de pièces d’or.", "Enter a whole number of gold coins.")
        : RequestedCopper > AvailableCopper ? L("Le montant dépasse le solde du personnage.", "The amount exceeds the character’s balance.")
        : ConversionQuote is not { CreditEuroCents: > 0 } ? L("Minimum : 1 pièce d’or.", "Minimum: 1 gold coin.")
        : _snapshot?.CreditBalanceEuroCents is null ? L("Le solde de Crédits Atlas est indisponible.", "The Atlas credit balance is unavailable.")
        : !HasValidConversionAmount ? L("Le plafond de Crédits Atlas serait dépassé.", "The Atlas credit limit would be exceeded.")
        : L("Le montant est calculé au centime. Le reste de l’or est conservé sur le personnage.", "The amount is calculated to the cent. Remaining gold stays on your character.");

    internal void OpenConversion()
    {
        if (_disposed) return;
        if (_conversionCharacter is null && _conversionCharacterId is null)
            SelectedConversionCharacter = SelectedCharacter ?? Characters.FirstOrDefault(c => !c.Character.Online && c.Character.GoldCopper is > 0) ?? Characters.FirstOrDefault();
        IsConversionOpen = true; Changed();
    }
    internal void CloseConversion() { IsConversionOpen = false; Changed(); }
    internal void SetConversionPercent(int percent)
    {
        if (AvailableCopper is not uint available || !CanPreviewConversion) return;
        ConversionGold = (available / 10000 * (uint)Math.Clamp(percent, 0, 100) / 100).ToString(CultureInfo.InvariantCulture);
    }
    // Only the explicitly isolated preview route may enable this in-memory demonstration.
    // Runtime Configure() clears the preview capability; no HTTP/SQL mutation is fabricated.
    internal void ConfigurePreview(ShopSnapshot snapshot)
    {
        snapshot.Validate();
        ResetSession();
        Configure(_ => Task.FromResult(_previewSnapshot ?? throw new InvalidOperationException("Preview session ended.")));
        _previewSnapshot = snapshot; Changed();
    }
    internal bool TryConvertPreview()
    {
        if (!CanConvert || _previewSnapshot is null || _snapshot is null || _conversionCharacter is null) return false;
        ShopGoldConversionQuote quote = ConversionQuote!;
        uint characterId = _conversionCharacter.Character.Guid;
        long before = _snapshot.CreditBalanceEuroCents!.Value;
        ShopSnapshot result = _snapshot with
        {
            CreditBalanceEuroCents = checked(before + quote.CreditEuroCents),
            Characters = _snapshot.Characters.Select(c => c.Guid == characterId ? c with { GoldCopper = checked(c.GoldCopper!.Value - quote.DebitedCopper) } : c).ToArray()
        };
        result.Validate();
        _previewSnapshot = _snapshot = result;
        // Preserve bound row identities: replacing ItemsSource during this notification
        // would let WPF clear the beneficiary, price and offer through two-way bindings.
        _conversionCharacter.Update(result.Characters.Single(c => c.Guid == characterId));
        _lastConversion = new(before, result.CreditBalanceEuroCents!.Value, characterId, quote.DebitedCopper);
        _conversionGold = ""; Changed();
        CreditGranted?.Invoke(_lastConversion);
        return true;
    }
    internal static bool IsNumericGoldInput(string text) => Regex.IsMatch(text, @"\A[0-9]{0,6}\z", RegexOptions.CultureInvariant);
    internal static bool TryParseGold(string text, out uint copper)
    {
        copper = 0;
        if (!Regex.IsMatch(text, @"\A[0-9]{1,6}\z", RegexOptions.CultureInvariant)
            || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint gold)
            || gold > uint.MaxValue / 10000) return false;
        copper = gold * 10000; return true;
    }
    internal static string FormatGoldNumber(long copper) => (copper / 10000).ToString("0", Culture);
}
