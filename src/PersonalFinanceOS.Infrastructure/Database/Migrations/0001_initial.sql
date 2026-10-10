-- 0001: accounts, settings, audit log.
-- Money is stored as INTEGER cents. Shares are INTEGER basis points (5000 = 50%).
-- Dates are ISO text (yyyy-MM-dd); timestamps are UTC ISO-8601 text.

CREATE TABLE accounts (
    id                      TEXT    NOT NULL PRIMARY KEY,
    name                    TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    type                    TEXT    NOT NULL,
    currency                TEXT    NOT NULL DEFAULT 'EUR' CHECK (currency = 'EUR'),
    ownership_share_bp      INTEGER NOT NULL CHECK (ownership_share_bp BETWEEN 1 AND 10000),
    opening_date            TEXT    NOT NULL,
    opening_balance_cents   INTEGER NOT NULL,
    status                  TEXT    NOT NULL,
    notes                   TEXT    NULL,
    sort_order              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT    NOT NULL,
    updated_at              TEXT    NOT NULL
);

CREATE INDEX ix_accounts_status_sort ON accounts (status, sort_order);

CREATE TABLE app_settings (
    key         TEXT NOT NULL PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

CREATE TABLE audit_log (
    id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    occurred_at TEXT    NOT NULL,
    entity      TEXT    NOT NULL,
    entity_id   TEXT    NULL,
    action      TEXT    NOT NULL
);

CREATE INDEX ix_audit_log_entity ON audit_log (entity, entity_id);
