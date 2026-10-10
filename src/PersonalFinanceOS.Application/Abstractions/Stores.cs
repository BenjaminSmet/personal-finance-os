using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Budgets;
using PersonalFinanceOS.Domain.Cards;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Imports;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Abstractions;

public interface IAccountRepository
{
    Task<IReadOnlyList<Account>> ListAsync(bool includeArchived, CancellationToken ct = default);

    Task<Account?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds the account and, for a credit card, its settings together: all or nothing.</summary>
    Task AddAsync(Account account, CreditCardProfile? card = null, CancellationToken ct = default);

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

    /// <summary>Saves several categories (a new order) in one database transaction.</summary>
    Task UpdateAsync(IReadOnlyCollection<Category> categories, CancellationToken ct = default);

    /// <summary>
    /// Moves everything that uses <paramref name="source"/> (transactions, payee rules, recurring rules, import
    /// links) to <paramref name="targetId"/>, applies the budget changes and saves the archived source, all in one
    /// database transaction: all or nothing.
    /// </summary>
    Task MergeAsync(Category source, Guid targetId, IReadOnlyCollection<Budget> movedBudgets, IReadOnlyCollection<Budget> removedBudgets, CancellationToken ct = default);
}

public interface IBudgetRepository
{
    Task<IReadOnlyList<Budget>> ListAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates one budget and removes the given ones, in one database transaction.</summary>
    Task SaveAsync(Budget budget, IReadOnlyCollection<Budget> removed, CancellationToken ct = default);

    /// <summary>Removes budgets, in one database transaction.</summary>
    Task RemoveAsync(IReadOnlyCollection<Budget> removed, CancellationToken ct = default);
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

/// <summary>Everything one import changes, saved together: all or nothing (spec §8 step 10).</summary>
public sealed record ImportChanges(
    ImportBatch Batch,
    IReadOnlyCollection<Transaction> Added,
    IReadOnlyCollection<Transaction> Linked,
    IReadOnlyCollection<ImportTransferLink> Links,
    ImportProfile? Profile);

public interface IImportRepository
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<ImportBatch>> ListBatchesAsync(Guid? accountId = null, CancellationToken ct = default);

    Task<ImportBatch?> FindBatchAsync(Guid id, CancellationToken ct = default);

    /// <summary>Lines created by the import, voided ones included.</summary>
    Task<IReadOnlyList<Transaction>> ListBatchTransactionsAsync(Guid batchId, CancellationToken ct = default);

    Task<IReadOnlyList<ImportTransferLink>> ListLinksAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>Lines of one account between two dates, voided ones included, with the status of their import.</summary>
    Task<IReadOnlyList<(Transaction Transaction, ImportBatch? Batch)>> ListForDuplicateCheckAsync(Guid accountId, DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<ImportProfile?> FindProfileAsync(string headerKey, CancellationToken ct = default);

    /// <summary>Batch, new lines, linked lines and the remembered mapping in one database transaction.</summary>
    Task SaveImportAsync(ImportChanges changes, CancellationToken ct = default);

    /// <summary>The batch's new status and every changed line in one database transaction.</summary>
    Task SaveUndoAsync(ImportBatch batch, IReadOnlyCollection<Transaction> changed, CancellationToken ct = default);
}

public interface ICreditCardRepository
{
    Task<IReadOnlyList<CreditCardProfile>> ListAsync(CancellationToken ct = default);

    Task<CreditCardProfile?> FindAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Inserts or updates the settings of one card.</summary>
    Task SaveAsync(CreditCardProfile profile, CancellationToken ct = default);
}

public interface IRecurringRepository
{
    Task<IReadOnlyList<RecurringRule>> ListRulesAsync(bool includeEnded, CancellationToken ct = default);

    Task<RecurringRule?> FindRuleAsync(Guid id, CancellationToken ct = default);

    Task AddRuleAsync(RecurringRule rule, CancellationToken ct = default);

    Task UpdateRuleAsync(RecurringRule rule, CancellationToken ct = default);

    Task<IReadOnlyList<ScheduledItem>> ListItemsAsync(CancellationToken ct = default);

    Task<ScheduledItem?> FindItemAsync(Guid ruleId, DateOnly occurrenceDate, CancellationToken ct = default);

    /// <summary>
    /// Saves the decision and the transactions it created (for a confirmation) in one database
    /// transaction, so a planned item is never linked to a transaction that was not saved.
    /// </summary>
    Task SaveItemAsync(ScheduledItem item, IReadOnlyCollection<Transaction> created, CancellationToken ct = default);

    /// <summary>Removes a decision, so the occurrence is open again. Only decisions without a live transaction.</summary>
    Task DeleteItemAsync(ScheduledItem item, CancellationToken ct = default);
}
