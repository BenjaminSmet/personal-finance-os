using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Domain.Ledger;

/// <summary>Income and spending for a period. Transfers and adjustments are never part of it.</summary>
public readonly record struct PeriodTotals(Money Income, Money Expenses)
{
    /// <summary>Income minus spending ("net cash movement", spec §13).</summary>
    public Money Net => Income - Expenses;
}

/// <summary>
/// The authoritative balance and total rules (spec §6, §9, §10, §23). Every screen, report and the
/// assistant must use these, so a number is calculated the same way everywhere.
/// </summary>
public static class LedgerMath
{
    /// <summary>
    /// Opening balance plus every active transaction on the account up to and including
    /// <paramref name="asOf"/> (all of them when null). Voided transactions never count.
    /// </summary>
    public static Money Balance(Account account, IEnumerable<Transaction> transactions, DateOnly? asOf = null) =>
        account.OpeningBalance + Money.Sum(transactions
            .Where(t => t.AccountId == account.Id && t.IsActive && (asOf is null || t.Date <= asOf))
            .Select(t => t.Amount));

    /// <summary>
    /// Income and spending between two dates (inclusive). Refunds reduce spending and are never income.
    /// A card purchase counts once, on its purchase date; paying the card is a transfer and does not count.
    /// </summary>
    public static PeriodTotals Totals(IEnumerable<Transaction> transactions, DateOnly from, DateOnly to)
    {
        var income = Money.Zero;
        var expenses = Money.Zero;
        foreach (var t in transactions)
        {
            if (!t.IsActive || t.Date < from || t.Date > to)
            {
                continue;
            }

            switch (t.Type)
            {
                case TransactionType.Income:
                    income += t.Amount;
                    break;
                case TransactionType.Expense:
                    expenses -= t.Amount; // stored negative
                    break;
                case TransactionType.Refund:
                    expenses -= t.Amount; // stored positive: lowers spending
                    break;
            }
        }

        return new PeriodTotals(income, expenses);
    }
}
