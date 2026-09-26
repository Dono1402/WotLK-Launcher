CREATE TABLE IF NOT EXISTS atlas_launcher_password_reset (
    account_id INT UNSIGNED NOT NULL PRIMARY KEY,
    email_normalized VARCHAR(254) NOT NULL,
    token_hash BINARY(32) NOT NULL UNIQUE,
    credential_hash BINARY(32) NOT NULL,
    created_at DATETIME NOT NULL,
    expires_at DATETIME NOT NULL,
    consumed_at DATETIME NULL,
    CONSTRAINT fk_atlas_password_reset_profile FOREIGN KEY (account_id)
        REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
