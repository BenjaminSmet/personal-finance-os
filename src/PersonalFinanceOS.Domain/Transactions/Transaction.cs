namespace PersonalFinanceOS.Domain.Transactions;

/// <summary>
/// One line in the ledger (spec §6). <see cref="Amount"/> is signed from the account's point of view:
/// money leaving the account is negative, money arriving is positive. For a credit card this means a
/// purchase makes the (already negative) balance more negative, i.e. more is owed (D-004).
/// </summary>
public sealed class Transaction
{
    public const int MaxDescriptionLength = 200;
    public const int MaxNoteLength = 1000;
    public const int MaxBankReferenceLength = 100;

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public DateOnly Date { get; private set; }
    public Money Amount { get; private set; }
    public TransactionType Type { get; private set; }

    /// <summary>Payee or short description. May be empty except for adjustments, where it is the reason.</summary>
    public string Description { get; private set; } = string.Empty;

    public Guid? CategoryId { get; private set; }
    public string? Note { get; private set; }
    public TransactionSource Source { get; private set; }

    /// <summary>Shared by both sides of a transfer, so they are always changed and voided together.</summary>
    public Guid? TransferId { get; private set; }

    public TransactionStatus Status { get; private set; }

    /// <summary>The bank-file import that created this line, so the whole import can be undone (spec §8).</summary>
    public Guid? ImportBatchId { get; private set; }

    /// <summary>The bank's own id for the line, when the export has one. Strongest duplicate signal.</summary>
    public string? BankReference { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == TransactionStatus.Active;
    public bool IsTransfer => Type == TransactionType.Transfer;

    // For the persistence layer.
    private Transaction()
    {
    }

    /// <summary>
    /// Income, expense or refund. <paramref name="amount"/> is the positive amount the user typed;
    /// the sign follows from the type.
    /// </summary>
    public static Transaction Create(
        Guid accountId,
        DateOnly date,
        Money amount,
        TransactionType type,
        string? description,
        Guid? categoryId,
        string? note,
        TransactionSource source,
        DateTimeOffset now)
    {
        if (!type.IsIncomeOrSpending())
        {
            throw new ArgumentException("Use CreateTransfer or CreateAdjustment for this type.", nameof(type));
        }

        var transaction = New(accountId, date, type, source, now);
        transaction.SetAmount(Signed(type, amount));
        transaction.SetDescription(description);
        transaction.SetNote(note);
        transaction.CategoryId = categoryId;
        return transaction;
    }

    /// <summary>Both sides of a transfer, linked by one <see cref="TransferId"/> (spec §9).</summary>
    public static (Transaction From, Transaction To) CreateTransfer(
        Guid fromAccountId,
        Guid toAccountId,
        DateOnly date,
        Money amount,
        string? description,
        string? note,
        TransactionSource source,
        DateTimeOffset now)
    {
        if (fromAccountId == toAccountId)
        {
            throw new DomainValidationException("transfer.sameAccount");
        }

        RequirePositive(amount);
        var transferId = Guid.NewGuid();
        var from = CreateTransferLeg(fromAccountId, date, amount.Negate(), transferId, description, note, source, now);
        var to = CreateTransferLeg(toAccountId, date, amount, transferId, description, note, source, now);
        return (from, to);
    }

    /// <summary>
    /// One side of a transfer whose other side already exists or is created separately, e.g. when an
    /// import links a bank line to a line already in the other account (spec §9).
    /// <paramref name="signedAmount"/> is negative for money leaving this account.
    /// </summary>
    public static Transaction CreateTransferLeg(
        Guid accountId,
        DateOnly date,
        Money signedAmount,
        Guid transferId,
        string? description,
        string? note,
        TransactionSource source,
        DateTimeOffset now)
    {
        if (signedAmount.IsZero)
        {
            throw new DomainValidationException("transaction.amount.zero");
        }

        var leg = New(accountId, date, TransactionType.Transfer, source, now);
        leg.TransferId = transferId;
        leg.SetAmount(signedAmount);
        leg.SetDescription(description);
        leg.SetNote(note);
        return leg;
    }

    /// <summary>
    /// An explicit balance correction (spec §5). The amount is signed; the reason is required so the
    /// correction is never silent.
    /// </summary>
    public static Transaction CreateAdjustment(Guid accountId, DateOnly date, Money signedAmount, string reason, DateTimeOffset now)
    {
        var transaction = New(accountId, date, TransactionType.Adjustment, TransactionSource.Reconciliation, now);
        if (signedAmount.IsZero)
        {
            throw new DomainValidationException("transaction.amount.zero");
        }

        transaction.Amount = signedAmount;
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException("transaction.adjustment.reasonRequired");
        }

        transaction.SetDescription(reason);
        return transaction;
    }

