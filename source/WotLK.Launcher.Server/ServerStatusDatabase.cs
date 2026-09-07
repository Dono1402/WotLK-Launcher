using System.Globalization;
using MySqlConnector;

namespace WotLK.Launcher.Server;

public sealed record ServerOnlinePlayerCount(int Count, string Kind);

public sealed partial class LauncherDatabase
{
    public async Task<ServerOnlinePlayerCount> GetOnlinePlayerCountAsync(CancellationToken cancellationToken)
    {
        // AzerothCore characters tables are realm-specific databases, with no
        // realm column. Never combine another realm's character database here.
        string characters = QuoteArmoryDatabase(_options.CharacterDatabaseName);
        bool excludeRandomBots = !string.IsNullOrWhiteSpace(_options.PlayerbotsDatabaseName);
        // RandomPlayerbotMgr registers every generated bot account here, including
        // unassigned type 0, RNDbot type 1 and AddClass type 2. Exclude all entries.
        string? bots = excludeRandomBots ? QuoteArmoryDatabase(_options.PlayerbotsDatabaseName!) : null;
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandTimeout = 3;
        command.CommandText = $"SELECT COUNT(*) FROM {characters}.characters c WHERE c.online=1"
            + (bots is null ? ";" : $" AND NOT EXISTS (SELECT 1 FROM {bots}.playerbots_account_type b WHERE b.account_id=c.account);");
        int count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count < 0) throw new InvalidDataException("Invalid online player count.");
        return new(count, excludeRandomBots ? "excluding-random-bots" : "characters");
    }
}
