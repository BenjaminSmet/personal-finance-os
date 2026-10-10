namespace PersonalFinanceOS.Domain.Ledger;

/// <summary>
/// The user compared the app's balance with a bank statement (spec §5). Kept as history: a
/// difference is shown, never hidden; an adjustment is only made when the user explicitly asks.
/// </summary>
public sealed class ReconciliationRecord
{
    public const int MaxNoteLength = 1000;

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public DateOnly StatementDate { get; private set; }
    public Money StatementBalance { get; private set; }

    /// <summary>The app's balance at the end of <see cref="StatementDate"/> before any adjustment.</summary>
    public Money CalculatedBalance { get; private set; }

    /// <summary>Set when the user chose to record the difference as an explicit adjustment.</summary>
    public Guid? AdjustmentTransactionId { get; private set; }

    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Statement minus app. Positive: the bank shows more than the app.</summary>
    public Money Difference => StatementBalance - CalculatedBalance;

    public bool IsMatched => Difference.IsZero || AdjustmentTransactionId is not null;

    // For the persistence layer.
    private ReconciliationRecord()
    {
    }

    public static ReconciliationRecord Create(
        Guid accountId,
        DateOnly statementDate,
        Money statementBalance,
        Money calculatedBalance,
        Guid? adjustmentTransactionId,
        string? note,
        DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
        {
            throw new DomainValidationException("transaction.note.tooLong");
        }

        return new ReconciliationRecord
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            StatementDate = statementDate,
            StatementBalance = statementBalance,
            CalculatedBalance = calculatedBalance,
            AdjustmentTransactionId = adjustmentTransactionId,
            Note = trimmed,
            CreatedAt = now,
        };
    }
}
