using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.QuickAdd;

/// <summary>
/// Global quick entry (spec §7): interpret a line, let the user confirm or correct it, then post it
/// and remember the confirmed choices for next time.
/// </summary>
public sealed class QuickAddService(
    AccountService accounts,
    CategoryService categories,
    IPayeeRuleRepository rules,
    ISettingsStore settings,
    LedgerService ledger,
    IClock clock)
{
    public const string LastAccountKey = "quickAdd.lastAccountId";

    public async Task<QuickAddContext> LoadContextAsync(CancellationToken ct = default)
    {
        var last = Guid.TryParse(await settings.GetAsync(LastAccountKey, ct), out var id) ? id : (Guid?)null;
        return new QuickAddContext(
            await accounts.ListAsync(includeArchived: false, ct),
            await categories.ListAsync(includeArchived: false, ct),
            await rules.ListAsync(ct),
            last,
            clock.Today);
    }

    public async Task<QuickAddDraft> InterpretAsync(string? input, CancellationToken ct = default) =>
        QuickAddParser.Parse(input, await LoadContextAsync(ct));

    /// <summary>
    /// Posts the (possibly corrected) draft. Only now is anything written to the ledger.
    /// The confirmed payee → type, category and account are learned, so a correction wins next time.
    /// </summary>
    public async Task<IReadOnlyList<Transaction>> ConfirmAsync(QuickAddDraft draft, CancellationToken ct = default)
    {
        if (draft.AccountId is not { } accountId)
        {
            throw new DomainValidationException("quickAdd.noAccount");
        }

        IReadOnlyList<Transaction> posted;
        if (draft.IsTransfer)
        {
            if (draft.DestinationAccountId is not { } destination)
            {
                throw new DomainValidationException("quickAdd.noDestination");
            }

            var (from, to) = await ledger.RecordTransferAsync(
                new TransferRequest(accountId, destination, draft.Date, draft.Amount, draft.Description, null, TransactionSource.QuickAdd), ct);
            posted = [from, to];
        }
        else
        {
            posted = [await ledger.RecordAsync(
                new TransactionRequest(accountId, draft.Date, draft.Amount, draft.Type, draft.Description, draft.CategoryId, null, TransactionSource.QuickAdd), ct)];
            await LearnAsync(draft, accountId, ct);
        }

        await settings.SetAsync(LastAccountKey, accountId.ToString("D"), ct);
        return posted;
    }

    private async Task LearnAsync(QuickAddDraft draft, Guid accountId, CancellationToken ct)
    {
        var key = PayeeRule.Normalize(draft.Description);
        if (key.Length == 0)
        {
            return;
        }

        var existing = (await rules.ListAsync(ct)).FirstOrDefault(r => r.PayeeKey == key);
        if (existing is null)
        {
            existing = PayeeRule.Learn(draft.Description, draft.Type, draft.CategoryId, accountId, clock.Now);
        }
        else
        {
            existing.Update(draft.Type, draft.CategoryId, accountId, clock.Now);
        }

        await rules.SaveAsync(existing, ct);
    }
}
