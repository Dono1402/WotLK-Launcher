CREATE TABLE IF NOT EXISTS atlas_launcher_avatar_asset (
    id BINARY(16) NOT NULL PRIMARY KEY,
    owner_account_id INT UNSIGNED NOT NULL,
    version BIGINT UNSIGNED NOT NULL,
    status TINYINT UNSIGNED NOT NULL,
    storage_key VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE INDEX uq_atlas_avatar_owner_version (owner_account_id, version),
    UNIQUE INDEX uq_atlas_avatar_storage_key (storage_key),
    INDEX ix_atlas_avatar_owner_status (owner_account_id, status),
    CONSTRAINT chk_atlas_avatar_status CHECK (status IN (0, 1, 2, 3)),
    CONSTRAINT fk_atlas_avatar_owner
        FOREIGN KEY (owner_account_id) REFERENCES account(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_launcher_avatar_variant (
    avatar_asset_id BINARY(16) NOT NULL,
    size SMALLINT UNSIGNED NOT NULL,
    content_type VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    byte_length INT UNSIGNED NOT NULL,
    sha256 BINARY(32) NOT NULL,
    PRIMARY KEY (avatar_asset_id, size),
    CONSTRAINT chk_atlas_avatar_variant_size CHECK (size IN (32, 64, 128, 256)),
    CONSTRAINT fk_atlas_avatar_variant_asset
        FOREIGN KEY (avatar_asset_id) REFERENCES atlas_launcher_avatar_asset(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_launcher_profile_avatar (
    account_id INT UNSIGNED NOT NULL PRIMARY KEY,
    current_avatar_asset_id BINARY(16) NOT NULL UNIQUE,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    CONSTRAINT fk_atlas_profile_avatar_profile
        FOREIGN KEY (account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE,
    CONSTRAINT fk_atlas_profile_avatar_asset
        FOREIGN KEY (current_avatar_asset_id) REFERENCES atlas_launcher_avatar_asset(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
