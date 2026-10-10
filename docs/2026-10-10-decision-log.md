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
