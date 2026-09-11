using System.Net;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Preview;

// Constructed only by the explicit offline funding preview and synthetic tests.
// It has no HttpClient, provider URL, persistent storage or game runtime.
internal sealed class ShopFundingPreview
{
    private const uint OwnAccount=42;
    private readonly Dictionary<string,ShopAdminTopUp> _requests=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> _keys=new(StringComparer.Ordinal);
    private readonly List<ShopTransaction> _history=[];
    private long _euro,_held;
    internal ShopFundingPreview()
    {
        DateTimeOffset now=DateTimeOffset.UtcNow;
        ShopTopUp example=new("a1111111111111111111111111111111",now.AddHours(-1),now.AddHours(-1),1000,"pending");
        _requests.Add(example.Id,new(example,7,1,null,null,0,[new(example.CreatedAtUtc,7,"create","",null)]));
    }
    internal ShopFundingActions Actions=>new(Create,Cancel,List,Read,Decide);
    internal Task<ShopSnapshot> ReadSnapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(ShopPreviewData.Create() with
        {
            EuroBalanceCents=_euro-_held, History=_history.ToArray(),
            ManualFunding=new(true,true,100,5000,10000,_held,0,_requests.Values.Where(row=>row.AccountId==OwnAccount)
                .OrderByDescending(row=>row.Request.CreatedAtUtc).Select(row=>row.Request).ToArray())
        });
    }
    private Task<ShopTopUp> Create(ShopCreateTopUp input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(_keys.TryGetValue(input.IdempotencyKey,out string? old))return Task.FromResult(_requests[old].Request);
        if(input.AmountCents is <100 or >5000)throw Error("shop-invalid-top-up",HttpStatusCode.BadRequest);
        if(_requests.Values.Any(row=>row.AccountId==OwnAccount && row.Request.Status=="pending"))throw Error("shop-top-up-already-pending");
        if(_requests.Values.Where(row=>row.AccountId==OwnAccount).Sum(row=>row.Request.AmountCents)+input.AmountCents>10000)throw Error("shop-daily-top-up-limit",HttpStatusCode.TooManyRequests);
        DateTimeOffset now=DateTimeOffset.UtcNow;
        ShopTopUp request=new(Guid.NewGuid().ToString("N"),now,now,input.AmountCents,"pending");
        _requests.Add(request.Id,new(request,OwnAccount,1,null,null,0,[new(now,OwnAccount,"create","",null)]));
        _keys.Add(input.IdempotencyKey,request.Id);
        return Task.FromResult(request);
    }
    private async Task<ShopTopUp> Cancel(string id,CancellationToken token)
        => (await Decide(id,new("cancel",1,""),token)).Request;
    private Task<ShopAdminTopUpPage> List(string? status,long? before,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new ShopAdminTopUpPage(_requests.Values.Where(row=>status is null || row.Request.Status==status)
            .OrderByDescending(row=>row.Request.CreatedAtUtc).Select(row=>row with { Audit=[] }).Take(50).ToArray(),null));
    }
    private Task<ShopAdminTopUp> Read(string id,CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(_requests.TryGetValue(id,out ShopAdminTopUp? row)?row:throw Error("shop-top-up-not-found",HttpStatusCode.NotFound)); }
    private async Task<ShopAdminTopUp> Decide(string id,ShopTopUpDecision input,CancellationToken token)
    {
        ShopAdminTopUp row=await Read(id,token);
        if(row.Version!=input.ExpectedVersion)throw Error("shop-top-up-changed");
        string status, note=input.Note;
        long held=row.HeldCents;
        string? payment=row.PayPalTransactionId, dispute=row.PayPalCaseId;
        bool own=row.AccountId==OwnAccount;
        switch(input.Action)
        {
            case "approve" when row.Request.Status is "pending" or "cancelled":
                if(!input.PaymentVerified || input.ReceivedAmountCents!=row.Request.AmountCents
                    || !ShopFundingValidation.IsTransactionId(input.PayPalTransactionId))throw Error("shop-payment-amount-mismatch");
                if(_requests.Values.Any(other=>other.PayPalTransactionId==input.PayPalTransactionId))throw Error("shop-payment-already-used");
                status="credited"; payment=input.PayPalTransactionId;
                if(own){_euro+=row.Request.AmountCents; AddHistory(row,"top-up",row.Request.AmountCents);}
                break;
            case "cancel" when row.Request.Status=="pending": status="cancelled"; break;
            case "dispute" when row.Request.Status=="credited":
                status="disputed"; dispute=input.PayPalCaseId; held=own?Math.Min(row.Request.AmountCents,_euro-_held):row.Request.AmountCents;
                if(own)_held+=held;
                break;
            case "resolve-won" when row.Request.Status=="disputed":
                status="credited"; if(own)_held-=held; held=0; break;
            case "refund-confirmed" when row.Request.Status is "disputed" or "credited":
                if(!input.RefundVerified)throw Error("shop-refund-verification-required",HttpStatusCode.BadRequest);
                status="refunded";
                if(own){_held-=held; _euro-=row.Request.AmountCents; AddHistory(row,"payment-reversal",-row.Request.AmountCents);}
                held=0; break;
            default:throw Error("shop-invalid-transition");
        }
        DateTimeOffset now=DateTimeOffset.UtcNow;
        row=row with { Request=row.Request with { Status=status,UpdatedAtUtc=now },Version=row.Version+1,PayPalTransactionId=payment,PayPalCaseId=dispute,HeldCents=held,
            Audit=new[]{new ShopTopUpAudit(now,OwnAccount,input.Action,note,dispute)}.Concat(row.Audit).ToArray() };
        _requests[id]=row;
        return row;
    }
    private void AddHistory(ShopAdminTopUp row,string kind,long amount) => _history.Insert(0,new(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,kind,"eur",amount,"completed",
        new("Opération de démonstration PayPal","PayPal demo operation"),BalanceAfterCents:_euro-_held));
    private static ShopApiException Error(string code,HttpStatusCode status=HttpStatusCode.Conflict)=>new(code,status);
}
