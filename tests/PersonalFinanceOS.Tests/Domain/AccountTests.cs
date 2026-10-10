using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;

namespace PersonalFinanceOS.Tests.Domain;

public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateOnly Today = new(2026, 10, 10);

    [Fact]
    public void Create_sets_all_fields()
    {
        var account = Account.Create("  Zichtrekening  ", AccountType.PersonalCurrent, Today, Money.FromCents(150000), Share.Full, Now, "salaris");

        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.Equal("Zichtrekening", account.Name);
        Assert.Equal("EUR", account.Currency);
        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.Equal(Money.FromCents(150000), account.OpeningBalance);
        Assert.Equal("salaris", account.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string name)
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Account.Create(name, AccountType.Savings, Today, Money.Zero, Share.Full, Now));
        Assert.Equal("account.name.required", ex.Code);
    }

    [Fact]
    public void Credit_card_balance_is_a_debt()
    {
        var card = Account.Create("VISA", AccountType.CreditCard, Today, Money.FromCents(-40000), Share.Full, Now);
        Assert.True(card.OpeningBalance.IsNegative);
        Assert.True(card.Type.IsLiability());
        Assert.False(card.Type.IsLiquid()); // available credit is never available cash

        var ex = Assert.Throws<DomainValidationException>(() =>
            Account.Create("VISA", AccountType.CreditCard, Today, Money.FromCents(100), Share.Full, Now));
        Assert.Equal("account.opening.creditCardPositive", ex.Code);
    }

    [Fact]
    public void Joint_account_keeps_full_balance_and_personal_part_separate()
    {
        var joint = Account.Create("Gezamenlijk", AccountType.JointCurrent, Today, Money.FromCents(200000), new Share(5000), Now);

        Assert.Equal(Money.FromCents(200000), joint.OpeningBalance);
        Assert.Equal(Money.FromCents(100000), joint.PersonalPart(joint.OpeningBalance));
    }

    [Fact]
    public void Zero_share_is_rejected()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Account.Create("X", AccountType.JointCurrent, Today, Money.Zero, Share.None, Now));
        Assert.Equal("account.share.zero", ex.Code);
    }

    [Fact]
    public void Archive_and_reactivate_update_status_and_timestamp()
    {
        var account = Account.Create("Spaar", AccountType.Savings, Today, Money.Zero, Share.Full, Now);
        var later = Now.AddDays(1);

        account.Archive(later);
        Assert.Equal(AccountStatus.Archived, account.Status);
        Assert.Equal(later, account.UpdatedAt);

        account.Reactivate(later);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Liquid_types_exclude_savings_and_investments()
    {
        Assert.True(AccountType.PersonalCurrent.IsLiquid());
        Assert.True(AccountType.JointCurrent.IsLiquid());
        Assert.True(AccountType.Cash.IsLiquid());
        Assert.False(AccountType.Savings.IsLiquid());
        Assert.False(AccountType.Investment.IsLiquid());
        Assert.True(AccountType.Pension.IsManuallyValued());
    }
}
