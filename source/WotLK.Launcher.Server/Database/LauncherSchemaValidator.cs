using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed class LauncherSchemaValidator
{
    private static readonly IReadOnlyDictionary<string, TableExpectation> HistoryTables = CreateHistoryTables();
    private static readonly IReadOnlyDictionary<string, TableExpectation> LegacyV1Tables = CreateLegacyTables(false);
    private static readonly IReadOnlyDictionary<string, TableExpectation> LegacyV4Tables = CreateLegacyTables(true);
    private static readonly IReadOnlyDictionary<string, TableExpectation> LegacyV5Tables = CreateLegacyTables(true, true);
    private static readonly IReadOnlyDictionary<string, TableExpectation> AvatarV2Tables = CreateAvatarTables(false, false);
    private static readonly IReadOnlyDictionary<string, TableExpectation> AvatarV3Tables = CreateAvatarTables(true, false);
    private static readonly IReadOnlyDictionary<string, TableExpectation> AvatarV4Tables = CreateAvatarTables(true, true);
    private static readonly IReadOnlyDictionary<string, TableExpectation> AvatarRateLimitV3Tables = CreateAvatarRateLimitTables(false);
    private static readonly IReadOnlyDictionary<string, TableExpectation> AvatarRateLimitV4Tables = CreateAvatarRateLimitTables(true);

    internal async Task<int> CountLegacyTablesAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN (
                  'atlas_launcher_profile',
                  'atlas_launcher_session',
                  'atlas_launcher_email_verification',
                  'atlas_launcher_friendship')
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal Task ValidateLegacyAsync(MySqlConnection connection, CancellationToken cancellationToken)
        => ValidateAsync(connection, LegacyV1Tables, cancellationToken);

    internal Task ValidateLegacyAsync(
        MySqlConnection connection,
        uint schemaVersion,
        CancellationToken cancellationToken)
        => ValidateAsync(
            connection,
            schemaVersion >= 5
                ? LegacyV5Tables
                : schemaVersion >= 4
                    ? LegacyV4Tables
                    : LegacyV1Tables,
            cancellationToken);

    internal Task ValidateHistoryAsync(MySqlConnection connection, CancellationToken cancellationToken)
        => ValidateAsync(connection, HistoryTables, cancellationToken);

    internal async Task ValidateAvatarAsync(
        MySqlConnection connection,
        uint schemaVersion,
        CancellationToken cancellationToken)
    {
        await ValidateAsync(
            connection,
            schemaVersion >= 4
                ? AvatarV4Tables
                : schemaVersion >= 3
                    ? AvatarV3Tables
                    : AvatarV2Tables,
            cancellationToken);
        if (schemaVersion >= 3)
        {
            await ValidateAsync(
                connection,
                schemaVersion >= 4 ? AvatarRateLimitV4Tables : AvatarRateLimitV3Tables,
                cancellationToken);
        }
    }

    private static async Task ValidateAsync(
        MySqlConnection connection,
        IReadOnlyDictionary<string, TableExpectation> expectedTables,
        CancellationToken cancellationToken)
    {
        foreach ((string tableName, TableExpectation expected) in expectedTables)
        {
            ActualTable actual = await ReadTableAsync(connection, tableName, cancellationToken);
            List<string> differences = [];

            if (!string.Equals(actual.Engine, "InnoDB", StringComparison.OrdinalIgnoreCase))
                differences.Add($"moteur={actual.Engine ?? "absent"}");
            if (!string.Equals(actual.Collation, expected.Collation, StringComparison.OrdinalIgnoreCase))
                differences.Add($"collation={actual.Collation ?? "absente"}");
            Compare("colonnes", expected.Columns, actual.Columns, differences, preserveOrder: true);
            Compare("index", expected.Indexes, actual.Indexes, differences, preserveOrder: false);
            Compare("cles etrangeres", expected.ForeignKeys, actual.ForeignKeys, differences, preserveOrder: false);
            Compare("checks", expected.CheckConstraints, actual.CheckConstraints, differences, preserveOrder: false);

            if (differences.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Le schema de {tableName} ne correspond pas a la baseline Atlas : {string.Join(" ; ", differences)}.");
            }
        }
    }

    private static void Compare(
        string label,
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        List<string> differences,
        bool preserveOrder)
    {
        IEnumerable<string> expectedValues = preserveOrder
            ? expected
            : expected.OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> actualValues = preserveOrder
            ? actual
            : actual.OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        if (!expectedValues.SequenceEqual(actualValues, StringComparer.OrdinalIgnoreCase))
        {
            differences.Add(
                $"{label} attendus=[{string.Join(",", expected)}] reels=[{string.Join(",", actual)}]");
        }
    }

    private static async Task<ActualTable> ReadTableAsync(
        MySqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        string? engine = null;
        string? collation = null;
        await using (MySqlCommand table = connection.CreateCommand())
        {
            table.CommandText = """
                SELECT ENGINE, TABLE_COLLATION
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
                """;
            table.Parameters.AddWithValue("@table", tableName);
            await using MySqlDataReader reader = await table.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                engine = reader.GetString(0);
                collation = reader.GetString(1);
            }
        }

        List<string> columns = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, COLLATION_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
                ORDER BY ORDINAL_POSITION
                """;
            command.Parameters.AddWithValue("@table", tableName);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(string.Join('|',
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? "<null>" : Convert.ToString(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? "<null>" : reader.GetString(5)));
            }
        }

        List<string> indexes = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
                ORDER BY INDEX_NAME, SEQ_IN_INDEX
                """;
            command.Parameters.AddWithValue("@table", tableName);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add($"{reader.GetString(0)}|{reader.GetInt32(1)}|{reader.GetInt32(2)}|{reader.GetString(3)}");
            }
        }

        List<string> foreignKeys = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT k.CONSTRAINT_NAME, k.ORDINAL_POSITION, k.COLUMN_NAME,
                       k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME, r.DELETE_RULE
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
                 AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                 AND r.TABLE_NAME = k.TABLE_NAME
                WHERE k.TABLE_SCHEMA = DATABASE()
                  AND k.TABLE_NAME = @table
                  AND k.REFERENCED_TABLE_NAME IS NOT NULL
                ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
                """;
            command.Parameters.AddWithValue("@table", tableName);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                foreignKeys.Add(string.Join('|',
                    reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }
        }

        List<string> checks = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT tc.CONSTRAINT_NAME
                FROM information_schema.TABLE_CONSTRAINTS tc
                WHERE tc.CONSTRAINT_SCHEMA = DATABASE()
                  AND tc.TABLE_NAME = @table
                  AND tc.CONSTRAINT_TYPE = 'CHECK'
                ORDER BY tc.CONSTRAINT_NAME
                """;
            command.Parameters.AddWithValue("@table", tableName);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                checks.Add(reader.GetString(0));
        }

        return new ActualTable(engine, collation, columns, indexes, foreignKeys, checks);
    }

    private static IReadOnlyDictionary<string, TableExpectation> CreateLegacyTables(
        bool profileScopedReferences,
        bool socialProfile = false)
    {
        string atlasOwnerTable = profileScopedReferences ? "atlas_launcher_profile" : "account";
        string atlasOwnerColumn = profileScopedReferences ? "account_id" : "id";
        return new Dictionary<string, TableExpectation>(StringComparer.Ordinal)
        {
            ["atlas_launcher_profile"] = Table(
                CreateProfileColumns(socialProfile),
                [I("PRIMARY", 0, 1, "account_id"), I("email_normalized", 0, 1, "email_normalized")],
                [F("fk_atlas_profile_account", 1, "account_id", "account", "id")]),
            ["atlas_launcher_session"] = Table(
                [
                    C("id", "binary(16)", "NO"), C("account_id", "int unsigned", "NO"),
                    C("access_hash", "binary(32)", "NO"), C("refresh_hash", "binary(32)", "NO"),
                    C("device_name", "varchar(128)", "YES", collation: "utf8mb4_0900_ai_ci"),
                    C("access_expires_at", "datetime", "NO"), C("refresh_expires_at", "datetime", "NO"),
                    C("revoked_at", "datetime", "YES"),
                    C("created_at", "datetime", "NO", "CURRENT_TIMESTAMP", "DEFAULT_GENERATED"),
                    C("updated_at", "datetime", "NO", "CURRENT_TIMESTAMP", "DEFAULT_GENERATED on update CURRENT_TIMESTAMP")
                ],
                [
                    I("PRIMARY", 0, 1, "id"), I("access_hash", 0, 1, "access_hash"),
                    I("ix_atlas_session_account", 1, 1, "account_id"),
                    I("ix_atlas_session_refresh_expiry", 1, 1, "refresh_expires_at"),
                    I("refresh_hash", 0, 1, "refresh_hash")
                ],
                [F("fk_atlas_session_account", 1, "account_id", atlasOwnerTable, atlasOwnerColumn)]),
            ["atlas_launcher_email_verification"] = Table(
                [
                    C("id", "binary(16)", "NO"), C("account_id", "int unsigned", "NO"),
                    C("email_normalized", "varchar(254)", "NO", collation: "utf8mb4_0900_ai_ci"),
                    C("token_hash", "binary(32)", "NO"), C("expires_at", "datetime", "NO"),
                    C("consumed_at", "datetime", "YES"),
                    C("created_at", "datetime", "NO", "CURRENT_TIMESTAMP", "DEFAULT_GENERATED")
                ],
                [
                    I("PRIMARY", 0, 1, "id"),
                    I("ix_atlas_email_account_created", 1, 1, "account_id"),
                    I("ix_atlas_email_account_created", 1, 2, "created_at"),
                    I("ix_atlas_email_expiry", 1, 1, "expires_at"),
                    I("token_hash", 0, 1, "token_hash")
                ],
                [F("fk_atlas_email_account", 1, "account_id", atlasOwnerTable, atlasOwnerColumn)]),
            ["atlas_launcher_friendship"] = Table(
                [
                    C("account_low_id", "int unsigned", "NO"), C("account_high_id", "int unsigned", "NO"),
                    C("requested_by_id", "int unsigned", "NO"), C("accepted_at", "datetime", "YES"),
                    C("created_at", "datetime", "NO", "CURRENT_TIMESTAMP", "DEFAULT_GENERATED"),
                    C("updated_at", "datetime", "NO", "CURRENT_TIMESTAMP", "DEFAULT_GENERATED on update CURRENT_TIMESTAMP")
                ],
                [
                    I("PRIMARY", 0, 1, "account_low_id"), I("PRIMARY", 0, 2, "account_high_id"),
                    I("fk_atlas_friend_high", 1, 1, "account_high_id"),
                    I("ix_atlas_friend_requested_by", 1, 1, "requested_by_id")
                ],
                [
                    F("fk_atlas_friend_high", 1, "account_high_id", atlasOwnerTable, atlasOwnerColumn),
                    F("fk_atlas_friend_low", 1, "account_low_id", atlasOwnerTable, atlasOwnerColumn),
                    F("fk_atlas_friend_requester", 1, "requested_by_id", atlasOwnerTable, atlasOwnerColumn)
                ])
        };
    }

    private static IReadOnlyList<string> CreateProfileColumns(bool socialProfile)
    {
        List<string> columns =
        [
            C("account_id", "int unsigned", "NO"),
            C("display_username", "varchar(32)", "NO", collation: "utf8mb4_0900_ai_ci"),
            C("email_normalized", "varchar(254)", "NO", collation: "utf8mb4_0900_ai_ci"),
            C("email_verified_at", "datetime", "YES"),
            C("avatar_key", "varchar(128)", "YES", collation: "utf8mb4_0900_ai_ci")
        ];
        if (socialProfile)
        {
            columns.Add(C("status_message", "varchar(80)", "YES", collation: "utf8mb4_0900_ai_ci"));
            columns.Add(C("bio", "varchar(280)", "YES", collation: "utf8mb4_0900_ai_ci"));
        }
        columns.AddRange(
        [
            C("two_factor_enabled", "tinyint(1)", "NO", "0"),
            C("recovery_codes_generated", "tinyint(1)", "NO", "0"),
            C("created_at", "datetime", "NO", "CURRENT_TIMESTAMP", "DEFAULT_GENERATED"),
            C("updated_at", "datetime", "NO", "CURRENT_TIMESTAMP", "DEFAULT_GENERATED on update CURRENT_TIMESTAMP")
        ]);
        return columns;
    }

    private static IReadOnlyDictionary<string, TableExpectation> CreateHistoryTables()
    {
        return new Dictionary<string, TableExpectation>(StringComparer.Ordinal)
        {
            ["atlas_launcher_schema_history"] = Table(
                [
                    C("version", "int unsigned", "NO"),
                    C("name", "varchar(128)", "NO", collation: "ascii_bin"),
                    C("sha256", "binary(32)", "NO"),
                    C("applied_at", "datetime(6)", "NO"),
                    C("duration_ms", "int unsigned", "NO"),
                    C("application_version", "varchar(64)", "NO", collation: "ascii_bin")
                ],
                [I("PRIMARY", 0, 1, "version")],
                [])
        };
    }

    private static IReadOnlyDictionary<string, TableExpectation> CreateAvatarTables(
        bool nullableActiveAvatar,
        bool profileScopedReferences)
    {
        string avatarOwnerTable = profileScopedReferences ? "atlas_launcher_profile" : "account";
        string avatarOwnerColumn = profileScopedReferences ? "account_id" : "id";
        return new Dictionary<string, TableExpectation>(StringComparer.Ordinal)
        {
            ["atlas_launcher_avatar_asset"] = Table(
                [
                    C("id", "binary(16)", "NO"), C("owner_account_id", "int unsigned", "NO"),
                    C("version", "bigint unsigned", "NO"), C("status", "tinyint unsigned", "NO"),
                    C("storage_key", "varchar(255)", "NO", collation: "ascii_bin"),
                    C("created_at", "datetime(6)", "NO", "CURRENT_TIMESTAMP(6)", "DEFAULT_GENERATED"),
                    C("updated_at", "datetime(6)", "NO", "CURRENT_TIMESTAMP(6)", "DEFAULT_GENERATED on update CURRENT_TIMESTAMP(6)")
                ],
                [
                    I("PRIMARY", 0, 1, "id"),
                    I("ix_atlas_avatar_owner_status", 1, 1, "owner_account_id"),
                    I("ix_atlas_avatar_owner_status", 1, 2, "status"),
                    I("uq_atlas_avatar_owner_version", 0, 1, "owner_account_id"),
                    I("uq_atlas_avatar_owner_version", 0, 2, "version"),
                    I("uq_atlas_avatar_storage_key", 0, 1, "storage_key")
                ],
                [F("fk_atlas_avatar_owner", 1, "owner_account_id", avatarOwnerTable, avatarOwnerColumn)],
                ["chk_atlas_avatar_status"]),
            ["atlas_launcher_avatar_variant"] = Table(
                [
                    C("avatar_asset_id", "binary(16)", "NO"), C("size", "smallint unsigned", "NO"),
                    C("content_type", "varchar(32)", "NO", collation: "ascii_bin"),
                    C("byte_length", "int unsigned", "NO"), C("sha256", "binary(32)", "NO")
                ],
                [I("PRIMARY", 0, 1, "avatar_asset_id"), I("PRIMARY", 0, 2, "size")],
                [F("fk_atlas_avatar_variant_asset", 1, "avatar_asset_id", "atlas_launcher_avatar_asset", "id")],
                ["chk_atlas_avatar_variant_size"]),
            ["atlas_launcher_profile_avatar"] = Table(
                [
                    C("account_id", "int unsigned", "NO"),
                    C("current_avatar_asset_id", "binary(16)", nullableActiveAvatar ? "YES" : "NO"),
                    C("updated_at", "datetime(6)", "NO", "CURRENT_TIMESTAMP(6)", "DEFAULT_GENERATED on update CURRENT_TIMESTAMP(6)")
                ],
                [I("PRIMARY", 0, 1, "account_id"), I("current_avatar_asset_id", 0, 1, "current_avatar_asset_id")],
                [
                    F("fk_atlas_profile_avatar_asset", 1, "current_avatar_asset_id", "atlas_launcher_avatar_asset", "id"),
                    F("fk_atlas_profile_avatar_profile", 1, "account_id", "atlas_launcher_profile", "account_id")
                ])
        };
    }

    private static IReadOnlyDictionary<string, TableExpectation> CreateAvatarRateLimitTables(
        bool profileScopedReferences)
    {
        string avatarOwnerTable = profileScopedReferences ? "atlas_launcher_profile" : "account";
        string avatarOwnerColumn = profileScopedReferences ? "account_id" : "id";
        return new Dictionary<string, TableExpectation>(StringComparer.Ordinal)
        {
            ["atlas_launcher_avatar_upload_attempt"] = Table(
                [
                    C("id", "bigint unsigned", "NO", extra: "auto_increment"),
                    C("account_id", "int unsigned", "NO"),
                    C("attempted_at", "datetime(6)", "NO", "CURRENT_TIMESTAMP(6)", "DEFAULT_GENERATED")
                ],
                [
                    I("PRIMARY", 0, 1, "id"),
                    I("ix_atlas_avatar_upload_account_time", 1, 1, "account_id"),
                    I("ix_atlas_avatar_upload_account_time", 1, 2, "attempted_at")
                ],
                [F("fk_atlas_avatar_upload_account", 1, "account_id", avatarOwnerTable, avatarOwnerColumn)])
        };
    }

    private static TableExpectation Table(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> indexes,
        IReadOnlyList<string> foreignKeys,
        IReadOnlyList<string>? checks = null)
        => new("utf8mb4_0900_ai_ci", columns, indexes, foreignKeys, checks ?? []);

    private static string C(
        string name,
        string type,
        string nullable,
        string? defaultValue = null,
        string extra = "",
        string? collation = null)
        => string.Join('|', name, type, nullable, defaultValue ?? "<null>", extra, collation ?? "<null>");

    private static string I(string name, int nonUnique, int sequence, string column)
        => $"{name}|{nonUnique}|{sequence}|{column}";

    private static string F(
        string name,
        int sequence,
        string column,
        string referencedTable,
        string referencedColumn)
        => $"{name}|{sequence}|{column}|{referencedTable}|{referencedColumn}|CASCADE";

    private sealed record TableExpectation(
        string Collation,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> Indexes,
        IReadOnlyList<string> ForeignKeys,
        IReadOnlyList<string> CheckConstraints);

    private sealed record ActualTable(
        string? Engine,
        string? Collation,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> Indexes,
        IReadOnlyList<string> ForeignKeys,
        IReadOnlyList<string> CheckConstraints);
}
