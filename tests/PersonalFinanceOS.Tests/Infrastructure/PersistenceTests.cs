using Microsoft.EntityFrameworkCore;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Infrastructure;
using PersonalFinanceOS.Infrastructure.Database;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

public class PersistenceTests
{
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));

    private static (AccountService Accounts, AppSettingsService Settings, Func<FinanceDbContext> Db) Compose(TempDataFolder temp)
    {
        Assert.True(temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        return (
            new AccountService(new AccountRepository(db), new AuditLog(db, Clock), Clock),
            new AppSettingsService(new SettingsStore(db, Clock)),
            db);
    }

    [Fact]
    public void Ef_model_matches_the_migrated_schema()
    {
        using var temp = new TempDataFolder();
        var (_, _, db) = Compose(temp);
        using var context = db();

        // Querying every mapped column fails if a table or column name differs from the SQL migrations.
        Assert.Empty(context.Accounts.ToList());
        Assert.Empty(context.AppSettings.ToList());
        Assert.Empty(context.AuditLog.ToList());
    }

    [Fact]
    public async Task Account_round_trips_exactly()
    {
        using var temp = new TempDataFolder();
        var (accounts, _, _) = Compose(temp);

        var created = await accounts.CreateAsync(new CreateAccountRequest(
            "Gezamenlijke rekening", AccountType.JointCurrent, new DateOnly(2026, 10, 1), Money.FromCents(250_075), new Share(5000), "met Alexander"));

        var loaded = Assert.Single(await accounts.ListAsync());
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("Gezamenlijke rekening", loaded.Name);
        Assert.Equal(AccountType.JointCurrent, loaded.Type);
        Assert.Equal(Money.FromCents(250_075), loaded.OpeningBalance);
        Assert.Equal(new Share(5000), loaded.OwnershipShare);
        Assert.Equal(new DateOnly(2026, 10, 1), loaded.OpeningDate);
        Assert.Equal("met Alexander", loaded.Notes);
        Assert.Equal(Clock.Now, loaded.CreatedAt);

        // Stored as integer cents, not as text or floating point.
        Assert.Equal(250_075, temp.Scalar("SELECT opening_balance_cents FROM accounts;"));
        Assert.Equal(1, temp.Scalar("SELECT typeof(opening_balance_cents) = 'integer' FROM accounts;"));
    }

    [Fact]
    public async Task Archived_accounts_are_hidden_by_default_and_changes_are_audited()
    {
        using var temp = new TempDataFolder();
        var (accounts, _, _) = Compose(temp);
        var a = await accounts.CreateAsync(new CreateAccountRequest("Oud", AccountType.Savings, new DateOnly(2026, 1, 1), Money.Zero, Share.Full));
        await accounts.CreateAsync(new CreateAccountRequest("Nieuw", AccountType.Savings, new DateOnly(2026, 1, 1), Money.Zero, Share.Full));

        await accounts.ArchiveAsync(a.Id);

        Assert.Equal(["Nieuw"], (await accounts.ListAsync()).Select(x => x.Name));
        Assert.Equal(2, (await accounts.ListAsync(includeArchived: true)).Count);
        Assert.Equal(3, temp.Scalar("SELECT COUNT(*) FROM audit_log;"));
    }

    [Fact]
    public async Task New_accounts_are_ordered_after_existing_ones()
    {
        using var temp = new TempDataFolder();
        var (accounts, _, _) = Compose(temp);
        await accounts.CreateAsync(new CreateAccountRequest("Zicht", AccountType.PersonalCurrent, new DateOnly(2026, 1, 1), Money.Zero, Share.Full));
        await accounts.CreateAsync(new CreateAccountRequest("Aaa spaar", AccountType.Savings, new DateOnly(2026, 1, 1), Money.Zero, Share.Full));

        Assert.Equal(["Zicht", "Aaa spaar"], (await accounts.ListAsync()).Select(x => x.Name));
    }

    [Fact]
    public async Task Settings_persist_and_have_safe_defaults()
    {
        using var temp = new TempDataFolder();
        var (_, settings, _) = Compose(temp);

        Assert.Equal(AppLanguage.Dutch, await settings.GetLanguageAsync());
        Assert.Equal(new Share(5000), await settings.GetJointDefaultShareAsync());

        await settings.SetLanguageAsync(AppLanguage.English);
        await settings.SetJointDefaultShareAsync(new Share(6000));

        Assert.Equal(AppLanguage.English, await settings.GetLanguageAsync());
        Assert.Equal(new Share(6000), await settings.GetJointDefaultShareAsync());
    }

    [Fact]
    public async Task Data_survives_an_upgrade_through_the_app_layer()
    {
        using var temp = new TempDataFolder();
        var (accounts, _, _) = Compose(temp);
        await accounts.CreateAsync(new CreateAccountRequest("Zicht", AccountType.PersonalCurrent, new DateOnly(2026, 1, 1), Money.FromCents(99_999), Share.Full));

        var result = temp.Initializer(new ExtendedMigrationSource("CREATE TABLE later (id INTEGER);")).Initialize();
        Assert.Equal(DatabaseStartupStatus.Migrated, result.Status);

        var options = FinanceDbContext.OptionsFor(temp.Paths.DatabaseFile);
        await using var db = new FinanceDbContext(options);
        var account = await db.Accounts.SingleAsync();
        Assert.Equal(Money.FromCents(99_999), account.OpeningBalance);
    }

    [Fact]
    public void Default_data_folder_is_per_user_local_app_data()
    {
        var previous = Environment.GetEnvironmentVariable(AppDataPaths.OverrideEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.OverrideEnvironmentVariable, null);
            var paths = AppDataPaths.ForCurrentUser();
            var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.StartsWith(expectedRoot, paths.Root, StringComparison.Ordinal);
            Assert.EndsWith(AppDataPaths.FolderName, paths.Root, StringComparison.Ordinal);
            Assert.DoesNotContain(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), paths.Root, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.OverrideEnvironmentVariable, previous);
        }
    }
}
