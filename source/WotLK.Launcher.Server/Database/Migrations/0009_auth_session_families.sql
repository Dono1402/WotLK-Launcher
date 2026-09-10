ALTER TABLE atlas_launcher_session
    ADD COLUMN absolute_expires_at DATETIME NULL AFTER refresh_expires_at;

-- Existing refresh tokens keep their current deadline. The migration must never
-- turn an already issued session into a new rolling 30-day session.
UPDATE atlas_launcher_session
SET absolute_expires_at = refresh_expires_at
WHERE absolute_expires_at IS NULL;

ALTER TABLE atlas_launcher_session
    MODIFY absolute_expires_at DATETIME NOT NULL,
    ADD INDEX ix_atlas_session_absolute_expiry (absolute_expires_at);

CREATE TABLE atlas_launcher_refresh_history (
    token_hash BINARY(32) NOT NULL PRIMARY KEY,
    session_id BINARY(16) NOT NULL,
    expires_at DATETIME NOT NULL,
    consumed_at DATETIME(6) NOT NULL,
    INDEX ix_atlas_refresh_history_session (session_id),
    INDEX ix_atlas_refresh_history_expiry (expires_at),
    CONSTRAINT fk_atlas_refresh_history_session
        FOREIGN KEY (session_id) REFERENCES atlas_launcher_session(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
