using PersonalFinanceOS.Application.Scenarios;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Application.Assistant;

/// <summary>The questions the assistant understands (spec §15 initial intents).</summary>
public enum AssistantIntent
{
    Unknown,
    Help,

    /// <summary>"Can I afford this?"</summary>
    CanAfford,

    /// <summary>"Can I spend another €100 on groceries this month?"</summary>
    CategoryRoom,

    /// <summary>"How much can I safely spend before payday?"</summary>
    SafeToSpend,

    /// <summary>"What payments are coming up?"</summary>
    Upcoming,

    /// <summary>"Why will my balance be lower next month?"</summary>
    WhyLower,

    /// <summary>"What if renovation costs increase by €5,000?"</summary>
    WhatIf,

    /// <summary>"When can I afford a wishlist item?"</summary>
    WhenAfford,

    /// <summary>"How much did I spend on subscriptions?"</summary>
    SpentOn,

    /// <summary>"What changed versus last month?"</summary>
    ChangedVsLastMonth,

    /// <summary>"Should I use VISA or wait?"</summary>
    CardOrWait,

    /// <summary>"What are my fixed costs?", "What do I usually spend a month?" (D-039)</summary>
    FixedCosts,
}

/// <summary>The period a spending question is about.</summary>
public enum PeriodKind
{
    ThisMonth,
    LastMonth,
    ThisYear,

    /// <summary>The last N complete months.</summary>
    LastMonths,

    /// <summary>A named month.</summary>
    Month,
}

public sealed record QuestionPeriod(PeriodKind Kind, DateOnly From, DateOnly To, int Months = 1);

/// <summary>What the parser found in a question. Everything is optional; the service asks for what it needs.</summary>
public sealed record ParsedQuestion(
    AssistantIntent Intent,
    Money? Amount = null,
    Category? Category = null,
    WishlistItem? Item = null,
    RecurringRule? Rule = null,
    QuestionPeriod? Period = null,

    /// <summary>"Per month": a monthly amount rather than a one-off.</summary>
    bool IsMonthly = false,

    /// <summary>"Lower", "cheaper", "drops"; otherwise a change is an increase.</summary>
    bool IsDecrease = false,

    /// <summary>The question is about salary or income.</summary>
    bool AboutIncome = false);

/// <summary>What the assistant asked for, so a short reply ("€250", "Groceries") can complete the question.</summary>
public enum MissingValue
{
    Amount,
    Category,
    Item,
}

/// <summary>The assistant's open question; <see cref="Text"/> is the user's original wording, kept to name a what-if.</summary>
public sealed record PendingQuestion(ParsedQuestion Question, MissingValue Missing, string Text = "");

/// <summary>Spec §15 answer rules: a direct or qualified answer, a focused question, or "not understood".</summary>
public enum AnswerKind
{
    Direct,

    /// <summary>An answer with conditions or missing information that matter.</summary>
    Qualified,

    /// <summary>One focused question, because a missing value changes the answer.</summary>
    Question,
    NotUnderstood,
}

/// <summary>
/// A localizable text: a string key with values. Values are <see cref="Money"/>, <see cref="DateOnly"/>, numbers, other
/// <see cref="AnswerText"/>s or the user's own names; the screen formats them. Every figure comes from a calculation
/// service, never from the text itself.
/// </summary>
public sealed record AnswerText(string Key, params object[] Args);

/// <summary>A key figure: a label and its value.</summary>
public sealed record AnswerFigure(AnswerText Label, object Value);

/// <summary>Something the user can do next. The assistant itself never changes data (spec §15).</summary>
public enum AssistantAction
{
    OpenForecasts,
    OpenScenarios,
    OpenWishlist,
    OpenBudgets,
    OpenAccounts,

    /// <summary>Saves the what-if as a scenario, only when the user clicks it.</summary>
    SaveScenario,
}

public sealed record AssistantAnswer(
    AssistantIntent Intent,
    AnswerKind Kind,
    AnswerText Headline,
    IReadOnlyList<AnswerFigure> Figures,

    /// <summary>The commitments and items the answer took into account.</summary>
    IReadOnlyList<AnswerText> Considered,

    /// <summary>Assumptions and missing information.</summary>
    IReadOnlyList<AnswerText> Assumptions,
    AnswerText? Explanation,
    IReadOnlyList<AssistantAction> Actions,

    /// <summary>Short replies the user can click: names of categories or wishlist items, or example questions (keys).</summary>
    IReadOnlyList<string> Suggestions,
    PendingQuestion? Pending = null,

    /// <summary>For <see cref="AssistantAction.SaveScenario"/>: the changes and a proposed name.</summary>
    IReadOnlyList<ScenarioChangeRequest>? Scenario = null,
    string? ScenarioName = null,

    /// <summary>The suggestions are example-question keys to translate, not the user's own names.</summary>
    bool SuggestionsAreExamples = false);

/// <summary>A month as a value in an <see cref="AnswerText"/>, shown as "October 2026".</summary>
public readonly record struct MonthRef(DateOnly Month);
