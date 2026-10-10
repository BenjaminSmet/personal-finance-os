using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.QuickAdd;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>The ledger services against a real, migrated SQLite database.</summary>
public sealed class LedgerTests : IDisposable
{
    private static readonly DateOnly Opening = new(2026, 10, 1);
    private static readonly DateOnly Day = new(2026, 10, 5);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly AccountService _accounts;
    private readonly CategoryService _categories;
    private readonly LedgerService _ledger;
    private readonly ReconciliationService _reconciliation;
    private readonly QuickAddService _quickAdd;

    public LedgerTests()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var audit = new AuditLog(db, _clock);
        var settings = new SettingsStore(db, _clock);
        var accountRepository = new AccountRepository(db);
        var categoryRepository = new CategoryRepository(db);
        _accounts = new AccountService(accountRepository, audit, _clock);
        _categories = new CategoryService(categoryRepository, settings, audit, _clock);
        _ledger = new LedgerService(new TransactionRepository(db), accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _reconciliation = new ReconciliationService(_ledger, accountRepository, new ReconciliationRepository(db), audit, _clock);
        _quickAdd = new QuickAddService(_accounts, _categories, new PayeeRuleRepository(db), settings, _ledger, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private Task<Account> AccountAsync(string name, AccountType type, long openingCents = 0, int shareBp = Share.FullBasisPoints) =>
        _accounts.CreateAsync(new CreateAccountRequest(name, type, Opening, Money.FromCents(openingCents), new Share(shareBp)));

    private Task<Transaction> ExpenseAsync(Account account, long cents, Guid? category = null) =>
        _ledger.RecordAsync(new TransactionRequest(account.Id, Day, Money.FromCents(cents), TransactionType.Expense, "Supermarkt", category));

    [Fact]
    public async Task Balances_follow_opening_balance_and_transactions()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent, 100_000);
        await _ledger.RecordAsync(new TransactionRequest(current.Id, Day, Money.FromCents(240_000), TransactionType.Income, "Loon"));
        await ExpenseAsync(current, 4_280);

        var balances = await _ledger.GetBalancesAsync();
        Assert.Equal(Money.FromCents(335_720), balances[current.Id]);
        Assert.Equal(Money.FromCents(100_000), (await _ledger.GetBalancesAsync(Opening))[current.Id]);

        // Integer cents, signed, in the database.
        Assert.Equal(240_000 - 4_280, _temp.Scalar("SELECT SUM(amount_cents) FROM transactions;"));
        Assert.Equal(1, _temp.Scalar("SELECT typeof(amount_cents) = 'integer' FROM transactions LIMIT 1;"));
    }

