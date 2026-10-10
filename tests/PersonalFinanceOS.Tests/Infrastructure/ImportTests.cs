using System.Text;
using Microsoft.EntityFrameworkCore;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Import;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Imports;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Database;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>Spec §8, §9 and the import acceptance tests of §23, against a real migrated database.</summary>
public sealed class ImportTests : IDisposable
{
    private const string Header = "Datum;Omschrijving;Naam tegenpartij;Bedrag;Referentie";
    private static readonly DateOnly Opening = new(2026, 10, 1);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly Func<FinanceDbContext> _db;
    private readonly AccountService _accounts;
    private readonly CategoryService _categories;
    private readonly LedgerService _ledger;
    private readonly ReconciliationService _reconciliation;
    private readonly ImportService _import;
    private readonly PayeeRuleRepository _rules;

    public ImportTests()
        : this(null)
    {
    }

    private ImportTests(IMigrationSource? migrations)
    {
        Assert.True(_temp.Initializer(migrations).Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        _db = () => new FinanceDbContext(options);
        var audit = new AuditLog(_db, _clock);
        var settings = new SettingsStore(_db, _clock);
        var accountRepository = new AccountRepository(_db);
        var categoryRepository = new CategoryRepository(_db);
        var transactions = new TransactionRepository(_db);
        _rules = new PayeeRuleRepository(_db);
        _accounts = new AccountService(accountRepository, audit, _clock);
        _categories = new CategoryService(categoryRepository, settings, audit, _clock);
        _ledger = new LedgerService(transactions, accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _reconciliation = new ReconciliationService(_ledger, accountRepository, new ReconciliationRepository(_db), audit, _clock);
        _import = new ImportService(new ImportRepository(_db), transactions, accountRepository, categoryRepository, _rules, audit, _ledger, new PersonalFinanceOS.Application.Settings.AppSettingsService(settings), _clock);
    }

    public void Dispose() => _temp.Dispose();

    private Task<Account> AccountAsync(string name, AccountType type, long openingCents = 0, DateOnly? opening = null) =>
        _accounts.CreateAsync(new CreateAccountRequest(name, type, opening ?? Opening, Money.FromCents(openingCents), Share.Full, Card: type == AccountType.CreditCard ? new PersonalFinanceOS.Application.Cards.CardSettings(Money.FromCents(150_000), 28, null, null) : null));

    private static ImportFile File(string name, params string[] lines) =>
        ImportFile.Read(name, Encoding.UTF8.GetBytes(Header + "\n" + string.Join("\n", lines) + "\n"));

    private async Task<ImportPreview> PreviewAsync(Account account, ImportFile file)
    {
        var mapping = (await _import.SuggestMappingAsync(file)).Mapping;
        return await _import.PreviewAsync(account.Id, file, mapping);
    }

    private async Task<ImportResult> ImportAsync(Account account, ImportFile file)
    {
        var preview = await PreviewAsync(account, file);
        return await _import.ConfirmAsync(preview);
    }

    private async Task<IReadOnlyList<Transaction>> ActiveAsync(Account account) =>
        (await _ledger.ListEntriesAsync(new PersonalFinanceOS.Application.Abstractions.TransactionQuery(AccountId: account.Id))).Select(e => e.Transaction).ToList();

    private static readonly string[] Statement =
    [
        "03/10/2026;BETALING DEBETKAART COLRUYT;COLRUYT GENT;-42,80;R1",
        "05/10/2026;OVERSCHRIJVING LOON SEPTEMBER;WERKGEVER NV;2.400,00;R2",
        "07/10/2026;DOMICILIERING;SPOTIFY AB;-17,99;R3",
        "08/10/2026;TERUGBETALING;BOL.COM;12,50;R4",
    ];

    [Fact]
    public async Task Confirmed_import_adds_every_line_once_with_its_batch_and_reference()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);

        var preview = await PreviewAsync(current, File("oktober.csv", Statement));
        Assert.All(preview.Candidates, c => Assert.Equal(ImportRowStatus.New, c.Status));
        Assert.Equal(0, _temp.Scalar("SELECT COUNT(*) FROM transactions;")); // a preview is not a posting

        var result = await _import.ConfirmAsync(preview);

        Assert.Equal(4, result.Imported);
        Assert.Equal(Money.FromCents(100_000 - 4_280 + 240_000 - 1_799 + 1_250), await _ledger.GetBalanceAsync(current.Id));
        var totals = await _ledger.GetTotalsAsync(Opening, new DateOnly(2026, 10, 31));
        Assert.Equal(Money.FromCents(240_000), totals.Income);
        Assert.Equal(Money.FromCents(4_280 + 1_799 - 1_250), totals.Expenses); // the refund lowers spending

        var lines = await ActiveAsync(current);
        Assert.All(lines, t => Assert.Equal(TransactionSource.Import, t.Source));
        Assert.All(lines, t => Assert.Equal(result.Batch.Id, t.ImportBatchId));
        var colruyt = Assert.Single(lines, t => t.BankReference == "R1");
        Assert.Equal("COLRUYT GENT", colruyt.Description);
        Assert.Equal("BETALING DEBETKAART COLRUYT", colruyt.Note);

        var batch = Assert.Single(await _import.ListBatchesAsync()).Batch;
        Assert.Equal("oktober.csv", batch.FileName);
        Assert.Equal((4, 0, 0, 0), (batch.ImportedCount, batch.DuplicateCount, batch.SkippedCount, batch.InvalidCount));
        Assert.Equal((new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 8)), (batch.FirstDate!.Value, batch.LastDate!.Value));
    }

    [Fact]
    public async Task Reimporting_the_same_file_never_duplicates_silently()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        await ImportAsync(current, File("a.csv", Statement));

        var again = await PreviewAsync(current, File("a.csv", Statement));

        Assert.All(again.Candidates, c => Assert.Equal(ImportRowStatus.Duplicate, c.Status));
        Assert.Equal(0, again.IncludedCount);
        Assert.Equal("import.nothingSelected", (await Assert.ThrowsAsync<DomainValidationException>(() => _import.ConfirmAsync(again))).Code);
        Assert.Equal(4, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
    }

    [Fact]
    public async Task Without_bank_references_the_same_date_amount_and_text_is_a_duplicate_but_text_alone_never_is()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        await ImportAsync(current, File("a.csv", "03/10/2026;KOFFIE;CAFE;-3,20;", "03/10/2026;KOFFIE;CAFE;-3,20;"));

        var again = await PreviewAsync(current, File("b.csv",
            "03/10/2026;KOFFIE;CAFE;-3,20;",
            "03/10/2026;KOFFIE;CAFE;-3,20;",
            "03/10/2026;KOFFIE;CAFE;-3,20;", // a third coffee that day is new: each existing line matches once
            "04/10/2026;KOFFIE;CAFE;-3,40;")); // same text, other amount: new

        Assert.Equal(
            [ImportRowStatus.Duplicate, ImportRowStatus.Duplicate, ImportRowStatus.New, ImportRowStatus.New],
            again.Candidates.Select(c => c.Status).ToArray());
    }

    [Fact]
    public async Task Invalid_lines_and_lines_before_the_balance_date_are_reported_and_never_imported()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 50_000);

        var preview = await PreviewAsync(current, File("x.csv",
            "30/09/2026;OUD;WINKEL;-5,00;",
            "31/09/2026;FOUT;WINKEL;-5,00;",
            "04/10/2026;NUL;WINKEL;0,00;",
            "04/10/2026;GEEN BEDRAG;WINKEL;abc;",
            "05/10/2026;GOED;WINKEL;-5,00;"));

        Assert.Equal(
            [ImportRowStatus.BeforeOpening, ImportRowStatus.Invalid, ImportRowStatus.Invalid, ImportRowStatus.Invalid, ImportRowStatus.New],
            preview.Candidates.Select(c => c.Status).ToArray());
        Assert.Equal(["import.row.date", "import.row.zero", "import.row.amount"], preview.Candidates.Skip(1).Take(3).Select(c => c.Problem!).ToArray());

        preview.Candidates[0].Include = true; // cannot be forced in
        Assert.False(preview.Candidates[0].Include);

        var result = await _import.ConfirmAsync(preview);
        Assert.Equal(1, result.Imported);
        Assert.Equal((1, 3), (result.Batch.SkippedCount, result.Batch.InvalidCount));
        Assert.Equal(Money.FromCents(49_500), await _ledger.GetBalanceAsync(current.Id));
    }

    [Fact]
    public async Task A_failing_import_leaves_no_partial_data()
    {
        // A database trigger that rejects one specific line, as a stand-in for any failure halfway.
        using var failing = new ImportTestsWithFailure();
        var current = await failing.Tests.AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var preview = await failing.Tests.PreviewAsync(current, File("x.csv", "03/10/2026;EERSTE;A;-1,00;", "04/10/2026;BOOM;BOOM;-2,00;", "05/10/2026;DERDE;C;-3,00;"));

        await Assert.ThrowsAsync<DbUpdateException>(() => failing.Tests._import.ConfirmAsync(preview));

        Assert.Equal(0, failing.Tests._temp.Scalar("SELECT COUNT(*) FROM transactions;"));
        Assert.Equal(0, failing.Tests._temp.Scalar("SELECT COUNT(*) FROM import_batches;"));
        Assert.Equal(0, failing.Tests._temp.Scalar("SELECT COUNT(*) FROM import_profiles;"));
    }

    [Fact]
    public async Task A_line_typed_in_by_hand_is_flagged_as_possible_duplicate_and_can_still_be_included()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var typed = await _ledger.RecordAsync(new TransactionRequest(current.Id, new DateOnly(2026, 10, 3), Money.FromCents(4_280), TransactionType.Expense, "Supermarkt", Source: TransactionSource.QuickAdd));

        var preview = await PreviewAsync(current, File("x.csv", "05/10/2026;BETALING;COLRUYT GENT;-42,80;R1", "05/10/2026;BETALING;BAKKER;-42,80;R2"));

        var first = preview.Candidates[0];
        Assert.Equal(ImportRowStatus.PossibleDuplicate, first.Status);
        Assert.Equal(typed.Id, first.MatchedTransactionId);
        Assert.False(first.Include);
        Assert.Equal(ImportRowStatus.New, preview.Candidates[1].Status); // one typed line matches one bank line

        first.Include = true; // the user decides
        Assert.Equal(2, (await _import.ConfirmAsync(preview)).Imported);
    }

    [Fact]
    public async Task Transfer_to_an_own_account_is_suggested_creates_both_sides_and_is_never_spending()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);
        var joint = await AccountAsync("Gezamenlijke rekening", AccountType.JointCurrent);

        var preview = await PreviewAsync(current, File("zicht.csv", "09/10/2026;OVERSCHRIJVING;GEZAMENLIJKE REKENING;-600,00;Z1"));
        var line = Assert.Single(preview.Candidates);
        Assert.Equal((TransactionType.Transfer, joint.Id, ImportSuggestion.OwnAccount), (line.Type, line.TransferAccountId!.Value, line.Suggestion));

        await _import.ConfirmAsync(preview);

        Assert.Equal(Money.FromCents(40_000), await _ledger.GetBalanceAsync(current.Id));
        Assert.Equal(Money.FromCents(60_000), await _ledger.GetBalanceAsync(joint.Id));
        Assert.Equal(new PeriodTotals(Money.Zero, Money.Zero), await _ledger.GetTotalsAsync(Opening, new DateOnly(2026, 10, 31)));

        // Later the joint account's own file arrives: its +600 is the side that already exists.
        var jointFile = await PreviewAsync(joint, File("gezamenlijk.csv", "10/10/2026;OVERSCHRIJVING VAN;BENJAMIN;600,00;G1"));
        Assert.Equal(ImportRowStatus.Duplicate, Assert.Single(jointFile.Candidates).Status);
    }

    [Fact]
    public async Task When_the_other_side_was_imported_first_it_becomes_the_transfer_instead_of_a_second_copy()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);
        var joint = await AccountAsync("Gezamenlijke rekening", AccountType.JointCurrent);
        var other = await _categories.CreateAsync("Overige inkomsten", CategoryKind.Income);

        var jointPreview = await PreviewAsync(joint, File("gezamenlijk.csv", "10/10/2026;OVERSCHRIJVING VAN;BENJAMIN;600,00;G1"));
        jointPreview.Candidates[0].CategoryId = other.Id; // first booked as income
        await _import.ConfirmAsync(jointPreview);
        Assert.Equal(Money.FromCents(60_000), (await _ledger.GetTotalsAsync(Opening, new DateOnly(2026, 10, 31))).Income);

        var currentPreview = await PreviewAsync(current, File("zicht.csv", "09/10/2026;OVERSCHRIJVING;GEZAMENLIJKE REKENING;-600,00;Z1"));
        var result = await _import.ConfirmAsync(currentPreview);

        Assert.Equal(1, result.Linked);
        Assert.Equal(Money.FromCents(60_000), await _ledger.GetBalanceAsync(joint.Id)); // not 1200
        var jointLine = Assert.Single(await ActiveAsync(joint));
        Assert.Equal(TransactionType.Transfer, jointLine.Type);
        Assert.Equal("G1", jointLine.BankReference);
        Assert.Equal(new PeriodTotals(Money.Zero, Money.Zero), await _ledger.GetTotalsAsync(Opening, new DateOnly(2026, 10, 31)));

        // Undoing the current-account import puts the joint line back exactly as it was.
        await _import.UndoAsync(result.Batch.Id, includeChanged: false);
        jointLine = Assert.Single(await ActiveAsync(joint));
        Assert.Equal((TransactionType.Income, other.Id, (Guid?)null), (jointLine.Type, jointLine.CategoryId!.Value, jointLine.TransferId));
        Assert.Equal(Money.FromCents(100_000), await _ledger.GetBalanceAsync(current.Id));
    }

    [Fact]
    public async Task Visa_purchases_and_the_settlement_from_the_current_account_count_as_spending_once()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);
        var visa = await AccountAsync("VISA", AccountType.CreditCard);

        await ImportAsync(visa, File("visa.csv", "02/10/2026;AANKOOP;MEDIAMARKT;-400,00;V1"));
        var settlement = await PreviewAsync(current, File("zicht.csv", "06/10/2026;VISA AFREKENING SEPTEMBER;KBC CARD;-400,00;Z9"));
        Assert.Equal((TransactionType.Transfer, visa.Id), (settlement.Candidates[0].Type, settlement.Candidates[0].TransferAccountId!.Value));
        await _import.ConfirmAsync(settlement);

        Assert.Equal(Money.FromCents(400_00), (await _ledger.GetTotalsAsync(Opening, new DateOnly(2026, 10, 31))).Expenses);
        Assert.Equal(Money.FromCents(60_000), await _ledger.GetBalanceAsync(current.Id));
        Assert.Equal(Money.Zero, await _ledger.GetBalanceAsync(visa.Id));

        // The payment also appears on the card statement: already there.
        var cardFile = await PreviewAsync(visa, File("visa2.csv", "07/10/2026;UW BETALING;DANK U;400,00;V2"));
        Assert.Equal(ImportRowStatus.Duplicate, cardFile.Candidates[0].Status);
    }

    [Fact]
    public async Task Undoing_an_import_removes_its_lines_and_the_same_file_can_be_imported_again()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);
        var result = await ImportAsync(current, File("a.csv", Statement));

        var plan = await _import.PlanUndoAsync(result.Batch.Id);
        Assert.Equal((4, 0, 0), (plan.Unchanged, plan.Changed, plan.AlreadyRemoved));
        Assert.False(plan.NeedsChoice);

        Assert.Equal(4, await _import.UndoAsync(result.Batch.Id, includeChanged: false));
        Assert.Empty(await ActiveAsync(current));
        Assert.Equal(Money.FromCents(100_000), await _ledger.GetBalanceAsync(current.Id));
        Assert.Equal(ImportBatchStatus.Undone, Assert.Single(await _import.ListBatchesAsync()).Batch.Status);
        Assert.Equal("import.alreadyUndone", (await Assert.ThrowsAsync<DomainValidationException>(() => _import.UndoAsync(result.Batch.Id, false))).Code);

        var again = await PreviewAsync(current, File("a.csv", Statement));
        Assert.All(again.Candidates, c => Assert.Equal(ImportRowStatus.New, c.Status));
    }

    [Fact]
    public async Task Lines_changed_after_the_import_are_kept_unless_the_user_chooses_otherwise()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var result = await ImportAsync(current, File("a.csv", Statement));
        var spotify = (await ActiveAsync(current)).Single(t => t.BankReference == "R3");
        _clock.Now = _clock.Now.AddMinutes(5);
        await _ledger.UpdateAsync(spotify.Id, new TransactionRequest(current.Id, spotify.Date, Money.FromCents(1_799), TransactionType.Expense, "Spotify", Note: "familie"));

        var plan = await _import.PlanUndoAsync(result.Batch.Id);
        Assert.Equal((3, 1), (plan.Unchanged, plan.Changed));
        Assert.True(plan.NeedsChoice);

        await _import.UndoAsync(result.Batch.Id, includeChanged: false);
        Assert.Equal("Spotify", Assert.Single(await ActiveAsync(current)).Description);
    }

    [Fact]
    public async Task A_line_the_user_removed_is_not_brought_back_by_reimporting()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        await ImportAsync(current, File("a.csv", Statement));
        await _ledger.VoidAsync((await ActiveAsync(current)).Single(t => t.BankReference == "R4").Id);

        var again = await PreviewAsync(current, File("a.csv", Statement));
        var removed = again.Candidates.Single(c => c.Row.Reference == "R4");
        Assert.Equal(ImportRowStatus.Duplicate, removed.Status);
        Assert.True(removed.MatchWasRemoved);
    }

    [Fact]
    public async Task Categories_come_from_learned_rules_and_choices_made_in_the_preview_are_learned()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var subscriptions = await _categories.CreateAsync("Abonnementen", CategoryKind.Expense);
        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);
        await _rules.SaveAsync(PayeeRule.Learn("Spotify", TransactionType.Expense, subscriptions.Id, null, _clock.Now)); // learned via Quick Add

        var preview = await PreviewAsync(current, File("a.csv", Statement));
        var spotify = preview.Candidates.Single(c => c.Row.Reference == "R3");
        Assert.Equal((ImportSuggestion.PayeeRule, subscriptions.Id), (spotify.Suggestion, spotify.CategoryId!.Value));

        preview.Candidates.Single(c => c.Row.Reference == "R1").CategoryId = groceries.Id;
        await _import.ConfirmAsync(preview);

        var next = await PreviewAsync(current, File("b.csv", "12/10/2026;BETALING DEBETKAART;COLRUYT GENT;-30,00;R10"));
        Assert.Equal(groceries.Id, next.Candidates[0].CategoryId);
    }

    [Fact]
    public async Task A_corrected_column_mapping_is_remembered_for_the_next_file_with_the_same_header()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var file = File("a.csv", Statement);
        var suggestion = await _import.SuggestMappingAsync(file);
        Assert.False(suggestion.Remembered);

        var corrected = suggestion.Mapping with { CounterpartyColumn = null }; // the user prefers the bank's description
        await _import.ConfirmAsync(await _import.PreviewAsync(current.Id, file, corrected));

        var next = await _import.SuggestMappingAsync(File("b.csv", "12/10/2026;X;Y;-1,00;R9"));
        Assert.True(next.Remembered);
        Assert.Equal(corrected, next.Mapping);
    }

    [Fact]
    public async Task Three_months_of_history_reconcile_against_the_opening_balance()
    {
        var start = new DateOnly(2026, 7, 1);
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 150_000, start);
        var lines = new List<string>();
        long expected = 150_000;
        for (var day = start; day <= new DateOnly(2026, 9, 30); day = day.AddDays(3))
        {
            var cents = day.Day == 25 ? 240_000 : -(1_000 + day.DayOfYear);
            expected += cents;
            lines.Add($"{day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)};REGEL {day.DayOfYear};WINKEL {day.DayOfYear};{cents / 100},{Math.Abs(cents % 100):00};H{day.DayOfYear}");
        }

        var result = await ImportAsync(current, File("drie-maanden.csv", [.. lines]));

        Assert.Equal(lines.Count, result.Imported);
        var check = await _reconciliation.PreviewAsync(current.Id, new DateOnly(2026, 9, 30), Money.FromCents(expected));
        Assert.Equal(Money.Zero, check.Difference);
    }

    [Fact]
    public async Task A_preview_can_only_be_confirmed_once()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var preview = await PreviewAsync(current, File("a.csv", Statement));
        await _import.ConfirmAsync(preview);

        Assert.Equal("import.alreadyConfirmed", (await Assert.ThrowsAsync<DomainValidationException>(() => _import.ConfirmAsync(preview))).Code);
        Assert.Equal(4, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
    }

    [Fact]
    public async Task Choices_that_cannot_be_saved_name_the_line()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var preview = await PreviewAsync(current, File("a.csv", Statement));
        preview.Candidates[1].Type = TransactionType.Expense; // salary as an expense

        var ex = await Assert.ThrowsAsync<ImportRowException>(() => _import.ConfirmAsync(preview));
        Assert.Equal(("import.row.typeDirection", 3), (ex.Code, ex.RowNumber));
        Assert.Equal(0, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
    }

    /// <summary>The same services on a database with a trigger that fails one insert.</summary>
    private sealed class ImportTestsWithFailure : IDisposable
    {
        public ImportTestsWithFailure() =>
            Tests = new ImportTests(new ExtendedMigrationSource(
                "CREATE TRIGGER test_fail BEFORE INSERT ON transactions WHEN NEW.description = 'BOOM' BEGIN SELECT RAISE(ABORT, 'test failure'); END;"));

        public ImportTests Tests { get; }

        public void Dispose() => Tests.Dispose();
    }
}
