namespace PersonalFinanceOS.Domain.Planning;

/// <summary>What a recurring rule plans. Persisted as text, so never rename members.</summary>
public enum RecurringKind
{
    Income,
    Expense,
    Transfer,
}

/// <summary>Persisted as text, so never rename members.</summary>
public enum RecurrenceFrequency
{
    Weekly,
    Monthly,
    Quarterly,
    Yearly,
}

public enum RecurringRuleStatus
{
    Active,

    /// <summary>No new occurrences. Kept, because confirmed occurrences refer to it.</summary>
    Ended,
}

/// <summary>
/// A rule that creates expected events, never completed transactions (spec §12): salary,
/// subscriptions, insurance, loan payments, transfers to the joint account, yearly bills…
/// Occurrences only become real when the user confirms them.
/// </summary>
public sealed class RecurringRule
{
    public const int MaxNameLength = 100;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public RecurringKind Kind { get; private set; }

    /// <summary>Where the money comes in or goes out; for a transfer, the account it leaves.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>For a transfer: the account it arrives on.</summary>
    public Guid? ToAccountId { get; private set; }

    public Guid? CategoryId { get; private set; }

    /// <summary>Always positive; the kind gives the direction.</summary>
    public Money Amount { get; private set; }

    public RecurrenceFrequency Frequency { get; private set; }

    /// <summary>The first occurrence. Later occurrences keep its day (the month's last day when shorter).</summary>
    public DateOnly StartDate { get; private set; }

    /// <summary>No occurrences after this date. Null = open-ended.</summary>
    public DateOnly? EndDate { get; private set; }

    public RecurringRuleStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == RecurringRuleStatus.Active;

    // For the persistence layer.
    private RecurringRule()
    {
    }

    public static RecurringRule Create(
        string name,
        RecurringKind kind,
        Guid accountId,
        Guid? toAccountId,
        Guid? categoryId,
        Money amount,
        RecurrenceFrequency frequency,
        DateOnly startDate,
        DateOnly? endDate,
        DateTimeOffset now)
    {
        var rule = new RecurringRule
        {
            Id = Guid.NewGuid(),
            Status = RecurringRuleStatus.Active,
            CreatedAt = now,
        };
        rule.Change(name, kind, accountId, toAccountId, categoryId, amount, frequency, startDate, endDate, now);
        return rule;
    }

    public void Change(
        string name,
        RecurringKind kind,
        Guid accountId,
        Guid? toAccountId,
        Guid? categoryId,
        Money amount,
        RecurrenceFrequency frequency,
        DateOnly startDate,
        DateOnly? endDate,
        DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("recurring.name.required");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException("recurring.name.tooLong");
        }

        if (!amount.IsPositive)
        {
            throw new DomainValidationException("transaction.amount.zero");
        }

        if (kind == RecurringKind.Transfer)
        {
            if (toAccountId is null)
            {
                throw new DomainValidationException("recurring.toAccount.required");
            }

            if (toAccountId == accountId)
            {
                throw new DomainValidationException("transfer.sameAccount");
            }

            categoryId = null;
        }
        else
        {
            toAccountId = null;
        }

        if (endDate is { } end && end < startDate)
        {
            throw new DomainValidationException("recurring.endBeforeStart");
        }

        Name = trimmed;
        Kind = kind;
        AccountId = accountId;
        ToAccountId = toAccountId;
        CategoryId = categoryId;
        Amount = amount;
        Frequency = frequency;
        StartDate = startDate;
        EndDate = endDate;
        UpdatedAt = now;
    }

    /// <summary>Stops the rule from the given date on: occurrences after <paramref name="lastDate"/> no longer exist.</summary>
    public void End(DateOnly lastDate, DateTimeOffset now)
    {
        EndDate = lastDate < StartDate ? StartDate.AddDays(-1) : lastDate;
        Status = RecurringRuleStatus.Ended;
        UpdatedAt = now;
    }

    /// <summary>Occurrence dates between two dates (inclusive), always counted from <see cref="StartDate"/> so no day drifts.</summary>
    public IEnumerable<DateOnly> Occurrences(DateOnly from, DateOnly to)
    {
        var last = EndDate is { } end && end < to ? end : to;
        for (var n = 0; ; n++)
        {
            var date = Nth(n);
            if (date > last)
            {
                yield break;
            }

            if (date >= from)
            {
                yield return date;
            }
        }
    }

    private DateOnly Nth(int n) => Frequency switch
    {
        RecurrenceFrequency.Weekly => StartDate.AddDays(7 * n),
        RecurrenceFrequency.Monthly => StartDate.AddMonths(n),
        RecurrenceFrequency.Quarterly => StartDate.AddMonths(3 * n),
        _ => StartDate.AddYears(n),
    };
}