    /// <summary>Corrects an income, expense or refund. Transfers are changed with <see cref="EditTransferLeg"/>.</summary>
    public void Edit(Guid accountId, DateOnly date, Money amount, TransactionType type, string? description, Guid? categoryId, string? note, DateTimeOffset now)
    {
        if (!Type.IsIncomeOrSpending() || !type.IsIncomeOrSpending())
        {
            throw new InvalidOperationException("Only income, expenses and refunds can change type.");
        }

        RequireActive();
        SetAmount(Signed(type, amount));
        SetDescription(description);
        SetNote(note);
        AccountId = accountId;
        Date = date;
        Type = type;
        CategoryId = categoryId;
        UpdatedAt = now;
    }

    /// <summary>Changes one side of a transfer; the caller changes the other side in the same save.</summary>
    public void EditTransferLeg(Guid accountId, DateOnly date, Money amount, bool outgoing, string? description, string? note, DateTimeOffset now)
    {
        if (!IsTransfer)
        {
            throw new InvalidOperationException("Not a transfer.");
        }

        RequireActive();
        RequirePositive(amount);
        SetAmount(outgoing ? amount.Negate() : amount);
        SetDescription(description);
        SetNote(note);
        AccountId = accountId;
        Date = date;
        UpdatedAt = now;
    }

    /// <summary>Marks a new line as coming from a bank-file import. Only once, before it is saved.</summary>
    public void AttachImport(Guid batchId, string? bankReference)
    {
        if (ImportBatchId is not null)
        {
            throw new InvalidOperationException("This transaction already belongs to an import.");
        }

        var reference = string.IsNullOrWhiteSpace(bankReference) ? null : bankReference.Trim();
        if (reference is { Length: > MaxBankReferenceLength })
        {
            reference = reference[..MaxBankReferenceLength];
        }

        ImportBatchId = batchId;
        BankReference = reference;
        Source = TransactionSource.Import;
    }

    /// <summary>
    /// Turns an existing income, expense or refund into one side of a transfer, because the bank line
    /// for the other side showed it was a movement between own accounts (spec §9: no second copy).
    /// Amount, date and description stay as they are; the category no longer applies.
    /// </summary>
    public void LinkAsTransfer(Guid transferId, DateTimeOffset now)
    {
        RequireActive();
        if (!Type.IsIncomeOrSpending())
        {
            throw new DomainValidationException("transaction.wrongKind");
        }

        Type = TransactionType.Transfer;
        TransferId = transferId;
        CategoryId = null;
        UpdatedAt = now;
    }

    /// <summary>Reverses <see cref="LinkAsTransfer"/>: the line becomes what it was before.</summary>
    public void UnlinkTransfer(TransactionType type, Guid? categoryId, DateTimeOffset now)
    {
        if (!IsTransfer || !type.IsIncomeOrSpending() || (type == TransactionType.Expense) != Amount.IsNegative)
        {
            throw new DomainValidationException("transaction.wrongKind");
        }

        Type = type;
        TransferId = null;
        CategoryId = categoryId;
        UpdatedAt = now;
    }

    public void Void(DateTimeOffset now)
    {
        RequireActive();
        Status = TransactionStatus.Voided;
        UpdatedAt = now;
    }

    public void Restore(DateTimeOffset now)
    {
        Status = TransactionStatus.Active;
        UpdatedAt = now;
    }

    /// <summary>Everything the user can change, captured so an edit can be undone exactly.</summary>
    public TransactionSnapshot Snapshot() => new(AccountId, Date, Amount, Type, Description, CategoryId, Note, Status);

    public void RestoreSnapshot(TransactionSnapshot snapshot, DateTimeOffset now)
    {
        AccountId = snapshot.AccountId;
        Date = snapshot.Date;
        Amount = snapshot.Amount;
        Type = snapshot.Type;
        Description = snapshot.Description;
        CategoryId = snapshot.CategoryId;
        Note = snapshot.Note;
        Status = snapshot.Status;
        UpdatedAt = now;
    }

    private static Transaction New(Guid accountId, DateOnly date, TransactionType type, TransactionSource source, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Date = date,
        Type = type,
        Source = source,
        Status = TransactionStatus.Active,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Money Signed(TransactionType type, Money amount)
    {
        RequirePositive(amount);
        return type == TransactionType.Expense ? amount.Negate() : amount;
    }

    private static void RequirePositive(Money amount)
    {
        if (amount.IsZero)
        {
            throw new DomainValidationException("transaction.amount.zero");
        }

        if (amount.IsNegative)
        {
            throw new DomainValidationException("transaction.amount.negative");
        }
    }

    private void RequireActive()
    {
        if (!IsActive)
        {
            throw new DomainValidationException("transaction.voided");
        }
    }

    private void SetAmount(Money amount) => Amount = amount;

    private void SetDescription(string? description)
    {
        var trimmed = description?.Trim() ?? string.Empty;
        if (trimmed.Length > MaxDescriptionLength)
        {
            throw new DomainValidationException("transaction.description.tooLong");
        }

        Description = trimmed;
    }

    private void SetNote(string? note)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
        {
            throw new DomainValidationException("transaction.note.tooLong");
        }

        Note = trimmed;
    }
}

public sealed record TransactionSnapshot(
    Guid AccountId,
    DateOnly Date,
    Money Amount,
    TransactionType Type,
    string Description,
    Guid? CategoryId,
    string? Note,
    TransactionStatus Status);
