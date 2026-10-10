namespace PersonalFinanceOS.Domain.Transactions;

/// <summary>
/// What a ledger line means (spec §6). Values are persisted as text, so never rename members.
///
/// The spec's credit-card types are expressed through the account rather than as separate types,
/// so double counting is impossible by construction (decision log D-013):
/// a card purchase is an <see cref="Expense"/> on a credit-card account, and a card settlement is a
/// <see cref="Transfer"/> from a current account to the card. Opening balances stay on the account.
/// </summary>
public enum TransactionType
{
    /// <summary>Money coming in (salary…). Always positive. Not allowed on a credit card.</summary>
    Income,

    /// <summary>Money spent. Always negative on the account it was paid from.</summary>
    Expense,

    /// <summary>Money returned for an earlier expense. Positive; reduces spending, never counted as income.</summary>
    Refund,

    /// <summary>One side of a movement between two own accounts. Never income or spending.</summary>
    Transfer,

    /// <summary>Explicit correction after reconciliation, with a reason. Never income or spending.</summary>
    Adjustment,
}

public enum TransactionStatus
{
    Active,

    /// <summary>Removed by the user. Kept (not deleted) so it can be undone and audited.</summary>
    Voided,
}

/// <summary>Where a transaction came from.</summary>
public enum TransactionSource
{
    Manual,
    QuickAdd,
    Reconciliation,

    /// <summary>Read from a bank file; <see cref="Transaction.ImportBatchId"/> names the import.</summary>
    Import,
}

public static class TransactionTypeRules
{
    /// <summary>Types that carry a category and count towards income or spending.</summary>
    public static bool IsIncomeOrSpending(this TransactionType type) =>
        type is TransactionType.Income or TransactionType.Expense or TransactionType.Refund;

    /// <summary>The category kind a type may use, or null when the type takes no category.</summary>
    public static Categories.CategoryKind? CategoryKind(this TransactionType type) => type switch
    {
        TransactionType.Income => Categories.CategoryKind.Income,
        TransactionType.Expense or TransactionType.Refund => Categories.CategoryKind.Expense,
        _ => null,
    };
}
