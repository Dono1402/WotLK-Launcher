ALTER TABLE atlas_launcher_session
    DROP FOREIGN KEY fk_atlas_session_account,
    ADD CONSTRAINT fk_atlas_session_account
        FOREIGN KEY (account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE;

ALTER TABLE atlas_launcher_email_verification
    DROP FOREIGN KEY fk_atlas_email_account,
    ADD CONSTRAINT fk_atlas_email_account
        FOREIGN KEY (account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE;

ALTER TABLE atlas_launcher_friendship
    DROP FOREIGN KEY fk_atlas_friend_low,
    DROP FOREIGN KEY fk_atlas_friend_high,
    DROP FOREIGN KEY fk_atlas_friend_requester,
    ADD CONSTRAINT fk_atlas_friend_low
        FOREIGN KEY (account_low_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE,
    ADD CONSTRAINT fk_atlas_friend_high
        FOREIGN KEY (account_high_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE,
    ADD CONSTRAINT fk_atlas_friend_requester
        FOREIGN KEY (requested_by_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE;

ALTER TABLE atlas_launcher_avatar_asset
    DROP FOREIGN KEY fk_atlas_avatar_owner,
    ADD CONSTRAINT fk_atlas_avatar_owner
        FOREIGN KEY (owner_account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE;

ALTER TABLE atlas_launcher_avatar_upload_attempt
    DROP FOREIGN KEY fk_atlas_avatar_upload_account,
    ADD CONSTRAINT fk_atlas_avatar_upload_account
        FOREIGN KEY (account_id) REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE;
