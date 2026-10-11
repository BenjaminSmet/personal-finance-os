# Decision log — Personal Finance OS

Agreed design choices and deviations from the specification (v1.1, 10 Oct 2026).
Newest entries at the bottom. The specification remains the source of truth for everything not listed here.

---

### D-001 · UI framework: Avalonia instead of WPF (2026-10-10)
**Spec:** §3 recommends WPF.
**Decision:** Avalonia 12.1 (XAML + MVVM, very close to WPF), approved by Benjamin.
**Why:** the development environment runs on Linux. With Avalonia the real UI can be started headlessly,
tested automatically and screenshotted on every build, instead of only being compiled. The result is still an
installable native Windows desktop app.
**Trade-off:** small differences from WPF controls; no impact on functionality in the spec.

### D-002 · Source hosting and builds on GitHub (2026-10-10)
Private repository `personal-finance-os`. GitHub Actions builds on Windows, runs all tests, uploads
screenshots and produces the installer on every push; version tags (`v0.1.0`, …) publish a GitHub Release.
Only source code is stored there — never financial data.

### D-003 · Schema migrations as plain SQL files, EF Core for queries (2026-10-10)
**Spec:** §3 "EF Core with SQLite or similar", §4 versioned migrations.
**Decision:** schema changes are numbered SQL files (`Database/Migrations/NNNN_name.sql`) applied by our own
`DatabaseInitializer`. EF Core is used for reading/writing data only (`EnsureCreated` / EF migrations are never used).
**Why:** full control over the upgrade sequence the spec demands (verified backup → transactional steps →
integrity/foreign-key/version/row-count/total checks → automatic restore on failure), easy to review, and
testable without design-time tooling. A test checks that the EF model matches the migrated schema.
**Rule:** a shipped migration is never edited; corrections are new migrations.

### D-004 · Sign convention for balances (2026-10-10)
Assets are positive, money owed is negative. A VISA balance of −€400 means €400 outstanding.
In the UI the user types the outstanding amount as a positive number; it is stored as negative.
This makes the VISA settlement a transfer (current account − / card +), so it can never be counted as a second expense (spec §6, §10).

### D-005 · Ownership share per account now, history later (2026-10-10)
Phase 1 stores the user's current share on each account (basis points, 50% = 5000, default suggestion
configurable). Effective-dated share history (`OwnershipShare` table, spec §9/§19) is added in phase 6
without changing existing data.

### D-006 · Protection against a missing database (2026-10-10)
A small `profile.json` marker is written next to the database. If the marker exists but `finance.db` is gone,
the app does **not** create a new empty database; it shows a recovery screen. Starting over requires an explicit,
confirmed user choice (spec §4 "never assume a new installation means a new financial profile").

### D-007 · Basic installer from phase 1 (2026-10-10)
**Spec:** installer is listed in phase 8.
**Decision:** a simple Inno Setup installer is produced from phase 1 so every phase can be tried on Windows.
Per-user install (no admin rights), program files and data strictly separate, uninstall keeps data and says
where it is. Phase 8 hardens it (upgrade tests per schema version, release notes, optional signing).

### D-008 · Journal mode (2026-10-10)
SQLite default rollback journal (not WAL) for a single-user desktop app: the database is always one file,
which keeps backups, restores and copies simple and safe.

### D-009 · Amount input (2026-10-10)
Amounts accept Belgian and English notation (`42,80`, `1.234,56`, `1,234.56`). One separator followed by
exactly three digits (`1.234`) is read as thousands — cents never have three digits — but flagged so the user
sees how it was read (spec §7 preview on ambiguity).

### D-010 · File naming (2026-10-10)
Benjamin's YYYY-MM-DD naming rule applies to documents (like this log). Source code, project files and
conventional files (`README.md`, `docs/releases/vX.Y.Z.md`) keep standard names because tooling depends on them.

