namespace PersonalFinanceOS.Domain.Assets;

/// <summary>
/// A manually entered value on a date (spec §17): for a pension or investment account, or for an <see cref="Asset"/>.
/// For a liability the value is the amount still owed. Every date keeps its own value, so entering a new value
/// never overwrites the only one; entering a value again for the same date corrects that date.
/// </summary>
public sealed class Valuation
{
    public const int MaxNoteLength = 500;

    public Guid Id { get; private set; }

    /// <summary>Set for a pension or investment account; otherwise <see cref="AssetId"/> is set.</summary>
    public Guid? AccountId { get; private set; }

    public Guid? AssetId { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>Never negative: a liability's value is what is owed.</summary>
    public Money Value { get; private set; }

    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private Valuation()
    {
    }

    public Guid OwnerId => AccountId ?? AssetId!.Value;

    public static Valuation ForAccount(Guid accountId, DateOnly date, Money value, string? note, DateTimeOffset now) =>
        Create(accountId, null, date, value, note, now);

    public static Valuation ForAsset(Guid assetId, DateOnly date, Money value, string? note, DateTimeOffset now) =>
        Create(null, assetId, date, value, note, now);

    public void Change(Money value, string? note, DateTimeOffset now)
    {
        if (value.IsNegative)
        {
            throw new DomainValidationException("valuation.value.negative");
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
        {
            throw new DomainValidationException("valuation.note.tooLong");
        }

        Value = value;
        Note = trimmed;
        UpdatedAt = now;
    }

    /// <summary>The latest valuation on or before <paramref name="date"/>, or null when there is none yet.</summary>
    public static Valuation? LatestOn(IEnumerable<Valuation> valuations, Guid ownerId, DateOnly date) =>
        valuations.Where(v => v.OwnerId == ownerId && v.Date <= date).MaxBy(v => v.Date);

    private static Valuation Create(Guid? accountId, Guid? assetId, DateOnly date, Money value, string? note, DateTimeOffset now)
    {
        var valuation = new Valuation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            AssetId = assetId,
            Date = date,
            CreatedAt = now,
        };
        valuation.Change(value, note, now);
        return valuation;
    }
}
