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
