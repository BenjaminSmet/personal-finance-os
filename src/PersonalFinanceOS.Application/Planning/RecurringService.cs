using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Planning;

/// <summary>A new or changed recurring rule. <see cref="Amount"/> is positive; the kind gives the direction.</summary>
public sealed record RecurringRuleRequest(
    string Name,
    RecurringKind Kind,
    Guid AccountId,
    Guid? ToAccountId,
    Guid? CategoryId,
    Money Amount,
    RecurrenceFrequency Frequency,
    DateOnly StartDate,
    DateOnly? EndDate = null);

/// <summary>
/// An occurrence that has not happened yet as far as the app knows: no decision, postponed, or
/// confirmed with a transaction that was removed since. It affects forecasts, never balances.
/// </summary>
public sealed record PlannedOccurrence(
    RecurringRule Rule,

    /// <summary>The date the rule planned; names the occurrence.</summary>
    DateOnly OccurrenceDate,

    /// <summary>When it is expected now: the planned date, or the new date when postponed.</summary>
    DateOnly DueDate,
    Money Amount,
    bool IsPostponed,
    bool IsOverdue,

    /// <summary>A recorded transaction that looks like this occurrence (spec §12 step 6). Only a suggestion.</summary>
    Transaction? SuggestedMatch);

/// <summary>
/// Recurring rules and their occurrences (spec §12): the user confirms, edits, postpones or skips
/// each one; confirming creates the real transaction, or links one that already exists (for example
/// from an import), so a planned and an actual item never count twice.
/// </summary>
public sealed class RecurringService(
    IRecurringRepository repository,
    IAccountRepository accounts,
    ICategoryRepository categories,
    ITransactionRepository transactions,
    LedgerService ledger,
    IAuditLog audit,
    IClock clock)
{
    /// <summary>How far a recorded transaction's date may be from the expected date and still be suggested as that occurrence.</summary>
    public const int MatchWindowDays = 5;

    /// <summary>A bill may differ a little from the planned amount (energy, phone) and still be suggested.</summary>
    public const int MatchTolerancePercent = 10;

    /// <summary>Raised after rules or decisions change.</summary>
    public event EventHandler? Changed;

    public Task<IReadOnlyList<RecurringRule>> ListRulesAsync(bool includeEnded = false, CancellationToken ct = default) =>
        repository.ListRulesAsync(includeEnded, ct);

    public async Task<RecurringRule> CreateRuleAsync(RecurringRuleRequest request, CancellationToken ct = default)
    {
        await ValidateAsync(request, ct);
        var rule = RecurringRule.Create(
            request.Name, request.Kind, request.AccountId, request.ToAccountId, request.CategoryId, request.Amount,
            request.Frequency, request.StartDate, request.EndDate, clock.Now);
        await repository.AddRuleAsync(rule, ct);
        await audit.RecordAsync("RecurringRule", rule.Id, "Created", ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return rule;
    }

    /// <summary>
    /// Stops a rule: nothing is planned after today any more. Earlier open occurrences stay visible,
    /// so they can still be confirmed or skipped. Confirmed occurrences and their transactions are kept.
    /// </summary>
    public async Task EndRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await repository.FindRuleAsync(ruleId, ct) ?? throw new DomainValidationException("recurring.notFound");
        rule.End(clock.Today.AddDays(-1), clock.Now);
        await repository.UpdateRuleAsync(rule, ct);
        await audit.RecordAsync("RecurringRule", ruleId, "Ended", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Every open occurrence due up to <paramref name="until"/>, overdue ones included, oldest first.
    /// With <paramref name="withMatches"/>, recorded transactions that look like an occurrence are
    /// suggested, each transaction for at most one occurrence.
    /// </summary>
    public async Task<IReadOnlyList<PlannedOccurrence>> ListOpenAsync(DateOnly until, bool withMatches = true, CancellationToken ct = default)
    {
        var today = clock.Today;
        var rules = await repository.ListRulesAsync(includeEnded: true, ct);
        if (rules.Count == 0)
        {
            return [];
        }

        var items = (await repository.ListItemsAsync(ct)).ToDictionary(i => (i.RuleId, i.OccurrenceDate));
        var linked = await LiveLinksAsync(items.Values, ct);

        var open = new List<(RecurringRule Rule, DateOnly Occurrence, DateOnly Due, bool Postponed)>();
        foreach (var rule in rules)
        {
            // A postponed occurrence is never due before its original date, so later ones cannot be due yet.
            foreach (var date in rule.Occurrences(rule.StartDate, until))
            {
                var decision = items.GetValueOrDefault((rule.Id, date));
                DateOnly? due;
                var postponed = false;
                switch (decision)
                {
                    case null:
                        due = date;
                        break;
                    case { Status: ScheduledItemStatus.Postponed }:
                        due = decision.PlannedDate;
                        postponed = true;
                        break;
                    case { Status: ScheduledItemStatus.Confirmed } when !linked.Contains(decision.TransactionId!.Value):
                        due = date; // its transaction was removed, so it is expected again
                        break;
                    default:
                        due = null;
                        break;
                }

                if (due is not { } d || d > until)
                {
                    continue;
                }

                open.Add((rule, date, d, postponed));
            }
        }

        var matches = withMatches ? await SuggestMatchesAsync(open, linked, today, ct) : [];
        return open
            .OrderBy(o => o.Due).ThenBy(o => o.Rule.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(o => new PlannedOccurrence(
                o.Rule, o.Occurrence, o.Due, o.Rule.Amount, o.Postponed, o.Due < today,
                matches.GetValueOrDefault((o.Rule.Id, o.Occurrence))))
            .ToList();
    }

    /// <summary>
    /// It happened: records the real transaction (with the date and amount the user confirms) and
    /// links it to the occurrence in one save. Undoing that transaction makes the occurrence open again.
    /// </summary>
    public async Task<IReadOnlyList<Transaction>> ConfirmAsync(Guid ruleId, DateOnly occurrence, DateOnly date, Money amount, CancellationToken ct = default)
    {
        var (rule, item) = await OpenItemAsync(ruleId, occurrence, ct);
        IReadOnlyList<Transaction> created;
        if (rule.Kind == RecurringKind.Transfer)
        {
            var request = new TransferRequest(rule.AccountId, rule.ToAccountId!.Value, date, amount, rule.Name, null, TransactionSource.Scheduled);
            await ledger.ValidateNewTransferAsync(request, ct);
            var (from, to) = Transaction.CreateTransfer(request.FromAccountId, request.ToAccountId, date, amount, rule.Name, null, TransactionSource.Scheduled, clock.Now);
            created = [from, to];
        }
        else
        {
            var type = rule.Kind == RecurringKind.Income ? TransactionType.Income : TransactionType.Expense;
            var request = new TransactionRequest(rule.AccountId, date, amount, type, rule.Name, rule.CategoryId, null, TransactionSource.Scheduled);
            await ledger.ValidateNewAsync(request, ct);
            created = [Transaction.Create(rule.AccountId, date, amount, type, rule.Name, rule.CategoryId, null, TransactionSource.Scheduled, clock.Now)];
        }

        item.Confirm(created[0].Id, clock.Now);
        await repository.SaveItemAsync(item, created, ct);
        await audit.RecordAsync("ScheduledItem", item.Id, "Confirmed", ct);
        await ledger.RecordedAsync(created, ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return created;
    }

    /// <summary>It happened and is already in the ledger (typed in or imported): link that transaction instead of adding one.</summary>
    public async Task LinkAsync(Guid ruleId, DateOnly occurrence, Guid transactionId, CancellationToken ct = default)
    {
        var (rule, item) = await OpenItemAsync(ruleId, occurrence, ct);
        var transaction = await transactions.FindAsync(transactionId, ct);
        if (transaction is null || !transaction.IsActive || !await FitsAsync(rule, transaction, ct))
        {
            throw new DomainValidationException("scheduled.link.invalid");
        }

        var items = await repository.ListItemsAsync(ct);
        var linked = await LiveLinksAsync(items, ct);
        if (linked.Contains(transaction.Id) || (transaction.TransferId is { } t && (await transactions.FindTransferAsync(t, ct)).Any(l => linked.Contains(l.Id))))
        {
            throw new DomainValidationException("scheduled.link.taken");
        }

        item.Confirm(await LinkIdAsync(transaction, ct), clock.Now);
        await repository.SaveItemAsync(item, [], ct);
        await audit.RecordAsync("ScheduledItem", item.Id, "Linked", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Still expected, but later.</summary>
    public async Task PostponeAsync(Guid ruleId, DateOnly occurrence, DateOnly newDate, CancellationToken ct = default)
    {
        var (_, item) = await OpenItemAsync(ruleId, occurrence, ct);
        item.Postpone(newDate, clock.Now);
        await repository.SaveItemAsync(item, [], ct);
        await audit.RecordAsync("ScheduledItem", item.Id, "Postponed", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Will not happen this time. Forecasts stop counting it.</summary>
    public async Task SkipAsync(Guid ruleId, DateOnly occurrence, CancellationToken ct = default)
    {
        var (_, item) = await OpenItemAsync(ruleId, occurrence, ct);
        item.Skip(clock.Now);
        await repository.SaveItemAsync(item, [], ct);
        await audit.RecordAsync("ScheduledItem", item.Id, "Skipped", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes a skip or postponement: the occurrence is open on its original date again.</summary>
    public async Task ReopenAsync(Guid ruleId, DateOnly occurrence, CancellationToken ct = default)
    {
        var item = await repository.FindItemAsync(ruleId, occurrence, ct) ?? throw new DomainValidationException("scheduled.notFound");
        if (item.Status == ScheduledItemStatus.Confirmed)
        {
            // A confirmation is undone by removing its transaction, which keeps the ledger's own undo and audit.
            throw new DomainValidationException("scheduled.reopen.confirmed");
        }

        await repository.DeleteItemAsync(item, ct);
        await audit.RecordAsync("ScheduledItem", item.Id, "Reopened", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<(RecurringRule Rule, ScheduledItem Item)> OpenItemAsync(Guid ruleId, DateOnly occurrence, CancellationToken ct)
    {
        var rule = await repository.FindRuleAsync(ruleId, ct) ?? throw new DomainValidationException("recurring.notFound");
        if (!rule.Occurrences(occurrence, occurrence).Any())
        {
            throw new DomainValidationException("scheduled.notFound");
        }

        var item = await repository.FindItemAsync(ruleId, occurrence, ct);
        if (item is null)
        {
            return (rule, ScheduledItem.For(ruleId, occurrence, clock.Now));
        }

        if (item.Status == ScheduledItemStatus.Skipped ||
            (item.Status == ScheduledItemStatus.Confirmed && (await transactions.FindAsync(item.TransactionId!.Value, ct))?.IsActive == true))
        {
            // Already handled; a second confirmation would count the money twice.
            throw new DomainValidationException("scheduled.alreadyHandled");
        }

        return (rule, item);
    }

    /// <summary>Transactions that settle an occurrence right now (removed ones no longer do).</summary>
    private async Task<HashSet<Guid>> LiveLinksAsync(IEnumerable<ScheduledItem> items, CancellationToken ct)
    {
        var result = new HashSet<Guid>();
        foreach (var id in items.Where(i => i.Status == ScheduledItemStatus.Confirmed).Select(i => i.TransactionId!.Value))
        {
            if ((await transactions.FindAsync(id, ct))?.IsActive == true)
            {
                result.Add(id);
            }
        }

        return result;
    }

    private async Task<Dictionary<(Guid, DateOnly), Transaction>> SuggestMatchesAsync(
        List<(RecurringRule Rule, DateOnly Occurrence, DateOnly Due, bool Postponed)> open,
        HashSet<Guid> linked,
        DateOnly today,
        CancellationToken ct)
    {
        var result = new Dictionary<(Guid, DateOnly), Transaction>();
        var candidates = open.Where(o => o.Due <= today.AddDays(MatchWindowDays)).ToList();
        if (candidates.Count == 0)
        {
            return result;
        }

        var from = candidates.Min(o => o.Due).AddDays(-MatchWindowDays);
        var to = candidates.Max(o => o.Due).AddDays(MatchWindowDays);
        var all = await transactions.ListAsync(new TransactionQuery(From: from, To: to), ct);
        var legs = all.Where(t => t.TransferId is not null).ToLookup(t => t.TransferId!.Value);
        var pool = all.Where(t => !linked.Contains(t.Id) && !(t.TransferId is { } x && legs[x].Any(l => linked.Contains(l.Id)))).ToList();
        var claimed = new HashSet<Guid>();

        // Closest amount first, then closest date, so two similar bills each find their own line.
        var pairs = new List<(int Occ, Transaction T, long AmountGap, int DayGap)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var (rule, _, due, _) = candidates[i];
            foreach (var t in pool)
            {
                var dayGap = Math.Abs(t.Date.DayNumber - due.DayNumber);
                if (dayGap > MatchWindowDays || !Fits(rule, t, legs))
                {
                    continue;
                }

                var gap = Math.Abs(t.Amount.Abs().Cents - rule.Amount.Cents);
                if (gap * 100 <= rule.Amount.Cents * MatchTolerancePercent)
                {
                    pairs.Add((i, t, gap, dayGap));
                }
            }
        }

        var done = new HashSet<int>();
        foreach (var (occ, t, _, _) in pairs.OrderBy(p => p.AmountGap).ThenBy(p => p.DayGap))
        {
            var key = t.TransferId ?? t.Id;
            if (done.Contains(occ) || claimed.Contains(key))
            {
                continue;
            }

            done.Add(occ);
            claimed.Add(key);
            result[(candidates[occ].Rule.Id, candidates[occ].Occurrence)] = t;
        }

        return result;
    }

    /// <summary>The transaction is the kind of money movement the rule plans, on the rule's accounts.</summary>
    private static bool Fits(RecurringRule rule, Transaction t, ILookup<Guid, Transaction> legs) => rule.Kind switch
    {
        RecurringKind.Income => t.AccountId == rule.AccountId && t.Type == TransactionType.Income,
        RecurringKind.Expense => t.AccountId == rule.AccountId && t.Type == TransactionType.Expense,
        _ => t.AccountId == rule.AccountId && t.IsTransfer && t.Amount.IsNegative &&
             legs[t.TransferId!.Value].Any(l => l.AccountId == rule.ToAccountId && l.Amount.IsPositive),
    };

    private async Task<bool> FitsAsync(RecurringRule rule, Transaction t, CancellationToken ct)
    {
        if (rule.Kind != RecurringKind.Transfer)
        {
            return Fits(rule, t, Array.Empty<Transaction>().ToLookup(x => x.Id));
        }

        if (t.TransferId is not { } transferId)
        {
            return false;
        }

        var legs = await transactions.FindTransferAsync(transferId, ct);
        var outgoing = legs.FirstOrDefault(l => l.Amount.IsNegative);
        return outgoing is not null && Fits(rule, outgoing, legs.ToLookup(l => transferId));
    }

    /// <summary>For a transfer the occurrence is linked to the side leaving the account, whichever side was picked.</summary>
    private async Task<Guid> LinkIdAsync(Transaction t, CancellationToken ct) =>
        t.TransferId is { } transferId
            ? (await transactions.FindTransferAsync(transferId, ct)).First(l => l.Amount.IsNegative).Id
            : t.Id;

    private async Task ValidateAsync(RecurringRuleRequest request, CancellationToken ct)
    {
        var account = await ActiveAccountAsync(request.AccountId, ct);
        if (request.StartDate < account.OpeningDate)
        {
            // The opening balance already contains that period.
            throw new DomainValidationException("transaction.date.beforeOpening");
        }

        switch (request.Kind)
        {
            case RecurringKind.Income when account.Type.IsLiability():
                throw new DomainValidationException("transaction.income.liability");
            case RecurringKind.Transfer:
                if (request.ToAccountId is not { } to)
                {
                    throw new DomainValidationException("recurring.toAccount.required");
                }

                var destination = await ActiveAccountAsync(to, ct);
                if (request.StartDate < destination.OpeningDate)
                {
                    throw new DomainValidationException("transaction.date.beforeOpening");
                }

                break;
        }

        if (request.Kind != RecurringKind.Transfer && request.CategoryId is { } categoryId)
        {
            var category = await categories.FindAsync(categoryId, ct);
            var type = request.Kind == RecurringKind.Income ? TransactionType.Income : TransactionType.Expense;
            if (category is null || !category.IsActive)
            {
                throw new DomainValidationException("category.notFound");
            }

            if (category.Kind != type.CategoryKind())
            {
                throw new DomainValidationException("transaction.category.kindMismatch");
            }
        }
    }

    private async Task<Account> ActiveAccountAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.FindAsync(id, ct) ?? throw new DomainValidationException("account.notFound");
        if (account.Status != AccountStatus.Active)
        {
            throw new DomainValidationException("account.archived");
        }

        return account;
    }
}
