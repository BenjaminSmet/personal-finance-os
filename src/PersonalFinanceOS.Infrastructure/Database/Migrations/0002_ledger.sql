-- 0002: the ledger — categories, transactions (incl. linked transfers), learned payee rules, reconciliations.
-- Amounts are INTEGER cents, signed from the account's point of view (money out < 0, money in > 0).
-- Voided transactions are kept (status = 'Voided') so they can be undone and audited; they never count.

CREATE TABLE categories (
    id          TEXT    NOT NULL PRIMARY KEY,
    name        TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 60),
    kind        TEXT    NOT NULL CHECK (kind IN ('Expense', 'Income')),
    sort_order  INTEGER NOT NULL DEFAULT 0,
    status      TEXT    NOT NULL,
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
);

CREATE INDEX ix_categories_status_sort ON categories (status, sort_order);

CREATE TABLE transactions (
    id            TEXT    NOT NULL PRIMARY KEY,
    account_id    TEXT    NOT NULL REFERENCES accounts (id),
    date          TEXT    NOT NULL,
    amount_cents  INTEGER NOT NULL CHECK (amount_cents <> 0),
    type          TEXT    NOT NULL CHECK (type IN ('Income', 'Expense', 'Refund', 'Transfer', 'Adjustment')),
    description   TEXT    NOT NULL DEFAULT '' CHECK (length(description) <= 200),
    category_id   TEXT    NULL REFERENCES categories (id),
    note          TEXT    NULL,
    source        TEXT    NOT NULL,
    transfer_id   TEXT    NULL,
    status        TEXT    NOT NULL CHECK (status IN ('Active', 'Voided')),
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL,
    -- Direction is part of the meaning: an expense can never add money, income never removes it.
    CHECK (type <> 'Expense' OR amount_cents < 0),
    CHECK (type NOT IN ('Income', 'Refund') OR amount_cents > 0),
    -- Both sides of a transfer share a transfer_id; nothing else has one. Transfers carry no category.
    CHECK ((type = 'Transfer') = (transfer_id IS NOT NULL)),
    CHECK (type IN ('Income', 'Expense', 'Refund') OR category_id IS NULL)
);

CREATE INDEX ix_transactions_account_date ON transactions (account_id, date);
CREATE INDEX ix_transactions_date ON transactions (date);
CREATE INDEX ix_transactions_transfer ON transactions (transfer_id) WHERE transfer_id IS NOT NULL;
CREATE INDEX ix_transactions_category ON transactions (category_id) WHERE category_id IS NOT NULL;

CREATE TABLE payee_rules (
    id           TEXT    NOT NULL PRIMARY KEY,
    payee_key    TEXT    NOT NULL UNIQUE,
    type         TEXT    NOT NULL CHECK (type IN ('Income', 'Expense', 'Refund')),
    category_id  TEXT    NULL REFERENCES categories (id),
    account_id   TEXT    NULL REFERENCES accounts (id),
    use_count    INTEGER NOT NULL DEFAULT 0,
    updated_at   TEXT    NOT NULL
);

CREATE TABLE reconciliations (
    id                         TEXT    NOT NULL PRIMARY KEY,
    account_id                 TEXT    NOT NULL REFERENCES accounts (id),
    statement_date             TEXT    NOT NULL,
    statement_balance_cents    INTEGER NOT NULL,
    calculated_balance_cents   INTEGER NOT NULL,
    adjustment_transaction_id  TEXT    NULL REFERENCES transactions (id),
    note                       TEXT    NULL,
    created_at                 TEXT    NOT NULL
);

CREATE INDEX ix_reconciliations_account_date ON reconciliations (account_id, statement_date);
