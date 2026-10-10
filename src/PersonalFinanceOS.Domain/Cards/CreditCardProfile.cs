namespace PersonalFinanceOS.Domain.Cards;

/// <summary>
/// The settings of one credit-card account (spec §10): limit, statement period and how the
/// statement is paid. Nothing here is hard-coded; the user enters it when creating the card and can
/// change it later. A changed setting only changes estimates, never recorded transactions.
/// </summary>
public sealed class CreditCardProfile
{
    public const int MinDay = 1;
    public const int MaxDay = 31;

    public Guid AccountId { get; private set; }

    /// <summary>A credit limit, not a spending budget and not available cash.</summary>
    public Money CreditLimit { get; private set; }

    /// <summary>
    /// Day of the month on which a new statement period starts; the period ends the day before
    /// (28 → 28th through the 27th). In shorter months a day past the month's end means its last day.
    /// </summary>
    public int StatementStartDay { get; private set; }

    /// <summary>Day of the month the statement is debited, the first such day after the period ends. Null when not known yet.</summary>
    public int? SettlementDay { get; private set; }

    /// <summary>The account that pays the statement automatically. Null when not set.</summary>
    public Guid? SettlementAccountId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private CreditCardProfile()
    {
    }

    public static CreditCardProfile Create(
        Guid accountId,
        Money creditLimit,
        int statementStartDay,
        int? settlementDay,
        Guid? settlementAccountId,
        DateTimeOffset now)
    {
        var profile = new CreditCardProfile { AccountId = accountId };
        profile.Change(creditLimit, statementStartDay, settlementDay, settlementAccountId, now);
        return profile;
    }

    public void Change(Money creditLimit, int statementStartDay, int? settlementDay, Guid? settlementAccountId, DateTimeOffset now)
    {
        if (creditLimit.IsNegative)
        {
            throw new DomainValidationException("card.limit.negative");
        }

        if (statementStartDay is < MinDay or > MaxDay)
        {
            throw new DomainValidationException("card.startDay.invalid");
        }

        if (settlementDay is < MinDay or > MaxDay)
        {
            throw new DomainValidationException("card.settlementDay.invalid");
        }

        if (settlementAccountId == AccountId)
        {
            throw new DomainValidationException("card.settlementAccount.invalid");
        }

        CreditLimit = creditLimit;
        StatementStartDay = statementStartDay;
        SettlementDay = settlementDay;
        SettlementAccountId = settlementAccountId;
        UpdatedAt = now;
    }

    public StatementPeriod PeriodOf(DateOnly date) => StatementCycle.PeriodOf(date, StatementStartDay);

    /// <summary>When the statement of <paramref name="period"/> is expected to be paid, if the day is known.</summary>
    public DateOnly? SettlementDateOf(StatementPeriod period) =>
        SettlementDay is { } day ? StatementCycle.FirstDayAfter(period.End, day) : null;
}

/// <summary>One statement period, both dates inclusive.</summary>
public readonly record struct StatementPeriod(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;
}

/// <summary>Statement-period arithmetic (spec §10 cycle logic), independent of month lengths and year ends.</summary>
public static class StatementCycle
{
    /// <summary>The period containing <paramref name="date"/> for a period that starts on <paramref name="startDay"/>.</summary>
    public static StatementPeriod PeriodOf(DateOnly date, int startDay)
    {
        var thisMonthStart = StartIn(date.Year, date.Month, startDay);
        var start = date >= thisMonthStart ? thisMonthStart : StartIn(date.AddMonths(-1).Year, date.AddMonths(-1).Month, startDay);
        var next = start.AddMonths(1);
        var end = StartIn(next.Year, next.Month, startDay).AddDays(-1);
        return new StatementPeriod(start, end);
    }

    public static StatementPeriod Previous(StatementPeriod period, int startDay) => PeriodOf(period.Start.AddDays(-1), startDay);

    public static StatementPeriod Next(StatementPeriod period, int startDay) => PeriodOf(period.End.AddDays(1), startDay);

    /// <summary>The first date after <paramref name="after"/> that falls on <paramref name="day"/> (or the month's last day when shorter).</summary>
    public static DateOnly FirstDayAfter(DateOnly after, int day)
    {
        var candidate = StartIn(after.Year, after.Month, day);
        if (candidate > after)
        {
            return candidate;
        }

        var next = after.AddMonths(1);
        return StartIn(next.Year, next.Month, day);
    }

    private static DateOnly StartIn(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
}
