using Microsoft.EntityFrameworkCore;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Assets;
using PersonalFinanceOS.Domain.Ownership;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Infrastructure.Persistence;

public sealed class AssetRepository(Func<FinanceDbContext> contextFactory) : IAssetRepository
{
    public async Task<IReadOnlyList<Asset>> ListAsync(bool includeArchived, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var query = db.Assets.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(a => a.Status == AssetStatus.Active);
        }

        var list = await query.ToListAsync(ct);
        return list.OrderBy(a => a.IsLiability).ThenBy(a => a.SortOrder).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Asset?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task AddAsync(Asset asset, Valuation? first, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Assets.Add(asset);
        if (first is not null)
        {
            db.Valuations.Add(first);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Asset asset, Valuation? valuation = null, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Assets.Update(asset);
        if (valuation is not null)
        {
            await ValuationRepository.UpsertAsync(db, valuation, ct);
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class ValuationRepository(Func<FinanceDbContext> contextFactory) : IValuationRepository
{
    public async Task<IReadOnlyList<Valuation>> ListAsync(CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var list = await db.Valuations.AsNoTracking().ToListAsync(ct);
        return list.OrderBy(v => v.Date).ToList();
    }

    public async Task<Valuation?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.Valuations.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
    }

    public async Task SaveAsync(Valuation valuation, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        await UpsertAsync(db, valuation, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Valuation valuation, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.Valuations.Remove(valuation);
        await db.SaveChangesAsync(ct);
    }

    internal static async Task UpsertAsync(FinanceDbContext db, Valuation valuation, CancellationToken ct)
    {
        if (await db.Valuations.AsNoTracking().AnyAsync(v => v.Id == valuation.Id, ct))
        {
            db.Valuations.Update(valuation);
        }
        else
        {
            db.Valuations.Add(valuation);
        }
    }
}

public sealed class OwnershipRepository(Func<FinanceDbContext> contextFactory) : IOwnershipRepository
{
    public async Task<IReadOnlyList<OwnershipChange>> ListAsync(CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var list = await db.OwnershipChanges.AsNoTracking().ToListAsync(ct);
        return list.OrderBy(c => c.EffectiveFrom).ToList();
    }

    public async Task SaveAsync(IReadOnlyCollection<OwnershipChange> changes, Account? account, Asset? asset, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        var ids = changes.Select(c => c.Id).ToList();
        var existing = await db.OwnershipChanges.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        foreach (var change in changes)
        {
            if (existing.Contains(change.Id))
            {
                db.OwnershipChanges.Update(change);
            }
            else
            {
                db.OwnershipChanges.Add(change);
            }
        }

        if (account is not null)
        {
            db.Accounts.Update(account);
        }

        if (asset is not null)
        {
            db.Assets.Update(asset);
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class WishlistRepository(Func<FinanceDbContext> contextFactory) : IWishlistRepository
{
    public async Task<IReadOnlyList<WishlistItem>> ListAsync(CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.WishlistItems.AsNoTracking().ToListAsync(ct);
    }

    public async Task<WishlistItem?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        return await db.WishlistItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    public async Task AddAsync(WishlistItem item, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.WishlistItems.Add(item);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(WishlistItem item, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        db.WishlistItems.Update(item);
        await db.SaveChangesAsync(ct);
    }

    public async Task SavePurchaseAsync(WishlistItem item, Transaction? created, CancellationToken ct = default)
    {
        await using var db = contextFactory();
        if (created is not null)
        {
            db.Transactions.Add(created);
        }

        db.WishlistItems.Update(item);
        await db.SaveChangesAsync(ct);
    }
}
