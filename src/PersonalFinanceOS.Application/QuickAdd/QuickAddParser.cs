using System.Globalization;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.QuickAdd;

/// <summary>Reasons to show the preview instead of saving straight away (spec §7).</summary>
public enum QuickAddIssue
{
    AmountMissing,
    AmountAmbiguous,
    SeveralAmounts,
    NoAccount,
    DestinationUnknown,
    SameAccount,
    FutureDate,
}

/// <summary>
/// The app's reading of a Quick Add line. Every field is a suggestion the user can change before
/// confirming; nothing is posted until then. <see cref="Amount"/> is positive.
/// </summary>
public sealed record QuickAddDraft(
    string Input,
    TransactionType Type,
    Money Amount,
    string Description,
    DateOnly Date,
    Guid? AccountId,
    Guid? DestinationAccountId,
    Guid? CategoryId,
    IReadOnlyList<QuickAddIssue> Issues)
{
    public bool IsTransfer => Type == TransactionType.Transfer;

    public bool IsComplete =>
        Amount.IsPositive && AccountId is not null &&
        (!IsTransfer || (DestinationAccountId is not null && DestinationAccountId != AccountId));

    /// <summary>Clear entries are confirmed with Enter; anything uncertain is shown for review first.</summary>
    public bool NeedsReview => Issues.Count > 0 || !IsComplete;
}

/// <summary>Everything the parser may use. Accounts and categories are the active ones.</summary>
public sealed record QuickAddContext(
    IReadOnlyList<Account> Accounts,
    IReadOnlyList<Category> Categories,
    IReadOnlyList<PayeeRule> Rules,
    Guid? LastUsedAccountId,
    DateOnly Today);

/// <summary>
/// Reads lines such as "Supermarkt 42,80", "Loon 2400", "Spotify 17,99 met visa", "Koffie 3,20 gisteren" or
/// "Transfer 600 naar gezamenlijke rekening" in Dutch or English. Runs locally and deterministically;
/// the text never leaves the computer (spec §7).
/// </summary>
public static class QuickAddParser
{
    private static readonly HashSet<string> TransferWords = ["transfer", "overschrijving", "overboeking", "overzetten", "overschrijven"];
    private static readonly HashSet<string> ToWords = ["naar", "to"];
    private static readonly HashSet<string> FromWords = ["van", "from"];
    private static readonly HashSet<string> WithWords = ["met", "with", "via"];
    private static readonly HashSet<string> IncomeWords = ["loon", "salaris", "salary", "wage", "wages", "inkomen", "income"];
    private static readonly HashSet<string> RefundWords = ["terugbetaling", "refund", "retour", "terugstorting"];
    private static readonly HashSet<string> CurrencyWords = ["€", "eur", "euro"];
    private static readonly HashSet<string> CardAliases = ["visa", "kredietkaart", "creditcard"];
    private static readonly HashSet<string> CashAliases = ["cash"];

    private sealed class Token(string text)
    {
        public string Text { get; } = text;
        public string Lower { get; } = text.ToLowerInvariant();
        public bool Used { get; set; }
    }

    public static QuickAddDraft Parse(string? input, QuickAddContext context)
    {
        var text = input?.Trim() ?? string.Empty;
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => new Token(t)).ToList();
        var issues = new List<QuickAddIssue>();

        // 1. Account phrases first ("naar spaarrekening 1"), so numbers in account names are not read as amounts.
        var destination = TakeAccountAfter(tokens, ToWords, context.Accounts);
        var source = TakeAccountAfter(tokens, FromWords, context.Accounts) ?? TakeAccountAfter(tokens, WithWords, context.Accounts);
        source ??= TakeAlias(tokens, context.Accounts);

        var isTransfer = destination is not null;
        foreach (var token in tokens.Where(t => !t.Used && TransferWords.Contains(t.Lower)))
        {
            token.Used = true;
            isTransfer = true;
        }

