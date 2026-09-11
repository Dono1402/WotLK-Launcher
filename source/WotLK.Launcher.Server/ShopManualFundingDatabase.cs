using System.Data;
using System.Globalization;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal sealed class ShopFundingException(string code, int statusCode = 409) : Exception(code)
{
    internal string Code { get; } = code;
    internal int StatusCode { get; } = statusCode;
}

internal sealed record ShopFundingRead(long AvailableCents, long CreditCents,
    ShopManualFunding Funding, IReadOnlyList<ShopTransaction> History);

public sealed partial class LauncherDatabase
{
    private sealed record FundingWallet(long Euro, long Credits, long Held, long Debt);
    private sealed record FundingRequest(long Sequence, string Id, uint AccountId, long Amount,
        string Status, long Version, string? TransactionId, string? CaseId, long Held,
        DateTimeOffset Created, DateTimeOffset Updated);

    internal async Task<ShopFundingRead> ReadShopFundingAsync(uint accountId, ShopManualFundingOptions options, CancellationToken token)
    {
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        FundingWallet wallet = await ReadFundingWallet(connection, transaction, accountId, false, token);
        List<ShopTopUp> requests = [];
        await using (MySqlCommand command = FundingCommand(connection, transaction,
            "SELECT * FROM atlas_shop_top_up WHERE account_id=@account ORDER BY sequence_id DESC LIMIT 50;", ("@account", accountId)))
        {
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) requests.Add(PublicTopUp(ReadFundingRequest(reader), options));
        }
        List<ShopTransaction> history = [];
        await using (MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT id,created_at,action,amount_cents,euro_after,held_after FROM atlas_shop_ledger
            WHERE account_id=@account AND action IN ('approve','refund-confirmed') ORDER BY id DESC LIMIT 100;
            """, ("@account", accountId)))
        {
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                bool reversed = reader.GetString("action") == "refund-confirmed";
                history.Add(new("manual-" + reader.GetInt64("id").ToString(CultureInfo.InvariantCulture), FundingUtc(reader, "created_at"),
                    reversed ? "payment-reversal" : "top-up", "eur", reader.GetInt64("amount_cents"), "completed",
                    reversed ? new("Paiement remboursé sur PayPal", "Payment refunded on PayPal") : new("Recharge PayPal validée", "PayPal top-up approved"),
                    BalanceAfterCents: reader.GetInt64("euro_after") - reader.GetInt64("held_after")));
            }
        }
        return new(wallet.Euro - wallet.Held, wallet.Credits,
            new(options.Enabled, options.CanAdminister(accountId), options.MinimumCents, options.MaximumCents,
                options.DailyMaximumCents, wallet.Held, wallet.Debt, requests), history);
    }

    internal async Task<ShopTopUp> CreateShopTopUpAsync(uint accountId, ShopCreateTopUp input, ShopManualFundingOptions options, CancellationToken token)
    {
        if (!ShopFundingValidation.IsId(input.IdempotencyKey) || input.AmountCents < options.MinimumCents || input.AmountCents > options.MaximumCents)
            throw new ShopFundingException("shop-invalid-top-up", 400);
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        // A single per-account lock serializes request quotas and all financial changes.
        await FundingExecute(connection, transaction, """
            INSERT INTO atlas_shop_wallet(account_id,updated_at) VALUES(@account,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE account_id=account_id;
            """, token, ("@account", accountId));
        FundingWallet wallet = await ReadFundingWallet(connection, transaction, accountId, true, token);
        FundingRequest? existing = await FindFundingRequest(connection, transaction,
            "account_id=@account AND idempotency_key=@key", false, token, ("@account", accountId), ("@key", input.IdempotencyKey));
        if (existing is not null)
        {
            if (existing.Amount != input.AmountCents) throw new ShopFundingException("shop-idempotency-conflict");
            return PublicTopUp(existing, options);
        }
        if (wallet.Euro > ShopSnapshot.MaximumBalanceCents - input.AmountCents)
            throw new ShopFundingException("shop-wallet-limit");
        await using (MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT COUNT(*) FROM atlas_shop_top_up WHERE account_id=@account AND status='pending';
            """, ("@account", accountId)))
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0)
                throw new ShopFundingException("shop-top-up-already-pending");
        await using (MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT COUNT(*) AS count,SUM(amount_cents) AS amount FROM atlas_shop_top_up
            WHERE account_id=@account AND created_at > UTC_TIMESTAMP(6)-INTERVAL 24 HOUR;
            """, ("@account", accountId)))
        {
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            if (reader.GetInt64("count") >= 10 || (!reader.IsDBNull(reader.GetOrdinal("amount"))
                && reader.GetInt64("amount") > options.DailyMaximumCents - input.AmountCents))
                throw new ShopFundingException("shop-daily-top-up-limit", 429);
        }
        string id = Guid.NewGuid().ToString("N");
        await FundingExecute(connection, transaction, """
            INSERT INTO atlas_shop_top_up(id,account_id,idempotency_key,amount_cents,created_at,updated_at)
            VALUES(@id,@account,@key,@amount,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            """, token, ("@id", id), ("@account", accountId), ("@key", input.IdempotencyKey), ("@amount", input.AmountCents));
        FundingRequest created = (await FindFundingRequest(connection, transaction, "id=@id", false, token, ("@id", id)))!;
        await AppendFundingEvent(connection, transaction, created, accountId, "create", "", null, wallet, wallet, 0, token);
        await transaction.CommitAsync(token);
        return PublicTopUp(created, options);
    }

    internal async Task<ShopAdminTopUpPage> ListShopTopUpsAsync(string? status, long? before, ShopManualFundingOptions options, CancellationToken token)
    {
        if ((status is not null && status is not ("pending" or "credited" or "cancelled" or "disputed" or "refunded")) || before is <= 0)
            throw new ShopFundingException("shop-invalid-query", 400);
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlCommand command = FundingCommand(connection, null, """
            SELECT * FROM atlas_shop_top_up WHERE (@status IS NULL OR status=@status)
            AND (@before IS NULL OR sequence_id < @before) ORDER BY sequence_id DESC LIMIT 51;
            """, ("@status", status), ("@before", before));
        List<FundingRequest> rows = [];
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(ReadFundingRequest(reader));
        return new(rows.Take(50).Select(row => AdminTopUp(row, options, [])).ToArray(), rows.Count > 50 ? rows[49].Sequence : null);
    }

    internal async Task<ShopAdminTopUp> ReadShopTopUpAsync(string id, ShopManualFundingOptions options, CancellationToken token)
    {
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        FundingRequest row = await FindFundingRequest(connection, transaction, "id=@id", false, token, ("@id", id))
            ?? throw new ShopFundingException("shop-top-up-not-found", 404);
        List<ShopTopUpAudit> audit = [];
        await using MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT created_at,actor_account_id,action,note,paypal_case_id FROM atlas_shop_ledger
            WHERE request_id=@id ORDER BY id DESC LIMIT 100;
            """, ("@id", id));
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            audit.Add(new(FundingUtc(reader, "created_at"), reader.GetUInt32("actor_account_id"), reader.GetString("action"),
                reader.GetString("note"), FundingNullable(reader, "paypal_case_id")));
        return AdminTopUp(row, options, audit);
    }

    internal async Task<ShopAdminTopUp> DecideShopTopUpAsync(uint actorId, string id, ShopTopUpDecision input,
        ShopManualFundingOptions options, bool administrator, CancellationToken token)
    {
        ValidateFundingDecision(input, administrator);
        await using MySqlConnection connection = await OpenAsync(token);
        await using (MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
        {
            FundingRequest locator = await FindFundingRequest(connection, transaction, "id=@id", false, token, ("@id", id))
                ?? throw new ShopFundingException("shop-top-up-not-found", 404);
            if (!administrator && locator.AccountId != actorId) throw new ShopFundingException("shop-top-up-not-found", 404);
            FundingWallet oldWallet = await ReadFundingWallet(connection, transaction, locator.AccountId, true, token);
            FundingRequest row = (await FindFundingRequest(connection, transaction, "id=@id", true, token, ("@id", id)))!;
            string? transactionId = input.PayPalTransactionId?.Trim().ToUpperInvariant();
            string? caseId = string.IsNullOrWhiteSpace(input.PayPalCaseId) ? null : input.PayPalCaseId.Trim();
            string note = input.Note.Trim();
            if (input.Action is "resolve-won" or "refund-confirmed") caseId ??= row.CaseId;
            if (input.Action == "approve" && input.ReceivedAmountCents != row.Amount)
                throw new ShopFundingException("shop-payment-amount-mismatch");
            if (row.Version != input.ExpectedVersion)
            {
                if (row.Version != input.ExpectedVersion + 1 || (input.Action == "approve" && transactionId != row.TransactionId)
                    || !await IsFundingReplay(connection, transaction, row, actorId, input.Action, note, caseId, token))
                    throw new ShopFundingException("shop-top-up-changed");
            }
            else
            {
                FundingWallet wallet = oldWallet;
                string nextStatus;
                long held = row.Held, amount = 0;
                switch (input.Action)
                {
                    case "approve" when row.Status is "pending" or "cancelled":
                        if (input.ReceivedAmountCents != row.Amount) throw new ShopFundingException("shop-payment-amount-mismatch");
                        long paidDebt = Math.Min(wallet.Debt, row.Amount);
                        wallet = wallet with { Euro = checked(wallet.Euro + row.Amount - paidDebt), Debt = wallet.Debt - paidDebt };
                        amount = row.Amount; nextStatus = "credited";
                        break;
                    case "cancel" when row.Status == "pending":
                        transactionId = row.TransactionId; nextStatus = "cancelled";
                        break;
                    case "dispute" when row.Status == "credited":
                        held = Math.Min(row.Amount, wallet.Euro - wallet.Held);
                        wallet = wallet with { Held = wallet.Held + held };
                        transactionId = row.TransactionId; nextStatus = "disputed";
                        break;
                    case "resolve-won" when row.Status == "disputed":
                        wallet = wallet with { Held = wallet.Held - held };
                        held = 0; transactionId = row.TransactionId; caseId = row.CaseId; nextStatus = "credited";
                        break;
                    case "refund-confirmed" when row.Status is "credited" or "disputed":
                        long otherHolds = wallet.Held - held;
                        long debit = Math.Min(row.Amount, wallet.Euro - otherHolds);
                        wallet = wallet with { Euro = wallet.Euro - debit, Held = otherHolds, Debt = checked(wallet.Debt + row.Amount - debit) };
                        held = 0; amount = -row.Amount; transactionId = row.TransactionId; caseId ??= row.CaseId; nextStatus = "refunded";
                        break;
                    default: throw new ShopFundingException("shop-invalid-transition");
                }
                if (wallet.Euro is < 0 or > ShopSnapshot.MaximumBalanceCents || wallet.Held < 0 || wallet.Held > wallet.Euro
                    || wallet.Debt is < 0 or > ShopSnapshot.MaximumBalanceCents)
                    throw new ShopFundingException("shop-wallet-limit");
                await FundingExecute(connection, transaction, """
                    UPDATE atlas_shop_wallet SET euro_cents=@euro,held_cents=@held,debt_cents=@debt,updated_at=UTC_TIMESTAMP(6) WHERE account_id=@account;
                    """, token, ("@euro", wallet.Euro), ("@held", wallet.Held), ("@debt", wallet.Debt), ("@account", row.AccountId));
                await FundingExecute(connection, transaction, """
                    UPDATE atlas_shop_top_up SET status=@status,version=version+1,paypal_transaction_id=@payment,paypal_case_id=@case,
                    held_cents=@held,updated_at=UTC_TIMESTAMP(6) WHERE id=@id;
                    """, token, ("@status", nextStatus), ("@payment", transactionId), ("@case", caseId), ("@held", held), ("@id", id));
                row = row with { Version = row.Version + 1 };
                await AppendFundingEvent(connection, transaction, row, actorId, input.Action, note, caseId, oldWallet, wallet, amount, token);
                await transaction.CommitAsync(token);
            }
        }
        return await ReadShopTopUpAsync(id, options, token);
    }

    private static void ValidateFundingDecision(ShopTopUpDecision input, bool administrator)
    {
        if (input.ExpectedVersion < 1 || input.ExpectedVersion == long.MaxValue || input.Note is null || input.Note.Length > 1000
            || input.Note.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            || input.PayPalCaseId?.Length > 100 || (input.PayPalCaseId?.Any(char.IsControl) ?? false)
            || input.Action is not ("approve" or "cancel" or "dispute" or "resolve-won" or "refund-confirmed"))
            throw new ShopFundingException("shop-invalid-decision", 400);
        if (!administrator && input.Action != "cancel") throw new ShopFundingException("shop-admin-required", 403);
        if (administrator && string.IsNullOrWhiteSpace(input.Note)) throw new ShopFundingException("shop-decision-note-required", 400);
        if (input.Action == "approve" && (!input.PaymentVerified
            || !ShopFundingValidation.IsTransactionId(input.PayPalTransactionId?.Trim().ToUpperInvariant())
            || input.ReceivedAmountCents is < 100 or > ShopSnapshot.MaximumBalanceCents || input.ReceivedAmountCents is null))
            throw new ShopFundingException("shop-payment-verification-required", 400);
        if (input.Action == "dispute" && string.IsNullOrWhiteSpace(input.PayPalCaseId))
            throw new ShopFundingException("shop-paypal-case-required", 400);
        if (input.Action == "refund-confirmed" && !input.RefundVerified)
            throw new ShopFundingException("shop-refund-verification-required", 400);
    }

    private static async Task<bool> IsFundingReplay(MySqlConnection connection, MySqlTransaction transaction,
        FundingRequest row, uint actor, string action, string note, string? caseId, CancellationToken token)
    {
        await using MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT COUNT(*) FROM atlas_shop_ledger WHERE request_id=@id AND request_version=@version
            AND actor_account_id=@actor AND action=@action AND BINARY note=BINARY @note
            AND (paypal_case_id <=> @case OR @action='resolve-won');
            """, ("@id", row.Id), ("@version", row.Version), ("@actor", actor), ("@action", action), ("@note", note), ("@case", caseId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task AppendFundingEvent(MySqlConnection connection, MySqlTransaction transaction, FundingRequest row,
        uint actor, string action, string note, string? caseId, FundingWallet before, FundingWallet after, long amount, CancellationToken token)
        => await FundingExecute(connection, transaction, """
            INSERT INTO atlas_shop_ledger(request_id,request_version,account_id,actor_account_id,action,amount_cents,
                euro_delta,held_delta,debt_delta,euro_after,held_after,debt_after,note,paypal_case_id,created_at)
            VALUES(@id,@version,@account,@actor,@action,@amount,@ed,@hd,@dd,@euro,@held,@debt,@note,@case,UTC_TIMESTAMP(6));
            """, token, ("@id", row.Id), ("@version", row.Version), ("@account", row.AccountId), ("@actor", actor),
            ("@action", action), ("@amount", amount), ("@ed", after.Euro-before.Euro), ("@hd", after.Held-before.Held),
            ("@dd", after.Debt-before.Debt), ("@euro", after.Euro), ("@held", after.Held), ("@debt", after.Debt), ("@note", note), ("@case", caseId));

    private static async Task<FundingWallet> ReadFundingWallet(MySqlConnection connection, MySqlTransaction transaction,
        uint account, bool forUpdate, CancellationToken token)
    {
        await using MySqlCommand command = FundingCommand(connection, transaction,
            "SELECT euro_cents,credit_cents,held_cents,debt_cents FROM atlas_shop_wallet WHERE account_id=@account" + (forUpdate ? " FOR UPDATE;" : ";"), ("@account", account));
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)) : new(0, 0, 0, 0);
    }

    private static async Task<FundingRequest?> FindFundingRequest(MySqlConnection connection, MySqlTransaction transaction,
        string predicate, bool forUpdate, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        // Predicates are application constants. All external values are parameters.
        await using MySqlCommand command = FundingCommand(connection, transaction,
            "SELECT * FROM atlas_shop_top_up WHERE " + predicate + (forUpdate ? " FOR UPDATE;" : ";"), parameters);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadFundingRequest(reader) : null;
    }
    private static FundingRequest ReadFundingRequest(MySqlDataReader reader) => new(reader.GetInt64("sequence_id"), reader.GetString("id"),
        reader.GetUInt32("account_id"), reader.GetInt64("amount_cents"), reader.GetString("status"), reader.GetInt64("version"),
        FundingNullable(reader, "paypal_transaction_id"), FundingNullable(reader, "paypal_case_id"), reader.GetInt64("held_cents"),
        FundingUtc(reader, "created_at"), FundingUtc(reader, "updated_at"));
    private static ShopTopUp PublicTopUp(FundingRequest row, ShopManualFundingOptions options) => new(row.Id, row.Created, row.Updated,
        row.Amount, row.Status, options.Enabled && row.Status == "pending" ? options.PaymentUrl(row.Amount) : null);
    private static ShopAdminTopUp AdminTopUp(FundingRequest row, ShopManualFundingOptions options, IReadOnlyList<ShopTopUpAudit> audit)
        => new(PublicTopUp(row, options), row.AccountId, row.Version, row.TransactionId, row.CaseId, row.Held, audit);
    private static string? FundingNullable(MySqlDataReader reader, string field) => reader.IsDBNull(reader.GetOrdinal(field)) ? null : reader.GetString(field);
    private static DateTimeOffset FundingUtc(MySqlDataReader reader, string field) => new(DateTime.SpecifyKind(reader.GetDateTime(field), DateTimeKind.Utc));
    private static MySqlCommand FundingCommand(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        MySqlCommand command = new(sql, connection, transaction) { CommandTimeout = 10 };
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static async Task FundingExecute(MySqlConnection connection, MySqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using MySqlCommand command = FundingCommand(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(token);
    }
}
