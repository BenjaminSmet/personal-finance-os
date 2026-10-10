using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;

namespace PersonalFinanceOS.Application.Accounts;

public sealed record CreateAccountRequest(
    string Name,
    AccountType Type,
    DateOnly OpeningDate,
    Money OpeningBalance,
    Share OwnershipShare,
    string? Notes = null);

public sealed class AccountService(IAccountRepository accounts, IAuditLog audit, IClock clock)
{
    public Task<IReadOnlyList<Account>> ListAsync(bool includeArchived = false, CancellationToken ct = default) =>
        accounts.ListAsync(includeArchived, ct);

    public async Task<Account> CreateAsync(CreateAccountRequest request, CancellationToken ct = default)
    {
        var existing = await accounts.ListAsync(includeArchived: true, ct);
        var sortOrder = existing.Count == 0 ? 0 : existing.Max(a => a.SortOrder) + 1;

        var account = Account.Create(
            request.Name,
            request.Type,
            request.OpeningDate,
            request.OpeningBalance,
            request.OwnershipShare,
            clock.Now,
            request.Notes,
            sortOrder);

        await accounts.AddAsync(account, ct);
        await audit.RecordAsync("Account", account.Id, "Created", ct);
        return account;
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        var account = await GetRequiredAsync(id, ct);
        account.Rename(name, clock.Now);
        await accounts.UpdateAsync(account, ct);
        await audit.RecordAsync("Account", id, "Renamed", ct);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var account = await GetRequiredAsync(id, ct);
        account.Archive(clock.Now);
        await accounts.UpdateAsync(account, ct);
        await audit.RecordAsync("Account", id, "Archived", ct);
    }

    private async Task<Account> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await accounts.FindAsync(id, ct) ?? throw new DomainValidationException("account.notFound");
}
