using System.Data;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal async Task<IReadOnlyList<ShopCharacter>> ListShopCharactersAsync(uint accountId, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken);
        await using MySqlCommand command = CreateArmoryCommand(connection, transaction);
        string characters = QuoteArmoryDatabase(_options.CharacterDatabaseName);
        command.CommandText = $"""
            SELECT guid, name, level, online, money FROM {characters}.characters
            WHERE account = @account ORDER BY name, guid LIMIT 51;
            """;
        command.Parameters.AddWithValue("@account", accountId);
        List<ShopCharacter> result = [];
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bool online = reader.GetByte("online") != 0;
            result.Add(new(reader.GetUInt32("guid"), reader.GetString("name"), reader.GetByte("level"),
                online, online ? null : reader.GetUInt32("money")));
        }
        if (result.Count > 50) throw new InvalidDataException("Shop character limit exceeded.");
        return result;
    }
}