        // 2. Amount: the last number that parses. Several numbers are flagged for review.
        var amount = Money.Zero;
        var plusSign = false;
        var amountTokens = tokens.Where(t => !t.Used && LooksLikeAmount(t.Text)).ToList();
        if (amountTokens.Count == 0)
        {
            issues.Add(QuickAddIssue.AmountMissing);
        }
        else
        {
            var chosen = amountTokens[^1];
            var parsed = AmountParser.Parse(StripCurrency(chosen.Text));
            chosen.Used = true;
            amount = parsed.Value.Abs();
            plusSign = chosen.Text.TrimStart('€').StartsWith('+');
            if (parsed.IsAmbiguous)
            {
                issues.Add(QuickAddIssue.AmountAmbiguous);
            }

            if (amountTokens.Count > 1)
            {
                issues.Add(QuickAddIssue.SeveralAmounts);
            }

            if (amount.IsZero)
            {
                issues.Add(QuickAddIssue.AmountMissing);
            }
        }

        // 3. Date words; today by default.
        var date = context.Today;
        foreach (var token in tokens.Where(t => !t.Used))
        {
            if (TryReadDate(token.Lower, context.Today, out var read))
            {
                date = read;
                token.Used = true;
            }
        }

        if (date > context.Today)
        {
            issues.Add(QuickAddIssue.FutureDate);
        }

        foreach (var token in tokens.Where(t => !t.Used && CurrencyWords.Contains(t.Lower)))
        {
            token.Used = true;
        }

        // 4. What is left is the payee/description.
        var description = string.Join(' ', tokens.Where(t => !t.Used).Select(t => t.Text));
        var rule = context.Rules.FirstOrDefault(r => r.PayeeKey == PayeeRule.Normalize(description) && description.Length > 0);
        var words = tokens.Select(t => t.Lower).ToHashSet();

        TransactionType type;
        if (isTransfer)
        {
            type = TransactionType.Transfer;
        }
        else if (words.Overlaps(RefundWords))
        {
            type = TransactionType.Refund;
        }
        else if (words.Overlaps(IncomeWords) || plusSign)
        {
            type = TransactionType.Income;
        }
        else
        {
            type = rule?.Type ?? TransactionType.Expense;
        }

        Guid? accountId;
        Guid? destinationId = null;
        if (type == TransactionType.Transfer)
        {
            destinationId = destination?.Id;
            if (destinationId is null)
            {
                issues.Add(QuickAddIssue.DestinationUnknown);
            }

            accountId = source?.Id ?? DefaultAccount(context, exclude: destinationId, allowLiability: false);
            if (accountId is not null && accountId == destinationId)
            {
                issues.Add(QuickAddIssue.SameAccount);
            }
        }
        else
        {
            var ruleAccount = rule?.AccountId is { } ra && context.Accounts.Any(a => a.Id == ra) ? ra : (Guid?)null;
            accountId = source?.Id ?? ruleAccount ?? DefaultAccount(context, exclude: null, allowLiability: type != TransactionType.Income);
        }

        if (accountId is null)
        {
            issues.Add(QuickAddIssue.NoAccount);
        }

        Guid? categoryId = null;
        if (rule?.CategoryId is { } rc && type.CategoryKind() is { } kind &&
            context.Categories.Any(c => c.Id == rc && c.Kind == kind))
        {
            categoryId = rc;
        }

