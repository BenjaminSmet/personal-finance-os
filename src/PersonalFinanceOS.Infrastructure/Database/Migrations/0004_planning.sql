-- 0004: planning — credit-card settings, recurring rules and the user's decisions about their occurrences.
-- Recurring rules create expected events only; a planned item never changes a balance until it is
-- confirmed, and the confirmation links the real transaction so nothing counts twice (spec §12).

CREATE TABLE credit_card_profiles (
    account_id              TEXT    NOT NULL PRIMARY KEY REFERENCES accounts (id),
    credit_limit_cents      INTEGER NOT NULL CHECK (credit_limit_cents >= 0),
    statement_start_day     INTEGER NOT NULL CHECK (statement_start_day BETWEEN 1 AND 31),
    settlement_day          INTEGER NULL CHECK (settlement_day IS NULL OR settlement_day BETWEEN 1 AND 31),
    settlement_account_id   TEXT    NULL REFERENCES accounts (id),
    updated_at              TEXT    NOT NULL,
    CHECK (settlement_account_id IS NULL OR settlement_account_id <> account_id)
);

CREATE TABLE recurring_rules (
    id             TEXT    NOT NULL PRIMARY KEY,
    name           TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    kind           TEXT    NOT NULL CHECK (kind IN ('Income', 'Expense', 'Transfer')),
    account_id     TEXT    NOT NULL REFERENCES accounts (id),
    to_account_id  TEXT    NULL REFERENCES accounts (id),
    category_id    TEXT    NULL REFERENCES categories (id),
    amount_cents   INTEGER NOT NULL CHECK (amount_cents > 0),
    frequency      TEXT    NOT NULL CHECK (frequency IN ('Weekly', 'Monthly', 'Quarterly', 'Yearly')),
    start_date     TEXT    NOT NULL,
    end_date       TEXT    NULL,
    status         TEXT    NOT NULL CHECK (status IN ('Active', 'Ended')),
    created_at     TEXT    NOT NULL,
    updated_at     TEXT    NOT NULL,
    -- A transfer goes to another own account and has no category; income and expenses have no destination.
    CHECK ((kind = 'Transfer') = (to_account_id IS NOT NULL)),
    CHECK (kind <> 'Transfer' OR (category_id IS NULL AND to_account_id <> account_id)),
    CHECK (end_date IS NULL OR end_date >= date(start_date, '-1 day'))
);

CREATE INDEX ix_recurring_rules_status ON recurring_rules (status);

CREATE TABLE scheduled_items (
    id               TEXT NOT NULL PRIMARY KEY,
    rule_id          TEXT NOT NULL REFERENCES recurring_rules (id),
    occurrence_date  TEXT NOT NULL,
    status           TEXT NOT NULL CHECK (status IN ('Confirmed', 'Postponed', 'Skipped')),
    planned_date     TEXT NULL,
    transaction_id   TEXT NULL REFERENCES transactions (id),
    created_at       TEXT NOT NULL,
    updated_at       TEXT NOT NULL,
    UNIQUE (rule_id, occurrence_date),
    CHECK ((status = 'Confirmed') = (transaction_id IS NOT NULL)),
    CHECK ((status = 'Postponed') = (planned_date IS NOT NULL))
);

-- One real transaction can settle only one planned occurrence.
CREATE UNIQUE INDEX ix_scheduled_items_transaction ON scheduled_items (transaction_id) WHERE transaction_id IS NOT NULL;
