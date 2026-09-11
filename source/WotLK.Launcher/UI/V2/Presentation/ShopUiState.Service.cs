using System.Globalization;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed partial class ShopUiState
{
    // Store identities, never stale row instances or a payment authorization.
    private sealed record FundingReturn(string OfferId, uint CharacterGuid, string Currency, long PriceCents);
    private FundingReturn? _fundingReturn;
    private bool _servicePriceChanged;

    public string IncludedLabel => L("Ce service comprend", "What is included");
    public string PreservedLabel => L("Ce que vous conservez", "What you keep");
    public string OfferPreserved => _offer?.Offer.Preserved is { } text ? Text(text)
        : L("Les éléments conservés seront précisés avant l’ouverture du service.", "Retained elements will be detailed before the service opens.");
    public string ServicePreparationLabel => L("Préparer mon service", "Prepare my service");
    public string PurchaseActionLabel => IsPurchasing ? L("Enregistrement…", "Saving…") : _purchaseAttempt is not null ? L("Reprendre la commande", "Resume order") : L("Confirmer l’achat", "Confirm purchase");
    public string ServiceAvailabilityLabel => RenameAvailable ? L("Service disponible", "Service available") : L("Service bientôt disponible", "Service available soon");
    public string ServiceCurrencyLabel => L("Choisir une monnaie", "Choose a currency");
    public string ServiceReturnLabel => L("Retour au service", "Back to service");
    public string WalletHelpLabel => L("Deux soldes indépendants", "Two separate balances");
    public string WalletHelpDescription => L(
        "Le portefeuille reçoit vos recharges en euros. Les Crédits Atlas proviennent de l’or converti. Chaque opération alimente uniquement le solde correspondant ; les deux soldes ne se cumulent pas pour un achat.",
        "Wallet holds your euro top-ups. Atlas credits come from converted gold. Each operation adds funds only to its own balance; the two balances cannot be combined for a purchase.");

    private bool HasBoostLevelConflict => _offer?.Offer.Id == "character-level-70" && _character?.Character.Level >= 70;
    public string EligibilityTitle => _character is null ? L("Personnage à choisir", "Choose a character")
        : _offer?.Offer.Id == "character-rename" && _character.Character.RenamePending == true ? L("Renommage déjà en attente", "Name change already pending")
        : HasBoostLevelConflict ? L("Niveau 70 déjà atteint", "Already level 70 or above")
        : _character.Character.Online ? L("Personnage en ligne", "Character is online")
        : RenameAvailable && _character.Character.RenamePending == false ? L("Personnage disponible", "Character available") : L("Éligibilité à confirmer", "Eligibility to be confirmed");
    public string EligibilityDescription => !HasCharacters ? L("Aucun personnage sur ce compte.", "No characters on this account.")
        : _character is null ? L("Sélectionnez le personnage qui recevra ce service.", "Select the character that will receive this service.")
        : HasBoostLevelConflict ? L($"Ce personnage est déjà de niveau {_character.Character.Level}. Ce sésame vise le niveau 70.",
            $"This character is already level {_character.Character.Level}. This boost targets level 70.")
        : _character.Character.Online && _offer?.Offer.Id is "character-rename" or "character-race-change" or "character-faction-change"
            ? L("Déconnectez ce personnage avant d’utiliser le service. Les autres conditions restent à vérifier.",
                "Log out of this character before using the service. Other conditions still need to be checked.")
        : RenameAvailable ? _character.Character.RenamePending == true
            ? L("Terminez le changement de nom en jeu avant d’en acheter un autre.", "Complete the name change in game before buying another.")
            : L("Le serveur vérifiera à nouveau le personnage et le solde lors de l’achat.", "The server will check the character and balance again at purchase.")
        : _offer?.Offer.Id == "character-rename" ? L("Les règles de nommage et l’absence de renommage en attente devront être vérifiées avant l’achat.",
            "Naming rules and any pending name change must be checked before purchase.")
        : L("Les restrictions de ce service seront précisées avant l’ouverture.", "Service restrictions will be detailed before launch.");
    public string EligibilityColor => HasBoostLevelConflict ? "#F1A2A2" : _character?.Character.Online == true ? "#ECD096" : "#A9C7DA";

    public bool HasServiceFundingAction => _price?.MissingCents > 0;
    public bool CanPrepareServiceFunding => !_disposed && !IsLoading && CanEditPurchase && IsServiceOpen && HasServiceFundingAction
        && _character is not null && !HasBoostLevelConflict && _snapshot is not null;
    public string ServiceFundingAction => _price?.Price.Currency == "eur"
        ? L($"Ajouter les {FormatEuros(_price.MissingCents ?? 0)} manquants", $"Add the missing {FormatEuros(_price.MissingCents ?? 0)}")
        : L("Obtenir les crédits manquants", "Get the missing credits");
    public bool ShowServicePriceChange => _servicePriceChanged;
    public string ServicePriceChangeText => L("Le tarif a changé. Vérifiez le montant avant de poursuivre.", "The price has changed. Check the amount before continuing.");

    public bool HasFundingReturn => _fundingReturn is not null;
    public string FundingBackLabel => HasFundingReturn ? ServiceReturnLabel : BackToShopLabel;
    public string FundingContextText => _fundingReturn is { } origin && Offers.FirstOrDefault(o => o.Offer.Id == origin.OfferId) is { } offer
        ? L("Pour : ", "For: ") + offer.Name + (Characters.FirstOrDefault(c => c.Character.Guid == origin.CharacterGuid) is { } character ? " · " + character.Character.Name : "") : "";
    private long? FundingMissingCents => _fundingReturn is { } origin
        && Offers.FirstOrDefault(o => o.Offer.Id == origin.OfferId)?.Offer.Prices.FirstOrDefault(p => p.Currency == origin.Currency) is { } price
        && BalanceFor(origin.Currency) is long balance ? Math.Max(0, price.Amount - balance) : null;
    public bool ShowFundingConversionHint => _fundingReturn?.Currency == "credits";
    public string FundingConversionHint => FundingMissingCents is not long missing ? L("Le montant nécessaire sera recalculé après actualisation.", "The required amount will be recalculated after refresh.")
        : missing == 0 ? L("Les crédits nécessaires sont disponibles. Vous pouvez revenir au service.", "The required credits are available. You can return to the service.")
        : L($"Il manque {FormatEuros(missing)} de Crédits Atlas pour ce service. La saisie tient compte de l’or disponible sur le personnage source.",
            $"This service needs {FormatEuros(missing)} more in Atlas credits. The suggested amount is limited to the source character’s available gold.");

    internal void PrepareServiceFunding()
    {
        if (!CanPrepareServiceFunding || _offer is null || _price?.MissingCents is not long missing || _character is null) return;
        _fundingReturn = new(_offer.Offer.Id, _character.Character.Guid, _price.Price.Currency, _price.Price.Amount);
        if (_price.Price.Currency == "eur")
        {
            OpenWallet(preserveServiceReturn: true);
            SetWalletAmount(missing);
        }
        else
        {
            OpenConversion(preserveServiceReturn: true);
            // The source may differ from the beneficiary. Never change the beneficiary.
            if (_conversionCharacter?.Character is not { Online: false, GoldCopper: >= 10000 })
            {
                ShopCharacterRow? source = Characters.Where(c => !c.Character.Online && c.Character.GoldCopper >= 10000)
                    .OrderByDescending(c => c.Character.GoldCopper).FirstOrDefault();
                if (source is not null) SelectedConversionCharacter = source;
            }
            long requiredGold = checked((missing * _snapshot!.GoldConversion.CopperPerEuroCent + 9999) / 10000);
            long availableGold = (AvailableCopper ?? 0) / 10000;
            ConversionGold = availableGold > 0 ? Math.Min(requiredGold, availableGold).ToString(CultureInfo.InvariantCulture) : "";
        }
        Changed();
    }

    private void ReturnToService()
    {
        FundingReturn? origin = _fundingReturn;
        ClearFundingReturn();
        if (origin is null || _disposed || _snapshot is null) return;
        ShopOfferRow? offer = Offers.FirstOrDefault(o => o.Offer.Id == origin.OfferId);
        if (offer is null || !offer.Offer.Prices.Any(p => p.Currency == origin.Currency)) return;
        SelectedOffer = offer;
        _character = Characters.FirstOrDefault(c => c.Character.Guid == origin.CharacterGuid);
        _price = Prices.Single(p => p.Price.Currency == origin.Currency);
        _servicePriceChanged = _price.Price.Amount != origin.PriceCents;
        IsServiceOpen = true;
    }

    private void ClearFundingReturn() { _fundingReturn = null; _servicePriceChanged = false; }
}
