using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Ledger;

/// <summary>What the app calculated versus what the bank statement says.</summary>
public sealed record ReconciliationPreview(Money Calculated, Money Statement)
{
    /// <summary>Statement minus app. Zero means the account is reconciled.</summary>
    public Money Difference => Statement - Calculated;
}

/// <summary>
/// Compares the app's balance with a statement balance (spec §5). Never alters balances to hide a
/// difference: it is recorded and shown, and only becomes an adjustment when the user asks for one,
/// with a reason.
/// </summary>
public sealed class ReconciliationService(
    LedgerService ledger,
    IAccountRepository accounts,
    IReconciliationRepository reconciliations,
    IAuditLog audit,
    IClock clock)
{
    public async Task<ReconciliationPreview> PreviewAsync(Guid accountId, DateOnly statementDate, Money statementBalance, CancellationToken ct = default)
    {
        await RequireDateAsync(accountId, statementDate, ct);
        return new ReconciliationPreview(await ledger.GetBalanceAsync(accountId, statementDate, ct), statementBalance);
    }

    public async Task<ReconciliationRecord> RecordAsync(
        Guid accountId,
        DateOnly statementDate,
        Money statementBalance,
        bool addAdjustment,
        string? reason,
        CancellationToken ct = default)
    {
        var preview = await PreviewAsync(accountId, statementDate, statementBalance, ct);
        Transaction? adjustment = null;
        if (addAdjustment && !preview.Difference.IsZero)
        {
            adjustment = Transaction.CreateAdjustment(accountId, statementDate, preview.Difference, reason ?? string.Empty, clock.Now);
        }

        var record = ReconciliationRecord.Create(
            accountId, statementDate, statementBalance, preview.Calculated, adjustment?.Id, adjustment is null ? reason : null, clock.Now);

        await reconciliations.AddAsync(record, adjustment, ct);
        await audit.RecordAsync("Reconciliation", record.Id, adjustment is null ? "Recorded" : "RecordedWithAdjustment", ct);
        if (adjustment is not null)
        {
            ledger.RaiseChanged();
        }

        return record;
    }

    /// <summary>The most recent reconciliation per account.</summary>
    public async Task<IReadOnlyDictionary<Guid, ReconciliationRecord>> LatestAsync(CancellationToken ct = default) =>
        (await reconciliations.ListAsync(accountId: null, ct))
            .GroupBy(r => r.AccountId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StatementDate).ThenByDescending(r => r.CreatedAt).First());

    private async Task RequireDateAsync(Guid accountId, DateOnly statementDate, CancellationToken ct)
    {
        var account = await accounts.FindAsync(accountId, ct) ?? throw new DomainValidationException("account.notFound");
        if (statementDate < account.OpeningDate)
        {
            throw new DomainValidationException("transaction.date.beforeOpening");
        }
    }
}
