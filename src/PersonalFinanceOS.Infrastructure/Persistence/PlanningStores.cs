using Microsoft.EntityFrameworkCore;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain.Cards;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Infrastructure.Persistence;

public sealed class CreditCardRepository(Func<FinanceDbContext> contextFactory) : ICreditCardRepository
{
    public async Task<IReadOnlyList<CreditCardProfile>> ListAsync(CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.CreditCardProfiles.AsNoTracking().ToListAsync(ct);
    }

    public async Task<CreditCardProfile?> FindAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.CreditCardProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.AccountId == accountId, ct);
    }

    public async Task SaveAsync(CreditCardProfile profile, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        if (await db.CreditCardProfiles.AsNoTracking().AnyAsync(p => p.AccountId == profile.AccountId, ct))
        {
            db.CreditCardProfiles.Update(profile);
        }
        else
        {
            db.CreditCardProfiles.Add(profile);
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class RecurringRepository(Func<FinanceDbContext> contextFactory) : IRecurringRepository
{
    public async Task<IReadOnlyList<RecurringRule>> ListRulesAsync(bool includeEnded, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var query = db.RecurringRules.AsNoTracking();
        if (!includeEnded)
        {
            query = query.Where(r => r.Status == RecurringRuleStatus.Active);
        }

        var list = await query.ToListAsync(ct);
        return list.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<RecurringRule?> FindRuleAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.RecurringRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task AddRuleAsync(RecurringRule rule, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.RecurringRules.Add(rule);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateRuleAsync(RecurringRule rule, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.RecurringRules.Update(rule);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ScheduledItem>> ListItemsAsync(CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.ScheduledItems.AsNoTracking().ToListAsync(ct);
    }

    public async Task<ScheduledItem?> FindItemAsync(Guid ruleId, DateOnly occurrenceDate, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.ScheduledItems.AsNoTracking().FirstOrDefaultAsync(i => i.RuleId == ruleId && i.OccurrenceDate == occurrenceDate, ct);
    }

    public async Task SaveItemAsync(ScheduledItem item, IReadOnlyCollection<Transaction> created, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Transactions.AddRange(created);
        if (await db.ScheduledItems.AsNoTracking().AnyAsync(i => i.Id == item.Id, ct))
        {
            db.ScheduledItems.Update(item);
        }
        else
        {
            db.ScheduledItems.Add(item);
        }

        await db.SaveChangesAsync(ct); // one SaveChanges = one database transaction
    }

    public async Task DeleteItemAsync(ScheduledItem item, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.ScheduledItems.Remove(item);
        await db.SaveChangesAsync(ct);
    }
}
