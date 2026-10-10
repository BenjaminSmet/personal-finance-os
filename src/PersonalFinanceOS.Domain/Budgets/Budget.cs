namespace PersonalFinanceOS.Domain.Budgets;

/// <summary>
/// A monthly spending budget for one expense category (spec §13). It holds from <see cref="StartMonth"/> until a
/// budget with a later start month replaces it, so changing a budget never rewrites earlier months.
/// An amount of zero means "no budget from this month on".
/// </summary>
public sealed class Budget
{
    public Guid Id { get; private set; }
    public Guid CategoryId { get; private set; }

    /// <summary>Always the first day of a month.</summary>
    public DateOnly StartMonth { get; private set; }

    public Money Amount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private Budget()
    {
    }

    public static Budget Create(Guid categoryId, DateOnly month, Money amount, DateTimeOffset now)
    {
        var budget = new Budget
        {
            Id = Guid.NewGuid(),
            CategoryId = categoryId,
            StartMonth = MonthOf(month),
            CreatedAt = now,
        };
        budget.Change(amount, now);
        return budget;
    }

    public void Change(Money amount, DateTimeOffset now)
    {
        if (amount.IsNegative)
        {
            throw new DomainValidationException("budget.amount.negative");
        }

        Amount = amount;
        UpdatedAt = now;
    }

    /// <summary>Moves the budget to another category (when categories are merged).</summary>
    public void MoveTo(Guid categoryId, DateTimeOffset now)
    {
        CategoryId = categoryId;
        UpdatedAt = now;
    }

    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>The budget of a category for a month: the latest one starting in or before it. Null when none applies.</summary>
    public static Money? AmountFor(IEnumerable<Budget> budgets, Guid categoryId, DateOnly month)
    {
        var start = MonthOf(month);
        var current = budgets
            .Where(b => b.CategoryId == categoryId && b.StartMonth <= start)
            .MaxBy(b => b.StartMonth);
        return current is { Amount.IsPositive: true } ? current.Amount : null;
    }
}
