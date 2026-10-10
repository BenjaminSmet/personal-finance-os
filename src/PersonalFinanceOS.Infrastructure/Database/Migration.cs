using System.Reflection;
using System.Text.RegularExpressions;

namespace PersonalFinanceOS.Infrastructure.Database;

/// <summary>One forward-only schema step. Versions are consecutive integers starting at 1.</summary>
public sealed record Migration(int Version, string Name, string Sql);

public interface IMigrationSource
{
    IReadOnlyList<Migration> GetMigrations();
}

/// <summary>
/// Loads migrations from embedded SQL files named "NNNN_description.sql".
/// Shipped migrations are immutable: fix mistakes with a new migration, never by editing an old one.
/// </summary>
public sealed partial class EmbeddedMigrationSource : IMigrationSource
{
    private readonly Assembly _assembly = typeof(EmbeddedMigrationSource).Assembly;

    public IReadOnlyList<Migration> GetMigrations()
    {
        var migrations = new List<Migration>();
        foreach (var resource in _assembly.GetManifestResourceNames())
        {
            var match = MigrationName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            using var stream = _assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing migration resource {resource}.");
            using var reader = new StreamReader(stream);
            migrations.Add(new Migration(int.Parse(match.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups["n"].Value, reader.ReadToEnd()));
        }

        return Validate(migrations);
    }

    internal static IReadOnlyList<Migration> Validate(List<Migration> migrations)
    {
        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException("Schema migrations must be numbered 1, 2, 3, … without gaps or duplicates.");
            }
        }

        return migrations;
    }

    [GeneratedRegex(@"Migrations\.(?<v>\d{4})_(?<n>[A-Za-z0-9_]+)\.sql$")]
    private static partial Regex MigrationName();
}
