namespace PersonalFinanceOS.Domain.Wishlist;

public enum WishlistPriority
{
    High,
    Normal,
    Low,
}

/// <summary>
/// What the user decided about an item. "Affordable" from spec §14 is not stored: it is calculated from the forecast
/// every time, so it can never be out of date. Persisted as text, so never rename members.
/// </summary>
public enum WishlistStatus
{
    Desired,
    Saving,
    Postponed,
    Purchased,
    Cancelled,
}

/// <summary>Something the user wants to buy (spec §14). Buying it records at most one transaction.</summary>
public sealed class WishlistItem
{
    public const int MaxNameLength = 100;
    public const int MaxUrlLength = 500;
    public const int MaxNoteLength = 1000;
    public const int MaxAlternativeLength = 200;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Money ExpectedPrice { get; private set; }
    public string? Url { get; private set; }
    public string? Note { get; private set; }
    public WishlistPriority Priority { get; private set; }
    public DateOnly? TargetDate { get; private set; }

    /// <summary>What the user plans to put aside for it each month. Informational: it never moves money.</summary>
    public Money? MonthlySaving { get; private set; }

    /// <summary>A cheaper or different option, as free text.</summary>
    public string? Alternative { get; private set; }

    public WishlistStatus Status { get; private set; }
    public DateOnly? PurchasedOn { get; private set; }

    /// <summary>The expense recorded when it was bought, when the user let the app record one.</summary>
    public Guid? PurchaseTransactionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private WishlistItem()
    {
    }

    public bool IsOpen => Status is WishlistStatus.Desired or WishlistStatus.Saving or WishlistStatus.Postponed;

    public static WishlistItem Create(
        string name, Money expectedPrice, WishlistPriority priority, DateOnly? targetDate, Money? monthlySaving,
        string? url, string? note, string? alternative, DateTimeOffset now)
    {
        var item = new WishlistItem { Id = Guid.NewGuid(), Status = WishlistStatus.Desired, CreatedAt = now };
        item.Change(name, expectedPrice, priority, targetDate, monthlySaving, url, note, alternative, now);
        return item;
    }

    public void Change(
        string name, Money expectedPrice, WishlistPriority priority, DateOnly? targetDate, Money? monthlySaving,
        string? url, string? note, string? alternative, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("wishlist.name.required");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException("wishlist.name.tooLong");
        }

        if (!expectedPrice.IsPositive)
        {
            throw new DomainValidationException("wishlist.price.notPositive");
        }

        if (monthlySaving is { } saving && !saving.IsPositive)
        {
            throw new DomainValidationException("wishlist.saving.notPositive");
        }

        Name = trimmed;
        ExpectedPrice = expectedPrice;
        Priority = priority;
        TargetDate = targetDate;
        MonthlySaving = monthlySaving;
        Url = Text(url, MaxUrlLength);
        Note = Text(note, MaxNoteLength);
        Alternative = Text(alternative, MaxAlternativeLength);
        UpdatedAt = now;
    }

    /// <summary>Desired, saving, postponed or cancelled. Buying goes through <see cref="MarkPurchased"/>.</summary>
    public void ChangeStatus(WishlistStatus status, DateTimeOffset now)
    {
        if (status == WishlistStatus.Purchased)
        {
            throw new DomainValidationException("wishlist.status.invalid");
        }

        Status = status;
        PurchasedOn = null;
        PurchaseTransactionId = null;
        UpdatedAt = now;
    }

    public void MarkPurchased(DateOnly date, Guid? transactionId, DateTimeOffset now)
    {
        Status = WishlistStatus.Purchased;
        PurchasedOn = date;
        PurchaseTransactionId = transactionId;
        UpdatedAt = now;
    }

    private static string? Text(string? value, int max)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed is { Length: var length } && length > max)
        {
            throw new DomainValidationException("wishlist.text.tooLong");
        }

        return trimmed;
    }
}
