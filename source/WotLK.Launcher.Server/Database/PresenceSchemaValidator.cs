using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed partial class LauncherSchemaValidator
{
    internal Task ValidatePresenceAsync(MySqlConnection connection, CancellationToken token) => ValidateAsync(connection,
        new Dictionary<string, TableExpectation>(StringComparer.Ordinal)
        {
            ["atlas_launcher_presence"] = Table(
                [C("account_id","int unsigned","NO"), C("manual_status","varchar(7)","NO","online",collation:"ascii_bin"),
                 C("effective_status","varchar(7)","NO","offline",collation:"ascii_bin"), C("last_active_at","datetime(6)","NO"),
                 C("version","bigint unsigned","NO","1"), C("updated_at","datetime(6)","NO","CURRENT_TIMESTAMP(6)","DEFAULT_GENERATED")],
                [I("PRIMARY",0,1,"account_id")], [F("fk_atlas_presence_profile",1,"account_id","atlas_launcher_profile","account_id")],
                ["chk_atlas_presence_manual","chk_atlas_presence_effective"])
        }, token);
}
