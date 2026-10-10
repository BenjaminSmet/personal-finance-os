using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Analysis;
using PersonalFinanceOS.Application.Assets;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Assets;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>Phase 6 against a real migrated database: valuations, assets and liabilities, ownership shares over time and net worth (spec §9, §17).</summary>
public sealed class AssetsTests : IDisposable
{
    private static readonly DateOnly January = new(2026, 1, 1);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly AssetService _assets;
    private readonly NetWorthService _netWorth;
    private readonly AnalysisService _analysis;

    public AssetsTests()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var audit = new AuditLog(db, _clock);
        var accountRepository = new AccountRepository(db);
        var categoryRepository = new CategoryRepository(db);
        var transactions = new TransactionRepository(db);
        var assetRepository = new AssetRepository(db);
        var valuations = new ValuationRepository(db);
        var ownership = new OwnershipRepository(db);
        _accounts = new AccountService(accountRepository, audit, _clock);
        _ledger = new LedgerService(transactions, accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _assets = new AssetService(assetRepository, valuations, ownership, accountRepository, audit, _clock);
        _netWorth = new NetWorthService(accountRepository, transactions, assetRepository, valuations, ownership, _clock);
        var recurring = new RecurringService(new RecurringRepository(db), accountRepository, categoryRepository, transactions, _ledger, audit, _clock);
        _analysis = new AnalysisService(accountRepository, categoryRepository, transactions, new BudgetRepository(db), ownership, recurring, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private Task<Account> AccountAsync(string name, AccountType type, long openingCents = 0, Share? share = null, CardSettings? card = null, DateOnly? opening = null) =>
        _accounts.CreateAsync(new CreateAccountRequest(name, type, opening ?? January, Money.FromCents(openingCents), share ?? Share.Full, Card: card));

    private static Money Euro(long euros) => Money.FromCents(euros * 100);

    private static NetWorthLine Line(NetWorth worth, string name) => worth.Lines.Single(l => l.Name == name);

    // ===== Net worth (spec §17, §23 joint balances) =====

    [Fact]
    public async Task Net_worth_counts_shares_once_never_the_credit_limit_and_explains_every_line()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 200_000);
        await AccountAsync("Gezamenlijk", AccountType.JointCurrent, 300_000, Share.FromPercent(50));
        var visa = await AccountAsync("VISA", AccountType.CreditCard, card: new CardSettings(Euro(1_500), 28, 5, current.Id));
        var pension = await AccountAsync("Pensioen", AccountType.Pension);
        await AccountAsync("Saxo", AccountType.Investment);
        await _ledger.RecordAsync(new TransactionRequest(visa.Id, new(2026, 10, 2), Euro(400), TransactionType.Expense, "Winkel"));
        await _assets.SetValuationAsync(pension.Id, new(2026, 9, 30), Euro(15_000));
        await _ledger.RecordTransferAsync(new TransferRequest(current.Id, pension.Id, new(2026, 10, 5), Euro(100), "Storting"));
        await _assets.CreateAsync(new AssetRequest("Huis", AssetKind.Property, Share.FromPercent(50), Euro(300_000), new(2026, 1, 15)));
        await _assets.CreateAsync(new AssetRequest("Hypotheek", AssetKind.Mortgage, Share.FromPercent(50), Euro(200_000), new(2026, 1, 15)));

        var worth = await _netWorth.CalculateAsync();

        Assert.Equal(Euro(1_900 + 1_500 + 15_100 + 0 + 150_000), worth.Assets);
        Assert.Equal(Euro(400 + 100_000), worth.Liabilities);
        Assert.Equal(Euro(68_100), worth.Total);
        Assert.Equal(Euro(1_900 + 3_000 + 15_100 + 300_000 - 400 - 200_000), worth.HouseholdTotal);

        // The joint account: the full balance and the user's part, counted once.
        var joint = Line(worth, "Gezamenlijk");
        Assert.Equal((Euro(3_000), Euro(1_500)), (joint.FullValue, joint.PersonalValue));

        // The card is a liability with what is owed; its limit is nowhere.
        var card = Line(worth, "VISA");
        Assert.True(card.IsLiability);
        Assert.Equal(Euro(400), card.FullValue);

        // Pension: the valuation plus the deposit after it.
        var p = Line(worth, "Pensioen");
        Assert.Equal((ValueSource.ValuationPlusLedger, new DateOnly(2026, 9, 30), Euro(15_100)), (p.Source, p.ValuedOn, p.FullValue));

        // Saxo has no valuation yet: its deposits are used and the total says it is incomplete.
        Assert.Equal(ValueSource.LedgerWithoutValuation, Line(worth, "Saxo").Source);
        Assert.False(worth.IsComplete);
        Assert.Equal(ValueSource.Ledger, Line(worth, "Zichtrekening").Source);
    }

