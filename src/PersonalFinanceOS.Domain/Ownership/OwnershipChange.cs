namespace PersonalFinanceOS.Domain.Ownership;

/// <summary>
/// The user's share of an account or asset from a date on (spec §9: shares may change over time). The share
/// before the first change is kept as a change too, so earlier months keep the share they had.
/// </summary>
public sealed class OwnershipChange
{
    public Guid Id { get; private set; }
    public Guid? AccountId { get; private set; }
    public Guid? AssetId { get; private set; }
    public DateOnly EffectiveFrom { get; private set; }
    public Share Share { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private OwnershipChange()
    {
    }

    public Guid OwnerId => AccountId ?? AssetId!.Value;

    public static OwnershipChange ForAccount(Guid accountId, DateOnly from, Share share, DateTimeOffset now) => Create(accountId, null, from, share, now);

    public static OwnershipChange ForAsset(Guid assetId, DateOnly from, Share share, DateTimeOffset now) => Create(null, assetId, from, share, now);

    public void Change(Share share, DateTimeOffset now)
    {
        if (share.BasisPoints == 0)
        {
            throw new DomainValidationException("account.share.zero");
        }

        Share = share;
        UpdatedAt = now;
    }

    private static OwnershipChange Create(Guid? accountId, Guid? assetId, DateOnly from, Share share, DateTimeOffset now)
    {
        var change = new OwnershipChange
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            AssetId = assetId,
            EffectiveFrom = from,
            CreatedAt = now,
        };
        change.Change(share, now);
        return change;
    }
}

public static class OwnershipHistory
{
    /// <summary>
    /// The share on <paramref name="date"/>: the latest change from that date or before. Before the first change the
    /// earliest known share applies; without any change the owner's current share applies.
    /// </summary>
    public static Share ShareAt(IEnumerable<OwnershipChange> changes, Guid ownerId, Share current, DateOnly date)
    {
        var own = changes.Where(c => c.OwnerId == ownerId).OrderBy(c => c.EffectiveFrom).ToList();
        if (own.Count == 0)
        {
            return current;
        }

        return (own.LastOrDefault(c => c.EffectiveFrom <= date) ?? own[0]).Share;
    }
}
