using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

internal static partial class ShopManualFundingMySqlTests
{
    private static int _checks;
    internal static async Task<int> RunAsync(bool rename = false, bool accountServices = false)
    {
        MySqlConnectionStringBuilder settings = new(Environment.GetEnvironmentVariable("ATLAS_SHOP_TEST_DB")
            ?? throw new InvalidOperationException("Disposable ATLAS_SHOP_TEST_DB required."));
        if (settings.Server != "127.0.0.1" || settings.Port != 13307
            || !Regex.IsMatch(settings.Database, "\\Aatlas_shop_test_[a-z0-9_]{1,30}\\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Only a fresh atlas_shop_test_ database on 127.0.0.1:13307 is permitted.");
        string auth = settings.Database, characters = auth + "_chars";
        settings.Pooling = false;
        LauncherServerOptions server = new() { ConnectionString = settings.ConnectionString, CharacterDatabaseName = characters, MaximumSchemaVersion = 10 };
        settings.Database = "";
        await using MySqlConnection admin = new(settings.ConnectionString); await admin.OpenAsync();
        Check((await Scalar(admin, "SELECT VERSION();"))!.ToString()!.StartsWith("8.4.", StringComparison.Ordinal), "MySQL 8.4 fixture.");
        List<string> created = [];
        try
        {
            foreach (string database in new[] { auth, characters })
            { await Sql(admin, $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"); created.Add(database); }
            await using MySqlConnection connection = new(server.ConnectionString); await connection.OpenAsync();
            await Sql(connection, """
                CREATE TABLE account(id INT UNSIGNED PRIMARY KEY,username VARCHAR(32) NOT NULL);
                INSERT INTO account VALUES(1,'PLAYERONE'),(2,'PLAYERTWO'),(3,'NONMEMBER'),(4,'ADMIN'),(6,'CLIENTFLOW');
                """);
            await new LauncherSchemaMigrator(server).MigrateAsync();
            Check(Convert.ToInt32(await Scalar(connection, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='atlas_shop_wallet';")) == 0,
                "Schema ceiling 0010 leaves the funding tables absent.");
            LauncherSchemaMigration fundingMigration = new EmbeddedLauncherSchemaMigrationSource().Load().Single(m => m.Version == 11);
            // Emulate interruption after the first DDL statement, before recording a migration.
            await Sql(connection, fundingMigration.Sql[..(fundingMigration.Sql.IndexOf(';') + 1)]);
            server.MaximumSchemaVersion = 11;
            await new LauncherSchemaMigrator(server).MigrateAsync();
            Check((await new LauncherSchemaMigrator(server).MigrateAsync()).Where(m => m.Version <= 11).All(m => m.State == LauncherSchemaMigrationState.AlreadyApplied),
                "Migration 0011 resumes partial DDL and is repeatable without data changes.");
            await Sql(connection, $"""
                INSERT INTO atlas_launcher_profile(account_id,display_username,email_normalized) VALUES
                    (1,'Player One','one@example.test'),(2,'Player Two','two@example.test'),(4,'Admin','admin@example.test'),(6,'Client Flow','client@example.test');
                CREATE TABLE `{characters}`.characters(guid INT UNSIGNED PRIMARY KEY,account INT UNSIGNED NOT NULL,
                    name VARCHAR(12) NOT NULL,level TINYINT UNSIGNED NOT NULL,online TINYINT UNSIGNED NOT NULL,money INT UNSIGNED NOT NULL,INDEX(account));
                INSERT INTO `{characters}`.characters VALUES(101,1,'Asteria',80,0,4235067),(201,2,'Boreal',70,1,99000000);
                """);
            foreach ((string name, uint account, bool revoked, bool expired) in new[]
                { ("one",1u,false,false), ("two",2u,false,false), ("admin",4u,false,false), ("client",6u,false,false), ("revoked",1u,true,false), ("expired",1u,false,true) })
                await Sql(connection, """
                    INSERT INTO atlas_launcher_session(id,account_id,access_hash,refresh_hash,access_expires_at,refresh_expires_at,absolute_expires_at,revoked_at)
                    VALUES(@id,@account,@access,@refresh,IF(@expired,UTC_TIMESTAMP()-INTERVAL 1 HOUR,UTC_TIMESTAMP()+INTERVAL 1 HOUR),
                        UTC_TIMESTAMP()+INTERVAL 1 DAY,UTC_TIMESTAMP()+INTERVAL 1 DAY,IF(@revoked,UTC_TIMESTAMP(),NULL));
                    """, ("@id", Guid.NewGuid().ToByteArray()), ("@account", account), ("@access", TokenService.Hash("funding-"+name)),
                    ("@refresh", TokenService.Hash(Guid.NewGuid().ToString())), ("@expired", expired), ("@revoked", revoked));

            ShopManualFundingOptions funding = new() { Enabled = true, StorageAvailable=true, PayPalMeName = "AtlasFixture", AdministratorAccountIds = [4] };
            funding.Validate(11);
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new LauncherDatabase(server, new TokenService(), new LauncherSchemaMigrator(server)));
            builder.Services.AddSingleton<ArmoryReadLimiter>(); builder.Services.AddSingleton(new ShopCatalog(accountServices: accountServices)); builder.Services.AddSingleton(funding);
            ShopPurchaseOptions purchases = new(); builder.Services.AddSingleton(purchases);
            await using WebApplication app = builder.Build(); app.MapShopEndpoints(); await app.StartAsync();
            try
            {
                using HttpClient http = new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(20) };
                foreach (string? account in new string?[] { null, "invalid", "revoked", "expired" })
                {
                    await Expect("POST", "/api/v1/shop/top-ups", account, new ShopCreateTopUp(Key(),1000), HttpStatusCode.Unauthorized);
                    await Expect("GET", "/api/v1/shop/admin/top-ups", account, null, HttpStatusCode.Unauthorized);
                }
                await Expect("GET", "/api/v1/shop/admin/top-ups", "one", null, HttpStatusCode.Forbidden);
                ShopSnapshot blank = await Snapshot("one");
                Check(blank.EuroBalanceCents == 0 && blank.CreditBalanceEuroCents == 0 && blank.History?.Count == 0
                    && blank.ManualFunding is { Available: true, CanAdminister: false, Requests.Count: 0 }, "A fresh wallet is empty and cannot administer.");
                Check((await Snapshot("admin")).ManualFunding!.CanAdminister, "Only the configured account has administration capability.");
                funding.Enabled = false;
                await Expect("POST", "/api/v1/shop/top-ups", "one", new ShopCreateTopUp(Key(),1000), HttpStatusCode.ServiceUnavailable);
                Check((await Snapshot("one")) is { EuroBalanceCents:0, ManualFunding.Available:false }, "Pausing top-ups preserves access to the persisted wallet.");
                funding.Enabled = true;
                foreach (long amount in new long[] { -1,0,99,5001,long.MaxValue })
                    await Expect("POST", "/api/v1/shop/top-ups", "one", new ShopCreateTopUp(Key(),amount), HttpStatusCode.BadRequest);
                await Expect("POST", "/api/v1/shop/top-ups", "one", new { idempotencyKey=Key(), amountCents=1000, accountId=2 }, HttpStatusCode.BadRequest);
                await Expect("POST", "/api/v1/shop/top-ups?accountId=2", "one", new ShopCreateTopUp(Key(),1000), HttpStatusCode.BadRequest);
                using (HttpResponseMessage oversized = await Send("POST", "/api/v1/shop/top-ups", "one", new StringContent(new string('x',9000),Encoding.UTF8,"application/json")))
                    Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Oversized JSON is rejected before parsing.");

                string key = Key();
                Task<ShopTopUp>[] repeatedCreates = Enumerable.Range(0,6).Select(_ => Create("one",2000,key)).ToArray();
                ShopTopUp[] repeated = await Task.WhenAll(repeatedCreates);
                ShopTopUp request = repeated[0];
                Check(repeated.All(row => row.Id == request.Id) && request.Status == "pending" && request.PaymentUrl == "https://paypal.me/AtlasFixture/20.00EUR"
                    && Math.Abs((request.CreatedAtUtc - DateTimeOffset.UtcNow).TotalSeconds) < 30,
                    "Concurrent retries create one pending request and an exact, restricted EUR payment URL.");
                Check((await Snapshot("one")).EuroBalanceCents == 0 && await Amount("SELECT COUNT(*) FROM atlas_shop_ledger;") == 1,
                    "Creating a request never credits the wallet; its audit event exists exactly once.");
                await Expect("POST", "/api/v1/shop/top-ups", "one", new ShopCreateTopUp(key,3000), HttpStatusCode.Conflict);
                await Expect("POST", "/api/v1/shop/top-ups", "one", new ShopCreateTopUp(Key(),1000), HttpStatusCode.Conflict);
                await Expect("POST", $"/api/v1/shop/top-ups/{request.Id}/cancel", "two", null, HttpStatusCode.NotFound);
                await Expect("GET", $"/api/v1/shop/admin/top-ups/{request.Id}", "one", null, HttpStatusCode.Forbidden);
                await Expect("POST", $"/api/v1/shop/admin/top-ups/{request.Id}/decision", "one", Approve(1,"PAYPAL00000000001",2000), HttpStatusCode.Forbidden);
                await Expect("POST", $"/api/v1/shop/admin/top-ups/{request.Id}/decision", "admin", Approve(1,"PAYPAL00000000001",2000) with { PaymentVerified=false }, HttpStatusCode.BadRequest);
                await Expect("POST", $"/api/v1/shop/admin/top-ups/{request.Id}/decision", "admin", Approve(1,"PAYPAL00000000001",1900), HttpStatusCode.Conflict);
                ShopTopUpDecision approve = Approve(1,"PAYPAL00000000001",2000);
                ShopAdminTopUp[] approved = await Task.WhenAll(Enumerable.Range(0,6).Select(_ => Decide(request.Id,approve)));
                foreach (ShopAdminTopUp row in approved) row.Request.Validate();
                Check(approved.All(row => row.Request.Status == "credited" && row.Version == 2), "Repeated concurrent approval returns the same committed result.");
                ShopSnapshot credited = await Snapshot("one");
                Check(credited.EuroBalanceCents == 2000 && credited.CreditBalanceEuroCents == 0 && credited.History!.Single().AmountCents == 2000,
                    "One verified payment grants exactly twenty euros, with a durable history entry and no Atlas-credit change.");
                Check(await Amount("SELECT COUNT(*) FROM atlas_shop_ledger;") == 2, "Only one approval event was committed.");
                funding.Enabled=false;
                Check((await Snapshot("one")) is { EuroBalanceCents:2000, ManualFunding.Available:false },"Pausing new requests does not hide existing funds.");
                Check((await Snapshot("admin")).ManualFunding!.CanAdminister,"Administrators can still reconcile payments while new requests are paused.");
                await Get<ShopAdminTopUp>($"/api/v1/shop/admin/top-ups/{request.Id}","admin");
                funding.Enabled=true;
                await Expect("POST", $"/api/v1/shop/top-ups/{request.Id}/cancel", "one", null, HttpStatusCode.Conflict);

                ShopTopUp other = await Create("two",1000,Key());
                await Expect("POST", $"/api/v1/shop/admin/top-ups/{other.Id}/decision", "admin", Approve(1,"paypal00000000001",1000), HttpStatusCode.Conflict);
                Check((await Snapshot("two")).EuroBalanceCents == 0 && (await Snapshot("two")).ManualFunding!.Requests.Single().Status == "pending",
                    "Reusing a PayPal transaction on another account rolls the entire credit back.");

                ShopAdminTopUp disputed = await Decide(request.Id,new("dispute",2,"Contestation à examiner",PayPalCaseId:"PP-CASE-01"));
                ShopSnapshot frozen = await Snapshot("one");
                Check(disputed.Request.Status == "disputed" && frozen.EuroBalanceCents == 0 && frozen.ManualFunding!.HeldCents == 2000,
                    "A dispute holds available funds without refunding money or modifying characters.");
                ShopAdminTopUp won = await Decide(request.Id,new("resolve-won",3,"Décision PayPal favorable"));
                Check(won.Version == 4 && (await Snapshot("one")).EuroBalanceCents == 2000 && (await Snapshot("one")).ManualFunding!.HeldCents == 0,
                    "A successful dispute releases the hold exactly once.");
                await Expect("POST", $"/api/v1/shop/admin/top-ups/{request.Id}/decision", "admin",new ShopTopUpDecision("refund-confirmed",4,"Pas encore remboursé"),HttpStatusCode.BadRequest);
                ShopTopUpDecision refund = new("refund-confirmed",4,"Remboursement constaté dans PayPal",RefundVerified:true);
                await Task.WhenAll(Decide(request.Id,refund),Decide(request.Id,refund));
                ShopSnapshot refunded = await Snapshot("one");
                Check(refunded.EuroBalanceCents == 0 && refunded.ManualFunding!.Requests.Single().Status == "refunded"
                    && refunded.History!.Count == 2 && refunded.History[0].Kind == "payment-reversal" && refunded.History[0].AmountCents == -2000,
                    "A confirmed refund reverses the wallet only once and retains the original credit history.");
                await Expect("POST", $"/api/v1/shop/admin/top-ups/{request.Id}/decision", "admin",Approve(5,"PAYPAL00000000001",2000),HttpStatusCode.Conflict);

                // Insufficient remaining funds and concurrent holds: never seize funds held for a different payment.
                await Decide(other.Id,Approve(1,"PAYPAL00000000002",1000));
                ShopTopUp second = await Create("two",2000,Key());
                await Decide(second.Id,Approve(1,"PAYPAL00000000003",2000));
                // A local fixture represents future consumption; no production purchase route is being enabled.
                await Sql(connection,"UPDATE atlas_shop_wallet SET euro_cents=2500 WHERE account_id=2;");
                await Decide(other.Id,new("dispute",2,"Premier dossier",PayPalCaseId:"PP-CASE-02"));
                await Decide(second.Id,new("dispute",2,"Second dossier",PayPalCaseId:"PP-CASE-03"));
                await Decide(second.Id,new("refund-confirmed",3,"Remboursement externe confirmé",RefundVerified:true));
                ShopSnapshot debt = await Snapshot("two");
                Check(debt.EuroBalanceCents == 0 && debt.ManualFunding is { HeldCents:1000, DebtCents:500 },
                    "An insufficient refund records only the shortfall and preserves another transaction's hold.");
                await Decide(other.Id,new("resolve-won",3,"Litige clos en faveur du marchand"));
                ShopTopUp debtPayment = await Create("two",1000,Key());
                await Decide(debtPayment.Id,Approve(1,"PAYPAL00000000004",1000));
                ShopSnapshot settled = await Snapshot("two");
                Check(settled.EuroBalanceCents == 1500 && settled.ManualFunding is { HeldCents:0, DebtCents:0 },
                    "A subsequent credit settles recorded debt before adding spendable funds.");

                ShopTopUp cancelled = await Create("one",1000,Key());
                await Expect("POST", $"/api/v1/shop/top-ups/{cancelled.Id}/cancel", "one", null, HttpStatusCode.OK);
                await Expect("POST", $"/api/v1/shop/top-ups/{cancelled.Id}/cancel", "one", null, HttpStatusCode.OK);
                await Decide(cancelled.Id,Approve(2,"PAYPAL00000000005",1000));
                Check((await Snapshot("one")).EuroBalanceCents == 1000, "An administrator can reconcile an actually received payment on an accidentally cancelled request.");
                funding.DailyMaximumCents = 5000;
                ShopTopUp quota = await Create("one",2000,Key());
                await Expect("POST", $"/api/v1/shop/top-ups/{quota.Id}/cancel", "one", null, HttpStatusCode.OK);
                await Expect("POST", "/api/v1/shop/top-ups", "one", new ShopCreateTopUp(Key(),100), HttpStatusCode.TooManyRequests);
                funding.DailyMaximumCents = 10000;
                ShopAdminTopUpPage page = await Get<ShopAdminTopUpPage>("/api/v1/shop/admin/top-ups?status=refunded","admin");
                Check(page.Requests.Count == 2 && page.Requests.All(row => row.Request.Status == "refunded" && row.Audit.Count == 0),
                    "The administration list is filtered and avoids embedding full audits on each row.");
                ShopAdminTopUp detail = await Get<ShopAdminTopUp>($"/api/v1/shop/admin/top-ups/{request.Id}","admin");
                Check(detail.Audit.Count == 5 && detail.Audit[0].Action == "refund-confirmed" && detail.Audit.Last().ActorAccountId == 1
                    && detail.PayPalTransactionId == "PAYPAL00000000001", "An administrator sees the complete payment trail, actor and provider reference.");
                await Expect("GET", "/api/v1/shop/admin/top-ups?accountId=2", "admin", null, HttpStatusCode.BadRequest);
                await Expect("GET", "/api/v1/shop/admin/top-ups?status=pending&status=credited", "admin", null, HttpStatusCode.BadRequest);
                Check(await Amount($"SELECT SUM(money) FROM `{characters}`.characters;") == 103235067, "No game-character money was changed.");
                Check(!settled.CheckoutAvailable, "Funding never enables character purchases or conversions.");
                await new LauncherSchemaMigrator(server).MigrateAsync();
                Check((await Snapshot("one")).EuroBalanceCents == 1000, "A schema recheck preserves committed financial data.");

                // Exercise the launcher's actual client and presentation against HTTP + MySQL.
                using HttpClient playerHttp=new(),adminHttp=new();
                playerHttp.DefaultRequestHeaders.Authorization=new("Bearer","funding-client");
                adminHttp.DefaultRequestHeaders.Authorization=new("Bearer","funding-admin");
                LauncherShopApiClient playerApi=new(playerHttp,new Uri(http.BaseAddress,"api/v1/"));
                LauncherShopApiClient adminApi=new(adminHttp,new Uri(http.BaseAddress,"api/v1/"));
                using ShopUiState playerState=new(),adminState=new();
                playerState.Configure(playerApi.ReadAsync); playerState.ConfigureFunding(Actions(playerApi));
                adminState.Configure(adminApi.ReadAsync); adminState.ConfigureFunding(Actions(adminApi));
                await playerState.RefreshAsync(); playerState.OpenWallet(); playerState.WalletAmount="10";
                Check(playerState.CanBeginWalletPayment && !playerState.CanAdministerFunding,"The actual launcher client receives player capabilities.");
                await playerState.CreateTopUpAsync();
                Check(playerState.HasPendingTopUp && (await Snapshot("client")).EuroBalanceCents==0,"The client creates a persistent pending request without local credit.");
                string clientId=playerState.TopUpRequests.Single().Id;
                await adminState.RefreshAsync(); await adminState.OpenAdminFundingAsync();
                adminState.AdminSearchReference=clientId; await adminState.SearchAdminTopUpAsync();
                adminState.AdminTransactionId="PAYPALCLIENT00001"; adminState.AdminReceivedAmount="10";
                adminState.AdminNote="Validation via le client du launcher sur HTTP réel"; adminState.AdminPaymentVerified=true;
                Check(adminState.CanSubmitAdminDecision,"The actual admin client requires and accepts a complete review.");
                await adminState.SubmitAdminDecisionAsync(); await playerState.RefreshAsync();
                Check((await Snapshot("client")).EuroBalanceCents==1000 && playerState.TopUpRequests.Single().Request.Status=="credited",
                    "Client, HTTP authorization, database transaction and refreshed player state complete one end-to-end funding flow.");

                ShopTopUp rollback=await Create("client",100,Key());
                await Sql(connection,"CREATE TRIGGER fixture_block_ledger BEFORE INSERT ON atlas_shop_ledger FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='fixture-ledger-failure';");
                await Expect("POST",$"/api/v1/shop/admin/top-ups/{rollback.Id}/decision","admin",Approve(1,"PAYPALROLLBACK001",100),HttpStatusCode.ServiceUnavailable);
                Check((await Snapshot("client")).EuroBalanceCents==1000 && (await Get<ShopAdminTopUp>($"/api/v1/shop/admin/top-ups/{rollback.Id}","admin")) is { Version:1,PayPalTransactionId:null },
                    "An audit-write failure rolls back wallet, request status, version and payment-ID reservation together.");
                await Sql(connection,"DROP TRIGGER fixture_block_ledger;");
                await Decide(rollback.Id,Approve(1,"PAYPALROLLBACK001",100));
                Check((await Snapshot("client")).EuroBalanceCents==1100,"A retry after a rolled-back failure safely applies the payment once.");

                await Sql(connection,"INSERT INTO atlas_shop_wallet(account_id,updated_at) VALUES(4,UTC_TIMESTAMP(6));");
                for(int i=0;i<51;i++)await Sql(connection,"""
                    INSERT INTO atlas_shop_top_up(id,account_id,idempotency_key,amount_cents,status,version,created_at,updated_at)
                    VALUES(@id,4,@key,100,'cancelled',2,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                    """,("@id",Key()),("@key",Key()));
                ShopAdminTopUpPage firstPage=await Get<ShopAdminTopUpPage>("/api/v1/shop/admin/top-ups?status=cancelled","admin");
                firstPage.Validate();
                Check(firstPage.Requests.Count==50 && firstPage.NextBefore is >0,"Administrative pagination is bounded at fifty records.");
                ShopAdminTopUpPage secondPage=await Get<ShopAdminTopUpPage>("/api/v1/shop/admin/top-ups?status=cancelled&before="+firstPage.NextBefore,"admin");
                Check(secondPage.NextBefore is null && !firstPage.Requests.Select(row=>row.Request.Id).Intersect(secondPage.Requests.Select(row=>row.Request.Id)).Any()
                    && firstPage.Requests.Count+secondPage.Requests.Count==await Amount("SELECT COUNT(*) FROM atlas_shop_top_up WHERE status='cancelled';"),
                    "Older pages cover all filtered requests without duplication or omission.");
                await Sql(connection,"UPDATE atlas_launcher_session SET revoked_at=UTC_TIMESTAMP() WHERE account_id=4;");
                await Expect("POST", $"/api/v1/shop/admin/top-ups/{quota.Id}/decision", "admin",Approve(2,"PAYPAL00000000006",2000),HttpStatusCode.Unauthorized);
                Console.WriteLine($"Manual shop funding MySQL/API PASS: {_checks} checks; schema 0011 recovery, authorization, limits, concurrent creation/approval, rollback, holds, refunds, debt, audit and persistence. No PayPal network or game mutations.");
                if (rename) await RunRenameStageAsync(server, connection, app, http, purchases);
                if (accountServices) await RunAccountServicesStageAsync(server, connection, app, http, purchases);
                return 0;

                async Task<long> Amount(string sql) => Convert.ToInt64(await Scalar(connection,sql),CultureInfo.InvariantCulture);
                async Task<ShopSnapshot> Snapshot(string account)
                { ShopSnapshot snapshot=await Get<ShopSnapshot>("/api/v1/shop",account); snapshot.Validate(); return snapshot; }
                Task<ShopTopUp> Create(string account,long cents,string idempotencyKey) => Post<ShopTopUp>("/api/v1/shop/top-ups",account,new ShopCreateTopUp(idempotencyKey,cents));
                Task<ShopAdminTopUp> Decide(string id,ShopTopUpDecision decision) => Post<ShopAdminTopUp>($"/api/v1/shop/admin/top-ups/{id}/decision","admin",decision);
                async Task<T> Get<T>(string path,string account)
                { using HttpResponseMessage response=await Send("GET",path,account,null); await Success(response); return (await response.Content.ReadFromJsonAsync<T>())!; }
                async Task<T> Post<T>(string path,string account,object body)
                { using HttpResponseMessage response=await Send("POST",path,account,JsonContent.Create(body)); await Success(response); return (await response.Content.ReadFromJsonAsync<T>())!; }
                async Task Expect(string method,string path,string? account,object? body,HttpStatusCode expected)
                { using HttpResponseMessage response=await Send(method,path,account,body is null?null:JsonContent.Create(body));
                    Check(response.StatusCode==expected,$"{method} {path}: expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); }
                async Task Success(HttpResponseMessage response)
                { Check(response.IsSuccessStatusCode,$"Expected success, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                    Check(response.Headers.CacheControl?.NoStore==true,"Financial responses are never cached."); }
                async Task<HttpResponseMessage> Send(string method,string path,string? account,HttpContent? body)
                { using HttpRequestMessage message=new(new HttpMethod(method),path) { Content=body };
                    if(account is not null)message.Headers.Authorization=new AuthenticationHeaderValue("Bearer","funding-"+account);
                    return await http.SendAsync(message); }
            }
            finally { await app.StopAsync(); }
        }
        finally { foreach(string database in created.AsEnumerable().Reverse())await Sql(admin,$"DROP DATABASE `{database}`;"); }
    }
    private static ShopTopUpDecision Approve(long version,string transaction,long cents)
        => new("approve",version,"Paiement biens et services vérifié dans PayPal",transaction,cents,PaymentVerified:true);
    private static ShopFundingActions Actions(LauncherShopApiClient client)=>new(client.CreateTopUpAsync,client.CancelTopUpAsync,client.ListTopUpsAsync,client.ReadTopUpAsync,client.DecideTopUpAsync);
    private static string Key()=>Guid.NewGuid().ToString("N");
    private static void Check(bool success,string message) { if(!success)throw new InvalidOperationException(message); _checks++; }
    private static async Task Sql(MySqlConnection connection,string sql,params (string Name,object Value)[] parameters)
    { await using MySqlCommand command=new(sql,connection); foreach(var pair in parameters)command.Parameters.AddWithValue(pair.Name,pair.Value); await command.ExecuteNonQueryAsync(); }
    private static async Task<object?> Scalar(MySqlConnection connection,string sql)
    { await using MySqlCommand command=new(sql,connection); return await command.ExecuteScalarAsync(); }
}
