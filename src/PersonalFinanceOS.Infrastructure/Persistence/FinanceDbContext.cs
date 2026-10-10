using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Infrastructure.Database;

namespace PersonalFinanceOS.Infrastructure.Persistence;

/// <summary>
/// EF Core is used for queries and saving. The schema itself is owned by the SQL migrations in
/// Database/Migrations, applied by <see cref="DatabaseInitializer"/>; EnsureCreated/EF migrations are
/// never used. A test verifies that this model matches the migrated schema.
/// </summary>
public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AppSettingRow> AppSettings => Set<AppSettingRow>();
    public DbSet<AuditLogRow> AuditLog => Set<AuditLogRow>();

    public static DbContextOptions<FinanceDbContext> OptionsFor(string databaseFile) =>
        new DbContextOptionsBuilder<FinanceDbContext>()
            .UseSqlite(SqliteTools.ConnectionString(databaseFile, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite))
            .Options;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var money = new ValueConverter<Money, long>(m => m.Cents, c => Money.FromCents(c));
        var share = new ValueConverter<Share, int>(s => s.BasisPoints, bp => new Share(bp));
        var date = new ValueConverter<DateOnly, string>(
            d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        var timestamp = new ValueConverter<DateTimeOffset, string>(
            t => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
            s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        var guid = new ValueConverter<Guid, string>(g => g.ToString("D"), s => Guid.Parse(s));

        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id").HasConversion(guid);
            e.Property(a => a.Name).HasColumnName("name").HasMaxLength(Account.MaxNameLength);
            e.Property(a => a.Type).HasColumnName("type").HasConversion<string>();
            e.Property(a => a.Currency).HasColumnName("currency");
            e.Property(a => a.OwnershipShare).HasColumnName("ownership_share_bp").HasConversion(share);
            e.Property(a => a.OpeningDate).HasColumnName("opening_date").HasConversion(date);
            e.Property(a => a.OpeningBalance).HasColumnName("opening_balance_cents").HasConversion(money);
            e.Property(a => a.Status).HasColumnName("status").HasConversion<string>();
            e.Property(a => a.Notes).HasColumnName("notes");
            e.Property(a => a.SortOrder).HasColumnName("sort_order");
            e.Property(a => a.CreatedAt).HasColumnName("created_at").HasConversion(timestamp);
            e.Property(a => a.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
        });

        modelBuilder.Entity<AppSettingRow>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasColumnName("key");
            e.Property(s => s.Value).HasColumnName("value");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
        });

        modelBuilder.Entity<AuditLogRow>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(a => a.OccurredAt).HasColumnName("occurred_at").HasConversion(timestamp);
            e.Property(a => a.Entity).HasColumnName("entity");
            e.Property(a => a.EntityId).HasColumnName("entity_id");
            e.Property(a => a.Action).HasColumnName("action");
        });
    }
}

public sealed class AppSettingRow
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AuditLogRow
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Entity { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
}
