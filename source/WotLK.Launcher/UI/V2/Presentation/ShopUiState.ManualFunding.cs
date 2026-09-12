using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed record ShopFundingActions(
    Func<ShopCreateTopUp,CancellationToken,Task<ShopTopUp>> Create,
    Func<string,CancellationToken,Task<ShopTopUp>> Cancel,
    Func<string?,long?,CancellationToken,Task<ShopAdminTopUpPage>> List,
    Func<string,CancellationToken,Task<ShopAdminTopUp>> Read,
    Func<string,ShopTopUpDecision,CancellationToken,Task<ShopAdminTopUp>> Decide);

internal sealed class ShopTopUpRow(ShopTopUp request) : ShopLocalizedRow
{
    public ShopTopUp Request { get; } = request;
    public string Id => Request.Id;
    public string Reference => Request.Reference;
    public string Amount => ShopUiState.FormatEuros(Request.AmountCents);
    public string Created => Request.CreatedAtUtc.ToLocalTime().ToString("dd MMM yyyy · HH:mm",ShopUiState.Culture);
    public string Status => StatusLabel(Request.Status);
    public bool IsPending => Request.Status=="pending";
    public string StatusColor => Request.Status switch { "credited"=>"#A0DFCE", "pending" or "disputed"=>"#ECD096", "refunded"=>"#F1A2A2", _=>"#A8BECE" };
    internal static string StatusLabel(string status) => status switch
    {
        "pending"=>ShopUiState.L("En attente de validation","Awaiting approval"),
        "credited"=>ShopUiState.L("Portefeuille crédité","Wallet credited"),
        "cancelled"=>ShopUiState.L("Demande annulée","Request cancelled"),
        "disputed"=>ShopUiState.L("Paiement contesté","Payment disputed"),
        _=>ShopUiState.L("Paiement remboursé","Payment refunded")
    };
}
internal sealed class ShopAdminTopUpRow(ShopAdminTopUp value) : ShopLocalizedRow
{
    public ShopAdminTopUp Value { get; } = value;
    public string Id => Value.Request.Id;
    public string Reference => Value.Request.Reference;
    public string Amount => ShopUiState.FormatEuros(Value.Request.AmountCents);
    public string Account => ShopUiState.L("Compte ","Account ")+Value.AccountId;
    public string Status => ShopTopUpRow.StatusLabel(Value.Request.Status);
    public string Created => Value.Request.CreatedAtUtc.ToLocalTime().ToString("dd MMM · HH:mm",ShopUiState.Culture);
}
internal sealed class ShopFundingActionRow(string id) : ShopLocalizedRow
{
    public string Id { get; }=id;
    public string Label => ActionLabel(Id);
    internal static string ActionLabel(string action) => action switch
    {
        "approve"=>ShopUiState.L("Valider et créditer","Approve and credit"),
        "cancel"=>ShopUiState.L("Annuler la demande","Cancel request"),
        "dispute"=>ShopUiState.L("Signaler un litige et geler le solde","Record a dispute and hold funds"),
        "resolve-won"=>ShopUiState.L("Clore le litige et libérer le solde","Close dispute and release funds"),
        "refund-confirmed"=>ShopUiState.L("Enregistrer un remboursement confirmé","Record a confirmed refund"),
        _=>ShopUiState.L("Demande créée","Request created")
    };
}

