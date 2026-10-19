using PersonalFinanceOS.Application.Assistant;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Tests.Application;

/// <summary>Spec §15: the assistant's ten initial questions in Dutch and English, and the values it reads from them.</summary>
public class QuestionParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateOnly Today = new(2026, 10, 10);

    private static readonly Category Groceries = Category.Create("Boodschappen", CategoryKind.Expense, 0, Now);
    private static readonly Category Subscriptions = Category.Create("Abonnementen", CategoryKind.Expense, 1, Now);
    private static readonly Category Renovation = Category.Create("Renovatie", CategoryKind.Expense, 2, Now);
    private static readonly Category Salary = Category.Create("Loon", CategoryKind.Income, 3, Now);

    private static readonly WishlistItem Laptop = WishlistItem.Create("Nieuwe laptop", Money.FromCents(120_000), WishlistPriority.Normal, null, null, null, null, null, Now);
    private static readonly WishlistItem Sofa = WishlistItem.Create("Zetel", Money.FromCents(90_000), WishlistPriority.Low, null, null, null, null, null, Now);

    private static readonly RecurringRule Rent = RecurringRule.Create("Huur", RecurringKind.Expense, Guid.NewGuid(), null, null, Money.FromCents(90_000), RecurrenceFrequency.Monthly, new(2026, 1, 15), null, Now);
    private static readonly RecurringRule Pay = RecurringRule.Create("Loon", RecurringKind.Income, Guid.NewGuid(), null, null, Money.FromCents(240_000), RecurrenceFrequency.Monthly, new(2026, 1, 25), null, Now);

    private static ParsedQuestion Parse(string text) =>
        QuestionParser.Parse(text, Today, [Groceries, Subscriptions, Renovation, Salary], [Laptop, Sofa], [Rent, Pay]);

    // ===== The ten initial questions (spec §15), both languages =====

    [Theory]
    [InlineData("Can I afford this?", AssistantIntent.CanAfford)]
    [InlineData("Kan ik dit betalen?", AssistantIntent.CanAfford)]
    [InlineData("Can I spend another €100 on groceries this month?", AssistantIntent.CategoryRoom)]
    [InlineData("Kan ik deze maand nog €100 uitgeven aan boodschappen?", AssistantIntent.CategoryRoom)]
    [InlineData("How much can I safely spend before payday?", AssistantIntent.SafeToSpend)]
    [InlineData("Hoeveel kan ik veilig uitgeven tot mijn loon?", AssistantIntent.SafeToSpend)]
    [InlineData("What payments are coming up?", AssistantIntent.Upcoming)]
    [InlineData("Welke betalingen komen eraan?", AssistantIntent.Upcoming)]
    [InlineData("Why will my balance be lower next month?", AssistantIntent.WhyLower)]
    [InlineData("Waarom is mijn saldo volgende maand lager?", AssistantIntent.WhyLower)]
    [InlineData("What if renovation costs increase by €5,000?", AssistantIntent.WhatIf)]
    [InlineData("Wat als de renovatie €5.000 meer kost?", AssistantIntent.WhatIf)]
    [InlineData("When can I afford the new laptop?", AssistantIntent.WhenAfford)]
    [InlineData("Wanneer kan ik de nieuwe laptop kopen?", AssistantIntent.WhenAfford)]
    [InlineData("How much did I spend on subscriptions?", AssistantIntent.SpentOn)]
    [InlineData("Hoeveel heb ik uitgegeven aan abonnementen?", AssistantIntent.SpentOn)]
    [InlineData("What changed versus last month?", AssistantIntent.ChangedVsLastMonth)]
    [InlineData("Wat is er veranderd ten opzichte van vorige maand?", AssistantIntent.ChangedVsLastMonth)]
    [InlineData("Should I use VISA or wait?", AssistantIntent.CardOrWait)]
    [InlineData("Moet ik de laptop met VISA betalen of wachten?", AssistantIntent.CardOrWait)]
    [InlineData("Wat zijn mijn vaste kosten?", AssistantIntent.FixedCosts)]
    [InlineData("Hoeveel geef ik normaal uit per maand?", AssistantIntent.FixedCosts)]
    [InlineData("What are my fixed costs?", AssistantIntent.FixedCosts)]
    [InlineData("How much do I usually spend a month?", AssistantIntent.FixedCosts)]
    [InlineData("Hoeveel geef ik normaal uit aan boodschappen?", AssistantIntent.SpentOn)]
    [InlineData("Kan ik deze maand nog iets extra uitgeven?", AssistantIntent.SafeToSpend)]
    [InlineData("Can I spend something extra this week?", AssistantIntent.SafeToSpend)]
    [InlineData("Kan ik €80 extra uitgeven?", AssistantIntent.CanAfford)]
    [InlineData("Help", AssistantIntent.Help)]
    [InlineData("Wat kan je?", AssistantIntent.Help)]
    [InlineData("Goedemorgen", AssistantIntent.Unknown)]
    [InlineData("", AssistantIntent.Unknown)]
    public void Recognises_the_intent(string text, AssistantIntent intent) =>
        Assert.Equal(intent, Parse(text).Intent);

    [Fact]
    public void Reads_the_amount_and_category_of_a_category_question()
    {
        var q = Parse("Can I spend another €100 on groceries this month?");
        Assert.Equal(Money.FromCents(10_000), q.Amount);
        Assert.Equal(Groceries.Id, q.Category?.Id);
        Assert.Equal(PeriodKind.ThisMonth, q.Period?.Kind);
    }

    [Fact]
    public void Finds_a_wishlist_item_by_its_significant_words()
    {
        var q = Parse("When can I afford the new laptop?");
        Assert.Equal(Laptop.Id, q.Item?.Id);
        Assert.Null(q.Amount);

        Assert.Equal(Sofa.Id, Parse("Kan ik de zetel kopen?").Item?.Id);
        Assert.Null(Parse("Kan ik een fiets kopen?").Item);
    }

    [Fact]
    public void A_what_if_about_salary_reads_direction_and_frequency()
    {
        var q = Parse("What if my salary drops by 200 euro per month?");
        Assert.Equal(AssistantIntent.WhatIf, q.Intent);
        Assert.Equal(Money.FromCents(20_000), q.Amount);
        Assert.True(q.AboutIncome);
        Assert.True(q.IsDecrease);
        Assert.True(q.IsMonthly);

        var rent = Parse("Wat als de huur met €50 stijgt?");
        Assert.Equal(Rent.Id, rent.Rule?.Id);
        Assert.False(rent.IsDecrease);
        Assert.False(rent.AboutIncome);
    }

    [Theory]
    [InlineData("€5.000", 500_000)]
    [InlineData("5,000 euro", 500_000)]
    [InlineData("€ 42,50", 4_250)]
    [InlineData("42.50", 4_250)]
    [InlineData("1.234,56", 123_456)]
    [InlineData("5k", 500_000)]
    [InlineData("250", 25_000)]
    [InlineData("in 3 maanden 200 euro", 20_000)]
    [InlineData("de afgelopen 6 maanden €75", 7_500)]
    public void Reads_amounts_in_common_formats(string text, long cents) =>
        Assert.Equal(Money.FromCents(cents), QuestionParser.FindAmount(text.ToLowerInvariant()));

    [Theory]
    [InlineData("10% meer")]
    [InlineData("over 30 dagen")]
    [InlineData("hoe lang nog")]
    public void Counts_and_percentages_are_not_amounts(string text) =>
        Assert.Null(QuestionParser.FindAmount(text));

    [Fact]
    public void Reads_the_period_of_a_spending_question()
    {
        var three = Parse("Hoeveel heb ik de afgelopen 3 maanden uitgegeven aan abonnementen?").Period!;
        Assert.Equal((PeriodKind.LastMonths, new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30), 3), (three.Kind, three.From, three.To, three.Months));

        var last = Parse("How much did I spend on subscriptions last month?").Period!;
        Assert.Equal((PeriodKind.LastMonth, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), (last.Kind, last.From, last.To));

        var year = Parse("Hoeveel heb ik dit jaar uitgegeven aan boodschappen?").Period!;
        Assert.Equal((PeriodKind.ThisYear, new DateOnly(2026, 1, 1), Today, 10), (year.Kind, year.From, year.To, year.Months));

        var march = Parse("Hoeveel heb ik in maart uitgegeven aan boodschappen?").Period!;
        Assert.Equal((PeriodKind.Month, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)), (march.Kind, march.From, march.To));

        // A month later in the year than today is last year's.
        var november = Parse("How much did I spend on groceries in November?").Period!;
        Assert.Equal(new DateOnly(2025, 11, 1), november.From);

        // "May" as a verb is not a month.
        Assert.Null(Parse("How much may I spend on groceries?").Period);
    }

    [Fact]
    public void Categories_match_by_name_plural_or_a_word_in_the_other_language()
    {
        Assert.Equal(Subscriptions.Id, Parse("How much did I spend on subscriptions?").Category?.Id);
        Assert.Equal(Subscriptions.Id, Parse("Hoeveel kostte mijn abonnement?").Category?.Id);
        Assert.Equal(Renovation.Id, Parse("What if renovation costs increase by €5,000?").Category?.Id);
        Assert.Equal(Groceries.Id, Parse("How much went to the supermarket?").Category?.Id);
    }

    [Fact]
    public void Accents_and_punctuation_do_not_matter()
    {
        Assert.Equal(" wat als de renovatie 5 000 meer kost ", QuestionParser.Normalize("wat als de rénovatie €5.000 meer kost?!"));
        Assert.Equal(AssistantIntent.SafeToSpend, Parse("HOEVEEL kan ik VEILIG uitgeven???").Intent);
    }

    [Fact]
    public void The_same_question_always_gives_the_same_result()
    {
        var first = Parse("Kan ik deze maand nog €100 uitgeven aan boodschappen?");
        var second = Parse("Kan ik deze maand nog €100 uitgeven aan boodschappen?");
        Assert.Equal(first, second);
    }
}
