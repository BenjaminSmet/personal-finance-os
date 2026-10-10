using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalFinanceOS.Infrastructure.Logging;

namespace PersonalFinanceOS.Infrastructure.Database;

public enum DatabaseStartupStatus
{
    /// <summary>First start for this Windows user: an empty profile was created.</summary>
    CreatedNew,

    /// <summary>Existing database, already on the current schema.</summary>
    UpToDate,

    /// <summary>Existing database was upgraded after a verified backup.</summary>
    Migrated,

    /// <summary>An upgrade failed; the original database was put back unchanged. App must not continue.</summary>
    MigrationFailedRestored,

    /// <summary>An upgrade failed and automatic restore also failed. The verified backup is still on disk.</summary>
    MigrationFailedManualRecovery,

    /// <summary>A profile existed here before, but the database file is gone. Nothing was created.</summary>
    DatabaseMissing,

    /// <summary>The database was made by a newer app version. Nothing was changed.</summary>
    DatabaseNewerThanApp,

    /// <summary>The file is damaged or is not a Personal Finance OS database. Nothing was changed.</summary>
    DatabaseUnreadable,

    /// <summary>A backup could not be made before an upgrade, so the upgrade was not attempted.</summary>
    BackupFailed,
}

public sealed record DatabaseStartupResult(
    DatabaseStartupStatus Status,
    int SchemaVersion,
    string? BackupFile = null,
    string? Detail = null)
{
    public bool CanOpen => Status is DatabaseStartupStatus.CreatedNew or DatabaseStartupStatus.UpToDate or DatabaseStartupStatus.Migrated;
}

/// <summary>
/// Opens the user's database safely on every start. Implements the data-preservation rule (spec §4):
/// installing or updating must never reset, overwrite or silently recreate the financial database.
///
/// Upgrade sequence:
///   1. integrity check of the existing file;
///   2. verified backup (VACUUM INTO + integrity check + row/total comparison);
///   3. each migration in its own transaction;
///   4. post-checks: integrity, foreign keys, schema version, row counts and money totals preserved;
///   5. on any failure: the failed file is kept aside and the verified backup is restored.
/// </summary>
public sealed class DatabaseInitializer(AppDataPaths paths, IMigrationSource migrations, SafeLog log, string appVersion)
{
    public int LatestVersion => migrations.GetMigrations().Count;

    public DatabaseStartupResult Initialize()
    {
        paths.EnsureDirectories();
        var all = migrations.GetMigrations();
        var latest = all.Count;

        if (!File.Exists(paths.DatabaseFile))
        {
            if (File.Exists(paths.ProfileMarkerFile))
            {
                log.Warn("Database file missing while a profile marker exists. Not creating a new database.");
                return new DatabaseStartupResult(DatabaseStartupStatus.DatabaseMissing, 0);
            }

            return CreateNew(all);
        }

        int? current;
        try
        {
            using var connection = SqliteTools.Open(paths.DatabaseFile, SqliteOpenMode.ReadWrite);
            if (!SqliteTools.IntegrityOk(connection))
            {
                log.Error("Database failed the integrity check at startup.");
                return new DatabaseStartupResult(DatabaseStartupStatus.DatabaseUnreadable, 0, Detail: "integrity");
            }

            current = SchemaVersioning.ReadVersion(connection);
        }
        catch (SqliteException ex)
        {
            log.Error("Database could not be opened.", ex);
            return new DatabaseStartupResult(DatabaseStartupStatus.DatabaseUnreadable, 0, Detail: ex.Message);
        }

        if (current is null)
        {
            log.Error("Database file has no schema history; refusing to modify it.");
            return new DatabaseStartupResult(DatabaseStartupStatus.DatabaseUnreadable, 0, Detail: "unknown-schema");
        }

        EnsureProfileMarker();

        if (current > latest)
        {
            log.Warn(string.Create(CultureInfo.InvariantCulture, $"Database schema v{current} is newer than this app (v{latest}). Not modified."));
            return new DatabaseStartupResult(DatabaseStartupStatus.DatabaseNewerThanApp, current.Value);
        }

        if (current == latest)
        {
            return new DatabaseStartupResult(DatabaseStartupStatus.UpToDate, latest);
        }

        return Upgrade(all, current.Value, latest);
    }

    /// <summary>
    /// Explicit user choice after <see cref="DatabaseStartupStatus.DatabaseMissing"/>: start an empty profile.
    /// The old marker is kept aside so the decision is traceable.
    /// </summary>
    public DatabaseStartupResult StartNewProfileAfterMissingDatabase()
    {
        if (File.Exists(paths.DatabaseFile))
        {
            throw new InvalidOperationException("A database already exists; refusing to replace it.");
        }

        if (File.Exists(paths.ProfileMarkerFile))
        {
            var aside = paths.ProfileMarkerFile + "." + DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".old";
            File.Move(paths.ProfileMarkerFile, aside);
        }

        log.Info("User explicitly chose to start a new profile after the database went missing.");
        return CreateNew(migrations.GetMigrations());
    }

