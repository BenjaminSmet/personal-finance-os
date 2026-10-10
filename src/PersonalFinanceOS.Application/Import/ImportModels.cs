using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Imports;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Import;

/// <summary>What the preview found for one line (spec §8 step 8).</summary>
public enum ImportRowStatus
{
    /// <summary>Not in the ledger yet. Imported unless the user unticks it.</summary>
    New,

    /// <summary>Almost certainly already in the ledger (same bank reference, or same date, amount and text). Skipped unless ticked.</summary>
    Duplicate,

    /// <summary>A line with the same amount a few days apart that was typed in by hand. Skipped unless ticked.</summary>
    PossibleDuplicate,

    /// <summary>Dated before the account's balance date, so already part of the opening balance. Never imported.</summary>
    BeforeOpening,

    /// <summary>Date or amount unreadable, foreign currency, zero… Never imported; the reason is shown.</summary>
    Invalid,
}

/// <summary>Why the type, category or transfer account was pre-filled.</summary>
public enum ImportSuggestion
{
    None,

    /// <summary>A payee rule learned from earlier confirmed entries.</summary>
    PayeeRule,

    /// <summary>The text names another own account, so it is probably a transfer.</summary>
    OwnAccount,

    /// <summary>Money arriving on a credit card that reads like a payment of the card.</summary>
    CardPayment,

    /// <summary>A bank rounding transfer to savings (spec §11): a transfer, never spending.</summary>
    Rounding,

    /// <summary>Reads like a rounding transfer, but the savings account or the amount is unclear. Left unticked for review.</summary>
    RoundingUncertain,
}

/// <summary>
/// One line of the preview with the user's choices. Nothing is saved until the whole preview is
/// confirmed (spec §8 step 9); the choices here are only suggestions until then.
/// </summary>
public sealed class ImportCandidate
{
    internal ImportCandidate(ParsedRow row, string description, string? note)
    {
        Row = row;
        Description = description;
        Note = note;
    }

    public ParsedRow Row { get; }

    public int RowNumber => Row.RowNumber;

    /// <summary>Valid for every status except <see cref="ImportRowStatus.Invalid"/>.</summary>
    public DateOnly Date => Row.Date ?? default;

    /// <summary>Signed from the account's point of view, as in the bank file.</summary>
    public Money Amount => Row.Amount ?? Money.Zero;

    /// <summary>Counterparty name when the file has one, otherwise the bank's description.</summary>
    public string Description { get; }

    /// <summary>The bank's description when it adds something to <see cref="Description"/>.</summary>
    public string? Note { get; }

    public ImportRowStatus Status { get; internal set; }

    /// <summary>Error code for invalid lines.</summary>
    public string? Problem => Row.Problem;

    /// <summary>The ledger line this one seems to repeat.</summary>
    public Guid? MatchedTransactionId { get; internal set; }

    /// <summary>The matching line was removed by the user earlier.</summary>
    public bool MatchWasRemoved { get; internal set; }

    public bool CanInclude => Status is ImportRowStatus.New or ImportRowStatus.Duplicate or ImportRowStatus.PossibleDuplicate;

    private bool _include;

    public bool Include
    {
        get => _include && CanInclude;
        set => _include = value && CanInclude;
    }

    /// <summary>Income, Expense, Refund or Transfer.</summary>
    public TransactionType Type { get; set; }

    public Guid? CategoryId { get; set; }

    /// <summary>For a transfer: the other own account. Money leaves whichever side has the negative amount.</summary>
    public Guid? TransferAccountId { get; set; }

    public ImportSuggestion Suggestion { get; internal set; }

    internal TransactionType SuggestedType { get; set; }

    internal Guid? SuggestedCategoryId { get; set; }

    /// <summary>The payee name a learned rule is stored under: the counterparty, never a whole bank description.</summary>
    internal string LearnKey => Row.Counterparty;
}

/// <summary>The full preview of one file for one account.</summary>
public sealed class ImportPreview
{
    internal ImportPreview(Account account, ImportFile file, ImportMapping mapping, IReadOnlyList<ImportCandidate> candidates)
    {
        Account = account;
        File = file;
        Mapping = mapping;
        Candidates = candidates;
    }

    public Account Account { get; }
    public ImportFile File { get; }
    public ImportMapping Mapping { get; }
    public IReadOnlyList<ImportCandidate> Candidates { get; }

    /// <summary>Set once confirmed, so a double click can never import the same lines twice.</summary>
    public bool IsConfirmed { get; internal set; }

    public int Count(ImportRowStatus status) => Candidates.Count(c => c.Status == status);

    public int IncludedCount => Candidates.Count(c => c.Include);

    public DateOnly? FirstDate => Candidates.Where(c => c.Status != ImportRowStatus.Invalid).Select(c => (DateOnly?)c.Date).Min();

    public DateOnly? LastDate => Candidates.Where(c => c.Status != ImportRowStatus.Invalid).Select(c => (DateOnly?)c.Date).Max();
}

/// <summary>A choice in the preview that cannot be saved, with the line it is on.</summary>
public sealed class ImportRowException(string code, int rowNumber) : Exception(code)
{
    public string Code { get; } = code;

    public int RowNumber { get; } = rowNumber;
}

public sealed record ImportResult(ImportBatch Batch, int Imported, int Linked, int NowDuplicate);

public sealed record ImportBatchInfo(ImportBatch Batch, Account Account);

/// <summary>
/// What undoing an import would do (spec §8 "safe batch undo… guided recovery"). Lines changed
/// since the import are counted separately so the user decides about them explicitly.
/// </summary>
public sealed record BatchUndoPlan(ImportBatch Batch, int Unchanged, int Changed, int AlreadyRemoved, int Linked)
{
    public bool NeedsChoice => Changed > 0;
}
