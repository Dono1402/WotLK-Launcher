ALTER TABLE atlas_launcher_profile_avatar
    MODIFY current_avatar_asset_id BINARY(16) NULL;

CREATE TABLE atlas_launcher_avatar_upload_attempt (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    account_id INT UNSIGNED NOT NULL,
    attempted_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX ix_atlas_avatar_upload_account_time (account_id, attempted_at),
    CONSTRAINT fk_atlas_avatar_upload_account
        FOREIGN KEY (account_id) REFERENCES account(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