internal sealed partial class ShopUiState
{
    private ShopFundingActions? _fundingActions;
    private long _fundingSession, _adminListGeneration, _adminDetailGeneration;
    private CancellationTokenSource? _fundingMutation, _adminListPending, _adminDetailPending;
    private string? _topUpKey;
    private long? _topUpKeyAmount;
    private ShopText? _fundingNotice;
    public bool IsFundingBusy { get; private set; }
    public bool IsFundingPreview { get; private set; }
    public bool ManualFundingAvailable => _snapshot?.ManualFunding?.Available==true && _fundingActions is not null;
    public bool CanAdministerFunding => _fundingActions is not null && _snapshot?.ManualFunding?.CanAdminister==true;
    public bool HasPendingTopUp => _snapshot?.ManualFunding?.Requests.Any(row=>row.Status=="pending")==true;
    public bool CanEditWalletDraft => !IsLoading && !IsFundingBusy && !HasPendingTopUp;
    public string FundingNotice => _fundingNotice is null ? "" : Text(_fundingNotice);
    public bool HasFundingNotice => _fundingNotice is not null;
    public string TopUpListLabel => L("Mes demandes de recharge","My top-up requests");
    public string TopUpReferenceLabel => L("Référence à joindre au paiement","Reference to include with your payment");
    public string CopyReferenceLabel => L("Copier la référence","Copy reference");
    public string OpenPayPalLabel => L("Ouvrir PayPal","Open PayPal");
    public string CancelTopUpLabel => L("Annuler la demande","Cancel request");
    public string ManualFundingInstructions => IsFundingPreview
        ? L("Démonstration : aucun paiement réel. Utilisez l’administration de démonstration pour valider cette demande.","Demo: no real payment. Use the demo administration to approve this request.")
        : L("Réglez en « biens et services » et joignez la référence ci-dessous. Votre portefeuille sera crédité après vérification du paiement par l’équipe Atlas.",
            "Pay using goods and services and include the reference below. The Atlas team will verify the payment before crediting your wallet.");
    public string TopUpPendingHint => L("Une demande attend déjà sa validation. Actualisez son statut après votre paiement. N’annulez la demande que si vous n’avez pas payé.",
        "A request is already awaiting approval. Refresh its status after paying. Only cancel the request if you have not paid.");
    public string TopUpLimits => _snapshot?.ManualFunding is { } funding
        ? L("De ","From ")+FormatEuros(funding.MinimumCents)+L(" à "," to ")+FormatEuros(funding.MaximumCents)
            +L(" par demande · "," per request · ")+FormatEuros(funding.DailyMaximumCents)+L(" maximum sur 24 h."," maximum over 24 hours.") : "";
    public string HeldFundsLabel => L("Montant temporairement gelé","Temporarily held amount");
    public string HeldFunds => FormatEuros(_snapshot?.ManualFunding?.HeldCents??0);
    public bool HasHeldFunds => _snapshot?.ManualFunding?.HeldCents>0;
    public bool HasFundingDebt => _snapshot?.ManualFunding?.DebtCents>0;
    public string FundingDebtNotice => L("Solde à régulariser : ","Outstanding balance: ")+FormatEuros(_snapshot?.ManualFunding?.DebtCents??0)
        +L(". Une prochaine recharge couvrira d’abord ce montant.",". Your next top-up will first cover this amount.");
    public IReadOnlyList<ShopTopUpRow> TopUpRequests { get; private set; }=[];
    public bool HasTopUpRequests => TopUpRequests.Count>0;
    public string FundingRefreshLabel => L("Actualiser le statut","Refresh status");
    public string AdminFundingLabel => L("Administration des recharges","Top-up administration");
    internal void ConfigureFunding(ShopFundingActions actions,bool preview=false)
    {
        _fundingActions=actions; IsFundingPreview=preview; Changed();
    }
    private void RefreshTopUpRows()
    {
        TopUpRequests=_snapshot?.ManualFunding?.Requests.Select(row=>new ShopTopUpRow(row)).ToArray()??[];
        if(TopUpRequests.FirstOrDefault(row=>row.IsPending) is { } pending)_walletAmount=(pending.Request.AmountCents/100m).ToString("0.##",Culture);
        if (_snapshot?.ManualFunding?.Available==true && _paymentMethod is null) _paymentMethod=PaymentMethods.Single(row=>row.Id=="paypal");
        foreach(ShopPaymentMethodRow row in PaymentMethods)row.SetManualFunding(_snapshot?.ManualFunding?.Available==true);
    }
    internal string? PaymentUrlFor(string id) => !IsFundingPreview && ManualFundingAvailable && !IsFundingBusy
        ? TopUpRequests.FirstOrDefault(row=>row.Id==id && row.IsPending)?.Request.PaymentUrl : null;
    internal async Task CreateTopUpAsync()
    {
        if (!CanBeginWalletPayment) return;
        long cents=WalletTopUpCents!.Value;
        if (_topUpKey is null || _topUpKeyAmount!=cents) { _topUpKey=Guid.NewGuid().ToString("N"); _topUpKeyAmount=cents; }
        ShopCreateTopUp request=new(_topUpKey,cents);
        await RunFundingMutation(async token=>
        {
            await _fundingActions!.Create(request,token);
        },IsFundingPreview ? new("Demande de démonstration créée. Vous pouvez la valider dans l’administration.","Demo request created. You can approve it in the administration page.")
            : new("Demande créée. Joignez sa référence à votre paiement PayPal.","Request created. Include its reference with your PayPal payment."));
    }
    internal async Task CancelTopUpAsync(ShopTopUpRow row)
    {
        if (_fundingActions is null || !TopUpRequests.Contains(row) || !row.IsPending) return;
        await RunFundingMutation(async token=>{ await _fundingActions!.Cancel(row.Id,token); },new("La demande a été annulée.","The request has been cancelled."));
    }
    private async Task RunFundingMutation(Func<CancellationToken,Task> mutation,ShopText success)
    {
        if (IsFundingBusy || IsLoading || IsConverting || IsPurchasing || IsPurchaseReading || _disposed) return;
        long session=_fundingSession;
        using CancellationTokenSource pending=new(); _fundingMutation=pending;
        IsFundingBusy=true; _fundingNotice=null; Changed();
        try
        {
            await mutation(pending.Token);
            if (_disposed || session!=_fundingSession || pending.IsCancellationRequested) return;
            _topUpKey=null; _topUpKeyAmount=null;
            IsFundingBusy=false;
            await RefreshAsync();
            if (session!=_fundingSession) return;
            _fundingNotice=success;
            if (IsShopAdminOpen && CanAdministerFunding) await RefreshAdminTopUpsAsync();
        }
        catch(Exception error) when (FundingError(error))
        {
            if (session==_fundingSession)
            {
                if (error is UnauthorizedAccessException or LauncherAuthException { StatusCode:HttpStatusCode.Unauthorized }) ResetSession();
                _fundingNotice=FundingErrorText(error);
            }
        }
        finally
        {
            if (ReferenceEquals(_fundingMutation,pending)) _fundingMutation=null;
            if (session==_fundingSession) IsFundingBusy=false;
            Changed();
        }
    }
    private static bool FundingError(Exception error) => error is HttpRequestException or IOException or JsonException
        or UnauthorizedAccessException or OperationCanceledException or LauncherAuthException;
    private static ShopText FundingErrorText(Exception error) => error switch
    {
        ShopApiException { Code:"shop-top-up-already-pending" }=>new("Une demande existe déjà. Actualisez pour la retrouver.","A request already exists. Refresh to find it."),
        ShopApiException { Code:"shop-payment-already-used" }=>new("Cet identifiant PayPal est déjà rattaché à une recharge. Aucun nouveau crédit n’a été ajouté.","This PayPal transaction is already linked to a top-up. No new funds were added."),
        ShopApiException { Code:"shop-payment-amount-mismatch" }=>new("Le montant vérifié ne correspond pas à la demande.","The verified amount does not match the request."),
        ShopApiException { Code:"shop-top-up-changed" or "shop-invalid-transition" }=>new("La demande a changé. Actualisez-la avant de décider.","The request has changed. Refresh it before making a decision."),
        ShopApiException { Code:"shop-daily-top-up-limit" }=>new("La limite de demandes sur 24 heures est atteinte. Réessayez plus tard.","The 24-hour request limit has been reached. Try again later."),
        ShopApiException { StatusCode:HttpStatusCode.Forbidden }=>new("Votre compte n’a pas accès à cette action.","Your account cannot perform this action."),
        UnauthorizedAccessException or LauncherAuthException { StatusCode:HttpStatusCode.Unauthorized }=>new("Reconnectez-vous pour continuer.","Sign in again to continue."),
        ShopApiException { StatusCode:HttpStatusCode.BadRequest }=>new("Vérifiez les champs de la demande avant de réessayer.","Check the request fields before trying again."),
        _=>new("Le résultat n’a pas pu être confirmé. Actualisez le statut avant de réessayer ou de payer à nouveau.",
            "The result could not be confirmed. Refresh the status before trying again or paying again.")
    };