    private DatabaseStartupResult CreateNew(IReadOnlyList<Migration> all)
    {
        var temp = paths.DatabaseFile + ".creating";
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            // Build in a temporary file and move into place only when complete, so a crash during
            // first start never leaves a half-created database that looks real.
            using (var connection = SqliteTools.Open(temp))
            {
                foreach (var migration in all)
                {
                    SchemaVersioning.Apply(connection, migration, appVersion);
                }
            }

            File.Move(temp, paths.DatabaseFile, overwrite: false);
            EnsureProfileMarker();
            log.Info(string.Create(CultureInfo.InvariantCulture, $"Created new database at schema v{all.Count}."));
            return new DatabaseStartupResult(DatabaseStartupStatus.CreatedNew, all.Count);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            log.Error("Creating the database failed.", ex);
            TryDelete(temp);
            return new DatabaseStartupResult(DatabaseStartupStatus.DatabaseUnreadable, 0, Detail: ex.Message);
        }
    }

    private DatabaseStartupResult Upgrade(IReadOnlyList<Migration> all, int current, int latest)
    {
        log.Info(string.Create(CultureInfo.InvariantCulture, $"Upgrading database schema v{current} -> v{latest}."));

        var backup = new DatabaseBackup().Create(
            paths.DatabaseFile,
            paths.BackupsDirectory,
            string.Create(CultureInfo.InvariantCulture, $"before-upgrade_v{current}-to-v{latest}"));

        if (!backup.Success || backup.FilePath is null)
        {
            log.Error("Pre-upgrade backup failed; upgrade not attempted. " + backup.Error);
            return new DatabaseStartupResult(DatabaseStartupStatus.BackupFailed, current, Detail: backup.Error);
        }

        log.Info("Verified pre-upgrade backup: " + Path.GetFileName(backup.FilePath));

        string? failure = null;
        try
        {
            using var connection = SqliteTools.Open(paths.DatabaseFile, SqliteOpenMode.ReadWrite);
            var before = DatabaseFingerprint.Capture(connection);

            foreach (var migration in all.Where(m => m.Version > current))
            {
                SchemaVersioning.Apply(connection, migration, appVersion);
                log.Info(string.Create(CultureInfo.InvariantCulture, $"Applied migration v{migration.Version} ({migration.Name})."));
            }

            if (!SqliteTools.IntegrityOk(connection))
            {
                failure = "integrity check failed after upgrade";
            }
            else if (!SqliteTools.ForeignKeysOk(connection))
            {
                failure = "foreign key check failed after upgrade";
            }
            else if (SchemaVersioning.ReadVersion(connection) != latest)
            {
                failure = "schema version mismatch after upgrade";
            }
            else if (!before.IsPreservedIn(DatabaseFingerprint.Capture(connection)))
            {
                failure = "row counts or totals changed during upgrade";
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
        }

        if (failure is null)
        {
            log.Info(string.Create(CultureInfo.InvariantCulture, $"Upgrade to v{latest} completed and validated."));
            return new DatabaseStartupResult(DatabaseStartupStatus.Migrated, latest, backup.FilePath);
        }

        log.Error("Upgrade failed: " + failure + ". Restoring the verified backup.");
        return RestoreAfterFailedUpgrade(backup.FilePath, current, failure);
    }

    private DatabaseStartupResult RestoreAfterFailedUpgrade(string backupFile, int originalVersion, string failure)
    {
        try
        {
            var failedCopy = paths.DatabaseFile + "." + DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".failed-upgrade";
            File.Move(paths.DatabaseFile, failedCopy);
            DeleteSidecars(paths.DatabaseFile);
            File.Copy(backupFile, paths.DatabaseFile, overwrite: false);

            using var check = SqliteTools.Open(paths.DatabaseFile, SqliteOpenMode.ReadOnly);
            if (!SqliteTools.IntegrityOk(check) || SchemaVersioning.ReadVersion(check) != originalVersion)
            {
                throw new IOException("Restored database did not verify.");
            }

            log.Info("Original database restored from the pre-upgrade backup.");
            return new DatabaseStartupResult(DatabaseStartupStatus.MigrationFailedRestored, originalVersion, backupFile, failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            log.Error("Automatic restore failed; manual recovery needed from " + Path.GetFileName(backupFile), ex);
            return new DatabaseStartupResult(DatabaseStartupStatus.MigrationFailedManualRecovery, originalVersion, backupFile, failure);
        }
    }

    private void EnsureProfileMarker()
    {
        if (File.Exists(paths.ProfileMarkerFile))
        {
            return;
        }

        var marker = new
        {
            profileId = Guid.NewGuid(),
            createdAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            note = "Personal Finance OS profile marker. Do not delete: it prevents an empty database from silently replacing a missing one.",
        };
        File.WriteAllText(paths.ProfileMarkerFile, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void DeleteSidecars(string databaseFile)
    {
        TryDelete(databaseFile + "-journal");
        TryDelete(databaseFile + "-wal");
        TryDelete(databaseFile + "-shm");
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
    }
}
