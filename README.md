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
src/PersonalFinanceOS.Domain          Money, shares, accounts, ledger rules — pure, no dependencies
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

Phase 5 (dashboard, budgets, analysis) of 8 — see `docs/2026-10-10-decision-log.md` and the product specification.

Quick Add sits above every page (Ctrl+K). Type a line and press Enter, for example:

| You type | Recorded as |
|---|---|
| `Supermarkt 42,80` | Expense of €42.80 from the remembered account, today |
| `Loon 2400` | Income of €2,400 |
| `Spotify 17,99 visa` | Card purchase on the VISA account |
| `Koffie 3,20 gisteren` | Expense dated yesterday |
| `Transfer 600 naar gezamenlijke rekening` | Transfer, not income or spending |

Anything uncertain is shown for checking first; nothing is saved before you confirm.

### Importing a bank file

Export your transactions from your bank's website as CSV (or XLSX) and open **Import**:

1. Choose the account the file belongs to and the file. It is read on this computer only and not kept.
2. The columns are detected (date, amount, description, counterparty, reference). Check them once; the choice is
   remembered for the next file with the same header line.
3. Every line is shown with what will happen. Lines that are probably already in the app (same bank reference, or
   same date, amount and text, or typed in by hand a few days earlier) are unticked. Unreadable lines, lines in
   another currency and lines before the account's balance date are never imported, and the reason is shown.
4. Type and category are suggestions: learned payee rules, refunds, and transfers to your own accounts (an own
   account's name or "VISA" in the text). A transfer whose other side is already in the app is linked to it, not
   added twice.
5. Confirm. Everything is saved together or not at all. Any import can be undone later from **Earlier imports**;
   lines you changed since are kept unless you choose otherwise.
6. Bank rounding lines ("afronding", "round up") become a transfer to the savings account chosen under
   **Planning › Forecast settings** (or the only savings account). When that is unclear, the line is flagged and
   left unticked.

### Credit card and planning

- A credit card asks for its **credit limit** and the **day a new statement period starts** when you create it
  (with 28: the 28th through the 27th). The debit day and the paying account are optional. All of it can be changed
  later under **Accounts › Card settings**; recorded transactions never change.
- **Planning** shows, for each card, what is owed, the estimated available credit (not cash), this period's
  spending, the expected statement and the next debit. A recorded payment that differs from the estimate is shown,
  never corrected silently.
- **Recurring items** (salary, rent, subscriptions, transfers…) create expected items, not transactions. Under
  **To confirm** you confirm each one (date and amount editable), link it to a line that is already there (an
  import, for example), postpone it or skip it. Removing the confirmed transaction makes the item open again.
- **Safe to spend** = personal current account and cash, minus everything planned and the card statements due
  before your next expected income, minus your minimum buffer. Expected income is never counted before it arrives.
  The calculation and anything that makes it less certain are shown with it.

### Budgets and analysis

- **Budgets & analysis** shows one month at a time (previous/next): income, spending and net, compared with the
  average of up to three earlier complete months. Months before your first balance date are never compared.
- Each spending category shows what was spent, its budget with a bar and what is left or over (in words, not only in
  colour), and its usual amount. Type a budget and press **Save**: it holds from that month until you change it;
  earlier months keep theirs. Clear differences from your own history are marked "unusual" once there are three
  complete months to compare with.
- Also on the page: income per category, recurring versus one-off spending, planned items next to what actually
  happened, shared accounts with your part, and a six-month trend.
- **Settings › Categories**: change the order, rename, merge one category into another (explained and confirmed
  first), archive, and bring archived categories back.
- The **Dashboard** adds what needs attention, the month's budget, the next 14 days, expected cash flow, credit cards
  and savings and investments, kept apart from spendable money.
