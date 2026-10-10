-- 0006: assets and liabilities, dated valuations, ownership shares over time, and the wishlist (spec §9, §14, §17).
-- Valuations are kept per date, so a new value never overwrites the only one. Net worth and affordability are
-- calculated from these tables and the ledger; nothing here stores a derived total.

CREATE TABLE assets (
    id                  TEXT    NOT NULL PRIMARY KEY,
    name                TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    kind                TEXT    NOT NULL CHECK (kind IN ('Property', 'Vehicle', 'OtherAsset', 'Mortgage', 'Loan', 'OtherLiability')),
    ownership_share_bp  INTEGER NOT NULL CHECK (ownership_share_bp BETWEEN 1 AND 10000),
    status              TEXT    NOT NULL CHECK (status IN ('Active', 'Archived')),
    notes               TEXT    NULL,
    sort_order          INTEGER NOT NULL DEFAULT 0,
    created_at          TEXT    NOT NULL,
    updated_at          TEXT    NOT NULL
);

-- A value on a date for a pension/investment account or for an asset: exactly one of the two.
CREATE TABLE valuations (
    id           TEXT    NOT NULL PRIMARY KEY,
    account_id   TEXT    NULL REFERENCES accounts (id),
    asset_id     TEXT    NULL REFERENCES assets (id),
    date         TEXT    NOT NULL,
    value_cents  INTEGER NOT NULL CHECK (value_cents >= 0),
    note         TEXT    NULL CHECK (note IS NULL OR length(note) <= 500),
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL,
    CHECK ((account_id IS NULL) <> (asset_id IS NULL))
);

CREATE UNIQUE INDEX ix_valuations_account_date ON valuations (account_id, date) WHERE account_id IS NOT NULL;
CREATE UNIQUE INDEX ix_valuations_asset_date ON valuations (asset_id, date) WHERE asset_id IS NOT NULL;

-- The user's share of an account or asset from a date on.
CREATE TABLE ownership_changes (
    id              TEXT    NOT NULL PRIMARY KEY,
    account_id      TEXT    NULL REFERENCES accounts (id),
    asset_id        TEXT    NULL REFERENCES assets (id),
    effective_from  TEXT    NOT NULL,
    share_bp        INTEGER NOT NULL CHECK (share_bp BETWEEN 1 AND 10000),
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL,
    CHECK ((account_id IS NULL) <> (asset_id IS NULL))
);

CREATE UNIQUE INDEX ix_ownership_changes_account ON ownership_changes (account_id, effective_from) WHERE account_id IS NOT NULL;
CREATE UNIQUE INDEX ix_ownership_changes_asset ON ownership_changes (asset_id, effective_from) WHERE asset_id IS NOT NULL;

CREATE TABLE wishlist_items (
    id                       TEXT    NOT NULL PRIMARY KEY,
    name                     TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    expected_price_cents     INTEGER NOT NULL CHECK (expected_price_cents > 0),
    url                      TEXT    NULL CHECK (url IS NULL OR length(url) <= 500),
    note                     TEXT    NULL,
    priority                 TEXT    NOT NULL CHECK (priority IN ('High', 'Normal', 'Low')),
    target_date              TEXT    NULL,
    monthly_saving_cents     INTEGER NULL CHECK (monthly_saving_cents IS NULL OR monthly_saving_cents > 0),
    alternative              TEXT    NULL,
    status                   TEXT    NOT NULL CHECK (status IN ('Desired', 'Saving', 'Postponed', 'Purchased', 'Cancelled')),
    purchased_on             TEXT    NULL,
    purchase_transaction_id  TEXT    NULL REFERENCES transactions (id),
    created_at               TEXT    NOT NULL,
    updated_at               TEXT    NOT NULL,
    CHECK ((status = 'Purchased') = (purchased_on IS NOT NULL)),
    CHECK (purchase_transaction_id IS NULL OR status = 'Purchased')
);

-- One expense can be the purchase of only one item, so buying never records the same thing twice.
CREATE UNIQUE INDEX ix_wishlist_items_transaction ON wishlist_items (purchase_transaction_id) WHERE purchase_transaction_id IS NOT NULL;
