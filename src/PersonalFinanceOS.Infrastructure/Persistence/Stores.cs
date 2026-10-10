using Microsoft.EntityFrameworkCore;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;

namespace PersonalFinanceOS.Infrastructure.Persistence;

/// <summary>
/// Each operation uses a short-lived context so the desktop app never holds stale tracked entities
/// and every save is its own transaction.
/// </summary>
public sealed class AccountRepository(Func<FinanceDbContext> contextFactory) : IAccountRepository
{
    public async Task<IReadOnlyList<Account>> ListAsync(bool includeArchived, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var query = db.Accounts.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(a => a.Status == AccountStatus.Active);
        }

        // Sorting in memory keeps names sorted culture-aware rather than by SQLite byte order.
        var list = await query.ToListAsync(ct);
        return list.OrderBy(a => a.SortOrder).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Account?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task AddAsync(Account account, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Account account, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Accounts.Update(account);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class SettingsStore(Func<FinanceDbContext> contextFactory, IClock clock) : ISettingsStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.AppSettings.Add(new AppSettingRow { Key = key, Value = value, UpdatedAt = clock.Now });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = clock.Now;
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class AuditLog(Func<FinanceDbContext> contextFactory, IClock clock) : IAuditLog
{
    public async Task RecordAsync(string entity, Guid? entityId, string action, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.AuditLog.Add(new AuditLogRow
        {
            OccurredAt = clock.Now,
            Entity = entity,
            EntityId = entityId?.ToString("D"),
            Action = action,
        });
        await db.SaveChangesAsync(ct);
    }
}
