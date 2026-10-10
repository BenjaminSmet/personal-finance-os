namespace PersonalFinanceOS.Domain.Categories;

/// <summary>Values are persisted as text, so never rename members.</summary>
public enum CategoryKind
{
    Expense,
    Income,
}

public enum CategoryStatus
{
    Active,
    Archived,
}

/// <summary>
/// A spending or income category (spec §13). Transfers never have a category, so they can never
/// appear in category spending. Archived categories stay on old transactions.
/// </summary>
public sealed class Category
{
    public const int MaxNameLength = 60;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public CategoryKind Kind { get; private set; }
    public int SortOrder { get; private set; }
    public CategoryStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == CategoryStatus.Active;

    // For the persistence layer.
    private Category()
    {
    }

    public static Category Create(string name, CategoryKind kind, int sortOrder, DateTimeOffset now)
    {
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            SortOrder = sortOrder,
            Status = CategoryStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        category.SetName(name);
        return category;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        SetName(name);
        UpdatedAt = now;
    }

    public void MoveTo(int sortOrder, DateTimeOffset now)
    {
        SortOrder = sortOrder;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        Status = CategoryStatus.Archived;
        UpdatedAt = now;
    }

    public void Reactivate(DateTimeOffset now)
    {
        Status = CategoryStatus.Active;
        UpdatedAt = now;
    }

    private void SetName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("category.name.required");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException("category.name.tooLong");
        }

        Name = trimmed;
    }
}