### D-011 · Dependency versions (2026-10-10)
Avalonia 12.1.3, CommunityToolkit.Mvvm 8.4.1, EF Core / Microsoft.Data.Sqlite 10.0.11, xUnit v3 3.2.2
(required by Avalonia's headless test package). All MIT/Apache-2.0 licensed; every version was at least two
weeks old when chosen. PDF/chart libraries are chosen in the phases that need them, after a licence check.

### D-012 · Visual design: "liquid glass" with light and dark mode (2026-10-10)
Requested by Benjamin: an iOS 26/27-like look.
- Translucent glass panels (fill + bright edge + soft shadow) over a softly coloured backdrop; floating glass sidebar;
  pill-shaped buttons; rounded inputs; large bold titles.
- On Windows 11 the window requests Mica/Acrylic so the desktop shows through slightly; elsewhere the backdrop is opaque.
- iOS-style toggle switches (green when on) and segmented pickers (theme, language).
- Theme: System (follows Windows) / Light / Dark, stored in settings. All colours are tokens in `Styles/Theme.axaml`.
- Meaning is never carried by colour alone: warnings, errors and debts also use symbols or labels (spec §20).

### D-013 · Ledger model: card purchases and settlements follow from the account (2026-10-10)
**Spec:** §6 lists eight transaction types, including credit-card purchase, credit-card settlement and opening balance.
**Decision:** stored types are Income, Expense, Refund, Transfer and Adjustment. A card purchase is an Expense on the
credit-card account; paying the card is a Transfer from the current account to the card (as D-004 already implies).
The screens still label them "card purchase" and "card payment". Opening balances stay on the account (spec §19
allows "explicit opening-balance fields").
**Why:** double counting becomes impossible by construction instead of by rule: a settlement can never be an
expense because it is a transfer, and the database enforces the direction of every type (an expense can never add
money, income never removes it, every transfer has a linked second side).
**Rules:** income cannot be booked on a credit card (money back is a refund, §10); a transaction cannot be dated
before its account's balance date, because that period is already in the opening balance.

### D-014 · Remove = void, with undo (2026-10-10)
Removing a transaction marks it voided instead of deleting it; voided lines never count anywhere. Every add, edit and
remove of this session can be undone, newest first (spec §7). Undo refuses, and changes nothing, when the
transaction was changed in another way since. Both sides of a transfer are always edited, voided and undone together.
Important changes go to the local audit log (no amounts or names). Phase 3 import batches will build on the same void.

### D-015 · Quick Add (2026-10-10)
One line in Dutch or English, read locally and deterministically: amount (D-009 formats), payee, date words
(`vandaag`, `gisteren`, `5/10`…), `naar`/`van`/`met` + account, `visa`/`cash`, and keywords for transfers, income and
refunds. A clear line is saved with Enter; anything uncertain (ambiguous or several amounts, unknown destination,
missing account, future date) opens an editable preview first. Nothing is saved before confirmation. Each confirmed
entry teaches a payee rule (type, category, account); a correction made in the preview is what is learned, so
suggestions never override corrections. Quick Add sits above every page; Ctrl+K focuses it.

### D-016 · Categories and reconciliation (2026-10-10)
Starter categories (spec §13 list, plus "Loan payment") are created once, in the interface language of that moment,
and are then ordinary editable data; archiving them all never brings them back. Categories can be added, renamed and
archived; merging and reordering come with budgets in phase 5.
Reconciliation records the statement balance and date per account and shows the difference. It never changes the
balance by itself: an adjustment is only added when the user asks, with a reason, and an adjustment is never
income or spending.

### D-017 · Bank-file import: formats and column detection (2026-10-10)
**Spec:** §8 (CSV required, XLSX if reliable, PDF not in v1.0) and §25 (bank column mappings deferred).
**Decision:** CSV in UTF-8, UTF-16 or Windows-1252 (Belgian exports, € sign) with `;`, `,`, tab or `|`, quoted cells
as in RFC 4180. XLSX is read without a third-party library (first worksheet, shared strings, number and date
cells; formulas use their saved value) with size limits on every part. Old `.xls` and PDF are refused with a clear
message. No bank is hard-coded: the header line, date format (day-first wins ties), decimal separator, and the
amount layout (signed / separate debit and credit / amount plus "Af/Bij") are detected from the file, and a
"Saldo"/"Balance" column is never taken for the amount. "Valuta" is only a currency when it holds codes like EUR,
because Belgian exports use it for the value date. Lines in another currency are rejected (§6). The confirmed
mapping is remembered per header line, never the file.
**Navigation:** Import gets its own item after Accounts (11 items instead of the 10 suggested in §22), because §22 lists
CSV import among the flows to prioritise and it then is one click away.

### D-018 · Duplicates are candidates, matched one to one (2026-10-10)
Each existing line can match one bank line, so two identical coffees on one day stay two lines. In order:
(1) same bank reference and amount → already in the app; (2) same date, amount and text, unless both carry different
references → already in the app; (3) same amount within 3 days: the generated other side of a transfer imported
from the other account's file → already in the app; a line typed in by hand (Quick Add or manual) → *possibly*
entered by hand. Text alone never decides. Duplicates and possible duplicates are unticked but can be ticked.
A line the user removed still counts, so re-importing does not bring it back silently; lines removed by undoing
their import do not, so that file can be imported again. Duplicates are checked again at confirmation, so two
imports of the same file never both land.

### D-019 · Import batches, transfer linking and undo (2026-10-10)
A confirmed import is one batch, saved in one database transaction with its lines (`import_batch_id`, bank
reference) and the remembered mapping: a failure leaves nothing behind (§8 step 10). Lines before the account's
balance date are skipped, because the opening balance already contains them.
A line marked as a transfer to an own account creates both sides — unless the other account already has an income,
expense or refund with the opposite amount within 3 days. That line then *becomes* the other side (it keeps its
date, text and reference; its category no longer applies) instead of a second copy (§9). What it was is stored in
`import_transfer_links`, so undo restores it exactly. Card payments use the same mechanism, so a VISA settlement
never counts as spending twice.
Undo voids the batch's lines (never deletes) and marks the batch undone. Lines edited since the import are listed
separately and kept unless the user chooses to remove them too (guided recovery); both sides of a transfer are
always kept or removed together. Editing or removing an imported line warns first (§7).
Learning: a category chosen in the preview is learned for the counterparty name (never a whole bank description),
the same payee rules Quick Add uses. Suggested transfers come from an own account's name in the text, "VISA" (paying
the card), and payment words on a card statement; refunds from words like "terugbetaling". Rounding transfers and
recurring items follow in phase 4.

