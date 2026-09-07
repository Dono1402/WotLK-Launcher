CREATE TABLE atlas_launcher_presence (
    account_id INT UNSIGNED NOT NULL PRIMARY KEY,
    manual_status VARCHAR(7) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'online',
    effective_status VARCHAR(7) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'offline',
    last_active_at DATETIME(6) NOT NULL,
    version BIGINT UNSIGNED NOT NULL DEFAULT 1,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT fk_atlas_presence_profile FOREIGN KEY (account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE,
    CONSTRAINT chk_atlas_presence_manual CHECK (manual_status IN ('online','away','dnd','offline')),
    CONSTRAINT chk_atlas_presence_effective CHECK (effective_status IN ('online','away','dnd','offline'))
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- Keep the existing account choice when upgrading from chat preferences.
INSERT INTO atlas_launcher_presence(account_id,manual_status,effective_status,last_active_at)
SELECT account_id,'dnd','offline',UTC_TIMESTAMP(6)
FROM atlas_launcher_chat_v2_preferences WHERE do_not_disturb=TRUE;
