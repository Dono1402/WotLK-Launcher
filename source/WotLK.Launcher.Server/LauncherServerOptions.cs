namespace WotLK.Launcher.Server;

public sealed class LauncherServerOptions
{
    public string ConnectionString { get; set; } = "";
    public uint? MaximumSchemaVersion { get; set; }
    public string CharacterDatabaseName { get; set; } = "arthas_chars";
    public string WorldDatabaseName { get; set; } = "arthas_world";
    public string FeedRoot { get; set; } = "/srv/wotlk/launcher-feed";
    public string AddonRoot { get; set; } = "/var/www/wotlk-launcher/launcher/addons";
    public string AvatarMediaRoot { get; set; } = "/srv/wotlk/atlas-media";
    public string HermesTicketUrl { get; set; } = "http://127.0.0.1:8099/internal/launcher-ticket/";
    public string HermesSharedSecret { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "https://animeclub.fr/wotlk";
    public string BrevoApiKey { get; set; } = "";
    public string BrevoSenderEmail { get; set; } = "noreply@animeclub.fr";
    public string BrevoSenderName { get; set; } = "Atlas - Arthas";
    public bool BrevoSandbox { get; set; }
    public int EmailVerificationExpiryHours { get; set; } = 24;
    public int EmailVerificationCooldownSeconds { get; set; } = 60;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}
