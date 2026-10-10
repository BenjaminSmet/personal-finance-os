using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Cards;
using PersonalFinanceOS.Domain.Planning;

namespace PersonalFinanceOS.Tests.Domain;

/// <summary>Statement periods (spec §10 cycle logic) and recurring occurrences (spec §12).</summary>
public class PlanningDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public void The_28th_starts_a_new_period_and_the_27th_ends_one()
    {
        Assert.Equal(new StatementPeriod(new(2026, 10, 28), new(2026, 11, 27)), StatementCycle.PeriodOf(new(2026, 10, 28), 28));
        Assert.Equal(new StatementPeriod(new(2026, 9, 28), new(2026, 10, 27)), StatementCycle.PeriodOf(new(2026, 10, 27), 28));
    }

    [Theory]
    [InlineData("2026-12-30", "2026-12-28", "2027-01-27")] // year boundary
    [InlineData("2027-01-05", "2026-12-28", "2027-01-27")]
    [InlineData("2027-02-27", "2027-01-28", "2027-02-27")] // short month
    [InlineData("2027-02-28", "2027-02-28", "2027-03-27")]
    [InlineData("2028-02-29", "2028-02-28", "2028-03-27")] // leap year
    public void Periods_handle_month_lengths_and_year_ends(string date, string start, string end)
    {
        var period = StatementCycle.PeriodOf(DateOnly.Parse(date), 28);
        Assert.Equal(DateOnly.Parse(start), period.Start);
        Assert.Equal(DateOnly.Parse(end), period.End);
    }

    [Fact]
    public void The_start_day_is_configurable_and_a_late_day_means_the_last_day_of_short_months()
    {
        Assert.Equal(new StatementPeriod(new(2026, 10, 1), new(2026, 10, 31)), StatementCycle.PeriodOf(new(2026, 10, 15), 1));

        // Day 31: February's period starts on the 28th; periods never overlap or leave gaps.
        var feb = StatementCycle.PeriodOf(new(2027, 2, 15), 31);
        Assert.Equal(new StatementPeriod(new(2027, 1, 31), new(2027, 2, 27)), feb);
        var next = StatementCycle.Next(feb, 31);
        Assert.Equal(new StatementPeriod(new(2027, 2, 28), new(2027, 3, 30)), next);
        Assert.Equal(feb, StatementCycle.Previous(next, 31));
    }

    [Fact]
    public void Settlement_date_is_the_first_debit_day_after_the_period_and_unknown_when_not_set()
    {
        var card = CreditCardProfile.Create(Guid.NewGuid(), Money.FromCents(150_000), 28, 5, null, Now);
        var period = card.PeriodOf(new(2026, 12, 30));
        Assert.Equal(new DateOnly(2027, 2, 5), card.SettlementDateOf(period));

        card.Change(Money.FromCents(150_000), 28, null, null, Now);
        Assert.Null(card.SettlementDateOf(period));
    }

    [Fact]
    public void Card_settings_are_validated()
    {
        var id = Guid.NewGuid();
        Assert.Equal("card.startDay.invalid", Assert.Throws<DomainValidationException>(() => CreditCardProfile.Create(id, Money.Zero, 0, null, null, Now)).Code);
        Assert.Equal("card.startDay.invalid", Assert.Throws<DomainValidationException>(() => CreditCardProfile.Create(id, Money.Zero, 32, null, null, Now)).Code);
        Assert.Equal("card.settlementDay.invalid", Assert.Throws<DomainValidationException>(() => CreditCardProfile.Create(id, Money.Zero, 28, 40, null, Now)).Code);
        Assert.Equal("card.limit.negative", Assert.Throws<DomainValidationException>(() => CreditCardProfile.Create(id, Money.FromCents(-1), 28, null, null, Now)).Code);
        Assert.Equal("card.settlementAccount.invalid", Assert.Throws<DomainValidationException>(() => CreditCardProfile.Create(id, Money.Zero, 28, null, id, Now)).Code);
    }

    [Fact]
    public void Monthly_occurrences_keep_the_start_day_without_drifting()
    {
        var rule = Rule(RecurrenceFrequency.Monthly, new(2027, 1, 31));
        Assert.Equal(
            [new(2027, 1, 31), new(2027, 2, 28), new(2027, 3, 31), new(2027, 4, 30)],
            rule.Occurrences(new(2027, 1, 1), new(2027, 4, 30)).ToArray());
    }

    [Fact]
    public void Occurrences_respect_frequency_range_and_end_date()
    {
        var weekly = Rule(RecurrenceFrequency.Weekly, new(2026, 10, 2));
        Assert.Equal([new(2026, 10, 9), new(2026, 10, 16)], weekly.Occurrences(new(2026, 10, 3), new(2026, 10, 20)).ToArray());

        var quarterly = Rule(RecurrenceFrequency.Quarterly, new(2026, 1, 15), end: new(2026, 12, 31));
        Assert.Equal([new(2026, 1, 15), new(2026, 4, 15), new(2026, 7, 15), new(2026, 10, 15)], quarterly.Occurrences(new(2026, 1, 1), new(2027, 12, 31)).ToArray());

        var yearly = Rule(RecurrenceFrequency.Yearly, new(2024, 2, 29));
        Assert.Equal([new(2025, 2, 28), new(2026, 2, 28)], yearly.Occurrences(new(2025, 1, 1), new(2026, 12, 31)).ToArray());
    }

    [Fact]
    public void Ending_a_rule_stops_later_occurrences()
    {
        var rule = Rule(RecurrenceFrequency.Monthly, new(2026, 8, 1));
        rule.End(new(2026, 9, 30), Now);
        Assert.Equal([new(2026, 8, 1), new(2026, 9, 1)], rule.Occurrences(new(2026, 1, 1), new(2027, 1, 1)).ToArray());
        Assert.False(rule.IsActive);
    }

    [Fact]
    public void A_transfer_rule_needs_another_destination_and_carries_no_category()
    {
        var account = Guid.NewGuid();
        Assert.Equal("recurring.toAccount.required", Assert.Throws<DomainValidationException>(() =>
            RecurringRule.Create("Sparen", RecurringKind.Transfer, account, null, null, Money.FromCents(100), RecurrenceFrequency.Monthly, new(2026, 10, 1), null, Now)).Code);
        Assert.Equal("transfer.sameAccount", Assert.Throws<DomainValidationException>(() =>
            RecurringRule.Create("Sparen", RecurringKind.Transfer, account, account, null, Money.FromCents(100), RecurrenceFrequency.Monthly, new(2026, 10, 1), null, Now)).Code);

        var rule = RecurringRule.Create("Sparen", RecurringKind.Transfer, account, Guid.NewGuid(), Guid.NewGuid(), Money.FromCents(100), RecurrenceFrequency.Monthly, new(2026, 10, 1), null, Now);
        Assert.Null(rule.CategoryId);
    }

    private static RecurringRule Rule(RecurrenceFrequency frequency, DateOnly start, DateOnly? end = null) =>
        RecurringRule.Create("Huur", RecurringKind.Expense, Guid.NewGuid(), null, null, Money.FromCents(85_000), frequency, start, end, Now);
}
