CREATE TABLE IF NOT EXISTS atlas_armory_combat_snapshot (
    guid INT UNSIGNED NOT NULL,
    snapshot JSON NOT NULL,
    PRIMARY KEY (guid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
