using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class ShopHistoryFilterRow(string id, ShopText label) : ShopLocalizedRow
{
    public string Id { get; } = id;
    public string Label => ShopUiState.Text(label);
}

internal sealed class ShopHistoryRow(ShopTransaction transaction) : ShopLocalizedRow
{
    public ShopTransaction Transaction { get; } = transaction;
    public string Title => Transaction.Kind switch
    {
        "top-up" => ShopUiState.L("Recharge du portefeuille", "Wallet top-up"),
        "conversion" => ShopUiState.L("Conversion d’or", "Gold conversion"),
        "refund" => ShopUiState.L("Remboursement", "Refund"),
        _ => ShopUiState.L("Achat en boutique", "Shop purchase")
    };
    public string Description => ShopUiState.Text(Transaction.Description)
        + (Transaction.GoldCopper is uint gold ? " · " + ShopUiState.FormatGoldNumber(gold) + ShopUiState.L(" pièces d’or", " gold coins") : "");
    public string Detail => Transaction.OccurredAtUtc.ToLocalTime().ToString("dd MMM yyyy · HH:mm", ShopUiState.Culture)
        + (Transaction.CharacterName is { } name ? " · " + name : "");
    public string Amount => (Transaction.AmountCents > 0 ? "+" : "−") + ShopUiState.FormatEuros(Math.Abs(Transaction.AmountCents));
    public bool IsCredits => Transaction.Currency == "credits";
    public string CurrencyLabel => IsCredits ? ShopUiState.L("Crédits Atlas", "Atlas credits") : ShopUiState.L("Portefeuille", "Wallet");
    public string CurrencyColor => IsCredits ? "#EDD18B" : "#A9DCFA";
    public string BalanceAfter => Transaction.BalanceAfterCents is long amount ? ShopUiState.L("Solde : ", "Balance: ") + ShopUiState.FormatEuros(amount) : "";
    public string Status => Transaction.Status switch
    {
        "completed" => ShopUiState.L("Terminé", "Completed"),
        "pending" => ShopUiState.L("En attente", "Pending"),
        "failed" => ShopUiState.L("Échoué", "Failed"),
        _ => ShopUiState.L("Annulé", "Cancelled")
    };
    public string StatusColor => Transaction.Status switch { "completed" => "#A0DFCE", "pending" => "#ECD096", "failed" => "#F1A2A2", _ => "#A8BECE" };
    public string StatusBackground => Transaction.Status switch { "completed" => "#233AA98C", "pending" => "#237E6530", "failed" => "#23552A2A", _ => "#23344C61" };
    public string Icon => Transaction.Kind switch
    {
        "top-up" => "M3,5 H21 V19 H3 Z M3,9 H21 M8,14 H16 M12,11 V17",
        "conversion" => "M4,7 H18 L14,3 M18,7 L14,11 M20,17 H6 L10,13 M6,17 L10,21",
        "refund" => "M7,6 H15 A7,7 0 0 1 15,20 H10 M7,6 L11,2 M7,6 L11,10",
        _ => "M2,3 H5 L8,15 H18 L21,7 H6 M9,20 A1,1 0 1 1 8.999,20 M18,20 A1,1 0 1 1 17.999,20"
    };
}

