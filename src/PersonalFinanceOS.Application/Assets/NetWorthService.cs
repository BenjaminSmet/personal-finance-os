using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Assets;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Ownership;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Assets;

/// <summary>Where a net worth value comes from, so the screen can explain it (spec §13: net worth with explanation).</summary>
public enum ValueSource
{
    /// <summary>The account balance from the ledger.</summary>
    Ledger,

    /// <summary>The latest manual valuation.</summary>
    Valuation,

    /// <summary>The latest manual valuation plus deposits and withdrawals recorded after it.</summary>
    ValuationPlusLedger,

    /// <summary>A pension or investment account without any valuation yet: its recorded deposits are used.</summary>
    LedgerWithoutValuation,

    /// <summary>An asset or liability without any valuation on or before the date: counted as zero.</summary>
    NoValuation,
}

/// <summary>
/// One line of net worth. Values are positive for what is owned and for what is owed; <see cref="IsLiability"/>
/// says which side it is on. <see cref="FullValue"/> is the whole account or asset; <see cref="PersonalValue"/> the
/// user's share of it, which is what personal net worth counts (spec §9: never both).
/// </summary>
public sealed record NetWorthLine(
    string Name,
    Account? Account,
    Asset? Asset,
    bool IsLiability,
    Money FullValue,
    Share Share,
    Money PersonalValue,
    ValueSource Source,
    DateOnly? ValuedOn,
    bool IsStale)
{
    public Guid OwnerId => Account?.Id ?? Asset!.Id;

    /// <summary>A value that is missing or estimated from deposits only, which makes the total incomplete.</summary>
    public bool IsUncertain => Source is ValueSource.LedgerWithoutValuation or ValueSource.NoValuation;
}

/// <summary>
/// Net worth on a date (spec §17): included assets minus included liabilities, with each line at the user's share.
/// A credit limit is never an asset; a card only counts with what is owed on it.
/// </summary>
public sealed record NetWorth(DateOnly Date, IReadOnlyList<NetWorthLine> Lines)
{
    /// <summary>The user's part of everything owned.</summary>
    public Money Assets => Money.Sum(Lines.Where(l => !l.IsLiability).Select(l => l.PersonalValue));

    /// <summary>The user's part of everything owed.</summary>
    public Money Liabilities => Money.Sum(Lines.Where(l => l.IsLiability).Select(l => l.PersonalValue));

    public Money Total => Assets - Liabilities;

    /// <summary>Household view: full values of shared items, without applying the user's share.</summary>
    public Money HouseholdTotal =>
        Money.Sum(Lines.Where(l => !l.IsLiability).Select(l => l.FullValue)) - Money.Sum(Lines.Where(l => l.IsLiability).Select(l => l.FullValue));

    public bool HasSharedItems => Lines.Any(l => !l.Share.IsFull);

    public bool IsComplete => !Lines.Any(l => l.IsUncertain);

    public bool HasStaleValues => Lines.Any(l => l.IsStale);

    /// <summary>Savings, pension and investments: shown apart from spendable cash (spec §13).</summary>
    public Money LongTerm => Money.Sum(Lines.Where(l => l.Account is { } a && (a.Type is AccountType.Savings or AccountType.Pension or AccountType.Investment)).Select(l => l.PersonalValue));
}

/// <summary>Net worth at the end of a month (today for the current month).</summary>
public sealed record NetWorthPoint(DateOnly Month, DateOnly Date, Money Total, bool IsComplete);

/// <summary>Net worth over time. Months before the first account are left out; months with missing values are marked.</summary>
public sealed record NetWorthHistory(IReadOnlyList<NetWorthPoint> Points)
{
    public bool IsComplete => Points.All(p => p.IsComplete);
}

