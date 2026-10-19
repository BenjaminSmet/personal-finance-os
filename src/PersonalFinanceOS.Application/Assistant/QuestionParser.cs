using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Application.Assistant;

/// <summary>
/// Recognises a question in Dutch or English (spec §15): its intent and the amount, category, wishlist item, planned
/// item and period in it. Rule-based and deterministic: the same words always give the same result, and nothing is
/// guessed. A value that is not in the question stays empty so the assistant can ask for it.
/// </summary>
public static partial class QuestionParser
{
    private static readonly string[] WhatIfWords = ["wat als", "wat gebeurt er als", "wat gebeurt er wanneer", "stel dat", "what if", "what happens if", "suppose", "scenario"];
    private static readonly string[] CardWords = ["visa", "kredietkaart", "creditcard", "credit card", "card", "kaart", "mastercard"];
    private static readonly string[] CardChoiceWords = ["wachten", "wait", "of ", " or ", "gebruiken", "gebruik", "use ", "should", "moet ik", "beter", "best", "betalen met", "pay with", "pay by"];
    private static readonly string[] ChangeWords = ["veranderd", "verandert", "verandering", "verschil", "verschilt", "changed", "change", "different", "difference", "compared", "vergeleken", "versus", " vs ", "tov", "t o v", "ten opzichte"];
    private static readonly string[] LastMonthWords = ["vorige maand", "vorige maanden", "last month", "previous month", "afgelopen maand"];
    private static readonly string[] WhyWords = ["waarom", "why", "hoe komt het", "how come"];
    private static readonly string[] SpentWords = ["uitgegeven", "spent", "how much did i spend", "how much have i spent", "hoeveel ging naar", "how much went to", "hoeveel betaalde ik", "kostte"];
    private static readonly string[] RoomWords = ["nog ", "another", "extra", "more", "uitgeven", "spend", "erbij"];
    private static readonly string[] UpcomingWords = ["betalingen", "payments", "rekeningen", "bills", "komende", "upcoming", "coming up", "moet ik betalen", "do i have to pay", "due", "vervalt", "vervallen", "eraan komen", "komen eraan", "staan er"];
    private static readonly string[] WhenWords = ["wanneer", "when", "vanaf wanneer", "hoe lang"];
    private static readonly string[] SafeWords = ["veilig", "safe", "safely", "besteedbaar", "hoeveel kan ik uitgeven", "how much can i spend", "payday", "pay day", "tot mijn loon", "tot het loon", "tot mijn salaris", "voor mijn loon", "before my salary", "until my salary", "vrij te besteden"];
    private static readonly string[] AffordWords = ["kan ik", "can i", "veroorloven", "afford", "betalen", "kopen", "buy", "aanschaffen", "permit", "lukt", "haalbaar"];
    private static readonly string[] FixedWords =
    [
        "vaste kosten", "vaste lasten", "vaste uitgaven", "vaste rekeningen", "maandelijkse kosten", "maandelijkse uitgaven", "fixed costs", "fixed expenses",
        "fixed bills", "monthly costs", "monthly expenses", "monthly bills", "normaal uit", "gewoonlijk uit", "meestal uit", "doorgaans uit", "gemiddeld uit",
        "usually spend", "normally spend", "typically spend", "spend on average", "gewone uitgaven", "usual spending",
    ];

    private static readonly string[] ExtraWords = ["extra", "nog", "meer", "more", "another", "additional"];
    private static readonly string[] SpendVerbs = ["uitgeven", "spenderen", "besteden", "spend", "kopen", "buy"];
    private static readonly string[] HelpWords = ["help", "hulp", "wat kan je", "wat kun je", "wat kan jij", "what can you", "what can i ask", "wat kan ik vragen", "voorbeelden", "examples"];
    private static readonly string[] IncomeWords = ["loon", "salaris", "inkomen", "wedde", "salary", "income", "wage", "wages", "pay rise", "paycheck"];
    private static readonly string[] MonthlyWords = ["per maand", "maandelijks", "elke maand", "iedere maand", "per month", "monthly", "a month", "every month", "each month"];
    private static readonly string[] DecreaseWords = ["minder", "goedkoper", "lager", "daalt", "dalen", "zakt", "omlaag", "decrease", "decreases", "less", "lower", "cheaper", "drops", "drop", "falls", "fall", "goes down", "cut", "reduced", "verlaagd"];

