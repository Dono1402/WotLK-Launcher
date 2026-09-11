using System.Globalization;
using System.Net;
using System.Net.Http;
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
    private static async Task RunAccountServicesStageAsync(LauncherServerOptions server, MySqlConnection connection,
        WebApplication app, HttpClient http, ShopPurchaseOptions options)
    {
        int initial = _checks;
        string characters = server.CharacterDatabaseName;
        server.MaximumSchemaVersion = 12;
        await new LauncherSchemaMigrator(server).MigrateAsync();
        string legacyId = Key();
        await Sql(connection, """
            INSERT INTO atlas_shop_order(id,account_id,realm_id,character_guid,character_name,idempotency_key,
                offer_id,catalog_revision,currency,amount_cents,status,created_at,updated_at)
            VALUES(@id,6,1,101,'Asteria',@key,'character-rename','legacy','eur',500,'delivered',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            """, ("@id", legacyId), ("@key", Key()));
        LauncherSchemaMigration migration = new EmbeddedLauncherSchemaMigrationSource().Load().Single(m => m.Version == 13);
        await Sql(connection, migration.Sql); // DDL committed, history write interrupted.
        server.MaximumSchemaVersion = 13;
        await new LauncherSchemaMigrator(server).MigrateAsync();
        Check((await new LauncherSchemaMigrator(server).MigrateAsync()).Where(m => m.Version <= 13)
            .All(m => m.State == LauncherSchemaMigrationState.AlreadyApplied), "Schema 0013 safely adopts completed DDL and replays without changes.");
        Check(await Amount("SELECT COUNT(*) FROM atlas_shop_order WHERE status='delivered' AND character_guid=101 AND requested_name IS NULL;") == 1,
            "Account-service migration preserves existing character-bound receipts.");
        await Sql(connection, $"""
            ALTER TABLE `{characters}`.characters ADD at_login SMALLINT UNSIGNED NOT NULL DEFAULT 0, ADD deleteDate BIGINT UNSIGNED NULL;
            UPDATE atlas_shop_wallet SET euro_cents=10000,credit_cents=10000,held_cents=0,debt_cents=0 WHERE account_id=6;
            """);
        options.StorageAvailable = true; options.AccountServicesEnabled = true; options.RenameEnabled = true;
        options.Validate(13);
        try { options.Validate(12); throw new Exception("Account services accepted schema 0012."); }
        catch (InvalidOperationException) { Check(true, "Account-service enablement requires schema 0013."); }
        app.Services.GetRequiredService<ShopManualFundingOptions>().PurchaseStorageAvailable = true;
        using HttpClient playerHttp = new() { BaseAddress = http.BaseAddress };
        playerHttp.DefaultRequestHeaders.Authorization = new("Bearer", "funding-client");
        LauncherShopApiClient api = new(playerHttp, new Uri(http.BaseAddress!, "api/v1/"));
        ShopSnapshot snapshot = await api.ReadAsync(default);
        Check(snapshot.Purchases is { AccountServices: true, RenameAvailable: false } && !snapshot.CheckoutAvailable,
            "An account-mode snapshot never enables checkout without a compatible worker.");
        await Heartbeat(1);
        await Expect(Input(), HttpStatusCode.ServiceUnavailable);
        await Heartbeat(2);
        snapshot = await api.ReadAsync(default);
        Check(snapshot.CheckoutAvailable && snapshot.Characters.Count == 0, "The test account has no characters and a compatible fixture heartbeat.");
        await Expect(Input() with { CharacterGuid = 101 }, HttpStatusCode.BadRequest);
        await Expect(Input() with { ExpectedAmountCents = 1 }, HttpStatusCode.Conflict);
        await Expect(Input() with { CatalogRevision = "stale" }, HttpStatusCode.Conflict);

        ShopCreateOrder attempt = Input();
        ShopOrder[] retries = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => api.CreateOrderAsync(attempt, default)));
        ShopOrder first = retries[0];
        Check(retries.All(o => o.Id == first.Id && o is { Status: "available", CharacterGuid: 0, CharacterName: "" })
            && await Wallet() == 9500 && await Events(first.Id) == 1,
            "Six concurrent retries buy one account service without a character and debit once.");
        snapshot = await api.ReadAsync(default); snapshot.Validate();
        Check(snapshot.History!.Any(h => h.Kind == "purchase" && h.CharacterName is null), "Unassigned services produce a valid history without an empty beneficiary name.");
        await Sql(connection, $"""
            INSERT INTO `{characters}`.characters(guid,account,name,level,online,money,at_login) VALUES(601,6,'Elune',80,1,100000,8);
            """);
        ShopOrder second = await api.CreateOrderAsync(Input("credits"), default);
        Check(second.Status == "available" && await Wallet("credit_cents") == 9300 && await Wallet() == 9500
            && await Amount($"SELECT online=1 AND at_login=8 AND name='Elune' FROM `{characters}`.characters WHERE guid=601;") == 1,
            "A purchase while playing changes only the chosen wallet and order, leaving the active character intact.");
        using (HttpClient foreign = new() { BaseAddress = http.BaseAddress })
        {
            foreign.DefaultRequestHeaders.Authorization = new("Bearer", "funding-one");
            using HttpResponseMessage response = await foreign.PostAsync($"/api/v1/shop/orders/{second.Id}/cancel", null);
            Check(response.StatusCode == HttpStatusCode.NotFound, "Another account cannot cancel an available service.");
        }
        ShopOrder[] cancelled = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => api.CancelOrderAsync(second.Id, default)));
        Check(cancelled.All(o => o.Status == "refunded") && await Wallet("credit_cents") == 10000 && await Events(second.Id) == 2,
            "Concurrent cancellation refunds an unused service exactly once in its original currency.");
        Check((await api.CreateOrderAsync(Input("credits") with { IdempotencyKey = second.IdempotencyKey }, default)).Status == "refunded",
            "A replay of a cancelled account purchase does not create or debit another service.");
        options.RenameEnabled = false;
        Check((await api.CreateOrderAsync(attempt, default)).Id == first.Id, "Receipts remain recoverable while sales are paused.");
        options.RenameEnabled = true;

        // Model a receipt persisted by the future native consumer. This does not test native redemption.
        await Sql(connection, """
            UPDATE atlas_shop_order SET status='consumed',character_guid=601,character_name='Elune',requested_name='Selene',
                redemption_key=@key,updated_at=UTC_TIMESTAMP(6) WHERE id=@id;
            """, ("@id", first.Id), ("@key", Key()));
        ShopOrder consumed = await api.CreateOrderAsync(attempt, default);
        Check(consumed is { Status: "consumed", CharacterGuid: 601, AppliedName: "Selene" }
            && await Wallet() == 9500 && await Events(first.Id) == 1,
            "Purchase retries remain idempotent after a consumer assigns the character and records use.");
        using (HttpResponseMessage response = await playerHttp.PostAsync($"/api/v1/shop/orders/{first.Id}/cancel", null))
            Check(response.StatusCode == HttpStatusCode.Conflict && await Wallet() == 9500, "A consumed service cannot be refunded by cancellation.");
        foreach (string invalid in new[] {
            "UPDATE atlas_shop_order SET character_guid=0 WHERE id=@id;",
            "UPDATE atlas_shop_order SET requested_name=NULL WHERE id=@id;",
            "UPDATE atlas_shop_order SET redemption_key=NULL WHERE id=@id;",
            "UPDATE atlas_shop_order SET status='available' WHERE id=@id;" })
        {
            try { await Sql(connection, invalid, ("@id", first.Id)); throw new Exception("Invalid consumed receipt accepted."); }
            catch (MySqlException error) when (error.Number == 3819) { Check(true, "MySQL enforces the consumed-service state invariant."); }
        }
        await Sql(connection, "CREATE TRIGGER fixture_block_order_event BEFORE INSERT ON atlas_shop_order_ledger FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='fixture-ledger-failure';");
        ShopCreateOrder failed = Input();
        await Expect(failed, HttpStatusCode.ServiceUnavailable);
        Check(await Wallet() == 9500 && await Amount("SELECT COUNT(*) FROM atlas_shop_order;") == 3,
            "Ledger failure rolls back the unassigned order and its debit atomically.");
        await Sql(connection, "DROP TRIGGER fixture_block_order_event;");
        ShopOrder recovered = await api.CreateOrderAsync(failed, default); await api.CancelOrderAsync(recovered.Id, default);
        Check(await Wallet() == 9500, "A failed purchase is safe to retry with its original key.");

        // The real launcher state calls the authenticated API with no beneficiary, even while playing.
        using ShopUiState state = new(); state.Configure(api.ReadAsync);
        int sends = 0;
        state.ConfigurePurchases(new(async (input, token) =>
        {
            ShopOrder saved = await api.CreateOrderAsync(input, token);
            if (++sends == 1) throw new HttpRequestException("Fixture: lost purchase response");
            return saved;
        }, api.CancelOrderAsync));
        await state.RefreshAsync(); state.OpenService(state.Offers.Single(o => o.Offer.Id == "character-rename"));
        Check(state.UsesAccountService && !state.ShowPurchaseCharacter && state.CanPurchase && state.SelectedCharacter is null,
            "The launcher offers account purchase without selecting the online character.");
        await state.PurchaseAsync();
        Check(state.CanPurchase && !state.CanEditPurchase, "Lost responses freeze the purchase key for safe recovery.");
        await state.PurchaseAsync();
        Check(sends == 2 && state.AvailableServiceCount == 1 && state.CanPurchase && state.CanCancelPurchase && await Wallet() == 9000,
            "The recovered receipt debits once and permits another distinct account service.");
        Check(state.NeedsPurchaseRefresh && state.PurchaseReceipt.Contains("compte"), "Available services are refreshed so future in-game use can update the launcher.");
        await state.CancelPurchaseAsync();
        Check(state.AvailableServiceCount == 0 && await Wallet() == 9500, "The launcher can cancel and refund its unused account service.");
        state.ResetSession();
        Check(!state.HasPurchaseOrders && !state.UsesAccountService && !state.CanPurchase, "Account logout clears service stock and authorization.");

        await Sql(connection, "UPDATE atlas_shop_wallet SET euro_cents=800 WHERE account_id=6;");
        HttpStatusCode[] outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using HttpResponseMessage response = await playerHttp.PostAsJsonAsync("/api/v1/shop/orders", Input()); return response.StatusCode;
        }));
        Check(outcomes.Count(s => s == HttpStatusCode.OK) == 1 && outcomes.Count(s => s == HttpStatusCode.Conflict) == 1 && await Wallet() == 300,
            "Different simultaneous account purchases cannot overspend a shared wallet.");
        foreach (ShopOrder available in (await api.ReadAsync(default)).Purchases!.Orders.Where(o => o.Status == "available"))
            await api.CancelOrderAsync(available.Id, default);
        await Sql(connection, "UPDATE atlas_shop_wallet SET euro_cents=1000000000 WHERE account_id=6;");
        ShopOrder reserve = await api.CreateOrderAsync(Input(), default);
        try
        {
            await app.Services.GetRequiredService<LauncherDatabase>().CreateShopTopUpAsync(6, new(Key(), 100),
                app.Services.GetRequiredService<ShopManualFundingOptions>(), default);
            throw new Exception("A top-up consumed refund headroom.");
        }
        catch (ShopFundingException error) when (error.Code == "shop-wallet-limit")
        { Check(true, "Unused account services retain enough wallet headroom for a full refund."); }
        await api.CancelOrderAsync(reserve.Id, default);
        Check(await Wallet() == ShopSnapshot.MaximumBalanceCents, "Unused-service refunds restore the wallet exactly at its ceiling.");
        await Heartbeat(1);
        Check(!(await api.ReadAsync(default)).CheckoutAvailable, "An older realm module closes account-service checkout while retaining receipts.");
        Console.WriteLine($"Account services MySQL/API PASS: {_checks - initial} additional checks. Native in-game redemption is not exercised by this suite.");

        ShopCreateOrder Input(string currency = "eur") => new(Key(), "character-rename", 0, currency, currency == "eur" ? 500 : 700, snapshot.CatalogRevision);
        Task<long> Wallet(string column = "euro_cents") => Amount("SELECT " + column + " FROM atlas_shop_wallet WHERE account_id=6;");
        Task<long> Events(string id) => Amount($"SELECT COUNT(*) FROM atlas_shop_order_ledger WHERE order_id='{id}';");
        async Task<long> Amount(string sql) => Convert.ToInt64(await Scalar(connection, sql), CultureInfo.InvariantCulture);
        Task Heartbeat(uint protocol) => Sql(connection, """
            INSERT INTO atlas_shop_delivery_health VALUES(1,@protocol,@db,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE protocol=@protocol,character_database=@db,last_seen_at=UTC_TIMESTAMP(6);
            """, ("@protocol", protocol), ("@db", characters));
        async Task Expect(ShopCreateOrder input, HttpStatusCode expected)
        {
            using HttpResponseMessage response = await playerHttp.PostAsJsonAsync("/api/v1/shop/orders", input);
            Check(response.StatusCode == expected, $"Account order expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }
}