/// <summary>Calculates net worth now and over time from the ledger, valuations and ownership shares. Read-only.</summary>
public sealed class NetWorthService(
    IAccountRepository accounts,
    ITransactionRepository transactions,
    IAssetRepository assets,
    IValuationRepository valuations,
    IOwnershipRepository ownership,
    IClock clock)
{
    /// <summary>A manual value older than this is flagged for an update (spec §13 attention: stale valuations).</summary>
    public const int StaleAfterDays = 90;

    /// <summary>Months shown in the history, the current month included.</summary>
    public const int HistoryMonths = 12;

    public async Task<NetWorth> CalculateAsync(DateOnly? asOf = null, CancellationToken ct = default)
    {
        var data = await LoadAsync(ct);
        return Calculate(data, asOf ?? clock.Today, current: asOf is null || asOf == clock.Today);
    }

    public async Task<NetWorthHistory> HistoryAsync(int months = HistoryMonths, CancellationToken ct = default)
    {
        var data = await LoadAsync(ct);
        var today = clock.Today;
        if (data.Accounts.Count == 0 && data.Assets.Count == 0)
        {
            return new NetWorthHistory([]);
        }

        var start = Start(data);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var points = new List<NetWorthPoint>();
        for (var i = months - 1; i >= 0; i--)
        {
            var month = thisMonth.AddMonths(-i);
            var date = i == 0 ? today : month.AddMonths(1).AddDays(-1);
            if (start is { } s && date < s)
            {
                continue;
            }

            var worth = Calculate(data, date, current: i == 0);
            points.Add(new NetWorthPoint(month, date, worth.Total, worth.IsComplete));
        }

        return new NetWorthHistory(points);
    }

    private static DateOnly? Start(Data data)
    {
        var dates = data.Accounts.Select(a => a.OpeningDate).Concat(data.Valuations.Select(v => v.Date)).ToList();
        return dates.Count == 0 ? null : dates.Min();
    }

    private NetWorth Calculate(Data data, DateOnly date, bool current)
    {
        var lines = new List<NetWorthLine>();
        foreach (var account in data.Accounts)
        {
            if (Line(data, account, date, current) is { } line)
            {
                lines.Add(line);
            }
        }

        foreach (var asset in data.Assets)
        {
            if (Line(data, asset, date, current) is { } line)
            {
                lines.Add(line);
            }
        }

        return new NetWorth(date, lines);
    }

    private NetWorthLine? Line(Data data, Account account, DateOnly date, bool current)
    {
        var archived = account.Status == AccountStatus.Archived;
        var share = OwnershipHistory.ShareAt(data.Ownership, account.Id, account.OwnershipShare, date);
        var lines = data.Lines[account.Id];
        var balance = LedgerMath.Balance(account, lines, date);
        Money value;
        ValueSource source;
        DateOnly? valuedOn = null;
        var stale = false;
        if (account.Type.IsManuallyValued())
        {
            var latest = Valuation.LatestOn(data.Valuations, account.Id, date);
            if (latest is null)
            {
                if (date < account.OpeningDate)
                {
                    return null;
                }

                value = balance;
                source = ValueSource.LedgerWithoutValuation;
            }
            else
            {
                // Deposits and withdrawals after the valuation date change the value; market changes need a new valuation.
                var after = Money.Sum(lines.Where(t => t.IsActive && t.Date > latest.Date && t.Date <= date).Select(t => t.Amount));
                value = latest.Value + after;
                source = after.IsZero ? ValueSource.Valuation : ValueSource.ValuationPlusLedger;
                valuedOn = latest.Date;
                stale = current && !archived && latest.Date < date.AddDays(-StaleAfterDays);
            }
        }
        else
        {
            if (date < account.OpeningDate)
            {
                // Before its first balance an account only matters when it already held money then.
                if (account.OpeningBalance.IsZero)
                {
                    return null;
                }

                return new NetWorthLine(account.Name, account, null, account.Type.IsLiability(), Money.Zero, share, Money.Zero, ValueSource.NoValuation, null, false);
            }

            value = account.Type.IsLiability() ? balance.Negate() : balance;
            source = ValueSource.Ledger;
        }

        if (archived && value.IsZero)
        {
            return null;
        }

        return new NetWorthLine(account.Name, account, null, account.Type.IsLiability(), value, share, share.ApplyTo(value), source, valuedOn, stale);
    }

    private NetWorthLine? Line(Data data, Asset asset, DateOnly date, bool current)
    {
        var archived = asset.Status == AssetStatus.Archived;
        var share = OwnershipHistory.ShareAt(data.Ownership, asset.Id, asset.OwnershipShare, date);
        var latest = Valuation.LatestOn(data.Valuations, asset.Id, date);
        if (latest is null)
        {
            if (archived || (DateOnly.FromDateTime(asset.CreatedAt.ToLocalTime().Date) > date && !data.Valuations.Any(v => v.AssetId == asset.Id)))
            {
                return null;
            }

            return new NetWorthLine(asset.Name, null, asset, asset.IsLiability, Money.Zero, share, Money.Zero, ValueSource.NoValuation, null, false);
        }

        if (archived && latest.Value.IsZero)
        {
            return null;
        }

        var stale = current && !archived && latest.Date < date.AddDays(-StaleAfterDays);
        return new NetWorthLine(asset.Name, null, asset, asset.IsLiability, latest.Value, share, share.ApplyTo(latest.Value), ValueSource.Valuation, latest.Date, stale);
    }

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var accountList = await accounts.ListAsync(includeArchived: true, ct);
        var assetList = await assets.ListAsync(includeArchived: true, ct);
        var lines = await transactions.ListAsync(new TransactionQuery(), ct);
        return new Data(accountList, assetList, await valuations.ListAsync(ct), await ownership.ListAsync(ct), lines.ToLookup(t => t.AccountId));
    }

    private sealed record Data(
        IReadOnlyList<Account> Accounts,
        IReadOnlyList<Asset> Assets,
        IReadOnlyList<Valuation> Valuations,
        IReadOnlyList<OwnershipChange> Ownership,
        ILookup<Guid, Transaction> Lines);
}
