-- Activation is separate from migration. AtlasShop:ManualPayPal:Enabled defaults to false.
-- Financial records intentionally prevent deletion of a referenced Atlas profile.
CREATE TABLE IF NOT EXISTS atlas_shop_wallet (
    account_id INT UNSIGNED NOT NULL PRIMARY KEY,
    euro_cents BIGINT NOT NULL DEFAULT 0,
    credit_cents BIGINT NOT NULL DEFAULT 0,
    held_cents BIGINT NOT NULL DEFAULT 0,
    debt_cents BIGINT NOT NULL DEFAULT 0,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_atlas_shop_wallet_profile FOREIGN KEY (account_id) REFERENCES atlas_launcher_profile(account_id),
    CONSTRAINT chk_atlas_shop_wallet_amounts CHECK (euro_cents BETWEEN 0 AND 1000000000 AND credit_cents BETWEEN 0 AND 1000000000
        AND held_cents BETWEEN 0 AND euro_cents AND debt_cents BETWEEN 0 AND 1000000000)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS atlas_shop_top_up (
    sequence_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    account_id INT UNSIGNED NOT NULL,
    idempotency_key CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    amount_cents BIGINT NOT NULL,
    status VARCHAR(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'pending',
    version BIGINT NOT NULL DEFAULT 1,
    paypal_transaction_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
    paypal_case_id VARCHAR(100) NULL,
    held_cents BIGINT NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_atlas_shop_top_up_id (id),
    UNIQUE KEY uq_atlas_shop_top_up_key (account_id,idempotency_key),
    UNIQUE KEY uq_atlas_shop_paypal_transaction (paypal_transaction_id),
    KEY ix_atlas_shop_top_up_account (account_id,sequence_id),
    KEY ix_atlas_shop_top_up_status (status,sequence_id),
    KEY ix_atlas_shop_top_up_daily (account_id,created_at),
    CONSTRAINT fk_atlas_shop_top_up_wallet FOREIGN KEY (account_id) REFERENCES atlas_shop_wallet(account_id),
    CONSTRAINT chk_atlas_shop_top_up_amount CHECK (amount_cents BETWEEN 100 AND 1000000000),
    CONSTRAINT chk_atlas_shop_top_up_state CHECK (status IN ('pending','credited','cancelled','disputed','refunded') AND version > 0
        AND held_cents BETWEEN 0 AND amount_cents AND (held_cents = 0 OR status = 'disputed')
        AND (status IN ('pending','cancelled') OR paypal_transaction_id IS NOT NULL))
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- Append-only through the application; balances and events commit in the same transaction.
CREATE TABLE IF NOT EXISTS atlas_shop_ledger (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    request_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    request_version BIGINT NOT NULL,
    account_id INT UNSIGNED NOT NULL,
    actor_account_id INT UNSIGNED NOT NULL,
    action VARCHAR(20) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    amount_cents BIGINT NOT NULL DEFAULT 0,
    euro_delta BIGINT NOT NULL DEFAULT 0,
    held_delta BIGINT NOT NULL DEFAULT 0,
    debt_delta BIGINT NOT NULL DEFAULT 0,
    euro_after BIGINT NOT NULL,
    held_after BIGINT NOT NULL,
    debt_after BIGINT NOT NULL,
    note VARCHAR(1000) NOT NULL,
    paypal_case_id VARCHAR(100) NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_atlas_shop_ledger_transition (request_id,request_version),
    KEY ix_atlas_shop_ledger_account (account_id,id),
    CONSTRAINT fk_atlas_shop_ledger_request FOREIGN KEY (request_id) REFERENCES atlas_shop_top_up(id),
    CONSTRAINT fk_atlas_shop_ledger_wallet FOREIGN KEY (account_id) REFERENCES atlas_shop_wallet(account_id),
    CONSTRAINT fk_atlas_shop_ledger_actor FOREIGN KEY (actor_account_id) REFERENCES atlas_launcher_profile(account_id),
    CONSTRAINT chk_atlas_shop_ledger_state CHECK (action IN ('create','approve','cancel','dispute','resolve-won','refund-confirmed')
        AND request_version > 0 AND euro_after BETWEEN 0 AND 1000000000 AND held_after BETWEEN 0 AND euro_after
        AND debt_after BETWEEN 0 AND 1000000000)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
