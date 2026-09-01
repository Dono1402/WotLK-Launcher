CREATE TABLE IF NOT EXISTS atlas_launcher_profile (
    account_id INT UNSIGNED NOT NULL PRIMARY KEY,
    display_username VARCHAR(32) NOT NULL,
    email_normalized VARCHAR(254) NOT NULL UNIQUE,
    email_verified_at DATETIME NULL,
    avatar_key VARCHAR(128) NULL,
    two_factor_enabled TINYINT(1) NOT NULL DEFAULT 0,
    recovery_codes_generated TINYINT(1) NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_atlas_profile_account
        FOREIGN KEY (account_id) REFERENCES account(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_launcher_session (
    id BINARY(16) NOT NULL PRIMARY KEY,
    account_id INT UNSIGNED NOT NULL,
    access_hash BINARY(32) NOT NULL UNIQUE,
    refresh_hash BINARY(32) NOT NULL UNIQUE,
    device_name VARCHAR(128) NULL,
    access_expires_at DATETIME NOT NULL,
    refresh_expires_at DATETIME NOT NULL,
    revoked_at DATETIME NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX ix_atlas_session_account (account_id),
    INDEX ix_atlas_session_refresh_expiry (refresh_expires_at),
    CONSTRAINT fk_atlas_session_account
        FOREIGN KEY (account_id) REFERENCES account(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_launcher_email_verification (
    id BINARY(16) NOT NULL PRIMARY KEY,
    account_id INT UNSIGNED NOT NULL,
    email_normalized VARCHAR(254) NOT NULL,
    token_hash BINARY(32) NOT NULL UNIQUE,
    expires_at DATETIME NOT NULL,
    consumed_at DATETIME NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX ix_atlas_email_account_created (account_id, created_at),
    INDEX ix_atlas_email_expiry (expires_at),
    CONSTRAINT fk_atlas_email_account
        FOREIGN KEY (account_id) REFERENCES account(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_launcher_friendship (
    account_low_id INT UNSIGNED NOT NULL,
    account_high_id INT UNSIGNED NOT NULL,
    requested_by_id INT UNSIGNED NOT NULL,
    accepted_at DATETIME NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (account_low_id, account_high_id),
    INDEX ix_atlas_friend_requested_by (requested_by_id),
    INDEX fk_atlas_friend_high (account_high_id),
    CONSTRAINT fk_atlas_friend_low
        FOREIGN KEY (account_low_id) REFERENCES account(id) ON DELETE CASCADE,
    CONSTRAINT fk_atlas_friend_high
        FOREIGN KEY (account_high_id) REFERENCES account(id) ON DELETE CASCADE,
    CONSTRAINT fk_atlas_friend_requester
        FOREIGN KEY (requested_by_id) REFERENCES account(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
