namespace PersonalFinanceOS.Domain.Imports;

public enum ImportBatchStatus
{
    Active,

    /// <summary>The import was undone: its lines were voided. Kept so the history stays complete.</summary>
    Undone,
}

/// <summary>
/// One confirmed bank-file import into one account (spec §8). Only the file name is kept, never the
/// file itself. The counts describe what the user saw at confirmation.
/// </summary>
public sealed class ImportBatch
{
    public const int MaxFileNameLength = 255;

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public string FileName { get; private set; } = string.Empty;

    /// <summary>"csv" or "xlsx".</summary>
    public string Format { get; private set; } = string.Empty;

    public DateOnly? FirstDate { get; private set; }
    public DateOnly? LastDate { get; private set; }
    public int ImportedCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public int SkippedCount { get; private set; }
    public int InvalidCount { get; private set; }
    public ImportBatchStatus Status { get; private set; }
    public DateTimeOffset ImportedAt { get; private set; }
    public DateTimeOffset? UndoneAt { get; private set; }

    public bool IsActive => Status == ImportBatchStatus.Active;

    // For the persistence layer.
    private ImportBatch()
    {
    }

    public static ImportBatch Create(
        Guid accountId,
        string fileName,
        string format,
        DateOnly? firstDate,
        DateOnly? lastDate,
        int importedCount,
        int duplicateCount,
        int skippedCount,
        int invalidCount,
        DateTimeOffset now)
    {
        if (importedCount < 0 || duplicateCount < 0 || skippedCount < 0 || invalidCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(importedCount), "Counts cannot be negative.");
        }

        // Only the name: a full path could reveal folder names and is of no use later.
        var name = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        if (name.Length > MaxFileNameLength)
        {
            name = name[..MaxFileNameLength];
        }

        return new ImportBatch
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FileName = name,
            Format = format,
            FirstDate = firstDate,
            LastDate = lastDate,
            ImportedCount = importedCount,
            DuplicateCount = duplicateCount,
            SkippedCount = skippedCount,
            InvalidCount = invalidCount,
            Status = ImportBatchStatus.Active,
            ImportedAt = now,
        };
    }

    public void MarkUndone(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new DomainValidationException("import.alreadyUndone");
        }

        Status = ImportBatchStatus.Undone;
        UndoneAt = now;
    }
}

/// <summary>
/// An existing line that an import turned into one side of a transfer. Remembers what it was, so
/// undoing the import puts it back exactly (spec §8 "safe batch undo").
/// </summary>
public sealed class ImportTransferLink
{
    public Guid BatchId { get; private set; }
    public Guid TransactionId { get; private set; }
    public Transactions.TransactionType OriginalType { get; private set; }
    public Guid? OriginalCategoryId { get; private set; }

    // For the persistence layer.
    private ImportTransferLink()
    {
    }

    public static ImportTransferLink For(Guid batchId, Transactions.Transaction before) => new()
    {
        BatchId = batchId,
        TransactionId = before.Id,
        OriginalType = before.Type,
        OriginalCategoryId = before.CategoryId,
    };
}

/// <summary>
/// A remembered column mapping for one bank-export layout (spec §8 step 4), found again by the
/// file's header line. The mapping itself is stored as JSON owned by the application layer.
/// </summary>
public sealed class ImportProfile
{
    public Guid Id { get; private set; }

    /// <summary>Normalised header line, e.g. "datum|bedrag|omschrijving". Unique.</summary>
    public string HeaderKey { get; private set; } = string.Empty;

    public string MappingJson { get; private set; } = string.Empty;
    public int UseCount { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private ImportProfile()
    {
    }

    public static ImportProfile Create(string headerKey, string mappingJson, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(headerKey))
        {
            throw new ArgumentException("A header key is required.", nameof(headerKey));
        }

        var profile = new ImportProfile { Id = Guid.NewGuid(), HeaderKey = headerKey };
        profile.Update(mappingJson, now);
        return profile;
    }

    public void Update(string mappingJson, DateTimeOffset now)
    {
        MappingJson = mappingJson;
        UseCount++;
        UpdatedAt = now;
    }
}
