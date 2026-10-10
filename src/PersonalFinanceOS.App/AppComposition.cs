using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Settings;
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

        services.AddSingleton<IAccountRepository, AccountRepository>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<AppSettingsService>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