        return new QuickAddDraft(text, type, amount, description, date, accountId, destinationId, categoryId, issues.Distinct().ToList());
    }

    /// <summary>Remembered account if it still exists; else the personal current account; else the first liquid one.</summary>
    private static Guid? DefaultAccount(QuickAddContext context, Guid? exclude, bool allowLiability)
    {
        bool Allowed(Account a) => a.Id != exclude && (allowLiability || !a.Type.IsLiability());

        if (context.Accounts.FirstOrDefault(a => a.Id == context.LastUsedAccountId) is { } last && Allowed(last))
        {
            return last.Id;
        }

        return (context.Accounts.FirstOrDefault(a => a.Type == AccountType.PersonalCurrent && Allowed(a))
                ?? context.Accounts.FirstOrDefault(a => a.Type.IsLiquid() && Allowed(a))
                ?? context.Accounts.FirstOrDefault(Allowed))?.Id;
    }

    /// <summary>Finds "naar X" / "van X" and consumes the longest run of words that names one account.</summary>
    private static Account? TakeAccountAfter(List<Token> tokens, HashSet<string> prepositions, IReadOnlyList<Account> accounts)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Used || !prepositions.Contains(tokens[i].Lower))
            {
                continue;
            }

            var end = i + 1;
            while (end < tokens.Count && !tokens[end].Used && !IsPreposition(tokens[end].Lower))
            {
                end++;
            }

            for (var length = end - i - 1; length >= 1; length--)
            {
                var phrase = string.Join(' ', tokens.Skip(i + 1).Take(length).Select(t => t.Lower));
                if (MatchAccount(phrase, accounts) is { } match)
                {
                    for (var k = i; k <= i + length; k++)
                    {
                        tokens[k].Used = true;
                    }

                    return match;
                }
            }
        }

        return null;
    }

    /// <summary>"Spotify 17,99 visa": a lone card or cash word after the payee names the account.</summary>
    private static Account? TakeAlias(List<Token> tokens, IReadOnlyList<Account> accounts)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Used)
            {
                continue;
            }

            AccountType? type = CardAliases.Contains(token.Lower) ? AccountType.CreditCard
                : CashAliases.Contains(token.Lower) ? AccountType.Cash
                : null;
            var match = type is null ? null : Single(accounts.Where(a => a.Type == type));
            if (match is not null)
            {
                token.Used = true;
                return match;
            }
        }

        return null;
    }

    internal static Account? MatchAccount(string phrase, IReadOnlyList<Account> accounts)
    {
        phrase = PayeeRule.Normalize(phrase);
        if (phrase.Length == 0)
        {
            return null;
        }

        if (Single(accounts.Where(a => PayeeRule.Normalize(a.Name) == phrase)) is { } exact)
        {
            return exact;
        }

        if (phrase.Length >= 3 && Single(accounts.Where(a => PayeeRule.Normalize(a.Name).Contains(phrase, StringComparison.Ordinal))) is { } partial)
        {
            return partial;
        }

        var type = TypeFromWords(phrase);
        return type is null ? null : Single(accounts.Where(a => a.Type == type));
    }

    private static AccountType? TypeFromWords(string phrase)
    {
        bool Has(params string[] stems) => phrase.Split(' ').Any(w => stems.Any(s => w.StartsWith(s, StringComparison.Ordinal)));

        if (Has("gezamenlijk", "joint"))
        {
            return AccountType.JointCurrent;
        }

        if (Has("spaar", "saving"))
        {
            return AccountType.Savings;
        }

        if (Has("visa", "kredietkaart", "creditcard", "kaart", "card"))
        {
            return AccountType.CreditCard;
        }

        if (Has("cash", "portemonnee", "wallet"))
        {
            return AccountType.Cash;
        }

        if (Has("zicht", "current", "betaalrekening"))
        {
            return AccountType.PersonalCurrent;
        }

        if (Has("pensioen", "pension"))
        {
            return AccountType.Pension;
        }

        return Has("saxo", "beleg", "etf", "invest") ? AccountType.Investment : null;
    }

    private static Account? Single(IEnumerable<Account> candidates)
    {
        var list = candidates.Take(2).ToList();
        return list.Count == 1 ? list[0] : null;
    }

    private static bool IsPreposition(string word) =>
        ToWords.Contains(word) || FromWords.Contains(word) || WithWords.Contains(word);

    private static string StripCurrency(string text) => text.Trim('€');

    private static bool LooksLikeAmount(string text)
    {
        var stripped = StripCurrency(text);
        return stripped.Any(char.IsAsciiDigit) && AmountParser.Parse(stripped).Success;
    }

    /// <summary>"vandaag", "gisteren", "eergisteren", "today", "yesterday", "3/10", "03-10-2026", "3/10/26".</summary>
    internal static bool TryReadDate(string word, DateOnly today, out DateOnly date)
    {
        date = today;
        switch (word)
        {
            case "vandaag" or "today":
                return true;
            case "gisteren" or "yesterday":
                date = today.AddDays(-1);
                return true;
            case "eergisteren":
                date = today.AddDays(-2);
                return true;
        }

        var parts = word.Split('/', '-');
        if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length == 0 || p.Length > 4 || !p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        var day = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var month = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int year;
        if (parts.Length == 3)
        {
            year = int.Parse(parts[2], CultureInfo.InvariantCulture);
            if (parts[2].Length == 2)
            {
                year += 2000;
            }
            else if (parts[2].Length != 4)
            {
                return false;
            }
        }
        else
        {
            // Without a year: the most recent such day, so "28/12" typed on 2 January means last December.
            year = today.Year;
            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) && new DateOnly(year, month, day) > today)
            {
                year--;
            }
        }

        if (month is < 1 or > 12 || year is < 1900 or > 9999 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }
}
