using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;
using WotLK.Launcher.Shop.Contracts;

internal static class ShopApiMySqlTests
{
    internal static async Task<int> RunAsync()
    {
        MySqlConnectionStringBuilder connectionString = new(Environment.GetEnvironmentVariable("ATLAS_SHOP_TEST_DB")
            ?? throw new InvalidOperationException("A disposable local ATLAS_SHOP_TEST_DB is required."));
        if (connectionString.Server != "127.0.0.1" || connectionString.Port != 13307
            || !Regex.IsMatch(connectionString.Database, "^atlas_shop_test_[a-z0-9_]{1,30}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Only a fresh atlas_shop_test_ database on 127.0.0.1:13307 is permitted.");
        string auth = connectionString.Database, characters = auth + "_chars";
        connectionString.Pooling = false;
        LauncherServerOptions options = new() { ConnectionString = connectionString.ConnectionString, CharacterDatabaseName = characters, MaximumSchemaVersion = 8 };
        connectionString.Database = "";
        await using MySqlConnection admin = new(connectionString.ConnectionString); await admin.OpenAsync();
        Check(Convert.ToString(await Scalar(admin, "SELECT VERSION();"), CultureInfo.InvariantCulture)!.StartsWith("8.4.", StringComparison.Ordinal), "MySQL 8.4 fixture required.");
        List<string> created = [];
        try
        {
            foreach (string name in new[] { auth, characters })
            {
                await Sql(admin, $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"); created.Add(name);
            }
            await using MySqlConnection connection = new(options.ConnectionString); await connection.OpenAsync();
            await Sql(connection, $"""
                CREATE TABLE account(id INT UNSIGNED PRIMARY KEY,username VARCHAR(32) NOT NULL);
                INSERT INTO account VALUES(1,'ACCOUNTONE'),(2,'ACCOUNTTWO'),(3,'NOATLASPROFILE');
                CREATE TABLE atlas_launcher_profile(account_id INT UNSIGNED PRIMARY KEY);
                INSERT INTO atlas_launcher_profile VALUES(1),(2);
                CREATE TABLE atlas_launcher_session(access_hash BINARY(32) PRIMARY KEY,account_id INT UNSIGNED NOT NULL,
                    access_expires_at DATETIME NOT NULL,revoked_at DATETIME NULL,updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE `{characters}`.characters(guid INT UNSIGNED PRIMARY KEY,account INT UNSIGNED NOT NULL,
                    name VARCHAR(12) NOT NULL,level TINYINT UNSIGNED NOT NULL,online TINYINT UNSIGNED NOT NULL,money INT UNSIGNED NOT NULL,INDEX(account));
                INSERT INTO `{characters}`.characters VALUES(101,1,'Asteria',80,0,4235067),(102,1,'Boreal',70,1,99000000),(201,2,'Other',40,0,100000);
                """);
            foreach ((string token, int account, bool revoked, bool expired) in new[]
                { ("one",1,false,false), ("two",2,false,false), ("revoked",1,true,false), ("expired",1,false,true), ("nonmember",3,false,false) })
                await Sql(connection, """
                    INSERT INTO atlas_launcher_session(access_hash,account_id,access_expires_at,revoked_at)
                    VALUES(@hash,@account,IF(@expired,UTC_TIMESTAMP()-INTERVAL 1 HOUR,UTC_TIMESTAMP()+INTERVAL 1 HOUR),IF(@revoked,UTC_TIMESTAMP(),NULL));
                    """, ("@hash",TokenService.Hash("shop-fixture-"+token)),("@account",account),("@expired",expired),("@revoked",revoked));

            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new LauncherDatabase(options,new TokenService(),new LauncherSchemaMigrator(options)));
            builder.Services.AddSingleton<ArmoryReadLimiter>(); builder.Services.AddSingleton(new ShopCatalog());
            await using WebApplication app = builder.Build(); app.MapShopEndpoints(); await app.StartAsync();
            try
            {
                using HttpClient http = new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
                foreach (string? token in new string?[] { null,"invalid","revoked","expired","nonmember" })
                    using (HttpResponseMessage response = await Read("/api/v1/shop",token))
                        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Missing, invalid, revoked, expired and nonmember sessions are denied.");
                foreach (string query in new[] { "accountId=2", "characterGuid=201", "price=0", "sql=SELECT+1" })
                    using (HttpResponseMessage response = await Read("/api/v1/shop?"+query,"one"))
                        Check(response.StatusCode == HttpStatusCode.BadRequest, "No client override is accepted.");
                using (HttpResponseMessage response = await Read("/api/v1/shop","one"))
                {
                    Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true, "Authenticated JSON is not cached.");
                    ShopSnapshot snapshot = (await response.Content.ReadFromJsonAsync<ShopSnapshot>())!; snapshot.Validate();
                    Check(snapshot.Characters.Select(c=>c.Guid).SequenceEqual(new uint[]{101,102}), "Only owned characters are returned.");
                    Check(snapshot.Characters[0].GoldCopper==4235067 && snapshot.Characters[1].GoldCopper is null, "Offline copper is exact; live gold is not fabricated.");
                    Check(snapshot.CreditBalanceEuroCents is null && snapshot.EuroBalanceCents is null && !snapshot.CheckoutAvailable, "Neither wallet nor purchases are fabricated.");
                    Check(snapshot.GoldConversion.Quote(2120000).CreditEuroCents==265, "Server supplies the agreed arbitrary-amount conversion rate.");
                    Check(snapshot.Offers.Single().Prices.Single(p=>p.Currency=="eur").Amount==500
                        && snapshot.Offers.Single().Prices.Single(p=>p.Currency=="credits").Amount==500, "The server owns both approved wallet prices.");
                }
                using (HttpResponseMessage response = await Read("/api/v1/shop","two"))
                    Check((await response.Content.ReadFromJsonAsync<ShopSnapshot>())!.Characters.Single().Guid==201, "A second account receives a distinct roster.");
                using (HttpRequestMessage request = new(HttpMethod.Post,"/api/v1/shop"))
                {
                    request.Headers.Authorization=new AuthenticationHeaderValue("Bearer","shop-fixture-one");
                    using HttpResponseMessage response=await http.SendAsync(request);
                    Check(response.StatusCode==HttpStatusCode.MethodNotAllowed, "Browsing route cannot buy or convert gold.");
                }
                Check(Convert.ToUInt64(await Scalar(connection,$"SELECT SUM(money) FROM `{characters}`.characters;"))==103335067UL,"Every character balance is unchanged.");
                await Sql(connection,$"DELETE FROM `{characters}`.characters WHERE account=2;");
                using (HttpResponseMessage response=await Read("/api/v1/shop","two"))
                    Check((await response.Content.ReadFromJsonAsync<ShopSnapshot>())!.Characters.Count==0,"No-character account keeps the catalog.");
                ArmoryReadLimiter limiter=app.Services.GetRequiredService<ArmoryReadLimiter>();
                for(int i=0;i<120;i++)using(limiter.Acquire(1)){}
                using(HttpResponseMessage response=await Read("/api/v1/shop","one"))
                    Check(response.StatusCode==HttpStatusCode.TooManyRequests&&response.Headers.RetryAfter?.Delta==TimeSpan.FromSeconds(60),"Per-account rate limit includes retry guidance.");
                using(HttpResponseMessage response=await Read("/api/v1/shop","two"))Check(response.IsSuccessStatusCode,"Another account keeps its own request budget.");
                await Sql(connection,$"DROP TABLE `{characters}`.characters;");
                using(HttpResponseMessage response=await Read("/api/v1/shop","two"))
                    Check(response.StatusCode==HttpStatusCode.ServiceUnavailable&&await response.Content.ReadAsStringAsync()=="{\"error\":\"shop-unavailable\"}","Database errors disclose no internal details.");
                Console.WriteLine("Shop MySQL/API PASS: authenticated membership, ownership, no overrides, exact saved gold, null live balances, catalog prices, conversion rate, no mutation, empty roster, rate limiting and sanitized failures. Disposable loopback databases only.");
                return 0;

                async Task<HttpResponseMessage> Read(string path,string? token)
                {
                    using HttpRequestMessage request=new(HttpMethod.Get,path);
                    if(token is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer","shop-fixture-"+token);
                    return await http.SendAsync(request);
                }
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            foreach(string name in created.AsEnumerable().Reverse())await Sql(admin,$"DROP DATABASE `{name}`;");
        }
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static async Task Sql(MySqlConnection connection,string sql,params (string Key,object Value)[] values)
    {
        await using MySqlCommand command=new(sql,connection);
        foreach(var pair in values)command.Parameters.AddWithValue(pair.Key,pair.Value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> Scalar(MySqlConnection connection,string sql)
    { await using MySqlCommand command=new(sql,connection); return await command.ExecuteScalarAsync(); }
}
