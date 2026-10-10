-- 0003: bank-file import — import batches, remembered column mappings, and the link from each
-- transaction to the import that created it. Source files are never stored, only their name.

CREATE TABLE import_batches (
    id               TEXT    NOT NULL PRIMARY KEY,
    account_id       TEXT    NOT NULL REFERENCES accounts (id),
    file_name        TEXT    NOT NULL CHECK (length(file_name) <= 255),
    format           TEXT    NOT NULL CHECK (format IN ('csv', 'xlsx')),
    first_date       TEXT    NULL,
    last_date        TEXT    NULL,
    imported_count   INTEGER NOT NULL CHECK (imported_count >= 0),
    duplicate_count  INTEGER NOT NULL CHECK (duplicate_count >= 0),
    skipped_count    INTEGER NOT NULL CHECK (skipped_count >= 0),
    invalid_count    INTEGER NOT NULL CHECK (invalid_count >= 0),
    status           TEXT    NOT NULL CHECK (status IN ('Active', 'Undone')),
    imported_at      TEXT    NOT NULL,
    undone_at        TEXT    NULL
);

CREATE INDEX ix_import_batches_account ON import_batches (account_id, imported_at);

-- Existing rows keep NULL: they were not imported.
ALTER TABLE transactions ADD COLUMN import_batch_id TEXT NULL REFERENCES import_batches (id);
ALTER TABLE transactions ADD COLUMN bank_reference TEXT NULL CHECK (bank_reference IS NULL OR length(bank_reference) <= 100);

CREATE INDEX ix_transactions_import_batch ON transactions (import_batch_id) WHERE import_batch_id IS NOT NULL;
CREATE INDEX ix_transactions_bank_reference ON transactions (account_id, bank_reference) WHERE bank_reference IS NOT NULL;

-- Lines that already existed and were turned into one side of a transfer by an import, with what
-- they were before, so undoing that import restores them exactly.
CREATE TABLE import_transfer_links (
    batch_id              TEXT NOT NULL REFERENCES import_batches (id),
    transaction_id        TEXT NOT NULL REFERENCES transactions (id),
    original_type         TEXT NOT NULL CHECK (original_type IN ('Income', 'Expense', 'Refund')),
    original_category_id  TEXT NULL REFERENCES categories (id),
    PRIMARY KEY (batch_id, transaction_id)
);

CREATE TABLE import_profiles (
    id            TEXT    NOT NULL PRIMARY KEY,
    header_key    TEXT    NOT NULL UNIQUE,
    mapping_json  TEXT    NOT NULL,
    use_count     INTEGER NOT NULL DEFAULT 0,
    updated_at    TEXT    NOT NULL
);