    public bool IsShopAdminOpen { get; private set; }
    public bool IsAdminLoading { get; private set; }
    public bool IsAdminDetailLoading { get; private set; }
    public IReadOnlyList<ShopAdminTopUpRow> AdminTopUps { get; private set; }=[];
    public IReadOnlyList<ShopHistoryFilterRow> AdminStatusFilters { get; }=
    [new("pending",new("À valider","Awaiting approval")),new("disputed",new("Litiges","Disputes")),new("credited",new("Créditées","Credited")),
        new("refunded",new("Remboursées","Refunded")),new("cancelled",new("Annulées","Cancelled")),new("all",new("Toutes les demandes","All requests"))];
    private ShopHistoryFilterRow? _adminFilter;
    public ShopHistoryFilterRow AdminStatusFilter
    {
        get=>_adminFilter??AdminStatusFilters[0];
        set { if(value is null || !AdminStatusFilters.Contains(value) || ReferenceEquals(value,_adminFilter))return; _adminFilter=value; Changed(); if(IsShopAdminOpen)_=RefreshAdminTopUpsAsync(); }
    }
    private ShopAdminTopUpRow? _adminSelected;
    private ShopAdminTopUp? _adminDetail;
    private long? _adminNextBefore;
    public bool HasAdminNextPage=>_adminNextBefore is not null && !IsAdminLoading && !IsFundingBusy;
    public bool CanRefreshAdmin=>CanAdministerFunding && !IsFundingBusy && !IsAdminLoading;
    public bool CanEditAdminDetails=>CanAdministerFunding && !IsFundingBusy && !IsAdminLoading;
    public bool HasAdminSelection=>_adminDetail is not null;
    public string AdminSubtitle=>IsFundingPreview ? L("Démonstration locale · aucun encaissement ni remboursement réel","Local demo · no real charge or refund")
        : L("Vérifiez les opérations dans PayPal, puis enregistrez votre décision.","Verify transactions in PayPal, then record your decision.");
    public string AdminNoSelection=>IsAdminDetailLoading ? L("Chargement de la demande…","Loading request…") : L("Sélectionnez une demande pour examiner son paiement.","Select a request to review its payment.");
    public string AdminEmpty=>IsAdminLoading ? L("Chargement…","Loading…") : L("Aucune demande dans cette liste.","No requests in this list.");
    public bool ShowAdminEmpty=>AdminTopUps.Count==0;
    public string AdminReference=>_adminDetail?.Request.Reference??"";
    public string AdminAccount=>_adminDetail is { } detail ? L("Compte ","Account ")+detail.AccountId+" · "+ShopTopUpRow.StatusLabel(detail.Request.Status):"";
    public string AdminAmount=>_adminDetail is { } detail ? FormatEuros(detail.Request.AmountCents):"—";
    public string AdminAuditLabel=>L("Journal de la demande · 100 dernières entrées","Request audit trail · latest 100 entries");
    public string AdminAudit=>_adminDetail is null ? "" : string.Join("\n\n",_adminDetail.Audit.Select(row=>
        row.OccurredAtUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm",Culture)+" · "+L("Compte ","Account ")+row.ActorAccountId
        +" · "+ShopFundingActionRow.ActionLabel(row.Action)+(row.PayPalCaseId is { } id ? " · "+id:"")+(row.Note.Length>0?"\n"+row.Note:"")));
    public ShopAdminTopUpRow? AdminSelectedTopUp
    {
        get=>_adminSelected;
        set { if(value is null || !AdminTopUps.Contains(value) || ReferenceEquals(value,_adminSelected))return; _adminSelected=value; _=ReadAdminTopUpAsync(value.Id); Changed(); }
    }
    public IReadOnlyList<ShopFundingActionRow> AdminActions { get; private set; }=[];
    private ShopFundingActionRow? _adminAction;
    public ShopFundingActionRow? AdminAction
    {
        get=>_adminAction;
        set { if(value is null || !AdminActions.Contains(value))return; _adminAction=value; AdminPaymentVerified=false; AdminRefundVerified=false; Changed(); }
    }
    private string _adminTransaction="",_adminAmount="",_adminCase="",_adminNote="",_adminSearch="";
    private bool _adminPaymentVerified,_adminRefundVerified;
    public string AdminTransactionId { get=>_adminTransaction; set { _adminTransaction=value; Changed(); } }
    public string AdminReceivedAmount { get=>_adminAmount; set { _adminAmount=value; Changed(); } }
    public string AdminCaseId { get=>_adminCase; set { _adminCase=value; Changed(); } }
    public string AdminNote { get=>_adminNote; set { _adminNote=value; Changed(); } }
    public string AdminSearchReference { get=>_adminSearch; set { _adminSearch=value; Changed(); } }
    public bool AdminPaymentVerified { get=>_adminPaymentVerified; set { _adminPaymentVerified=value; Changed(); } }
    public bool AdminRefundVerified { get=>_adminRefundVerified; set { _adminRefundVerified=value; Changed(); } }
    public bool IsApprovalAction=>_adminAction?.Id=="approve";
    public bool IsDisputeAction=>_adminAction?.Id=="dispute";
    public bool IsRefundAction=>_adminAction?.Id=="refund-confirmed";
    public string AdminActionLabel=>L("Décision","Decision");
    public string AdminTransactionLabel=>L("Identifiant de la transaction PayPal","PayPal transaction ID");
    public string AdminReceivedLabel=>L("Montant reçu vérifié (€)","Verified received amount (€)");
    public string AdminCaseLabel=>L("Référence du litige PayPal","PayPal dispute reference");
    public string AdminNoteLabel=>L("Note de vérification (obligatoire)","Verification note (required)");
    public string AdminPaymentConfirmation=>L("J’ai vérifié dans PayPal un paiement biens et services reçu, au bon montant et pour cette demande.",
        "I verified in PayPal a received goods-and-services payment with the correct amount and request reference.");
    public string AdminRefundConfirmation=>L("J’ai vérifié que PayPal a déjà remboursé ou repris ce paiement. Cette action corrige uniquement le portefeuille Atlas.",
        "I verified that PayPal has already refunded or reversed this payment. This action only adjusts the Atlas wallet.");
    public string AdminSubmitLabel=>IsFundingBusy?L("Enregistrement…","Saving…"):_adminAction?.Label??L("Aucune action disponible","No action available");
    public string AdminNextLabel=>L("Demandes plus anciennes","Older requests");
    public string AdminLatestLabel=>L("Actualiser la liste","Refresh list");
    public string AdminSearchLabel=>L("Rechercher une référence","Find a reference");
    public bool CanSubmitAdminDecision=>CanAdministerFunding && HasAdminSelection && !IsFundingBusy && !IsAdminLoading && !IsAdminDetailLoading
        && _adminAction is not null && !string.IsNullOrWhiteSpace(_adminNote) && _adminNote.Length<=1000
        && (!IsApprovalAction || (AdminPaymentVerified && ShopFundingValidation.IsTransactionId(_adminTransaction.Trim().ToUpperInvariant())
            && TryParseWalletAmount(_adminAmount,out long amount) && amount==_adminDetail!.Request.AmountCents))
        && (!IsDisputeAction || !string.IsNullOrWhiteSpace(_adminCase)) && (!IsRefundAction || AdminRefundVerified);
    internal async Task OpenAdminFundingAsync()
    {
        if(!CanAdministerFunding || _disposed)return;
        _fundingNotice=null;
        IsWalletOpen=false; IsHistoryOpen=false; IsConversionOpen=false; IsServiceOpen=false; IsShopAdminOpen=true; ClearFundingReturn(); Changed();
        await RefreshAdminTopUpsAsync();
    }
    internal void CloseAdminFunding(bool returnToWallet=false)
    {
        bool wasOpen=IsShopAdminOpen; IsShopAdminOpen=false; CancelAdminReads(); ClearAdminDetail(); AdminTopUps=[];
        if(returnToWallet && wasOpen)IsWalletOpen=true;
        Changed();
    }
    internal async Task RefreshAdminTopUpsAsync(bool nextPage=false)
    {
        if(!CanAdministerFunding || !IsShopAdminOpen || IsFundingBusy || _disposed)return;
        long? before=nextPage?_adminNextBefore:null;
        if(nextPage && before is null)return;
        long session=_fundingSession, generation=++_adminListGeneration;
        _adminListPending?.Cancel(); using CancellationTokenSource pending=new(); _adminListPending=pending;
        CancelAdminDetailRead(); ClearAdminDetail(); AdminTopUps=[]; IsAdminLoading=true; Changed();
        try
        {
            ShopAdminTopUpPage page=await _fundingActions!.List(AdminStatusFilter.Id=="all"?null:AdminStatusFilter.Id,before,pending.Token);
            if(session!=_fundingSession || generation!=_adminListGeneration || pending.IsCancellationRequested || !IsShopAdminOpen)return;
            page.Validate(); AdminTopUps=page.Requests.Select(row=>new ShopAdminTopUpRow(row)).ToArray(); _adminNextBefore=page.NextBefore;
        }
        catch(Exception error)when(FundingError(error))
        { if(session==_fundingSession && generation==_adminListGeneration && !pending.IsCancellationRequested)HandleAdminError(error); }
        finally { if(ReferenceEquals(_adminListPending,pending))_adminListPending=null; if(generation==_adminListGeneration)IsAdminLoading=false; Changed(); }
    }
    internal async Task SearchAdminTopUpAsync()
    {
        string id=_adminSearch.Trim(); if(id.StartsWith("ATLAS-",StringComparison.OrdinalIgnoreCase))id=id[6..]; id=id.ToLowerInvariant();
        if(!ShopFundingValidation.IsId(id)) { _fundingNotice=new("Saisissez une référence Atlas complète.","Enter a complete Atlas reference."); Changed(); return; }
        await ReadAdminTopUpAsync(id);
    }
    private async Task ReadAdminTopUpAsync(string id)
    {
        if(!CanAdministerFunding || !IsShopAdminOpen || IsFundingBusy || _disposed)return;
        long session=_fundingSession, generation=++_adminDetailGeneration;
        _adminDetailPending?.Cancel(); using CancellationTokenSource pending=new(); _adminDetailPending=pending;
        ClearAdminDetail(keepSelection:true); IsAdminDetailLoading=true; Changed();
        try
        {
            ShopAdminTopUp detail=await _fundingActions!.Read(id,pending.Token);
            if(session!=_fundingSession || generation!=_adminDetailGeneration || pending.IsCancellationRequested || !IsShopAdminOpen)return;
            detail.Validate(); _adminDetail=detail;
            string[] actions=detail.Request.Status switch
            { "pending"=>["approve","cancel"], "cancelled"=>["approve"], "credited"=>["dispute","refund-confirmed"], "disputed"=>["resolve-won","refund-confirmed"], _=>[] };
            AdminActions=actions.Select(action=>new ShopFundingActionRow(action)).ToArray(); _adminAction=AdminActions.FirstOrDefault();
            _adminTransaction=detail.PayPalTransactionId??""; _adminCase=detail.PayPalCaseId??"";
        }
        catch(Exception error)when(FundingError(error))
        { if(session==_fundingSession && generation==_adminDetailGeneration && !pending.IsCancellationRequested)HandleAdminError(error); }
        finally { if(ReferenceEquals(_adminDetailPending,pending))_adminDetailPending=null; if(generation==_adminDetailGeneration)IsAdminDetailLoading=false; Changed(); }
    }
    internal async Task SubmitAdminDecisionAsync()
    {
        if(!CanSubmitAdminDecision)return;
        ShopAdminTopUp detail=_adminDetail!;
        ShopTopUpDecision input=new(_adminAction!.Id,detail.Version,_adminNote.Trim(),IsApprovalAction?_adminTransaction.Trim().ToUpperInvariant():null,
            IsApprovalAction&&TryParseWalletAmount(_adminAmount,out long amount)?amount:null,
            string.IsNullOrWhiteSpace(_adminCase)?null:_adminCase.Trim(),AdminPaymentVerified,AdminRefundVerified);
        await RunFundingMutation(async token=>{await _fundingActions!.Decide(detail.Request.Id,input,token);},
            new("Décision enregistrée. Le portefeuille et le journal ont été mis à jour.","Decision recorded. The wallet and audit trail have been updated."));
    }
    private void HandleAdminError(Exception error)
    {
        if(error is UnauthorizedAccessException or LauncherAuthException { StatusCode:HttpStatusCode.Unauthorized })ResetSession();
        else if(error is HttpRequestException { StatusCode:HttpStatusCode.Forbidden })CloseAdminFunding();
        _fundingNotice=FundingErrorText(error);
    }
    private void ClearAdminDetail(bool keepSelection=false)
    {
        if(!keepSelection)_adminSelected=null;
        _adminDetail=null; _adminAction=null; AdminActions=[]; _adminTransaction=_adminAmount=_adminCase=_adminNote="";
        _adminPaymentVerified=_adminRefundVerified=false;
    }
    private void CancelAdminDetailRead() { ++_adminDetailGeneration; _adminDetailPending?.Cancel(); _adminDetailPending=null; IsAdminDetailLoading=false; }
    private void CancelAdminReads() { ++_adminListGeneration; _adminListPending?.Cancel(); _adminListPending=null; IsAdminLoading=false; CancelAdminDetailRead(); }
    private void ResetManualFunding()
    {
        ++_fundingSession; _fundingMutation?.Cancel(); _fundingMutation=null; IsFundingBusy=false;
        CancelAdminReads(); ClearAdminDetail(); AdminTopUps=[]; TopUpRequests=[]; _adminNextBefore=null; _adminFilter=null; _adminSearch="";
        IsShopAdminOpen=false; _topUpKey=null; _topUpKeyAmount=null; _fundingNotice=null;
        if(IsFundingPreview){_fundingActions=null;_read=null;}
        IsFundingPreview=false;
    }
}
