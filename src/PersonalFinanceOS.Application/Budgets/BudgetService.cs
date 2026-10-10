using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Budgets;
using PersonalFinanceOS.Domain.Categories;

namespace PersonalFinanceOS.Application.Budgets;

/// <summary>Monthly budgets per expense category (spec §13). Setting one never changes earlier months.</summary>
public sealed class BudgetService(IBudgetRepository budgets, ICategoryRepository categories, IAuditLog audit, IClock clock)
{
    /// <summary>Raised after a budget changes.</summary>
    public event EventHandler? Changed;

    public Task<IReadOnlyList<Budget>> ListAsync(CancellationToken ct = default) => budgets.ListAsync(ct);

    /// <summary>
    /// Sets the budget of an expense category from <paramref name="month"/> on; zero removes it from that month on.
    /// Budgets set earlier for later months are replaced, so the new amount holds until it is changed again.
    /// Earlier months keep the budget they had.
    /// </summary>
    public async Task SetAsync(Guid categoryId, DateOnly month, Money amount, CancellationToken ct = default)
    {
        var category = await categories.FindAsync(categoryId, ct);
        if (category is not { IsActive: true, Kind: CategoryKind.Expense })
        {
            throw new DomainValidationException("budget.category.invalid");
        }

        if (amount.IsNegative)
        {
            throw new DomainValidationException("budget.amount.negative");
        }

        var start = Budget.MonthOf(month);
        var own = (await budgets.ListAsync(ct)).Where(b => b.CategoryId == categoryId).ToList();
        var later = own.Where(b => b.StartMonth > start).ToList();
        var existing = own.FirstOrDefault(b => b.StartMonth == start);
        var before = Budget.AmountFor(own.Where(b => b.StartMonth < start), categoryId, start);

        if (existing is null && amount.IsZero && before is null)
        {
            // Nothing applied before this month, so "no budget" needs no row; only later ones go.
            await budgets.RemoveAsync(later, ct);
        }
        else if (existing is not null)
        {
            existing.Change(amount, clock.Now);
            await budgets.SaveAsync(existing, later, ct);
        }
        else
        {
            await budgets.SaveAsync(Budget.Create(categoryId, start, amount, clock.Now), later, ct);
        }

        await audit.RecordAsync("Budget", categoryId, "Set", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
