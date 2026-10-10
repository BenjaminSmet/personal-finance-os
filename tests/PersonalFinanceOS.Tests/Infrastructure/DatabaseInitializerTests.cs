using PersonalFinanceOS.Infrastructure.Database;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>Spec §4 "immutable data-preservation rule" and §23 "Updates, installer, backup".</summary>
public class DatabaseInitializerTests
{
    private const string InsertAccount = """
        INSERT INTO accounts (id, name, type, currency, ownership_share_bp, opening_date, opening_balance_cents, status, sort_order, created_at, updated_at)
        VALUES ('11111111-1111-1111-1111-111111111111', 'Zicht', 'PersonalCurrent', 'EUR', 10000, '2026-10-01', 123456, 'Active', 0, '2026-10-01T00:00:00.0000000Z', '2026-10-01T00:00:00.0000000Z');
        """;

    [Fact]
    public void Embedded_migrations_are_found_and_numbered()
    {
        var migrations = new EmbeddedMigrationSource().GetMigrations();
        Assert.NotEmpty(migrations);
        Assert.Equal(1, migrations[0].Version);
        Assert.Equal("initial", migrations[0].Name);
    }

    [Fact]
    public void Fresh_install_creates_database_in_the_data_folder()
    {
        using var temp = new TempDataFolder();
        var result = temp.Initializer().Initialize();

        Assert.Equal(DatabaseStartupStatus.CreatedNew, result.Status);
        Assert.True(File.Exists(temp.Paths.DatabaseFile));
        Assert.True(File.Exists(temp.Paths.ProfileMarkerFile));
        Assert.Equal(result.SchemaVersion, temp.Scalar("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(result.SchemaVersion, temp.Scalar("PRAGMA user_version;"));
    }

    [Fact]
    public void Second_start_reuses_the_existing_database()
    {
        using var temp = new TempDataFolder();
        temp.Initializer().Initialize();
        temp.Execute(InsertAccount);

        var result = temp.Initializer().Initialize();

        Assert.Equal(DatabaseStartupStatus.UpToDate, result.Status);
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM accounts;"));
    }

    [Fact]
    public void Missing_database_with_existing_profile_is_never_silently_recreated()
    {
        using var temp = new TempDataFolder();
        temp.Initializer().Initialize();
        File.Delete(temp.Paths.DatabaseFile);

        var result = temp.Initializer().Initialize();

        Assert.Equal(DatabaseStartupStatus.DatabaseMissing, result.Status);
        Assert.False(result.CanOpen);
        Assert.False(File.Exists(temp.Paths.DatabaseFile));
    }

    [Fact]
    public void User_can_explicitly_start_a_new_profile_after_missing_database()
    {
        using var temp = new TempDataFolder();
        var initializer = temp.Initializer();
        initializer.Initialize();
        File.Delete(temp.Paths.DatabaseFile);
        initializer.Initialize();

        var result = initializer.StartNewProfileAfterMissingDatabase();

        Assert.Equal(DatabaseStartupStatus.CreatedNew, result.Status);
        Assert.True(File.Exists(temp.Paths.DatabaseFile));
        Assert.NotEmpty(Directory.GetFiles(temp.Paths.Root, "profile.json.*.old"));
    }

    [Fact]
    public void Upgrade_makes_verified_backup_and_preserves_all_data()
    {
        using var temp = new TempDataFolder();
        temp.Initializer().Initialize();
        temp.Execute(InsertAccount);

        var upgraded = new ExtendedMigrationSource("CREATE TABLE extra_feature (id INTEGER PRIMARY KEY);");
        var result = temp.Initializer(upgraded).Initialize();

        Assert.Equal(DatabaseStartupStatus.Migrated, result.Status);
        Assert.Equal(upgraded.GetMigrations().Count, result.SchemaVersion);
        Assert.NotNull(result.BackupFile);
        Assert.True(File.Exists(result.BackupFile));
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(123456, temp.Scalar("SELECT SUM(opening_balance_cents) FROM accounts;"));
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM extra_feature;"));
    }

    [Fact]
    public void Failed_upgrade_restores_the_original_database()
    {
        using var temp = new TempDataFolder();
        var original = temp.Initializer().Initialize();
        temp.Execute(InsertAccount);

        // Second extra step fails halfway: the first step succeeded before it.
        var broken = new ExtendedMigrationSource(
            "CREATE TABLE step_one (id INTEGER PRIMARY KEY);",
            "CREATE TABLE step_two (id INTEGER PRIMARY KEY); INSERT INTO no_such_table VALUES (1);");
        var result = temp.Initializer(broken).Initialize();

        Assert.Equal(DatabaseStartupStatus.MigrationFailedRestored, result.Status);
        Assert.False(result.CanOpen);
        Assert.Equal(original.SchemaVersion, temp.Scalar("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('step_one', 'step_two');"));
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM accounts;"));
        Assert.NotEmpty(Directory.GetFiles(temp.Paths.Root, "finance.db.*.failed-upgrade"));
    }

    [Fact]
    public void Upgrade_that_loses_rows_is_detected_and_rolled_back()
    {
        using var temp = new TempDataFolder();
        temp.Initializer().Initialize();
        temp.Execute(InsertAccount);

        var lossy = new ExtendedMigrationSource("DELETE FROM accounts;");
        var result = temp.Initializer(lossy).Initialize();

        Assert.Equal(DatabaseStartupStatus.MigrationFailedRestored, result.Status);
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM accounts;"));
    }

    [Fact]
    public void Database_from_newer_app_is_not_modified()
    {
        using var temp = new TempDataFolder();
        temp.Initializer(new ExtendedMigrationSource("CREATE TABLE future (id INTEGER);")).Initialize();
        var before = File.ReadAllBytes(temp.Paths.DatabaseFile);

        var result = temp.Initializer().Initialize();

        Assert.Equal(DatabaseStartupStatus.DatabaseNewerThanApp, result.Status);
        Assert.Equal(before, File.ReadAllBytes(temp.Paths.DatabaseFile));
    }

    [Fact]
    public void Damaged_file_is_not_modified()
    {
        using var temp = new TempDataFolder();
        temp.Paths.EnsureDirectories();
        File.WriteAllText(temp.Paths.DatabaseFile, "this is not a database");

        var result = temp.Initializer().Initialize();

        Assert.Equal(DatabaseStartupStatus.DatabaseUnreadable, result.Status);
        Assert.Equal("this is not a database", File.ReadAllText(temp.Paths.DatabaseFile));
    }

    [Fact]
    public void Foreign_sqlite_file_without_schema_history_is_not_modified()
    {
        using var temp = new TempDataFolder();
        temp.Paths.EnsureDirectories();
        temp.Execute("CREATE TABLE something_else (x INTEGER); INSERT INTO something_else VALUES (1);");

        var result = temp.Initializer().Initialize();

        Assert.Equal(DatabaseStartupStatus.DatabaseUnreadable, result.Status);
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM something_else;"));
    }

    [Fact]
    public void Log_never_contains_amounts_or_names()
    {
        using var temp = new TempDataFolder();
        temp.Initializer().Initialize();
        temp.Execute(InsertAccount);
        temp.Initializer(new ExtendedMigrationSource("CREATE TABLE x (id INTEGER);")).Initialize();

        var log = File.ReadAllText(Path.Combine(temp.Paths.LogsDirectory, "app.log"));
        Assert.DoesNotContain("123456", log, StringComparison.Ordinal);
        Assert.DoesNotContain("1234,56", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Zicht", log, StringComparison.Ordinal);
    }
}
