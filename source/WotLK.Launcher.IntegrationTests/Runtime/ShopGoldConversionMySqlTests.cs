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

internal static partial class ShopManualFundingMySqlTests
{
    private static async Task RunGoldConversionStageAsync(LauncherServerOptions server, MySqlConnection connection, WebApplication app, HttpClient http)
    {
        int firstCheck = _checks;
        LauncherSchemaMigration migration = new EmbeddedLauncherSchemaMigrationSource().Load().Single(m => m.Version == 14);
        await Sql(connection, migration.Sql[..(migration.Sql.IndexOf(';') + 1)]);
        server.MaximumSchemaVersion = 14;
        await new LauncherSchemaMigrator(server).MigrateAsync();
        Check((await new LauncherSchemaMigrator(server).MigrateAsync()).Where(m => m.Version <= 14)
            .All(m => m.State == LauncherSchemaMigrationState.AlreadyApplied), "Schema 0014 resumes partial DDL and replays without changing existing financial records.");
        ShopGoldConversionOptions options = app.Services.GetRequiredService<ShopGoldConversionOptions>();
        options.StorageAvailable = true; options.Enabled = true;
        options.Validate(14);
        try { options.Validate(13); throw new Exception("Gold conversion accepted an old schema ceiling."); }
        catch (InvalidOperationException) { Check(true, "Gold conversion enablement requires schema 0014."); }
        await Sql(connection, $"""
            UPDATE `{server.CharacterDatabaseName}`.characters SET online=0,money=1234567 WHERE guid=601;
            UPDATE atlas_shop_wallet SET euro_cents=1234,credit_cents=1000,held_cents=0,debt_cents=0 WHERE account_id=6;
            """);
        using HttpClient player = new() { BaseAddress = http.BaseAddress };
        player.DefaultRequestHeaders.Authorization = new("Bearer", "funding-client");
        LauncherShopApiClient api = new(player, new Uri(http.BaseAddress!, "api/v1/"));
        ShopSnapshot snapshot = await api.ReadAsync(default);
        Check(snapshot.Conversions is { Available: false, Requests.Count: 0 }, "Missing worker health keeps conversions closed while allowing history reads.");
        ShopCreateGoldConversion Input() => new(Key(), 601, 100000, 10, snapshot.CatalogRevision);
        async Task Expect(ShopCreateGoldConversion body, HttpStatusCode code)
        {
            using HttpResponseMessage reply = await player.PostAsJsonAsync("/api/v1/shop/conversions", body);
            Check(reply.StatusCode == code, $"Conversion API expected {code}, got {reply.StatusCode}: {await reply.Content.ReadAsStringAsync()}");
        }
        await Expect(Input(), HttpStatusCode.ServiceUnavailable);
        await Sql(connection, "INSERT INTO atlas_shop_conversion_health VALUES(1,1,@db,10000,UTC_TIMESTAMP(6));", ("@db", server.CharacterDatabaseName));
        Check((await api.ReadAsync(default)).Conversions!.Available, "Only a matching realm, rate, database and fresh heartbeat enable enqueueing.");
        await Expect(Input() with { CharacterGuid = 101 }, HttpStatusCode.Conflict);
        await Expect(Input() with { OfferedCopper = 10001 }, HttpStatusCode.BadRequest);
        await Expect(Input() with { ExpectedCreditCents = 999 }, HttpStatusCode.Conflict);
        await Expect(Input() with { CatalogRevision = "old" }, HttpStatusCode.Conflict);
        using (HttpResponseMessage unauthorized = await http.PostAsJsonAsync("/api/v1/shop/conversions", Input()))
            Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Gold enqueueing requires the account session.");
        ShopCreateGoldConversion attempt = Input();
        ShopGoldConversion[] retries = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => api.CreateConversionAsync(attempt, default)));
        ShopGoldConversion row = retries[0];
        Check(retries.All(r => r.Id == row.Id && r.Status == "pending" && r.GoldBeforeCopper is null && r.CreditAfterCents is null),
            "Five concurrent API retries create one durable request and cannot claim a completed debit.");
        snapshot = await api.ReadAsync(default);
        Check(snapshot.CreditBalanceEuroCents == 1000 && snapshot.Characters.Single(c => c.Guid == 601).GoldCopper == 1234567
            && snapshot.Conversions!.Requests.Count == 1, "Enqueueing alone changes neither character gold nor wallet credits.");
        await Expect(Input(), HttpStatusCode.Conflict);
        await Expect(attempt with { ExpectedCreditCents = 11 }, HttpStatusCode.Conflict);
        using (HttpClient other = new() { BaseAddress = http.BaseAddress })
        {
            other.DefaultRequestHeaders.Authorization = new("Bearer", "funding-one");
            using HttpResponseMessage forbidden = await other.GetAsync("/api/v1/shop/conversions/" + row.Id);
            Check(forbidden.StatusCode == HttpStatusCode.NotFound, "Receipt reads enforce account ownership.");
        }
        options.Enabled = false;
        Check((await api.CreateConversionAsync(attempt, default)).Id == row.Id && (await api.ReadConversionAsync(row.Id, default)).Status == "pending",
            "Pausing new conversions preserves idempotent replay and receipt reads.");
        await Expect(Input(), HttpStatusCode.ServiceUnavailable);
        try
        {
            await Sql(connection, "UPDATE atlas_shop_gold_conversion SET status='completed',gold_before=1234567,gold_after=1,credit_before=1000,credit_after=1010 WHERE id=@id;", ("@id", row.Id));
            throw new Exception("An inconsistent conversion receipt was accepted.");
        }
        catch (MySqlException) { Check(true, "The database rejects a completed receipt whose gold arithmetic does not balance."); }
        Check((await api.ReadConversionAsync(row.Id, default)).Status == "pending", "A rejected SQL write preserves the pending record.");
        await Sql(connection, "ALTER TABLE atlas_shop_gold_conversion MODIFY active_account INT UNSIGNED GENERATED ALWAYS AS (IF(status='completed',account_id,NULL)) STORED;");
        try { await new LauncherSchemaMigrator(server).MigrateAsync(); throw new Exception("A drifted pending-account uniqueness expression was accepted."); }
        catch (InvalidOperationException error) when (error.Message.Contains("pending-conversion", StringComparison.Ordinal))
        { Check(true, "Startup detects drift in the generated pending-account constraint."); }
        await Sql(connection, "ALTER TABLE atlas_shop_gold_conversion MODIFY active_account INT UNSIGNED GENERATED ALWAYS AS (IF(status='pending',account_id,NULL)) STORED;");
        await new LauncherSchemaMigrator(server).MigrateAsync();
        Console.WriteLine($"Gold schema/API MySQL PASS: {_checks - firstCheck} checks. Core execution is covered separately by the real realm suite.");
    }
}
