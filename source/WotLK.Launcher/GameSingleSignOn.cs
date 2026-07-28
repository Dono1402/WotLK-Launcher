using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace WotLK.Launcher;

internal static class GameSingleSignOn
{
    private const string RegistryPath =
        @"Software\Custom Game Server Dev\Battle.net\Launch Options\WoW";

    private static readonly byte[] BattleNetEntropy =
    [
        0xC8, 0x76, 0xF4, 0xAE, 0x4C, 0x95, 0x2E, 0xFE,
        0xF2, 0xFA, 0x0F, 0x54, 0x19, 0xC0, 0x9C, 0x43
    ];

    public static void Write(GameTicket ticket, string locale)
    {
        string normalizedLocale = LauncherSettings.NormalizeGameLocale(locale);
        string accountState = JsonSerializer.Serialize(new
        {
            account_country = "BE",
            account_id = ticket.Username,
            game_account = new
            {
                has_game_time = true,
                id = ticket.GameAccount,
                is_trial = false,
                name = ticket.GameAccount,
                region = "EU"
            },
            igr_detected = false,
            ratings_board_min_age = 0,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            RegistryPath,
            writable: true)
            ?? throw new InvalidOperationException(
                "Impossible d'ouvrir la configuration Battle.net locale.");

        key.SetValue("ACCOUNT", Protect(ticket.Username), RegistryValueKind.Binary);
        key.SetValue("WEB_TOKEN", Protect(ticket.Ticket), RegistryValueKind.Binary);
        key.SetValue("ACCOUNT_STATE", Protect(accountState), RegistryValueKind.Binary);
        key.SetValue("GAME_ACCOUNT", ticket.GameAccount, RegistryValueKind.String);
        key.SetValue("LOCALE", normalizedLocale, RegistryValueKind.String);
        key.SetValue("LOCALE_AUDIO", normalizedLocale, RegistryValueKind.String);
        key.SetValue("REGION", "EU", RegistryValueKind.String);
        key.SetValue("CONNECTION_STRING", "animeclub.fr", RegistryValueKind.String);
        key.SetValue("LAUNCH_64BIT", "true", RegistryValueKind.String);
        key.SetValue(
            "ACCOUNT_TS",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            RegistryValueKind.String);
    }

    private static byte[] Protect(string value)
        => ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            BattleNetEntropy,
            DataProtectionScope.CurrentUser);
}