    private static readonly string[] Months = ["januari", "februari", "maart", "april", "mei", "juni", "juli", "augustus", "september", "oktober", "november", "december"];
    private static readonly string[] MonthsEn = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    /// <summary>Words for the starter categories in both languages (their default names included), so "groceries" finds "Boodschappen".</summary>
    private static readonly string[][] CategoryWords =
    [
        ["wonen", "housing", "huur", "rent", "woning", "hypotheek", "mortgage"],
        ["lening", "loan payment", "loan", "afbetaling", "afbetalingen"],
        ["boodschappen", "groceries", "grocery", "supermarkt", "supermarket", "eten", "food", "voeding"],
        ["vervoer", "transport", "auto", "car", "brandstof", "benzine", "diesel", "fuel", "trein", "train", "bus"],
        ["nutsvoorzieningen", "utilities", "energie", "energy", "elektriciteit", "electricity", "water", "gas", "internet"],
        ["verzekeringen", "verzekering", "insurance", "insurances"],
        ["abonnementen", "abonnement", "subscriptions", "subscription", "streaming"],
        ["persoonlijke verzorging", "personal care", "verzorging", "kapper", "hairdresser"],
        ["vrije tijd", "leisure", "ontspanning", "uitgaan", "restaurant", "restaurants", "hobby", "hobbys", "going out"],
        ["shopping", "winkelen", "kleding", "clothes", "clothing"],
        ["renovatie", "home renovation", "renovation", "verbouwing", "verbouwen", "renoveren"],
        ["andere uitgaven", "other spending", "other expenses"],
        ["loon", "salary", "salaris", "wedde"],
        ["ander inkomen", "other income"],
    ];

    private static readonly HashSet<string> StopWords =
    [
        "de", "het", "een", "en", "van", "voor", "met", "naar", "mijn", "the", "and", "for", "with", "my", "new", "nieuwe", "nieuw",
    ];

    public static ParsedQuestion Parse(
        string text,
        DateOnly today,
        IReadOnlyList<Category> categories,
        IReadOnlyList<WishlistItem> items,
        IReadOnlyList<RecurringRule> rules)
    {
        var lower = (text ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture);
        var words = Normalize(lower);
        var amount = FindAmount(lower);
        var category = FindCategory(words, categories);
        var item = FindItem(words, items);
        var rule = FindRule(words, rules);
        var period = FindPeriod(words, today);
        var monthly = Any(words, MonthlyWords);
        var decrease = Any(words, DecreaseWords);
        var income = Any(words, IncomeWords);

        var intent = Classify(words, amount, category, item);
        return new ParsedQuestion(intent, amount, category, item, rule, period, monthly, decrease, income);
    }

