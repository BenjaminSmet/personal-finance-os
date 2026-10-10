using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PersonalFinanceOS.Infrastructure.Database;

/// <summary>
/// Schema version bookkeeping. The <c>schema_migrations</c> table is the source of truth;
/// PRAGMA user_version mirrors it for quick inspection with external tools.
/// </summary>
internal static class SchemaVersioning
{
    public const string HistoryTable = "schema_migrations";

    public const string CreateHistoryTableSql = $"""
        CREATE TABLE IF NOT EXISTS {HistoryTable} (
            version     INTEGER NOT NULL PRIMARY KEY,
            name        TEXT    NOT NULL,
            applied_at  TEXT    NOT NULL,
            app_version TEXT    NOT NULL
        );
        """;

    /// <summary>Returns the current version, 0 for an empty database, or null when the file is not one of ours.</summary>
    public static int? ReadVersion(SqliteConnection connection)
    {
        if (!SqliteTools.TableExists(connection, HistoryTable))
        {
            return SqliteTools.UserTables(connection).Count == 0 ? 0 : null;
        }

        return (int)SqliteTools.Scalar(connection, $"SELECT COALESCE(MAX(version), 0) FROM {HistoryTable};");
    }

    public static void Apply(SqliteConnection connection, Migration migration, string appVersion)
    {
        using var transaction = connection.BeginTransaction();

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = CreateHistoryTableSql;
            create.ExecuteNonQuery();
        }

        using (var script = connection.CreateCommand())
        {
            script.Transaction = transaction;
            script.CommandText = migration.Sql;
            script.ExecuteNonQuery();
        }

        using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = $"INSERT INTO {HistoryTable} (version, name, applied_at, app_version) VALUES ($v, $n, $t, $a);";
            record.Parameters.AddWithValue("$v", migration.Version);
            record.Parameters.AddWithValue("$n", migration.Name);
            record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            record.Parameters.AddWithValue("$a", appVersion);
            record.ExecuteNonQuery();
        }

        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)};";
            pragma.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
