namespace PersonalFinanceOS.Domain.Assets;

/// <summary>Kinds of things owned or owed outside the accounts (spec §17). Persisted as text, so never rename members.</summary>
public enum AssetKind
{
    Property,
    Vehicle,
    OtherAsset,
    Mortgage,
    Loan,
    OtherLiability,
}

public static class AssetKindRules
{
    /// <summary>A liability's valuation is the amount still owed; it lowers net worth.</summary>
    public static bool IsLiability(this AssetKind kind) => kind is AssetKind.Mortgage or AssetKind.Loan or AssetKind.OtherLiability;
}

public enum AssetStatus
{
    Active,
    Archived,
}

/// <summary>
/// A house, car, mortgage or other item that counts for net worth but has no transactions (spec §17).
/// Its value only comes from dated <see cref="Valuation"/>s, so history is kept, never overwritten.
/// Loan payments recorded as expenses never lower a liability automatically: the user enters the remaining
/// amount as a new valuation.
/// </summary>
public sealed class Asset
{
    public const int MaxNameLength = 100;
    public const int MaxNotesLength = 1000;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AssetKind Kind { get; private set; }

    /// <summary>The user's share today. Earlier shares are kept as ownership changes.</summary>
    public Share OwnershipShare { get; private set; }

    public AssetStatus Status { get; private set; }
    public string? Notes { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private Asset()
    {
    }

    public bool IsLiability => Kind.IsLiability();

    public static Asset Create(string name, AssetKind kind, Share ownershipShare, DateTimeOffset now, string? notes = null, int sortOrder = 0)
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = AssetStatus.Active,
            SortOrder = sortOrder,
            CreatedAt = now,
        };
        asset.Change(name, notes, now);
        asset.ChangeOwnershipShare(ownershipShare, now);
        return asset;
    }

    public void Change(string name, string? notes, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("asset.name.required");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException("asset.name.tooLong");
        }

        var note = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (note is { Length: > MaxNotesLength })
        {
            throw new DomainValidationException("asset.notes.tooLong");
        }

        Name = trimmed;
        Notes = note;
        UpdatedAt = now;
    }

    public void ChangeOwnershipShare(Share share, DateTimeOffset now)
    {
        if (share.BasisPoints == 0)
        {
            throw new DomainValidationException("account.share.zero");
        }

        OwnershipShare = share;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        Status = AssetStatus.Archived;
        UpdatedAt = now;
    }

    public void Reactivate(DateTimeOffset now)
    {
        Status = AssetStatus.Active;
        UpdatedAt = now;
    }
}