    private static AssistantIntent Classify(string w, Money? amount, Category? category, WishlistItem? item)
    {
        if (Any(w, WhatIfWords))
        {
            return AssistantIntent.WhatIf;
        }

        if (Any(w, CardWords) && Any(w, CardChoiceWords))
        {
            return AssistantIntent.CardOrWait;
        }

        if (Any(w, ChangeWords) && Any(w, LastMonthWords))
        {
            return AssistantIntent.ChangedVsLastMonth;
        }

        if (Any(w, WhyWords))
        {
            return AssistantIntent.WhyLower;
        }

        if (Any(w, SpentWords))
        {
            return AssistantIntent.SpentOn;
        }

        if (category is { Kind: CategoryKind.Expense } && Any(w, RoomWords) && (amount is not null || Any(w, ["uitgeven", "spend"])))
        {
            return AssistantIntent.CategoryRoom;
        }

        if (amount is null && Any(w, FixedWords))
        {
            return category is null ? AssistantIntent.FixedCosts : AssistantIntent.SpentOn;
        }

        // "Can I still spend something extra?" without an amount: what is left after fixed costs and usual spending.
        if (amount is null && item is null && category is null && Any(w, ExtraWords) && Any(w, SpendVerbs) && !Any(w, WhenWords))
        {
            return AssistantIntent.SafeToSpend;
        }

        if (Any(w, UpcomingWords) && amount is null && item is null)
        {
            return AssistantIntent.Upcoming;
        }

        if (Any(w, WhenWords))
        {
            return AssistantIntent.WhenAfford;
        }

        if (Any(w, SafeWords) && amount is null)
        {
            return AssistantIntent.SafeToSpend;
        }

        if (Any(w, AffordWords))
        {
            return AssistantIntent.CanAfford;
        }

        if (Any(w, HelpWords))
        {
            return AssistantIntent.Help;
        }

        return AssistantIntent.Unknown;
    }

    /// <summary>
    /// The amount in the question: one marked with € or "euro" first, otherwise the first number that is not a count of
    /// days, months or a percentage. "5.000", "5,000", "5000", "1.234,56" and "5k" all work.
    /// </summary>
    public static Money? FindAmount(string lower)
    {
        Money? plain = null;
        foreach (Match m in AmountPattern().Matches(lower))
        {
            var after = lower[(m.Index + m.Length)..].TrimStart();
            if (UnitPattern().IsMatch(after))
            {
                continue;
            }

            var number = m.Groups["n"].Value;
            if (ThousandsPattern().IsMatch(number))
            {
                number = number.Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
            }

            var parsed = AmountParser.Parse(number);
            if (!parsed.Success || !parsed.Value.IsPositive)
            {
                continue;
            }

            var value = m.Groups["k"].Success ? Money.FromCents(parsed.Value.Cents * 1000) : parsed.Value;
            var marked = m.Groups["cur"].Success || m.Groups["unit"].Success || m.Groups["k"].Success;
            if (marked)
            {
                return value;
            }

            plain ??= value;
        }

        return plain;
    }

