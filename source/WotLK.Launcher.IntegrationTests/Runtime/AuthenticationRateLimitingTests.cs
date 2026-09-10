using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using WotLK.Launcher.Server;

internal static class AuthenticationRateLimitingTests
{
    private static int _checks;

    internal static int Run()
    {
        _checks = 0;
        VerifyIpAndActionIsolation();
        VerifyIpv6PrefixAggregation();
        VerifyForwardedHeaderTrustBoundary();
        VerifyAuthenticationBodyLimits();
        VerifyAuthenticationResponseHeaders();
        VerifyForgedAuthenticationInputs();
        VerifyEmailVerificationFormBoundary();
        VerifyRefreshRevocationPolicyAsync().GetAwaiter().GetResult();
        Console.WriteLine(
            $"Authentication rate limiting PASS: {_checks} assertions; in-memory limiters and options only, no listener or network access.");
        return 0;
    }

    private static void VerifyAuthenticationResponseHeaders()
    {
        DefaultHttpContext context = new();
        AuthenticationResponseHeaders.Apply(context.Response);
        Check(
            string.Equals(
                context.Response.Headers.CacheControl.ToString(),
                "no-store",
                StringComparison.Ordinal)
            && string.Equals(
                context.Response.Headers.Pragma.ToString(),
                "no-cache",
                StringComparison.Ordinal),
            "Every token-emitting authentication response disables shared and browser caching.");
    }

    private static void VerifyEmailVerificationFormBoundary()
    {
        DefaultHttpContext normal = new();
        normal.Request.ContentType = "application/x-www-form-urlencoded";
        normal.Request.ContentLength = 64;
        Check(
            AuthenticationInputValidation.IsBoundedEmailVerificationForm(normal.Request),
            "A bounded browser verification form with a declared length remains accepted.");

        DefaultHttpContext chunked = new();
        chunked.Request.ContentType = "application/x-www-form-urlencoded";
        chunked.Request.ContentLength = null;
        Check(
            !AuthenticationInputValidation.IsBoundedEmailVerificationForm(chunked.Request),
            "A chunked verification form is rejected before ReadFormAsync can buffer an undeclared body.");

        DefaultHttpContext oversized = new();
        oversized.Request.ContentType = "application/x-www-form-urlencoded";
        oversized.Request.ContentLength =
            AuthenticationInputValidation.MaximumEmailVerificationFormBytes + 1;
        Check(
            !AuthenticationInputValidation.IsBoundedEmailVerificationForm(oversized.Request),
            "A declared verification form larger than 4 KiB is rejected before parsing.");
    }

    private static async Task VerifyRefreshRevocationPolicyAsync()
    {
        foreach ((LauncherDatabase.RefreshSessionResult result, string label) in new[]
        {
            (LauncherDatabase.RefreshSessionResult.Replay("REPLAY_USER"), "replay"),
            (LauncherDatabase.RefreshSessionResult.RotationLimitExceeded("LIMIT_USER"),
                "rotation ceiling")
        })
        {
            int hermesCalls = 0;
            string? revokedUsername = null;
            IResult mapped = await AuthenticationEndpointResults.FromRefreshAsync(
                result,
                username =>
                {
                    hermesCalls++;
                    revokedUsername = username;
                    return Task.CompletedTask;
                });
            Check(mapped is IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized },
                $"A {label} revocation is mapped to HTTP 401.");
            Check(hermesCalls == 1 && revokedUsername == result.RevokedUsername,
                $"A {label} revocation asks Hermes to revoke exactly once for the affected account.");
        }

