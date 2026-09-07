CREATE TABLE atlas_launcher_chat_account (
    account_id INT UNSIGNED NOT NULL PRIMARY KEY,
    last_message_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
    send_window_started_at DATETIME(6) NULL,
    send_window_count SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    CONSTRAINT fk_atlas_chat_account_profile FOREIGN KEY (account_id)
        REFERENCES atlas_launcher_profile (account_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_conversation (
    account_low_id INT UNSIGNED NOT NULL,
    account_high_id INT UNSIGNED NOT NULL,
    last_message_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
    low_last_read_message_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
    high_last_read_message_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (account_low_id, account_high_id),
    INDEX ix_atlas_chat_conversation_high (account_high_id, last_message_id),
    INDEX ix_atlas_chat_conversation_low (account_low_id, last_message_id),
    CONSTRAINT fk_atlas_chat_conversation_low FOREIGN KEY (account_low_id)
        REFERENCES atlas_launcher_profile (account_id) ON DELETE CASCADE,
    CONSTRAINT fk_atlas_chat_conversation_high FOREIGN KEY (account_high_id)
        REFERENCES atlas_launcher_profile (account_id) ON DELETE CASCADE,
    CONSTRAINT chk_atlas_chat_conversation_pair CHECK (account_low_id < account_high_id)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_message (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    client_message_id BINARY(16) NOT NULL,
    account_low_id INT UNSIGNED NOT NULL,
    account_high_id INT UNSIGNED NOT NULL,
    sender_account_id INT UNSIGNED NOT NULL,
    recipient_account_id INT UNSIGNED NOT NULL,
    sender_username VARCHAR(32) NOT NULL,
    body VARCHAR(1000) NOT NULL,
    origin TINYINT UNSIGNED NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_atlas_chat_sender_request (sender_account_id, client_message_id),
    UNIQUE KEY uq_atlas_chat_message_recipient (id, recipient_account_id),
    INDEX ix_atlas_chat_message_conversation (account_low_id, account_high_id, id),
    INDEX ix_atlas_chat_message_recipient (recipient_account_id, id),
    INDEX ix_atlas_chat_message_sender (sender_account_id, id),
    CONSTRAINT fk_atlas_chat_message_conversation FOREIGN KEY (account_low_id, account_high_id)
        REFERENCES atlas_launcher_chat_conversation (account_low_id, account_high_id) ON DELETE CASCADE,
    CONSTRAINT fk_atlas_chat_message_sender FOREIGN KEY (sender_account_id)
        REFERENCES atlas_launcher_profile (account_id) ON DELETE CASCADE,
    CONSTRAINT fk_atlas_chat_message_recipient FOREIGN KEY (recipient_account_id)
        REFERENCES atlas_launcher_profile (account_id) ON DELETE CASCADE,
    CONSTRAINT chk_atlas_chat_message_pair CHECK (
        (sender_account_id = account_low_id AND recipient_account_id = account_high_id)
        OR (sender_account_id = account_high_id AND recipient_account_id = account_low_id)),
    CONSTRAINT chk_atlas_chat_message_origin CHECK (origin IN (0, 1)),
    CONSTRAINT chk_atlas_chat_message_body CHECK (CHAR_LENGTH(body) BETWEEN 1 AND 1000 AND OCTET_LENGTH(body) <= 4000)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_outbox (
    message_id BIGINT UNSIGNED NOT NULL PRIMARY KEY,
    recipient_account_id INT UNSIGNED NOT NULL,
    realm_id INT UNSIGNED NOT NULL DEFAULT 0,
    status TINYINT UNSIGNED NOT NULL DEFAULT 0,
    lease_token BINARY(16) NULL,
    lease_until DATETIME(6) NULL,
    attempts SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    last_error VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    expires_at DATETIME(6) NOT NULL,
    delivered_at DATETIME(6) NULL,
    delivered_character_guid INT UNSIGNED NULL,
    INDEX ix_atlas_chat_outbox_pending (status, realm_id, message_id),
    INDEX ix_atlas_chat_outbox_lease (lease_token),
    INDEX ix_atlas_chat_outbox_recipient (message_id, recipient_account_id),
    CONSTRAINT fk_atlas_chat_outbox_message FOREIGN KEY (message_id, recipient_account_id)
        REFERENCES atlas_launcher_chat_message (id, recipient_account_id) ON DELETE CASCADE,
    CONSTRAINT chk_atlas_chat_outbox_status CHECK (status IN (0, 1, 2, 3, 4))
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_inbox (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    request_id BINARY(16) NOT NULL,
    realm_id INT UNSIGNED NOT NULL,
    sender_account_id INT UNSIGNED NOT NULL,
    sender_character_guid INT UNSIGNED NOT NULL,
    recipient_account_id INT UNSIGNED NOT NULL,
    body VARCHAR(1000) NOT NULL,
    status TINYINT UNSIGNED NOT NULL DEFAULT 0,
    message_id BIGINT UNSIGNED NULL,
    error_code VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    processed_at DATETIME(6) NULL,
    UNIQUE KEY uq_atlas_chat_inbox_request (request_id),
    INDEX ix_atlas_chat_inbox_pending (status, id),
    INDEX ix_atlas_chat_inbox_sender (sender_account_id, id),
    INDEX ix_atlas_chat_inbox_recipient (recipient_account_id),
    CONSTRAINT fk_atlas_chat_inbox_sender FOREIGN KEY (sender_account_id)
        REFERENCES atlas_launcher_profile (account_id) ON DELETE CASCADE,
    CONSTRAINT fk_atlas_chat_inbox_recipient FOREIGN KEY (recipient_account_id)
        REFERENCES atlas_launcher_profile (account_id) ON DELETE CASCADE,
    CONSTRAINT chk_atlas_chat_inbox_status CHECK (status IN (0, 1, 2)),
    CONSTRAINT chk_atlas_chat_inbox_body CHECK (CHAR_LENGTH(body) BETWEEN 1 AND 1000 AND OCTET_LENGTH(body) <= 4000),
    CONSTRAINT chk_atlas_chat_inbox_identity CHECK (realm_id > 0 AND sender_character_guid > 0 AND sender_account_id <> recipient_account_id)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
