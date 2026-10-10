using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Tests.Domain;

public class TransactionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateOnly Today = new(2026, 10, 10);
    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Theory]
    [InlineData(TransactionType.Expense, -4280)]
    [InlineData(TransactionType.Income, 4280)]
    [InlineData(TransactionType.Refund, 4280)]
    public void Typed_amount_is_positive_and_type_gives_the_direction(TransactionType type, long expected)
    {
        var t = Transaction.Create(Account, Today, Money.FromCents(4280), type, "Supermarkt", null, null, TransactionSource.Manual, Now);
        Assert.Equal(Money.FromCents(expected), t.Amount);
        Assert.True(t.IsActive);
    }

    [Theory]
    [InlineData(0, "transaction.amount.zero")]
    [InlineData(-100, "transaction.amount.negative")]
    public void Amount_must_be_positive(long cents, string code)
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Transaction.Create(Account, Today, Money.FromCents(cents), TransactionType.Expense, "x", null, null, TransactionSource.Manual, Now));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Transfer_creates_two_linked_sides_that_cancel_out()
    {
        var (from, to) = Transaction.CreateTransfer(Account, Other, Today, Money.FromCents(60_000), null, null, TransactionSource.Manual, Now);

        Assert.Equal(Money.FromCents(-60_000), from.Amount);
        Assert.Equal(Money.FromCents(60_000), to.Amount);
        Assert.NotNull(from.TransferId);
        Assert.Equal(from.TransferId, to.TransferId);
        Assert.Null(from.CategoryId);
        Assert.Equal(Money.Zero, from.Amount + to.Amount); // a transfer never changes the total
    }

    [Fact]
    public void Transfer_to_the_same_account_is_rejected()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Transaction.CreateTransfer(Account, Account, Today, Money.FromCents(100), null, null, TransactionSource.Manual, Now));
        Assert.Equal("transfer.sameAccount", ex.Code);
    }

    [Fact]
    public void Adjustment_needs_a_reason()
    {
        var ex = Assert.Throws<DomainValidationException>(() => Transaction.CreateAdjustment(Account, Today, Money.FromCents(-500), "  ", Now));
        Assert.Equal("transaction.adjustment.reasonRequired", ex.Code);

        var ok = Transaction.CreateAdjustment(Account, Today, Money.FromCents(-500), "Bank fee not entered", Now);
        Assert.Equal(TransactionType.Adjustment, ok.Type);
        Assert.Equal(Money.FromCents(-500), ok.Amount);
    }

    [Fact]
    public void Voided_transaction_cannot_be_edited_but_can_be_restored()
    {
        var t = Transaction.Create(Account, Today, Money.FromCents(100), TransactionType.Expense, "x", null, null, TransactionSource.Manual, Now);
        t.Void(Now);

        var ex = Assert.Throws<DomainValidationException>(() =>
            t.Edit(Account, Today, Money.FromCents(200), TransactionType.Expense, "y", null, null, Now));
        Assert.Equal("transaction.voided", ex.Code);

        t.Restore(Now);
        Assert.True(t.IsActive);
    }

    [Fact]
    public void Snapshot_restores_an_edit_exactly()
    {
        var t = Transaction.Create(Account, Today, Money.FromCents(1799), TransactionType.Expense, "Spotify", null, null, TransactionSource.QuickAdd, Now);
        var before = t.Snapshot();

        t.Edit(Other, Today.AddDays(-1), Money.FromCents(2000), TransactionType.Refund, "Spotify refund", null, "note", Now);
        Assert.NotEqual(before, t.Snapshot());

        t.RestoreSnapshot(before, Now);
        Assert.Equal(before, t.Snapshot());
    }

    [Theory]
    [InlineData("  Spotify   AB ", "spotify ab")]
    [InlineData("Café", "café")]
    [InlineData("   ", "")]
    public void Payee_keys_are_normalised(string payee, string expected) =>
        Assert.Equal(expected, PayeeRule.Normalize(payee));
}
