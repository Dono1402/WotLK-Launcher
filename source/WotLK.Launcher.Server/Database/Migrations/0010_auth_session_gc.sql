ALTER TABLE atlas_launcher_session
    ADD INDEX ix_atlas_session_revoked_gc (revoked_at, id),
    ADD INDEX ix_atlas_session_expired_gc (revoked_at, absolute_expires_at, id),
    ADD INDEX ix_atlas_session_account_revoked (account_id, revoked_at, id),
    ADD INDEX ix_atlas_session_account_active (account_id, revoked_at, refresh_expires_at, id),
    ADD INDEX ix_atlas_session_account_active_order (account_id, revoked_at, created_at, id);
