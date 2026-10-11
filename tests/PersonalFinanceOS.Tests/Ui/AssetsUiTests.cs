using Avalonia.Headless.XUnit;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Assets;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Wishlist;
using PersonalFinanceOS.Infrastructure.Persistence;
using PersonalFinanceOS.Tests.Infrastructure;
using static PersonalFinanceOS.Tests.Ui.LedgerUiTests;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Phase 6 through the real UI: net worth with valuations and shares, the wishlist and buying an item.</summary>
[Collection("UI")]
public class AssetsUiTests
{
    private static readonly DateOnly Opening = new(2026, 1, 1);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Fake pension and Saxo accounts, the card paid from the current account, salary and rent, and a €500 buffer.</summary>
    private static async Task SeedPhase6Async(TempDataFolder temp)
    {
        await SeedAsync(temp);
        var options = FinanceDbContext.OptionsFor(temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var clock = new FixedClock(DateTimeOffset.Now);
        var audit = new AuditLog(db, clock);
        var accountRepository = new AccountRepository(db);
        var categories = new CategoryRepository(db);
        var transactions = new TransactionRepository(db);
        var accounts = new AccountService(accountRepository, audit, clock);
        var ledger = new LedgerService(transactions, accountRepository, categories, audit, new UndoHistory(), clock);
        var recurring = new RecurringService(new RecurringRepository(db), accountRepository, categories, transactions, ledger, audit, clock);
        var cards = new CreditCardService(new CreditCardRepository(db), accountRepository, transactions, recurring, audit, clock);
        var list = await accounts.ListAsync();
        var current = list.Single(a => a.Name == "Zichtrekening");
        var visa = list.Single(a => a.Name == "VISA");

        await cards.SaveSettingsAsync(visa.Id, new CardSettings(Money.FromCents(150_000), 28, 5, current.Id));
        await accounts.CreateAsync(new CreateAccountRequest("Pensioensparen", AccountType.Pension, Opening, Money.FromCents(1_250_000), Share.Full));
        await accounts.CreateAsync(new CreateAccountRequest("Saxo ETF's", AccountType.Investment, Opening, Money.FromCents(900_000), Share.Full));
        await new AppSettingsService(new SettingsStore(db, clock)).SetMinimumBufferAsync(Money.FromCents(50_000));
        await recurring.CreateRuleAsync(new RecurringRuleRequest("Huur", RecurringKind.Expense, current.Id, null, null, Money.FromCents(85_000), RecurrenceFrequency.Monthly, Today.AddDays(5)));
        await recurring.CreateRuleAsync(new RecurringRuleRequest("Loon", RecurringKind.Income, current.Id, null, null, Money.FromCents(240_000), RecurrenceFrequency.Monthly, Today.AddDays(15)));
    }

    [AvaloniaFact]
    public async Task Net_worth_takes_valuations_assets_and_a_share_change_and_says_what_is_missing()
    {
        using var temp = new TempDataFolder();
        await SeedPhase6Async(temp);
        var (window, vm) = Start(temp);
        var assets = Navigate<AssetsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => assets.AssetLines.Count == 5);

        // Pension and Saxo have no value yet: their deposits are used and the total says it is incomplete.
        Assert.NotNull(assets.CompletenessNote);
        Assert.Contains("Pensioensparen", assets.CompletenessNote, StringComparison.Ordinal);
        Assert.Single(assets.LiabilityLines); // the VISA debt; its limit is never an asset

        var pension = assets.AssetLines.Single(l => l.Name == "Pensioensparen");
        Assert.True(pension.CanValue);
        await pension.ValueCommand.ExecuteAsync(null);
        Assert.True(assets.IsValuationOpen);
        assets.ValueText = "13.480,55";
        await assets.SaveValuationCommand.ExecuteAsync(null);
        Assert.Null(assets.PanelError);
        HeadlessSetup.PumpUntil(() => assets.AssetLines.Single(l => l.Name == "Pensioensparen").Line.Source == ValueSource.Valuation);

        var saxo = assets.AssetLines.Single(l => l.Name == "Saxo ETF's");
        await saxo.ValueCommand.ExecuteAsync(null);
        assets.ValueText = "9875,20";
        await assets.SaveValuationCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => assets.CompletenessNote is null);

        // The current account's value comes from its transactions: no valuation, only a share.
        Assert.False(assets.AssetLines.Single(l => l.Name == "Zichtrekening").CanValue);
        Assert.False(assets.LiabilityLines.Single().CanChangeShare);

        // Joint account: 60 % from the first of this month; earlier months keep 50 %.
        var joint = assets.AssetLines.Single(l => l.Name == "Gezamenlijke rekening");
        await joint.ShareCommand.ExecuteAsync(null);
        Assert.True(assets.IsShareOpen);
        assets.ShareDate = new DateTime(Today.Year, Today.Month, 1);
        assets.SharePercentText = "60";
        await assets.SaveShareCommand.ExecuteAsync(null);
        Assert.Null(assets.PanelError);
        HeadlessSetup.PumpUntil(() => assets.AssetLines.Single(l => l.Name == "Gezamenlijke rekening").Line.Share.BasisPoints == 6_000);
        Assert.Equal(2, temp.Scalar("SELECT COUNT(*) FROM ownership_changes;"));

