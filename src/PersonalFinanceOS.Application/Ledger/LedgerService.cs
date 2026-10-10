using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Ledger;

/// <summary>Income, expense or refund. <see cref="Amount"/> is positive; the type gives the direction.</summary>
public sealed record TransactionRequest(
    Guid AccountId,
    DateOnly Date,
    Money Amount,
    TransactionType Type,
    string? Description = null,
    Guid? CategoryId = null,
    string? Note = null,
    TransactionSource Source = TransactionSource.Manual);

/// <summary>A movement between two own accounts. <see cref="Amount"/> is positive.</summary>
public sealed record TransferRequest(
    Guid FromAccountId,
    Guid ToAccountId,
    DateOnly Date,
    Money Amount,
    string? Description = null,
    string? Note = null,
    TransactionSource Source = TransactionSource.Manual);

/// <summary>How a ledger line is shown. Card purchases and settlements are recognised from the account type.</summary>
public enum EntryKind
{
    Income,
    Expense,
    CardPurchase,
    Refund,
    Transfer,
    CardSettlement,
    Adjustment,
}

/// <summary>A transaction with the names a screen needs.</summary>
public sealed record LedgerEntry(
    Transaction Transaction,
    Account Account,
    Account? Counterpart,
    Category? Category,
    EntryKind Kind);

