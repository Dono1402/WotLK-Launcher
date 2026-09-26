using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed partial class LauncherSchemaValidator
{
    internal Task ValidatePasswordRecoveryAsync(MySqlConnection connection, CancellationToken token)
        => ValidateAsync(connection, new Dictionary<string, TableExpectation>(StringComparer.Ordinal)
        {
            ["atlas_launcher_password_reset"] = Table(
                [C("account_id", "int unsigned", "NO"),
                 C("email_normalized", "varchar(254)", "NO", collation: "utf8mb4_0900_ai_ci"),
                 C("token_hash", "binary(32)", "NO"), C("credential_hash", "binary(32)", "NO"),
                 C("created_at", "datetime", "NO"), C("expires_at", "datetime", "NO"),
                 C("consumed_at", "datetime", "YES")],
                [I("PRIMARY", 0, 1, "account_id"), I("token_hash", 0, 1, "token_hash")],
                [F("fk_atlas_password_reset_profile", 1, "account_id", "atlas_launcher_profile", "account_id")])
        }, token);
}
