using PersonalFinanceOS.Domain.Accounts;

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
