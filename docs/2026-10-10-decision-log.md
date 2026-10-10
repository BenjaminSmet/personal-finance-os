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

