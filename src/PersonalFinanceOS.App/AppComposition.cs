using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Analysis;
using PersonalFinanceOS.Application.Assets;
using PersonalFinanceOS.Application.Assistant;
using PersonalFinanceOS.Application.Budgets;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Import;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.QuickAdd;
using PersonalFinanceOS.Application.Reports;
using PersonalFinanceOS.Application.Scenarios;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Infrastructure;
using PersonalFinanceOS.Infrastructure.Database;
using PersonalFinanceOS.Infrastructure.Logging;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.App;

/// <summary>Wires the layers together. Only called after the database opened safely.</summary>
public static class AppComposition
{
    public static string AppVersion { get; } =
        typeof(AppComposition).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static ServiceProvider Build(AppDataPaths paths, DatabaseStartupResult startup, IClock? clock = null)
    {
        if (!startup.CanOpen)
        {
            throw new InvalidOperationException("The database must be opened safely before composing the app.");
        }

        var options = FinanceDbContext.OptionsFor(paths.DatabaseFile);
        var services = new ServiceCollection();

        services.AddSingleton(paths);
        services.AddSingleton(new SafeLog(paths.LogsDirectory));
        services.AddSingleton(clock ?? new SystemClock());
        services.AddSingleton(new AppRuntimeInfo(AppVersion, startup.SchemaVersion));
        services.AddSingleton<Func<FinanceDbContext>>(() => new FinanceDbContext(options));
        services.AddSingleton(sp => new BackupArchive(paths, sp.GetRequiredService<SafeLog>(), AppVersion, new EmbeddedMigrationSource().GetMigrations().Count));

        services.AddSingleton<IAccountRepository, AccountRepository>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddSingleton<ITransactionRepository, TransactionRepository>();
        services.AddSingleton<ICategoryRepository, CategoryRepository>();
        services.AddSingleton<IPayeeRuleRepository, PayeeRuleRepository>();
        services.AddSingleton<IReconciliationRepository, ReconciliationRepository>();
        services.AddSingleton<IImportRepository, ImportRepository>();
        services.AddSingleton<ICreditCardRepository, CreditCardRepository>();
        services.AddSingleton<IRecurringRepository, RecurringRepository>();
        services.AddSingleton<IBudgetRepository, BudgetRepository>();
        services.AddSingleton<IAssetRepository, AssetRepository>();
        services.AddSingleton<IValuationRepository, ValuationRepository>();
        services.AddSingleton<IOwnershipRepository, OwnershipRepository>();
        services.AddSingleton<IWishlistRepository, WishlistRepository>();
        services.AddSingleton<IScenarioRepository, ScenarioRepository>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<UndoHistory>();
        services.AddSingleton<LedgerService>();
        services.AddSingleton<CategoryService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<QuickAddService>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<RecurringService>();
        services.AddSingleton<CreditCardService>();
        services.AddSingleton<SpendingProfileService>();
        services.AddSingleton<ForecastService>();
        services.AddSingleton<BudgetService>();
        services.AddSingleton<AnalysisService>();
        services.AddSingleton<AssetService>();
        services.AddSingleton<NetWorthService>();
        services.AddSingleton<AffordabilityService>();
        services.AddSingleton<WishlistService>();
        services.AddSingleton<ScenarioService>();
        services.AddSingleton<AssistantService>();
        services.AddSingleton<ReportService>();

        services.AddSingleton<QuickAddViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<TransactionsViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<ImportViewModel>();
        services.AddSingleton<PlanningViewModel>();
        services.AddSingleton<ScenariosViewModel>();
        services.AddSingleton<ForecastsViewModel>();
        services.AddSingleton<BudgetsViewModel>();
        services.AddSingleton<WishlistViewModel>();
        services.AddSingleton<AssetsViewModel>();
        services.AddSingleton<AssistantViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
