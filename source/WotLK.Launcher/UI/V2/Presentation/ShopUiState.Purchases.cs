using System.Net;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed record ShopPurchaseActions(Func<ShopCreateOrder, CancellationToken, Task<ShopOrder>> Create,
    Func<string, CancellationToken, Task<ShopOrder>> Cancel);

internal sealed class ShopOrderRow(ShopOrder order)
{
    public ShopOrder Order { get; } = order;
    public string Id => Order.Id;
    public bool CanCancel => Order.Status is "available" or "pending" or "rejected";
    public string Summary => ShopUiState.OrderSummary(Order);
    public string Detail => ShopUiState.FormatPrice(new(Order.Currency, Order.AmountCents)) + " · " + Order.Id[..8].ToUpperInvariant();
    public string CancelLabel => ShopUiState.L("Annuler et recréditer", "Cancel and restore funds");
}

internal sealed partial class ShopUiState
{
    private ShopPurchaseActions? _purchaseActions;
    private ShopCreateOrder? _purchaseAttempt;
    private CancellationTokenSource? _purchasePending;
    private CancellationTokenSource? _purchaseReadPending;
    private long _purchaseSession;
    private ShopText? _purchaseNotice;
    public bool IsPurchasing { get; private set; }
    public bool IsPurchaseReading { get; private set; }
    public bool CanEditPurchase => !IsPurchasing && !IsPurchaseReading && _purchaseAttempt is null;
    public bool UsesAccountService => _snapshot?.Purchases?.AccountServices == true && _offer?.Offer.Id == "character-rename";
    public bool ShowPurchaseCharacter => !UsesAccountService;
    public bool CanChoosePurchaseCharacter => ShowPurchaseCharacter && HasCharacters && CanEditPurchase;
    public bool CanManagePurchaseOrders => CanEditPurchase && !IsLoading && !IsFundingBusy && _purchaseActions is not null;
    private bool RenameAvailable => _snapshot is { CheckoutAvailable: true, Purchases.RenameAvailable: true } && _offer?.Offer.Id == "character-rename";
    public bool CanPurchase => !_disposed && !IsPurchasing && !IsPurchaseReading && !IsLoading && !IsFundingBusy && _purchaseActions is not null
        && (_purchaseAttempt is not null || (RenameAvailable && (UsesAccountService
            ? AvailableServiceCount < 100 : _character?.Character is { Online: false, RenamePending: false })
            && _price?.MissingCents == 0 && (_snapshot?.ManualFunding?.DebtCents ?? 0) == 0));
    public int AvailableServiceCount => _snapshot?.Purchases?.Orders.Count(o => o.Status == "available" && o.OfferId == _offer?.Offer.Id) ?? 0;
    private ShopOrder? SelectedOrder => _snapshot?.Purchases?.Orders.FirstOrDefault(o => (UsesAccountService || o.CharacterGuid == _character?.Character.Guid) && o.OfferId == _offer?.Offer.Id);
    public bool CanCancelPurchase => !IsPurchasing && !IsPurchaseReading && !IsLoading && !IsFundingBusy && _purchaseAttempt is null && _purchaseActions is not null && SelectedOrder?.Status is "available" or "pending" or "rejected";
    public bool ShowPurchaseReceipt => SelectedOrder is not null || _purchaseNotice is not null;
    public string PurchaseReceipt => (SelectedOrder is { } order
        ? OrderSummary(order) + "\n" + FormatPrice(new(order.Currency, order.AmountCents)) + " · " + order.Id[..8].ToUpperInvariant() : "")
        + (_purchaseNotice is not null ? (SelectedOrder is not null ? "\n" : "") + Text(_purchaseNotice) : "");
    public string PurchaseOrdersSummary => string.Join("\n", (_snapshot?.Purchases?.Orders ?? []).Take(5).Select(OrderSummary));
    public bool HasPurchaseOrders => _snapshot?.Purchases?.Orders.Count > 0;
    public IReadOnlyList<ShopOrderRow> PurchaseOrderRows => (_snapshot?.Purchases?.Orders ?? []).Select(o => new ShopOrderRow(o)).ToArray();
    public string PurchaseOrdersHeading => L("Suivi des services · ", "Service orders · ") + (_snapshot?.Purchases?.Orders.Count ?? 0);
    internal bool NeedsPurchaseRefresh => CanRefresh && _purchaseActions is not null
        && (_purchaseAttempt is not null || _snapshot?.Purchases?.Orders.Any(o => o.Status is "available" or "pending" or "rejected") == true);
    public string CancelPurchaseLabel => L("Annuler et recréditer", "Cancel and restore funds");
    public string RefreshPurchaseLabel => L("Actualiser le suivi", "Refresh order status");
    public string PurchaseHint => UsesAccountService
        ? L("Vous pouvez continuer à jouer après l’achat. Pour utiliser le service, revenez quand vous le souhaitez à la sélection des personnages et cliquez sur son icône. Annulation possible avant utilisation.",
            "You can keep playing after your purchase. To use the service, return to character selection whenever you wish and click its icon. You can cancel before use.")
        : SelectedOrder?.Status == "delivered" && _character?.Character.RenamePending == true
        ? L("L’activation est confirmée. Connectez-vous au royaume puis choisissez le nouveau nom à la sélection de ce personnage.",
            "Activation is confirmed. Connect to the realm and choose the new name on the character selection screen.")
        : RenameAvailable || SelectedOrder is not null
        ? L("Revenez à l’écran de connexion du jeu jusqu’à confirmation de l’activation. Choisissez ensuite le nouveau nom en jeu. Annulation possible avant activation.",
            "Return to the game’s sign-in screen until activation is confirmed. Then choose the new name in game. You can cancel before activation.")
        : L("Les achats ouvriront une fois le service disponible sur le royaume.", "Purchases will open once the service is available on the realm.");
    internal static string OrderStatus(string status) => status switch
    {
        "available" => L("Disponible sur le compte", "Available on the account"),
        "consumed" => L("Service utilisé", "Service used"),
        "pending" => L("Activation en attente de déconnexion", "Activation awaiting realm disconnection"),
        "delivered" => L("Activation livrée", "Activation delivered"),
        "rejected" => L("Activation impossible · remboursement en cours", "Activation unavailable · refund pending"),
        _ => L("Remboursé", "Refunded")
    };
    internal static string OrderSummary(ShopOrder order) => (order.CharacterGuid == 0
        ? L("Changement de nom", "Name change") : order.AppliedName is { } name ? order.CharacterName + " → " + name : order.CharacterName)
        + " · " + OrderStatus(order.Status);
    internal void ConfigurePurchases(ShopPurchaseActions actions) { ResetPurchases(); _purchaseActions = actions; Changed(); }
    internal async Task PurchaseAsync()
    {
        if (!CanPurchase) return;
        _purchaseAttempt ??= new(Guid.NewGuid().ToString("N"), _offer!.Offer.Id, UsesAccountService ? 0 : _character!.Character.Guid,
            _price!.Price.Currency, _price.Price.Amount, _snapshot!.CatalogRevision);
        ShopCreateOrder attempt = _purchaseAttempt;
        await PurchaseMutationAsync(token => _purchaseActions!.Create(attempt, token), creating: true);
    }
    internal async Task CancelPurchaseAsync()
    {
        if (!CanCancelPurchase || SelectedOrder is not { } order) return;
        await PurchaseMutationAsync(token => _purchaseActions!.Cancel(order.Id, token), creating: false);
    }
    internal async Task CancelListedPurchaseAsync(string id)
    {
        if (!CanManagePurchaseOrders || _snapshot?.Purchases?.Orders.Any(o => o.Id == id && o.Status is "available" or "pending" or "rejected") != true) return;
        await PurchaseMutationAsync(token => _purchaseActions!.Cancel(id, token), creating: false);
    }
    private async Task PurchaseMutationAsync(Func<CancellationToken, Task<ShopOrder>> operation, bool creating)
    {
        long session = _purchaseSession;
        using CancellationTokenSource pending = new(); _purchasePending = pending;
        IsPurchasing = true; _purchaseNotice = null; Changed();
        try
        {
            ShopOrder order = await operation(pending.Token); order.Validate();
            if (session != _purchaseSession || pending.IsCancellationRequested) return;
            _purchaseAttempt = null;
            // Keep the authoritative receipt even if the following snapshot read fails.
            if (_snapshot is { } snapshot)
                _snapshot = snapshot with { Purchases = new(snapshot.Purchases?.RenameAvailable ?? false,
                    new[] { order }.Concat(snapshot.Purchases?.Orders.Where(o => o.Id != order.Id) ?? []).Take(100).ToArray(), snapshot.Purchases?.AccountServices ?? false),
                    EuroBalanceCents = order.Currency == "eur" ? null : snapshot.EuroBalanceCents,
                    CreditBalanceEuroCents = order.Currency == "credits" ? null : snapshot.CreditBalanceEuroCents,
                    Characters = snapshot.Characters.Select(c => c.Guid == order.CharacterGuid ? c with { RenamePending = order.Status is "pending" or "delivered" ? true : null } : c).ToArray() };
            if (_character?.Character.Guid == order.CharacterGuid)
                _character.Update(_character.Character with { RenamePending = order.Status is "pending" or "delivered" ? true : null });
            foreach (ShopPriceRow price in Prices) price.UpdateBalance(BalanceFor(price.Price.Currency));
            _purchaseNotice = null;
            await RefreshPurchaseAsync(pending.Token);
        }
        catch (Exception error) when (FundingError(error))
        {
            if (session != _purchaseSession || pending.IsCancellationRequested) return;
            if (error is UnauthorizedAccessException or LauncherAuthException { StatusCode: HttpStatusCode.Unauthorized })
            { ResetSession(); _status = "unauthorized"; Changed(); return; }
            // Only an explicit client/eligibility rejection proves no debit took place.
            if (creating && error is ShopApiException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests }) _purchaseAttempt = null;
            _purchaseNotice = error switch
            {
                ShopApiException { Code: "shop-price-changed" } => new("Le tarif a changé. Actualisez et vérifiez le nouveau montant.", "The price changed. Refresh and check the new amount."),
                ShopApiException { Code: "shop-insufficient-funds" } => new("Le solde disponible est insuffisant. Actualisez votre portefeuille.", "Available funds are insufficient. Refresh your wallet."),
                ShopApiException { Code: "shop-character-online" } => new("Déconnectez votre personnage puis actualisez.", "Log out of your character, then refresh."),
                ShopApiException { Code: "shop-rename-already-pending" } => new("Un changement de nom est déjà en attente. Actualisez le suivi.", "A name change is already pending. Refresh order status."),
                ShopApiException { Code: "shop-wallet-debt" } => new("Un solde à régulariser bloque cet achat.", "An outstanding balance prevents this purchase."),
                ShopApiException { Code: "shop-service-limit" } => new("Vous avez atteint la limite de services disponibles. Utilisez ou annulez un service avant un nouvel achat.", "You have reached the limit of available services. Use or cancel a service before purchasing another."),
                _ => new("Le résultat reste à confirmer. Actualisez le suivi ou réessayez : la même commande sera reprise sans second débit.",
                    "The result still needs confirmation. Refresh or retry: the same order will resume without a second debit.")
            };
        }
        finally
        {
            if (ReferenceEquals(_purchasePending, pending)) _purchasePending = null;
            if (session == _purchaseSession) { IsPurchasing = false; Changed(); }
        }
    }
    internal async Task RefreshPurchaseAsync() { if (CanRefresh) await RefreshPurchaseAsync(CancellationToken.None); }
    private async Task RefreshPurchaseAsync(CancellationToken token)
    {
        if (_read is null || _disposed || IsPurchaseReading) return;
        long session = _purchaseSession;
        using CancellationTokenSource pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        _purchaseReadPending = pending; IsPurchaseReading = true; Changed();
        try
        {
            ShopSnapshot snapshot = await _read(pending.Token); snapshot.Validate();
            if (session != _purchaseSession || _disposed || pending.IsCancellationRequested) return;
            Apply(snapshot, _offer?.Offer.Id, _character?.Character.Guid, _price?.Price.Currency);
            if (_purchaseAttempt is { } attempt && snapshot.Purchases?.Orders.Any(o => o.IdempotencyKey == attempt.IdempotencyKey) == true) _purchaseAttempt = null;
            _purchaseNotice = null; Changed();
        }
        catch (Exception error) when (FundingError(error))
        { if (session == _purchaseSession) { _purchaseNotice = new("Actualisation impossible. Le suivi sera repris au prochain essai.", "Refresh failed. Order tracking will resume on the next attempt."); Changed(); } }
        finally
        {
            if (ReferenceEquals(_purchaseReadPending, pending)) _purchaseReadPending = null;
            if (session == _purchaseSession) { IsPurchaseReading = false; Changed(); }
        }
    }
    private void ResetPurchases()
    {
        ++_purchaseSession; _purchasePending?.Cancel(); _purchasePending = null;
        _purchaseReadPending?.Cancel(); _purchaseReadPending = null;
        _purchaseAttempt = null; _purchaseNotice = null; IsPurchasing = false; IsPurchaseReading = false;
    }
}