    /// <summary>
    /// A category named in the question: its own name (also without a plural ending), or a word for a starter category
    /// when the user kept that category.
    /// </summary>
    private static Category? FindCategory(string w, IReadOnlyList<Category> categories)
    {
        var active = categories.Where(c => c.IsActive).ToList();
        foreach (var c in active.OrderByDescending(c => c.Name.Length))
        {
            var name = Normalize(c.Name.ToLower(CultureInfo.InvariantCulture)).Trim();
            if (name.Length > 0 && (w.Contains($" {name} ", StringComparison.Ordinal) || (Stem(name) is { } stem && w.Contains($" {stem}", StringComparison.Ordinal))))
            {
                return c;
            }
        }

        foreach (var group in CategoryWords)
        {
            if (!group.Any(word => w.Contains($" {word} ", StringComparison.Ordinal)))
            {
                continue;
            }

            var match = active.FirstOrDefault(c => group.Contains(Normalize(c.Name.ToLower(CultureInfo.InvariantCulture)).Trim()));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>A wishlist item named in the question (the caller passes the open ones): its full name, or the most of its significant words.</summary>
    private static WishlistItem? FindItem(string w, IReadOnlyList<WishlistItem> items) =>
        BestByWords(w, items, i => i.Name);

    private static RecurringRule? FindRule(string w, IReadOnlyList<RecurringRule> rules) =>
        BestByWords(w, rules.Where(r => r.IsActive), r => r.Name);

    private static T? BestByWords<T>(string w, IEnumerable<T> candidates, Func<T, string> name)
        where T : class
    {
        T? best = null;
        var bestScore = 0;
        foreach (var candidate in candidates)
        {
            var normalized = Normalize(name(candidate).ToLower(CultureInfo.InvariantCulture)).Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            var score = w.Contains($" {normalized} ", StringComparison.Ordinal)
                ? 100
                : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Count(word => word.Length >= 3 && !StopWords.Contains(word) && (w.Contains($" {word} ", StringComparison.Ordinal) || (Stem(word) is { } s && w.Contains($" {s}", StringComparison.Ordinal))));
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static QuestionPeriod? FindPeriod(string w, DateOnly today)
    {
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var count = LastMonthsPattern().Match(w);
        if (count.Success && int.TryParse(count.Groups[1].Value, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 24)
        {
            return new QuestionPeriod(PeriodKind.LastMonths, thisMonth.AddMonths(-n), thisMonth.AddDays(-1), n);
        }

        if (Any(w, ["vorige maand", "last month", "previous month", "afgelopen maand"]))
        {
            return new QuestionPeriod(PeriodKind.LastMonth, thisMonth.AddMonths(-1), thisMonth.AddDays(-1));
        }

        if (Any(w, ["dit jaar", "this year", "sinds januari", "since january"]))
        {
            return new QuestionPeriod(PeriodKind.ThisYear, new DateOnly(today.Year, 1, 1), today, today.Month);
        }

        for (var i = 0; i < 12; i++)
        {
            if (w.Contains($" {Months[i]} ", StringComparison.Ordinal) || w.Contains($" {MonthsEn[i]} ", StringComparison.Ordinal))
            {
                // "May" is also an English verb; only a month when it is clearly about a period.
                if (i == 4 && !w.Contains(" mei ", StringComparison.Ordinal) && !Any(w, [" in may ", " during may "]))
                {
                    continue;
                }

                var year = i + 1 > today.Month ? today.Year - 1 : today.Year;
                var first = new DateOnly(year, i + 1, 1);
                var last = first.AddMonths(1).AddDays(-1);
                return new QuestionPeriod(PeriodKind.Month, first, last < today ? last : today);
            }
        }

        if (Any(w, ["deze maand", "this month"]))
        {
            return new QuestionPeriod(PeriodKind.ThisMonth, thisMonth, today);
        }

        return null;
    }

    /// <summary>Lower case without accents, every character other than a letter or digit a space, padded with spaces.</summary>
    public static string Normalize(string lower)
    {
        var decomposed = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length + 2).Append(' ');
        var space = true;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }

        if (!space)
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>Whether one of the phrases occurs as whole words.</summary>
    private static bool Any(string w, IEnumerable<string> phrases) =>
        phrases.Any(p => w.Contains($" {p.Trim()} ", StringComparison.Ordinal));

    /// <summary>A word without its plural ending ("abonnementen" → "abonnement", "subscriptions" → "subscription").</summary>
    private static string? Stem(string word)
    {
        if (word.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var ending in new[] { "en", "es", "s" })
        {
            if (word.Length - ending.Length >= 4 && word.EndsWith(ending, StringComparison.Ordinal))
            {
                return word[..^ending.Length];
            }
        }

        return word.Length >= 4 ? word : null;
    }

    [GeneratedRegex(@"(?<cur>€\s*)?(?<n>\d+(?:[.,\s]\d{3})*(?:[.,]\d{1,2})?)(?:\s*(?<k>k)\b)?(?:\s*(?<unit>euro|eur)\b)?")]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"^(?:%|(?:procent|percent|dag|dagen|day|days|maand|maanden|month|months|week|weken|weeks|jaar|jaren|year|years|keer|times|stuks)\b)")]
    private static partial Regex UnitPattern();

    [GeneratedRegex(@"^\d{1,3}([.,]\d{3})+$")]
    private static partial Regex ThousandsPattern();

    [GeneratedRegex(@" (?:afgelopen|laatste|vorige|last|past|previous) (\d{1,2}) (?:maanden|maand|months|month) ")]
    private static partial Regex LastMonthsPattern();
}