/// <summary>
/// Records, corrects and voids ledger transactions, and calculates balances and totals through
/// <see cref="LedgerMath"/>. Every change is validated, audited and can be undone.
/// </summary>
public sealed class LedgerService(
    ITransactionRepository transactions,
    IAccountRepository accounts,
    ICategoryRepository categories,
    IAuditLog audit,
    UndoHistory undo,
    IClock clock)
{
    /// <summary>Raised after any change to the ledger, including undo.</summary>
    public event EventHandler? Changed;

    public UndoHistory History => undo;

    public async Task<Transaction> RecordAsync(TransactionRequest request, CancellationToken ct = default)
    {
        await ValidateAsync(request, current: null, ct);
        var transaction = Transaction.Create(
            request.AccountId, request.Date, request.Amount, request.Type, request.Description, request.CategoryId, request.Note, request.Source, clock.Now);

        await transactions.AddAsync([transaction], ct);
        await audit.RecordAsync("Transaction", transaction.Id, "Created", ct);
        Done("created", [new UndoChange(transaction.Id, null, transaction.Snapshot())]);
        return transaction;
    }

    public async Task<(Transaction From, Transaction To)> RecordTransferAsync(TransferRequest request, CancellationToken ct = default)
    {
        await ValidateTransferAsync(request, current: null, ct);
        var (from, to) = Transaction.CreateTransfer(
            request.FromAccountId, request.ToAccountId, request.Date, request.Amount, request.Description, request.Note, request.Source, clock.Now);

        await transactions.AddAsync([from, to], ct);
        await audit.RecordAsync("Transfer", from.TransferId, "Created", ct);
        Done("created", [new UndoChange(from.Id, null, from.Snapshot()), new UndoChange(to.Id, null, to.Snapshot())]);
        return (from, to);
    }

    /// <summary>Corrects an income, expense or refund.</summary>
    public async Task UpdateAsync(Guid id, TransactionRequest request, CancellationToken ct = default)
    {
        var transaction = await GetRequiredAsync(id, ct);
        if (transaction.IsTransfer || transaction.Type == TransactionType.Adjustment)
        {
            throw new DomainValidationException("transaction.wrongKind");
        }

        await ValidateAsync(request, transaction, ct);
        var before = transaction.Snapshot();
        transaction.Edit(request.AccountId, request.Date, request.Amount, request.Type, request.Description, request.CategoryId, request.Note, clock.Now);

        await transactions.UpdateAsync([transaction], ct);
        await audit.RecordAsync("Transaction", id, "Edited", ct);
        Done("edited", [new UndoChange(id, before, transaction.Snapshot())]);
    }

    /// <summary>Changes both sides of a transfer together.</summary>
    public async Task UpdateTransferAsync(Guid transferId, TransferRequest request, CancellationToken ct = default)
    {
        var (from, to) = await GetTransferAsync(transferId, ct);
        await ValidateTransferAsync(request, (from, to), ct);

        var beforeFrom = from.Snapshot();
        var beforeTo = to.Snapshot();
        from.EditTransferLeg(request.FromAccountId, request.Date, request.Amount, outgoing: true, request.Description, request.Note, clock.Now);
        to.EditTransferLeg(request.ToAccountId, request.Date, request.Amount, outgoing: false, request.Description, request.Note, clock.Now);

        await transactions.UpdateAsync([from, to], ct);
        await audit.RecordAsync("Transfer", transferId, "Edited", ct);
        Done("edited", [new UndoChange(from.Id, beforeFrom, from.Snapshot()), new UndoChange(to.Id, beforeTo, to.Snapshot())]);
    }

    /// <summary>Voids a transaction; for a transfer both sides are voided together.</summary>
    public async Task VoidAsync(Guid id, CancellationToken ct = default)
    {
        var transaction = await GetRequiredAsync(id, ct);
        IReadOnlyList<Transaction> affected = transaction.TransferId is { } transferId
            ? [.. (await GetTransferAsync(transferId, ct)).AsList()]
            : [transaction];

        var changes = new List<UndoChange>();
        foreach (var t in affected)
        {
            var before = t.Snapshot();
            t.Void(clock.Now);
            changes.Add(new UndoChange(t.Id, before, t.Snapshot()));
        }

        await transactions.UpdateAsync(affected, ct);
        await audit.RecordAsync(transaction.IsTransfer ? "Transfer" : "Transaction", transaction.TransferId ?? id, "Voided", ct);
        Done("voided", changes);
    }

    /// <summary>
    /// Reverses the most recent action of this session. Refuses (and forgets the action) when the
    /// transactions were changed in another way since, so an undo never overwrites newer data.
    /// </summary>
    public async Task<bool> UndoAsync(CancellationToken ct = default)
    {
        var entry = undo.Peek();
        if (entry is null)
        {
            return false;
        }

        var loaded = new List<Transaction>();
        foreach (var change in entry.Changes)
        {
            var transaction = await transactions.FindAsync(change.TransactionId, ct);
            if (transaction is null || transaction.Snapshot() != change.After)
            {
                undo.Remove(entry);
                throw new DomainValidationException("undo.stale");
            }

            if (change.Before is null)
            {
                transaction.Void(clock.Now);
            }
            else
            {
                transaction.RestoreSnapshot(change.Before, clock.Now);
            }

            loaded.Add(transaction);
        }

        await transactions.UpdateAsync(loaded, ct);
        foreach (var t in loaded)
        {
            await audit.RecordAsync("Transaction", t.Id, "Undone", ct);
        }

        undo.Remove(entry);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<IReadOnlyList<LedgerEntry>> ListEntriesAsync(TransactionQuery query, CancellationToken ct = default)
    {
        var list = await transactions.ListAsync(query, ct);
        var accountById = (await accounts.ListAsync(includeArchived: true, ct)).ToDictionary(a => a.Id);
        var categoryById = (await categories.ListAsync(includeArchived: true, ct)).ToDictionary(c => c.Id);

        // Transfer partners may fall outside the filter (other account), so look them up separately.
        var legsByTransfer = new Dictionary<Guid, IReadOnlyList<Transaction>>();
        foreach (var transferId in list.Where(t => t.TransferId is not null).Select(t => t.TransferId!.Value).Distinct())
        {
            legsByTransfer[transferId] = await transactions.FindTransferAsync(transferId, ct);
        }

        var entries = new List<LedgerEntry>(list.Count);
        foreach (var t in list)
        {
            var account = accountById[t.AccountId];
            var partner = t.TransferId is { } id ? legsByTransfer[id].FirstOrDefault(l => l.Id != t.Id) : null;
            var counterpart = partner is null ? null : accountById.GetValueOrDefault(partner.AccountId);
            var category = t.CategoryId is { } c ? categoryById.GetValueOrDefault(c) : null;
            entries.Add(new LedgerEntry(t, account, counterpart, category, KindOf(t, account, counterpart)));
        }

        return entries;
    }

    /// <summary>Balance of every account (archived included) at the end of <paramref name="asOf"/>, or now.</summary>
    public async Task<IReadOnlyDictionary<Guid, Money>> GetBalancesAsync(DateOnly? asOf = null, CancellationToken ct = default)
    {
        var all = await accounts.ListAsync(includeArchived: true, ct);
        var active = await transactions.ListAsync(new TransactionQuery(To: asOf), ct);
        var byAccount = active.ToLookup(t => t.AccountId);
        return all.ToDictionary(a => a.Id, a => LedgerMath.Balance(a, byAccount[a.Id], asOf));
    }

    public async Task<Money> GetBalanceAsync(Guid accountId, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var account = await accounts.FindAsync(accountId, ct) ?? throw new DomainValidationException("account.notFound");
        var list = await transactions.ListAsync(new TransactionQuery(AccountId: accountId, To: asOf), ct);
        return LedgerMath.Balance(account, list, asOf);
    }

    /// <summary>Income and spending between two dates, optionally for one account. Transfers never count.</summary>
    public async Task<PeriodTotals> GetTotalsAsync(DateOnly from, DateOnly to, Guid? accountId = null, CancellationToken ct = default)
    {
        var list = await transactions.ListAsync(new TransactionQuery(AccountId: accountId, From: from, To: to), ct);
        return LedgerMath.Totals(list, from, to);
    }

    internal static EntryKind KindOf(Transaction t, Account account, Account? counterpart) => t.Type switch
    {
        TransactionType.Income => EntryKind.Income,
        TransactionType.Refund => EntryKind.Refund,
        TransactionType.Expense => account.Type.IsLiability() ? EntryKind.CardPurchase : EntryKind.Expense,
        TransactionType.Transfer when account.Type.IsLiability() || counterpart?.Type.IsLiability() == true => EntryKind.CardSettlement,
        TransactionType.Transfer => EntryKind.Transfer,
        _ => EntryKind.Adjustment,
    };

    /// <summary>For services that change the ledger without going through this class (reconciliation).</summary>
    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The same checks as <see cref="RecordAsync"/>, for services that save the line together with their own data.</summary>
    internal Task ValidateNewAsync(TransactionRequest request, CancellationToken ct) => ValidateAsync(request, current: null, ct);

    /// <summary>The same checks as <see cref="RecordTransferAsync"/>, for services that save the transfer together with their own data.</summary>
    internal Task ValidateNewTransferAsync(TransferRequest request, CancellationToken ct) => ValidateTransferAsync(request, current: null, ct);

    /// <summary>Audits lines another service saved after <see cref="ValidateNewAsync"/> and makes them undoable like any other.</summary>
    internal async Task RecordedAsync(IReadOnlyList<Transaction> created, CancellationToken ct)
    {
        var first = created[0];
        await audit.RecordAsync(first.IsTransfer ? "Transfer" : "Transaction", first.TransferId ?? first.Id, "Created", ct);
        Done("created", [.. created.Select(t => new UndoChange(t.Id, null, t.Snapshot()))]);
    }

    private void Done(string actionKey, IReadOnlyList<UndoChange> changes)
    {
        undo.Push(new UndoEntry(actionKey, changes));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ValidateAsync(TransactionRequest request, Transaction? current, CancellationToken ct)
    {
        if (!request.Type.IsIncomeOrSpending())
        {
            throw new DomainValidationException("transaction.wrongKind");
        }

        var account = await ValidateAccountAsync(request.AccountId, request.Date, current?.AccountId, ct);
        if (request.Type == TransactionType.Income && account.Type.IsLiability())
        {
            // Money back on a card is a refund (spec §10); income never lands on a credit card.
            throw new DomainValidationException("transaction.income.liability");
        }

        if (request.CategoryId is { } categoryId)
        {
            var category = await categories.FindAsync(categoryId, ct) ?? throw new DomainValidationException("category.notFound");
            if (category.Kind != request.Type.CategoryKind())
            {
                throw new DomainValidationException("transaction.category.kindMismatch");
            }

            if (!category.IsActive && current?.CategoryId != categoryId)
            {
                throw new DomainValidationException("category.archived");
            }
        }
    }

    private async Task ValidateTransferAsync(TransferRequest request, (Transaction From, Transaction To)? current, CancellationToken ct)
    {
        if (request.FromAccountId == request.ToAccountId)
        {
            throw new DomainValidationException("transfer.sameAccount");
        }

        await ValidateAccountAsync(request.FromAccountId, request.Date, current?.From.AccountId, ct);
        await ValidateAccountAsync(request.ToAccountId, request.Date, current?.To.AccountId, ct);
    }

    private async Task<Account> ValidateAccountAsync(Guid accountId, DateOnly date, Guid? currentAccountId, CancellationToken ct)
    {
        var account = await accounts.FindAsync(accountId, ct) ?? throw new DomainValidationException("account.notFound");
        if (account.Status != AccountStatus.Active && currentAccountId != accountId)
        {
            throw new DomainValidationException("account.archived");
        }

        if (date < account.OpeningDate)
        {
            // The opening balance already contains everything before that date; counting it again would double count.
            throw new DomainValidationException("transaction.date.beforeOpening");
        }

        return account;
    }

    private async Task<Transaction> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await transactions.FindAsync(id, ct) ?? throw new DomainValidationException("transaction.notFound");

    private async Task<(Transaction From, Transaction To)> GetTransferAsync(Guid transferId, CancellationToken ct)
    {
        var legs = await transactions.FindTransferAsync(transferId, ct);
        var from = legs.SingleOrDefault(l => l.Amount.IsNegative);
        var to = legs.SingleOrDefault(l => l.Amount.IsPositive);
        if (legs.Count != 2 || from is null || to is null)
        {
            throw new DomainValidationException("transaction.notFound");
        }

        return (from, to);
    }
}

internal static class TransferLegs
{
    public static IReadOnlyList<Transaction> AsList(this (Transaction From, Transaction To) legs) => [legs.From, legs.To];
}
