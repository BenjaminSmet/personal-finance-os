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
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record PriorityOption(WishlistPriority Priority, string Name)
{
    public static IReadOnlyList<PriorityOption> All() =>
        Enum.GetValues<WishlistPriority>().Select(p => new PriorityOption(p, Loc.T("Priority_" + p))).ToList();
}

/// <summary>One wishlist item with its short affordability answer.</summary>
public sealed partial class WishlistRowViewModel(
    WishlistEntry entry,
    AffordabilitySummary? summary,
    DateOnly today,
    Func<WishlistRowViewModel, Task> open,
    Func<WishlistRowViewModel, Task> edit) : ViewModelBase
{
    public WishlistEntry Entry { get; } = entry;

    public WishlistItem Item => Entry.Item;

    public string Name => Item.Name;

    public string PriceText => LedgerText.Amount(Item.ExpectedPrice);

    public string PriorityText => Loc.T("Priority_" + Item.Priority);

    public bool IsHighPriority => Item.Priority == WishlistPriority.High;

    public bool IsOpen => Entry.IsOpen;

    public string StatusText => Loc.T("WishlistStatus_" + Entry.Status);

    /// <summary>"Affordable" is calculated, never stored (spec §14), so it is shown next to the status.</summary>
    public bool IsAffordable => IsOpen && summary?.Verdict == AffordabilityVerdict.Prudent;

    public string? VerdictText => !IsOpen || summary is null ? null : summary.Verdict switch
    {
        AffordabilityVerdict.Prudent => Loc.T("Wishlist_RowPrudent"),
        AffordabilityVerdict.CannotCalculate => Loc.T("Wishlist_RowUnknown"),
        _ when summary.EarliestPrudent is { } d => Loc.Format(summary.Verdict == AffordabilityVerdict.TechnicallyOnly ? "Wishlist_RowTechnicalFrom" : "Wishlist_RowFrom", LedgerText.Date(d, today)),
        _ => Loc.T("Wishlist_RowNoDate"),
    };

    public string? DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Item.TargetDate is { } target)
            {
                parts.Add(Loc.Format("Wishlist_Target", LedgerText.Date(target, today)));
            }

            if (Item.Status == WishlistStatus.Purchased && !Entry.PurchaseRemoved && Item.PurchasedOn is { } on)
            {
                parts.Add(Loc.Format("Wishlist_BoughtOn", LedgerText.Date(on, today)));
            }

            if (Entry.PurchaseRemoved)
            {
                parts.Add(Loc.T("Wishlist_PurchaseRemoved"));
            }

            if (Item.Alternative is { } alternative)
            {
                parts.Add(Loc.Format("Wishlist_AlternativeLine", alternative));
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    public string? LinkText => Item.Url ?? Item.Note;

    [ObservableProperty]
    private bool _isSelected;

    public bool IsLast { get; set; }

    [RelayCommand]
    private Task Open() => open(this);

    [RelayCommand]
    private Task Edit() => edit(this);
}

/// <summary>One line of the affordability check. <see cref="Ok"/> is shown as ✓ or ✕ next to the words, never by colour alone.</summary>
public sealed record CheckLineViewModel(string Label, string Value, bool? Ok = null)
{
    public string Mark => Ok switch { true => "✓", false => "✕", _ => string.Empty };

    public bool IsBad => Ok == false;
}

/// <summary>One way to pay, with its own verdict.</summary>
public sealed record PaymentOptionViewModel(string Title, string VerdictText, string? Detail, bool IsPrudent, bool IsBad);

/// <summary>
/// The wishlist and purchase planner (spec §14, §24 phase 6): what the user wants, whether it is affordable now and
/// prudent, the earliest comfortable date, current account versus card versus waiting, the effect on the other items,
/// and buying it with one confirmed expense. Every number comes from <see cref="AffordabilityService"/>.
/// </summary>
public sealed partial class WishlistViewModel : PageViewModel
{
    private readonly WishlistService _wishlist;
    private readonly AffordabilityService _affordability;
    private readonly AccountService _accounts;
    private readonly CategoryService _categories;
    private readonly IClock _clock;
    private bool _active;
    private IReadOnlyList<WishlistEntry> _entries = [];

