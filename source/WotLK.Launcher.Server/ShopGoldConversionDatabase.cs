using System.Data;
using System.Globalization;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    private async Task<bool> ShopConversionHealthy(MySqlConnection connection, MySqlTransaction? transaction,
        ShopGoldConversionOptions options, CancellationToken token)
    {
        if (!options.Enabled) return false;
        await using MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT COUNT(*) FROM atlas_shop_conversion_health WHERE realm_id=@realm AND protocol=1
            AND character_database=@characters AND copper_per_cent=@rate
            AND last_seen_at BETWEEN UTC_TIMESTAMP(6)-INTERVAL 30 SECOND AND UTC_TIMESTAMP(6)+INTERVAL 5 SECOND;
            """, ("@realm", options.RealmId), ("@characters", _options.CharacterDatabaseName),
            ("@rate", ShopGoldConversionRate.Default.CopperPerEuroCent));
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    internal async Task<ShopGoldConversion> CreateShopGoldConversionAsync(uint account, ShopCreateGoldConversion input,
        ShopCatalog catalog, ShopGoldConversionOptions options, CancellationToken token)
    {
        if (!ShopFundingValidation.IsId(input.IdempotencyKey) || input.CharacterGuid == 0
            || input.OfferedCopper == 0 || input.OfferedCopper % 10_000 != 0
            || input.ExpectedCreditCents is <= 0 or > ShopSnapshot.MaximumBalanceCents
            || string.IsNullOrWhiteSpace(input.CatalogRevision) || input.CatalogRevision.Length > 80)
            throw new ShopFundingException("shop-invalid-conversion", 400);
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await FundingExecute(connection, transaction, """
            INSERT INTO atlas_shop_wallet(account_id,updated_at) VALUES(@account,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE account_id=account_id;
            """, token, ("@account", account));
        FundingWallet wallet = await ReadFundingWallet(connection, transaction, account, true, token);
        await using (MySqlCommand existing = FundingCommand(connection, transaction, """
            SELECT * FROM atlas_shop_gold_conversion WHERE account_id=@account AND idempotency_key=@key;
            """, ("@account", account), ("@key", input.IdempotencyKey)))
        await using (MySqlDataReader reader = await existing.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token))
            {
                ShopGoldConversion found = ReadGoldConversion(reader);
                if (found.CharacterGuid != input.CharacterGuid || found.OfferedCopper != input.OfferedCopper
                    || found.CreditEuroCents != input.ExpectedCreditCents
                    || reader.GetString("catalog_revision") != input.CatalogRevision
                    || reader.GetUInt32("realm_id") != options.RealmId)
                    throw new ShopFundingException("shop-idempotency-conflict", 409);
                return found; // Readable after completion, expiry, or disabling new conversions.
            }
        }
        ShopSnapshot current = catalog.CreateSnapshot([]);
        ShopGoldConversionQuote quote = current.GoldConversion.Quote(input.OfferedCopper);
        if (current.CatalogRevision != input.CatalogRevision || quote.CreditEuroCents != input.ExpectedCreditCents
            || quote.DebitedCopper != input.OfferedCopper || quote.CreditEuroCents <= 0)
            throw new ShopFundingException("shop-price-changed", 409);
        if (!await ShopConversionHealthy(connection, transaction, options, token))
            throw new ShopFundingException("shop-conversion-unavailable", 503);
        if (wallet.Debt != 0) throw new ShopFundingException("shop-wallet-debt", 409);
        long refundable;
        await using (MySqlCommand reserve = FundingCommand(connection, transaction, """
            SELECT COALESCE(SUM(amount_cents),0) FROM atlas_shop_order
            WHERE account_id=@account AND currency='credits' AND status IN ('available','pending','rejected');
            """, ("@account", account)))
            refundable = Convert.ToInt64(await reserve.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        if (wallet.Credits > ShopSnapshot.MaximumBalanceCents - quote.CreditEuroCents - refundable)
            throw new ShopFundingException("shop-credit-limit", 409);
        await using (MySqlCommand pending = FundingCommand(connection, transaction,
            "SELECT COUNT(*) FROM atlas_shop_gold_conversion WHERE account_id=@account AND status='pending';", ("@account", account)))
            if (Convert.ToInt32(await pending.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0)
                throw new ShopFundingException("shop-conversion-pending", 409);
        await using (MySqlCommand quota = FundingCommand(connection, transaction, """
            SELECT COUNT(*) FROM atlas_shop_gold_conversion WHERE account_id=@account
            AND created_at>UTC_TIMESTAMP(6)-INTERVAL 24 HOUR;
            """, ("@account", account)))
            if (Convert.ToInt32(await quota.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) >= 100)
                throw new ShopFundingException("shop-conversion-limit", 429);
        string name;
        // This is an early eligibility check only. The core rechecks its live player
        // state and saved money under its write guard before committing any debit.
        await using (MySqlCommand character = FundingCommand(connection, transaction, $"""
            SELECT name,online,money FROM {QuoteArmoryDatabase(_options.CharacterDatabaseName)}.characters
            WHERE guid=@guid AND account=@account AND deleteDate IS NULL;
            """, ("@guid", input.CharacterGuid), ("@account", account)))
        await using (MySqlDataReader reader = await character.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new ShopFundingException("shop-character-unavailable", 409);
            if (reader.GetByte("online") != 0) throw new ShopFundingException("shop-character-online", 409);
            if (reader.GetUInt32("money") < input.OfferedCopper) throw new ShopFundingException("shop-insufficient-gold", 409);
            name = reader.GetString("name");
        }
        string id = Guid.NewGuid().ToString("N");
        await FundingExecute(connection, transaction, """
            INSERT INTO atlas_shop_gold_conversion(id,account_id,realm_id,character_guid,character_name,
                idempotency_key,catalog_revision,offered_copper,copper_per_cent,credit_cents,status,created_at,updated_at,expires_at)
            VALUES(@id,@account,@realm,@guid,@name,@key,@revision,@copper,@rate,@credits,'pending',
                UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)+INTERVAL 2 MINUTE);
            """, token, ("@id", id), ("@account", account), ("@realm", options.RealmId), ("@guid", input.CharacterGuid),
            ("@name", name), ("@key", input.IdempotencyKey), ("@revision", input.CatalogRevision),
            ("@copper", quote.DebitedCopper), ("@rate", current.GoldConversion.CopperPerEuroCent), ("@credits", quote.CreditEuroCents));
        ShopGoldConversion created = (await FindGoldConversion(connection, transaction, account, id, options.RealmId, token))!;
        await transaction.CommitAsync(token);
        return created;
    }

    internal async Task<ShopGoldConversion?> ReadShopGoldConversionAsync(uint account, string id,
        ShopGoldConversionOptions options, CancellationToken token)
    {
        await using MySqlConnection connection = await OpenAsync(token);
        return await FindGoldConversion(connection, null, account, id, options.RealmId, token);
    }

    private static async Task<ShopGoldConversion?> FindGoldConversion(MySqlConnection connection,
        MySqlTransaction? transaction, uint account, string id, uint realm, CancellationToken token)
    {
        await using MySqlCommand command = FundingCommand(connection, transaction,
            "SELECT * FROM atlas_shop_gold_conversion WHERE account_id=@account AND realm_id=@realm AND id=@id;",
            ("@account", account), ("@realm", realm), ("@id", id));
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadGoldConversion(reader) : null;
    }

    private static ShopGoldConversion ReadGoldConversion(MySqlDataReader reader) => new(
        reader.GetString("id"), reader.GetString("idempotency_key"), reader.GetUInt32("character_guid"),
        reader.GetString("character_name"), reader.GetUInt32("offered_copper"), reader.GetInt64("credit_cents"),
        reader.GetString("status"), reader.IsDBNull(reader.GetOrdinal("reason")) ? null : reader.GetString("reason"),
        reader.IsDBNull(reader.GetOrdinal("gold_before")) ? null : reader.GetUInt32("gold_before"),
        reader.IsDBNull(reader.GetOrdinal("gold_after")) ? null : reader.GetUInt32("gold_after"),
        reader.IsDBNull(reader.GetOrdinal("credit_before")) ? null : reader.GetInt64("credit_before"),
        reader.IsDBNull(reader.GetOrdinal("credit_after")) ? null : reader.GetInt64("credit_after"),
        FundingUtc(reader, "created_at"), FundingUtc(reader, "updated_at"));

    internal async Task<ShopSnapshot> AddShopGoldConversionsAsync(uint account, ShopSnapshot snapshot,
        ShopGoldConversionOptions options, CancellationToken token)
    {
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        bool available = await ShopConversionHealthy(connection, transaction, options, token);
        FundingWallet wallet = await ReadFundingWallet(connection, transaction, account, false, token);
        List<ShopCharacter> characters = [];
        // Money, wallet and conversion receipts must describe the same committed
        // snapshot even when the realm completes a conversion during this HTTP read.
        await using (MySqlCommand command = FundingCommand(connection, transaction, $"""
            SELECT guid,name,level,online,money,at_login FROM {QuoteArmoryDatabase(_options.CharacterDatabaseName)}.characters
            WHERE account=@account AND deleteDate IS NULL ORDER BY name,guid LIMIT 51;
            """, ("@account", account)))
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                bool online = reader.GetByte("online") != 0;
                characters.Add(new(reader.GetUInt32("guid"), reader.GetString("name"), reader.GetByte("level"),
                    online, online ? null : reader.GetUInt32("money"), (reader.GetUInt16("at_login") & 1) != 0));
            }
        if (characters.Count > 50) throw new InvalidDataException("Shop character limit exceeded.");
        List<ShopGoldConversion> conversions = [];
        await using (MySqlCommand command = FundingCommand(connection, transaction, """
            SELECT * FROM atlas_shop_gold_conversion WHERE account_id=@account AND realm_id=@realm
            ORDER BY status='pending' DESC,sequence_id DESC LIMIT 100;
            """, ("@account", account), ("@realm", options.RealmId)))
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) conversions.Add(ReadGoldConversion(reader));
        IEnumerable<ShopTransaction> history = conversions.Where(c => c.Status == "completed").Select(c => new ShopTransaction(
            "conversion-" + c.Id, c.UpdatedAtUtc, "conversion", "credits", c.CreditEuroCents, "completed",
            new("Conversion d’or", "Gold conversion"), c.CharacterName, c.CreditAfterCents, c.OfferedCopper));
        return snapshot with { Conversions = new(available, conversions), Characters = characters,
            EuroBalanceCents = wallet.Euro - wallet.Held, CreditBalanceEuroCents = wallet.Credits,
            ManualFunding = snapshot.ManualFunding is { } funding ? funding with { HeldCents = wallet.Held, DebtCents = wallet.Debt } : null,
            History = history.Concat(snapshot.History ?? []).OrderByDescending(h => h.OccurredAtUtc).ThenByDescending(h => h.Id).Take(100).ToArray() };
    }
}
