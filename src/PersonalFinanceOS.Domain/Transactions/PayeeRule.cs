using System.Globalization;
using System.Text;

namespace PersonalFinanceOS.Domain.Transactions;

/// <summary>
/// A remembered choice for a payee, learned locally from confirmed entries (spec §7 smart defaults):
/// e.g. "spotify" → Expense, Subscriptions, VISA. A suggestion only; the user's correction at
/// confirmation time is what gets learned next, so suggestions never override corrections.
/// </summary>
public sealed class PayeeRule
{
    public Guid Id { get; private set; }

    /// <summary>Normalised payee text (see <see cref="Normalize"/>). Unique.</summary>
    public string PayeeKey { get; private set; } = string.Empty;

    public TransactionType Type { get; private set; }
    public Guid? CategoryId { get; private set; }
    public Guid? AccountId { get; private set; }
    public int UseCount { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // For the persistence layer.
    private PayeeRule()
    {
    }

    public static PayeeRule Learn(string payee, TransactionType type, Guid? categoryId, Guid? accountId, DateTimeOffset now)
    {
        var key = Normalize(payee);
        if (key.Length == 0)
        {
            throw new ArgumentException("A payee is required.", nameof(payee));
        }

        var rule = new PayeeRule { Id = Guid.NewGuid(), PayeeKey = key };
        rule.Update(type, categoryId, accountId, now);
        return rule;
    }

    public void Update(TransactionType type, Guid? categoryId, Guid? accountId, DateTimeOffset now)
    {
        if (!type.IsIncomeOrSpending())
        {
            throw new ArgumentException("Rules only describe income, expenses and refunds.", nameof(type));
        }

        Type = type;
        CategoryId = categoryId;
        AccountId = accountId;
        UseCount++;
        UpdatedAt = now;
    }

    /// <summary>Lower case, accents kept, inner whitespace collapsed: "  Spotify   AB " → "spotify ab".</summary>
    public static string Normalize(string? payee)
    {
        if (string.IsNullOrWhiteSpace(payee))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(payee.Length);
        var space = false;
        foreach (var c in payee.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
