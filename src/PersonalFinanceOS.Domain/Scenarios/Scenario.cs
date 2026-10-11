namespace PersonalFinanceOS.Domain.Scenarios;

/// <summary>
/// A named what-if (spec §16): a set of changes laid over the forecast. A scenario never changes the ledger,
/// planned items or the wishlist; it only changes what the forecast shows next to the baseline.
/// </summary>
public sealed class Scenario
{
    public const int MaxNameLength = 100;
    public const int MaxNoteLength = 500;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private Scenario()
    {
    }

    public static Scenario Create(string name, string? note, DateTimeOffset now)
    {
        var scenario = new Scenario { Id = Guid.NewGuid(), CreatedAt = now };
        scenario.Change(name, note, now);
        return scenario;
    }

    public void Change(string name, string? note, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("scenario.name.required");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException("scenario.name.tooLong");
        }

        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (text?.Length > MaxNoteLength)
        {
            throw new DomainValidationException("scenario.note.tooLong");
        }

        Name = trimmed;
        Note = text;
        UpdatedAt = now;
    }
}

/// <summary>What one scenario change does. Persisted as text, so never rename members.</summary>
public enum ScenarioChangeKind
{
    /// <summary>One extra amount out (or in) on a day, for example a renovation cost.</summary>
    OneOff,

    /// <summary>An extra amount every month from a day on, for example a new loan payment.</summary>
    Monthly,

    /// <summary>A planned item (salary, rent, transfer to the joint account…) at another amount from a day on; zero stops it.</summary>
    ChangePlanned,

    /// <summary>A purchase on a day, from an account or by card, for example a wishlist item.</summary>
    Purchase,
}

/// <summary>One change in a <see cref="Scenario"/>. Amounts are always positive; <see cref="IsIncome"/> gives the direction.</summary>
public sealed class ScenarioChange
{
    public const int MaxNameLength = 100;

    public Guid Id { get; private set; }
    public Guid ScenarioId { get; private set; }
    public ScenarioChangeKind Kind { get; private set; }

    /// <summary>What the user calls it. Empty for <see cref="ScenarioChangeKind.ChangePlanned"/>: the planned item's name is used.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Money comes in rather than goes out (one-off and monthly changes only).</summary>
    public bool IsIncome { get; private set; }

    /// <summary>The amount; for <see cref="ScenarioChangeKind.ChangePlanned"/> the new amount of each occurrence (zero = stops).</summary>
    public Money Amount { get; private set; }

    /// <summary>The day it happens, or the first day it applies.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>The last day of a monthly change. Null = open-ended.</summary>
    public DateOnly? EndDate { get; private set; }

    /// <summary>The account the money comes from or goes to.</summary>
    public Guid? AccountId { get; private set; }

    /// <summary>A purchase by card: the statement pays it later.</summary>
    public Guid? CardId { get; private set; }

    /// <summary>The planned item that changes.</summary>
    public Guid? RuleId { get; private set; }

    /// <summary>The wishlist item a purchase is for.</summary>
    public Guid? WishlistItemId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    // For the persistence layer.
    private ScenarioChange()
    {
    }

    public static ScenarioChange OneOff(Guid scenarioId, string name, bool isIncome, Money amount, DateOnly date, Guid accountId, DateTimeOffset now) =>
        Create(scenarioId, ScenarioChangeKind.OneOff, RequireName(name), isIncome, Positive(amount), date, null, accountId, null, null, null, now);

    public static ScenarioChange Monthly(Guid scenarioId, string name, bool isIncome, Money amount, DateOnly from, DateOnly? until, Guid accountId, DateTimeOffset now)
    {
        if (until is { } end && end < from)
        {
            throw new DomainValidationException("recurring.endBeforeStart");
        }

        return Create(scenarioId, ScenarioChangeKind.Monthly, RequireName(name), isIncome, Positive(amount), from, until, accountId, null, null, null, now);
    }

    public static ScenarioChange ChangePlanned(Guid scenarioId, Guid ruleId, DateOnly from, Money newAmount, DateTimeOffset now)
    {
        if (newAmount.IsNegative)
        {
            throw new DomainValidationException("scenario.amount.negative");
        }

        return Create(scenarioId, ScenarioChangeKind.ChangePlanned, string.Empty, false, newAmount, from, null, null, null, ruleId, null, now);
    }

    /// <summary>A purchase from <paramref name="accountId"/> or by <paramref name="cardId"/>: exactly one of the two.</summary>
    public static ScenarioChange Purchase(Guid scenarioId, string name, Money price, DateOnly date, Guid? accountId, Guid? cardId, Guid? wishlistItemId, DateTimeOffset now)
    {
        if (accountId is null == cardId is null)
        {
            throw new DomainValidationException("scenario.payment.required");
        }

        return Create(scenarioId, ScenarioChangeKind.Purchase, RequireName(name), false, Positive(price), date, null, accountId, cardId, null, wishlistItemId, now);
    }

    private static ScenarioChange Create(
        Guid scenarioId,
        ScenarioChangeKind kind,
        string name,
        bool isIncome,
        Money amount,
        DateOnly date,
        DateOnly? endDate,
        Guid? accountId,
        Guid? cardId,
        Guid? ruleId,
        Guid? wishlistItemId,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            ScenarioId = scenarioId,
            Kind = kind,
            Name = name,
            IsIncome = isIncome,
            Amount = amount,
            Date = date,
            EndDate = endDate,
            AccountId = accountId,
            CardId = cardId,
            RuleId = ruleId,
            WishlistItemId = wishlistItemId,
            CreatedAt = now,
        };

    private static string RequireName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("scenario.change.name.required");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException("scenario.name.tooLong");
        }

        return trimmed;
    }

    private static Money Positive(Money amount) =>
        amount.IsPositive ? amount : throw new DomainValidationException("transaction.amount.zero");
}