### D-020 · Credit-card settings per card, nothing hard-coded (2026-10-10)
**Spec:** §10 names €1,500 and the 28th–27th period as known settings, both configurable.
**Decision (Benjamin, 10 Oct):** the limit and the day a statement period starts are entered when a card is created
and can be changed later (Accounts › Card settings); there are no built-in values. The debit day and the paying
account are optional until known. A day past a short month's end means its last day (31 → 28 Feb), so periods never
overlap or leave gaps. Settings live in `credit_card_profiles`; changing them only changes estimates.
**Card figures** (§10 display): outstanding = the card balance; available credit = limit − outstanding (an estimate,
never added to cash); the statement of the closed period = what was owed when it closed; payments recorded after
the close are compared with it and a difference is shown, never adjusted. The current period's expected statement
includes open planned card payments (subscriptions) by their date, so a subscription on the 28th belongs to the next
period.

### D-021 · Recurring items: rules plus decisions, confirmation links one transaction (2026-10-10)
A recurring rule (income, payment or transfer; weekly, monthly, quarterly or yearly, counted from its first date so
no day drifts) only plans. Open occurrences are calculated, not stored; only the user's decision is stored
(`scheduled_items`: confirmed with its transaction, postponed with a new date, or skipped). Confirming creates the
transaction and the link in one save, and goes through the ledger's checks, audit and undo. If that transaction is
later removed, the occurrence is open again, so a planned and an actual item can never both count, and nothing is
lost. A recorded line (for example from an import) with the same kind and account, within 5 days and 10% of the
amount, is suggested for linking instead of adding a second one; each line can settle one occurrence (enforced by a
unique index). Card statements are not separate rules: they are calculated from the card (D-020), so a planned
transfer to the card is left out of forecasts to avoid counting it twice.

### D-022 · Safe-to-spend and cash flow (2026-10-10)
Safe-to-spend counts the personal current account(s) and cash only; joint, savings and investment accounts are not
spendable unless a transfer from them is planned (§13). It runs until the day before the next expected income
(30 days when none is planned) and subtracts open planned payments and outgoing transfers (overdue ones included),
card statements due from a counted account, transactions already recorded with a later date, and the minimum buffer
(Planning › Forecast settings, €0 until set). Expected income is never counted before it arrives. The breakdown and
the reasons a figure is less certain (no income planned, card not set up, debit day or paying account unknown,
overdue items) are always shown. Cash flow for 7, 30 and 90 days uses the same accounts and labels income as expected.