    public WishlistViewModel(
        WishlistService wishlist,
        AffordabilityService affordability,
        AccountService accounts,
        CategoryService categories,
        LedgerService ledger,
        RecurringService recurring,
        CreditCardService cards,
        IClock clock)
    {
        _wishlist = wishlist;
        _affordability = affordability;
        _accounts = accounts;
        _categories = categories;
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
        _priorityOptions = PriorityOption.All();
        _formPriority = _priorityOptions[1];
    }

    public ObservableCollection<WishlistRowViewModel> Items { get; } = [];

    public ObservableCollection<WishlistRowViewModel> ClosedItems { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool HasClosed => ClosedItems.Count > 0;

    [ObservableProperty]
    private string? _statusMessage;

    // ----- Quick check: "can I afford this amount?" -----

    [ObservableProperty]
    private string _quickPriceText = string.Empty;

    [ObservableProperty]
    private string? _quickError;

    // ----- The item form -----

    [ObservableProperty]
    private bool _isFormOpen;

    [ObservableProperty]
    private Guid? _editingId;

    public string FormTitle => Loc.T(EditingId is null ? "Wishlist_NewItem" : "Wishlist_EditItem");

    [ObservableProperty]
    private IReadOnlyList<PriorityOption> _priorityOptions;

    [ObservableProperty]
    private PriorityOption _formPriority;

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private string _formPriceText = string.Empty;

    [ObservableProperty]
    private DateTime? _formTargetDate;

    [ObservableProperty]
    private string _formSavingText = string.Empty;

    [ObservableProperty]
    private string? _formUrl;

    [ObservableProperty]
    private string? _formNote;

    [ObservableProperty]
    private string? _formAlternative;

    [ObservableProperty]
    private string? _formError;

    // ----- The affordability answer -----

    [ObservableProperty]
    private bool _hasCheck;

    [ObservableProperty]
    private WishlistRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _checkTitle = string.Empty;

    [ObservableProperty]
    private string _verdictTitle = string.Empty;

    [ObservableProperty]
    private string _verdictExplanation = string.Empty;

    [ObservableProperty]
    private bool _verdictIsPrudent;

    [ObservableProperty]
    private bool _verdictIsBad;

    [ObservableProperty]
    private string? _savingPlanText;

    public ObservableCollection<CheckLineViewModel> CheckLines { get; } = [];

    public ObservableCollection<PaymentOptionViewModel> PaymentOptions { get; } = [];

    public ObservableCollection<string> Effects { get; } = [];

    public ObservableCollection<string> Caveats { get; } = [];

    public bool HasOptions => PaymentOptions.Count > 0;

    public bool HasEffects => Effects.Count > 0;

    public bool HasCaveats => Caveats.Count > 0;

    public bool CanBuy => SelectedRow is { IsOpen: true };

    public bool CanReopen => SelectedRow is { IsOpen: false } row && row.Entry.Status != WishlistStatus.Purchased;

    // ----- Buying -----

    [ObservableProperty]
    private bool _isPurchaseOpen;

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _purchaseAccounts = [];

    [ObservableProperty]
    private AccountOption? _purchaseAccount;

    [ObservableProperty]
    private IReadOnlyList<CategoryOption> _purchaseCategories = [];

    [ObservableProperty]
    private CategoryOption? _purchaseCategory;

    [ObservableProperty]
    private DateTime? _purchaseDate;

    [ObservableProperty]
    private string _purchaseAmountText = string.Empty;

    /// <summary>On by default; off when the expense is already in the ledger (typed in or imported), so it is not recorded twice.</summary>
    [ObservableProperty]
    private bool _recordTransaction = true;

    [ObservableProperty]
    private string? _purchaseError;

    public override Task ActivateAsync()
    {
        _active = true;
        return ReloadAsync();
    }

    [RelayCommand]
    private void OpenNew()
    {
        EditingId = null;
        FormName = string.Empty;
        FormPriceText = string.Empty;
        FormPriority = PriorityOptions[1];
        FormTargetDate = null;
        FormSavingText = string.Empty;
        FormUrl = null;
        FormNote = null;
        FormAlternative = null;
        FormError = null;
        StatusMessage = null;
        IsFormOpen = true;
        OnPropertyChanged(nameof(FormTitle));
    }

    [RelayCommand]
    private void CancelForm() => IsFormOpen = false;

    [RelayCommand]
    private async Task SaveItem()
    {
        FormError = null;
        var price = AmountParser.Parse(FormPriceText);
        if (!price.Success || !price.Value.IsPositive)
        {
            FormError = Loc.Error("wishlist.price.notPositive");
            return;
        }

        Money? saving = null;
        if (!string.IsNullOrWhiteSpace(FormSavingText))
        {
            var parsed = AmountParser.Parse(FormSavingText);
            if (!parsed.Success || !parsed.Value.IsPositive)
            {
                FormError = Loc.Error("wishlist.saving.notPositive");
                return;
            }

            saving = parsed.Value;
        }

        var request = new WishlistRequest(
            FormName, price.Value, FormPriority.Priority, FormTargetDate is { } t ? DateOnly.FromDateTime(t) : null, saving, FormUrl, FormNote, FormAlternative);
        try
        {
            if (EditingId is { } id)
            {
                await _wishlist.UpdateAsync(id, request);
            }
            else
            {
                var item = await _wishlist.CreateAsync(request);
                EditingId = item.Id;
            }
        }
        catch (DomainValidationException ex)
        {
            FormError = Loc.Error(ex.Code);
            return;
        }

        IsFormOpen = false;
        StatusMessage = Loc.Format("Wishlist_Saved", FormName.Trim());
        await ReloadAsync();
        if (Items.FirstOrDefault(r => r.Item.Id == EditingId) is { } row)
        {
            await ShowItemAsync(row);
        }
    }

    [RelayCommand]
    private async Task QuickCheck()
    {
        QuickError = null;
        var price = AmountParser.Parse(QuickPriceText);
        if (!price.Success || !price.Value.IsPositive)
        {
            QuickError = Loc.Error("amount.invalid");
            return;
        }

        Select(null);
        var check = await _affordability.CheckAsync(price.Value, null, OpenItems());
        Show(check, Loc.Format("Wishlist_QuickTitle", LedgerText.Amount(price.Value)), null);
    }

    [RelayCommand]
    private void CloseCheck()
    {
        HasCheck = false;
        IsPurchaseOpen = false;
        Select(null);
    }

    [RelayCommand]
    private Task MarkSaving() => SetStatusAsync(WishlistStatus.Saving);

    [RelayCommand]
    private Task Postpone() => SetStatusAsync(WishlistStatus.Postponed);

    [RelayCommand]
    private Task CancelItem() => SetStatusAsync(WishlistStatus.Cancelled);

    [RelayCommand]
    private Task Reopen() => SetStatusAsync(WishlistStatus.Desired);

    [RelayCommand]
    private async Task OpenPurchase()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        PurchaseError = null;
        var accounts = (await _accounts.ListAsync())
            .Where(a => !a.Type.IsManuallyValued())
            .ToList();
        PurchaseAccounts = [.. accounts.Select(a => new AccountOption(a.Id, a.Name))];
        PurchaseAccount = PurchaseAccounts.FirstOrDefault(o => accounts.First(a => a.Id == o.Id).Type == AccountType.PersonalCurrent) ?? PurchaseAccounts.FirstOrDefault();
        var categories = await _categories.ListAsync();
        PurchaseCategories = [new CategoryOption(null, Loc.T("Editor_NoCategory")), .. categories.Where(c => c.Kind == CategoryKind.Expense).Select(c => new CategoryOption(c.Id, c.Name))];
        PurchaseCategory = PurchaseCategories[0];
        PurchaseDate = _clock.Today.ToDateTime(TimeOnly.MinValue);
        PurchaseAmountText = row.Item.ExpectedPrice.ToEuros().ToString("0.00", Loc.Instance.Culture);
        RecordTransaction = true;
        IsPurchaseOpen = true;
    }