    [Fact]
    public async Task Transfer_is_saved_as_two_linked_rows_and_never_counts_as_spending()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent, 100_000);
        var joint = await AccountAsync("Gezamenlijk", AccountType.JointCurrent, 0, 5000);

        await _ledger.RecordTransferAsync(new TransferRequest(current.Id, joint.Id, Day, Money.FromCents(60_000)));

        var balances = await _ledger.GetBalancesAsync();
        Assert.Equal(Money.FromCents(40_000), balances[current.Id]);
        Assert.Equal(Money.FromCents(60_000), balances[joint.Id]);
        Assert.Equal(new PeriodTotals(Money.Zero, Money.Zero), await _ledger.GetTotalsAsync(Opening, Day));
        Assert.Equal(1, _temp.Scalar("SELECT COUNT(DISTINCT transfer_id) FROM transactions;"));

        var entries = await _ledger.ListEntriesAsync(new TransactionQuery(AccountId: joint.Id));
        var entry = Assert.Single(entries);
        Assert.Equal(EntryKind.Transfer, entry.Kind);
        Assert.Equal(current.Id, entry.Counterpart?.Id);
    }

    [Fact]
    public async Task Visa_purchase_and_settlement_count_the_expense_once()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent, 100_000);
        var visa = await AccountAsync("VISA", AccountType.CreditCard);

        await ExpenseAsync(visa, 40_000);
        Assert.Equal(Money.FromCents(100_000), (await _ledger.GetBalancesAsync())[current.Id]);

        await _ledger.RecordTransferAsync(new TransferRequest(current.Id, visa.Id, Day, Money.FromCents(40_000)));

        var balances = await _ledger.GetBalancesAsync();
        Assert.Equal(Money.FromCents(60_000), balances[current.Id]);
        Assert.Equal(Money.Zero, balances[visa.Id]);
        Assert.Equal(Money.FromCents(40_000), (await _ledger.GetTotalsAsync(Opening, Day)).Expenses);

        var kinds = (await _ledger.ListEntriesAsync(new TransactionQuery())).Select(e => e.Kind).ToList();
        Assert.Equal(1, kinds.Count(k => k == EntryKind.CardPurchase));
        Assert.Equal(2, kinds.Count(k => k == EntryKind.CardSettlement));
    }

    [Fact]
    public async Task Rules_that_protect_the_ledger_are_enforced()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent);
        var visa = await AccountAsync("VISA", AccountType.CreditCard);
        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);

        async Task<string> Fails(Func<Task> action) => (await Assert.ThrowsAsync<DomainValidationException>(action)).Code;

        Assert.Equal("transaction.date.beforeOpening", await Fails(() =>
            _ledger.RecordAsync(new TransactionRequest(current.Id, Opening.AddDays(-1), Money.FromCents(100), TransactionType.Expense))));
        Assert.Equal("transaction.income.liability", await Fails(() =>
            _ledger.RecordAsync(new TransactionRequest(visa.Id, Day, Money.FromCents(100), TransactionType.Income))));
        Assert.Equal("transaction.category.kindMismatch", await Fails(() =>
            _ledger.RecordAsync(new TransactionRequest(current.Id, Day, Money.FromCents(100), TransactionType.Income, null, groceries.Id))));
        Assert.Equal("transfer.sameAccount", await Fails(() =>
            _ledger.RecordTransferAsync(new TransferRequest(current.Id, current.Id, Day, Money.FromCents(100)))));

        await _accounts.ArchiveAsync(current.Id);
        Assert.Equal("account.archived", await Fails(() => ExpenseAsync(current, 100)));
        Assert.Equal(0, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
    }

    [Fact]
    public async Task Editing_a_transfer_changes_both_sides()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent, 100_000);
        var savings = await AccountAsync("Spaar", AccountType.Savings);
        var joint = await AccountAsync("Gezamenlijk", AccountType.JointCurrent);
        var (from, _) = await _ledger.RecordTransferAsync(new TransferRequest(current.Id, savings.Id, Day, Money.FromCents(10_000)));

        await _ledger.UpdateTransferAsync(from.TransferId!.Value, new TransferRequest(current.Id, joint.Id, Day, Money.FromCents(25_000), "Huishoudpot"));

        var balances = await _ledger.GetBalancesAsync();
        Assert.Equal(Money.FromCents(75_000), balances[current.Id]);
        Assert.Equal(Money.Zero, balances[savings.Id]);
        Assert.Equal(Money.FromCents(25_000), balances[joint.Id]);
    }

    [Fact]
    public async Task Void_and_undo_restore_exactly_and_are_audited()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent, 100_000);
        var savings = await AccountAsync("Spaar", AccountType.Savings);
        var (from, _) = await _ledger.RecordTransferAsync(new TransferRequest(current.Id, savings.Id, Day, Money.FromCents(10_000)));

        await _ledger.VoidAsync(from.Id);
        Assert.Equal(Money.FromCents(100_000), (await _ledger.GetBalancesAsync())[current.Id]);
        Assert.Equal(2, _temp.Scalar("SELECT COUNT(*) FROM transactions WHERE status = 'Voided';")); // both sides, kept

        Assert.True(await _ledger.UndoAsync()); // undo the void
        Assert.Equal(Money.FromCents(90_000), (await _ledger.GetBalancesAsync())[current.Id]);

        Assert.True(await _ledger.UndoAsync()); // undo the transfer itself
        Assert.Equal(Money.FromCents(100_000), (await _ledger.GetBalancesAsync())[current.Id]);
        Assert.False(_ledger.History.CanUndo);
        Assert.True(_temp.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'Undone';") >= 2);
    }

    [Fact]
    public async Task Undo_of_an_edit_brings_back_the_old_values()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent);
        var spent = await ExpenseAsync(current, 4_280);
        await _ledger.UpdateAsync(spent.Id, new TransactionRequest(current.Id, Day, Money.FromCents(4_820), TransactionType.Expense, "Supermarkt Colruyt"));

        await _ledger.UndoAsync();

        var entry = Assert.Single(await _ledger.ListEntriesAsync(new TransactionQuery()));
        Assert.Equal(Money.FromCents(-4_280), entry.Transaction.Amount);
        Assert.Equal("Supermarkt", entry.Transaction.Description);
    }

    [Fact]
    public async Task Undo_never_overwrites_a_newer_change()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent);
        var spent = await ExpenseAsync(current, 4_280);

        // Changed behind the session's back (e.g. another window): undo must refuse.
        _temp.Execute($"UPDATE transactions SET amount_cents = -1 WHERE id = '{spent.Id:D}';");

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => _ledger.UndoAsync());
        Assert.Equal("undo.stale", ex.Code);
        Assert.Equal(-1, _temp.Scalar("SELECT amount_cents FROM transactions;"));
    }

    [Fact]
    public async Task Reconciliation_shows_the_difference_and_only_adjusts_when_asked()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent, 100_000);
        await ExpenseAsync(current, 4_280);

        var preview = await _reconciliation.PreviewAsync(current.Id, Day, Money.FromCents(95_000));
        Assert.Equal(Money.FromCents(95_720), preview.Calculated);
        Assert.Equal(Money.FromCents(-720), preview.Difference);

        // Recorded without adjustment: the balance is not touched and the difference stays visible.
        var plain = await _reconciliation.RecordAsync(current.Id, Day, Money.FromCents(95_000), addAdjustment: false, reason: null);
        Assert.False(plain.IsMatched);
        Assert.Equal(Money.FromCents(95_720), (await _ledger.GetBalancesAsync())[current.Id]);

        // Adjustment needs a reason.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _reconciliation.RecordAsync(current.Id, Day, Money.FromCents(95_000), addAdjustment: true, reason: ""));

        _clock.Now = _clock.Now.AddMinutes(1);
        var adjusted = await _reconciliation.RecordAsync(current.Id, Day, Money.FromCents(95_000), addAdjustment: true, reason: "Bankkosten");
        Assert.True(adjusted.IsMatched);
        Assert.Equal(Money.FromCents(95_000), (await _ledger.GetBalancesAsync())[current.Id]);
        Assert.Equal(new PeriodTotals(Money.Zero, Money.FromCents(4_280)), await _ledger.GetTotalsAsync(Opening, Day)); // not spending
        Assert.Equal(adjusted.Id, (await _reconciliation.LatestAsync())[current.Id].Id);
    }

    [Fact]
    public async Task Quick_add_posts_only_on_confirm_and_learns_the_corrected_choice()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);
        var visa = await AccountAsync("VISA", AccountType.CreditCard);
        var subscriptions = await _categories.CreateAsync("Abonnementen", CategoryKind.Expense);

        var draft = await _quickAdd.InterpretAsync("Spotify 17,99");
        Assert.Equal(current.Id, draft.AccountId);
        Assert.Equal(0, _temp.Scalar("SELECT COUNT(*) FROM transactions;")); // nothing posted yet

        // The user corrects account and category in the preview, then confirms.
        await _quickAdd.ConfirmAsync(draft with { AccountId = visa.Id, CategoryId = subscriptions.Id });
        Assert.Equal(Money.FromCents(-1_799), (await _ledger.GetBalancesAsync())[visa.Id]);
        Assert.Equal(1, _temp.Scalar("SELECT COUNT(*) FROM transactions WHERE source = 'QuickAdd';"));

        // Next time the correction is suggested.
        var next = await _quickAdd.InterpretAsync("spotify 17,99");
        Assert.Equal(visa.Id, next.AccountId);
        Assert.Equal(subscriptions.Id, next.CategoryId);
    }

    [Fact]
    public async Task Quick_add_transfer_and_last_used_account()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);
        var joint = await AccountAsync("Gezamenlijke rekening", AccountType.JointCurrent, 0, 5000);

        await _quickAdd.ConfirmAsync(await _quickAdd.InterpretAsync("Transfer 600 naar gezamenlijke rekening"));
        Assert.Equal(Money.FromCents(60_000), (await _ledger.GetBalancesAsync())[joint.Id]);

        await _quickAdd.ConfirmAsync((await _quickAdd.InterpretAsync("Bakker 4,50")) with { AccountId = joint.Id });
        Assert.Equal(joint.Id, (await _quickAdd.InterpretAsync("Slager 12")).AccountId);
        Assert.Equal(Money.FromCents(40_000), (await _ledger.GetBalancesAsync())[current.Id]);
    }

    [Fact]
    public async Task Default_categories_are_created_once()
    {
        DefaultCategory[] defaults = [new("Boodschappen", CategoryKind.Expense), new("Loon", CategoryKind.Income)];
        await _categories.EnsureDefaultsAsync(defaults);
        var list = await _categories.ListAsync();
        Assert.Equal(2, list.Count);

        foreach (var c in list)
        {
            await _categories.ArchiveAsync(c.Id);
        }

        await _categories.EnsureDefaultsAsync(defaults); // archiving everything never brings them back
        Assert.Empty(await _categories.ListAsync());
        Assert.Equal(2, (await _categories.ListAsync(includeArchived: true)).Count);
    }

    [Fact]
    public async Task Category_names_are_unique_per_kind()
    {
        await _categories.CreateAsync("Andere", CategoryKind.Expense);
        await _categories.CreateAsync("Andere", CategoryKind.Income); // other kind: fine

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => _categories.CreateAsync(" andere ", CategoryKind.Expense));
        Assert.Equal("category.name.duplicate", ex.Code);
    }

    [Fact]
    public async Task Archived_category_stays_on_old_transactions_but_not_on_new_ones()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent);
        var old = await _categories.CreateAsync("Oud", CategoryKind.Expense);
        var spent = await ExpenseAsync(current, 1_000, old.Id);
        await _categories.ArchiveAsync(old.Id);

        // Editing the amount keeps the archived category.
        await _ledger.UpdateAsync(spent.Id, new TransactionRequest(current.Id, Day, Money.FromCents(1_100), TransactionType.Expense, "x", old.Id));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => ExpenseAsync(current, 500, old.Id));
        Assert.Equal("category.archived", ex.Code);
    }

    [Fact]
    public async Task Database_constraints_back_up_the_domain_rules()
    {
        var current = await AccountAsync("Zicht", AccountType.PersonalCurrent);
        await ExpenseAsync(current, 100);

        // An expense that adds money, or a transfer without its link, is refused by SQLite itself.
        Assert.ThrowsAny<Exception>(() => _temp.Execute("UPDATE transactions SET amount_cents = 100;"));
        Assert.ThrowsAny<Exception>(() => _temp.Execute("UPDATE transactions SET type = 'Transfer';"));
    }
}
