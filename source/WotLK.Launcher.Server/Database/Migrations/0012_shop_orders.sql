-- Separate activation: both API RenameEnabled and the realm module default to false.
CREATE TABLE IF NOT EXISTS atlas_shop_order (
    sequence_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    account_id INT UNSIGNED NOT NULL,
    realm_id INT UNSIGNED NOT NULL,
    character_guid INT UNSIGNED NOT NULL,
    character_name VARCHAR(24) NOT NULL,
    idempotency_key CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    offer_id VARCHAR(40) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    catalog_revision VARCHAR(80) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    currency VARCHAR(7) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    amount_cents BIGINT NOT NULL,
    status VARCHAR(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'pending',
    reason VARCHAR(40) CHARACTER SET ascii COLLATE ascii_bin NULL,
    active_character INT UNSIGNED GENERATED ALWAYS AS (IF(status='pending',character_guid,NULL)) STORED,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_atlas_shop_order_id (id),
    UNIQUE KEY uq_atlas_shop_order_key (account_id,idempotency_key),
    UNIQUE KEY uq_atlas_shop_order_active (realm_id,active_character),
    KEY ix_atlas_shop_order_worker (realm_id,status,sequence_id),
    KEY ix_atlas_shop_order_account (account_id,sequence_id),
    CONSTRAINT fk_atlas_shop_order_wallet FOREIGN KEY (account_id) REFERENCES atlas_shop_wallet(account_id),
    CONSTRAINT chk_atlas_shop_order_state CHECK (offer_id='character-rename' AND character_guid>0 AND realm_id>0
        AND currency IN ('eur','credits') AND amount_cents BETWEEN 1 AND 1000000000
        AND status IN ('pending','delivered','rejected','refunded')
        AND (reason IS NULL OR reason IN ('cancelled','character-unavailable','rename-already-pending')))
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_shop_order_ledger (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    order_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    account_id INT UNSIGNED NOT NULL,
    kind VARCHAR(8) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    currency VARCHAR(7) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    amount_cents BIGINT NOT NULL,
    euro_after BIGINT NOT NULL,
    credit_after BIGINT NOT NULL,
    held_after BIGINT NOT NULL,
    debt_after BIGINT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_atlas_shop_order_event (order_id,kind),
    KEY ix_atlas_shop_order_ledger_account (account_id,id),
    CONSTRAINT fk_atlas_shop_order_event_order FOREIGN KEY (order_id) REFERENCES atlas_shop_order(id),
    CONSTRAINT fk_atlas_shop_order_event_wallet FOREIGN KEY (account_id) REFERENCES atlas_shop_wallet(account_id),
    CONSTRAINT chk_atlas_shop_order_event CHECK (currency IN ('eur','credits')
        AND ((kind='purchase' AND amount_cents<0) OR (kind='refund' AND amount_cents>0))
        AND euro_after BETWEEN 0 AND 1000000000 AND credit_after BETWEEN 0 AND 1000000000
        AND held_after BETWEEN 0 AND euro_after AND debt_after BETWEEN 0 AND 1000000000)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_shop_delivery_health (
    realm_id INT UNSIGNED NOT NULL PRIMARY KEY,
    protocol INT UNSIGNED NOT NULL,
    character_database VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    last_seen_at DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
