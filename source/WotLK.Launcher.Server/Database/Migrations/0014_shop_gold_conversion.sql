-- Only the realm worker changes character money. Enqueueing never grants credits.
CREATE TABLE IF NOT EXISTS atlas_shop_gold_conversion (
    sequence_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    account_id INT UNSIGNED NOT NULL,
    realm_id INT UNSIGNED NOT NULL,
    character_guid INT UNSIGNED NOT NULL,
    character_name VARCHAR(24) NOT NULL,
    idempotency_key CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    catalog_revision VARCHAR(80) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    offered_copper INT UNSIGNED NOT NULL,
    copper_per_cent INT UNSIGNED NOT NULL,
    credit_cents BIGINT NOT NULL,
    status VARCHAR(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'pending',
    reason VARCHAR(40) CHARACTER SET ascii COLLATE ascii_bin NULL,
    gold_before INT UNSIGNED NULL,
    gold_after INT UNSIGNED NULL,
    credit_before BIGINT NULL,
    credit_after BIGINT NULL,
    active_account INT UNSIGNED GENERATED ALWAYS AS (IF(status='pending',account_id,NULL)) STORED,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_atlas_shop_conversion_id (id),
    UNIQUE KEY uq_atlas_shop_conversion_key (account_id,idempotency_key),
    UNIQUE KEY uq_atlas_shop_conversion_active (active_account),
    KEY ix_atlas_shop_conversion_worker (realm_id,status,sequence_id),
    KEY ix_atlas_shop_conversion_account (account_id,sequence_id),
    KEY ix_atlas_shop_conversion_daily (account_id,created_at),
    CONSTRAINT fk_atlas_shop_conversion_wallet FOREIGN KEY (account_id) REFERENCES atlas_shop_wallet(account_id),
    CONSTRAINT chk_atlas_shop_conversion_quote CHECK (realm_id>0 AND character_guid>0 AND offered_copper>0
        AND MOD(offered_copper,10000)=0 AND copper_per_cent>0 AND MOD(offered_copper,copper_per_cent)=0
        AND credit_cents=offered_copper DIV copper_per_cent AND credit_cents BETWEEN 1 AND 1000000000),
    CONSTRAINT chk_atlas_shop_conversion_receipt CHECK (
        (status IN ('pending','rejected') AND gold_before IS NULL AND gold_after IS NULL
            AND credit_before IS NULL AND credit_after IS NULL)
        OR (status='completed' AND gold_before IS NOT NULL AND gold_after IS NOT NULL
            AND credit_before IS NOT NULL AND credit_after IS NOT NULL
            AND gold_before>=offered_copper AND gold_after=gold_before-offered_copper
            AND credit_before BETWEEN 0 AND 1000000000 AND credit_after BETWEEN 0 AND 1000000000
            AND credit_after=credit_before+credit_cents)),
    CONSTRAINT chk_atlas_shop_conversion_reason CHECK (
        (status='rejected' AND reason IS NOT NULL) OR (status IN ('pending','completed') AND reason IS NULL))
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_shop_conversion_health (
    realm_id INT UNSIGNED NOT NULL PRIMARY KEY,
    protocol INT UNSIGNED NOT NULL,
    character_database VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    copper_per_cent INT UNSIGNED NOT NULL,
    last_seen_at DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
