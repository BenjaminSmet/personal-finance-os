# Personal Finance OS

A private, offline Windows desktop app for personal finances: current and joint accounts, savings,
a VISA credit card, pension savings and ETF investments, with forecasts, a wishlist, reports and a
rule-based assistant. No cloud, no bank connections, no telemetry.

## Install (Windows 10/11)

1. Download the latest `PersonalFinanceOS-Setup-x.y.z.exe` from **Releases**.
2. Run it. No administrator rights are needed.
3. Windows may warn that the app is from an unknown publisher (it is not code-signed yet).
   Click **More info → Run anyway**.

Each Windows user gets their own separate database. Updating or uninstalling never deletes your data.

## Where is my data?

`%LOCALAPPDATA%\PersonalFinanceOS\`

| File / folder | Purpose |
|---|---|
| `finance.db` | Your financial database (SQLite). **Not encrypted** — protect your Windows account with a password. |
| `backups\` | Verified backups, made automatically before every database upgrade. |
| `logs\app.log` | Technical log. Never contains amounts, names or payees. |
| `profile.json` | Marker that prevents an empty database from silently replacing a missing one. |

A backup on the same disk does not protect against disk failure; copy backups to an external drive.

## Project structure

```
src/PersonalFinanceOS.Domain          Money, shares, accounts — pure rules, no dependencies
src/PersonalFinanceOS.Application     Use cases and calculations (the single source of truth for numbers)
src/PersonalFinanceOS.Infrastructure  SQLite, safe migrations, backups, logging
src/PersonalFinanceOS.App             Avalonia UI (MVVM), Dutch/English
tests/PersonalFinanceOS.Tests         Unit, database and headless UI tests (+ screenshots)
installer/                            Inno Setup script
docs/                                 Decision log, UI string table, release notes
```

## Development

```bash
dotnet build PersonalFinanceOS.slnx
dotnet test PersonalFinanceOS.slnx
dotnet run --project src/PersonalFinanceOS.App
```

UI texts live in `docs/strings.tsv` (English and Dutch side by side). After editing, regenerate the resource files:

```bash
python3 tools/gen_resx.py docs/strings.tsv src/PersonalFinanceOS.App/Resources
```

Database changes: add a new `src/PersonalFinanceOS.Infrastructure/Database/Migrations/NNNN_name.sql`.
Never edit a migration that has been released.

## Status

Phase 1 (foundation) of 8 — see `docs/2026-10-10-decision-log.md` and the product specification.
