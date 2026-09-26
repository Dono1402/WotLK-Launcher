namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed partial class ShopUiState
{
    public string PageHeading => Title;
    public string CatalogueHeading => L("CATALOGUE", "CATALOGUE");
    public string CreditsHeading => CreditsLabel.ToUpper(Culture);
    public string SecureLabel => L("Simple et sécurisé", "Simple and secure");
    public string OfficialLabel => L("Service officiel", "Official service");
    public string RealmSupportLabel => L("Soutient le royaume", "Supports the realm");
    public string ServicesHeading => L("Nos services", "Our services");
    public string ServicesSubtitle => L("Personnalisez votre expérience de jeu avec nos services officiels.", "Customize your game experience with our official services.");
    public string SoonLabel => L("Bientôt disponible", "Available soon");
    public string UpcomingLabel => L("Prochainement", "Coming soon");
    public string SelectionHeading => L("VOTRE SÉLECTION", "YOUR SELECTION");
    public string BeneficiaryLabel => L("Personnage bénéficiaire", "Beneficiary character");
    public string PaymentLabel => L("Moyen de paiement", "Payment method");
    public string RecapHeading => L("Récapitulatif", "Order summary");
    public string SummaryCharacter => UsesAccountService ? L("Pour votre compte · personnage à choisir en jeu", "For your account · choose a character in game")
        : L("Personnage : ", "Character: ") + (_character?.Character.Name ?? "—");
    public string TotalLabel => L("Total", "Total");
    public string ConvertGoldLabel => L("Convertir de l’or", "Convert gold");
    public string ConversionHeading => L("Convertir mon or en Crédits Atlas", "Convert my gold into Atlas credits");
    public string MoreLabel => L("En savoir plus", "Learn more");
    public string RefreshLabel => L("Actualiser la boutique", "Refresh shop");
    public string Motto => L("L E  F R O I D\nN E  M E U R T\nJ A M A I S", "T H E  C O L D\nN E V E R\nD I E S");
}