    [Fact]
    public async Task A_valuation_per_date_is_kept_and_the_same_date_corrects_it()
    {
        var saxo = await AccountAsync("Saxo", AccountType.Investment);
        await _assets.SetValuationAsync(saxo.Id, new(2026, 6, 30), Euro(10_000));
        await _assets.SetValuationAsync(saxo.Id, new(2026, 9, 30), Euro(11_000));
        await _assets.SetValuationAsync(saxo.Id, new(2026, 9, 30), Euro(11_500), "Correctie");

        var list = await _assets.ListValuationsAsync(saxo.Id);
        Assert.Equal([Euro(10_000), Euro(11_500)], list.Select(v => v.Value));
        Assert.Equal("Correctie", list[1].Note);
        Assert.Equal(Euro(11_500), (await _netWorth.CalculateAsync()).Total);
        Assert.Equal(Euro(10_000), (await _netWorth.CalculateAsync(new(2026, 8, 31))).Total);

        await _assets.RemoveValuationAsync(list[1].Id);
        Assert.Equal(Euro(10_000), (await _netWorth.CalculateAsync()).Total);
    }

    [Fact]
    public async Task Valuations_are_only_for_manual_values_and_never_in_the_future()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var pension = await AccountAsync("Pensioen", AccountType.Pension);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => _assets.SetValuationAsync(current.Id, new(2026, 10, 1), Euro(5)));
        Assert.Equal("valuation.account.notManual", error.Code);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => _assets.SetValuationAsync(pension.Id, new(2026, 10, 11), Euro(5)));
        Assert.Equal("valuation.date.future", error.Code);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => _assets.SetValuationAsync(pension.Id, new(2026, 10, 1), Euro(-5)));
        Assert.Equal("valuation.value.negative", error.Code);
        Assert.Equal(0, _temp.Scalar("SELECT COUNT(*) FROM valuations;"));
    }

    [Fact]
    public async Task Old_valuations_are_flagged_as_stale()
    {
        var pension = await AccountAsync("Pensioen", AccountType.Pension);
        await _assets.SetValuationAsync(pension.Id, new(2026, 6, 1), Euro(12_000));
        var car = await _assets.CreateAsync(new AssetRequest("Auto", AssetKind.Vehicle, Share.Full, Euro(8_000), new(2026, 9, 1)));

        var worth = await _netWorth.CalculateAsync();
        Assert.True(Line(worth, "Pensioen").IsStale);
        Assert.False(Line(worth, "Auto").IsStale);
        Assert.True(worth.HasStaleValues);

        await _assets.SetValuationAsync(pension.Id, new(2026, 10, 1), Euro(12_400));
        Assert.False((await _netWorth.CalculateAsync()).HasStaleValues);
        Assert.Equal(Euro(20_400), (await _netWorth.CalculateAsync()).Total);
        Assert.Equal(car.Id, Line(worth, "Auto").Asset!.Id);
    }

    [Fact]
    public async Task Selling_an_asset_sets_it_to_zero_from_today_and_keeps_its_history()
    {
        await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var car = await _assets.CreateAsync(new AssetRequest("Auto", AssetKind.Vehicle, Share.Full, Euro(8_000), new(2026, 3, 1)));
        await _assets.ArchiveAsync(car.Id);

        var worth = await _netWorth.CalculateAsync();
        Assert.DoesNotContain(worth.Lines, l => l.Name == "Auto");
        Assert.Equal(Euro(8_000), (await _netWorth.CalculateAsync(new(2026, 9, 30))).Total);
        Assert.Empty(await _assets.ListAsync());
        Assert.Equal(2, _temp.Scalar("SELECT COUNT(*) FROM valuations;"));
    }

    [Fact]
    public async Task History_starts_at_the_first_balance_and_marks_months_with_missing_valuations()
    {
        await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);
        var pension = await AccountAsync("Pensioen", AccountType.Pension);
        await _assets.SetValuationAsync(pension.Id, new(2026, 8, 31), Euro(9_000));

        var history = await _netWorth.HistoryAsync();

        // January to October: November and December 2025 are before the first balance date and left out.
        Assert.Equal(10, history.Points.Count);
        Assert.Equal(new DateOnly(2026, 1, 1), history.Points[0].Month);
        Assert.Equal(new DateOnly(2026, 10, 10), history.Points[^1].Date);
        Assert.False(history.IsComplete);
        Assert.All(history.Points.Where(p => p.Date < new DateOnly(2026, 8, 31)), p => Assert.False(p.IsComplete));
        Assert.All(history.Points.Where(p => p.Date >= new DateOnly(2026, 8, 31)), p => Assert.True(p.IsComplete));
        Assert.Equal(Euro(10_000), history.Points[^1].Total);
        Assert.Equal(Euro(1_000), history.Points[0].Total);
    }

    // ===== Ownership shares over time (spec §9) =====

    [Fact]
    public async Task A_share_change_applies_from_its_date_and_keeps_earlier_months()
    {
        await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, opening: new(2026, 6, 1));
        var joint = await AccountAsync("Gezamenlijk", AccountType.JointCurrent, 200_000, Share.FromPercent(50), opening: new(2026, 6, 1));
        await _ledger.RecordAsync(new TransactionRequest(joint.Id, new(2026, 6, 10), Euro(100), TransactionType.Expense, "Winkel"));
        await _ledger.RecordAsync(new TransactionRequest(joint.Id, new(2026, 10, 2), Euro(100), TransactionType.Expense, "Winkel"));

        await _assets.ChangeShareAsync(joint.Id, new(2026, 7, 1), Share.FromPercent(60));

        Assert.Equal(Share.FromPercent(60), (await _accounts.ListAsync()).Single(a => a.Id == joint.Id).OwnershipShare);
        var changes = await _assets.ListShareChangesAsync(joint.Id);
        Assert.Equal([(new DateOnly(2026, 6, 1), 5_000), (new DateOnly(2026, 7, 1), 6_000)], changes.Select(c => (c.EffectiveFrom, c.Share.BasisPoints)));

        Assert.Equal(Euro(950), Line(await _netWorth.CalculateAsync(new(2026, 6, 30)), "Gezamenlijk").PersonalValue);
        Assert.Equal(Euro(1_080), Line(await _netWorth.CalculateAsync(), "Gezamenlijk").PersonalValue);

        var june = Assert.Single((await _analysis.AnalyzeMonthAsync(new(2026, 6, 1))).Shared);
        Assert.Equal((Share.FromPercent(50), Euro(50)), (june.Share, june.PersonalPart));
        var october = Assert.Single((await _analysis.AnalyzeMonthAsync(new(2026, 10, 1))).Shared);
        Assert.Equal((Share.FromPercent(60), Euro(60)), (october.Share, october.PersonalPart));

        // Becoming the sole owner takes the account out of the shared lines from then on.
        await _assets.ChangeShareAsync(joint.Id, new(2026, 10, 1), Share.Full);
        Assert.Empty((await _analysis.AnalyzeMonthAsync(new(2026, 10, 1))).Shared);
        Assert.Single((await _analysis.AnalyzeMonthAsync(new(2026, 6, 1))).Shared);
    }

    [Fact]
    public async Task A_share_change_cannot_start_in_the_future_or_apply_to_a_card()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent);
        var visa = await AccountAsync("VISA", AccountType.CreditCard, card: new CardSettings(Euro(1_000), 28, 5, current.Id));

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => _assets.ChangeShareAsync(current.Id, new(2026, 11, 1), Share.FromPercent(50)));
        Assert.Equal("share.date.future", error.Code);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => _assets.ChangeShareAsync(visa.Id, new(2026, 10, 1), Share.FromPercent(50)));
        Assert.Equal("share.card", error.Code);
        Assert.Equal(0, _temp.Scalar("SELECT COUNT(*) FROM ownership_changes;"));
    }

    [Fact]
    public async Task An_asset_share_change_on_the_same_date_corrects_it()
    {
        var house = await _assets.CreateAsync(new AssetRequest("Huis", AssetKind.Property, Share.FromPercent(50), Euro(300_000), new(2026, 1, 15)));
        await _assets.ChangeShareAsync(house.Id, new(2026, 9, 1), Share.FromPercent(70));
        await _assets.ChangeShareAsync(house.Id, new(2026, 9, 1), Share.FromPercent(75));

        Assert.Equal([5_000, 7_500], (await _assets.ListShareChangesAsync(house.Id)).Select(c => c.Share.BasisPoints));
        Assert.Equal(Euro(225_000), (await _netWorth.CalculateAsync()).Total);
        Assert.Equal(Euro(150_000), (await _netWorth.CalculateAsync(new(2026, 8, 31))).Total);
    }
}
