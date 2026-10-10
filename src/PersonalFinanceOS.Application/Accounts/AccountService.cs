using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Cards;

namespace PersonalFinanceOS.Application.Accounts;

public sealed record CreateAccountRequest(
    string Name,
    AccountType Type,
    DateOnly OpeningDate,
    Money OpeningBalance,
    Share OwnershipShare,
    string? Notes = null,

    /// <summary>Required for a credit card (spec §10): limit and statement period are entered, never assumed.</summary>
    CardSettings? Card = null);

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

        CreditCardProfile? card = null;
        if (request.Type.IsLiability())
        {
            if (request.Card is not { } settings)
            {
                throw new DomainValidationException("card.settings.required");
            }

            if (!await CreditCardService.IsValidSettlementAccountAsync(accounts, account.Id, settings.SettlementAccountId, ct))
            {
                throw new DomainValidationException("card.settlementAccount.invalid");
            }

            card = CreditCardProfile.Create(account.Id, settings.CreditLimit, settings.StatementStartDay, settings.SettlementDay, settings.SettlementAccountId, clock.Now);
        }

        await accounts.AddAsync(account, card, ct);
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
