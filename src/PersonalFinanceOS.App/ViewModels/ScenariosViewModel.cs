using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Scenarios;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Scenarios;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>A saved scenario, or the baseline alone (<see cref="Id"/> null).</summary>
public sealed record ScenarioOption(Guid? Id, string Name);

public sealed record ChangeKindOption(ScenarioChangeKind Kind, string Name)
{
    public static IReadOnlyList<ChangeKindOption> All() =>
        Enum.GetValues<ScenarioChangeKind>().Select(k => new ChangeKindOption(k, Loc.T("ScenarioKind_" + k))).ToList();
}

public sealed record DirectionOption(bool IsIncome, string Name)
{
    public static IReadOnlyList<DirectionOption> All() =>
        [new(false, Loc.T("Scenario_DirectionOut")), new(true, Loc.T("Scenario_DirectionIn"))];
}

/// <summary>A wishlist item to buy in a scenario, or another purchase (<see cref="Id"/> null).</summary>
public sealed record ScenarioItemOption(Guid? Id, string Name, Money? Price);

/// <summary>What the month table shows: the spendable accounts together (<see cref="AccountId"/> null) or one account.</summary>
public sealed record ProjectionOption(Guid? AccountId, string Name);

/// <summary>One change of the selected scenario, with what it does.</summary>
public sealed partial class ScenarioChangeRowViewModel(string title, string detail, string? effect, string? note, Func<ScenarioChangeRowViewModel, Task> remove) : ViewModelBase
{
    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public string? Effect { get; } = effect;

    public string? Note { get; } = note;

    public bool IsLast { get; set; }

    public Guid ChangeId { get; init; }

    [RelayCommand]
    private Task Remove() => remove(this);
}

/// <summary>One month of the forecast. Warnings carry words and a sign, never colour alone.</summary>
public sealed record MonthRowViewModel(string Month, string Baseline, string Scenario, string Difference, string Lowest, string? Warning, bool DifferenceIsNegative, bool LowestIsBad, bool HasScenario);

/// <summary>The expected spendable balance a number of days ahead.</summary>
public sealed record HorizonViewModel(string Title, string Baseline, string Scenario, string Difference, bool DifferenceIsNegative, bool HasScenario)
{
    public string BaselineCaption => Loc.Format("Scenario_BaselineValue", Baseline);
}

/// <summary>
/// What-if scenarios (spec §16, §24 phase 7): renovation costs, purchase timing and payment method, salary, recurring
/// costs, joint transfers and wishlist purchases, shown next to the baseline month by month for a year, at 7/30/90 days,
/// per account, with shortfall warnings and the effect on the wishlist. A scenario never changes the ledger.
/// </summary>
public sealed partial class ScenariosViewModel : PageViewModel
{
    private readonly ScenarioService _scenarios;
    private readonly AccountService _accounts;
    private readonly RecurringService _recurring;
    private readonly WishlistService _wishlist;
    private readonly IClock _clock;
    private bool _active;
    private bool _loading;
    private ScenarioResult? _result;
    private Dictionary<Guid, Account> _allAccounts = [];

    public ScenariosViewModel(
        ScenarioService scenarios,
        AccountService accounts,
        RecurringService recurring,
        WishlistService wishlist,
        LedgerService ledger,
        CreditCardService cards,
        IClock clock)
    {
        _scenarios = scenarios;
        _accounts = accounts;
        _recurring = recurring;
        _wishlist = wishlist;
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
        wishlist.Changed += Refresh;
        scenarios.Changed += Refresh;
        _changeKinds = ChangeKindOption.All();
        _changeKind = _changeKinds[0];
        _directions = DirectionOption.All();
        _direction = _directions[0];
    }

    // ----- Choosing a scenario -----

    [ObservableProperty]
    private IReadOnlyList<ScenarioOption> _scenarioOptions = [];

    [ObservableProperty]
    private ScenarioOption? _selectedScenario;

