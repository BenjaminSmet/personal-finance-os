namespace PersonalFinanceOS.Domain.Accounts;

/// <summary>Account kinds from spec §5. Values are persisted as text, so never rename members.</summary>
public enum AccountType
{
    PersonalCurrent,
    JointCurrent,
    Savings,
    CreditCard,
    Pension,
    Investment,
    Cash,
    Other,
}

public enum AccountStatus
{
    Active,
    Archived,
}

public static class AccountTypeRules
{
    /// <summary>
    /// Liability accounts carry a negative balance when money is owed.
    /// A VISA balance of -400 means €400 outstanding.
    /// </summary>
    public static bool IsLiability(this AccountType type) => type == AccountType.CreditCard;

    /// <summary>Spendable cash (current accounts and cash wallet). Savings/investments are shown separately (spec §13).</summary>
    public static bool IsLiquid(this AccountType type) =>
        type is AccountType.PersonalCurrent or AccountType.JointCurrent or AccountType.Cash;

    /// <summary>Values that are entered manually as dated valuations rather than derived from transactions (spec §17).</summary>
    public static bool IsManuallyValued(this AccountType type) =>
        type is AccountType.Pension or AccountType.Investment;
}
