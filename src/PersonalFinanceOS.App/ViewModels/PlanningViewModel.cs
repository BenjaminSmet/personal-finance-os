using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record KindOption(RecurringKind Kind, string Name);

public sealed record FrequencyOption(RecurrenceFrequency Frequency, string Name);

/// <summary>One line of the safe-to-spend breakdown.</summary>
public sealed record ForecastLineViewModel(string Label, string AmountText, bool IsNegative);

/// <summary>Expected money in and out over a number of days.</summary>
public sealed record CashFlowViewModel(string Title, string InText, string OutText, string NetText);

/// <summary>
/// One open occurrence of a recurring rule, with what the user can do about it (spec §12):
/// confirm (with the real date and amount), link an existing transaction, postpone or skip.
/// </summary>
public sealed partial class PlannedRowViewModel : ViewModelBase
{
    private readonly Func<PlannedRowViewModel, PlannedAction, Task> _act;

    public PlannedRowViewModel(PlannedOccurrence occurrence, string accountText, DateOnly today, Func<PlannedRowViewModel, PlannedAction, Task> act)
    {
        Occurrence = occurrence;
        AccountText = accountText;
        _act = act;
        _date = occurrence.DueDate.ToDateTime(TimeOnly.MinValue);
        _editAmountText = occurrence.Amount.ToEuros().ToString("0.00", Loc.Instance.Culture);
        DueText = Loc.Format(occurrence.IsOverdue ? "Planning_DueOverdue" : "Planning_Due", LedgerText.Date(occurrence.DueDate, today));
        MatchText = occurrence.SuggestedMatch is { } m
            ? Loc.Format("Planning_Match", string.IsNullOrEmpty(m.Description) ? LedgerText.Type(m.Type) : m.Description, LedgerText.Date(m.Date, today), LedgerText.Amount(m.Amount.Abs()))
            : null;
    }

    public PlannedOccurrence Occurrence { get; }

    public string Name => Occurrence.Rule.Name;

    public string KindText => Loc.T("RecurringKind_" + Occurrence.Rule.Kind);

    public string AccountText { get; }

    public string AmountText => LedgerText.Amount(Occurrence.Rule.Kind == RecurringKind.Income ? Occurrence.Amount : Occurrence.Amount.Negate());

    public bool IsIncome => Occurrence.Rule.Kind == RecurringKind.Income;

    public string DueText { get; }

    public bool IsOverdue => Occurrence.IsOverdue;

    public bool IsPostponed => Occurrence.IsPostponed;

    public string? MatchText { get; }

    public bool HasMatch => MatchText is not null;

    public bool IsLast { get; set; }

    [ObservableProperty]
    private DateTime? _date;

    [ObservableProperty]
    private string _editAmountText;

    [ObservableProperty]
    private string? _error;

    [RelayCommand]
    private Task Confirm() => _act(this, PlannedAction.Confirm);

    [RelayCommand]
    private Task Link() => _act(this, PlannedAction.Link);

    [RelayCommand]
    private Task Postpone() => _act(this, PlannedAction.Postpone);

    [RelayCommand]
    private Task Skip() => _act(this, PlannedAction.Skip);
}

public enum PlannedAction
{
    Confirm,
    Link,
    Postpone,
    Skip,
}

public sealed partial class RuleRowViewModel(RecurringRule rule, string detail, string? nextText, Func<RuleRowViewModel, Task> end) : ViewModelBase
{
    public RecurringRule Rule { get; } = rule;

    public string Name => Rule.Name;

    public string Detail { get; } = detail;

    public string? NextText { get; } = nextText;

    public string AmountText => LedgerText.Amount(Rule.Kind == RecurringKind.Income ? Rule.Amount : Rule.Amount.Negate());

    public bool IsIncome => Rule.Kind == RecurringKind.Income;

    public bool IsLast { get; set; }

    [RelayCommand]
    private Task End() => end(this);
}

/// <summary>The state of one credit card in words (spec §10 display). Every amount is an estimate until reconciled.</summary>
public sealed class CardStatusViewModel(CardStatus status, DateOnly today)
{
    private static string M(Money m) => LedgerText.Amount(m);

    private static string D(DateOnly d) => d.ToString("d MMM", Loc.Instance.Culture);

    public string Name => status.Card.Name;

    public bool IsSetUp => status.IsSetUp;

    public string PeriodText => status.IsSetUp ? Loc.Format("Card_Period", D(status.CurrentPeriod.Start), D(status.CurrentPeriod.End)) : string.Empty;

