using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Tests.Domain;

/// <summary>Spec §23 "Ledger and transfers", "VISA" and "Rounding" acceptance rules on the core maths.</summary>
public class LedgerMathTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateOnly Opening = new(2026, 10, 1);
    private static readonly DateOnly Day = new(2026, 10, 5);

    private static readonly Account Current = Account.Create("Zicht", AccountType.PersonalCurrent, Opening, Money.FromCents(200_000), Share.Full, Now);
    private static readonly Account Savings = Account.Create("Spaar 1", AccountType.Savings, Opening, Money.FromCents(50_000), Share.Full, Now);
    private static readonly Account Visa = Account.Create("VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Now);

    private static Transaction Tx(Account account, TransactionType type, long cents, DateOnly? date = null) =>
        Transaction.Create(account.Id, date ?? Day, Money.FromCents(cents), type, "x", null, null, TransactionSource.Manual, Now);

    private static IEnumerable<Transaction> Transfer(Account from, Account to, long cents)
    {
        var (a, b) = Transaction.CreateTransfer(from.Id, to.Id, Day, Money.FromCents(cents), null, null, TransactionSource.Manual, Now);
        return [a, b];
    }

    [Fact]
    public void Opening_balance_plus_transactions_equals_balance()
    {
        List<Transaction> ledger = [Tx(Current, TransactionType.Income, 240_000), Tx(Current, TransactionType.Expense, 4_280), Tx(Savings, TransactionType.Expense, 999)];

        Assert.Equal(Money.FromCents(200_000 + 240_000 - 4_280), LedgerMath.Balance(Current, ledger));
    }

    [Fact]
    public void Balance_as_of_a_date_ignores_later_transactions()
    {
        List<Transaction> ledger = [Tx(Current, TransactionType.Expense, 1_000, new DateOnly(2026, 10, 3)), Tx(Current, TransactionType.Expense, 2_000, new DateOnly(2026, 10, 4))];

        Assert.Equal(Money.FromCents(199_000), LedgerMath.Balance(Current, ledger, new DateOnly(2026, 10, 3)));
        Assert.Equal(Money.FromCents(200_000), LedgerMath.Balance(Current, ledger, new DateOnly(2026, 10, 2)));
    }

    [Fact]
    public void Voided_transactions_never_count()
    {
        var expense = Tx(Current, TransactionType.Expense, 5_000);
        expense.Void(Now);

        Assert.Equal(Current.OpeningBalance, LedgerMath.Balance(Current, [expense]));
        Assert.Equal(Money.Zero, LedgerMath.Totals([expense], Opening, Day).Expenses);
    }

    [Fact]
    public void Transfer_changes_both_accounts_equally_and_is_not_income_or_spending()
    {
        var ledger = Transfer(Current, Savings, 60_000).ToList();

        Assert.Equal(Money.FromCents(140_000), LedgerMath.Balance(Current, ledger));
        Assert.Equal(Money.FromCents(110_000), LedgerMath.Balance(Savings, ledger));
        Assert.Equal(new PeriodTotals(Money.Zero, Money.Zero), LedgerMath.Totals(ledger, Opening, Day));
    }

    [Fact]
    public void Visa_purchase_is_an_expense_and_debt_without_touching_the_current_account()
    {
        List<Transaction> ledger = [Tx(Visa, TransactionType.Expense, 40_000)];

        Assert.Equal(Money.FromCents(40_000), LedgerMath.Totals(ledger, Opening, Day).Expenses);
        Assert.Equal(Money.FromCents(-40_000), LedgerMath.Balance(Visa, ledger)); // €400 outstanding
        Assert.Equal(Current.OpeningBalance, LedgerMath.Balance(Current, ledger));
    }

    [Fact]
    public void Visa_settlement_reduces_cash_and_debt_without_a_second_expense()
    {
        var ledger = new List<Transaction> { Tx(Visa, TransactionType.Expense, 40_000) };
        ledger.AddRange(Transfer(Current, Visa, 40_000));

        Assert.Equal(Money.FromCents(40_000), LedgerMath.Totals(ledger, Opening, Day).Expenses); // still once
        Assert.Equal(Money.Zero, LedgerMath.Balance(Visa, ledger));
        Assert.Equal(Money.FromCents(160_000), LedgerMath.Balance(Current, ledger));
    }

    [Fact]
    public void Rounding_transfer_is_not_an_extra_expense()
    {
        var ledger = new List<Transaction> { Tx(Current, TransactionType.Expense, 295) };
        ledger.AddRange(Transfer(Current, Savings, 5));

        Assert.Equal(Money.FromCents(295), LedgerMath.Totals(ledger, Opening, Day).Expenses);
        Assert.Equal(Money.FromCents(200_000 - 300), LedgerMath.Balance(Current, ledger));
        Assert.Equal(Money.FromCents(50_005), LedgerMath.Balance(Savings, ledger));
    }

    [Fact]
    public void Refund_lowers_spending_and_is_never_income()
    {
        List<Transaction> ledger = [Tx(Visa, TransactionType.Expense, 10_000), Tx(Visa, TransactionType.Refund, 2_500)];
        var totals = LedgerMath.Totals(ledger, Opening, Day);

        Assert.Equal(Money.FromCents(7_500), totals.Expenses);
        Assert.Equal(Money.Zero, totals.Income);
        Assert.Equal(Money.FromCents(-7_500), LedgerMath.Balance(Visa, ledger));
    }

    [Fact]
    public void Loan_payment_is_one_ordinary_expense()
    {
        List<Transaction> ledger = [Tx(Current, TransactionType.Expense, 85_000)];
        Assert.Equal(Money.FromCents(85_000), LedgerMath.Totals(ledger, Opening, Day).Expenses);
    }

    [Fact]
    public void Adjustments_change_the_balance_but_not_income_or_spending()
    {
        List<Transaction> ledger = [Transaction.CreateAdjustment(Current.Id, Day, Money.FromCents(-1_250), "Bank costs", Now)];

        Assert.Equal(Money.FromCents(198_750), LedgerMath.Balance(Current, ledger));
        Assert.Equal(new PeriodTotals(Money.Zero, Money.Zero), LedgerMath.Totals(ledger, Opening, Day));
    }

    [Fact]
    public void Totals_respect_the_date_range_and_net_is_income_minus_spending()
    {
        List<Transaction> ledger =
        [
            Tx(Current, TransactionType.Income, 240_000, new DateOnly(2026, 10, 1)),
            Tx(Current, TransactionType.Expense, 50_000, new DateOnly(2026, 10, 31)),
            Tx(Current, TransactionType.Expense, 9_999, new DateOnly(2026, 11, 1)),
        ];

        var october = LedgerMath.Totals(ledger, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal(Money.FromCents(240_000), october.Income);
        Assert.Equal(Money.FromCents(50_000), october.Expenses);
        Assert.Equal(Money.FromCents(190_000), october.Net);
    }
}
