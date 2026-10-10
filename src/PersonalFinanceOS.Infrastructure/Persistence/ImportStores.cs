using Microsoft.EntityFrameworkCore;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain.Imports;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Infrastructure.Persistence;

public sealed class ImportRepository(Func<FinanceDbContext> contextFactory) : IImportRepository
{
    public async Task<IReadOnlyList<ImportBatch>> ListBatchesAsync(Guid? accountId = null, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var query = db.ImportBatches.AsNoTracking();
        if (accountId is { } id)
        {
            query = query.Where(b => b.AccountId == id);
        }

        var list = await query.ToListAsync(ct);
        return list.OrderByDescending(b => b.ImportedAt).ToList();
    }

    public async Task<ImportBatch?> FindBatchAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<IReadOnlyList<Transaction>> ListBatchTransactionsAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.Transactions.AsNoTracking().Where(t => t.ImportBatchId == batchId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ImportTransferLink>> ListLinksAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.ImportTransferLinks.AsNoTracking().Where(l => l.BatchId == batchId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(Transaction Transaction, ImportBatch? Batch)>> ListForDuplicateCheckAsync(
        Guid accountId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var lines = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.Date >= from && t.Date <= to)
            .ToListAsync(ct);
        var batchIds = lines.Where(t => t.ImportBatchId is not null).Select(t => t.ImportBatchId!.Value).Distinct().ToList();
        var batches = await db.ImportBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        return lines
            .OrderBy(t => t.Date).ThenBy(t => t.CreatedAt)
            .Select(t => (t, t.ImportBatchId is { } id ? batches.GetValueOrDefault(id) : null))
            .ToList();
    }

    public async Task<ImportProfile?> FindProfileAsync(string headerKey, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.ImportProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.HeaderKey == headerKey, ct);
    }

    public async Task SaveImportAsync(ImportChanges changes, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.ImportBatches.Add(changes.Batch);
        db.Transactions.AddRange(changes.Added);
        db.Transactions.UpdateRange(changes.Linked);
        db.ImportTransferLinks.AddRange(changes.Links);
        if (changes.Profile is { } profile)
        {
            if (await db.ImportProfiles.AsNoTracking().AnyAsync(p => p.Id == profile.Id, ct))
            {
                db.ImportProfiles.Update(profile);
            }
            else
            {
                db.ImportProfiles.Add(profile);
            }
        }

        await db.SaveChangesAsync(ct); // one SaveChanges = one database transaction: a failed import leaves nothing
    }

    public async Task SaveUndoAsync(ImportBatch batch, IReadOnlyCollection<Transaction> changed, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.ImportBatches.Update(batch);
        db.Transactions.UpdateRange(changed);
        await db.SaveChangesAsync(ct);
    }
}
