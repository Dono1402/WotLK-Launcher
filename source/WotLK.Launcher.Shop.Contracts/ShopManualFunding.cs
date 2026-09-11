using System.Text.RegularExpressions;

namespace WotLK.Launcher.Shop.Contracts;

// All amounts are EUR cents. A payment instruction is never proof of payment.
public sealed record ShopManualFunding(bool Available, bool CanAdminister, long MinimumCents,
    long MaximumCents, long DailyMaximumCents, long HeldCents, long DebtCents,
    IReadOnlyList<ShopTopUp> Requests)
{
    public void Validate()
    {
        if (MinimumCents < 100 || MaximumCents < MinimumCents || MaximumCents > ShopSnapshot.MaximumBalanceCents
            || DailyMaximumCents < MaximumCents || DailyMaximumCents > ShopSnapshot.MaximumBalanceCents
            || HeldCents is < 0 or > ShopSnapshot.MaximumBalanceCents || DebtCents is < 0 or > ShopSnapshot.MaximumBalanceCents
            || Requests is null || Requests.Count > 50)
            throw new InvalidDataException("Invalid manual funding configuration.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ShopTopUp request in Requests)
        {
            if (request is null || !ids.Add(request.Id)) throw new InvalidDataException("Invalid top-up list.");
            request.Validate();
        }
    }
}

public sealed record ShopTopUp(string Id, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    long AmountCents, string Status, string? PaymentUrl = null)
{
    public string Reference => "ATLAS-" + Id.ToUpperInvariant();
    public void Validate()
    {
        if (!ShopFundingValidation.IsId(Id) || CreatedAtUtc.Offset != TimeSpan.Zero || UpdatedAtUtc.Offset != TimeSpan.Zero
            || CreatedAtUtc.Year is < 2000 or > 2200 || UpdatedAtUtc < CreatedAtUtc || UpdatedAtUtc.Year > 2200
            || AmountCents is < 100 or > ShopSnapshot.MaximumBalanceCents
            || Status is not ("pending" or "credited" or "cancelled" or "disputed" or "refunded")
            || (PaymentUrl is not null && (Status != "pending" || !ShopFundingValidation.IsPayPalMeUrl(PaymentUrl))))
            throw new InvalidDataException("Invalid top-up request.");
    }
}

public sealed record ShopCreateTopUp(string IdempotencyKey, long AmountCents);
public sealed record ShopTopUpDecision(string Action, long ExpectedVersion, string Note,
    string? PayPalTransactionId = null, long? ReceivedAmountCents = null,
    string? PayPalCaseId = null, bool PaymentVerified = false, bool RefundVerified = false);
public sealed record ShopTopUpAudit(DateTimeOffset OccurredAtUtc, uint ActorAccountId,
    string Action, string Note, string? PayPalCaseId);
public sealed record ShopAdminTopUp(ShopTopUp Request, uint AccountId, long Version,
    string? PayPalTransactionId, string? PayPalCaseId, long HeldCents,
    IReadOnlyList<ShopTopUpAudit> Audit)
{
    public void Validate()
    {
        if (Request is null || AccountId == 0 || Version < 1 || HeldCents < 0 || HeldCents > Request.AmountCents
            || (PayPalTransactionId is not null && !ShopFundingValidation.IsTransactionId(PayPalTransactionId))
            || PayPalCaseId?.Length > 100 || Audit is null || Audit.Count > 100)
            throw new InvalidDataException("Invalid administrator top-up.");
        Request.Validate();
        foreach (ShopTopUpAudit row in Audit)
            if (row is null || row.ActorAccountId == 0 || row.OccurredAtUtc.Offset != TimeSpan.Zero || row.OccurredAtUtc.Year is < 2000 or > 2200
                || row.Action is not ("create" or "approve" or "cancel" or "dispute" or "resolve-won" or "refund-confirmed")
                || row.Note is null || row.Note.Length > 1000 || row.PayPalCaseId?.Length > 100)
                throw new InvalidDataException("Invalid top-up audit.");
    }
}
public sealed record ShopAdminTopUpPage(IReadOnlyList<ShopAdminTopUp> Requests, long? NextBefore)
{
    public void Validate()
    {
        if (Requests is null || Requests.Count > 50 || NextBefore is <= 0)
            throw new InvalidDataException("Invalid administrator page.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ShopAdminTopUp row in Requests)
        {
            if (row is null || row.Request is null || !ids.Add(row.Request.Id)) throw new InvalidDataException("Invalid administrator list.");
            row.Validate();
        }
    }
}

public static partial class ShopFundingValidation
{
    public static bool IsId(string? value) => value is not null && IdPattern().IsMatch(value);
    public static bool IsTransactionId(string? value) => value is not null && TransactionPattern().IsMatch(value);
    public static bool IsPayPalMeUrl(string? value)
    {
        return value is not null && value.Length <= 200 && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == "https" && uri.Host.Equals("paypal.me", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && PayPalPathPattern().IsMatch(uri.AbsolutePath);
    }
    [GeneratedRegex("\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    [GeneratedRegex("\\A[A-Z0-9]{8,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TransactionPattern();
    [GeneratedRegex("\\A/[A-Za-z0-9]{2,20}/[0-9]{1,8}(?:\\.[0-9]{2})?EUR\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PayPalPathPattern();
}
