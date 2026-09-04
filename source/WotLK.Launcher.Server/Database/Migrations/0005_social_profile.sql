ALTER TABLE atlas_launcher_profile
    ADD COLUMN status_message VARCHAR(80) NULL AFTER avatar_key,
    ADD COLUMN bio VARCHAR(280) NULL AFTER status_message;
