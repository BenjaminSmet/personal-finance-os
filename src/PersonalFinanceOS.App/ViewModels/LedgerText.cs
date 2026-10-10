using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.QuickAdd;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>Translated names for ledger concepts. Display only; nothing here affects stored values.</summary>
internal static class LedgerText
{
    public static string Type(TransactionType type) => Loc.T("TxType_" + type);

    public static string Kind(EntryKind kind) => Loc.T("EntryKind_" + kind);

    public static string Issue(QuickAddIssue issue) => Loc.T("QuickAddIssue_" + issue);

    public static string Amount(Money amount) => amount.Format(Loc.Instance.Culture);

    /// <summary>"today", "yesterday" or a short localized date.</summary>
    public static string Date(DateOnly date, DateOnly today) =>
        date == today ? Loc.T("Date_Today")
        : date == today.AddDays(-1) ? Loc.T("Date_Yesterday")
        : date.ToString("d MMM yyyy", Loc.Instance.Culture);
}