    [RelayCommand]
    private void CancelPurchase() => IsPurchaseOpen = false;

    [RelayCommand]
    private async Task ConfirmPurchase()
    {
        PurchaseError = null;
        if (SelectedRow is not { } row)
        {
            return;
        }

        var amount = AmountParser.Parse(PurchaseAmountText);
        if (!amount.Success || !amount.Value.IsPositive)
        {
            PurchaseError = Loc.Error("amount.invalid");
            return;
        }

        if (PurchaseDate is not { } date)
        {
            PurchaseError = Loc.Error("date.required");
            return;
        }

        if (RecordTransaction && PurchaseAccount is null)
        {
            PurchaseError = Loc.Error("account.notFound");
            return;
        }

        try
        {
            await _wishlist.PurchaseAsync(row.Item.Id, new PurchaseRequest(
                DateOnly.FromDateTime(date), amount.Value, RecordTransaction ? PurchaseAccount!.Id : null, RecordTransaction ? PurchaseCategory?.Id : null));
        }
        catch (DomainValidationException ex)
        {
            PurchaseError = Loc.Error(ex.Code);
            return;
        }

        IsPurchaseOpen = false;
        HasCheck = false;
        Select(null);
        StatusMessage = RecordTransaction
            ? Loc.Format("Wishlist_BoughtRecorded", row.Name, LedgerText.Amount(amount.Value), PurchaseAccount!.Name)
            : Loc.Format("Wishlist_BoughtMarked", row.Name);
    }

