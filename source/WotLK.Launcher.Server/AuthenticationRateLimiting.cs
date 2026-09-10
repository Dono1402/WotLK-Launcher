using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace WotLK.Launcher.Server;

internal static class AuthenticationRateLimiting
{
    internal const string Registration = "auth-registration";
    internal const string Login = "auth-login";
    internal const string Refresh = "auth-refresh";
    internal const string Logout = "auth-logout";
    internal const string ProfileMutation = "auth-profile-mutation";
    internal const string EmailDispatch = "auth-email-dispatch";
    internal const string EmailVerification = "auth-email-verification";
    internal const string Password = "auth-password";
    internal const string Friendship = "auth-friendship";
    internal const string GameTicket = "auth-game-ticket";

    private const int DefaultPermitLimit = 10;
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    internal static IServiceCollection AddAtlasAuthenticationRateLimiting(
        this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            AddPolicy(options, Registration);
            AddPolicy(options, Login);
            AddPolicy(options, Refresh);
            AddPolicy(options, Logout);
            AddPolicy(options, ProfileMutation);
            AddPolicy(options, EmailDispatch);
            AddPolicy(options, EmailVerification);
            AddPolicy(options, Password);
            AddPolicy(options, Friendship);
            AddPolicy(options, GameTicket);
        });
        return services;
    }

    internal static void ConfigureForwardedHeaders(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    }

    internal static PartitionedRateLimiter<HttpContext> CreateLimiter(
        string action,
        int permitLimit,
        TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (permitLimit <= 0) throw new ArgumentOutOfRangeException(nameof(permitLimit));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        return PartitionedRateLimiter.Create<HttpContext, string>(context =>
            CreatePartition(context, action, permitLimit, window));
    }

    internal static string PartitionKey(HttpContext context, string action)
    {
        IPAddress address = context.Connection.RemoteIpAddress ?? IPAddress.None;
        return action + "\n" + NormalizeClientAddress(address);
    }

    internal static IPAddress NormalizeClientAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    private static void AddPolicy(RateLimiterOptions options, string action)
        => options.AddPolicy(
            action,
            context => CreatePartition(
                context,
                action,
                DefaultPermitLimit,
                DefaultWindow));

    private static RateLimitPartition<string> CreatePartition(
        HttpContext context,
        string action,
        int permitLimit,
        TimeSpan window)
        => RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context, action),
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                Window = window
            });
}
