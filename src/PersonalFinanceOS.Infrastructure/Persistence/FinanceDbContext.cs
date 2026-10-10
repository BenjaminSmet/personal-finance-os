using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;
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
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<PayeeRule> PayeeRules => Set<PayeeRule>();
    public DbSet<ReconciliationRecord> Reconciliations => Set<ReconciliationRecord>();

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
        var optionalGuid = new ValueConverter<Guid?, string?>(
            g => g.HasValue ? g.Value.ToString("D") : null,
            s => s == null ? null : Guid.Parse(s));

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

        modelBuilder.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.HasKey(c => c.Id);
            e.Ignore(c => c.IsActive);
            e.Property(c => c.Id).HasColumnName("id").HasConversion(guid);
            e.Property(c => c.Name).HasColumnName("name").HasMaxLength(Category.MaxNameLength);
            e.Property(c => c.Kind).HasColumnName("kind").HasConversion<string>();
            e.Property(c => c.SortOrder).HasColumnName("sort_order");
            e.Property(c => c.Status).HasColumnName("status").HasConversion<string>();
            e.Property(c => c.CreatedAt).HasColumnName("created_at").HasConversion(timestamp);
            e.Property(c => c.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.ToTable("transactions");
            e.HasKey(t => t.Id);
            e.Ignore(t => t.IsActive);
            e.Ignore(t => t.IsTransfer);
            e.Property(t => t.Id).HasColumnName("id").HasConversion(guid);
            e.Property(t => t.AccountId).HasColumnName("account_id").HasConversion(guid);
            e.Property(t => t.Date).HasColumnName("date").HasConversion(date);
            e.Property(t => t.Amount).HasColumnName("amount_cents").HasConversion(money);
            e.Property(t => t.Type).HasColumnName("type").HasConversion<string>();
            e.Property(t => t.Description).HasColumnName("description").HasMaxLength(Transaction.MaxDescriptionLength);
            e.Property(t => t.CategoryId).HasColumnName("category_id").HasConversion(optionalGuid);
            e.Property(t => t.Note).HasColumnName("note");
            e.Property(t => t.Source).HasColumnName("source").HasConversion<string>();
            e.Property(t => t.TransferId).HasColumnName("transfer_id").HasConversion(optionalGuid);
            e.Property(t => t.Status).HasColumnName("status").HasConversion<string>();
            e.Property(t => t.CreatedAt).HasColumnName("created_at").HasConversion(timestamp);
            e.Property(t => t.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
            e.HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PayeeRule>(e =>
        {
            e.ToTable("payee_rules");
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.PayeeKey).IsUnique();
            e.Property(r => r.Id).HasColumnName("id").HasConversion(guid);
            e.Property(r => r.PayeeKey).HasColumnName("payee_key");
            e.Property(r => r.Type).HasColumnName("type").HasConversion<string>();
            e.Property(r => r.CategoryId).HasColumnName("category_id").HasConversion(optionalGuid);
            e.Property(r => r.AccountId).HasColumnName("account_id").HasConversion(optionalGuid);
            e.Property(r => r.UseCount).HasColumnName("use_count");
            e.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
            e.HasOne<Category>().WithMany().HasForeignKey(r => r.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReconciliationRecord>(e =>
        {
            e.ToTable("reconciliations");
            e.HasKey(r => r.Id);
            e.Ignore(r => r.Difference);
            e.Ignore(r => r.IsMatched);
            e.Property(r => r.Id).HasColumnName("id").HasConversion(guid);
            e.Property(r => r.AccountId).HasColumnName("account_id").HasConversion(guid);
            e.Property(r => r.StatementDate).HasColumnName("statement_date").HasConversion(date);
            e.Property(r => r.StatementBalance).HasColumnName("statement_balance_cents").HasConversion(money);
            e.Property(r => r.CalculatedBalance).HasColumnName("calculated_balance_cents").HasConversion(money);
            e.Property(r => r.AdjustmentTransactionId).HasColumnName("adjustment_transaction_id").HasConversion(optionalGuid);
            e.Property(r => r.Note).HasColumnName("note");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasConversion(timestamp);
            e.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Transaction>().WithMany().HasForeignKey(r => r.AdjustmentTransactionId).OnDelete(DeleteBehavior.Restrict);
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
