-- Additive chat workspace. The version 6 tables remain the game/v1 projection.
-- All v2 writers lock the singleton BEFORE allocating message/event identifiers.
CREATE TABLE atlas_launcher_chat_v2_sequence (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    revision BIGINT UNSIGNED NOT NULL DEFAULT 0,
    CONSTRAINT chk_chat_v2_sequence CHECK (id=1)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
INSERT INTO atlas_launcher_chat_v2_sequence(id) VALUES(1);

CREATE TABLE atlas_launcher_chat_v2_thread (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    kind TINYINT UNSIGNED NOT NULL,
    account_low_id INT UNSIGNED NULL,
    account_high_id INT UNSIGNED NULL,
    owner_account_id INT UNSIGNED NOT NULL,
    created_by_account_id INT UNSIGNED NOT NULL,
    title VARCHAR(120) NOT NULL DEFAULT '',
    avatar_json JSON NULL,
    request_id BINARY(16) NULL,
    request_hash BINARY(32) NULL,
    last_message_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
    version BIGINT UNSIGNED NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_chat_v2_direct(account_low_id,account_high_id),
    UNIQUE KEY uq_chat_v2_thread_request(created_by_account_id,request_id),
    CONSTRAINT fk_chat_v2_thread_owner FOREIGN KEY(owner_account_id) REFERENCES atlas_launcher_profile(account_id),
    CONSTRAINT chk_chat_v2_thread_kind CHECK ((kind=0 AND account_low_id IS NOT NULL AND account_high_id IS NOT NULL AND account_low_id<account_high_id) OR (kind=1 AND account_low_id IS NULL AND account_high_id IS NULL))
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_v2_member (
    thread_id BIGINT UNSIGNED NOT NULL,
    account_id INT UNSIGNED NOT NULL,
    role VARCHAR(8) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'member',
    status TINYINT UNSIGNED NOT NULL DEFAULT 1,
    joined_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    history_after_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
    last_read_message_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
    is_pinned BOOLEAN NOT NULL DEFAULT FALSE,
    is_archived BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY(thread_id,account_id),
    INDEX ix_chat_v2_member_account(account_id,status,thread_id),
    CONSTRAINT fk_chat_v2_member_thread FOREIGN KEY(thread_id) REFERENCES atlas_launcher_chat_v2_thread(id) ON DELETE CASCADE,
    CONSTRAINT fk_chat_v2_member_profile FOREIGN KEY(account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE,
    CONSTRAINT chk_chat_v2_member_status CHECK(status IN(0,1,2)),
    CONSTRAINT chk_chat_v2_member_role CHECK(role IN('owner','admin','member'))
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_v2_request (
    account_id INT UNSIGNED NOT NULL,
    request_id BINARY(16) NOT NULL,
    request_hash BINARY(32) NOT NULL,
    thread_id BIGINT UNSIGNED NOT NULL,
    PRIMARY KEY(account_id,request_id),
    CONSTRAINT fk_chat_v2_request_profile FOREIGN KEY(account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE,
    CONSTRAINT fk_chat_v2_request_thread FOREIGN KEY(thread_id) REFERENCES atlas_launcher_chat_v2_thread(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_v2_message (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    thread_id BIGINT UNSIGNED NOT NULL,
    sender_account_id INT UNSIGNED NOT NULL,
    client_message_id BINARY(16) NOT NULL,
    request_hash BINARY(32) NOT NULL,
    legacy_request_hash BINARY(32) NULL,
    legacy_message_id BIGINT UNSIGNED NULL,
    sender_username VARCHAR(32) NOT NULL,
    sender_character_name VARCHAR(12) NULL,
    body VARCHAR(1000) NOT NULL,
    origin TINYINT UNSIGNED NOT NULL DEFAULT 0,
    reply_to_message_id BIGINT UNSIGNED NULL,
    attachments_json JSON NOT NULL,
    previews_json JSON NOT NULL,
    card_json JSON NULL,
    is_pinned BOOLEAN NOT NULL DEFAULT FALSE,
    version BIGINT UNSIGNED NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    edited_at DATETIME(6) NULL,
    deleted_at DATETIME(6) NULL,
    UNIQUE KEY uq_chat_v2_sender_request(sender_account_id,client_message_id),
    UNIQUE KEY uq_chat_v2_legacy(legacy_message_id),
    INDEX ix_chat_v2_message_thread(thread_id,id),
    CONSTRAINT fk_chat_v2_message_thread FOREIGN KEY(thread_id) REFERENCES atlas_launcher_chat_v2_thread(id) ON DELETE CASCADE,
    CONSTRAINT fk_chat_v2_message_sender FOREIGN KEY(sender_account_id) REFERENCES atlas_launcher_profile(account_id),
    CONSTRAINT chk_chat_v2_message_origin CHECK(origin IN(0,1)),
    CONSTRAINT chk_chat_v2_message_body CHECK(CHAR_LENGTH(body)<=1000 AND OCTET_LENGTH(body)<=4000)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_v2_reaction (
    message_id BIGINT UNSIGNED NOT NULL,
    account_id INT UNSIGNED NOT NULL,
    emoji VARCHAR(32) COLLATE utf8mb4_bin NOT NULL,
    PRIMARY KEY(message_id,account_id,emoji),
    CONSTRAINT fk_chat_v2_reaction_message FOREIGN KEY(message_id) REFERENCES atlas_launcher_chat_v2_message(id) ON DELETE CASCADE,
    CONSTRAINT fk_chat_v2_reaction_profile FOREIGN KEY(account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_v2_preferences (
    account_id INT UNSIGNED NOT NULL PRIMARY KEY,
    do_not_disturb BOOLEAN NOT NULL DEFAULT FALSE,
    share_read_receipts BOOLEAN NOT NULL DEFAULT TRUE,
    share_typing BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT fk_chat_v2_preferences_profile FOREIGN KEY(account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE atlas_launcher_chat_v2_event (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    account_id INT UNSIGNED NOT NULL,
    thread_id BIGINT UNSIGNED NULL,
    message_id BIGINT UNSIGNED NULL,
    kind VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    payload_json JSON NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX ix_chat_v2_event_account(account_id,id),
    CONSTRAINT fk_chat_v2_event_profile FOREIGN KEY(account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- Preserve identifiers for the initial history. Future legacy IDs have an explicit mapping.
INSERT INTO atlas_launcher_chat_v2_thread(kind,account_low_id,account_high_id,owner_account_id,created_by_account_id,updated_at)
SELECT 0,account_low_id,account_high_id,account_low_id,account_low_id,updated_at FROM atlas_launcher_chat_conversation ORDER BY account_low_id,account_high_id;
INSERT INTO atlas_launcher_chat_v2_member(thread_id,account_id,last_read_message_id)
SELECT t.id,c.account_low_id,c.low_last_read_message_id FROM atlas_launcher_chat_conversation c
JOIN atlas_launcher_chat_v2_thread t ON t.account_low_id=c.account_low_id AND t.account_high_id=c.account_high_id;
INSERT INTO atlas_launcher_chat_v2_member(thread_id,account_id,last_read_message_id)
SELECT t.id,c.account_high_id,c.high_last_read_message_id FROM atlas_launcher_chat_conversation c
JOIN atlas_launcher_chat_v2_thread t ON t.account_low_id=c.account_low_id AND t.account_high_id=c.account_high_id;
INSERT INTO atlas_launcher_chat_v2_message(id,thread_id,sender_account_id,client_message_id,request_hash,legacy_request_hash,legacy_message_id,sender_username,body,origin,attachments_json,previews_json,created_at)
SELECT m.id,t.id,m.sender_account_id,m.client_message_id,UNHEX(SHA2(CONCAT(m.recipient_account_id,':',m.origin,':',m.body),256)),UNHEX(SHA2(CONCAT(m.recipient_account_id,':',m.origin,':',m.body),256)),m.id,m.sender_username,m.body,m.origin,JSON_ARRAY(),JSON_ARRAY(),m.created_at
FROM atlas_launcher_chat_message m JOIN atlas_launcher_chat_v2_thread t ON t.account_low_id=m.account_low_id AND t.account_high_id=m.account_high_id ORDER BY m.id;
UPDATE atlas_launcher_chat_v2_thread t JOIN atlas_launcher_chat_conversation c ON t.account_low_id=c.account_low_id AND t.account_high_id=c.account_high_id SET t.last_message_id=c.last_message_id;
