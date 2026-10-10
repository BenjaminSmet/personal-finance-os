using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Cards;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Cards;

/// <summary>What the user enters for a credit card (spec §10). Nothing has a hard-coded value.</summary>
public sealed record CardSettings(Money CreditLimit, int StatementStartDay, int? SettlementDay, Guid? SettlementAccountId);

/// <summary>One expected payment of a card statement.</summary>
public sealed record CardSettlement(
    Account Card,
    StatementPeriod Period,
    Money Amount,

    /// <summary>Null when the debit day is not set yet.</summary>
    DateOnly? Date,
    Guid? FromAccountId);

/// <summary>
/// The state of one credit card, calculated from recorded transactions and open planned card
/// payments (spec §10 display). Every amount is an estimate until reconciled with a real statement.
/// </summary>
public sealed record CardStatus(
    Account Card,
    CreditCardProfile? Profile,

    /// <summary>What is owed on the card now (positive = debt).</summary>
    Money Outstanding,

    /// <summary>Limit minus what is owed. Not cash: never add it to spendable money.</summary>
    Money AvailableCredit,
    StatementPeriod CurrentPeriod,

    /// <summary>Card spending recorded in the current period, by purchase date. Refunds lower it.</summary>
    Money CurrentSpending,

    /// <summary>Open planned card payments (subscriptions…) falling in the current period.</summary>
    Money PlannedInPeriod,

    /// <summary>The statement for the current period as it stands now, including planned card payments.</summary>
    Money EstimatedStatement,
    StatementPeriod LastPeriod,

    /// <summary>What was owed when the last period closed: the statement to be paid.</summary>
    Money LastStatement,

    /// <summary>Payments to the card recorded since the last period closed.</summary>
    Money PaidSinceClose,

    /// <summary>Paid minus statement, when a payment was recorded and it differs. Shown, never corrected silently.</summary>
    Money? SettlementDifference,
    CardSettlement? NextSettlement)
{
    public bool IsSetUp => Profile is not null;

    public bool IsOverLimit => Profile is not null && AvailableCredit.IsNegative;
}