    public bool HasScenario => SelectedScenario?.Id is not null;

    public string? ScenarioNote { get; private set; }

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isNameFormOpen;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private string? _formNote;

    [ObservableProperty]
    private string? _nameError;

    [ObservableProperty]
    private bool _isDeleteConfirmOpen;

    public string NameFormTitle => Loc.T(IsRenaming ? "Scenario_Rename" : "Scenario_New");

    public ObservableCollection<ScenarioChangeRowViewModel> Changes { get; } = [];

    public bool HasChanges => Changes.Count > 0;

    // ----- Adding a change -----

    [ObservableProperty]
    private bool _isChangeFormOpen;

    [ObservableProperty]
    private IReadOnlyList<ChangeKindOption> _changeKinds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAmountChange), nameof(IsMonthly), nameof(IsPlanned), nameof(IsPurchase), nameof(NeedsName), nameof(AmountLabel), nameof(DateLabel))]
    private ChangeKindOption _changeKind;

    public bool IsAmountChange => ChangeKind.Kind is ScenarioChangeKind.OneOff or ScenarioChangeKind.Monthly;

    public bool IsMonthly => ChangeKind.Kind == ScenarioChangeKind.Monthly;

    public bool IsPlanned => ChangeKind.Kind == ScenarioChangeKind.ChangePlanned;

    public bool IsPurchase => ChangeKind.Kind == ScenarioChangeKind.Purchase;

    public bool NeedsName => !IsPlanned;

    public string AmountLabel => Loc.T(IsPlanned ? "Scenario_NewAmount" : IsPurchase ? "Wishlist_Price" : "Scenario_Amount");

    public string DateLabel => Loc.T(IsPlanned || IsMonthly ? "Scenario_From" : "Scenario_Date");

    [ObservableProperty]
    private IReadOnlyList<DirectionOption> _directions;

    [ObservableProperty]
    private DirectionOption _direction;

    [ObservableProperty]
    private string _changeName = string.Empty;

    [ObservableProperty]
    private string _changeAmountText = string.Empty;

    [ObservableProperty]
    private DateTime? _changeDate;

    [ObservableProperty]
    private DateTime? _changeEndDate;

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _moneyAccounts = [];

    [ObservableProperty]
    private AccountOption? _changeAccount;

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _plannedItems = [];

    [ObservableProperty]
    private AccountOption? _changeRule;

    [ObservableProperty]
    private IReadOnlyList<ScenarioItemOption> _wishlistItems = [];

    [ObservableProperty]
    private ScenarioItemOption? _changeItem;

    /// <summary>Accounts and cards a purchase can be paid with.</summary>
    [ObservableProperty]
    private IReadOnlyList<AccountOption> _payOptions = [];

    [ObservableProperty]
    private AccountOption? _changePay;

    [ObservableProperty]
    private string? _changeError;

    // ----- The result -----

    [ObservableProperty]
    private bool _canCalculate = true;

    public ObservableCollection<HorizonViewModel> Horizons { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    private IReadOnlyList<ProjectionOption> _projections = [];

    [ObservableProperty]
    private ProjectionOption? _selectedProjection;

    public ObservableCollection<MonthRowViewModel> Months { get; } = [];

    public ObservableCollection<string> WishlistEffects { get; } = [];

    public bool HasWishlistEffects => WishlistEffects.Count > 0;

    public ObservableCollection<string> Assumptions { get; } = [];

    public string MonthsTitle => Loc.Format("Scenario_MonthsTitle", ScenarioMath.MonthsShown);

    public override Task ActivateAsync()
    {
        _active = true;
        return ReloadAsync();
    }

    /// <summary>Shows a saved scenario, for example one the assistant just saved.</summary>
    public async Task SelectAsync(Guid scenarioId)
    {
        await ReloadAsync();
        SelectedScenario = ScenarioOptions.FirstOrDefault(o => o.Id == scenarioId) ?? SelectedScenario;
    }

    partial void OnSelectedScenarioChanged(ScenarioOption? value)
    {
        OnPropertyChanged(nameof(HasScenario));
        IsDeleteConfirmOpen = false;
        IsChangeFormOpen = false;
        if (!_loading)
        {
            StatusMessage = null;
            _ = LoadResultAsync();
        }
    }

    partial void OnSelectedProjectionChanged(ProjectionOption? value) => FillMonths();

    partial void OnChangeItemChanged(ScenarioItemOption? value)
    {
        if (value is { Id: not null, Price: { } price })
        {
            ChangeName = value.Name;
            ChangeAmountText = AmountText(price);
        }
    }

    partial void OnIsRenamingChanged(bool value) => OnPropertyChanged(nameof(NameFormTitle));

    [RelayCommand]
    private void OpenNew()
    {
        IsRenaming = false;
        FormName = string.Empty;
        FormNote = null;
        NameError = null;
        IsNameFormOpen = true;
    }

    [RelayCommand]
    private async Task OpenRename()
    {
        if (SelectedScenario?.Id is not { } id)
        {
            return;
        }

        var scenario = (await _scenarios.ListAsync()).FirstOrDefault(s => s.Id == id);
        IsRenaming = true;
        FormName = scenario?.Name ?? SelectedScenario.Name;
        FormNote = scenario?.Note;
        NameError = null;
        IsNameFormOpen = true;
    }

    [RelayCommand]
    private void CancelName() => IsNameFormOpen = false;

    [RelayCommand]
    private async Task SaveName()
    {
        NameError = null;
        Guid id;
        try
        {
            if (IsRenaming && SelectedScenario?.Id is { } existing)
            {
                await _scenarios.RenameAsync(existing, FormName, FormNote);
                id = existing;
            }
            else
            {
                id = (await _scenarios.CreateAsync(FormName, FormNote)).Id;
            }
        }
        catch (DomainValidationException ex)
        {
            NameError = Loc.Error(ex.Code);
            return;
        }

        IsNameFormOpen = false;
        await SelectAsync(id);
        StatusMessage = Loc.Format("Scenario_Saved", FormName.Trim());
        if (!IsRenaming)
        {
            await OpenChangeForm();
        }
    }

    [RelayCommand]
    private void AskDelete() => IsDeleteConfirmOpen = true;

    [RelayCommand]
    private void CancelDelete() => IsDeleteConfirmOpen = false;

    [RelayCommand]
    private async Task ConfirmDelete()
    {
        if (SelectedScenario is not { Id: { } id } option)
        {
            return;
        }

        IsDeleteConfirmOpen = false;
        await _scenarios.RemoveAsync(id);
        SelectedScenario = null;
        await ReloadAsync();
        StatusMessage = Loc.Format("Scenario_Removed", option.Name);
    }

    [RelayCommand]
    private async Task OpenChangeForm()
    {
        var today = _clock.Today;
        var accounts = await _accounts.ListAsync();
        var money = accounts.Where(ForecastService.Projects).ToList();
        MoneyAccounts = [.. money.Select(a => new AccountOption(a.Id, a.Name))];
        ChangeAccount = MoneyAccounts.FirstOrDefault(o => money.First(a => a.Id == o.Id).Type == AccountType.PersonalCurrent) ?? MoneyAccounts.FirstOrDefault();
        PayOptions = [.. MoneyAccounts, .. accounts.Where(a => a.Type.IsLiability()).Select(a => new AccountOption(a.Id, a.Name))];
        ChangePay = ChangeAccount;
        var rules = await _recurring.ListRulesAsync();
        PlannedItems = [.. rules.Select(r => new AccountOption(r.Id, Loc.Format("Scenario_PlannedOption", r.Name, LedgerText.Amount(r.Amount), Loc.T("Frequency_" + r.Frequency))))];
        ChangeRule = PlannedItems.FirstOrDefault();
        var items = (await _wishlist.ListAsync()).Where(e => e.IsOpen).Select(e => e.Item).ToList();
        WishlistItems = [new ScenarioItemOption(null, Loc.T("Scenario_OtherPurchase"), null), .. items.Select(i => new ScenarioItemOption(i.Id, i.Name, i.ExpectedPrice))];
        ChangeItem = WishlistItems[0];
        Direction = Directions[0];
        ChangeName = string.Empty;
        ChangeAmountText = string.Empty;
        ChangeDate = today.ToDateTime(TimeOnly.MinValue);
        ChangeEndDate = null;
        ChangeError = null;
        IsChangeFormOpen = true;
    }

    [RelayCommand]
    private void CancelChange() => IsChangeFormOpen = false;

    [RelayCommand]
    private async Task SaveChange()
    {
        ChangeError = null;
        if (SelectedScenario?.Id is not { } scenarioId)
        {
            return;
        }

        var amount = AmountParser.Parse(ChangeAmountText);
        if (!amount.Success || amount.Value.IsNegative || (!IsPlanned && amount.Value.IsZero))
        {
            ChangeError = Loc.Error(IsPlanned ? "scenario.amount.negative" : "transaction.amount.zero");
            return;
        }

        var date = ChangeDate is { } d ? DateOnly.FromDateTime(d) : _clock.Today;
        var payIsCard = ChangePay is { } pay && !MoneyAccounts.Any(m => m.Id == pay.Id);
        var request = ChangeKind.Kind switch
        {
            ScenarioChangeKind.OneOff => new ScenarioChangeRequest(ScenarioChangeKind.OneOff, amount.Value, date, ChangeName, Direction.IsIncome, AccountId: ChangeAccount?.Id),
            ScenarioChangeKind.Monthly => new ScenarioChangeRequest(
                ScenarioChangeKind.Monthly, amount.Value, date, ChangeName, Direction.IsIncome, ChangeEndDate is { } e ? DateOnly.FromDateTime(e) : null, ChangeAccount?.Id),
            ScenarioChangeKind.ChangePlanned => new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, amount.Value, date, RuleId: ChangeRule?.Id),
            _ => new ScenarioChangeRequest(
                ScenarioChangeKind.Purchase, amount.Value, date, ChangeName, AccountId: payIsCard ? null : ChangePay?.Id, CardId: payIsCard ? ChangePay?.Id : null, WishlistItemId: ChangeItem?.Id),
        };

        try
        {
            await _scenarios.AddChangeAsync(scenarioId, request);
        }
        catch (DomainValidationException ex)
        {
            ChangeError = Loc.Error(ex.Code);
            return;
        }

        IsChangeFormOpen = false;
        await LoadResultAsync();
    }

    private async Task RemoveChangeAsync(ScenarioChangeRowViewModel row)
    {
        if (SelectedScenario?.Id is { } id)
        {
            await _scenarios.RemoveChangeAsync(id, row.ChangeId);
            await LoadResultAsync();
        }
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            var selected = SelectedScenario?.Id;
            var list = await _scenarios.ListAsync();
            ScenarioOptions = [new ScenarioOption(null, Loc.T("Scenario_BaselineOnly")), .. list.Select(s => new ScenarioOption(s.Id, s.Name))];
            SelectedScenario = ScenarioOptions.FirstOrDefault(o => o.Id == selected) ?? ScenarioOptions[0];
            ScenarioNote = list.FirstOrDefault(s => s.Id == SelectedScenario.Id)?.Note;
            OnPropertyChanged(nameof(ScenarioNote));
        }
        finally
        {
            _loading = false;
        }

        await LoadResultAsync();
    }

    private async Task LoadResultAsync()
    {
        var today = _clock.Today;
        _allAccounts = (await _accounts.ListAsync(includeArchived: true)).ToDictionary(a => a.Id);
        var result = await _scenarios.EvaluateAsync(SelectedScenario?.Id);
        _result = result;
        CanCalculate = result.CanCalculate;
        var rules = (await _recurring.ListRulesAsync(includeEnded: true)).ToDictionary(r => r.Id);

        Changes.Clear();
        foreach (var applied in result.Changes)
        {
            Changes.Add(Row(applied, rules, today));
        }

        if (Changes.Count > 0)
        {
            Changes[^1].IsLast = true;
        }

        OnPropertyChanged(nameof(HasChanges));

        Horizons.Clear();
        foreach (var h in result.Horizons)
        {
            Horizons.Add(new HorizonViewModel(
                Loc.Format("Scenario_InDays", h.Days, LedgerText.Date(h.Date, today)),
                LedgerText.Amount(h.Baseline),
                LedgerText.Amount(h.Scenario),
                Signed(h.Difference),
                h.Difference.IsNegative,
                HasScenario));
        }

        Warnings.Clear();
        var shown = HasScenario ? result.ScenarioShortfalls : result.BaselineShortfalls;
        foreach (var s in shown)
        {
            var isNew = HasScenario && !result.BaselineShortfalls.Any(b => b.Account?.Id == s.Account?.Id && b.Kind == s.Kind);
            var text = s.Account is { } account
                ? Loc.Format("Scenario_ShortAccount", account.Name, LedgerText.Date(s.Date, today), LedgerText.Amount(s.Balance))
                : Loc.Format(s.Kind == ShortfallKind.BelowZero ? "Scenario_ShortZero" : "Scenario_ShortBuffer", LedgerText.Date(s.Date, today), LedgerText.Amount(s.Balance), LedgerText.Amount(result.Baseline.Buffer));
            Warnings.Add(isNew ? $"{text} {Loc.T("Scenario_OnlyInScenario")}" : text);
        }

        OnPropertyChanged(nameof(HasWarnings));

        var selectedAccount = SelectedProjection?.AccountId;
        Projections =
        [
            new ProjectionOption(null, Loc.T("Scenario_Spendable")),
            .. result.Baseline.Accounts.Select(a => new ProjectionOption(a.Id, a.Name)),
        ];
        _loading = true;
        SelectedProjection = Projections.FirstOrDefault(p => p.AccountId == selectedAccount) ?? Projections[0];
        _loading = false;
        FillMonths();

        WishlistEffects.Clear();
        foreach (var effect in result.Wishlist)
        {
            var before = effect.Before is { } b ? LedgerText.Date(b, today) : Loc.T("Effect_NoDate");
            var after = effect.After is { } a ? LedgerText.Date(a, today) : Loc.T("Effect_NoDate");
            WishlistEffects.Add(HasScenario && effect.IsDelayed
                ? Loc.Format("Effect_Delayed", effect.Item.Name, before, after)
                : Loc.Format("Scenario_WishFrom", effect.Item.Name, before));
        }

        OnPropertyChanged(nameof(HasWishlistEffects));

        Assumptions.Clear();
        Assumptions.Add(Loc.T("Scenario_Assume_NotLedger"));
        Assumptions.Add(Loc.T("Scenario_Assume_Income"));
        Assumptions.Add(Loc.T("Scenario_Assume_Transfers"));
        foreach (var caveat in result.Baseline.Caveats)
        {
            Assumptions.Add(Loc.T("ForecastCaveat_" + caveat));
        }
    }

    private void FillMonths()
    {
        if (_loading || _result is not { } result)
        {
            return;
        }

        var spendable = SelectedProjection?.AccountId is null;
        var buffer = spendable ? result.Baseline.Buffer : Money.Zero;
        Months.Clear();
        foreach (var m in result.Months(SelectedProjection?.AccountId))
        {
            var lowest = HasScenario ? m.ScenarioLowest : m.BaselineLowest;
            var warning = lowest.IsNegative ? Loc.T("Scenario_BelowZero")
                : spendable && buffer.IsPositive && lowest < buffer ? Loc.T("Scenario_BelowBuffer")
                : null;
            Months.Add(new MonthRowViewModel(
                m.Month.ToString("MMM yyyy", Loc.Instance.Culture),
                LedgerText.Amount(m.Baseline),
                LedgerText.Amount(m.Scenario),
                Signed(m.Difference),
                LedgerText.Amount(lowest),
                warning is null ? null : $"⚠ {warning}",
                m.Difference.IsNegative,
                warning is not null,
                HasScenario));
        }
    }

    private ScenarioChangeRowViewModel Row(AppliedChange applied, IReadOnlyDictionary<Guid, RecurringRule> rules, DateOnly today)
    {
        var c = applied.Change;
        string Name(Guid? id) => id is { } x && _allAccounts.TryGetValue(x, out var a) ? a.Name : "?";
        var amount = LedgerText.Amount(c.Amount);
        var date = LedgerText.Date(c.Date, today);
        var detail = c.Kind switch
        {
            ScenarioChangeKind.OneOff => Loc.Format(c.IsIncome ? "Scenario_Change_OneOffIn" : "Scenario_Change_OneOffOut", amount, Name(c.AccountId), date),
            ScenarioChangeKind.Monthly => Loc.Format(c.IsIncome ? "Scenario_Change_MonthlyIn" : "Scenario_Change_MonthlyOut", amount, Name(c.AccountId), date)
                + (c.EndDate is { } end ? " " + Loc.Format("Scenario_Change_Until", LedgerText.Date(end, today)) : string.Empty),
            ScenarioChangeKind.ChangePlanned when c.Amount.IsZero => Loc.Format("Scenario_Change_PlannedStops", date),
            ScenarioChangeKind.ChangePlanned => Loc.Format(
                "Scenario_Change_Planned", amount, rules.TryGetValue(c.RuleId!.Value, out var rule) ? LedgerText.Amount(rule.Amount) : "?", date),
            _ when c.CardId is not null => Loc.Format(
                "Scenario_Change_PurchaseCard", amount, Name(c.CardId), date, applied.PaidOn is { } paid ? LedgerText.Date(paid, today) : "?"),
            _ => Loc.Format("Scenario_Change_PurchaseAccount", amount, Name(c.AccountId), date),
        };

        string? effect = null;
        if (!applied.SpendableEffect.IsZero || !applied.OtherEffect.IsZero)
        {
            effect = Loc.Format("Scenario_Effect", Signed(applied.SpendableEffect));
            if (!applied.OtherEffect.IsZero)
            {
                effect += " · " + Loc.Format("Scenario_EffectOther", Signed(applied.OtherEffect));
            }
        }

        var note = applied.Note == Application.Scenarios.ScenarioNote.None ? null : Loc.T("ScenarioNote_" + applied.Note);
        return new ScenarioChangeRowViewModel(Loc.Format("ScenarioKindTitle_" + c.Kind, applied.Name), detail, effect, note, RemoveChangeAsync) { ChangeId = c.Id };
    }

    private static string Signed(Money m) => m.IsPositive ? "+" + LedgerText.Amount(m) : LedgerText.Amount(m);

    private static string AmountText(Money m) => m.ToEuros().ToString("0.00", Loc.Instance.Culture);

    protected override void OnLanguageChanged()
    {
        var kind = ChangeKind?.Kind ?? ScenarioChangeKind.OneOff;
        var income = Direction?.IsIncome ?? false;
        ChangeKinds = ChangeKindOption.All();
        ChangeKind = ChangeKinds.First(k => k.Kind == kind);
        Directions = DirectionOption.All();
        Direction = Directions.First(d => d.IsIncome == income);
        OnPropertyChanged(nameof(NameFormTitle));
        OnPropertyChanged(nameof(MonthsTitle));
        IsChangeFormOpen = false;
        StatusMessage = null;
        if (_active)
        {
            _ = ReloadAsync();
        }
    }
}