### D-023 · Bank rounding on import (2026-10-10)
Lines whose text says "afronding", "round up", "arrondi"… (at most €10) become a transfer to the savings account
chosen in Planning › Forecast settings, or to the only savings account; on that savings account's own file they are
the incoming side, so the duplicate check of D-018 recognises them and they are never added twice (§11). When the
account is unclear or the amount too large, the line is flagged "looks like rounding" and left unticked for review.
**Navigation:** the Forecasts item is called "Planning" and opens this phase's page; budgets stay in phase 5.


### D-024 · Budgets hold from a month until changed (2026-10-10)
A budget belongs to an active spending category and starts in a month (`budgets`: category, start month, amount). The
budget of a month is the one with the latest start month in or before it, so changing a budget never rewrites earlier
months and "actual vs budget" for the past stays what it was. Setting a budget for a month replaces budgets that were
set for later months, so the new amount holds until it is changed again; zero removes the budget from that month on.
Income categories, archived categories and lines without a category have no budget. Transfers and card settlements
never count as spending; refunds lower it; a card purchase counts in its purchase month (same rules as `LedgerMath`).

### D-025 · Analysis only compares complete months (2026-10-10)
Nothing before the earliest balance date of any account is recorded, so a month that starts before it is marked
incomplete and a month before it has no data. Comparisons use up to 3 earlier months that are complete and over; with
fewer, the screen says how many it used or that there is nothing to compare with yet. A category is called unusual
only with 3 complete months of history, when it differs from its average by at least 50 % and €25; a month still in
progress can only be unusually high. Recurring spending = expenses that settle a planned item (confirmed or linked,
D-021); the rest is one-off. Planned vs actual lists the month's occurrences by due date with what became of them.
Shared accounts show their full spending and the personal part from the account's ownership share; totals keep
counting the full amount, as before.

### D-026 · Category merge, order and bringing back (2026-10-10)
Merging moves every transaction, payee rule, recurring rule and import link of the source to the target (same kind,
active) and archives the source, in one database transaction. The transactions' `updated_at` is left alone, so an
imported line does not look edited by the user. The target keeps its own budgets; only when it has none, it takes over
the source's. Undo does not reverse a merge, so the screen explains this and asks for confirmation first. Order is
changed with up/down within a kind and is used in lists and budgets. Archived categories are listed in Settings and
can be brought back when no active category of the same kind has that name.
**Dashboard** (§13): spendable money, safe-to-spend, items needing attention (to confirm, over budget, card not set up,
over limit or a payment that differs from the statement, reconciliation difference), this month with its budget,
the next 14 days, expected cash flow, credit cards, savings and investments apart from spendable money, and every
account. Import review, unmatched transfers and stale valuations are not stored as open items yet; valuations come in
phase 6.

### D-027 · Valuations are dated and never overwrite history (2026-10-10)
Pension and investment accounts (§17: pension, Saxo) and every asset or liability get their value from `valuations`:
one value per owner and date, never negative (a liability's value is what is still owed). A value for a date that
already has one corrects that date; any other date adds a value, so the history is kept. Values cannot be dated in the
future. Current, savings, cash and card accounts always take their value from the ledger and refuse valuations.
For a pension or investment account the value on a date is its latest valuation on or before that date plus the
deposits and withdrawals recorded after it; without any valuation the recorded deposits are used and the line (and net
worth) is marked incomplete. A valuation older than 90 days is flagged as out of date on the page and the Dashboard.

### D-028 · Assets and liabilities outside the ledger (2026-10-10)
`assets` holds a house, vehicle, mortgage, loan or other asset/liability with the user's share. There are no
transactions on them and loan payments recorded as expenses never lower a liability (§17): the user enters the
remaining amount. "Sold or paid off" records a zero value today and archives the item, so earlier months keep their
value. An asset with values, but none on a given date, makes that date's net worth incomplete.

### D-029 · Ownership shares change from a date on (2026-10-10)
`ownership_changes` keeps the share of an account or asset from a date on (§9). The first change also stores the share
used so far, from the account's opening date (or the asset's first value), so earlier months keep it. Changes cannot
start in the future and a credit card is always owed in full. `Account.OwnershipShare` stays the share of today and is
saved together with the history. The share on a date is the latest change on or before it (before the first, the
earliest). Shared spending in the monthly analysis (D-025) now uses the share of that month.