    public string LimitText => status.Profile is { } p ? Loc.Format("Card_Limit", M(p.CreditLimit)) : string.Empty;

    public string OutstandingText => M(status.Outstanding);

    public string AvailableText => M(status.AvailableCredit);

    /// <summary>One line for the dashboard: what is owed and the available credit, which is not cash.</summary>
    public string SummaryText => Loc.Format("Dashboard_CardLine", M(status.Outstanding), M(status.AvailableCredit));

    public bool IsOverLimit => status.IsOverLimit;

    public string SpendingText => M(status.CurrentSpending);

    public string EstimateText => status.PlannedInPeriod.IsPositive
        ? Loc.Format("Card_EstimateWithPlanned", M(status.EstimatedStatement), M(status.PlannedInPeriod))
        : Loc.Format("Card_Estimate", M(status.EstimatedStatement));

    public string NextSettlementText => status.NextSettlement switch
    {
        null => Loc.T("Card_NothingToPay"),
        { Date: { } date } s when date < today => Loc.Format("Card_SettlementOverdue", M(s.Amount), LedgerText.Date(date, today)),
        { Date: { } date } s => Loc.Format("Card_SettlementOn", M(s.Amount), LedgerText.Date(date, today)),
        { } s => Loc.Format("Card_SettlementNoDate", M(s.Amount)),
    };

    public string? DifferenceText => status.SettlementDifference is { } diff
        ? Loc.Format(diff.IsPositive ? "Card_PaidMore" : "Card_PaidLess", M(diff.Abs()), M(status.LastStatement), M(status.PaidSinceClose))
        : null;

    public bool HasDifference => DifferenceText is not null;
}

/// <summary>
/// Planning (spec §12, §13, §10): safe-to-spend, expected cash flow, planned items to confirm, the
/// state of each credit card, and the recurring rules. Nothing here changes a balance until the user
/// confirms an item.
/// </summary>
public sealed partial class PlanningViewModel : PageViewModel
{
    private static readonly int[] CashFlowDays = [7, 30, 90];

    private readonly RecurringService _recurring;
    private readonly ForecastService _forecast;
    private readonly CreditCardService _cards;
    private readonly AccountService _accounts;
    private readonly CategoryService _categories;
    private readonly AppSettingsService _settings;
    private readonly IClock _clock;
    private IReadOnlyList<Account> _accountList = [];
    private IReadOnlyList<Category> _categoryList = [];
    private bool _active;
    private bool _loading;

