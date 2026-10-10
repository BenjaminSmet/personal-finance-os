using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Abstractions;

public interface IAccountRepository
{
    Task<IReadOnlyList<Account>> ListAsync(bool includeArchived, CancellationToken ct = default);

    Task<Account?> FindAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(Account account, CancellationToken ct = default);

    Task UpdateAsync(Account account, CancellationToken ct = default);
}

/// <summary>Simple key/value settings persisted in the local database.</summary>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);
}

/// <summary>
/// Local audit trail for important changes (spec §7). Entries describe what changed, not amounts,
/// and live only inside the local database.
/// </summary>
public interface IAuditLog
{
    Task RecordAsync(string entity, Guid? entityId, string action, CancellationToken ct = default);
}

public sealed record TransactionQuery(
    Guid? AccountId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    bool IncludeVoided = false);

public interface ITransactionRepository
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<Transaction>> ListAsync(TransactionQuery query, CancellationToken ct = default);

    Task<Transaction?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>Both sides of a transfer.</summary>
    Task<IReadOnlyList<Transaction>> FindTransferAsync(Guid transferId, CancellationToken ct = default);

    /// <summary>Adds all given transactions in one database transaction: all or nothing.</summary>
    Task AddAsync(IReadOnlyCollection<Transaction> transactions, CancellationToken ct = default);

    /// <summary>Saves all given transactions in one database transaction: all or nothing.</summary>
    Task UpdateAsync(IReadOnlyCollection<Transaction> transactions, CancellationToken ct = default);
}

public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> ListAsync(bool includeArchived, CancellationToken ct = default);

    Task<Category?> FindAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(IReadOnlyCollection<Category> categories, CancellationToken ct = default);

    Task UpdateAsync(Category category, CancellationToken ct = default);
}

public interface IPayeeRuleRepository
{
    Task<IReadOnlyList<PayeeRule>> ListAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates the rule for its payee key.</summary>
    Task SaveAsync(PayeeRule rule, CancellationToken ct = default);
}

public interface IReconciliationRepository
{
    /// <summary>Newest statement date first.</summary>
    Task<IReadOnlyList<ReconciliationRecord>> ListAsync(Guid? accountId, CancellationToken ct = default);

    /// <summary>Saves the record and, when given, its adjustment transaction together: all or nothing.</summary>
    Task AddAsync(ReconciliationRecord record, Transaction? adjustment, CancellationToken ct = default);
}