### D-030 · Net worth (2026-10-10)
Net worth = the user's share of every account and asset minus the user's share of every liability (§17). A card counts
with what is owed; its limit never counts. A shared account counts once, at the user's share; the household view shows
full amounts separately, never added to the personal total. Archived items are left out once their value is zero. The
history shows month ends for the last 12 months (today for this month), leaves out months before the first balance
date or value, and marks months where a value was missing as incomplete instead of guessing. An account opened later
with an opening balance marks earlier months incomplete; one opened with zero did not exist yet.

### D-031 · Affordability: technically affordable versus prudent (2026-10-10)
Affordability uses the same forecast as safe-to-spend (D-022), projected day by day for the personal current
account(s) and cash: recorded later transactions, open planned items (overdue ones today, overdue income left out) and
card statements on their debit day. Savings, the joint account and card credit are never spendable cash.
- **Prudent**: bought today, the lowest expected balance over the next 90 days (30 when no income is planned) stays at
  or above the minimum buffer. 90 days covers about three pay cycles, so a purchase that only looks fine until the next
  salary but breaks the month after is not called prudent. Without planned income the answer says so.
- **Technically affordable**: the money is on the accounts now, but the buffer or (shown separately) the expected
  bills and card statements would not be covered. Otherwise **not affordable now**; without a current account or cash
  it **cannot be calculated** rather than guessing (§23).
- **Earliest comfortable date**: the first day within a year from which buying keeps the buffer through the look-ahead.
- **By card**: the purchase joins the current statement and leaves the paying account on the debit day; it needs
  enough available credit. When the paying account is unknown or not counted, no verdict is given.
- **Effect on other wishes**: each open item's earliest comfortable date before and after buying this one (today when
  the money is there, else on its own earliest date).

### D-032 · Wishlist and buying (2026-10-10)
Statuses stored: wanted, saving, postponed, bought, cancelled. "Affordable" (§14) is calculated every time and shown
as a badge, never stored, so it cannot go stale. The monthly saving is the user's plan, shown as "together in N
months"; it moves no money. Buying is confirmed by the user and records one expense (on a current account, cash or
card) together with the item in one database transaction, or only marks it bought when the expense is already in the
ledger. One expense can belong to one item only, and a bought item cannot be bought again; undoing or voiding its
expense puts it back on the list. Only an item marked bought without an expense can be reopened by hand.
**Dashboard**: net worth with assets and liabilities, savings and investments at their valued amount, and attention
items for missing or out-of-date values; pension and investment tiles show their valued amount.

### D-033 · One forecast timeline per account (2026-10-11)
Safe-to-spend, affordability and scenarios now share one timeline: the expected balance of every money account
(current, joint, savings, cash) from today's balance, recorded later transactions, open planned items on both sides
of a transfer and card statements on the paying account's debit day. "Spendable" stays the personal current account
and cash (D-022, D-031); the joint and savings accounts are projected but never counted as spendable. Cards and valued
accounts (pension, investments) are not projected. A planned payment to a card is left out, because the statement is
calculated from the card itself.

### D-034 · What-if scenarios (2026-10-11)
A scenario is a saved list of changes laid over that timeline (§16); evaluating it never writes anything. Changes:
a one-off amount, a monthly amount (optional end), a planned item at a new amount from a date (zero removes it), and a
purchase from an account or by card. Choices made where the spec is open:
- A date in the past counts from today and says so. A change outside the twelve months shown says so.
- A planned **card** expense changes each statement it falls in, paid on that statement's debit day. Changing a
  planned payment *to* a card changes nothing (the statement is what is forecast) and says so.
- A card purchase over the available credit is still calculated but flagged: the limit is not money.
- Horizons 7/30/90 days; months: the current month (from today) plus eleven. The forecast itself runs a year plus the
  affordability look-ahead so wishlist dates (D-031) match the wishlist page.
- Shortfalls: the first day spendable money goes below the buffer and below zero, and the first day any account goes
  below zero. Warnings that only appear with the scenario are marked as such.
- Effect on the wishlist: each open item's earliest comfortable date with and without the scenario; an item bought in
  the scenario is left out.

### D-035 · The assistant (2026-10-11)
The assistant (§15) is local and rule-based: keyword rules in Dutch and English recognise the ten initial questions
and read an amount (€, "euro", "5k", 5.000/5,000; counts of days or months and percentages are not amounts), a
category (its name, plural or a starter-category word in either language), a wishlist item, a planned item and a
period. Every figure in an answer comes from the existing services (forecast, affordability, analysis, scenarios); the
answer text only names them. Rules applied:
- Expected income is always listed as an assumption; savings and the joint account are not spendable; card credit is
  never money and the card is only suggested when the purchase is prudent with its statement on its debit day.