/// <summary>Credit-card settings and the calculated card state (spec §10).</summary>
public sealed class CreditCardService(
    ICreditCardRepository cards,
    IAccountRepository accounts,
    ITransactionRepository transactions,
    RecurringService recurring,
    IAuditLog audit,
    IClock clock)
{
    /// <summary>Raised after card settings change.</summary>
    public event EventHandler? Changed;

    public Task<CreditCardProfile?> GetSettingsAsync(Guid accountId, CancellationToken ct = default) => cards.FindAsync(accountId, ct);

    public async Task SaveSettingsAsync(Guid accountId, CardSettings settings, CancellationToken ct = default)
    {
        var account = await accounts.FindAsync(accountId, ct) ?? throw new DomainValidationException("account.notFound");
        if (!account.Type.IsLiability())
        {
            throw new DomainValidationException("card.notACard");
        }

        await ValidateSettlementAccountAsync(accountId, settings.SettlementAccountId, ct);
        var profile = await cards.FindAsync(accountId, ct);
        if (profile is null)
        {
            profile = CreditCardProfile.Create(accountId, settings.CreditLimit, settings.StatementStartDay, settings.SettlementDay, settings.SettlementAccountId, clock.Now);
        }
        else
        {
            profile.Change(settings.CreditLimit, settings.StatementStartDay, settings.SettlementDay, settings.SettlementAccountId, clock.Now);
        }

        await cards.SaveAsync(profile, ct);
        await audit.RecordAsync("CreditCard", accountId, "SettingsChanged", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The state of every active credit card, set up or not.</summary>
    public async Task<IReadOnlyList<CardStatus>> ListStatusesAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var list = (await accounts.ListAsync(includeArchived: false, ct)).Where(a => a.Type.IsLiability()).ToList();
        if (list.Count == 0)
        {
            return [];
        }

        var profiles = (await cards.ListAsync(ct)).ToDictionary(p => p.AccountId);
        var planned = await recurring.ListOpenAsync(today.AddDays(400), withMatches: false, ct);
        var result = new List<CardStatus>(list.Count);
        foreach (var card in list)
        {
            var lines = await transactions.ListAsync(new TransactionQuery(AccountId: card.Id), ct);
            var cardPlanned = planned.Where(p => p.Rule.Kind == RecurringKind.Expense && p.Rule.AccountId == card.Id).ToList();
            result.Add(CardMath.Status(card, profiles.GetValueOrDefault(card.Id), lines, cardPlanned, today));
        }

        return result;
    }

    /// <summary>
    /// Expected statement payments up to <paramref name="until"/>: the unpaid part of the closed
    /// statement, the current period, and later periods with planned card payments.
    /// </summary>
    public async Task<IReadOnlyList<CardSettlement>> ListSettlementsAsync(DateOnly until, CancellationToken ct = default)
    {
        var today = clock.Today;
        var statuses = await ListStatusesAsync(ct);
        var planned = await recurring.ListOpenAsync(until.AddDays(62), withMatches: false, ct);
        var result = new List<CardSettlement>();
        foreach (var status in statuses.Where(s => s.Profile is not null))
        {
            var cardPlanned = planned.Where(p => p.Rule.Kind == RecurringKind.Expense && p.Rule.AccountId == status.Card.Id).ToList();
            result.AddRange(CardMath.Settlements(status, cardPlanned, today, until));
        }

        return result;
    }

    private async Task ValidateSettlementAccountAsync(Guid cardId, Guid? settlementAccountId, CancellationToken ct)
    {
        if (!await IsValidSettlementAccountAsync(accounts, cardId, settlementAccountId, ct))
        {
            throw new DomainValidationException("card.settlementAccount.invalid");
        }
    }

    /// <summary>The paying account must be another active account that holds money (not a card).</summary>
    internal static async Task<bool> IsValidSettlementAccountAsync(IAccountRepository accounts, Guid cardId, Guid? settlementAccountId, CancellationToken ct)
    {
        if (settlementAccountId is not { } id)
        {
            return true;
        }

        var payer = await accounts.FindAsync(id, ct);
        return payer is not null && id != cardId && !payer.Type.IsLiability() && payer.Status == AccountStatus.Active;
    }
}

/// <summary>The card calculations, free of storage so they are easy to test (spec §10, §23 VISA).</summary>
public static class CardMath
{
    public static CardStatus Status(Account card, CreditCardProfile? profile, IEnumerable<Transaction> lines, IReadOnlyList<PlannedOccurrence> planned, DateOnly today)
    {
        var active = lines.Where(t => t.IsActive && t.AccountId == card.Id).ToList();
        var outstanding = LedgerMath.Balance(card, active, today).Negate();
        if (profile is null)
        {
            var empty = new StatementPeriod(today, today);
            return new CardStatus(card, null, outstanding, Money.Zero, empty, Money.Zero, Money.Zero, Money.Zero, empty, Money.Zero, Money.Zero, null, null);
        }

        var current = profile.PeriodOf(today);
        var last = StatementCycle.Previous(current, profile.StatementStartDay);

        var owedAtClose = Max0(LedgerMath.Balance(card, active, last.End).Negate());
        var paid = Money.Sum(active.Where(t => t.IsTransfer && t.Amount.IsPositive && t.Date > last.End).Select(t => t.Amount));
        var remaining = Max0(owedAtClose - paid);
        var overpaid = Max0(paid - owedAtClose);

        // Everything that changes the debt within the period, except payments to the card.
        var spending = Money.Sum(active
            .Where(t => current.Contains(t.Date) && !(t.IsTransfer && t.Amount.IsPositive))
            .Select(t => t.Amount)).Negate();
        var plannedInPeriod = Money.Sum(planned.Where(p => current.Contains(p.DueDate)).Select(p => p.Amount));
        var estimate = Max0(spending + plannedInPeriod - overpaid);

        Money? difference = paid.IsPositive && paid != owedAtClose ? paid - owedAtClose : null;
        var next = remaining.IsPositive
            ? new CardSettlement(card, last, remaining, profile.SettlementDateOf(last), profile.SettlementAccountId)
            : estimate.IsPositive
                ? new CardSettlement(card, current, estimate, profile.SettlementDateOf(current), profile.SettlementAccountId)
                : null;

        return new CardStatus(
            card,
            profile,
            outstanding,
            profile.CreditLimit - Max0(outstanding),
            current,
            spending,
            plannedInPeriod,
            estimate,
            last,
            owedAtClose,
            paid,
            difference,
            next);
    }

    /// <summary>
    /// Statement payments due up to <paramref name="until"/>. A payment whose date has passed but is not
    /// recorded yet is still expected (from today). Without a known debit day, the day after the period
    /// ends is assumed and <see cref="CardSettlement.Date"/> stays null so screens can say so.
    /// </summary>
    public static IEnumerable<CardSettlement> Settlements(CardStatus status, IReadOnlyList<PlannedOccurrence> planned, DateOnly today, DateOnly until)
    {
        if (status.Profile is not { } profile)
        {
            yield break;
        }

        var remaining = status.NextSettlement is { } n && n.Period == status.LastPeriod ? n.Amount : Money.Zero;
        if (remaining.IsPositive && Due(profile, status.LastPeriod, today) <= until)
        {
            yield return new CardSettlement(status.Card, status.LastPeriod, remaining, profile.SettlementDateOf(status.LastPeriod), profile.SettlementAccountId);
        }

        if (status.EstimatedStatement.IsPositive && Due(profile, status.CurrentPeriod, today) <= until)
        {
            yield return new CardSettlement(status.Card, status.CurrentPeriod, status.EstimatedStatement, profile.SettlementDateOf(status.CurrentPeriod), profile.SettlementAccountId);
        }

        var period = StatementCycle.Next(status.CurrentPeriod, profile.StatementStartDay);
        while (Due(profile, period, today) <= until)
        {
            var amount = Money.Sum(planned.Where(p => period.Contains(p.DueDate)).Select(p => p.Amount));
            if (amount.IsPositive)
            {
                yield return new CardSettlement(status.Card, period, amount, profile.SettlementDateOf(period), profile.SettlementAccountId);
            }

            period = StatementCycle.Next(period, profile.StatementStartDay);
        }
    }

    /// <summary>When a statement is expected to leave the paying account, for forecasting.</summary>
    public static DateOnly Due(CreditCardProfile profile, StatementPeriod period, DateOnly today)
    {
        var date = profile.SettlementDateOf(period) ?? period.End.AddDays(1);
        return date < today ? today : date;
    }

    private static Money Max0(Money m) => m.IsNegative ? Money.Zero : m;
}
