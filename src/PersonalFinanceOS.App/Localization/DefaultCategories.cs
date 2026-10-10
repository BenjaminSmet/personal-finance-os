using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Domain.Categories;

namespace PersonalFinanceOS.App.Localization;

/// <summary>Starter categories from spec §13, named in the current interface language.</summary>
internal static class DefaultCategories
{
    private static readonly (string Key, CategoryKind Kind)[] Keys =
    [
        ("Housing", CategoryKind.Expense),
        ("LoanPayment", CategoryKind.Expense),
        ("Groceries", CategoryKind.Expense),
        ("Transport", CategoryKind.Expense),
        ("Utilities", CategoryKind.Expense),
        ("Insurance", CategoryKind.Expense),
        ("Subscriptions", CategoryKind.Expense),
        ("PersonalCare", CategoryKind.Expense),
        ("Leisure", CategoryKind.Expense),
        ("Shopping", CategoryKind.Expense),
        ("HomeRenovation", CategoryKind.Expense),
        ("OtherExpenses", CategoryKind.Expense),
        ("Salary", CategoryKind.Income),
        ("OtherIncome", CategoryKind.Income),
    ];

    public static IReadOnlyList<DefaultCategory> Localized() =>
        Keys.Select(k => new DefaultCategory(Loc.T("DefaultCategory_" + k.Key), k.Kind)).ToList();
}