- A missing value that changes the answer (amount, category, wishlist item) gets one question; a short reply ("€250",
  "Laptop") completes the original question. Unclear questions get the example questions instead of a guess.
- "What changed versus last month" compares the last two **complete** months (September with August on 11 October),
  so a half month is never compared with a full one.
- "What if X costs €N more": a planned item named in the question changes by N (salary when the question is about
  income); otherwise it is a one-off cost of N today on the first current account, stated as an assumption. The
  result is an unsaved scenario; **Save as scenario** saves it only when clicked.
- "Category room" checks both the category's budget and safe-to-spend; without a budget it says so.

### D-036 · Reports (2026-10-11)
The reports of §18 are built by one service from the ledger, forecast, card, budget and net worth services. The
screen, the PDF and the CSV are all made from one formatted report, so their values cannot differ (§23). Choices made
where the spec is open:
- **Months shown:** only months from the first account's opening date up to today. A note names what was left out.
  The monthly summary compares with up to three complete months before the period, as the analysis page does.
- **Planned items:** only when "Include planned items" is on. They are shown in separate tables marked as estimates
  and never added to totals. Budget, joint, card and net worth reports have no forecast part, so the option is hidden
  for them.
- **Cash flow:**
  - actual money in and out per account up to today, transfers included (it is cash movement, not spending);
  - with planned items, the expected closing balance from the forecast timeline, with card statements on their
    debit day;
  - cards have no projected balance of their own.
- **Card report:**
  - one line per statement period from the card's own settings;
  - a closed period's statement is what was owed at its end, and "paid" is the transfers to the card in the
    following period;
  - the running period shows the estimated statement, marked as an estimate.
- **Joint report:** the full amounts and your part at each line's date, so a share that changed during the period
  is applied correctly.
- **Exports:**
  - no new dependency;
  - the PDF is written directly with the standard Helvetica fonts (WinAnsi covers € and Dutch accents) on A4, landscape
    when a table is too wide, with page numbers and the title on every page;
  - the CSV uses `;` with decimal commas in Dutch and `,` with decimal points in English, ISO dates, numbers without
    a currency symbol, and neutralised formula-like text;
  - the on-screen report serves as the print preview; printing is done from the saved PDF;
  - file names are "<report> <from> - <to>", and the user picks the folder.

### D-037 · Manual backup and restore (2026-10-11)
- **The backup file:** one `.zip` with `finance.db`, `profile.json` and `manifest.json`. The manifest holds the
  format, the app and schema versions, the row counts and a SHA-256 of the database.
- **Settings:** all settings live in the database, so they are included. Logs are not.
- **Success:** the backup is reported only after the zip was written to a temporary name, read back, its checksum,
  integrity and schema version checked, and then renamed.
- **Same drive:** the drive is compared with the data folder's drive. A warning is shown after saving there, and a
  note is always visible.
- **Reminder:** never, or every 7, 14, 30 or 90 days; 30 is the default. The dashboard shows it once at least one
  account exists.
- **Restore:**
  1. The file is inspected, which changes nothing.
  2. The user gives an explicit confirmation.
  3. A verified copy of the current database is made in `backups\` (labelled `before-restore`). If that fails,
     nothing is replaced.
  4. The database is replaced and the app reopens through the normal startup, which upgrades an older schema with
     its own pre-upgrade backup.
  5. If replacing fails, the previous database is copied back.
- **Newer backups:** a backup with a newer schema than the app is refused.

### D-038 · Release hardening (2026-10-11)
- **Tests that read the source tree:**
  - every UI text has English and Dutch with the same placeholders;
  - every key used in code, markup and reports exists, every error code has a text, and every enum value shown to
    the user has a text;
  - no network API appears anywhere in `src`, and no telemetry package is referenced;
  - every text box, list and date field has an accessible name. Missing names were added; most come from the
    field's label.
- **Installer test in CI:**
  - a silent install, then the installed exe runs `--self-check <folder>`, which opens or creates a database through
    the normal startup without a window;
  - a second install over the top, then a silent uninstall;
  - the data folder must survive every step.
- **Version:** 1.0.0. A `v1.0.0` tag publishes the release with `docs/releases/v1.0.0.md`. The tag is only pushed when
  Benjamin asks.
- **Placeholder page removed:** every navigation item now leads to a real page.
