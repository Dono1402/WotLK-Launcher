-- One atomic ALTER: existing character-bound purchases keep their history.
-- New purchases remain available to the account until a native in-game choice.
ALTER TABLE atlas_shop_order
    ADD COLUMN redemption_key CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    ADD COLUMN requested_name VARCHAR(12) NULL,
    ADD UNIQUE KEY uq_atlas_shop_order_redemption (account_id,redemption_key),
    DROP CHECK chk_atlas_shop_order_state,
    ADD CONSTRAINT chk_atlas_shop_order_state CHECK (
        offer_id='character-rename' AND realm_id>0
        AND currency IN ('eur','credits') AND amount_cents BETWEEN 1 AND 1000000000
        AND status IN ('available','pending','delivered','consumed','rejected','refunded')
        AND (reason IS NULL OR reason IN ('cancelled','character-unavailable','rename-already-pending'))
        AND ((character_guid=0 AND character_name='' AND status IN ('available','refunded'))
            OR (character_guid>0 AND character_name<>'' AND status IN ('pending','delivered','consumed','rejected','refunded')))
        AND ((status='consumed' AND redemption_key IS NOT NULL AND requested_name IS NOT NULL AND requested_name<>'')
            OR (status<>'consumed' AND redemption_key IS NULL AND requested_name IS NULL))
    );
