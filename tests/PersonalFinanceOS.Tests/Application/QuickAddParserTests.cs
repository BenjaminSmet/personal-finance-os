using PersonalFinanceOS.Application.QuickAdd;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Tests.Application;

/// <summary>Spec §7 examples and §23 "Quick Add supports common decimal formats".</summary>
public class QuickAddParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateOnly Today = new(2026, 10, 10);
    private static readonly DateOnly Opening = new(2026, 1, 1);

    private static readonly Account Current = Account.Create("Zichtrekening", AccountType.PersonalCurrent, Opening, Money.Zero, Share.Full, Now);
    private static readonly Account Joint = Account.Create("Gezamenlijke rekening", AccountType.JointCurrent, Opening, Money.Zero, new Share(5000), Now);
    private static readonly Account Savings1 = Account.Create("Spaarrekening 1", AccountType.Savings, Opening, Money.Zero, Share.Full, Now);
    private static readonly Account Savings2 = Account.Create("Spaarrekening 2", AccountType.Savings, Opening, Money.Zero, Share.Full, Now);
    private static readonly Account Visa = Account.Create("VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Now);

    private static readonly Category Subscriptions = Category.Create("Abonnementen", CategoryKind.Expense, 0, Now);
    private static readonly Category Salary = Category.Create("Loon", CategoryKind.Income, 1, Now);

    private static QuickAddContext Context(Guid? lastUsed = null, params PayeeRule[] rules) =>
        new([Current, Joint, Savings1, Savings2, Visa], [Subscriptions, Salary], rules, lastUsed, Today);

    private static QuickAddDraft Parse(string text, QuickAddContext? context = null) => QuickAddParser.Parse(text, context ?? Context());

    [Theory]
    [InlineData("Supermarkt 42,80", "Supermarkt", 4280)]
    [InlineData("Tankstation 65", "Tankstation", 6500)]
    [InlineData("Spotify 17,99", "Spotify", 1799)]
    [InlineData("Koffie 3,20", "Koffie", 320)]
    [InlineData("Koffie 3.20", "Koffie", 320)]
    [InlineData("Koffie €3,20", "Koffie", 320)]
    [InlineData("Koffie 3,20€", "Koffie", 320)]
    [InlineData("Koffie € 3,20", "Koffie", 320)]
    [InlineData("42,80 Supermarkt", "Supermarkt", 4280)]
    [InlineData("Nieuwe zetel 1.234,56", "Nieuwe zetel", 123_456)]
    [InlineData("New sofa 1,234.56", "New sofa", 123_456)]
    public void Clear_expenses_are_read_and_can_be_confirmed_straight_away(string input, string description, long cents)
    {
        var draft = Parse(input);

        Assert.Equal(TransactionType.Expense, draft.Type);
        Assert.Equal(description, draft.Description);
        Assert.Equal(Money.FromCents(cents), draft.Amount);
        Assert.Equal(Today, draft.Date);
        Assert.Equal(Current.Id, draft.AccountId);
        Assert.False(draft.NeedsReview);
    }

    [Theory]
    [InlineData("Loon 2400")]
    [InlineData("Salary 2400")]
    [InlineData("Bijverdienste +2400")]
    public void Income_is_recognised(string input)
    {
        var draft = Parse(input);
        Assert.Equal(TransactionType.Income, draft.Type);
        Assert.Equal(Money.FromCents(240_000), draft.Amount);
    }

    [Fact]
    public void Refund_is_not_income()
    {
        var draft = Parse("Terugbetaling Zalando 25 met visa");
        Assert.Equal(TransactionType.Refund, draft.Type);
        Assert.Equal(Visa.Id, draft.AccountId);
    }

    [Theory]
    [InlineData("Transfer 600 naar gezamenlijke rekening")]
    [InlineData("Overschrijving 600 naar gezamenlijke")]
    [InlineData("600 naar Gezamenlijke rekening")]
    [InlineData("transfer 600 to joint")]
    public void Transfer_suggests_source_and_destination(string input)
    {
        var draft = Parse(input);

        Assert.Equal(TransactionType.Transfer, draft.Type);
        Assert.Equal(Money.FromCents(60_000), draft.Amount);
        Assert.Equal(Current.Id, draft.AccountId);
        Assert.Equal(Joint.Id, draft.DestinationAccountId);
        Assert.Empty(draft.Description);
        Assert.False(draft.NeedsReview);
    }

    [Fact]
    public void Numbers_in_account_names_are_not_read_as_the_amount()
    {
        var draft = Parse("Transfer 250 naar spaarrekening 2");
        Assert.Equal(Money.FromCents(25_000), draft.Amount);
        Assert.Equal(Savings2.Id, draft.DestinationAccountId);
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void Transfer_with_explicit_source()
    {
        var draft = Parse("Transfer 100 van gezamenlijke rekening naar spaarrekening 1");
        Assert.Equal(Joint.Id, draft.AccountId);
        Assert.Equal(Savings1.Id, draft.DestinationAccountId);
    }

    [Fact]
    public void Visa_settlement_is_a_transfer_to_the_card()
    {
        var draft = Parse("Transfer 400 naar visa");
        Assert.Equal(TransactionType.Transfer, draft.Type);
        Assert.Equal(Visa.Id, draft.DestinationAccountId);
        Assert.Equal(Current.Id, draft.AccountId);
    }

    [Fact]
    public void Transfer_without_a_known_destination_needs_review()
    {
        var draft = Parse("Transfer 600");
        Assert.Contains(QuickAddIssue.DestinationUnknown, draft.Issues);
        Assert.True(draft.NeedsReview);
    }

    [Fact]
    public void Ambiguous_saving_account_needs_review()
    {
        // "spaarrekening" matches two accounts; nothing is guessed.
        var draft = Parse("Transfer 50 naar spaarrekening");
        Assert.Null(draft.DestinationAccountId);
        Assert.True(draft.NeedsReview);
    }

    [Theory]
    [InlineData("Spotify 17,99 visa")]
    [InlineData("Spotify 17,99 met visa")]
    [InlineData("Spotify 17,99 with VISA")]
    public void Card_word_selects_the_card(string input)
    {
        var draft = Parse(input);
        Assert.Equal(Visa.Id, draft.AccountId);
        Assert.Equal("Spotify", draft.Description);
    }

    [Fact]
    public void Expense_from_a_named_account()
    {
        var draft = Parse("Delhaize 85,10 van gezamenlijke rekening");
        Assert.Equal(TransactionType.Expense, draft.Type);
        Assert.Equal(Joint.Id, draft.AccountId);
        Assert.Equal("Delhaize", draft.Description);
    }

    [Fact]
    public void Payee_names_that_start_with_van_stay_the_payee()
    {
        var draft = Parse("Van Marcke 120");
        Assert.Equal("Van Marcke", draft.Description);
        Assert.Equal(TransactionType.Expense, draft.Type);
    }

    [Theory]
    [InlineData("Koffie 3,20 gisteren", 2026, 10, 9)]
    [InlineData("Koffie 3,20 yesterday", 2026, 10, 9)]
    [InlineData("Koffie 3,20 eergisteren", 2026, 10, 8)]
    [InlineData("Koffie 3,20 5/10", 2026, 10, 5)]
    [InlineData("Koffie 3,20 05-10-2026", 2026, 10, 5)]
    [InlineData("Koffie 3,20 28/12", 2025, 12, 28)]
    public void Dates_are_read(string input, int y, int m, int d)
    {
        var draft = Parse(input);
        Assert.Equal(new DateOnly(y, m, d), draft.Date);
        Assert.Equal("Koffie", draft.Description);
        Assert.Equal(Money.FromCents(320), draft.Amount);
    }

    [Fact]
    public void Future_date_needs_review()
    {
        var draft = Parse("Koffie 3,20 12/10/2026");
        Assert.Contains(QuickAddIssue.FutureDate, draft.Issues);
    }

    [Theory]
    [InlineData("Supermarkt", QuickAddIssue.AmountMissing)]
    [InlineData("Zetel 1.234", QuickAddIssue.AmountAmbiguous)]
    [InlineData("Delhaize 365 12,50", QuickAddIssue.SeveralAmounts)]
    public void Uncertain_input_is_shown_for_review(string input, QuickAddIssue issue)
    {
        var draft = Parse(input);
        Assert.Contains(issue, draft.Issues);
        Assert.True(draft.NeedsReview);
    }

    [Fact]
    public void Several_amounts_take_the_last_one_and_keep_the_rest_in_the_description()
    {
        var draft = Parse("Delhaize 365 12,50");
        Assert.Equal(Money.FromCents(1250), draft.Amount);
        Assert.Equal("Delhaize 365", draft.Description);
    }

    [Fact]
    public void Learned_rule_suggests_category_and_account()
    {
        var rule = PayeeRule.Learn("Spotify", TransactionType.Expense, Subscriptions.Id, Visa.Id, Now);
        var draft = Parse("spotify 17,99", Context(rules: rule));

        Assert.Equal(Subscriptions.Id, draft.CategoryId);
        Assert.Equal(Visa.Id, draft.AccountId);
    }

    [Fact]
    public void Typed_account_wins_over_the_rule()
    {
        var rule = PayeeRule.Learn("Spotify", TransactionType.Expense, Subscriptions.Id, Visa.Id, Now);
        var draft = Parse("Spotify 17,99 van gezamenlijke rekening", Context(rules: rule));
        Assert.Equal(Joint.Id, draft.AccountId);
        Assert.Equal(Subscriptions.Id, draft.CategoryId);
    }

    [Fact]
    public void Last_used_account_is_the_default()
    {
        var draft = Parse("Bakker 4,50", Context(lastUsed: Joint.Id));
        Assert.Equal(Joint.Id, draft.AccountId);
    }

    [Fact]
    public void Income_never_defaults_to_the_credit_card()
    {
        var draft = Parse("Loon 2400", Context(lastUsed: Visa.Id));
        Assert.Equal(Current.Id, draft.AccountId);
    }

    [Fact]
    public void No_accounts_means_review()
    {
        var draft = QuickAddParser.Parse("Koffie 3", new QuickAddContext([], [], [], null, Today));
        Assert.Contains(QuickAddIssue.NoAccount, draft.Issues);
        Assert.False(draft.IsComplete);
    }

    [Fact]
    public void Empty_input_is_incomplete()
    {
        var draft = Parse("   ");
        Assert.False(draft.IsComplete);
        Assert.Contains(QuickAddIssue.AmountMissing, draft.Issues);
    }
}
