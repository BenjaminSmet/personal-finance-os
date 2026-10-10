namespace PersonalFinanceOS.Domain.Planning;

/// <summary>What the user decided about one occurrence. Persisted as text, so never rename members.</summary>
public enum ScheduledItemStatus
{
    /// <summary>It happened: <see cref="ScheduledItem.TransactionId"/> is the real transaction.</summary>
    Confirmed,

    /// <summary>Moved to <see cref="ScheduledItem.PlannedDate"/>, still expected.</summary>
    Postponed,

    /// <summary>Will not happen this time.</summary>
    Skipped,
}

/// <summary>
/// The user's decision about one occurrence of a <see cref="RecurringRule"/> (spec §12). Occurrences
/// without a decision are open and are calculated from the rule, so nothing is stored for them.
/// The link to the real transaction is what stops a planned and an actual item counting twice.
/// </summary>
public sealed class ScheduledItem
{
    public Guid Id { get; private set; }
    public Guid RuleId { get; private set; }

    /// <summary>The date the rule planned. Together with <see cref="RuleId"/> it names the occurrence.</summary>
    public DateOnly OccurrenceDate { get; private set; }

    public ScheduledItemStatus Status { get; private set; }

    /// <summary>For a postponed occurrence: the new expected date.</summary>
    public DateOnly? PlannedDate { get; private set; }

    /// <summary>For a confirmed occurrence: the transaction (for a transfer, the side leaving the account).</summary>
    public Guid? TransactionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private ScheduledItem()
    {
    }

    public static ScheduledItem For(Guid ruleId, DateOnly occurrenceDate, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        RuleId = ruleId,
        OccurrenceDate = occurrenceDate,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Confirm(Guid transactionId, DateTimeOffset now)
    {
        Status = ScheduledItemStatus.Confirmed;
        TransactionId = transactionId;
        PlannedDate = null;
        UpdatedAt = now;
    }

    public void Postpone(DateOnly plannedDate, DateTimeOffset now)
    {
        if (plannedDate < OccurrenceDate)
        {
            throw new DomainValidationException("scheduled.postpone.beforeDue");
        }

        Status = ScheduledItemStatus.Postponed;
        PlannedDate = plannedDate;
        TransactionId = null;
        UpdatedAt = now;
    }

    public void Skip(DateTimeOffset now)
    {
        Status = ScheduledItemStatus.Skipped;
        PlannedDate = null;
        TransactionId = null;
        UpdatedAt = now;
    }
}
