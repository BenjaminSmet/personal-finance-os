using Microsoft.Data.Sqlite;
using PersonalFinanceOS.Infrastructure;
using PersonalFinanceOS.Infrastructure.Database;
using PersonalFinanceOS.Infrastructure.Logging;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>An isolated data folder per test, deleted afterwards.</summary>
public sealed class TempDataFolder : IDisposable
{
    public TempDataFolder()
    {
        Paths = new AppDataPaths(Path.Combine(Path.GetTempPath(), "pfos-tests", Guid.NewGuid().ToString("N")));
    }

    public AppDataPaths Paths { get; }

    public SafeLog Log => new(Paths.LogsDirectory);

    public DatabaseInitializer Initializer(IMigrationSource? source = null) =>
        new(Paths, source ?? new EmbeddedMigrationSource(), Log, "test");

    public SqliteConnection Open() => new(new SqliteConnectionStringBuilder { DataSource = Paths.DatabaseFile, Pooling = false }.ToString());

    public long Scalar(string sql)
    {
        using var connection = Open();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Execute(string sql)
    {
        using var connection = Open();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Paths.Root))
            {
                Directory.Delete(Paths.Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The real migrations plus extra test steps appended after them.</summary>
public sealed class ExtendedMigrationSource(params string[] extraSql) : IMigrationSource
{
    public IReadOnlyList<Migration> GetMigrations()
    {
        var list = new EmbeddedMigrationSource().GetMigrations().ToList();
        foreach (var sql in extraSql)
        {
            list.Add(new Migration(list.Count + 1, "test_step", sql));
        }

        return list;
    }
}

/// <summary>Only the first <paramref name="count"/> real migrations: a database as an older app version left it.</summary>
public sealed class OlderMigrationSource(int count) : IMigrationSource
{
    public IReadOnlyList<Migration> GetMigrations() => new EmbeddedMigrationSource().GetMigrations().Take(count).ToList();
}
