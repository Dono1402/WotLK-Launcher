using System.Data;
using System.Globalization;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal async Task<bool> ShopDeliveryHealthyAsync(ShopPurchaseOptions options, CancellationToken token)
    {
        if (!options.RenameEnabled) return false;
        await using MySqlConnection connection = await OpenAsync(token);
        return await ShopDeliveryHealthy(connection, null, options, token);
    }

    private async Task<bool> ShopDeliveryHealthy(MySqlConnection connection, MySqlTransaction? transaction, ShopPurchaseOptions options, CancellationToken token)
    {
        await using MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT COUNT(*) FROM atlas_shop_delivery_health WHERE realm_id=@realm AND protocol=1
            AND character_database=@characters AND last_seen_at BETWEEN UTC_TIMESTAMP(6)-INTERVAL 30 SECOND AND UTC_TIMESTAMP(6)+INTERVAL 5 SECOND;
            """, ("@realm", options.RealmId), ("@characters", _options.CharacterDatabaseName));
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    internal async Task<ShopOrder> CreateShopOrderAsync(uint accountId, ShopCreateOrder input, ShopCatalog catalog, ShopPurchaseOptions options, CancellationToken token)
    {
        if (!ShopFundingValidation.IsId(input.IdempotencyKey) || input.OfferId != "character-rename" || input.CharacterGuid == 0
            || input.Currency is not ("eur" or "credits") || input.ExpectedAmountCents is <= 0 or > ShopSnapshot.MaximumBalanceCents
            || string.IsNullOrWhiteSpace(input.CatalogRevision) || input.CatalogRevision.Length > 80)
            throw new ShopFundingException("shop-invalid-order", 400);
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await FundingExecute(connection, transaction, """
            INSERT INTO atlas_shop_wallet(account_id,updated_at) VALUES(@account,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE account_id=account_id;
            """, token, ("@account", accountId));
        FundingWallet before = await ReadFundingWallet(connection, transaction, accountId, true, token);
        await using (MySqlCommand existing = FundingCommand(connection, transaction,
            "SELECT * FROM atlas_shop_order WHERE account_id=@account AND idempotency_key=@key;", ("@account", accountId), ("@key", input.IdempotencyKey)))
        {
            await using MySqlDataReader reader = await existing.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                ShopOrder order = ReadShopOrder(reader);
                if (order.OfferId != input.OfferId || order.CharacterGuid != input.CharacterGuid || order.Currency != input.Currency
                    || order.AmountCents != input.ExpectedAmountCents || reader.GetString("catalog_revision") != input.CatalogRevision
                    || reader.GetUInt32("realm_id") != options.RealmId)
                    throw new ShopFundingException("shop-idempotency-conflict");
                return order; // Replays remain readable even if delivery or sales are paused.
            }
        }
        ShopSnapshot current = catalog.CreateSnapshot([]);
        long price = current.Offers.Single(o => o.Id == input.OfferId).Prices.Single(p => p.Currency == input.Currency).Amount;
        if (current.CatalogRevision != input.CatalogRevision || price != input.ExpectedAmountCents)
            throw new ShopFundingException("shop-price-changed");
        if (!options.RenameEnabled || !await ShopDeliveryHealthy(connection, transaction, options, token))
            throw new ShopFundingException("shop-delivery-unavailable", 503);
        if (before.Debt != 0) throw new ShopFundingException("shop-wallet-debt");
        if ((input.Currency == "eur" ? before.Euro - before.Held : before.Credits) < price)
            throw new ShopFundingException("shop-insufficient-funds");
        await using (MySqlCommand quota = FundingCommand(connection, transaction,
            "SELECT COUNT(*) FROM atlas_shop_order WHERE account_id=@account AND created_at>UTC_TIMESTAMP(6)-INTERVAL 24 HOUR;", ("@account", accountId)))
            if (Convert.ToInt32(await quota.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) >= 100)
                throw new ShopFundingException("shop-order-limit", 429);
        string name;
        await using (MySqlCommand character = FundingCommand(connection, transaction, $"""
            SELECT name,online,at_login FROM {QuoteArmoryDatabase(_options.CharacterDatabaseName)}.characters
            WHERE guid=@guid AND account=@account AND deleteDate IS NULL FOR UPDATE;
            """, ("@guid", input.CharacterGuid), ("@account", accountId)))
        {
            await using MySqlDataReader reader = await character.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new ShopFundingException("shop-character-unavailable");
            name = reader.GetString("name");
            if (reader.GetByte("online") != 0) throw new ShopFundingException("shop-character-online");
            if ((reader.GetUInt32("at_login") & 1) != 0) throw new ShopFundingException("shop-rename-already-pending");
        }
        string id = Guid.NewGuid().ToString("N");
        await FundingExecute(connection, transaction, """
            INSERT INTO atlas_shop_order(id,account_id,realm_id,character_guid,character_name,idempotency_key,offer_id,catalog_revision,currency,amount_cents,created_at,updated_at)
            VALUES(@id,@account,@realm,@guid,@name,@key,@offer,@revision,@currency,@amount,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            """, token, ("@id", id), ("@account", accountId), ("@realm", options.RealmId), ("@guid", input.CharacterGuid), ("@name", name),
            ("@key", input.IdempotencyKey), ("@offer", input.OfferId), ("@revision", input.CatalogRevision), ("@currency", input.Currency), ("@amount", price));
        FundingWallet after = input.Currency == "eur" ? before with { Euro = before.Euro - price } : before with { Credits = before.Credits - price };
        await WriteShopWallet(connection, transaction, accountId, after, token);
        await AppendShopOrderEvent(connection, transaction, id, accountId, "purchase", input.Currency, -price, after, token);
        ShopOrder created = (await FindShopOrder(connection, transaction, accountId, id, false, token))!;
        await transaction.CommitAsync(token);
        return created;
    }

    internal async Task<ShopOrder> RefundShopOrderAsync(uint accountId, string id, bool cancelPending, CancellationToken token)
    {
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        FundingWallet before = await ReadFundingWallet(connection, transaction, accountId, true, token);
        ShopOrder order = await FindShopOrder(connection, transaction, accountId, id, true, token)
            ?? throw new ShopFundingException("shop-order-not-found", 404);
        if (order.Status == "refunded") return order;
        if (order.Status != "rejected" && !(cancelPending && order.Status == "pending"))
            throw new ShopFundingException("shop-order-already-delivered");
        // A refunded euro purchase first repays any debt created by a reversed top-up.
        long debtPaid = order.Currency == "eur" ? Math.Min(before.Debt, order.AmountCents) : 0;
        FundingWallet after = order.Currency == "eur"
            ? before with { Euro = checked(before.Euro + order.AmountCents - debtPaid), Debt = before.Debt - debtPaid }
            : before with { Credits = checked(before.Credits + order.AmountCents) };
        await WriteShopWallet(connection, transaction, accountId, after, token);
        await FundingExecute(connection, transaction, """
            UPDATE atlas_shop_order SET status='refunded',reason=COALESCE(reason,'cancelled'),updated_at=UTC_TIMESTAMP(6) WHERE id=@id;
            """, token, ("@id", id));
        await AppendShopOrderEvent(connection, transaction, id, accountId, "refund", order.Currency, order.AmountCents, after, token);
        ShopOrder refunded = (await FindShopOrder(connection, transaction, accountId, id, false, token))!;
        await transaction.CommitAsync(token);
        return refunded;
    }

    internal async Task ReconcileShopOrdersAsync(CancellationToken token)
    {
        List<(uint Account, string Id)> rejected = [];
        await using (MySqlConnection connection = await OpenAsync(token))
        await using (MySqlCommand command = FundingCommand(connection, null, "SELECT account_id,id FROM atlas_shop_order WHERE status='rejected' ORDER BY updated_at,sequence_id LIMIT 100;"))
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) rejected.Add((reader.GetUInt32(0), reader.GetString(1)));
        List<Exception> failures = [];
        foreach (var order in rejected)
        {
            try { await RefundShopOrderAsync(order.Account, order.Id, false, token); }
            catch (Exception error) when (error is MySqlException or ShopFundingException or OverflowException) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Some order refunds require a retry.", failures);
    }

    internal async Task<ShopSnapshot> AddShopPurchasesAsync(uint accountId, ShopSnapshot snapshot, ShopPurchaseOptions options, CancellationToken token)
    {
        bool healthy = await ShopDeliveryHealthyAsync(options, token);
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        List<ShopOrder> orders = [];
        await using (MySqlCommand command = FundingCommand(connection, transaction,
            "SELECT * FROM atlas_shop_order WHERE account_id=@account AND realm_id=@realm ORDER BY status IN ('pending','rejected') DESC,sequence_id DESC LIMIT 100;", ("@account", accountId), ("@realm", options.RealmId)))
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) orders.Add(ReadShopOrder(reader));
        Dictionary<uint, bool> renameFlags = [];
        await using (MySqlCommand command = FundingCommand(connection, transaction, $"""
            SELECT guid,(at_login & 1)<>0 AS pending FROM {QuoteArmoryDatabase(_options.CharacterDatabaseName)}.characters
            WHERE account=@account AND deleteDate IS NULL LIMIT 51;
            """, ("@account", accountId)))
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) renameFlags.Add(reader.GetUInt32(0), reader.GetBoolean(1));
        FundingWallet wallet = await ReadFundingWallet(connection, transaction, accountId, false, token);
        List<ShopTransaction> history = (snapshot.History ?? []).ToList();
        await using (MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT e.*,o.character_name FROM atlas_shop_order_ledger e JOIN atlas_shop_order o ON o.id=e.order_id
            WHERE e.account_id=@account ORDER BY e.id DESC LIMIT 100;
            """, ("@account", accountId)))
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                string currency = reader.GetString("currency"), kind = reader.GetString("kind");
                history.Add(new("order-" + reader.GetInt64("id").ToString(CultureInfo.InvariantCulture), FundingUtc(reader, "created_at"), kind,
                    currency, reader.GetInt64("amount_cents"), "completed",
                    kind == "purchase" ? new("Achat : changement de nom", "Purchase: name change") : new("Remboursement : changement de nom", "Refund: name change"),
                    reader.GetString("character_name"), currency == "eur" ? reader.GetInt64("euro_after") - reader.GetInt64("held_after") : reader.GetInt64("credit_after")));
            }
        return snapshot with { CheckoutAvailable = healthy, Purchases = new(healthy, orders), EuroBalanceCents = wallet.Euro - wallet.Held,
            ManualFunding = snapshot.ManualFunding is { } funding ? funding with { HeldCents = wallet.Held, DebtCents = wallet.Debt } : null,
            CreditBalanceEuroCents = wallet.Credits, History = history.OrderByDescending(h => h.OccurredAtUtc).ThenByDescending(h => h.Id).Take(100).ToArray(),
            Characters = snapshot.Characters.Where(c => renameFlags.ContainsKey(c.Guid)).Select(c => c with {
                RenamePending = renameFlags[c.Guid] || orders.Any(o => o.CharacterGuid == c.Guid && o.Status == "pending") }).ToArray() };
    }

    private static async Task<ShopOrder?> FindShopOrder(MySqlConnection connection, MySqlTransaction transaction, uint account, string id, bool forUpdate, CancellationToken token)
    {
        await using MySqlCommand command = FundingCommand(connection, transaction,
            "SELECT * FROM atlas_shop_order WHERE id=@id AND account_id=@account" + (forUpdate ? " FOR UPDATE;" : ";"), ("@id", id), ("@account", account));
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadShopOrder(reader) : null;
    }
    private static async Task<long> ShopEuroRefundReserve(MySqlConnection connection, MySqlTransaction transaction, uint account, CancellationToken token)
    {
        await using MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT COALESCE(SUM(amount_cents),0) FROM atlas_shop_order WHERE account_id=@account AND currency='eur' AND status IN ('pending','rejected');
            """, ("@account", account));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }
    private static ShopOrder ReadShopOrder(MySqlDataReader r) => new(r.GetString("id"), r.GetString("offer_id"), r.GetUInt32("character_guid"),
        r.GetString("character_name"), r.GetString("currency"), r.GetInt64("amount_cents"), r.GetString("status"), FundingNullable(r, "reason"), FundingUtc(r, "created_at"), FundingUtc(r, "updated_at"), r.GetString("idempotency_key"));
    private static Task WriteShopWallet(MySqlConnection c, MySqlTransaction t, uint account, FundingWallet wallet, CancellationToken token)
        => FundingExecute(c, t, "UPDATE atlas_shop_wallet SET euro_cents=@euro,credit_cents=@credits,debt_cents=@debt,updated_at=UTC_TIMESTAMP(6) WHERE account_id=@account;",
            token, ("@euro", wallet.Euro), ("@credits", wallet.Credits), ("@debt", wallet.Debt), ("@account", account));
    private static Task AppendShopOrderEvent(MySqlConnection c, MySqlTransaction t, string id, uint account, string kind, string currency, long amount, FundingWallet wallet, CancellationToken token)
        => FundingExecute(c, t, """
            INSERT INTO atlas_shop_order_ledger(order_id,account_id,kind,currency,amount_cents,euro_after,credit_after,held_after,debt_after,created_at)
            VALUES(@id,@account,@kind,@currency,@amount,@euro,@credits,@held,@debt,UTC_TIMESTAMP(6));
            """, token, ("@id", id), ("@account", account), ("@kind", kind), ("@currency", currency), ("@amount", amount),
            ("@euro", wallet.Euro), ("@credits", wallet.Credits), ("@held", wallet.Held), ("@debt", wallet.Debt));
}
