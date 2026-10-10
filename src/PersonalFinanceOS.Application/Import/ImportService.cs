using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Imports;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Import;

public sealed record MappingSuggestion(ImportMapping Mapping, bool Remembered);

/// <summary>
/// Local bank-file import (spec §8): suggest a mapping, build a preview with duplicates and
/// suggestions, save everything the user confirmed in one database transaction, and undo a whole
/// import safely. No file is kept and nothing leaves the computer.
/// </summary>
public sealed class ImportService(
    IImportRepository imports,
    ITransactionRepository transactions,
    IAccountRepository accounts,
    ICategoryRepository categories,
    IPayeeRuleRepository payeeRules,
    IAuditLog audit,
    LedgerService ledger,
    IClock clock)
{
    /// <summary>How far apart a bank date and a typed-in or other-account date may be and still be the same money.</summary>
    public const int MatchWindowDays = 3;

    private static readonly string[] CardKeywords = ["visa", "kredietkaart", "credit card", "creditcard", "mastercard", "carte de credit"];
    private static readonly string[] RefundKeywords = ["terugbetaling", "terugstorting", "retour", "refund", "remboursement", "creditnota"];
    private static readonly string[] CardPaymentKeywords = ["betaling", "aflossing", "afrekening", "overschrijving", "domiciliering", "storting", "payment", "repayment", "paiement"];

    public async Task<MappingSuggestion> SuggestMappingAsync(ImportFile file, CancellationToken ct = default)
    {
        var detected = ImportDetector.Detect(file);
        if (ImportMapping.HeaderKey(file, detected.HeaderRow) is { } key &&
            await imports.FindProfileAsync(key, ct) is { } profile &&
            ImportMapping.FromJson(profile.MappingJson) is { } remembered)
        {
            // Same header line, so the same layout; the number of lines above it may differ per export.
            var mapping = remembered with { HeaderRow = detected.HeaderRow };
            if (mapping.Problem(file.ColumnCount) is null)
            {
                return new MappingSuggestion(mapping, Remembered: true);
            }
        }

        return new MappingSuggestion(detected, Remembered: false);
    }

    public async Task<ImportPreview> PreviewAsync(Guid accountId, ImportFile file, ImportMapping mapping, CancellationToken ct = default)
    {
        var account = await ActiveAccountAsync(accountId, ct);
        if (mapping.Problem(file.ColumnCount) is { } problem)
        {
            throw new DomainValidationException(problem);
        }

        var rows = ImportRowReader.Read(file, mapping);
        if (rows.Count == 0)
        {
            throw new DomainValidationException("import.file.noRows");
        }

        var candidates = rows.Select(Candidate).ToList();
        foreach (var candidate in candidates)
        {
            candidate.Status = candidate.Problem is not null ? ImportRowStatus.Invalid
                : candidate.Date < account.OpeningDate ? ImportRowStatus.BeforeOpening
                : ImportRowStatus.New;
        }

        await MarkDuplicatesAsync(account, candidates, ct);
        await SuggestAsync(account, candidates, ct);
        foreach (var candidate in candidates)
        {
            candidate.Include = candidate.Status == ImportRowStatus.New;
        }

        return new ImportPreview(account, file, mapping, candidates);
    }

    public async Task<ImportResult> ConfirmAsync(ImportPreview preview, CancellationToken ct = default)
    {
        if (preview.IsConfirmed)
        {
            throw new DomainValidationException("import.alreadyConfirmed");
        }

        var account = await ActiveAccountAsync(preview.Account.Id, ct);

        // Check again against the ledger as it is now: another import may have happened since the preview.
        var before = preview.Candidates.ToDictionary(c => c, c => c.Status);
        await MarkDuplicatesAsync(account, preview.Candidates, ct);
        var nowDuplicate = 0;
        foreach (var candidate in preview.Candidates)
        {
            if (before[candidate] == ImportRowStatus.New && candidate.Status != ImportRowStatus.New)
            {
                candidate.Include = false;
                nowDuplicate++;
            }
        }

        var included = preview.Candidates.Where(c => c.Include).ToList();
        if (included.Count == 0)
        {
            throw new DomainValidationException("import.nothingSelected");
        }

        var accountById = (await accounts.ListAsync(includeArchived: true, ct)).ToDictionary(a => a.Id);
        var categoryById = (await categories.ListAsync(includeArchived: true, ct)).ToDictionary(c => c.Id);
        foreach (var candidate in included)
        {
            Validate(account, candidate, accountById, categoryById);
        }

        var now = clock.Now;
        var batch = ImportBatch.Create(
            account.Id,
            preview.File.FileName,
            preview.File.Format,
            preview.FirstDate,
            preview.LastDate,
            importedCount: included.Count,
            duplicateCount: preview.Candidates.Count(c => c.Status is ImportRowStatus.Duplicate or ImportRowStatus.PossibleDuplicate && !c.Include),
            skippedCount: preview.Candidates.Count(c => c.Status is ImportRowStatus.New or ImportRowStatus.BeforeOpening && !c.Include),
            invalidCount: preview.Count(ImportRowStatus.Invalid),
            now);

        var added = new List<Transaction>();
        var linked = new List<Transaction>();
        var links = new List<ImportTransferLink>();
        foreach (var candidate in included)
        {
            if (candidate.Type != TransactionType.Transfer)
            {
                var transaction = Transaction.Create(
                    account.Id, candidate.Date, candidate.Amount.Abs(), candidate.Type, candidate.Description, candidate.CategoryId, candidate.Note, TransactionSource.Import, now);
                transaction.AttachImport(batch.Id, candidate.Row.Reference);
                added.Add(transaction);
                continue;
            }

            var other = accountById[candidate.TransferAccountId!.Value];
            var transferId = Guid.NewGuid();
            var leg = Transaction.CreateTransferLeg(account.Id, candidate.Date, candidate.Amount, transferId, candidate.Description, candidate.Note, TransactionSource.Import, now);
            leg.AttachImport(batch.Id, candidate.Row.Reference);
            added.Add(leg);

            // The other side may already be in the ledger (imported from that account's file, or typed in).
            // Then it becomes this transfer's second side instead of a second copy (spec §9).
            var counterpart = await FindCounterpartAsync(other, candidate, linked, ct);
            if (counterpart is not null)
            {
                links.Add(ImportTransferLink.For(batch.Id, counterpart));
                counterpart.LinkAsTransfer(transferId, now);
                linked.Add(counterpart);
            }
            else
            {
                var otherLeg = Transaction.CreateTransferLeg(other.Id, candidate.Date, candidate.Amount.Negate(), transferId, candidate.Description, null, TransactionSource.Import, now);
                otherLeg.AttachImport(batch.Id, bankReference: null);
                added.Add(otherLeg);
            }
        }

        var profile = await ProfileAsync(preview, now, ct);
        await imports.SaveImportAsync(new ImportChanges(batch, added, linked, links, profile), ct);
        preview.IsConfirmed = true;
        await audit.RecordAsync("ImportBatch", batch.Id, "Imported", ct);
        await LearnAsync(included, ct);
        ledger.RaiseChanged();
        return new ImportResult(batch, included.Count, linked.Count, nowDuplicate);
    }

    public async Task<IReadOnlyList<ImportBatchInfo>> ListBatchesAsync(CancellationToken ct = default)
    {
        var accountById = (await accounts.ListAsync(includeArchived: true, ct)).ToDictionary(a => a.Id);
        return (await imports.ListBatchesAsync(null, ct)).Select(b => new ImportBatchInfo(b, accountById[b.AccountId])).ToList();
    }

    public async Task<BatchUndoPlan> PlanUndoAsync(Guid batchId, CancellationToken ct = default)
    {
        var (batch, lines, links) = await LoadForUndoAsync(batchId, ct);
        var changed = ChangedLines(lines);
        return new BatchUndoPlan(
            batch,
            Unchanged: lines.Count(t => t.IsActive && !changed.Contains(t.Id)),
            Changed: lines.Count(t => t.IsActive && changed.Contains(t.Id)),
            AlreadyRemoved: lines.Count(t => !t.IsActive),
            Linked: links.Count);
    }

    /// <summary>
    /// Voids the lines this import created and restores lines it linked as transfers. Lines changed
    /// since the import are kept unless <paramref name="includeChanged"/> (guided recovery, spec §8).
    /// Returns the number of lines removed.
    /// </summary>
    public async Task<int> UndoAsync(Guid batchId, bool includeChanged, CancellationToken ct = default)
    {
        var (batch, lines, links) = await LoadForUndoAsync(batchId, ct);
        var changed = ChangedLines(lines);
        var now = clock.Now;
        var toSave = new List<Transaction>();
        var voidedTransfers = new HashSet<Guid>();
        foreach (var line in lines.Where(t => t.IsActive && (includeChanged || !changed.Contains(t.Id))))
        {
            line.Void(now);
            toSave.Add(line);
            if (line.TransferId is { } transferId)
            {
                voidedTransfers.Add(transferId);
            }
        }

        // A transfer never keeps one side only: a side outside this import (e.g. linked by a later
        // import and then kept) is removed with it, unless it is a line this import only linked.
        var linkedIds = links.Select(l => l.TransactionId).ToHashSet();
        foreach (var transferId in voidedTransfers)
        {
            foreach (var leg in await transactions.FindTransferAsync(transferId, ct))
            {
                if (leg.IsActive && !linkedIds.Contains(leg.Id) && toSave.TrueForAll(t => t.Id != leg.Id))
                {
                    leg.Void(now);
                    toSave.Add(leg);
                }
            }
        }

        foreach (var link in links)
        {
            var counterpart = await transactions.FindAsync(link.TransactionId, ct);
            if (counterpart is { IsActive: true, TransferId: { } transferId } && voidedTransfers.Contains(transferId))
            {
                counterpart.UnlinkTransfer(link.OriginalType, link.OriginalCategoryId, now);
                toSave.Add(counterpart);
            }
        }

        batch.MarkUndone(now);
        await imports.SaveUndoAsync(batch, toSave, ct);
        await audit.RecordAsync("ImportBatch", batch.Id, "Undone", ct);
        ledger.RaiseChanged();
        return toSave.Count(t => !t.IsActive);
    }

    private static ImportCandidate Candidate(ParsedRow row)
    {
        var counterparty = row.Counterparty.Trim();
        var text = row.Description.Trim();
        string description;
        string? note;
        if (counterparty.Length > 0)
        {
            description = counterparty;
            note = text.Length > 0 && !ImportText.Normalize(text).Equals(ImportText.Normalize(counterparty), StringComparison.Ordinal) ? text : null;
        }
        else
        {
            description = text;
            note = text.Length > Transaction.MaxDescriptionLength ? text : null;
        }

        return new ImportCandidate(row, Cut(description, Transaction.MaxDescriptionLength), note is null ? null : Cut(note, Transaction.MaxNoteLength));
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>
    /// Duplicates are candidates, never certainties, and never decided on the description alone
    /// (spec §8 step 6). Each existing line can match one bank line only.
    /// </summary>
    private async Task MarkDuplicatesAsync(Account account, IReadOnlyList<ImportCandidate> candidates, CancellationToken ct)
    {
        var checkable = candidates.Where(c => c.Status is ImportRowStatus.New or ImportRowStatus.Duplicate or ImportRowStatus.PossibleDuplicate).ToList();
        foreach (var c in checkable)
        {
            c.Status = ImportRowStatus.New;
            c.MatchedTransactionId = null;
            c.MatchWasRemoved = false;
        }

        if (checkable.Count == 0)
        {
            return;
        }

        var from = checkable.Min(c => c.Date).AddDays(-MatchWindowDays);
        var to = checkable.Max(c => c.Date).AddDays(MatchWindowDays);
        var pool = (await imports.ListForDuplicateCheckAsync(account.Id, from, to, ct))
            .Where(p => p.Transaction.Type != TransactionType.Adjustment)
            // A line removed by hand still counts (re-importing must not bring it back silently);
            // lines removed by undoing their import do not, so that file can be imported again.
            .Where(p => p.Transaction.IsActive || (p.Transaction.Source == TransactionSource.Import && p.Batch is { IsActive: true }))
            .ToList();

        var claimed = new HashSet<Guid>();
        void Match(ImportCandidate c, Transaction t, ImportRowStatus status)
        {
            claimed.Add(t.Id);
            c.Status = status;
            c.MatchedTransactionId = t.Id;
            c.MatchWasRemoved = !t.IsActive;
        }

        // 1. The bank's own reference, with the same amount.
        foreach (var c in checkable.Where(c => c.Row.Reference is not null))
        {
            var hit = pool.FirstOrDefault(p => !claimed.Contains(p.Transaction.Id) && p.Transaction.BankReference == c.Row.Reference && p.Transaction.Amount == c.Amount);
            if (hit.Transaction is not null)
            {
                Match(c, hit.Transaction, ImportRowStatus.Duplicate);
            }
        }

        // 2. Same date, amount and text, unless both carry different bank references.
        foreach (var c in checkable.Where(c => c.Status == ImportRowStatus.New))
        {
            var text = ImportText.Normalize(c.Description);
            var hit = pool.FirstOrDefault(p =>
                !claimed.Contains(p.Transaction.Id) &&
                p.Transaction.Amount == c.Amount &&
                p.Transaction.Date == c.Date &&
                ImportText.Normalize(p.Transaction.Description) == text &&
                !(p.Transaction.BankReference is not null && c.Row.Reference is not null && p.Transaction.BankReference != c.Row.Reference));
            if (hit.Transaction is not null)
            {
                Match(c, hit.Transaction, ImportRowStatus.Duplicate);
            }
        }

        // 3. Same amount within a few days: the second side of a transfer imported from the other
        //    account's file (certain), or something typed in by hand before the bank booked it (possible).
        foreach (var c in checkable.Where(c => c.Status == ImportRowStatus.New))
        {
            var hit = pool
                .Where(p => p.Transaction.IsActive && !claimed.Contains(p.Transaction.Id) && p.Transaction.Amount == c.Amount &&
                            Math.Abs(p.Transaction.Date.DayNumber - c.Date.DayNumber) <= MatchWindowDays)
                .Where(p => IsGeneratedTransferLeg(p.Transaction, p.Batch, account) || p.Transaction.Source is TransactionSource.Manual or TransactionSource.QuickAdd)
                .OrderBy(p => Math.Abs(p.Transaction.Date.DayNumber - c.Date.DayNumber))
                .FirstOrDefault();
            if (hit.Transaction is not null)
            {
                Match(c, hit.Transaction, IsGeneratedTransferLeg(hit.Transaction, hit.Batch, account) ? ImportRowStatus.Duplicate : ImportRowStatus.PossibleDuplicate);
            }
        }
    }

    /// <summary>A transfer side created on this account while importing another account's file.</summary>
    private static bool IsGeneratedTransferLeg(Transaction t, ImportBatch? batch, Account account) =>
        t.IsTransfer && t.Source == TransactionSource.Import && t.BankReference is null && batch is not null && batch.AccountId != account.Id;

    /// <summary>Pre-fills type, category and transfer account (spec §8 step 7). Always editable.</summary>
    private async Task SuggestAsync(Account account, IReadOnlyList<ImportCandidate> candidates, CancellationToken ct)
    {
        var rules = (await payeeRules.ListAsync(ct))
            .Select(r => (Rule: r, Key: ImportText.Normalize(r.PayeeKey)))
            .Where(r => r.Key.Length >= 3)
            .OrderByDescending(r => r.Key.Length)
            .ToList();
        var activeCategories = (await categories.ListAsync(includeArchived: false, ct)).ToDictionary(c => c.Id);
        var others = (await accounts.ListAsync(includeArchived: false, ct)).Where(a => a.Id != account.Id).ToList();
        var card = others.FirstOrDefault(a => a.Type == AccountType.CreditCard);
        var payer = others.FirstOrDefault(a => a.Type == AccountType.PersonalCurrent) ?? others.FirstOrDefault(a => !a.Type.IsLiability());
        var liability = account.Type.IsLiability();

        foreach (var c in candidates.Where(c => c.Status != ImportRowStatus.Invalid))
        {
            var positive = c.Amount.IsPositive;
            var text = ImportText.Normalize(c.Row.Counterparty + " " + c.Row.Description);
            // Money back is a refund, never income (spec §10); on a card any money in is.
            var refund = liability || RefundKeywords.Any(k => ImportText.ContainsWords(text, k));
            c.Type = positive ? (refund ? TransactionType.Refund : TransactionType.Income) : TransactionType.Expense;

            var named = others.FirstOrDefault(a => ImportText.Normalize(a.Name) is { Length: >= 4 } n && ImportText.ContainsWords(text, n));
            if (!liability && named is null && !positive && card is not null && CardKeywords.Any(k => ImportText.ContainsWords(text, k)))
            {
                named = card;
            }

            if (named is not null)
            {
                Suggest(c, TransactionType.Transfer, null, named.Id, ImportSuggestion.OwnAccount);
                continue;
            }

            if (liability && positive && payer is not null && CardPaymentKeywords.Any(k => ImportText.ContainsWords(text, k)))
            {
                Suggest(c, TransactionType.Transfer, null, payer.Id, ImportSuggestion.CardPayment);
                continue;
            }

            var rule = rules.FirstOrDefault(r => ImportText.ContainsWords(text, r.Key)).Rule;
            if (rule is not null)
            {
                var type = rule.Type switch
                {
                    TransactionType.Income when positive => liability ? TransactionType.Refund : TransactionType.Income,
                    TransactionType.Income => TransactionType.Expense,
                    _ => positive ? TransactionType.Refund : TransactionType.Expense,
                };
                var category = rule.CategoryId is { } id && activeCategories.TryGetValue(id, out var cat) && cat.Kind == type.CategoryKind() ? id : (Guid?)null;
                Suggest(c, type, category, null, ImportSuggestion.PayeeRule);
                continue;
            }

            c.SuggestedType = c.Type;
        }
    }

    private static void Suggest(ImportCandidate c, TransactionType type, Guid? category, Guid? transferAccount, ImportSuggestion why)
    {
        c.Type = type;
        c.CategoryId = category;
        c.TransferAccountId = transferAccount;
        c.Suggestion = why;
        c.SuggestedType = type;
        c.SuggestedCategoryId = category;
    }

    private static void Validate(Account account, ImportCandidate c, Dictionary<Guid, Account> accountById, Dictionary<Guid, Category> categoryById)
    {
        if (c.Date < account.OpeningDate)
        {
            throw new ImportRowException("transaction.date.beforeOpening", c.RowNumber);
        }

        switch (c.Type)
        {
            case TransactionType.Transfer:
                if (c.TransferAccountId is not { } otherId || !accountById.TryGetValue(otherId, out var other))
                {
                    throw new ImportRowException("import.row.transferAccount", c.RowNumber);
                }

                if (other.Id == account.Id)
                {
                    throw new ImportRowException("transfer.sameAccount", c.RowNumber);
                }

                if (other.Status != AccountStatus.Active)
                {
                    throw new ImportRowException("account.archived", c.RowNumber);
                }

                if (c.Date < other.OpeningDate)
                {
                    throw new ImportRowException("import.row.transferBeforeOpening", c.RowNumber);
                }

                return;

            case TransactionType.Income or TransactionType.Refund when c.Amount.IsNegative:
            case TransactionType.Expense when c.Amount.IsPositive:
                throw new ImportRowException("import.row.typeDirection", c.RowNumber);

            case TransactionType.Income when account.Type.IsLiability():
                throw new ImportRowException("transaction.income.liability", c.RowNumber);

            case TransactionType.Income or TransactionType.Expense or TransactionType.Refund:
                break;

            default:
                throw new ImportRowException("transaction.wrongKind", c.RowNumber);
        }

        if (c.CategoryId is { } categoryId)
        {
            if (!categoryById.TryGetValue(categoryId, out var category))
            {
                throw new ImportRowException("category.notFound", c.RowNumber);
            }

            if (category.Kind != c.Type.CategoryKind())
            {
                throw new ImportRowException("transaction.category.kindMismatch", c.RowNumber);
            }

            if (!category.IsActive)
            {
                throw new ImportRowException("category.archived", c.RowNumber);
            }
        }
    }

    /// <summary>An income, expense or refund on the other account with the opposite amount, a few days apart at most.</summary>
    private async Task<Transaction?> FindCounterpartAsync(Account other, ImportCandidate c, List<Transaction> alreadyLinked, CancellationToken ct)
    {
        var lines = await imports.ListForDuplicateCheckAsync(other.Id, c.Date.AddDays(-MatchWindowDays), c.Date.AddDays(MatchWindowDays), ct);
        var target = c.Amount.Negate();
        return lines
            .Select(p => p.Transaction)
            .Where(t => t.IsActive && t.Type.IsIncomeOrSpending() && t.Amount == target && alreadyLinked.TrueForAll(l => l.Id != t.Id))
            .OrderBy(t => Math.Abs(t.Date.DayNumber - c.Date.DayNumber))
            .ThenBy(t => t.CreatedAt)
            .FirstOrDefault();
    }

    private async Task<ImportProfile?> ProfileAsync(ImportPreview preview, DateTimeOffset now, CancellationToken ct)
    {
        if (ImportMapping.HeaderKey(preview.File, preview.Mapping.HeaderRow) is not { } key)
        {
            return null; // without a header line there is nothing reliable to recognise the layout by
        }

        var json = preview.Mapping.ToJson();
        var profile = await imports.FindProfileAsync(key, ct);
        if (profile is null)
        {
            return ImportProfile.Create(key, json, now);
        }

        profile.Update(json, now);
        return profile;
    }

    /// <summary>
    /// Remembers the category chosen for a counterparty, like Quick Add does (spec §7). Only what the
    /// user chose or confirmed for a named counterparty is learned; whole bank descriptions never are.
    /// </summary>
    private async Task LearnAsync(IEnumerable<ImportCandidate> included, CancellationToken ct)
    {
        var learnable = included
            .Where(c => c.Type.IsIncomeOrSpending() && c.CategoryId is not null && PayeeRule.Normalize(c.LearnKey).Length >= 3)
            .Where(c => c.Suggestion != ImportSuggestion.PayeeRule || c.Type != c.SuggestedType || c.CategoryId != c.SuggestedCategoryId)
            .GroupBy(c => PayeeRule.Normalize(c.LearnKey))
            .Select(g => g.Last())
            .ToList();
        if (learnable.Count == 0)
        {
            return;
        }

        var existing = (await payeeRules.ListAsync(ct)).ToDictionary(r => r.PayeeKey);
        foreach (var c in learnable)
        {
            var key = PayeeRule.Normalize(c.LearnKey);
            if (existing.TryGetValue(key, out var rule))
            {
                rule.Update(c.Type, c.CategoryId, rule.AccountId, clock.Now); // keep Quick Add's account choice
            }
            else
            {
                rule = PayeeRule.Learn(c.LearnKey, c.Type, c.CategoryId, null, clock.Now);
            }

            await payeeRules.SaveAsync(rule, ct);
        }
    }

    private async Task<(ImportBatch Batch, IReadOnlyList<Transaction> Lines, IReadOnlyList<ImportTransferLink> Links)> LoadForUndoAsync(Guid batchId, CancellationToken ct)
    {
        var batch = await imports.FindBatchAsync(batchId, ct) ?? throw new DomainValidationException("import.notFound");
        if (!batch.IsActive)
        {
            throw new DomainValidationException("import.alreadyUndone");
        }

        return (batch, await imports.ListBatchTransactionsAsync(batchId, ct), await imports.ListLinksAsync(batchId, ct));
    }

    /// <summary>
    /// Lines edited since the import. Both sides of a transfer count as changed when either is, so
    /// they are always kept or removed together.
    /// </summary>
    private static HashSet<Guid> ChangedLines(IReadOnlyList<Transaction> lines)
    {
        var changed = lines.Where(t => t.IsActive && t.UpdatedAt != t.CreatedAt).ToList();
        var transferIds = changed.Where(t => t.TransferId is not null).Select(t => t.TransferId).ToHashSet();
        return lines.Where(t => changed.Contains(t) || (t.TransferId is not null && transferIds.Contains(t.TransferId))).Select(t => t.Id).ToHashSet();
    }

    private async Task<Account> ActiveAccountAsync(Guid accountId, CancellationToken ct)
    {
        var account = await accounts.FindAsync(accountId, ct) ?? throw new DomainValidationException("account.notFound");
        return account.Status == AccountStatus.Active ? account : throw new DomainValidationException("account.archived");
    }
}
