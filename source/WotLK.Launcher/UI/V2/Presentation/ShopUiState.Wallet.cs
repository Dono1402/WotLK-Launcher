using System.Globalization;
using System.Text.RegularExpressions;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class ShopPaymentMethodRow(string id) : ShopLocalizedRow
{
    private bool _manualFunding;
    public bool IsSelectable => !_manualFunding || Id == "paypal";
    internal void SetManualFunding(bool available) { _manualFunding=available; RefreshLocale(); }
    public string Id { get; } = id;
    public string Name => Id switch
    {
        "card" => ShopUiState.L("Carte bancaire", "Bank card"),
        "paypal" => "PayPal",
        _ => "Bancontact"
    };
    public string Description => Id switch
    {
        "card" => ShopUiState.L("Carte de débit ou de crédit", "Debit or credit card"),
        "paypal" => _manualFunding ? ShopUiState.L("Biens et services · validation manuelle", "Goods and services · manual approval") : ShopUiState.L("Avec votre compte PayPal", "With your PayPal account"),
        _ => ShopUiState.L("Avec votre banque", "With your bank")
    };
    public string Logo => "/WotLK.Launcher;component/Assets/Shop/Payment_" + (Id switch
    {
        "card" => "visa",
        "paypal" => "paypal",
        _ => "bancontact"
    }) + ".png";
    public string? SecondaryLogo => Id == "card" ? "/WotLK.Launcher;component/Assets/Shop/Payment_mastercard.png" : null;
}

internal sealed partial class ShopUiState
{
    private string _walletAmount = "";
    private ShopPaymentMethodRow? _paymentMethod;
    public bool IsWalletOpen { get; private set; }
    public string WalletLabel => L("Portefeuille", "Wallet");
    public string WalletHeading => L("Créditer mon portefeuille", "Add funds to my wallet");
    public string WalletSubtitle => L("Choisissez le montant à ajouter et votre moyen de paiement.", "Choose how much to add and your payment method.");
    public string WalletAmountLabel => L("Montant à ajouter", "Amount to add");
    public string WalletMethodLabel => L("Choisir un moyen de paiement", "Choose a payment method");
    public string WalletCurrentBalanceLabel => L("Solde du portefeuille", "Wallet balance");
    public string WalletAfterLabel => L("Solde après recharge", "Balance after top-up");
    public string WalletPaymentLabel => L("Moyen de paiement", "Payment method");
    public string WalletContinueLabel => IsFundingBusy ? L("Création de la demande…", "Creating request…")
        : HasPendingTopUp ? L("En attente de validation", "Awaiting approval")
        : ManualFundingAvailable ? L("Créer une demande de recharge", "Create a top-up request") : L("Continuer vers le paiement", "Continue to payment");
    public string WalletAvailability => IsFundingPreview ? L("Démonstration · aucun paiement réel", "Demo · no real payment")
        : ManualFundingAvailable ? L("PayPal · validation manuelle", "PayPal · manual approval") : L("Recharge bientôt disponible", "Wallet top-ups coming soon");
    public string ConversionShortcutLabel => L("Convertir mon or", "Convert my gold");
    public IReadOnlyList<ShopPaymentMethodRow> PaymentMethods { get; } = [new("card"), new("paypal"), new("bancontact")];
    public ShopPaymentMethodRow? SelectedPaymentMethod
    {
        get => _paymentMethod;
        set { if (IsFundingBusy || HasPendingTopUp || ReferenceEquals(value, _paymentMethod)) return; _paymentMethod = value is not null && value.IsSelectable && PaymentMethods.Contains(value) ? value : null; Changed(); }
    }
    public string WalletAmount
    {
        get => _walletAmount;
        set { if (value == _walletAmount) return; _walletAmount = value; Changed(); }
    }
    public long? WalletTopUpCents => TryParseWalletAmount(_walletAmount, out long cents) ? cents : null;
    public bool HasValidWalletAmount => WalletTopUpCents is > 0 and <= ShopSnapshot.MaximumBalanceCents
        && (!ManualFundingAvailable || (WalletTopUpCents >= _snapshot!.ManualFunding!.MinimumCents && WalletTopUpCents <= _snapshot.ManualFunding.MaximumCents))
        && (_snapshot?.EuroBalanceCents is not long balance || balance <= ShopSnapshot.MaximumBalanceCents - WalletTopUpCents.Value);
    public string WalletAmountDisplay => HasValidWalletAmount ? FormatEuros(WalletTopUpCents!.Value) : "—";
    public string WalletBalanceAfter => HasValidWalletAmount && _snapshot?.EuroBalanceCents is long balance
        ? FormatEuros(balance + Math.Max(0, WalletTopUpCents!.Value - (_snapshot.ManualFunding?.DebtCents ?? 0))) : "—";
    public string WalletPaymentName => _paymentMethod?.Name ?? L("À choisir", "Choose a method");
    public string WalletAmountHint => HasPendingTopUp ? TopUpPendingHint
        : ManualFundingAvailable ? TopUpLimits
        : _walletAmount.Length == 0 ? L("Saisissez le montant de votre recharge en euros.", "Enter your top-up amount in euros.")
        : !HasValidWalletAmount ? L("Saisissez un montant positif avec deux décimales maximum, dans la limite du portefeuille.", "Enter a positive amount with up to two decimal places, within the wallet limit.")
        : L("Ce montant sera ajouté à votre portefeuille après confirmation du paiement.", "This amount will be added to your wallet after payment confirmation.");
    // This only creates a pending server request. A browser return never grants money.
    public bool CanBeginWalletPayment => ManualFundingAvailable && HasValidWalletAmount && !HasPendingTopUp && !IsLoading && !IsFundingBusy
        && _paymentMethod?.Id == "paypal" && !_disposed;
    internal void OpenWallet(bool preserveServiceReturn = false)
    {
        if (_disposed) return;
        CloseAdminFunding();
        if (!preserveServiceReturn) ClearFundingReturn();
        IsHistoryOpen = false; IsConversionOpen = false; IsServiceOpen = false; IsWalletOpen = true; Changed();
    }
    internal void CloseWallet(bool returnToService = false)
    {
        bool wasOpen = IsWalletOpen; IsWalletOpen = false;
        if (wasOpen && returnToService) ReturnToService(); else ClearFundingReturn();
        Changed();
    }
    internal void SetWalletAmount(long cents) => WalletAmount = (cents / 100m).ToString("0.##", Culture);
    internal static bool TryParseWalletAmount(string text, out long cents)
    {
        cents = 0;
        if (!Regex.IsMatch(text, @"\A[0-9]{1,8}(?:[.,][0-9]{1,2})?\z", RegexOptions.CultureInvariant)
            || !decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount)
            || amount <= 0 || amount > ShopSnapshot.MaximumBalanceCents / 100m) return false;
        cents = decimal.ToInt64(amount * 100m); return true;
    }
}
