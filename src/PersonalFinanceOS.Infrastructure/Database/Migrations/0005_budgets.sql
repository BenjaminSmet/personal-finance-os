-- 0005: monthly budgets per expense category (spec §13). A budget holds from its start month until a budget with a
-- later start month replaces it, so earlier months keep the budget they had. Zero means "no budget from then on".

CREATE TABLE budgets (
    id            TEXT    NOT NULL PRIMARY KEY,
    category_id   TEXT    NOT NULL REFERENCES categories (id),
    start_month   TEXT    NOT NULL CHECK (start_month GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-01'),
    amount_cents  INTEGER NOT NULL CHECK (amount_cents >= 0),
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL,
    UNIQUE (category_id, start_month)
);