    private async Task SetStatusAsync(WishlistStatus status)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            await _wishlist.SetStatusAsync(row.Item.Id, status);
        }
        catch (DomainValidationException ex)
        {
            StatusMessage = null;
            PurchaseError = Loc.Error(ex.Code);
            return;
        }

        HasCheck = false;
        IsPurchaseOpen = false;
        Select(null);
        StatusMessage = Loc.Format("Wishlist_StatusSaved", row.Name, Loc.T("WishlistStatus_" + status));
    }

    private Task OpenRowAsync(WishlistRowViewModel row)
    {
        StatusMessage = null;
        return ShowItemAsync(row);
    }

    private async Task ShowItemAsync(WishlistRowViewModel row)
    {
        IsPurchaseOpen = false;
        PurchaseError = null;
        Select(row);
        if (row.IsOpen)
        {
            var check = await _affordability.CheckAsync(row.Item.ExpectedPrice, row.Item, OpenItems());
            Show(check, row.Name, row.Item);
        }
        else
        {
            ShowClosed(row);
        }
    }

    private Task EditRowAsync(WishlistRowViewModel row)
    {
        var item = row.Item;
        EditingId = item.Id;
        FormName = item.Name;
        FormPriceText = item.ExpectedPrice.ToEuros().ToString("0.00", Loc.Instance.Culture);
        FormPriority = PriorityOptions.First(p => p.Priority == item.Priority);
        FormTargetDate = item.TargetDate?.ToDateTime(TimeOnly.MinValue);
        FormSavingText = item.MonthlySaving is { } s ? s.ToEuros().ToString("0.00", Loc.Instance.Culture) : string.Empty;
        FormUrl = item.Url;
        FormNote = item.Note;
        FormAlternative = item.Alternative;
        FormError = null;
        StatusMessage = null;
        IsFormOpen = true;
        OnPropertyChanged(nameof(FormTitle));
        return Task.CompletedTask;
    }

    private void Select(WishlistRowViewModel? row)
    {
        foreach (var r in Items.Concat(ClosedItems))
        {
            r.IsSelected = r == row;
        }

        SelectedRow = row;
        OnPropertyChanged(nameof(CanBuy));
        OnPropertyChanged(nameof(CanReopen));
    }

    private void ShowClosed(WishlistRowViewModel row)
    {
        CheckTitle = row.Name;
        VerdictTitle = row.StatusText;
        VerdictExplanation = row.DetailText ?? string.Empty;
        VerdictIsPrudent = false;
        VerdictIsBad = false;
        SavingPlanText = null;
        CheckLines.Clear();
        PaymentOptions.Clear();
        Effects.Clear();
        Caveats.Clear();
        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(HasEffects));
        OnPropertyChanged(nameof(HasCaveats));
        HasCheck = true;
    }

    private void Show(Affordability a, string title, WishlistItem? item)
    {
        var today = a.Today;
        CheckTitle = title;
        VerdictTitle = Loc.T("Verdict_" + a.Verdict);
        VerdictIsPrudent = a.Verdict == AffordabilityVerdict.Prudent;
        VerdictIsBad = a.Verdict is AffordabilityVerdict.NotAffordable or AffordabilityVerdict.CannotCalculate;
        VerdictExplanation = a.Verdict switch
        {
            AffordabilityVerdict.Prudent => Loc.Format("Verdict_PrudentWhy", LedgerText.Date(a.LookAheadUntil, today)),
            AffordabilityVerdict.TechnicallyOnly when !a.BillsCovered => Loc.T("Verdict_TechnicalBills"),
            AffordabilityVerdict.TechnicallyOnly => Loc.T("Verdict_TechnicalBuffer"),
            AffordabilityVerdict.NotAffordable => Loc.T("Verdict_NotAffordableWhy"),
            _ => Loc.T("Verdict_CannotCalculateWhy"),
        };

        CheckLines.Clear();
        PaymentOptions.Clear();
        Effects.Clear();
        Caveats.Clear();
        SavingPlanText = null;
        if (a.CanCalculate)
        {
            CheckLines.Add(new CheckLineViewModel(Loc.T("Check_CashNow"), LedgerText.Amount(a.CashNow)));
            CheckLines.Add(new CheckLineViewModel(Loc.T("Check_CashAfter"), LedgerText.Amount(a.CashAfter), a.CashAfter.IsNegative ? false : null));
            CheckLines.Add(new CheckLineViewModel(
                Loc.Format("Check_Lowest", LedgerText.Date(a.LookAheadUntil, today)),
                Loc.Format("Check_LowestOn", LedgerText.Amount(a.LowestAfter), LedgerText.Date(a.LowestOn, today))));
            CheckLines.Add(new CheckLineViewModel(Loc.Format("Check_Buffer", LedgerText.Amount(a.Buffer)), Loc.T(a.BufferIntact ? "Check_Yes" : "Check_No"), a.BufferIntact));
            CheckLines.Add(new CheckLineViewModel(Loc.T("Check_Bills"), Loc.T(a.BillsCovered ? "Check_Yes" : "Check_No"), a.BillsCovered));
            CheckLines.Add(new CheckLineViewModel(
                Loc.T("Check_NextIncome"),
                a.NextIncome is { } income ? Loc.Format("Check_Expected", LedgerText.Date(income, today)) : Loc.T("Check_NoIncome")));
            CheckLines.Add(new CheckLineViewModel(
                Loc.T("Check_Earliest"),
                a.EarliestPrudent is { } earliest ? LedgerText.Date(earliest, today) : Loc.Format("Check_NoDate", AffordabilityService.ScanDays)));

            foreach (var option in a.Options)
            {
                PaymentOptions.Add(Option(option, today));
            }

            foreach (var effect in a.Effects.Where(e => e.IsDelayed))
            {
                Effects.Add(Loc.Format(
                    "Effect_Delayed",
                    effect.Item.Name,
                    effect.Before is { } b ? LedgerText.Date(b, today) : Loc.T("Effect_NoDate"),
                    effect.After is { } af ? LedgerText.Date(af, today) : Loc.T("Effect_NoDate")));
            }

            if (Effects.Count == 0 && a.Effects.Count > 0)
            {
                Effects.Add(Loc.T("Effect_None"));
            }
        }

        foreach (var caveat in a.Caveats)
        {
            Caveats.Add(Loc.T("ForecastCaveat_" + caveat));
        }

        if (item?.MonthlySaving is { } saving)
        {
            var months = (int)Math.Ceiling(item.ExpectedPrice.Cents / (double)saving.Cents);
            SavingPlanText = Loc.Format("Wishlist_SavingPlan", LedgerText.Amount(saving), months, today.AddMonths(months).ToString("MMMM yyyy", Loc.Instance.Culture));
        }

        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(HasEffects));
        OnPropertyChanged(nameof(HasCaveats));
        HasCheck = true;
    }

    private static PaymentOptionViewModel Option(PaymentOption o, DateOnly today)
    {
        var verdict = Loc.T("Verdict_" + o.Verdict);
        var prudent = o.Verdict == AffordabilityVerdict.Prudent;
        var bad = o.Verdict is AffordabilityVerdict.NotAffordable or AffordabilityVerdict.CannotCalculate;
        return o.Method switch
        {
            PaymentMethod.CurrentAccount => new PaymentOptionViewModel(
                Loc.T("Pay_Now"), verdict, Loc.Format("Pay_NowDetail", LedgerText.Amount(o.LowestBalance ?? Money.Zero)), prudent, bad),
            PaymentMethod.CreditCard => new PaymentOptionViewModel(
                Loc.Format("Pay_Card", o.Card!.Name), verdict, CardDetail(o, today), prudent, bad),
            _ => new PaymentOptionViewModel(
                Loc.T("Pay_Postpone"),
                o.PaidOn is { } on ? Loc.Format("Pay_PostponeUntil", LedgerText.Date(on, today)) : Loc.T("Verdict_NotAffordable"),
                o.Note switch
                {
                    PaymentNote.BeforeTargetDate => Loc.T("Pay_BeforeTarget"),
                    PaymentNote.AfterTargetDate => Loc.T("Pay_AfterTarget"),
                    PaymentNote.NoDateFound => Loc.Format("Check_NoDate", AffordabilityService.ScanDays),
                    _ => null,
                },
                prudent,
                bad),
        };
    }

    private static string CardDetail(PaymentOption o, DateOnly today) => o.Note switch
    {
        PaymentNote.CardNotSetUp => Loc.T("Card_NotSetUp"),
        PaymentNote.OverLimit => Loc.Format("Pay_CardOverLimit", LedgerText.Amount(o.AvailableCreditAfter!.Value.Abs())),
        PaymentNote.SettlementAccountUnknown => Loc.T("Pay_CardNoAccount"),
        PaymentNote.SettlementAccountNotCounted => Loc.Format("Pay_CardOtherAccount", LedgerText.Date(o.PaidOn!.Value, today)),
        _ => Loc.Format(
            "Pay_CardDetail",
            LedgerText.Date(o.PaidOn!.Value, today),
            LedgerText.Amount(o.StatementAfter ?? Money.Zero),
            LedgerText.Amount(o.AvailableCreditAfter ?? Money.Zero),
            LedgerText.Amount(o.LowestBalance ?? Money.Zero)),
    };

    private IReadOnlyList<WishlistItem> OpenItems() => _entries.Where(e => e.IsOpen).Select(e => e.Item).ToList();

    private async Task ReloadAsync()
    {
        var today = _clock.Today;
        var selectedId = SelectedRow?.Item.Id;
        _entries = await _wishlist.ListAsync();
        var summaries = await _affordability.SummariesAsync(_entries.Where(e => e.IsOpen).Select(e => e.Item));
        Items.Clear();
        ClosedItems.Clear();
        foreach (var entry in _entries)
        {
            var row = new WishlistRowViewModel(entry, summaries.GetValueOrDefault(entry.Item.Id), today, OpenRowAsync, EditRowAsync);
            (entry.IsOpen ? Items : ClosedItems).Add(row);
        }

        if (Items.Count > 0)
        {
            Items[^1].IsLast = true;
        }

        if (ClosedItems.Count > 0)
        {
            ClosedItems[^1].IsLast = true;
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasClosed));

        // Keep the open answer up to date when the ledger or plans change underneath it.
        if (HasCheck && selectedId is { } id && Items.Concat(ClosedItems).FirstOrDefault(r => r.Item.Id == id) is { } again && !IsPurchaseOpen)
        {
            await ShowItemAsync(again);
        }
        else if (selectedId is { } sid && Items.Concat(ClosedItems).FirstOrDefault(r => r.Item.Id == sid) is { } same)
        {
            Select(same);
        }
    }

    protected override void OnLanguageChanged()
    {
        // Read the choice first: replacing the options clears the selection in the list.
        var priority = FormPriority?.Priority ?? WishlistPriority.Normal;
        PriorityOptions = PriorityOption.All();
        FormPriority = PriorityOptions.First(p => p.Priority == priority);
        OnPropertyChanged(nameof(FormTitle));
        StatusMessage = null;
        HasCheck = false;
        if (_active)
        {
            _ = ReloadAsync();
        }
    }
}
