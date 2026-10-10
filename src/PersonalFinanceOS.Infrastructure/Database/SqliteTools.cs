using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PersonalFinanceOS.Infrastructure.Database;

/// <summary>Low-level SQLite helpers used by migrations, backups and integrity checks.</summary>
internal static class SqliteTools
{
    public static string ConnectionString(string file, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = mode,
            Pooling = false, // files are copied/restored; pooled handles would keep them locked
            ForeignKeys = true,
        }.ToString();

    public static SqliteConnection Open(string file, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(ConnectionString(file, mode));
        connection.Open();
        return connection;
    }

    public static bool IntegrityOk(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetString(0) == "ok" && !reader.Read();
    }

    public static bool ForeignKeysOk(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        return !reader.Read();
    }

    public static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public static IReadOnlyList<string> UserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// Row counts per table plus key money totals. Compared before and after a migration and between a
/// database and its backup. Contains numbers only in memory; it is never written to the log.
/// </summary>
internal sealed record DatabaseFingerprint(IReadOnlyDictionary<string, long> RowCounts, long OpeningBalanceTotalCents)
{
    public static DatabaseFingerprint Capture(SqliteConnection connection)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in SqliteTools.UserTables(connection))
        {
            counts[table] = SqliteTools.Scalar(connection, $"SELECT COUNT(*) FROM {SqliteTools.QuoteIdentifier(table)};");
        }

        var openingTotal = SqliteTools.TableExists(connection, "accounts")
            ? SqliteTools.Scalar(connection, "SELECT COALESCE(SUM(opening_balance_cents), 0) FROM accounts;")
            : 0;

        return new DatabaseFingerprint(counts, openingTotal);
    }

    /// <summary>
    /// True when every table that existed before still has the same number of rows and the money totals
    /// are unchanged. New tables may appear. The migration log table itself grows by design.
    /// </summary>
    public bool IsPreservedIn(DatabaseFingerprint after)
    {
        foreach (var (table, count) in RowCounts)
        {
            if (table == SchemaVersioning.HistoryTable)
            {
                continue;
            }

            if (!after.RowCounts.TryGetValue(table, out var afterCount) || afterCount != count)
            {
                return false;
            }
        }

        return OpeningBalanceTotalCents == after.OpeningBalanceTotalCents;
    }

    public bool Matches(DatabaseFingerprint other) =>
        OpeningBalanceTotalCents == other.OpeningBalanceTotalCents &&
        RowCounts.Count == other.RowCounts.Count &&
        RowCounts.All(kv => other.RowCounts.TryGetValue(kv.Key, out var c) && c == kv.Value);
}
