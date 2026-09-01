using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace WotLK.Launcher.Server.Avatars;

internal interface IAvatarMutationLockProvider
{
    Task<IAvatarMutationLease?> TryAcquireAsync(uint accountId, CancellationToken cancellationToken);
}

internal interface IAvatarMutationLease : IAsyncDisposable;

internal sealed class AvatarMutationLockProvider : IAvatarMutationLockProvider
{
    private readonly string _connectionString;
    private readonly string _databaseScope;

    internal AvatarMutationLockProvider(LauncherServerOptions options)
    {
        _connectionString = options.ConnectionString;
        MySqlConnectionStringBuilder builder = new(_connectionString);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.Database));
        _databaseScope = Convert.ToHexString(hash.AsSpan(0, 8));
    }

    public async Task<IAvatarMutationLease?> TryAcquireAsync(
        uint accountId,
        CancellationToken cancellationToken)
    {
        MySqlConnection connection = new(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            string lockName = $"atlas_avatar:{_databaseScope}:{accountId}";
            await using MySqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT GET_LOCK(@name, 0)";
            command.Parameters.AddWithValue("@name", lockName);
            object? result = await command.ExecuteScalarAsync(cancellationToken);
            if (Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                await connection.DisposeAsync();
                return null;
            }
            return new AvatarMutationLease(connection, lockName);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class AvatarMutationLease : IAvatarMutationLease
{
    private MySqlConnection? _connection;
    private readonly string _lockName;

    internal AvatarMutationLease(MySqlConnection connection, string lockName)
    {
        _connection = connection;
        _lockName = lockName;
    }

    public async ValueTask DisposeAsync()
    {
        MySqlConnection? connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
            return;

        try
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using MySqlCommand command = connection.CreateCommand();
                command.CommandText = "SELECT RELEASE_LOCK(@name)";
                command.Parameters.AddWithValue("@name", _lockName);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        catch
        {
            MySqlConnection.ClearPool(connection);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
