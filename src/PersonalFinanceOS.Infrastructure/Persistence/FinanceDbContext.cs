using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Cards;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Imports;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Planning;
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
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportTransferLink> ImportTransferLinks => Set<ImportTransferLink>();
    public DbSet<ImportProfile> ImportProfiles => Set<ImportProfile>();
    public DbSet<CreditCardProfile> CreditCardProfiles => Set<CreditCardProfile>();
    public DbSet<RecurringRule> RecurringRules => Set<RecurringRule>();
    public DbSet<ScheduledItem> ScheduledItems => Set<ScheduledItem>();

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
            e.Property(t => t.ImportBatchId).HasColumnName("import_batch_id").HasConversion(optionalGuid);
            e.Property(t => t.BankReference).HasColumnName("bank_reference").HasMaxLength(Transaction.MaxBankReferenceLength);
            e.Property(t => t.CreatedAt).HasColumnName("created_at").HasConversion(timestamp);
            e.Property(t => t.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
            e.HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(t => t.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
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

        var optionalDate = new ValueConverter<DateOnly?, string?>(
            d => d.HasValue ? d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            s => s == null ? null : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        var optionalTimestamp = new ValueConverter<DateTimeOffset?, string?>(
            t => t.HasValue ? t.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture) : null,
            s => s == null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

        modelBuilder.Entity<ImportBatch>(e =>
        {
            e.ToTable("import_batches");
            e.HasKey(b => b.Id);
            e.Ignore(b => b.IsActive);
            e.Property(b => b.Id).HasColumnName("id").HasConversion(guid);
            e.Property(b => b.AccountId).HasColumnName("account_id").HasConversion(guid);
            e.Property(b => b.FileName).HasColumnName("file_name").HasMaxLength(ImportBatch.MaxFileNameLength);
            e.Property(b => b.Format).HasColumnName("format");
            e.Property(b => b.FirstDate).HasColumnName("first_date").HasConversion(optionalDate);
            e.Property(b => b.LastDate).HasColumnName("last_date").HasConversion(optionalDate);
            e.Property(b => b.ImportedCount).HasColumnName("imported_count");
            e.Property(b => b.DuplicateCount).HasColumnName("duplicate_count");
            e.Property(b => b.SkippedCount).HasColumnName("skipped_count");
            e.Property(b => b.InvalidCount).HasColumnName("invalid_count");
            e.Property(b => b.Status).HasColumnName("status").HasConversion<string>();
            e.Property(b => b.ImportedAt).HasColumnName("imported_at").HasConversion(timestamp);
            e.Property(b => b.UndoneAt).HasColumnName("undone_at").HasConversion(optionalTimestamp);
            e.HasOne<Account>().WithMany().HasForeignKey(b => b.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportTransferLink>(e =>
        {
            e.ToTable("import_transfer_links");
            e.HasKey(l => new { l.BatchId, l.TransactionId });
            e.Property(l => l.BatchId).HasColumnName("batch_id").HasConversion(guid);
            e.Property(l => l.TransactionId).HasColumnName("transaction_id").HasConversion(guid);
            e.Property(l => l.OriginalType).HasColumnName("original_type").HasConversion<string>();
            e.Property(l => l.OriginalCategoryId).HasColumnName("original_category_id").HasConversion(optionalGuid);
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(l => l.BatchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Transaction>().WithMany().HasForeignKey(l => l.TransactionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(l => l.OriginalCategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportProfile>(e =>
        {
            e.ToTable("import_profiles");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.HeaderKey).IsUnique();
            e.Property(p => p.Id).HasColumnName("id").HasConversion(guid);
            e.Property(p => p.HeaderKey).HasColumnName("header_key");
            e.Property(p => p.MappingJson).HasColumnName("mapping_json");
            e.Property(p => p.UseCount).HasColumnName("use_count");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
        });

        modelBuilder.Entity<CreditCardProfile>(e =>
        {
            e.ToTable("credit_card_profiles");
            e.HasKey(p => p.AccountId);
            e.Property(p => p.AccountId).HasColumnName("account_id").HasConversion(guid).ValueGeneratedNever();
            e.Property(p => p.CreditLimit).HasColumnName("credit_limit_cents").HasConversion(money);
            e.Property(p => p.StatementStartDay).HasColumnName("statement_start_day");
            e.Property(p => p.SettlementDay).HasColumnName("settlement_day");
            e.Property(p => p.SettlementAccountId).HasColumnName("settlement_account_id").HasConversion(optionalGuid);
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
            e.HasOne<Account>().WithOne().HasForeignKey<CreditCardProfile>(p => p.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(p => p.SettlementAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecurringRule>(e =>
        {
            e.ToTable("recurring_rules");
            e.HasKey(r => r.Id);
            e.Ignore(r => r.IsActive);
            e.Property(r => r.Id).HasColumnName("id").HasConversion(guid);
            e.Property(r => r.Name).HasColumnName("name").HasMaxLength(RecurringRule.MaxNameLength);
            e.Property(r => r.Kind).HasColumnName("kind").HasConversion<string>();
            e.Property(r => r.AccountId).HasColumnName("account_id").HasConversion(guid);
            e.Property(r => r.ToAccountId).HasColumnName("to_account_id").HasConversion(optionalGuid);
            e.Property(r => r.CategoryId).HasColumnName("category_id").HasConversion(optionalGuid);
            e.Property(r => r.Amount).HasColumnName("amount_cents").HasConversion(money);
            e.Property(r => r.Frequency).HasColumnName("frequency").HasConversion<string>();
            e.Property(r => r.StartDate).HasColumnName("start_date").HasConversion(date);
            e.Property(r => r.EndDate).HasColumnName("end_date").HasConversion(optionalDate);
            e.Property(r => r.Status).HasColumnName("status").HasConversion<string>();
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasConversion(timestamp);
            e.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
            e.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(r => r.ToAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(r => r.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ScheduledItem>(e =>
        {
            e.ToTable("scheduled_items");
            e.HasKey(i => i.Id);
            e.HasIndex(i => new { i.RuleId, i.OccurrenceDate }).IsUnique();
            e.Property(i => i.Id).HasColumnName("id").HasConversion(guid);
            e.Property(i => i.RuleId).HasColumnName("rule_id").HasConversion(guid);
            e.Property(i => i.OccurrenceDate).HasColumnName("occurrence_date").HasConversion(date);
            e.Property(i => i.Status).HasColumnName("status").HasConversion<string>();
            e.Property(i => i.PlannedDate).HasColumnName("planned_date").HasConversion(optionalDate);
            e.Property(i => i.TransactionId).HasColumnName("transaction_id").HasConversion(optionalGuid);
            e.Property(i => i.CreatedAt).HasColumnName("created_at").HasConversion(timestamp);
            e.Property(i => i.UpdatedAt).HasColumnName("updated_at").HasConversion(timestamp);
            e.HasOne<RecurringRule>().WithMany().HasForeignKey(i => i.RuleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Transaction>().WithMany().HasForeignKey(i => i.TransactionId).OnDelete(DeleteBehavior.Restrict);
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