        int invalidHermesCalls = 0;
        IResult invalid = await AuthenticationEndpointResults.FromRefreshAsync(
            LauncherDatabase.RefreshSessionResult.Invalid,
            _ =>
            {
                invalidHermesCalls++;
                return Task.CompletedTask;
            });
        Check(invalid is IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized }
            && invalidHermesCalls == 0,
            "An invalid refresh remains HTTP 401 without an unrelated Hermes revocation.");
        Check(
            LauncherDatabase.RefreshSessionResult.RotationLimitExceeded("LIMIT_USER") is
            {
                ReplayUsername: null,
                RotationLimitWasExceeded: true,
                RevocationReason: LauncherDatabase.RefreshSessionRevocationReason.RotationLimitExceeded
            },
            "The rotation ceiling has a distinct revocation outcome from replay detection.");
    }

    private static void VerifyAuthenticationBodyLimits()
    {
        foreach ((string method, string path) in new[]
        {
            ("POST", "/api/v1/accounts"),
            ("POST", "/api/v1/auth/login"),
            ("POST", "/api/v1/auth/refresh"),
            ("POST", "/api/v1/auth/logout"),
            ("PATCH", "/api/v1/me/email"),
            ("PATCH", "/api/v1/me/social-profile"),
            ("POST", "/api/v1/me/password"),
            ("PATCH", "/api/v1/me/avatar"),
            ("POST", "/api/v1/friends/requests")
        })
        {
            DefaultHttpContext context = new();
            context.Request.Method = method;
            context.Request.Path = path;
            MutableMaxRequestBodySizeFeature feature = new();
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
            Check(
                AuthenticationRequestBodyLimits.TryConfigure(context, out long maximum)
                && maximum == AuthenticationRequestBodyLimits.MaximumJsonBodyBytes
                && feature.MaxRequestBodySize == maximum,
                $"{method} {path} receives the 8 KiB transport limit before DTO binding.");
        }

        DefaultHttpContext media = new();
        media.Request.Method = HttpMethods.Post;
        media.Request.Path = "/api/v1/me/avatar/photo";
        Check(!AuthenticationRequestBodyLimits.TryConfigure(media, out _),
            "The small JSON limit does not apply to the separately bounded avatar media route.");

        DefaultHttpContext trailingSlash = new();
        trailingSlash.Request.Method = HttpMethods.Post;
        trailingSlash.Request.Path = "/api/v1/auth/login/";
        trailingSlash.Features.Set<IHttpMaxRequestBodySizeFeature>(
            new MutableMaxRequestBodySizeFeature());
        Check(AuthenticationRequestBodyLimits.TryConfigure(trailingSlash, out _),
            "A trailing slash cannot bypass the authentication request-body limit.");

        bool nextCalled = false;
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        ApplicationBuilder builder = new(services);
        builder.UseAtlasAuthenticationRequestBodyLimits();
        builder.Run(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        DefaultHttpContext oversized = new();
        oversized.Request.Method = HttpMethods.Post;
        oversized.Request.Path = "/api/v1/auth/login";
        oversized.Request.ContentLength =
            AuthenticationRequestBodyLimits.MaximumJsonBodyBytes + 1;
        oversized.Features.Set<IHttpMaxRequestBodySizeFeature>(
            new MutableMaxRequestBodySizeFeature());
        builder.Build()(oversized).GetAwaiter().GetResult();
        Check(!nextCalled
            && oversized.Response.StatusCode == StatusCodes.Status413PayloadTooLarge,
            "A declared oversized authentication body is rejected before endpoint model binding.");
        services.Dispose();
    }

    private static void VerifyIpAndActionIsolation()
    {
        DefaultHttpContext ipA = Context("192.0.2.10");
        DefaultHttpContext ipB = Context("198.51.100.20");
        using PartitionedRateLimiter<HttpContext> login =
            AuthenticationRateLimiting.CreateLimiter(
                AuthenticationRateLimiting.Login,
                permitLimit: 2,
                window: TimeSpan.FromMinutes(5));
        using PartitionedRateLimiter<HttpContext> refresh =
            AuthenticationRateLimiting.CreateLimiter(
                AuthenticationRateLimiting.Refresh,
                permitLimit: 2,
                window: TimeSpan.FromMinutes(5));

        using RateLimitLease first = login.AttemptAcquire(ipA);
        using RateLimitLease second = login.AttemptAcquire(ipA);
        using RateLimitLease saturated = login.AttemptAcquire(ipA);
        Check(first.IsAcquired && second.IsAcquired && !saturated.IsAcquired,
            "One source address reaches its own login window after two permits.");

        using RateLimitLease otherIp = login.AttemptAcquire(ipB);
        Check(otherIp.IsAcquired,
            "Saturating one source address does not consume another address's permits.");

        using RateLimitLease otherAction = refresh.AttemptAcquire(ipA);
        Check(otherAction.IsAcquired,
            "Saturating login does not consume the same address's refresh permits.");
        Check(
            AuthenticationRateLimiting.PartitionKey(ipA, AuthenticationRateLimiting.Login)
            != AuthenticationRateLimiting.PartitionKey(ipA, AuthenticationRateLimiting.Refresh),
            "The action name is part of every partition key.");
        Check(
            AuthenticationRateLimiting.PartitionKey(ipA, AuthenticationRateLimiting.Login)
            != AuthenticationRateLimiting.PartitionKey(ipB, AuthenticationRateLimiting.Login),
            "The normalized remote address is part of every partition key.");
    }

    private static void VerifyForwardedHeaderTrustBoundary()
    {
        ForwardedHeadersOptions options = new();
        options.KnownProxies.Add(IPAddress.Parse("203.0.113.8"));
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
            IPAddress.Parse("10.0.0.0"),
            8));

        AuthenticationRateLimiting.ConfigureForwardedHeaders(options);

        Check(options.ForwardedHeaders ==
            (ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto),
            "Only the client-address and scheme forwarding headers are enabled.");
        Check(options.ForwardLimit == 1,
            "Only one trusted reverse-proxy hop is consumed.");
        Check(options.KnownNetworks.Count == 0,
            "No private or broad network range is trusted as a forwarding proxy.");
        Check(options.KnownProxies.Count == 2
            && options.KnownProxies.Contains(IPAddress.Loopback)
            && options.KnownProxies.Contains(IPAddress.IPv6Loopback),
            "Only IPv4 and IPv6 loopback proxies are trusted.");
    }

    private static void VerifyIpv6PrefixAggregation()
    {
        DefaultHttpContext firstAddress = Context("2001:db8:1234:5678::10");
        DefaultHttpContext rotatedAddress = Context("2001:db8:1234:5678:ffff::20");
        DefaultHttpContext otherPrefix = Context("2001:db8:1234:5679::10");
        using PartitionedRateLimiter<HttpContext> limiter =
            AuthenticationRateLimiting.CreateLimiter(
                AuthenticationRateLimiting.Login,
                permitLimit: 1,
                window: TimeSpan.FromMinutes(5));

        using RateLimitLease first = limiter.AttemptAcquire(firstAddress);
        using RateLimitLease samePrefix = limiter.AttemptAcquire(rotatedAddress);
        using RateLimitLease differentPrefix = limiter.AttemptAcquire(otherPrefix);
        Check(first.IsAcquired && !samePrefix.IsAcquired,
            "Rotating an IPv6 interface identifier inside one /64 cannot reset the authentication quota.");
        Check(differentPrefix.IsAcquired,
            "A different IPv6 /64 retains an independent authentication quota.");
        Check(
            AuthenticationRateLimiting.PartitionKey(
                Context("192.0.2.44"),
                AuthenticationRateLimiting.Login)
            == AuthenticationRateLimiting.PartitionKey(
                Context("::ffff:192.0.2.44"),
                AuthenticationRateLimiting.Login),
            "IPv4-mapped IPv6 addresses share the exact IPv4 partition.");
        Check(
            AuthenticationRateLimiting.PartitionKey(
                Context("192.0.2.44"),
                AuthenticationRateLimiting.Login)
            != AuthenticationRateLimiting.PartitionKey(
                Context("192.0.2.45"),
                AuthenticationRateLimiting.Login),
            "Native IPv4 clients remain isolated by their exact address.");
    }

    private static void VerifyForgedAuthenticationInputs()
    {
        AssertRejected(
            AuthenticationInputValidation.Registration(null),
            StatusCodes.Status400BadRequest,
            "A JSON null registration body is rejected without dereferencing it.");
        AssertRejected(
            AuthenticationInputValidation.Registration(
                new RegisterRequest(null!, "player@example.test", "valid-password")),
            StatusCodes.Status400BadRequest,
            "A null registration username is rejected.");
        AssertRejected(
            AuthenticationInputValidation.Registration(
                new RegisterRequest("Player", null!, "valid-password")),
            StatusCodes.Status400BadRequest,
            "A null registration email is rejected.");
        AssertRejected(
            AuthenticationInputValidation.Registration(
                new RegisterRequest("Player", "player@example.test", null!)),
            StatusCodes.Status400BadRequest,
            "A null registration password is rejected.");
        AssertRejected(
            AuthenticationInputValidation.Registration(
                new RegisterRequest("Player", OversizedEmail(1_000), "valid-password")),
            StatusCodes.Status400BadRequest,
            "A 1,000-character registration email is rejected before database insertion.");

        AssertRejected(
            AuthenticationInputValidation.Login(null),
            StatusCodes.Status401Unauthorized,
            "A JSON null login body is rejected without dereferencing it.");
        AssertRejected(
            AuthenticationInputValidation.Login(
                new LoginRequest(null!, "password", null)),
            StatusCodes.Status401Unauthorized,
            "A null login username is rejected without account lookup.");
        AssertRejected(
            AuthenticationInputValidation.Login(
                new LoginRequest("Player", null!, null)),
            StatusCodes.Status401Unauthorized,
            "A null login password is rejected without account lookup.");
        AssertRejected(
            AuthenticationInputValidation.Login(
                new LoginRequest("Player", new string('p', 129), null)),
            StatusCodes.Status401Unauthorized,
            "An oversized login password is rejected before password verification.");
        AssertRejected(
            AuthenticationInputValidation.Login(
                new LoginRequest("Player", "password", new string('d', 1_000))),
            StatusCodes.Status400BadRequest,
            "A 1,000-character login device name is rejected before session insertion.");

        AssertRejected(
            AuthenticationInputValidation.Refresh(null, out string nullRefresh),
            StatusCodes.Status401Unauthorized,
            "A JSON null refresh body is rejected without dereferencing it.");
        Check(nullRefresh.Length == 0,
            "A null refresh body does not produce a database lookup token.");
        AssertRejected(
            AuthenticationInputValidation.Refresh(
                new RefreshRequest(null!),
                out string missingRefresh),
            StatusCodes.Status401Unauthorized,
            "A null refresh-token field is rejected without hashing it.");
        Check(missingRefresh.Length == 0,
            "A null refresh-token field does not produce a database lookup token.");
        AuthenticationInputValidation.Logout(null, out string? nullLogoutProof);
        Check(nullLogoutProof is null,
            "A JSON null logout body is accepted as an absent optional refresh proof for bearer logout.");
        AuthenticationInputValidation.Logout(
            new LogoutRequest("forged-refresh-proof"),
            out string? forgedLogoutProof);
        Check(forgedLogoutProof is null,
            "A malformed optional logout refresh proof is ignored instead of being hashed or dereferenced.");

        AssertRejected(
            AuthenticationInputValidation.ChangeEmail(null),
            StatusCodes.Status400BadRequest,
            "A JSON null email-change body is rejected without dereferencing it.");
        AssertRejected(
            AuthenticationInputValidation.ChangeEmail(new ChangeEmailRequest(null!)),
            StatusCodes.Status400BadRequest,
            "A null replacement email is rejected.");
        AssertRejected(
            AuthenticationInputValidation.ChangeEmail(
                new ChangeEmailRequest(OversizedEmail(10_000))),
            StatusCodes.Status400BadRequest,
            "A 10,000-character replacement email is rejected before database update.");

        AssertRejected(
            AuthenticationInputValidation.DeviceName(new string('h', 10_000)),
            StatusCodes.Status400BadRequest,
            "A 10,000-character X-Atlas-Device header is rejected before registration insertion.");

        AssertRejected(
            AuthenticationInputValidation.Password(null),
            StatusCodes.Status400BadRequest,
            "A JSON null password-change body is rejected without dereferencing it.");
        AssertRejected(
            AuthenticationInputValidation.Password(
                new ChangePasswordRequest(null!, "valid-password")),
            StatusCodes.Status400BadRequest,
            "A null current password is rejected.");
        AssertRejected(
            AuthenticationInputValidation.Password(
                new ChangePasswordRequest("current-password", null!)),
            StatusCodes.Status400BadRequest,
            "A null replacement password is rejected.");
        AssertRejected(
            AuthenticationInputValidation.Password(
                new ChangePasswordRequest("current-password", new string('p', 129))),
            StatusCodes.Status400BadRequest,
            "An oversized replacement password is rejected.");

        AssertRejected(
            AuthenticationInputValidation.Friend(null, out string nullBodyFriend),
            StatusCodes.Status400BadRequest,
            "A JSON null friend-request body is rejected without dereferencing it.");
        Check(nullBodyFriend.Length == 0,
            "A null friend-request body does not produce a database lookup key.");
        AssertRejected(
            AuthenticationInputValidation.Friend(
                new CreateFriendRequest(null!),
                out string nullFriend),
            StatusCodes.Status400BadRequest,
            "A null friend username is rejected.");
        Check(nullFriend.Length == 0,
            "A null friend username does not produce a database lookup key.");

        AssertRejected(
            AuthenticationInputValidation.SocialProfile(
                null,
                out string nullStatus,
                out string nullBio),
            StatusCodes.Status400BadRequest,
            "A JSON null social-profile body is rejected without dereferencing it.");
        Check(nullStatus.Length == 0 && nullBio.Length == 0,
            "A null social-profile body does not produce profile values.");
        AssertRejected(
            AuthenticationInputValidation.Avatar(null, out string? nullAvatar),
            StatusCodes.Status400BadRequest,
            "A JSON null avatar body is rejected without dereferencing it.");
        Check(nullAvatar is null,
            "A null avatar body does not produce an avatar key.");

        Check(
            AuthenticationInputValidation.Registration(
                new RegisterRequest("Player_1", "player@example.test", "valid-password")) is null,
            "A structurally valid registration remains accepted.");
        Check(
            AuthenticationInputValidation.Login(
                new LoginRequest("Player_1", "password", null)) is null,
            "A structurally valid login remains accepted.");
        string validRefresh = "atl_refresh-" + new string('a', 43);
        Check(
            AuthenticationInputValidation.Refresh(
                new RefreshRequest(validRefresh),
                out string acceptedRefresh) is null
            && acceptedRefresh == validRefresh,
            "A structurally valid refresh proof remains accepted unchanged.");
        Check(
            AuthenticationInputValidation.Password(
                new ChangePasswordRequest("current-password", "valid-password")) is null,
            "A structurally valid password change remains accepted.");
        Check(
            AuthenticationInputValidation.ChangeEmail(
                new ChangeEmailRequest("  player@example.test  ")) is null,
            "A valid bounded replacement email remains accepted after trimming.");
        Check(
            AuthenticationInputValidation.DeviceName(null) is null
            && AuthenticationInputValidation.DeviceName("  DESKTOP-ATLAS  ") is null,
            "A missing or bounded device name remains accepted.");
        Check(
            AuthenticationInputValidation.Friend(
                new CreateFriendRequest("  Player_2  "),
                out string friendUsername) is null
            && friendUsername == "Player_2",
            "A structurally valid friend username remains accepted and is trimmed once.");
        Check(
            AuthenticationInputValidation.SocialProfile(
                new UpdateSocialProfileRequest("  Disponible  ", "  Bio  "),
                out string status,
                out string bio) is null
            && status == "Disponible"
            && bio == "Bio",
            "A bounded social profile remains accepted and normalized once.");
        Check(
            AuthenticationInputValidation.Avatar(
                new ChangeAvatarRequest("  ICE  "),
                out string? avatarKey) is null
            && avatarKey == "ice",
            "A known avatar remains accepted and normalized once.");
        Check(
            AuthenticationInputValidation.Avatar(
                new ChangeAvatarRequest(null),
                out string? clearedAvatar) is null
            && clearedAvatar is null,
            "An explicit null avatar field keeps the supported avatar-reset behavior.");
    }

    private static string OversizedEmail(int length)
    {
        const string domain = "@example.test";
        return new string('a', length - domain.Length) + domain;
    }

    private static void AssertRejected(
        AuthenticationInputRejection? rejection,
        int expectedStatusCode,
        string message)
    {
        Check(rejection is not null, message);
        IResult result = rejection!.ToResult();
        Check(
            result is IStatusCodeHttpResult { StatusCode: var statusCode }
            && statusCode == expectedStatusCode,
            $"{message} The endpoint rejection must preserve HTTP {expectedStatusCode}.");
    }

    private static DefaultHttpContext Context(string address)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MutableMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; set; }
        public long? MaxRequestBodySize { get; set; }
    }
}
