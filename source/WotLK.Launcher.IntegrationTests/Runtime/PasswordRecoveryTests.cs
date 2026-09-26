using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static partial class AuthSessionSecurityMySqlTests
{
    internal static async Task<int> RunPasswordRecoveryAsync()
    {
        _checks = 0;
        MySqlConnectionStringBuilder connection = ReadSafeConnection();
        await ResetSchemaAsync(connection.ConnectionString);
        try
        {
            await CreateAzerothCoreFixtureAsync(connection.ConnectionString);
            LauncherServerOptions options = Options(connection, 15);
            LauncherServerOptions capped = Options(connection, 14);
            var blocked = await new LauncherSchemaMigrator(capped).MigrateAsync(None);
            Check(blocked.Last().Version == 15 && blocked.Last().State == LauncherSchemaMigrationState.BlockedByCeiling,
                "Recovery migration stays blocked at ceiling 14.");
            Check(!new LauncherDatabase(capped, new TokenService(), new LauncherSchemaMigrator(capped)).PasswordRecoveryAvailable, "Recovery unavailable on older schema.");
            await new LauncherSchemaMigrator(options).MigrateAsync(None);
            await new LauncherSchemaMigrator(options).MigrateAsync(None);
            Check(true, "Migration 15 applies and validates idempotently.");
            await SeedAccountAsync(connection.ConnectionString);
            LauncherDatabase database = new(options, new TokenService(), new LauncherSchemaMigrator(options));
            const string address = "auth-fixture@example.test";
            Check(await database.CreatePasswordResetAsync("missing@example.test", None) is null, "Unknown email creates no challenge.");
            AuthResponse session = await LoginAsync(database, InitialPassword, "before-reset");
            EmailVerificationChallenge challenge = (await database.CreatePasswordResetAsync(address, None))!;
            Check(challenge is not null && TokenService.IsPasswordResetToken(challenge.Token), "Recovery issues a purpose-specific token.");
            Check(!TokenService.IsEmailVerificationToken(challenge!.Token) && !TokenService.IsRefreshToken(challenge.Token), "Tokens cannot cross auth purposes.");
            Check(await database.CreatePasswordResetAsync(address, None) is null, "Account cooldown prevents duplicate emails.");
            Check(await database.ResetPasswordAsync(TokenService.CreateEmailVerificationToken(), ChangedPassword, None) is null,
                "Email verification token cannot reset a password.");
            Check(await database.ResetPasswordAsync(challenge.Token, "short", None) is null, "Short password is rejected without consuming the token.");
            string?[] raced = await Task.WhenAll(
                database.ResetPasswordAsync(challenge.Token, ChangedPassword, None),
                database.ResetPasswordAsync(challenge.Token, ChangedPassword, None));
            Check(raced.Count(result => result == Username) == 1 && raced.Count(result => result is null) == 1,
                "Concurrent redemption changes the password exactly once.");
            Check(await database.AuthenticateAsync(session.AccessToken, None) is null, "Password reset revokes old access sessions.");
            Check((await database.RefreshSessionAsync(session.RefreshToken, None)).Response is null, "Old refresh token cannot restore a reset session.");
            Check((await database.LoginAsync(new LoginRequest(Username, InitialPassword, "old"), None)).Outcome == AtlasLoginOutcome.InvalidCredentials,
                "Old password no longer authenticates.");
            AuthResponse updated = await LoginAsync(database, ChangedPassword, "after-reset");
            Check(await database.ResetPasswordAsync(challenge.Token, FinalPassword, None) is null, "Consumed token is rejected.");

            challenge = await NextChallengeAsync();
            await ExecuteAsync(connection.ConnectionString, "UPDATE atlas_launcher_password_reset SET expires_at=UTC_TIMESTAMP()-INTERVAL 1 SECOND;");
            Check(await database.ResetPasswordAsync(challenge.Token, FinalPassword, None) is null, "Expired token cannot change credentials.");
            challenge = await NextChallengeAsync();
            await database.ChangeEmailAsync(AccountId, "changed@example.test", None);
            Check(await database.ResetPasswordAsync(challenge.Token, FinalPassword, None) is null, "Changing the profile email invalidates existing recovery links.");
            await database.ChangeEmailAsync(AccountId, address, None);
            Check(await database.ResetPasswordAsync(challenge.Token, FinalPassword, None) is null, "Restoring a previous email never revives its recovery link.");
            challenge = await NextChallengeAsync();
            Check(await database.ChangePasswordAsync(AccountId, updated.AccessToken, ChangedPassword, FinalPassword, None) is not null, "Authenticated password change succeeds.");
            Check(await database.ResetPasswordAsync(challenge.Token, InitialPassword, None) is null, "Credential changes invalidate older recovery links.");
            challenge = await NextChallengeAsync();
            await database.CancelPasswordResetAsync(challenge.TokenHash, None);
            Check(await database.ResetPasswordAsync(challenge.Token, InitialPassword, None) is null, "Failed email dispatch token is unusable.");
            await CooldownAsync();
            await VerifyRecoveryHttpAsync(database, options, address);
            Console.WriteLine($"Password recovery PASS: {_checks} checks, disposable local MySQL, mocked email/Hermes, no external requests.");
            return 0;

            async Task CooldownAsync() => await ExecuteAsync(connection.ConnectionString,
                "UPDATE atlas_launcher_password_reset SET created_at=UTC_TIMESTAMP()-INTERVAL 6 MINUTE;");
            async Task<EmailVerificationChallenge> NextChallengeAsync()
            {
                await CooldownAsync();
                return (await database.CreatePasswordResetAsync(address, None))!;
            }
        }
        finally { await ResetSchemaAsync(connection.ConnectionString); }
    }

    private static async Task VerifyRecoveryHttpAsync(LauncherDatabase database, LauncherServerOptions options, string address)
    {
        options.BrevoApiKey = "fixture-key";
        options.BrevoSenderEmail = "atlas@example.test";
        options.PublicBaseUrl = "https://atlas.example.test/wotlk";
        using RecoveryMailHandler mail = new();
        using HttpClient mailHttp = new(mail) { BaseAddress = new Uri("https://email.example.test/") };
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(new BrevoEmailClient(mailHttp, options));
        builder.Services.AddSingleton(new HermesTicketClient(mailHttp, options));
        builder.Services.AddSingleton<PasswordRecoveryService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<PasswordRecoveryService>());
        builder.Services.AddAtlasAuthenticationRateLimiting();
        await using WebApplication app = builder.Build();
        app.UseAtlasAuthenticationRequestBodyLimits();
        app.UseRateLimiter();
        app.MapPasswordRecovery();
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single() + "/api/v1/") };
            Check((await PasswordRecoveryClient.RequestAsync(client, address, None)).Accepted, "Launcher submits the real recovery request route.");
            Check((await PasswordRecoveryClient.RequestAsync(client, "missing@example.test", None)).Accepted, "Unknown account gets identical accepted result.");
            string secret = await mail.Secret.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(TokenService.IsPasswordResetToken(secret), "Mock email contains the recovery token in a fragment.");
            using HttpResponseMessage page = await client.GetAsync("auth/password-reset");
            string html = await page.Content.ReadAsStringAsync();
            Check(page.IsSuccessStatusCode && page.Headers.CacheControl?.NoStore == true, "Reset page cannot be cached.");
            Check(page.Headers.Contains("Content-Security-Policy") && page.Headers.GetValues("Referrer-Policy").Single() == "no-referrer", "Reset page restricts scripts, frames and referrers.");
            Check(html.Contains("history.replaceState") && !html.Contains(secret), "Page removes the fragment and never renders a server token.");
            using HttpResponseMessage mismatch = await client.PostAsJsonAsync("auth/password-reset/confirm", new { token = secret, password = InitialPassword, confirmation = "mismatch" });
            Check(mismatch.StatusCode == HttpStatusCode.BadRequest, "Endpoint validates password confirmation.");
            using HttpResponseMessage reset = await client.PostAsJsonAsync("auth/password-reset/confirm", new { token = secret, password = InitialPassword, confirmation = InitialPassword });
            Check(reset.IsSuccessStatusCode, "HTTP recovery completes with the actual emailed token.");
            using HttpResponseMessage replay = await client.PostAsJsonAsync("auth/password-reset/confirm", new { token = secret, password = ChangedPassword, confirmation = ChangedPassword });
            Check(replay.StatusCode == HttpStatusCode.BadRequest, "HTTP replay rejected.");
            await LoginAsync(database, InitialPassword, "after-http-reset");
            options.BrevoSandbox = true;
            Check(!(await PasswordRecoveryClient.RequestAsync(client, address, None)).Accepted, "Unavailable email configuration is never reported as sent.");
            PasswordRecoveryResult limited = await PasswordRecoveryClient.RequestAsync(client, address, None);
            Check(!limited.Accepted && limited.Error.Contains("minute"), "Request rate limiting produces actionable feedback.");
            using HttpResponseMessage tooLarge = await client.PostAsJsonAsync("auth/password-reset/confirm", new { token = new string('x', 9000) });
            Check(tooLarge.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Recovery bodies are limited to 8 KiB.");
            using HttpClient oldApi = new(new RecoveryStatusHandler(HttpStatusCode.NotFound)) { BaseAddress = new Uri("https://old.example.test/") };
            Check(!(await PasswordRecoveryClient.RequestAsync(oldApi, address, None)).Accepted, "Old API 404 does not claim email dispatch.");
        }
        finally { await app.StopAsync(); }
    }

    private sealed class RecoveryMailHandler : HttpMessageHandler
    {
        internal TaskCompletionSource<string> Secret { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using JsonDocument payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            string body = payload.RootElement.GetProperty("textContent").GetString()!;
            Match match = Regex.Match(body, "#token=(atl_reset-[A-Za-z0-9_-]{43})");
            if (!match.Success) throw new InvalidOperationException("Recovery email link missing.");
            Secret.TrySetResult(match.Groups[1].Value);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
    private sealed class RecoveryStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
