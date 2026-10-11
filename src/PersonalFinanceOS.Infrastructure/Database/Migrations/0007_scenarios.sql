-- 0007: what-if scenarios (spec §16). A scenario is a named set of changes laid over the forecast; it never changes
-- transactions, planned items or the wishlist, and nothing here stores a calculated result.

CREATE TABLE scenarios (
    id          TEXT    NOT NULL PRIMARY KEY,
    name        TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    note        TEXT    NULL CHECK (note IS NULL OR length(note) <= 500),
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
);

CREATE TABLE scenario_changes (
    id                TEXT    NOT NULL PRIMARY KEY,
    scenario_id       TEXT    NOT NULL REFERENCES scenarios (id) ON DELETE CASCADE,
    kind              TEXT    NOT NULL CHECK (kind IN ('OneOff', 'Monthly', 'ChangePlanned', 'Purchase')),
    name              TEXT    NOT NULL CHECK (length(name) <= 100),
    is_income         INTEGER NOT NULL CHECK (is_income IN (0, 1)),
    amount_cents      INTEGER NOT NULL CHECK (amount_cents >= 0),
    date              TEXT    NOT NULL,
    end_date          TEXT    NULL,
    account_id        TEXT    NULL REFERENCES accounts (id),
    card_id           TEXT    NULL REFERENCES accounts (id),
    rule_id           TEXT    NULL REFERENCES recurring_rules (id),
    wishlist_item_id  TEXT    NULL REFERENCES wishlist_items (id),
    created_at        TEXT    NOT NULL,
    CHECK (end_date IS NULL OR end_date >= date),
    CHECK (kind <> 'ChangePlanned' OR rule_id IS NOT NULL),
    CHECK (kind = 'ChangePlanned' OR amount_cents > 0),
    CHECK (kind NOT IN ('OneOff', 'Monthly') OR account_id IS NOT NULL),
    CHECK (kind <> 'Purchase' OR ((account_id IS NULL) <> (card_id IS NULL)))
);

CREATE INDEX ix_scenario_changes_scenario ON scenario_changes (scenario_id);
