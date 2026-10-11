using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using PersonalFinanceOS.Infrastructure.Database;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>Phase 8 manual backup and guided restore (spec §4) against real database files.</summary>
public sealed class BackupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 16, 30, 0, TimeSpan.FromHours(2));
    private readonly TempDataFolder _temp = new();
    private readonly string _usb;

    public BackupTests()
    {
        _usb = Path.Combine(Path.GetTempPath(), "pfos-usb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_usb);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
        Directory.Delete(_usb, recursive: true);
    }

    private static int Latest => new EmbeddedMigrationSource().GetMigrations().Count;

    private BackupArchive Archive() => new(_temp.Paths, _temp.Log, "1.0.0", Latest);

    private const string InsertTransaction = """
        INSERT INTO transactions (id, account_id, date, amount_cents, type, description, source, status, created_at, updated_at)
        VALUES ('{0}', '11111111-1111-1111-1111-111111111111', '2026-10-05', {1}, 'Expense', 'Supermarkt', 'QuickAdd', 'Active', '2026-10-05T00:00:00.0000000Z', '2026-10-05T00:00:00.0000000Z');
        """;

    private void AddTransaction(long cents) => _temp.Execute(string.Format(System.Globalization.CultureInfo.InvariantCulture, InsertTransaction, Guid.NewGuid(), cents));

    private void SeedCurrent()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        _temp.Execute(DatabaseInitializerTests.InsertAccount);
        AddTransaction(-4_280);
        AddTransaction(-1_999);
    }

    [Fact]
    public void A_backup_is_one_checked_file_with_the_database_profile_and_manifest()
    {
        SeedCurrent();
        var target = Path.Combine(_usb, BackupArchive.SuggestedFileName(Now));
        var result = Archive().Create(target, Now);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal("Personal Finance OS backup 2026-10-11 1630.zip", Path.GetFileName(result.FilePath));
        Assert.Equal((1L, 2L, Latest), (result.Info!.Accounts, result.Info.Transactions, result.Info.SchemaVersion));
        using (var zip = ZipFile.OpenRead(target))
        {
            Assert.Equal(["finance.db", "manifest.json", "profile.json"], zip.Entries.Select(e => e.FullName).Order());
        }

        Assert.False(File.Exists(target + ".partial"));
        Assert.Empty(Directory.GetDirectories(_temp.Paths.BackupsDirectory, ".work-*"));
        var inspected = Archive().Inspect(target);
        Assert.True(inspected.IsValid);
        Assert.Equal(2, inspected.Info!.Transactions);

        // A temp folder sits on the same drive as the data here; a backup there would not survive a disk failure.
        Assert.True(Archive().IsOnSameDrive(_usb));
    }

    [Fact]
    public void Restore_brings_back_the_backup_after_keeping_a_checked_copy_of_the_current_data()
    {
        SeedCurrent();
        var target = Path.Combine(_usb, "backup.zip");
        Assert.True(Archive().Create(target, Now).Success);

        AddTransaction(-50_000);
        Assert.Equal(3, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));

        var result = Archive().Restore(target);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(2, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
        Assert.Equal(-6_279, _temp.Scalar("SELECT SUM(amount_cents) FROM transactions;"));

        // The data from just before the restore is kept and complete.
        Assert.True(File.Exists(result.PreRestoreBackup));
        using (var before = new SqliteConnection($"Data Source={result.PreRestoreBackup};Mode=ReadOnly;Pooling=false"))
        {
            before.Open();
            using var command = before.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM transactions;";
            Assert.Equal(3L, command.ExecuteScalar());
        }

        // The normal startup opens the restored data.
        Assert.Equal(DatabaseStartupStatus.UpToDate, _temp.Initializer().Initialize().Status);
    }

    [Fact]
    public void A_backup_from_an_older_version_is_restored_and_then_upgraded_by_the_normal_startup()
    {
        // Version 1 of the app: only the first two schema steps.
        Assert.True(_temp.Initializer(new OlderMigrationSource(2)).Initialize().CanOpen);
        _temp.Execute(DatabaseInitializerTests.InsertAccount);
        AddTransaction(-4_280);
        var old = Path.Combine(_usb, "old.zip");
        var made = new BackupArchive(_temp.Paths, _temp.Log, "0.1.0", 2).Create(old, Now);
        Assert.True(made.Success);
        Assert.Equal(2, made.Info!.SchemaVersion);

        // Today's app on today's schema, with other data.
        Assert.Equal(DatabaseStartupStatus.Migrated, _temp.Initializer().Initialize().Status);
        AddTransaction(-10);
        AddTransaction(-20);

        Assert.True(Archive().Restore(old).Success);
        Assert.Equal(2, _temp.Scalar("SELECT MAX(version) FROM schema_migrations;"));
        var startup = _temp.Initializer().Initialize();
        Assert.Equal(DatabaseStartupStatus.Migrated, startup.Status);
        Assert.Equal(Latest, startup.SchemaVersion);
        Assert.Equal(-4_280, _temp.Scalar("SELECT SUM(amount_cents) FROM transactions;"));
    }

    [Fact]
    public void Files_that_are_not_good_backups_are_refused_without_touching_the_data()
    {
        SeedCurrent();
        var good = Path.Combine(_usb, "good.zip");
        Assert.True(Archive().Create(good, Now).Success);

        var text = Path.Combine(_usb, "notes.zip");
        File.WriteAllText(text, "not a zip");
        Assert.Equal("backup.notABackup", Archive().Inspect(text).ErrorCode);

        var otherZip = Path.Combine(_usb, "photos.zip");
        using (var zip = ZipFile.Open(otherZip, ZipArchiveMode.Create))
        {
            zip.CreateEntry("photo.jpg");
        }

        Assert.Equal("backup.notABackup", Archive().Inspect(otherZip).ErrorCode);

        // A changed database no longer matches its checksum.
        var damaged = Path.Combine(_usb, "damaged.zip");
        File.Copy(good, damaged);
        using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            zip.GetEntry("finance.db")!.Delete();
            using var stream = zip.CreateEntry("finance.db").Open();
            stream.Write(Encoding.UTF8.GetBytes("SQLite format 3\0 but broken"));
        }

        Assert.Equal("backup.damaged", Archive().Inspect(damaged).ErrorCode);

        // Made by a newer app.
        Assert.Equal("backup.tooNew", new BackupArchive(_temp.Paths, _temp.Log, "0.1.0", Latest - 1).Inspect(good).ErrorCode);

        var refused = Archive().Restore(damaged);
        Assert.False(refused.Success);
        Assert.Null(refused.PreRestoreBackup);
        Assert.Equal(2, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
    }

    [Fact]
    public void A_backup_that_cannot_be_written_reports_failure_and_leaves_nothing_behind()
    {
        SeedCurrent();
        var missing = Path.Combine(_usb, "no-such-folder", "backup.zip");
        var result = Archive().Create(missing, Now);
        Assert.False(result.Success);
        Assert.Equal("backup.writeFailed", result.ErrorCode);
        Assert.False(File.Exists(missing));
        Assert.Empty(Directory.GetDirectories(_temp.Paths.BackupsDirectory, ".work-*"));
    }
}

/// <summary>The check the installer test in CI runs against the installed app.</summary>
public sealed class SelfCheckTests
{
    [Fact]
    public void The_installed_app_can_open_a_data_folder_without_a_window()
    {
        using var temp = new TempDataFolder();
        Assert.Equal(0, PersonalFinanceOS.App.Program.SelfCheck(temp.Paths.Root));
        Assert.True(File.Exists(temp.Paths.DatabaseFile));
        Assert.Equal(0, PersonalFinanceOS.App.Program.SelfCheck(temp.Paths.Root));

        File.WriteAllText(temp.Paths.DatabaseFile, "not a database");
        Assert.Equal(2, PersonalFinanceOS.App.Program.SelfCheck(temp.Paths.Root));
        Assert.Equal("not a database", File.ReadAllText(temp.Paths.DatabaseFile));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