        // A house and its mortgage, half each.
        assets.OpenAddCommand.Execute(null);
        assets.NewName = "Huis";
        assets.NewShareText = "50";
        assets.NewValueText = "320000";
        await assets.SaveAssetCommand.ExecuteAsync(null);
        Assert.Null(assets.AddError);
        assets.OpenAddCommand.Execute(null);
        assets.NewName = "Hypotheek";
        assets.NewKind = assets.KindOptions.Single(k => k.Kind == PersonalFinanceOS.Domain.Assets.AssetKind.Mortgage);
        assets.NewShareText = "50";
        assets.NewValueText = "210000";
        await assets.SaveAssetCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => assets.LiabilityLines.Count == 2 && assets.AssetLines.Count == 6);

        // 1.842,35 + 60 % of 2.400 + 8.150 + 13.480,55 + 9.875,20 + 160.000 − 312,40 − 105.000
        Assert.Equal("€ 89.475,70", assets.TotalText.Replace(' ', ' '));
        Assert.True(assets.HasShared);
        Assert.True(assets.HasHistory);
        Assert.NotNull(assets.HistoryNote); // earlier months had no pension or Saxo value

        window.Height = 2400;
        Save(window, "30-assets");

        var dashboard = Navigate<DashboardViewModel>(vm);
        HeadlessSetup.PumpUntil(() => dashboard.NetWorthText.Replace(' ', ' ') == "€ 89.475,70");
        Assert.Null(dashboard.NetWorthNote);
        Save(window, "31-dashboard-net-worth");
    }

    [AvaloniaFact]
    public async Task The_wishlist_tells_prudent_from_technically_affordable_and_buys_once()
    {
        using var temp = new TempDataFolder();
        await SeedPhase6Async(temp);
        var (window, vm) = Start(temp);
        var wishlist = Navigate<WishlistViewModel>(vm);

        wishlist.OpenNewCommand.Execute(null);
        wishlist.FormName = "Koptelefoon";
        wishlist.FormPriceText = "149";
        await wishlist.SaveItemCommand.ExecuteAsync(null);
        Assert.Null(wishlist.FormError);
        HeadlessSetup.PumpUntil(() => wishlist.HasCheck && wishlist.Items.Count == 1);
        Assert.Equal(Loc("Verdict_Prudent"), wishlist.VerdictTitle);

        wishlist.OpenNewCommand.Execute(null);
        wishlist.FormName = "Racefiets";
        wishlist.FormPriceText = "1.200";
        wishlist.FormPriority = wishlist.PriorityOptions.Single(p => p.Priority == WishlistPriority.High);
        wishlist.FormTargetDate = Today.AddMonths(2).ToDateTime(TimeOnly.MinValue);
        wishlist.FormSavingText = "100";
        wishlist.FormAlternative = "Tweedehands voor €650";
        await wishlist.SaveItemCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => wishlist.Items.Count == 2 && wishlist.CheckTitle == "Racefiets");

        // €1.842,35 now, rent €850 and the VISA statement of €312,40 before the salary, €500 buffer: €1.200 is there, but not prudent.
        Assert.Equal(Loc("Verdict_TechnicallyOnly"), wishlist.VerdictTitle);
        Assert.Contains(wishlist.PaymentOptions, o => o.Title.Contains("VISA", StringComparison.Ordinal));
        Assert.NotNull(wishlist.SavingPlanText);
        Assert.True(wishlist.Items[0].IsHighPriority);
        Assert.False(wishlist.Items[0].IsAffordable);
        Assert.True(wishlist.Items[1].IsAffordable);

        window.Height = 2200;
        Save(window, "32-wishlist");

        // Buy the headphones by card: one expense, and the item cannot be bought again.
        var before = temp.Scalar("SELECT COUNT(*) FROM transactions;");
        await wishlist.Items.Single(r => r.Name == "Koptelefoon").OpenCommand.ExecuteAsync(null);
        await wishlist.OpenPurchaseCommand.ExecuteAsync(null);
        Assert.True(wishlist.IsPurchaseOpen);
        Assert.Equal("Zichtrekening", wishlist.PurchaseAccount!.Name);
        wishlist.PurchaseAccount = wishlist.PurchaseAccounts.Single(a => a.Name == "VISA");
        wishlist.PurchaseAmountText = "139,99";
        Save(window, "33-wishlist-buy");
        await wishlist.ConfirmPurchaseCommand.ExecuteAsync(null);
        Assert.Null(wishlist.PurchaseError);
        Assert.StartsWith("Koptelefoon gekocht", wishlist.StatusMessage, StringComparison.Ordinal);
        HeadlessSetup.PumpUntil(() => wishlist.ClosedItems.Count == 1);

        Assert.Equal(before + 1, temp.Scalar("SELECT COUNT(*) FROM transactions;"));
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM wishlist_items WHERE status = 'Purchased' AND purchase_transaction_id IS NOT NULL;"));
        await wishlist.ClosedItems[0].OpenCommand.ExecuteAsync(null);
        Assert.False(wishlist.CanBuy);
    }

    private static string Loc(string key) => PersonalFinanceOS.App.Localization.Loc.T(key);
}