internal sealed partial class ShopUiState
{
    private bool _historyReturnToWallet;
    private ShopHistoryFilterRow? _historyKind;
    private ShopHistoryFilterRow? _historyWallet;
    public bool IsHistoryOpen { get; private set; }
    public bool HistoryAvailable => _snapshot?.History is not null;
    public string HistoryLabel => L("Historique", "History");
    public string HistoryHeading => L("Historique des opérations", "Transaction history");
    public string HistorySubtitle => L("Retrouvez vos recharges, conversions et achats, monnaie par monnaie.", "Review your top-ups, conversions and purchases, wallet by wallet.");
    public string HistoryBackLabel => _historyReturnToWallet ? L("Retour au portefeuille", "Back to wallet") : BackToShopLabel;
    public string HistoryKindLabel => L("Type d’opération", "Operation type");
    public string HistoryWalletLabel => L("Monnaie", "Currency");
    public IReadOnlyList<ShopHistoryRow> HistoryRows { get; private set; } = [];
    public IReadOnlyList<ShopHistoryFilterRow> HistoryKindFilters { get; } =
    [new("all", new("Toutes les opérations", "All operations")), new("top-up", new("Recharges", "Top-ups")),
        new("conversion", new("Conversions", "Conversions")), new("purchase", new("Achats", "Purchases")), new("refund", new("Remboursements", "Refunds"))];
    public IReadOnlyList<ShopHistoryFilterRow> HistoryWalletFilters { get; } =
    [new("all", new("Toutes les monnaies", "All currencies")), new("eur", new("Portefeuille", "Wallet")), new("credits", new("Crédits Atlas", "Atlas credits"))];
    public ShopHistoryFilterRow SelectedHistoryKind
    {
        get => _historyKind ?? HistoryKindFilters[0];
        set { _historyKind = HistoryKindFilters.Contains(value) ? value : null; Changed(); }
    }
    public ShopHistoryFilterRow SelectedHistoryWallet
    {
        get => _historyWallet ?? HistoryWalletFilters[0];
        set { _historyWallet = HistoryWalletFilters.Contains(value) ? value : null; Changed(); }
    }
    public IReadOnlyList<ShopHistoryRow> FilteredHistory => HistoryRows.Where(row =>
        (SelectedHistoryKind.Id == "all" || row.Transaction.Kind == SelectedHistoryKind.Id)
        && (SelectedHistoryWallet.Id == "all" || row.Transaction.Currency == SelectedHistoryWallet.Id)).ToArray();
    public string HistoryCountLabel => !HistoryAvailable ? "—" : FilteredHistory.Count + (FilteredHistory.Count == 1 ? L(" opération", " operation") : L(" opérations", " operations"));
    public bool ShowHistoryEmpty => !IsLoading && FilteredHistory.Count == 0;
    public bool ShowHistoryLimit => HistoryRows.Count == ShopSnapshot.MaximumHistoryEntries;
    public string HistoryLimitLabel => L("Les 100 opérations les plus récentes sont affichées.", "Showing the 100 most recent operations.");
    public string HistoryEmptyHeading => !HistoryAvailable ? L("Historique bientôt disponible", "History available soon")
        : HistoryRows.Count == 0 ? L("Aucune opération pour le moment", "No operations yet") : L("Aucune opération correspondante", "No matching operations");
    public string HistoryEmptyDescription => !HistoryAvailable ? L("Vos mouvements seront consultables ici dès l’ouverture des opérations sur le royaume.", "Your transactions will appear here when operations open on the realm.")
        : HistoryRows.Count == 0 ? L("Vos prochaines recharges, conversions et achats apparaîtront ici.", "Your next top-ups, conversions and purchases will appear here.")
        : L("Essayez un autre type d’opération ou une autre monnaie.", "Try a different operation type or currency.");
    internal void OpenHistory()
    {
        if (_disposed || IsHistoryOpen) return;
        if (!IsWalletOpen) ClearFundingReturn();
        _historyReturnToWallet = IsWalletOpen;
        IsConversionOpen = false; IsServiceOpen = false; IsWalletOpen = false; IsHistoryOpen = true; Changed();
    }
    internal void CloseHistory(bool returnToOrigin = false)
    {
        bool wasOpen = IsHistoryOpen; IsHistoryOpen = false;
        if (returnToOrigin && wasOpen && _historyReturnToWallet) IsWalletOpen = true;
        Changed();
    }
    private void RefreshHistoryRows() => HistoryRows = (_snapshot?.History ?? []).OrderByDescending(t => t.OccurredAtUtc).Select(t => new ShopHistoryRow(t)).ToArray();
    private void ResetHistory() { IsHistoryOpen = false; _historyReturnToWallet = false; _historyKind = _historyWallet = null; HistoryRows = []; }
}
