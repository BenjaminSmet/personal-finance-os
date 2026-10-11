using Microsoft.EntityFrameworkCore;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain.Scenarios;

namespace PersonalFinanceOS.Infrastructure.Persistence;

public sealed class ScenarioRepository(Func<FinanceDbContext> contextFactory) : IScenarioRepository
{
    public async Task<IReadOnlyList<Scenario>> ListAsync(CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var list = await db.Scenarios.AsNoTracking().ToListAsync(ct);
        return list.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Scenario?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<IReadOnlyList<ScenarioChange>> ListChangesAsync(Guid scenarioId, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var list = await db.ScenarioChanges.AsNoTracking().Where(c => c.ScenarioId == scenarioId).ToListAsync(ct);
        return list.OrderBy(c => c.CreatedAt).ToList();
    }

    public async Task AddAsync(Scenario scenario, IReadOnlyCollection<ScenarioChange> changes, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Scenarios.Add(scenario);
        db.ScenarioChanges.AddRange(changes);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Scenario scenario, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Scenarios.Update(scenario);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ScenarioChanges.Where(c => c.ScenarioId == id).ExecuteDeleteAsync(ct);
        await db.Scenarios.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task AddChangeAsync(ScenarioChange change, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.ScenarioChanges.Add(change);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveChangeAsync(Guid changeId, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        await db.ScenarioChanges.Where(c => c.Id == changeId).ExecuteDeleteAsync(ct);
    }
}
