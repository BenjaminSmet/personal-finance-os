using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PersonalFinanceOS.Infrastructure.Database;

public sealed record BackupResult(bool Success, string? FilePath, string? Error);

/// <summary>
/// Consistent single-file copies of the database using SQLite's VACUUM INTO, verified after creation.
/// Success is only reported after the copy passed an integrity check and matches the source (spec §4).
/// </summary>
public sealed class DatabaseBackup
{
    public BackupResult Create(string sourceDatabase, string targetDirectory, string label)
    {
        Directory.CreateDirectory(targetDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(targetDirectory, $"{stamp}_{label}.db");
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(targetDirectory, $"{stamp}_{label}_{i}.db");
        }

        try
        {
            DatabaseFingerprint sourceFingerprint;
            using (var source = SqliteTools.Open(sourceDatabase, SqliteOpenMode.ReadWrite))
            {
                sourceFingerprint = DatabaseFingerprint.Capture(source);
                using var vacuum = source.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $target;";
                vacuum.Parameters.AddWithValue("$target", target);
                vacuum.ExecuteNonQuery();
            }

            var verification = Verify(target, sourceFingerprint);
            if (verification is not null)
            {
                TryDelete(target);
                return new BackupResult(false, null, verification);
            }

            return new BackupResult(true, target, null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            return new BackupResult(false, null, ex.Message);
        }
    }

    internal static string? Verify(string backupFile, DatabaseFingerprint expected)
    {
        if (!File.Exists(backupFile))
        {
            return "Backup file was not created.";
        }

        using var backup = SqliteTools.Open(backupFile, SqliteOpenMode.ReadOnly);
        if (!SqliteTools.IntegrityOk(backup))
        {
            return "Backup failed the integrity check.";
        }

        return DatabaseFingerprint.Capture(backup).Matches(expected) ? null : "Backup contents do not match the database.";
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
