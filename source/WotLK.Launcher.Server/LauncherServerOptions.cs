namespace WotLK.Launcher.Server;

public sealed class LauncherServerOptions
{
    public string ConnectionString { get; set; } = "";
    public string FeedRoot { get; set; } = "/srv/wotlk/launcher-feed";
    public string AddonRoot { get; set; } = "/var/www/wotlk-launcher/launcher/addons";
    public string HermesTicketUrl { get; set; } = "http://127.0.0.1:8099/internal/launcher-ticket/";
    public string HermesSharedSecret { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}
