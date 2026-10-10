namespace PersonalFinanceOS.Domain.Accounts;

/// <summary>
/// A financial account (spec §5). Balances are never stored here: the current balance is always
/// derived from the opening balance plus transactions, so there is no redundant authoritative total.
/// </summary>
public sealed class Account
{
    public const string SupportedCurrency = "EUR";
    public const int MaxNameLength = 100;
    public const int MaxNotesLength = 1000;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AccountType Type { get; private set; }
    public string Currency { get; private set; } = SupportedCurrency;

    /// <summary>
    /// The user's default ownership share. Joint accounts usually have less than 100%.
    /// Effective-dated share history is added in phase 6; this is the current default.
    /// </summary>
    public Share OwnershipShare { get; private set; }

    public DateOnly OpeningDate { get; private set; }

    /// <summary>Balance at the start of <see cref="OpeningDate"/>. Negative for an outstanding credit-card balance.</summary>
    public Money OpeningBalance { get; private set; }

    public AccountStatus Status { get; private set; }
    public string? Notes { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private Account()
    {
    }

    public static Account Create(
        string name,
        AccountType type,
        DateOnly openingDate,
        Money openingBalance,
        Share ownershipShare,
        DateTimeOffset now,
        string? notes = null,
        int sortOrder = 0)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Type = type,
            Currency = SupportedCurrency,
            Status = AccountStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = sortOrder,
        };

        account.SetName(name);
        account.SetOpening(openingDate, openingBalance);
        account.SetOwnershipShare(ownershipShare);
        account.SetNotes(notes);
        return account;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        SetName(name);
        UpdatedAt = now;
    }

    public void ChangeOwnershipShare(Share share, DateTimeOffset now)
    {
        SetOwnershipShare(share);
        UpdatedAt = now;
    }

    public void ChangeNotes(string? notes, DateTimeOffset now)
    {
        SetNotes(notes);
        UpdatedAt = now;
    }

    public void MoveTo(int sortOrder, DateTimeOffset now)
    {
        SortOrder = sortOrder;
        UpdatedAt = now;
    }

    /// <summary>
    /// Changing the opening balance changes every derived balance, so callers must surface this
    /// explicitly to the user (spec §5: historical imports must not silently change opening balances).
    /// </summary>
    public void CorrectOpening(DateOnly openingDate, Money openingBalance, DateTimeOffset now)
    {
        SetOpening(openingDate, openingBalance);
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        Status = AccountStatus.Archived;
        UpdatedAt = now;
    }

    public void Reactivate(DateTimeOffset now)
    {
        Status = AccountStatus.Active;
        UpdatedAt = now;
    }

    /// <summary>The user's part of a balance on this account (spec §9, §17).</summary>
    public Money PersonalPart(Money fullBalance) => OwnershipShare.ApplyTo(fullBalance);

    private void SetName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("account.name.required");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException("account.name.tooLong");
        }

        Name = trimmed;
    }

    private void SetOpening(DateOnly openingDate, Money openingBalance)
    {
        if (openingBalance.IsPositive && Type.IsLiability())
        {
            // A credit card cannot start "in credit" in normal use; a negative value means money owed.
            throw new DomainValidationException("account.opening.creditCardPositive");
        }

        OpeningDate = openingDate;
        OpeningBalance = openingBalance;
    }

    private void SetOwnershipShare(Share share)
    {
        if (share.BasisPoints == 0)
        {
            throw new DomainValidationException("account.share.zero");
        }

        OwnershipShare = share;
    }

    private void SetNotes(string? notes)
    {
        var trimmed = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (trimmed is { Length: > MaxNotesLength })
        {
            throw new DomainValidationException("account.notes.tooLong");
        }

        Notes = trimmed;
    }
}
