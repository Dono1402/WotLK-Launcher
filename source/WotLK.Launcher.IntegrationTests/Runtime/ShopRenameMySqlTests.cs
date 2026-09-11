using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Presentation;

internal static partial class ShopManualFundingMySqlTests
{
    private static async Task RunRenameStageAsync(LauncherServerOptions server, MySqlConnection connection, WebApplication app,
        HttpClient http, ShopPurchaseOptions options)
    {
        int initial = _checks;
        string characters = server.CharacterDatabaseName;
        string auth = new MySqlConnectionStringBuilder(server.ConnectionString).Database;
        string emitter = Environment.GetEnvironmentVariable("ATLAS_SHOP_SQL_EMITTER")
            ?? throw new InvalidOperationException("Compile the actual module SQL emitter and set ATLAS_SHOP_SQL_EMITTER.");
        LauncherSchemaMigration migration = new EmbeddedLauncherSchemaMigrationSource().Load().Single(m => m.Version == 12);
        await Sql(connection, migration.Sql[..(migration.Sql.IndexOf(';') + 1)]);
        server.MaximumSchemaVersion = 12;
        await new LauncherSchemaMigrator(server).MigrateAsync();
        Check((await new LauncherSchemaMigrator(server).MigrateAsync()).Where(m => m.Version <= 12).All(m => m.State == LauncherSchemaMigrationState.AlreadyApplied),
            "Schema 0012 recovers interrupted DDL and validates on replay.");
        await Sql(connection, "ALTER TABLE atlas_shop_order MODIFY active_character INT UNSIGNED GENERATED ALWAYS AS (IF(status='delivered',character_guid,NULL)) STORED;");
        try { await new LauncherSchemaMigrator(server).MigrateAsync(); throw new Exception("A changed generated expression was accepted."); }
        catch (InvalidOperationException error) when (error.Message.Contains("active-character", StringComparison.Ordinal))
        { Check(true, "Schema validation rejects a changed active-character expression, even when its column and index names match."); }
        await Sql(connection, "ALTER TABLE atlas_shop_order MODIFY active_character INT UNSIGNED GENERATED ALWAYS AS (IF(status='pending',character_guid,NULL)) STORED;");
        await Sql(connection, $"""
            ALTER TABLE `{characters}`.characters ADD at_login SMALLINT UNSIGNED NOT NULL DEFAULT 0, ADD deleteDate BIGINT UNSIGNED NULL;
            INSERT INTO `{characters}`.characters(guid,account,name,level,online,money) VALUES
                (601,6,'Elune',80,0,100000),(602,6,'Lune',80,0,200000),(603,6,'Soleil',80,0,300000),
                (604,6,'Brume',80,0,400000),(605,6,'Aube',80,0,500000);
            UPDATE atlas_shop_wallet SET euro_cents=10000,credit_cents=10000,held_cents=0,debt_cents=0 WHERE account_id=6;
            """);
        options.StorageAvailable = true;
        app.Services.GetRequiredService<ShopManualFundingOptions>().PurchaseStorageAvailable = true;
        LauncherDatabase database = app.Services.GetRequiredService<LauncherDatabase>();
        using HttpClient playerHttp = new() { BaseAddress = http.BaseAddress };
        playerHttp.DefaultRequestHeaders.Authorization = new("Bearer", "funding-client");
        LauncherShopApiClient api = new(playerHttp, new Uri(http.BaseAddress!, "api/v1/"));
        ShopSnapshot snapshot = await api.ReadAsync(default);
        Check(!snapshot.CheckoutAvailable && snapshot.Purchases is { RenameAvailable: false, Orders.Count: 0 }, "Migration alone does not enable checkout.");
        options.RenameEnabled = true;
        await Expect(null, Input(601), HttpStatusCode.Unauthorized);
        await Expect("client", Input(601), HttpStatusCode.ServiceUnavailable);
        await Heartbeat();
        snapshot = await api.ReadAsync(default);
        Check(snapshot.CheckoutAvailable && snapshot.Purchases!.RenameAvailable && snapshot.Characters.All(c => c.RenamePending == false),
            "Only explicit enablement with a fresh, matching realm worker exposes rename checkout.");
        await Expect("client", Input(601) with { ExpectedAmountCents = 1 }, HttpStatusCode.Conflict);
        await Expect("client", Input(601) with { CatalogRevision = "old" }, HttpStatusCode.Conflict);
        await Expect("client", Input(601) with { OfferId = "character-race-change" }, HttpStatusCode.BadRequest);
        await Expect("client", Input(201), HttpStatusCode.Conflict);
        await Expect("client", new { idempotencyKey = Key(), offerId = "character-rename", characterGuid = 601, currency = "eur", expectedAmountCents = 500, catalogRevision = snapshot.CatalogRevision, accountId = 1 }, HttpStatusCode.BadRequest);
        await Sql(connection, $"UPDATE `{characters}`.characters SET online=1 WHERE guid=601;");
        await Expect("client", Input(601), HttpStatusCode.Conflict);
        await Sql(connection, $"UPDATE `{characters}`.characters SET online=0,at_login=1 WHERE guid=601;");
        await Expect("client", Input(601), HttpStatusCode.Conflict);
        await Sql(connection, $"UPDATE `{characters}`.characters SET at_login=8 WHERE guid=601;");
        await Sql(connection, "UPDATE atlas_shop_wallet SET held_cents=9600 WHERE account_id=6;");
        await Expect("client", Input(601), HttpStatusCode.Conflict);
        await Sql(connection, "UPDATE atlas_shop_wallet SET held_cents=0,debt_cents=10 WHERE account_id=6;");
        await Expect("client", Input(601, "credits"), HttpStatusCode.Conflict);
        await Sql(connection, "UPDATE atlas_shop_wallet SET debt_cents=0 WHERE account_id=6;");
        Check(await Amount("SELECT COUNT(*) FROM atlas_shop_order;") == 0 && await Wallet() == 10000, "All rejected checks leave wallets and orders unchanged.");

        ShopCreateOrder attempt = Input(601);
        ShopOrder[] duplicate = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => api.CreateOrderAsync(attempt, default)));
        ShopOrder order = duplicate[0];
        Check(duplicate.All(o => o.Id == order.Id) && await Wallet() == 9500 && await Events(order.Id) == 1,
            "Six concurrent retries produce one durable order and exactly one debit.");
        Check(await Flag(601) == 8, "The API never grants the in-game flag.");
        await Expect("client", attempt with { CharacterGuid = 602 }, HttpStatusCode.Conflict);
        await Expect("client", Input(601), HttpStatusCode.Conflict);
        using (HttpRequestMessage foreignCancel = new(HttpMethod.Post, $"/api/v1/shop/orders/{order.Id}/cancel"))
        {
            foreignCancel.Headers.Authorization = new("Bearer", "funding-one");
            using HttpResponseMessage response = await http.SendAsync(foreignCancel);
            Check(response.StatusCode == HttpStatusCode.NotFound, "Another account cannot read/cancel an owned order.");
        }
        Check((await api.ReadAsync(default)).Characters.Single(c => c.Guid == 601).RenamePending == true, "Pending durable orders block a new purchase after reconnect.");
        options.RenameEnabled = false;
        Check((await api.CreateOrderAsync(attempt, default)).Id == order.Id, "Idempotent recovery works while sales are disabled.");
        options.RenameEnabled = true;
        await Sql(connection, $"UPDATE `{characters}`.characters SET online=1 WHERE guid=601;");
        await Deliver(order);
        Check(await Flag(601) == 8 && (await Order(order.Id)).Status == "pending", "A character that reconnects stays pending without activation.");
        await Sql(connection, $"UPDATE `{characters}`.characters SET online=0 WHERE guid=601;");
        await Sql(connection, "CREATE TRIGGER fixture_block_delivery BEFORE UPDATE ON atlas_shop_order FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='fixture-receipt-failure';");
        try { await Deliver(order); throw new InvalidOperationException("Injected receipt failure was ignored."); }
        catch (MySqlException) { Check(await Flag(601) == 8, "A receipt failure rolls back the character entitlement in the same statement."); }
        await Sql(connection, "DROP TRIGGER fixture_block_delivery;");
        await Deliver(order);
        Check(await Flag(601) == 9 && (await Order(order.Id)).Status == "delivered" && await Wallet() == 9500,
            "Actual C++ module SQL delivers one rename flag, preserves unrelated flags and records delivery atomically.");
        await Sql(connection, $"UPDATE `{characters}`.characters SET at_login=8,name='Selene' WHERE guid=601;");
        await Deliver(order); await Deliver(order);
        Check(await Flag(601) == 8 && (await api.CreateOrderAsync(attempt, default)).Status == "delivered" && await Events(order.Id) == 1,
            "Replaying an old delivered order after simulated native consumption cannot regrant or recharge it.");
        await ExpectCancel(order.Id, HttpStatusCode.Conflict);

        ShopOrder credits = await api.CreateOrderAsync(Input(602, "credits"), default);
        Check(await Wallet("credit_cents") == 9300 && await Wallet() == 9500, "Credits debit their own balance at the server price of 700 cents.");
        ShopOrder[] cancelled = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => api.CancelOrderAsync(credits.Id, default)));
        await Deliver(credits);
        Check(cancelled.All(o => o.Status == "refunded") && await Wallet("credit_cents") == 10000 && await Flag(602) == 0 && await Events(credits.Id) == 2,
            "Concurrent cancellation refunds once; a cancelled order can never activate later.");

        ShopOrder removed = await api.CreateOrderAsync(Input(602), default);
        await Sql(connection, $"UPDATE `{characters}`.characters SET account=2 WHERE guid=602;");
        await Deliver(removed);
        Check((await Order(removed.Id)).Status == "rejected" && await Flag(602) == 0, "Ownership is rechecked during delivery.");
        await database.ReconcileShopOrdersAsync(default); await database.ReconcileShopOrdersAsync(default);
        Check((await Order(removed.Id)).Status == "refunded" && await Wallet() == 9500 && await Events(removed.Id) == 2,
            "The recovery worker refunds rejected deliveries exactly once without the launcher being open.");
        await Sql(connection, $"UPDATE `{characters}`.characters SET account=6 WHERE guid=602;");
        ShopOrder flagged = await api.CreateOrderAsync(Input(602), default);
        await Sql(connection, $"UPDATE `{characters}`.characters SET at_login=1 WHERE guid=602;");
        await Deliver(flagged); await database.ReconcileShopOrdersAsync(default);
        Check((await Order(flagged.Id)) is { Status: "refunded", Reason: "rename-already-pending" }, "An intervening native rename entitlement is refunded, never charged twice.");
        await Sql(connection, $"UPDATE `{characters}`.characters SET at_login=0 WHERE guid=602;");

        ShopOrder debt = await api.CreateOrderAsync(Input(602), default);
        await Sql(connection, "UPDATE atlas_shop_wallet SET debt_cents=300 WHERE account_id=6;");
        await api.CancelOrderAsync(debt.Id, default);
        Check(await Wallet() == 9200 && await Wallet("debt_cents") == 0, "Euro refunds first repay a subsequently created reversal debt.");
        await Sql(connection, "CREATE TRIGGER fixture_block_order_event BEFORE INSERT ON atlas_shop_order_ledger FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='fixture-ledger-failure';");
        ShopCreateOrder rollback = Input(602);
        await Expect("client", rollback, HttpStatusCode.ServiceUnavailable);
        Check(await Wallet() == 9200, "A debit journal failure rolls back the wallet and order.");
        await Sql(connection, "DROP TRIGGER fixture_block_order_event;");
        ShopOrder recovered = await api.CreateOrderAsync(rollback, default); await api.CancelOrderAsync(recovered.Id, default);
        Check(await Wallet() == 9200, "The same failed request can be safely retried and refunded.");

        // Competing distinct characters must share the same account wallet lock.
        await Sql(connection, "UPDATE atlas_shop_wallet SET euro_cents=800 WHERE account_id=6;");
        Task<HttpStatusCode>[] competing = new[] { 603u, 604u }.Select(async guid =>
        {
            using HttpResponseMessage response = await playerHttp.PostAsJsonAsync("/api/v1/shop/orders", Input(guid)); return response.StatusCode;
        }).ToArray();
        HttpStatusCode[] outcomes = await Task.WhenAll(competing);
        Check(outcomes.Count(s => s == HttpStatusCode.OK) == 1 && outcomes.Count(s => s == HttpStatusCode.Conflict) == 1 && await Wallet() == 300,
            "Concurrent different purchases cannot overspend the shared wallet.");
        foreach (ShopOrder pending in (await api.ReadAsync(default)).Purchases!.Orders.Where(o => o.Status == "pending")) await api.CancelOrderAsync(pending.Id, default);

        // Real launcher state -> client -> authenticated HTTP -> MySQL, with a lost response.
        using ShopUiState state = new(); state.Configure(api.ReadAsync);
        int sends = 0;
        state.ConfigurePurchases(new(async (input, token) =>
        {
            ShopOrder saved = await api.CreateOrderAsync(input, token);
            if (++sends == 1) throw new HttpRequestException("Fixture: response lost after commit");
            return saved;
        }, api.CancelOrderAsync));
        await state.RefreshAsync(); state.OpenService(state.Offers.Single(o => o.Offer.Id == "character-rename"));
        state.SelectedCharacter = state.Characters.Single(c => c.Character.Guid == 605);
        Check(state.CanPurchase, "The real launcher enables only the eligible, funded rename service.");
        await state.PurchaseAsync();
        Check(state.CanPurchase && !state.CanEditPurchase && state.PurchaseActionLabel.Contains("commande", StringComparison.OrdinalIgnoreCase),
            "A lost response retains the exact purchase key and freezes recipient/currency for recovery.");
        await state.PurchaseAsync();
        Check(sends == 2 && !state.CanPurchase && state.CanCancelPurchase && await Wallet() == 300,
            "Retrying the real client after a lost response recovers the order without another debit.");
        await state.CancelPurchaseAsync();
        Check(await Wallet() == 800 && !state.CanCancelPurchase, "The launcher cancellation restores the balance and refreshes the receipt.");
        state.ResetSession();
        Check(!state.HasPurchaseOrders && !state.CanPurchase && state.PurchaseReceipt == "", "Logout clears purchase drafts, receipts and wallet state.");
        snapshot = await api.ReadAsync(default); snapshot.Validate();
        Check(snapshot.History!.Any(h => h.Kind == "purchase" && h.Currency == "credits") && snapshot.History!.Any(h => h.Kind == "refund" && h.Currency == "credits")
            && snapshot.Purchases!.Orders.Single(o => o.Id == order.Id).CharacterName == "Elune", "History retains both currencies and the original beneficiary name after rename.");
        for (int i = 0; i < 5; i++)
        {
            await Sql(connection, $"UPDATE atlas_shop_wallet SET euro_cents=800 WHERE account_id=6; UPDATE `{characters}`.characters SET at_login=0 WHERE guid=603;");
            ShopOrder racing = await api.CreateOrderAsync(Input(603), default);
            Task delivery = Deliver(racing);
            Task<HttpResponseMessage> cancellation = playerHttp.PostAsync($"/api/v1/shop/orders/{racing.Id}/cancel", null);
            await delivery;
            using HttpResponseMessage response = await cancellation;
            ShopOrder final = await Order(racing.Id);
            Check((final.Status == "delivered" && response.StatusCode == HttpStatusCode.Conflict && await Flag(603) == 1 && await Wallet() == 300 && await Events(racing.Id) == 1)
                || (final.Status == "refunded" && response.IsSuccessStatusCode && await Flag(603) == 0 && await Wallet() == 800 && await Events(racing.Id) == 2),
                "Concurrent delivery/cancellation yields exactly one valid outcome: charged activation or refunded non-activation.");
        }
        await Sql(connection, "UPDATE atlas_shop_wallet SET euro_cents=1000000000 WHERE account_id=6;");
        ShopOrder reserve = await api.CreateOrderAsync(Input(602), default);
        try
        {
            await database.CreateShopTopUpAsync(6, new(Key(), 100), app.Services.GetRequiredService<ShopManualFundingOptions>(), default);
            throw new Exception("A top-up consumed reserved refund headroom.");
        }
        catch (ShopFundingException error) when (error.Code == "shop-wallet-limit")
        { Check(true, "Pending order refunds retain wallet headroom against new top-ups."); }
        await api.CancelOrderAsync(reserve.Id, default);
        Check(await Wallet() == ShopSnapshot.MaximumBalanceCents, "A refund at the wallet ceiling restores every cent.");
        await Sql(connection, "UPDATE atlas_shop_delivery_health SET protocol=2;");
        Check(!(await api.ReadAsync(default)).CheckoutAvailable, "An incompatible module protocol cannot enable checkout.");
        await Heartbeat();
        await Sql(connection, "UPDATE atlas_shop_delivery_health SET last_seen_at=UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE;");
        Check(!(await api.ReadAsync(default)).CheckoutAvailable, "A stale worker heartbeat closes new checkout while preserving order history.");
        await Expect("client", Input(602), HttpStatusCode.ServiceUnavailable);
        Console.WriteLine($"Rename shop MySQL/API/C++ SQL PASS: {_checks - initial} additional checks; durable debit, native flag SQL, crash/replay, ownership, cancellation/refund, wallet isolation and real client recovery. No live realm or game client used.");

        ShopCreateOrder Input(uint guid, string currency = "eur") => new(Key(), "character-rename", guid, currency, currency == "eur" ? 500 : 700, snapshot.CatalogRevision);
        Task<long> Wallet(string column = "euro_cents") => Amount("SELECT " + column + " FROM atlas_shop_wallet WHERE account_id=6;");
        Task<long> Flag(uint guid) => Amount($"SELECT at_login FROM `{characters}`.characters WHERE guid={guid};");
        Task<long> Events(string id) => Amount($"SELECT COUNT(*) FROM atlas_shop_order_ledger WHERE order_id='{id}';");
        async Task<long> Amount(string sql) => Convert.ToInt64(await Scalar(connection, sql), CultureInfo.InvariantCulture);
        async Task<ShopOrder> Order(string id) => (await api.ReadAsync(default)).Purchases!.Orders.Single(o => o.Id == id);
        Task Heartbeat() => Sql(connection, "INSERT INTO atlas_shop_delivery_health VALUES(1,1,@db,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE protocol=1,character_database=VALUES(character_database),last_seen_at=UTC_TIMESTAMP(6);", ("@db", characters));
        async Task Expect(string? account, object input, HttpStatusCode expected)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/shop/orders") { Content = JsonContent.Create(input) };
            if (account is not null) request.Headers.Authorization = new("Bearer", "funding-" + account);
            using HttpResponseMessage response = await http.SendAsync(request);
            Check(response.StatusCode == expected, $"Order expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        async Task ExpectCancel(string id, HttpStatusCode expected)
        { using HttpResponseMessage response = await playerHttp.PostAsync($"/api/v1/shop/orders/{id}/cancel", null); Check(response.StatusCode == expected, "Delivered activation cannot be refunded by cancellation."); }
        async Task Deliver(ShopOrder delivery)
        {
            long sequence = await Amount($"SELECT sequence_id FROM atlas_shop_order WHERE id='{delivery.Id}';");
            ProcessStartInfo start = new(emitter) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { characters, "1", sequence.ToString(CultureInfo.InvariantCulture), "6", delivery.CharacterGuid.ToString(CultureInfo.InvariantCulture), auth }) start.ArgumentList.Add(value);
            using Process process = Process.Start(start)!;
            string sql = await process.StandardOutput.ReadToEndAsync(); string errors = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            Check(process.ExitCode == 0 && sql.Contains("UPDATE", StringComparison.Ordinal), "The compiled core-module SQL emitter succeeded: " + errors);
            await using MySqlConnection worker = new(server.ConnectionString); await worker.OpenAsync();
            await using MySqlTransaction transaction = await worker.BeginTransactionAsync();
            await using MySqlCommand command = new(sql, worker, transaction); await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
        }
    }
}
