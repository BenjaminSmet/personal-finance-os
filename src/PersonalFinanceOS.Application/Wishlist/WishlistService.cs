using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Application.Wishlist;

public sealed record WishlistRequest(
    string Name,
    Money ExpectedPrice,
    WishlistPriority Priority = WishlistPriority.Normal,
    DateOnly? TargetDate = null,
    Money? MonthlySaving = null,
    string? Url = null,
    string? Note = null,
    string? Alternative = null);

/// <summary>
/// Buying an item. With <see cref="AccountId"/> an expense is recorded on that account (a current account, cash or a
/// credit card); without it the item is only marked as bought, for when the expense is already in the ledger.
/// </summary>
public sealed record PurchaseRequest(DateOnly Date, Money Amount, Guid? AccountId, Guid? CategoryId = null);

/// <summary>An item with its recorded purchase, if any.</summary>
public sealed record WishlistEntry(WishlistItem Item, Transaction? Purchase)
{
    /// <summary>Bought with a recorded expense that was voided or undone since: the item is open again.</summary>
    public bool PurchaseRemoved => Item.Status == WishlistStatus.Purchased && Item.PurchaseTransactionId is not null && Purchase is not { IsActive: true };

    /// <summary>The status to show and calculate with.</summary>
    public WishlistStatus Status => PurchaseRemoved ? WishlistStatus.Desired : Item.Status;

    public bool IsOpen => Status is WishlistStatus.Desired or WishlistStatus.Saving or WishlistStatus.Postponed;
}

/// <summary>The wishlist (spec §14). Buying records at most one expense per item and only after the user confirms it.</summary>
public sealed class WishlistService(
    IWishlistRepository items,
    ITransactionRepository transactions,
    LedgerService ledger,
    IAuditLog audit,
    IClock clock)
{
    /// <summary>Raised after items change.</summary>
    public event EventHandler? Changed;

    /// <summary>Open items first, by priority and target date; bought and cancelled items last.</summary>
    public async Task<IReadOnlyList<WishlistEntry>> ListAsync(CancellationToken ct = default)
    {
        var list = await items.ListAsync(ct);
        var entries = new List<WishlistEntry>(list.Count);
        foreach (var item in list)
        {
            var purchase = item.PurchaseTransactionId is { } id ? await transactions.FindAsync(id, ct) : null;
            entries.Add(new WishlistEntry(item, purchase));
        }

        return entries
            .OrderBy(e => e.IsOpen ? 0 : 1)
            .ThenBy(e => e.Item.Priority)
            .ThenBy(e => e.Item.TargetDate ?? DateOnly.MaxValue)
            .ThenBy(e => e.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<WishlistItem> CreateAsync(WishlistRequest request, CancellationToken ct = default)
    {
        var item = WishlistItem.Create(
            request.Name, request.ExpectedPrice, request.Priority, request.TargetDate, request.MonthlySaving, request.Url, request.Note, request.Alternative, clock.Now);
        await items.AddAsync(item, ct);
        await audit.RecordAsync("WishlistItem", item.Id, "Created", ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public async Task UpdateAsync(Guid id, WishlistRequest request, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        item.Change(request.Name, request.ExpectedPrice, request.Priority, request.TargetDate, request.MonthlySaving, request.Url, request.Note, request.Alternative, clock.Now);
        await items.UpdateAsync(item, ct);
        await audit.RecordAsync("WishlistItem", id, "Edited", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Desired, saving, postponed or cancelled. An item only marked as bought can be reopened; one bought with a live
    /// expense keeps its status until that expense is voided, so the ledger and the list never disagree.
    /// </summary>
    public async Task SetStatusAsync(Guid id, WishlistStatus status, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        if (item.PurchaseTransactionId is { } tid && await transactions.FindAsync(tid, ct) is { IsActive: true })
        {
            throw new DomainValidationException("wishlist.alreadyPurchased");
        }

        item.ChangeStatus(status, clock.Now);
        await items.UpdateAsync(item, ct);
        await audit.RecordAsync("WishlistItem", id, "StatusChanged", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Bought: records the expense (when an account is given) and marks the item in one save, so it is never recorded
    /// twice. Undoing that expense makes the item open again.
    /// </summary>
    public async Task<Transaction?> PurchaseAsync(Guid id, PurchaseRequest request, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        await EnsureNotBoughtAsync(item, ct);
        Transaction? created = null;
        if (request.AccountId is { } accountId)
        {
            var transaction = new TransactionRequest(accountId, request.Date, request.Amount, TransactionType.Expense, item.Name, request.CategoryId);
            await ledger.ValidateNewAsync(transaction, ct);
            created = Transaction.Create(accountId, request.Date, request.Amount, TransactionType.Expense, item.Name, request.CategoryId, null, TransactionSource.Manual, clock.Now);
        }
        else if (request.Date > clock.Today)
        {
            throw new DomainValidationException("wishlist.purchase.future");
        }

        item.MarkPurchased(request.Date, created?.Id, clock.Now);
        await items.SavePurchaseAsync(item, created, ct);
        await audit.RecordAsync("WishlistItem", id, "Purchased", ct);
        if (created is not null)
        {
            await ledger.RecordedAsync([created], ct);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return created;
    }

    private async Task EnsureNotBoughtAsync(WishlistItem item, CancellationToken ct)
    {
        if (item.Status != WishlistStatus.Purchased)
        {
            return;
        }

        var purchase = item.PurchaseTransactionId is { } tid ? await transactions.FindAsync(tid, ct) : null;
        if (item.PurchaseTransactionId is null || purchase is { IsActive: true })
        {
            throw new DomainValidationException("wishlist.alreadyPurchased");
        }
    }

    private async Task<WishlistItem> GetAsync(Guid id, CancellationToken ct) =>
        await items.FindAsync(id, ct) ?? throw new DomainValidationException("wishlist.notFound");
}