    public PlanningViewModel(
        RecurringService recurring,
        ForecastService forecast,
        CreditCardService cards,
        AccountService accounts,
        CategoryService categories,
        AppSettingsService settings,
        LedgerService ledger,
        IClock clock)
    {
        _recurring = recurring;
        _forecast = forecast;
        _cards = cards;
        _accounts = accounts;
        _categories = categories;
        _settings = settings;
        _clock = clock;
        void Refresh(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (_active)
            {
                _ = ReloadAsync();
            }
        });
        ledger.Changed += Refresh;
        recurring.Changed += Refresh;
        cards.Changed += Refresh;
        KindOptions = BuildKindOptions();
        FrequencyOptions = BuildFrequencyOptions();
        _newKind = KindOptions[1];
        _newFrequency = FrequencyOptions[1];
        _newStartDate = clock.Today.ToDateTime(TimeOnly.MinValue);
    }

    // ===== Safe-to-spend =====

    [ObservableProperty]
    private bool _canCalculate;

    [ObservableProperty]
    private string _safeAmountText = string.Empty;

    [ObservableProperty]
    private bool _safeIsNegative;

    [ObservableProperty]
    private string _safeUntilText = string.Empty;

    public ObservableCollection<ForecastLineViewModel> SafeLines { get; } = [];

    public ObservableCollection<string> Caveats { get; } = [];

    public ObservableCollection<CashFlowViewModel> CashFlows { get; } = [];

    // ===== To confirm =====

    public ObservableCollection<PlannedRowViewModel> Planned { get; } = [];

    public bool HasPlanned => Planned.Count > 0;

    [ObservableProperty]
    private string? _statusMessage;

    // ===== Cards =====

    public ObservableCollection<CardStatusViewModel> Cards { get; } = [];

    public bool HasCards => Cards.Count > 0;

    // ===== Rules =====

    public ObservableCollection<RuleRowViewModel> Rules { get; } = [];

    public bool HasRules => Rules.Count > 0;

    [ObservableProperty]
    private bool _isRuleFormOpen;

    [ObservableProperty]
    private IReadOnlyList<KindOption> _kindOptions;

    [ObservableProperty]
    private IReadOnlyList<FrequencyOption> _frequencyOptions;

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _accountOptions = [];

    [ObservableProperty]
    private IReadOnlyList<CategoryOption> _categoryOptions = [];

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransfer), nameof(AccountLabel))]
    private KindOption _newKind;

    [ObservableProperty]
    private AccountOption? _newAccount;

    [ObservableProperty]
    private AccountOption? _newToAccount;

    [ObservableProperty]
    private CategoryOption? _newCategory;

    [ObservableProperty]
    private string _newAmountText = string.Empty;

    [ObservableProperty]
    private FrequencyOption _newFrequency;

    [ObservableProperty]
    private DateTime? _newStartDate;

    [ObservableProperty]
    private DateTime? _newEndDate;

    [ObservableProperty]
    private string? _ruleError;

    public bool IsTransfer => NewKind.Kind == RecurringKind.Transfer;

    public string AccountLabel => Loc.T(IsTransfer ? "Planning_FromAccount" : "Planning_Account");

    // ===== Settings used by the forecast =====

    [ObservableProperty]
    private string _bufferText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _roundingOptions = [];

    [ObservableProperty]
    private AccountOption? _roundingAccount;

    [ObservableProperty]
    private string? _settingsMessage;

    [ObservableProperty]
    private string? _settingsError;

    public override Task ActivateAsync()
    {
        _active = true;
        return ReloadAsync();
    }

    partial void OnNewKindChanged(KindOption value) => UpdateCategoryOptions();

    async partial void OnRoundingAccountChanged(AccountOption? value)
    {
        // A combo box clears its selection while its list is replaced; that is not a choice.
        if (_loading || value is null)
        {
            return;
        }

        await _settings.SetRoundingAccountIdAsync(value.Id == Guid.Empty ? null : value.Id);
        SettingsMessage = Loc.T("Planning_SettingsSaved");
    }

    [RelayCommand]
    private async Task SaveBufferAsync()
    {
        SettingsError = null;
        SettingsMessage = null;
        var parsed = string.IsNullOrWhiteSpace(BufferText) ? new AmountParseResult(true, Money.Zero, false) : AmountParser.Parse(BufferText);
        if (!parsed.Success || parsed.Value.IsNegative)
        {
            SettingsError = Loc.Error("amount.invalid");
            return;
        }

        await _settings.SetMinimumBufferAsync(parsed.Value);
        SettingsMessage = Loc.T("Planning_SettingsSaved");
        await ReloadAsync();
    }

    [RelayCommand]
    private void OpenRuleForm()
    {
        NewName = string.Empty;
        NewAmountText = string.Empty;
        NewKind = KindOptions[1];
        NewFrequency = FrequencyOptions[1];
        NewStartDate = _clock.Today.ToDateTime(TimeOnly.MinValue);
        NewEndDate = null;
        NewAccount = AccountOptions.FirstOrDefault();
        NewToAccount = AccountOptions.Skip(1).FirstOrDefault();
        UpdateCategoryOptions();
        RuleError = null;
        IsRuleFormOpen = true;
    }

    [RelayCommand]
    private void CancelRuleForm()
    {
        IsRuleFormOpen = false;
        RuleError = null;
    }

    [RelayCommand]
    private async Task SaveRuleAsync()
    {
        RuleError = null;
        var amount = AmountParser.Parse(NewAmountText);
        if (!amount.Success || !amount.Value.IsPositive)
        {
            RuleError = Loc.Error("amount.invalid");
            return;
        }

        if (NewStartDate is not { } start || NewAccount is null)
        {
            RuleError = Loc.Error(NewAccount is null ? "account.notFound" : "date.required");
            return;
        }

        try
        {
            await _recurring.CreateRuleAsync(new RecurringRuleRequest(
                NewName,
                NewKind.Kind,
                NewAccount.Id,
                IsTransfer ? NewToAccount?.Id : null,
                IsTransfer ? null : NewCategory?.Id,
                amount.Value.Abs(),
                NewFrequency.Frequency,
                DateOnly.FromDateTime(start),
                NewEndDate is { } end ? DateOnly.FromDateTime(end) : null));
        }
        catch (DomainValidationException ex)
        {
            RuleError = Loc.Error(ex.Code);
            return;
        }

        IsRuleFormOpen = false;
        StatusMessage = Loc.T("Planning_RuleSaved");
    }

    private async Task EndRuleAsync(RuleRowViewModel row)
    {
        await _recurring.EndRuleAsync(row.Rule.Id);
        StatusMessage = Loc.Format("Planning_RuleEnded", row.Name);
    }

    private async Task ActAsync(PlannedRowViewModel row, PlannedAction action)
    {
        row.Error = null;
        StatusMessage = null;
        var o = row.Occurrence;
        try
        {
            switch (action)
            {
                case PlannedAction.Confirm:
                    var amount = AmountParser.Parse(row.EditAmountText);
                    if (!amount.Success || !amount.Value.IsPositive || row.Date is not { } date)
                    {
                        row.Error = Loc.Error(row.Date is null ? "date.required" : "amount.invalid");
                        return;
                    }

                    await _recurring.ConfirmAsync(o.Rule.Id, o.OccurrenceDate, DateOnly.FromDateTime(date), amount.Value.Abs());
                    StatusMessage = Loc.Format("Planning_Confirmed", o.Rule.Name);
                    break;
                case PlannedAction.Link when o.SuggestedMatch is { } match:
                    await _recurring.LinkAsync(o.Rule.Id, o.OccurrenceDate, match.Id);
                    StatusMessage = Loc.Format("Planning_Linked", o.Rule.Name);
                    break;
                case PlannedAction.Postpone:
                    if (row.Date is not { } later || DateOnly.FromDateTime(later) <= o.DueDate)
                    {
                        row.Error = Loc.T("Planning_PostponePickDate");
                        return;
                    }

                    await _recurring.PostponeAsync(o.Rule.Id, o.OccurrenceDate, DateOnly.FromDateTime(later));
                    StatusMessage = Loc.Format("Planning_Postponed", o.Rule.Name, LedgerText.Date(DateOnly.FromDateTime(later), _clock.Today));
                    break;
                case PlannedAction.Skip:
                    await _recurring.SkipAsync(o.Rule.Id, o.OccurrenceDate);
                    StatusMessage = Loc.Format("Planning_Skipped", o.Rule.Name);
                    break;
            }
        }
        catch (DomainValidationException ex)
        {
            row.Error = Loc.Error(ex.Code);
        }
    }

    private async Task ReloadAsync()
    {
        var today = _clock.Today;
        _accountList = await _accounts.ListAsync();
        _categoryList = await _categories.ListAsync();
        var names = (await _accounts.ListAsync(includeArchived: true)).ToDictionary(a => a.Id, a => a.Name);

        await LoadSettingsAsync();
        await LoadSafeToSpendAsync(today);

        var flows = await _forecast.CashFlowAsync(CashFlowDays);
        CashFlows.Clear();
        foreach (var f in flows)
        {
            CashFlows.Add(new CashFlowViewModel(Loc.Format("Planning_NextDays", f.Days), LedgerText.Amount(f.ExpectedIn), LedgerText.Amount(f.ExpectedOut), LedgerText.Amount(f.Net)));
        }

        var open = await _recurring.ListOpenAsync(today.AddDays(30));
        Planned.Clear();
        foreach (var o in open)
        {
            Planned.Add(new PlannedRowViewModel(o, RuleAccounts(o.Rule, names), today, ActAsync));
        }

        if (Planned.Count > 0)
        {
            Planned[^1].IsLast = true;
        }

        Cards.Clear();
        foreach (var status in await _cards.ListStatusesAsync())
        {
            Cards.Add(new CardStatusViewModel(status, today));
        }

        var next = (await _recurring.ListOpenAsync(today.AddDays(400), withMatches: false))
            .Where(o => o.DueDate >= today)
            .GroupBy(o => o.Rule.Id)
            .ToDictionary(g => g.Key, g => g.First().DueDate);
        Rules.Clear();
        foreach (var rule in await _recurring.ListRulesAsync())
        {
            var detail = $"{Loc.T("RecurringKind_" + rule.Kind)} · {Loc.T("Frequency_" + rule.Frequency)} · {RuleAccounts(rule, names)}";
            var nextText = next.TryGetValue(rule.Id, out var d) ? Loc.Format("Planning_NextOn", LedgerText.Date(d, today)) : null;
            Rules.Add(new RuleRowViewModel(rule, detail, nextText, EndRuleAsync));
        }

        if (Rules.Count > 0)
        {
            Rules[^1].IsLast = true;
        }

        AccountOptions = [.. _accountList.Select(a => new AccountOption(a.Id, a.Name))];
        UpdateCategoryOptions();
        OnPropertyChanged(nameof(HasPlanned));
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(HasRules));
    }

    private async Task LoadSettingsAsync()
    {
        _loading = true;
        var buffer = await _settings.GetMinimumBufferAsync();
        BufferText = buffer.IsZero ? string.Empty : buffer.ToEuros().ToString("0.00", Loc.Instance.Culture);
        var none = new AccountOption(Guid.Empty, Loc.T("Planning_RoundingNone"));
        RoundingOptions = [none, .. _accountList.Where(a => a.Type == AccountType.Savings).Select(a => new AccountOption(a.Id, a.Name))];
        var chosen = await _settings.GetRoundingAccountIdAsync();
        RoundingAccount = RoundingOptions.FirstOrDefault(o => o.Id == chosen) ?? none;
        _loading = false;
    }

    private async Task LoadSafeToSpendAsync(DateOnly today)
    {
        var safe = await _forecast.SafeToSpendAsync();
        CanCalculate = safe.CanCalculate;
        SafeAmountText = LedgerText.Amount(safe.Amount);
        SafeIsNegative = safe.Amount.IsNegative;
        SafeUntilText = safe.NextIncome is { } income
            ? Loc.Format("Planning_SafeUntilIncome", LedgerText.Date(safe.Until, today), income.Rule.Name, LedgerText.Date(income.DueDate, today))
            : Loc.Format("Planning_SafeUntil", LedgerText.Date(safe.Until, today));

        SafeLines.Clear();
        foreach (var line in safe.Lines)
        {
            var label = line.Kind switch
            {
                ForecastLineKind.Balance => Loc.Format("Forecast_Balance", string.Join(", ", safe.Accounts.Select(a => a.Name))),
                ForecastLineKind.RecordedLater => Loc.T("Forecast_RecordedLater"),
                ForecastLineKind.Buffer => Loc.T("Forecast_Buffer"),
                ForecastLineKind.CardSettlement => Loc.Format("Forecast_CardSettlement", line.Name, LedgerText.Date(line.Date!.Value, today)),
                _ => Loc.Format("Forecast_Planned", line.Name, LedgerText.Date(line.Date!.Value, today)),
            };
            SafeLines.Add(new ForecastLineViewModel(label, LedgerText.Amount(line.Amount), line.Amount.IsNegative));
        }

        Caveats.Clear();
        foreach (var caveat in safe.Caveats)
        {
            Caveats.Add(Loc.T("ForecastCaveat_" + caveat));
        }
    }

    private void UpdateCategoryOptions()
    {
        var kind = NewKind.Kind == RecurringKind.Income ? CategoryKind.Income : CategoryKind.Expense;
        CategoryOptions = [new CategoryOption(null, Loc.T("Editor_NoCategory")), .. _categoryList.Where(c => c.Kind == kind).Select(c => new CategoryOption(c.Id, c.Name))];
        NewCategory = CategoryOptions[0];
    }

    private static string RuleAccounts(RecurringRule rule, IReadOnlyDictionary<Guid, string> names) =>
        rule.ToAccountId is { } to
            ? $"{names.GetValueOrDefault(rule.AccountId)} → {names.GetValueOrDefault(to)}"
            : names.GetValueOrDefault(rule.AccountId) ?? string.Empty;

    private static List<KindOption> BuildKindOptions() =>
        [.. Enum.GetValues<RecurringKind>().Select(k => new KindOption(k, Loc.T("RecurringKind_" + k)))];

    private static List<FrequencyOption> BuildFrequencyOptions() =>
        [.. Enum.GetValues<RecurrenceFrequency>().Select(f => new FrequencyOption(f, Loc.T("Frequency_" + f)))];

    protected override void OnLanguageChanged()
    {
        var kind = NewKind.Kind;
        var frequency = NewFrequency.Frequency;
        KindOptions = BuildKindOptions();
        FrequencyOptions = BuildFrequencyOptions();
        NewKind = KindOptions.First(k => k.Kind == kind);
        NewFrequency = FrequencyOptions.First(f => f.Frequency == frequency);
        OnPropertyChanged(nameof(AccountLabel));
        StatusMessage = null;
        if (_active)
        {
            _ = ReloadAsync();
        }
    }
}
