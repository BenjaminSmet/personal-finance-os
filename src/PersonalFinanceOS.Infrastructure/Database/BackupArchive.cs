using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalFinanceOS.Infrastructure.Logging;

namespace PersonalFinanceOS.Infrastructure.Database;

/// <summary>What a backup file says about itself, after it was checked.</summary>
public sealed record BackupInfo(DateTimeOffset CreatedAt, string AppVersion, int SchemaVersion, long Accounts, long Transactions);

/// <summary>
/// The result of a manual backup. <see cref="Success"/> is only true after the written file was read back and verified
/// (spec §4: never claim success before verification). <see cref="ErrorCode"/> is a localizable code.
/// </summary>
public sealed record ManualBackupResult(bool Success, string? FilePath, string? ErrorCode, BackupInfo? Info)
{
    public static ManualBackupResult Failed(string code) => new(false, null, code, null);
}

public sealed record BackupInspection(BackupInfo? Info, string? ErrorCode)
{
    public bool IsValid => Info is not null;
}

public sealed record RestoreResult(bool Success, string? ErrorCode, string? PreRestoreBackup);

/// <summary>
/// Manual backups and guided restore (spec §4). A backup is one zip file in a folder the user picks, holding a
/// consistent copy of the database (which also holds every setting), the profile marker and a manifest with the app
/// and schema version, row counts and a checksum. Restoring first makes a verified backup of the current data, then
/// replaces the database; the normal startup checks then open it and upgrade an older schema the safe way.
/// </summary>
public sealed class BackupArchive(AppDataPaths paths, SafeLog log, string appVersion, int latestSchemaVersion)
{
    public const string Extension = ".zip";
    private const string DatabaseEntry = "finance.db";
    private const string ProfileEntry = "profile.json";
    private const string ManifestEntry = "manifest.json";
    private const int Format = 1;

