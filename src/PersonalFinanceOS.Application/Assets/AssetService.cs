using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Assets;
using PersonalFinanceOS.Domain.Ownership;

namespace PersonalFinanceOS.Application.Assets;

/// <summary>A new asset or liability, optionally with its first value.</summary>
public sealed record AssetRequest(
    string Name,
    AssetKind Kind,
    Share OwnershipShare,
    Money? Value = null,
    DateOnly? ValueDate = null,
    string? Notes = null);

/// <summary>
/// Assets and liabilities outside the ledger, manual valuations and ownership shares over time (spec §9, §17).
/// Values are only ever entered by the user: there are no live prices and nothing is fetched.
/// </summary>
public sealed class AssetService(
    IAssetRepository assets,
    IValuationRepository valuations,
    IOwnershipRepository ownership,
    IAccountRepository accounts,
    IAuditLog audit,
    IClock clock)
{
    /// <summary>Raised after an asset, valuation or share changes.</summary>
    public event EventHandler? Changed;

    public Task<IReadOnlyList<Asset>> ListAsync(bool includeArchived = false, CancellationToken ct = default) =>
        assets.ListAsync(includeArchived, ct);

    public async Task<Asset> CreateAsync(AssetRequest request, CancellationToken ct = default)
    {
        var existing = await assets.ListAsync(includeArchived: true, ct);
        var sortOrder = existing.Count == 0 ? 0 : existing.Max(a => a.SortOrder) + 1;
        var asset = Asset.Create(request.Name, request.Kind, request.OwnershipShare, clock.Now, request.Notes, sortOrder);
        Valuation? first = null;
        if (request.Value is { } value)
        {
            var date = request.ValueDate ?? clock.Today;
            ValidateDate(date);
            first = Valuation.ForAsset(asset.Id, date, value, null, clock.Now);
        }

        await assets.AddAsync(asset, first, ct);
        await audit.RecordAsync("Asset", asset.Id, "Created", ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return asset;
    }

    public async Task UpdateAsync(Guid id, string name, string? notes, CancellationToken ct = default)
    {
        var asset = await GetAssetAsync(id, ct);
        asset.Change(name, notes, clock.Now);
        await assets.UpdateAsync(asset, ct: ct);
        await audit.RecordAsync("Asset", id, "Edited", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sold or paid off: the value becomes zero from today, recorded as a valuation so earlier months keep their
    /// value, and the item is hidden from the active list.
    /// </summary>
    public async Task ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await GetAssetAsync(id, ct);
        var all = await valuations.ListAsync(ct);
        var today = clock.Today;
        var zero = all.FirstOrDefault(v => v.AssetId == id && v.Date == today) ?? Valuation.ForAsset(id, today, Money.Zero, null, clock.Now);
        zero.Change(Money.Zero, zero.Note, clock.Now);
        asset.Archive(clock.Now);
        await assets.UpdateAsync(asset, zero, ct);
        await audit.RecordAsync("Asset", id, "Archived", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<Valuation>> ListValuationsAsync(Guid? ownerId = null, CancellationToken ct = default)
    {
        var all = await valuations.ListAsync(ct);
        return ownerId is { } id ? all.Where(v => v.OwnerId == id).ToList() : all;
    }

    /// <summary>
    /// Records the value of a pension or investment account, or of an asset, on a date. A value for a date that
    /// already has one corrects that date; every other date keeps its own value.
    /// </summary>
    public async Task<Valuation> SetValuationAsync(Guid ownerId, DateOnly date, Money value, string? note = null, CancellationToken ct = default)
    {
        ValidateDate(date);
        var isAccount = await IsManualAccountAsync(ownerId, ct);
        if (!isAccount)
        {
            _ = await GetAssetAsync(ownerId, ct);
        }

        var all = await valuations.ListAsync(ct);
        var valuation = all.FirstOrDefault(v => v.OwnerId == ownerId && v.Date == date);
        if (valuation is null)
        {
            valuation = isAccount
                ? Valuation.ForAccount(ownerId, date, value, note, clock.Now)
                : Valuation.ForAsset(ownerId, date, value, note, clock.Now);
        }
        else
        {
            valuation.Change(value, note, clock.Now);
        }

        await valuations.SaveAsync(valuation, ct);
        await audit.RecordAsync("Valuation", valuation.Id, "Saved", ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return valuation;
    }

    public async Task RemoveValuationAsync(Guid valuationId, CancellationToken ct = default)
    {
        var valuation = await valuations.FindAsync(valuationId, ct) ?? throw new DomainValidationException("valuation.notFound");
        await valuations.RemoveAsync(valuation, ct);
        await audit.RecordAsync("Valuation", valuationId, "Removed", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<OwnershipChange>> ListShareChangesAsync(Guid? ownerId = null, CancellationToken ct = default)
    {
        var all = await ownership.ListAsync(ct);
        return ownerId is { } id ? all.Where(c => c.OwnerId == id).ToList() : all;
    }

    /// <summary>
    /// Changes the user's share of an account or asset from <paramref name="from"/> on (spec §9). The share that
    /// applied before is kept for earlier dates, so past months and net worth history do not change.
    /// A change for a date that already has one corrects it. Changes cannot start in the future.
    /// </summary>
    public async Task ChangeShareAsync(Guid ownerId, DateOnly from, Share share, CancellationToken ct = default)
    {
        if (share.BasisPoints == 0)
        {
            throw new DomainValidationException("account.share.zero");
        }

        if (from > clock.Today)
        {
            throw new DomainValidationException("share.date.future");
        }

        var account = await accounts.FindAsync(ownerId, ct);
        var asset = account is null ? await GetAssetAsync(ownerId, ct) : null;
        if (account is { Type: AccountType.CreditCard })
        {
            throw new DomainValidationException("share.card");
        }

        var all = await ownership.ListAsync(ct);
        var own = all.Where(c => c.OwnerId == ownerId).ToList();
        var changed = new List<OwnershipChange>();
        if (own.Count == 0)
        {
            // Keep the share used so far, from the first date it mattered.
            var since = account?.OpeningDate ?? await FirstAssetDateAsync(asset!, ct);
            var current = account?.OwnershipShare ?? asset!.OwnershipShare;
            if (since < from)
            {
                var initial = account is not null
                    ? OwnershipChange.ForAccount(ownerId, since, current, clock.Now)
                    : OwnershipChange.ForAsset(ownerId, since, current, clock.Now);
                own.Add(initial);
                changed.Add(initial);
            }
        }

        var change = own.FirstOrDefault(c => c.EffectiveFrom == from);
        if (change is null)
        {
            change = account is not null
                ? OwnershipChange.ForAccount(ownerId, from, share, clock.Now)
                : OwnershipChange.ForAsset(ownerId, from, share, clock.Now);
            own.Add(change);
        }
        else
        {
            change.Change(share, clock.Now);
        }

        changed.Add(change);
        var today = OwnershipHistory.ShareAt(own, ownerId, share, clock.Today);
        account?.ChangeOwnershipShare(today, clock.Now);
        asset?.ChangeOwnershipShare(today, clock.Now);
        await ownership.SaveAsync(changed, account, asset, ct);
        await audit.RecordAsync(account is not null ? "Account" : "Asset", ownerId, "ShareChanged", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ValidateDate(DateOnly date)
    {
        if (date > clock.Today)
        {
            throw new DomainValidationException("valuation.date.future");
        }
    }

    private async Task<bool> IsManualAccountAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.FindAsync(id, ct);
        if (account is null)
        {
            return false;
        }

        if (!account.Type.IsManuallyValued())
        {
            // Current, savings and card values always come from the ledger (spec §17).
            throw new DomainValidationException("valuation.account.notManual");
        }

        return true;
    }

    private async Task<DateOnly> FirstAssetDateAsync(Asset asset, CancellationToken ct)
    {
        var created = DateOnly.FromDateTime(asset.CreatedAt.ToLocalTime().Date);
        var first = (await valuations.ListAsync(ct)).Where(v => v.AssetId == asset.Id).Select(v => (DateOnly?)v.Date).Min();
        return first is { } f && f < created ? f : created;
    }

    private async Task<Asset> GetAssetAsync(Guid id, CancellationToken ct) =>
        await assets.FindAsync(id, ct) ?? throw new DomainValidationException("asset.notFound");
}
