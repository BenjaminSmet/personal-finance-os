using Microsoft.EntityFrameworkCore;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain.Budgets;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Infrastructure.Persistence;

public sealed class TransactionRepository(Func<FinanceDbContext> contextFactory) : ITransactionRepository
{
    public async Task<IReadOnlyList<Transaction>> ListAsync(TransactionQuery query, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var rows = db.Transactions.AsNoTracking();
        if (!query.IncludeVoided)
        {
            rows = rows.Where(t => t.Status == TransactionStatus.Active);
        }

        if (query.AccountId is { } accountId)
        {
            rows = rows.Where(t => t.AccountId == accountId);
        }

        // Dates are stored as ISO text, so the converter turns these into correct text comparisons.
        if (query.From is { } from)
        {
            rows = rows.Where(t => t.Date >= from);
        }

        if (query.To is { } to)
        {
            rows = rows.Where(t => t.Date <= to);
        }

        var list = await rows.ToListAsync(ct);
        return list.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).ThenBy(t => t.Amount).ToList();
    }

    public async Task<Transaction?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<IReadOnlyList<Transaction>> FindTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.Transactions.AsNoTracking().Where(t => t.TransferId == transferId).ToListAsync(ct);
    }

    public async Task AddAsync(IReadOnlyCollection<Transaction> transactions, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Transactions.AddRange(transactions);
        await db.SaveChangesAsync(ct); // one SaveChanges = one database transaction
    }

    public async Task UpdateAsync(IReadOnlyCollection<Transaction> transactions, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Transactions.UpdateRange(transactions);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class CategoryRepository(Func<FinanceDbContext> contextFactory) : ICategoryRepository
{
    public async Task<IReadOnlyList<Category>> ListAsync(bool includeArchived, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var query = db.Categories.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(c => c.Status == CategoryStatus.Active);
        }

        var list = await query.ToListAsync(ct);
        return list.OrderBy(c => c.Kind).ThenBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Category?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task AddAsync(IReadOnlyCollection<Category> categories, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Categories.AddRange(categories);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Category category, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Categories.Update(category);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(IReadOnlyCollection<Category> categories, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Categories.UpdateRange(categories);
        await db.SaveChangesAsync(ct);
    }

    public async Task MergeAsync(Category source, Guid targetId, IReadOnlyCollection<Budget> movedBudgets, IReadOnlyCollection<Budget> removedBudgets, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Guid? from = source.Id;
        Guid? to = targetId;

        // Only the category changes; updated_at stays, so an imported line does not look edited by the user.
        await db.Transactions.Where(t => t.CategoryId == from).ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, to), ct);
        await db.PayeeRules.Where(r => r.CategoryId == from).ExecuteUpdateAsync(s => s.SetProperty(r => r.CategoryId, to), ct);
        await db.RecurringRules.Where(r => r.CategoryId == from).ExecuteUpdateAsync(s => s.SetProperty(r => r.CategoryId, to), ct);
        await db.ImportTransferLinks.Where(l => l.OriginalCategoryId == from).ExecuteUpdateAsync(s => s.SetProperty(l => l.OriginalCategoryId, to), ct);

        db.Budgets.RemoveRange(removedBudgets);
        db.Budgets.UpdateRange(movedBudgets);
        db.Categories.Update(source);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

public sealed class BudgetRepository(Func<FinanceDbContext> contextFactory) : IBudgetRepository
{
    public async Task<IReadOnlyList<Budget>> ListAsync(CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var list = await db.Budgets.AsNoTracking().ToListAsync(ct);
        return list.OrderBy(b => b.StartMonth).ToList();
    }

    public async Task SaveAsync(Budget budget, IReadOnlyCollection<Budget> removed, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Budgets.RemoveRange(removed);
        if (await db.Budgets.AsNoTracking().AnyAsync(b => b.Id == budget.Id, ct))
        {
            db.Budgets.Update(budget);
        }
        else
        {
            db.Budgets.Add(budget);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(IReadOnlyCollection<Budget> removed, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Budgets.RemoveRange(removed);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class PayeeRuleRepository(Func<FinanceDbContext> contextFactory) : IPayeeRuleRepository
{
    public async Task<IReadOnlyList<PayeeRule>> ListAsync(CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.PayeeRules.AsNoTracking().ToListAsync(ct);
    }

    public async Task SaveAsync(PayeeRule rule, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        if (await db.PayeeRules.AsNoTracking().AnyAsync(r => r.Id == rule.Id, ct))
        {
            db.PayeeRules.Update(rule);
        }
        else
        {
            db.PayeeRules.Add(rule);
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class ReconciliationRepository(Func<FinanceDbContext> contextFactory) : IReconciliationRepository
{
    public async Task<IReadOnlyList<ReconciliationRecord>> ListAsync(Guid? accountId, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var query = db.Reconciliations.AsNoTracking();
        if (accountId is { } id)
        {
            query = query.Where(r => r.AccountId == id);
        }

        var list = await query.ToListAsync(ct);
        return list.OrderByDescending(r => r.StatementDate).ThenByDescending(r => r.CreatedAt).ToList();
    }

    public async Task AddAsync(ReconciliationRecord record, Transaction? adjustment, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        if (adjustment is not null)
        {
            db.Transactions.Add(adjustment);
        }

        db.Reconciliations.Add(record);
        await db.SaveChangesAsync(ct); // record and adjustment together, or neither
    }
}