    /// <summary>True when the folder is on the same drive as the app's data: such a backup does not survive a disk failure.</summary>
    public bool IsOnSameDrive(string folder) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(folder)), Path.GetPathRoot(paths.Root), StringComparison.OrdinalIgnoreCase);

    /// <summary>"Personal Finance OS backup 2026-10-11 1648.zip"</summary>
    public static string SuggestedFileName(DateTimeOffset now) =>
        "Personal Finance OS backup " + now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + Extension;

    /// <summary>Creates a backup at <paramref name="targetFile"/>. Nothing is left behind when it fails.</summary>
    public ManualBackupResult Create(string targetFile, DateTimeOffset now)
    {
        var work = WorkFolder();
        var copy = Path.Combine(work, DatabaseEntry);
        var partial = targetFile + ".partial";
        try
        {
            DatabaseFingerprint fingerprint;
            int schema;
            using (var source = SqliteTools.Open(paths.DatabaseFile, SqliteOpenMode.ReadWrite))
            {
                fingerprint = DatabaseFingerprint.Capture(source);
                schema = SchemaVersioning.ReadVersion(source) ?? 0;
                using var vacuum = source.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $target;";
                vacuum.Parameters.AddWithValue("$target", copy);
                vacuum.ExecuteNonQuery();
            }

            if (DatabaseBackup.Verify(copy, fingerprint) is { } problem)
            {
                log.Error("Manual backup copy failed verification: " + problem);
                return ManualBackupResult.Failed("backup.verifyFailed");
            }

            var info = new BackupInfo(now, appVersion, schema, Count(fingerprint, "accounts"), Count(fingerprint, "transactions"));
            var manifest = new Manifest(Format, info.CreatedAt, info.AppVersion, info.SchemaVersion, info.Accounts, info.Transactions, Sha256(copy));
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(copy, DatabaseEntry, CompressionLevel.Optimal);
                if (File.Exists(paths.ProfileMarkerFile))
                {
                    zip.CreateEntryFromFile(paths.ProfileMarkerFile, ProfileEntry);
                }

                using var writer = new StreamWriter(zip.CreateEntry(ManifestEntry).Open());
                writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
            }

            // Read the written file back before saying it worked.
            var check = Inspect(partial);
            if (!check.IsValid || check.Info!.Transactions != info.Transactions)
            {
                log.Error("Manual backup file failed verification after writing.");
                TryDelete(partial);
                return ManualBackupResult.Failed("backup.verifyFailed");
            }

            File.Move(partial, targetFile, overwrite: true);
            log.Info("Manual backup created and verified.");
            return new ManualBackupResult(true, targetFile, null, info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            log.Error("Manual backup failed.", ex);
            TryDelete(partial);
            return ManualBackupResult.Failed(ex is UnauthorizedAccessException ? "backup.noAccess" : "backup.writeFailed");
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    /// <summary>Checks a backup file without changing anything: manifest, checksum, integrity and schema version.</summary>
    public BackupInspection Inspect(string file)
    {
        var work = WorkFolder();
        try
        {
            var (manifest, database) = Extract(file, work);
            if (manifest is null || database is null)
            {
                return new BackupInspection(null, "backup.notABackup");
            }

            if (manifest.Format > Format || manifest.SchemaVersion > latestSchemaVersion)
            {
                return new BackupInspection(null, "backup.tooNew");
            }

            if (!string.Equals(Sha256(database), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new BackupInspection(null, "backup.damaged");
            }

            using (var connection = SqliteTools.Open(database, SqliteOpenMode.ReadOnly))
            {
                if (!SqliteTools.IntegrityOk(connection) || SchemaVersioning.ReadVersion(connection) != manifest.SchemaVersion)
                {
                    return new BackupInspection(null, "backup.damaged");
                }
            }

            return new BackupInspection(new BackupInfo(manifest.CreatedAt, manifest.AppVersion, manifest.SchemaVersion, manifest.Accounts, manifest.Transactions), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException or JsonException)
        {
            log.Warn("A file chosen for restore could not be read as a backup.");
            return new BackupInspection(null, "backup.notABackup");
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    /// <summary>
    /// Replaces the current data with the backup. First a verified backup of the current data is made in the app's
    /// backups folder; if that fails, nothing is replaced. If replacing fails half-way, the current data is put back.
    /// All database connections must be closed (the app does this by closing the main window first).
    /// </summary>
    public RestoreResult Restore(string file)
    {
        var inspection = Inspect(file);
        if (!inspection.IsValid)
        {
            return new RestoreResult(false, inspection.ErrorCode, null);
        }

        SqliteConnection.ClearAllPools();
        var before = new DatabaseBackup().Create(paths.DatabaseFile, paths.BackupsDirectory, "before-restore");
        if (!before.Success)
        {
            log.Error("Restore stopped: the backup of the current data failed. " + before.Error);
            return new RestoreResult(false, "backup.preRestoreFailed", null);
        }

        var work = WorkFolder();
        var profileBefore = File.Exists(paths.ProfileMarkerFile) ? File.ReadAllBytes(paths.ProfileMarkerFile) : null;
        try
        {
            var (_, database) = Extract(file, work);
            File.Copy(database!, paths.DatabaseFile + ".restoring", overwrite: true);
            DeleteSidecars();
            File.Move(paths.DatabaseFile + ".restoring", paths.DatabaseFile, overwrite: true);
            var profile = Path.Combine(work, ProfileEntry);
            if (File.Exists(profile))
            {
                File.Copy(profile, paths.ProfileMarkerFile, overwrite: true);
            }

            using (var connection = SqliteTools.Open(paths.DatabaseFile, SqliteOpenMode.ReadOnly))
            {
                if (!SqliteTools.IntegrityOk(connection))
                {
                    throw new InvalidDataException("Restored database failed the integrity check.");
                }
            }

            log.Info("Backup restored. The previous data was kept as " + Path.GetFileName(before.FilePath));
            return new RestoreResult(true, null, before.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            log.Error("Restore failed; putting the previous data back.", ex);
            try
            {
                DeleteSidecars();
                File.Copy(before.FilePath!, paths.DatabaseFile, overwrite: true);
                if (profileBefore is not null)
                {
                    File.WriteAllBytes(paths.ProfileMarkerFile, profileBefore);
                }
            }
            catch (IOException rollback)
            {
                log.Error("Putting the previous data back failed; it is kept in the backups folder.", rollback);
            }

            TryDelete(paths.DatabaseFile + ".restoring");
            return new RestoreResult(false, "backup.restoreFailed", before.FilePath);
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    private static (Manifest? Manifest, string? Database) Extract(string file, string work)
    {
        using var zip = ZipFile.OpenRead(file);
        var manifestEntry = zip.GetEntry(ManifestEntry);
        var databaseEntry = zip.GetEntry(DatabaseEntry);
        if (manifestEntry is null || databaseEntry is null)
        {
            return (null, null);
        }

        Manifest? manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifest = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd(), JsonOptions);
        }

        // Only the two known entries are ever written to disk, under fixed names.
        var database = Path.Combine(work, DatabaseEntry);
        databaseEntry.ExtractToFile(database, overwrite: true);
        if (zip.GetEntry(ProfileEntry) is { } profile)
        {
            profile.ExtractToFile(Path.Combine(work, ProfileEntry), overwrite: true);
        }

        return (manifest, database);
    }

    private string WorkFolder()
    {
        var folder = Path.Combine(paths.BackupsDirectory, ".work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void DeleteSidecars()
    {
        TryDelete(paths.DatabaseFile + "-wal");
        TryDelete(paths.DatabaseFile + "-shm");
        TryDelete(paths.DatabaseFile + "-journal");
    }

    private static long Count(DatabaseFingerprint fingerprint, string table) => fingerprint.RowCounts.GetValueOrDefault(table);

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
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

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Manifest(int Format, DateTimeOffset CreatedAt, string AppVersion, int SchemaVersion, long Accounts, long Transactions, string Sha256);
}
