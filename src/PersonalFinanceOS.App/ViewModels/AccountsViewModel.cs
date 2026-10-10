using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Cards;
using PersonalFinanceOS.Domain.Ledger;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record AccountTypeOption(AccountType Type, string Name)
{
    public static IReadOnlyList<AccountTypeOption> All() =>
        Enum.GetValues<AccountType>().Select(t => new AccountTypeOption(t, Loc.T("AccountType_" + t))).ToList();
}

public sealed partial class AccountRowViewModel(
    Account account,
    Money balance,
    ReconciliationRecord? lastReconciliation,
    Func<AccountRowViewModel, Task> archive,
    Func<AccountRowViewModel, Task>? reconcile = null,
    CreditCardProfile? card = null,
    Func<AccountRowViewModel, Task>? editCard = null) : ViewModelBase
{
    public CreditCardProfile? Card { get; } = card;

    public Account Account { get; } = account;

    /// <summary>Opening balance plus all recorded transactions (spec §5), from the ledger service.</summary>
    public Money Balance { get; } = balance;

    public ReconciliationRecord? LastReconciliation { get; } = lastReconciliation;

    /// <summary>Last row in the grouped list (no separator below it).</summary>
    public bool IsLast { get; set; }

    public Geometry Icon => AccountTypeVisuals.Icon(Account.Type);

    public IBrush IconBackground => AccountTypeVisuals.Background(Account.Type);

    public string Name => Account.Name;
    public string TypeName => Loc.T("AccountType_" + Account.Type);
    public bool IsArchived => Account.Status == AccountStatus.Archived;
    public bool IsLiability => Account.Type.IsLiability();

    public string BalanceText => Balance.Format(Loc.Instance.Culture);

    public string SinceText => Loc.Format("Accounts_Since", Account.OpeningDate.ToString("d", Loc.Instance.Culture));

    public bool HasPartialShare => !Account.OwnershipShare.IsFull;

    public string ShareText => HasPartialShare
        ? $"{Account.OwnershipShare.Format(Loc.Instance.Culture)} · {Loc.Format("Accounts_YourPart", Account.PersonalPart(Balance).Format(Loc.Instance.Culture))}"
        : string.Empty;

    /// <summary>Reconciliation state in words, so it never depends on colour (spec §20).</summary>
    public string ReconciliationText => LastReconciliation switch
    {
        null => Loc.T("Accounts_NotReconciled"),
        { IsMatched: true } r => Loc.Format("Accounts_ReconciledOn", r.StatementDate.ToString("d", Loc.Instance.Culture)),
        { } r => Loc.Format("Accounts_ReconcileDifference", r.Difference.Format(Loc.Instance.Culture), r.StatementDate.ToString("d", Loc.Instance.Culture)),
    };

    public bool HasReconciliationDifference => LastReconciliation is { IsMatched: false };

    public bool CanReconcile => reconcile is not null && !IsArchived;

    public bool CanEditCard => editCard is not null && IsLiability && !IsArchived;

    /// <summary>The card's settings in words, or that they are missing (spec §10).</summary>
    public string CardText => !IsLiability ? string.Empty
        : Card is null ? Loc.T("Accounts_CardNotSetUp")
        : CardSettingsText.Describe(Card);

    public bool HasCardText => IsLiability;

    public bool CardMissing => IsLiability && Card is null;

    [RelayCommand]
    private Task EditCard() => editCard?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task Archive() => archive(this);

    [RelayCommand]
    private Task Reconcile() => reconcile?.Invoke(this) ?? Task.CompletedTask;
}

public sealed partial class AccountsViewModel : PageViewModel
{
    private readonly AccountService _accounts;
    private readonly AppSettingsService _settings;
    private readonly LedgerService _ledger;
    private readonly ReconciliationService _reconciliation;
    private readonly CreditCardService _cards;
    private readonly IClock _clock;
    private bool _active;

    public AccountsViewModel(
        AccountService accounts,
        AppSettingsService settings,
        LedgerService ledger,
        ReconciliationService reconciliation,
        CreditCardService cards,
        IClock clock)
    {
        _accounts = accounts;
        _settings = settings;
        _ledger = ledger;
        _reconciliation = reconciliation;
        _cards = cards;
        _clock = clock;
        ledger.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_active)
            {
                _ = ReloadAsync();
            }
        });
        TypeOptions = AccountTypeOption.All();
        _newType = TypeOptions[0];
        _newOpeningDate = clock.Today.ToDateTime(TimeOnly.MinValue);
    }

    public ObservableCollection<AccountRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<AccountTypeOption> _typeOptions;

    [ObservableProperty]
    private bool _showArchived;

    [ObservableProperty]
    private bool _isFormOpen;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreditCard), nameof(BalanceLabel))]
    private AccountTypeOption _newType;

    [ObservableProperty]
    private DateTime? _newOpeningDate;

    [ObservableProperty]
    private string _newBalanceText = string.Empty;

    [ObservableProperty]
    private string _newShareText = "100";

    [ObservableProperty]
    private string? _newNotes;

    [ObservableProperty]
    private string? _formError;

    [ObservableProperty]
    private string? _amountHint;

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsEmpty => Rows.Count == 0;

    // ===== Credit-card settings (spec §10): entered with the card, changeable later, never assumed =====

    [ObservableProperty]
    private string _cardLimitText = string.Empty;

    [ObservableProperty]
    private string _cardStartDayText = string.Empty;

    [ObservableProperty]
    private string _cardSettlementDayText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _settlementOptions = [];

    [ObservableProperty]
    private AccountOption? _cardSettlementAccount;

    /// <summary>The card whose settings are being changed (null: none, or a new card in the add form).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardEditOpen), nameof(CardEditTitle))]
    private AccountRowViewModel? _cardRow;

    [ObservableProperty]
    private string? _cardError;

    public bool IsCardEditOpen => CardRow is not null;

    public string CardEditTitle => CardRow is null ? string.Empty : Loc.Format("Card_EditTitle", CardRow.Name);

    public bool IsCreditCard => NewType.Type.IsLiability();

    public string BalanceLabel => Loc.T(IsCreditCard ? "Accounts_OutstandingBalance" : "Accounts_OpeningBalance");

    // ===== Reconciliation (spec §5) =====

    [ObservableProperty]
    private AccountRowViewModel? _reconcileRow;

    [ObservableProperty]
    private DateTime? _reconcileDate;

    [ObservableProperty]
    private string _reconcileBalanceText = string.Empty;

    [ObservableProperty]
    private string? _reconcileReason;

    [ObservableProperty]
    private string? _reconcilePreview;

    [ObservableProperty]
    private bool _reconcileHasDifference;

    [ObservableProperty]
    private string? _reconcileError;

    public bool IsReconcileOpen => ReconcileRow is not null;

    public string ReconcileTitle => ReconcileRow is null ? string.Empty : Loc.Format("Reconcile_Title", ReconcileRow.Name);

    public string ReconcileBalanceLabel => Loc.T(ReconcileRow?.IsLiability == true ? "Reconcile_StatementOwed" : "Reconcile_StatementBalance");

    partial void OnReconcileRowChanged(AccountRowViewModel? value)
    {
        OnPropertyChanged(nameof(IsReconcileOpen));
        OnPropertyChanged(nameof(ReconcileTitle));
        OnPropertyChanged(nameof(ReconcileBalanceLabel));
    }

    partial void OnReconcileDateChanged(DateTime? value) => _ = UpdateReconcilePreviewAsync();

    partial void OnReconcileBalanceTextChanged(string value) => _ = UpdateReconcilePreviewAsync();

    public override Task ActivateAsync()
    {
        _active = true;
        return ReloadAsync();
    }

    partial void OnShowArchivedChanged(bool value) => _ = ReloadAsync();

    async partial void OnNewTypeChanged(AccountTypeOption value)
    {
        if (value is null)
        {
            return;
        }

        var share = value.Type == AccountType.JointCurrent ? await _settings.GetJointDefaultShareAsync() : Share.Full;
        NewShareText = share.Percent.ToString("0.##", Loc.Instance.Culture);
    }

    partial void OnNewBalanceTextChanged(string value)
    {
        var parsed = AmountParser.Parse(value);
        AmountHint = parsed is { Success: true, IsAmbiguous: true }
            ? Loc.Format("Warning_amount_ambiguous", parsed.Value.Format(Loc.Instance.Culture))
            : null;
    }

    [RelayCommand]
    private async Task OpenForm()
    {
        ReconcileRow = null;
        CardRow = null;
        ResetForm();
        await LoadSettlementOptionsAsync(null);
        IsFormOpen = true;
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsFormOpen = false;
        FormError = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        FormError = null;
        StatusMessage = null;

        var balance = AmountParser.Parse(NewBalanceText);
        if (!balance.Success)
        {
            FormError = Loc.Error("amount.invalid");
            return;
        }

        // The share is typed as a percentage; parsing it like an amount gives basis points exactly.
        var share = AmountParser.Parse(NewShareText);
        if (!share.Success || share.IsAmbiguous || share.Value.Cents is < 1 or > Share.FullBasisPoints)
        {
            FormError = Loc.Error("account.share.invalid");
            return;
        }

        if (NewOpeningDate is not { } date)
        {
            FormError = Loc.Error("date.required");
            return;
        }

        // For a credit card the user types what they owe; it is stored as a negative balance (a debt).
        var opening = IsCreditCard ? balance.Value.Abs().Negate() : balance.Value;

        CardSettings? card = null;
        if (IsCreditCard && !TryReadCard(out card, error => FormError = error))
        {
            return;
        }

        try
        {
            await _accounts.CreateAsync(new CreateAccountRequest(
                NewName,
                NewType.Type,
                DateOnly.FromDateTime(date),
                opening,
                new Share((int)share.Value.Cents),
                NewNotes,
                card));
        }
        catch (DomainValidationException ex)
        {
            FormError = Loc.Error(ex.Code);
            return;
        }

        IsFormOpen = false;
        StatusMessage = Loc.T("Accounts_Saved");
        await ReloadAsync();
    }

    private async Task ArchiveAsync(AccountRowViewModel row)
    {
        await _accounts.ArchiveAsync(row.Account.Id);
        await ReloadAsync();
    }

    private async Task OpenCardAsync(AccountRowViewModel row)
    {
        IsFormOpen = false;
        ReconcileRow = null;
        StatusMessage = null;
        CardError = null;
        var profile = row.Card;
        CardLimitText = profile is null ? string.Empty : profile.CreditLimit.ToEuros().ToString("0.##", Loc.Instance.Culture);
        CardStartDayText = profile?.StatementStartDay.ToString(Loc.Instance.Culture) ?? string.Empty;
        CardSettlementDayText = profile?.SettlementDay?.ToString(Loc.Instance.Culture) ?? string.Empty;
        await LoadSettlementOptionsAsync(row.Account.Id);
        CardSettlementAccount = SettlementOptions.FirstOrDefault(o => o.Id == (profile?.SettlementAccountId ?? Guid.Empty)) ?? SettlementOptions[0];
        CardRow = row;
    }

    [RelayCommand]
    private void CancelCard() => CardRow = null;

    [RelayCommand]
    private async Task SaveCardAsync()
    {
        CardError = null;
        if (CardRow is not { } row || !TryReadCard(out var settings, error => CardError = error))
        {
            return;
        }

        try
        {
            await _cards.SaveSettingsAsync(row.Account.Id, settings!);
        }
        catch (DomainValidationException ex)
        {
            CardError = Loc.Error(ex.Code);
            return;
        }

        CardRow = null;
        StatusMessage = Loc.T("Card_Saved");
        await ReloadAsync();
    }

    /// <summary>Reads the card fields. Limit and period start are required; the debit day and paying account may be unknown yet.</summary>
    private bool TryReadCard(out CardSettings? settings, Action<string> fail)
    {
        settings = null;
        var limit = AmountParser.Parse(CardLimitText);
        if (string.IsNullOrWhiteSpace(CardLimitText) || !limit.Success || limit.Value.IsNegative)
        {
            fail(Loc.Error("card.limit.invalid"));
            return false;
        }

        if (!TryDay(CardStartDayText, out var startDay) || startDay is null)
        {
            fail(Loc.Error("card.startDay.invalid"));
            return false;
        }

        if (!TryDay(CardSettlementDayText, out var settlementDay))
        {
            fail(Loc.Error("card.settlementDay.invalid"));
            return false;
        }

        var payer = CardSettlementAccount is { } option && option.Id != Guid.Empty ? option.Id : (Guid?)null;
        settings = new CardSettings(limit.Value, startDay.Value, settlementDay, payer);
        return true;
    }

    /// <summary>A day of the month, 1–31; empty means not known.</summary>
    private static bool TryDay(string? text, out int? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text.Trim().TrimEnd('e', 'E', '.'), System.Globalization.NumberStyles.None, Loc.Instance.Culture, out var value)
            && value is >= CreditCardProfile.MinDay and <= CreditCardProfile.MaxDay)
        {
            day = value;
            return true;
        }

        return false;
    }

    private async Task LoadSettlementOptionsAsync(Guid? cardId)
    {
        var list = await _accounts.ListAsync();
        SettlementOptions =
        [
            new AccountOption(Guid.Empty, Loc.T("Card_NoSettlementAccount")),
            .. list.Where(a => a.Id != cardId && !a.Type.IsLiability()).Select(a => new AccountOption(a.Id, a.Name)),
        ];
        CardSettlementAccount = SettlementOptions.FirstOrDefault(o => list.Any(a => a.Id == o.Id && a.Type == AccountType.PersonalCurrent))
            ?? SettlementOptions[0];
    }

    private Task OpenReconcileAsync(AccountRowViewModel row)
    {
        IsFormOpen = false;
        CardRow = null;
        StatusMessage = null;
        ReconcileError = null;
        ReconcileReason = null;
        ReconcilePreview = null;
        ReconcileHasDifference = false;
        ReconcileBalanceText = string.Empty;
        ReconcileRow = row;
        ReconcileDate = _clock.Today.ToDateTime(TimeOnly.MinValue);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelReconcile() => ReconcileRow = null;

    [RelayCommand]
    private Task SaveReconciliationAsync() => SaveReconcileAsync(addAdjustment: false);

    [RelayCommand]
    private Task SaveReconciliationWithAdjustmentAsync() => SaveReconcileAsync(addAdjustment: true);

    private async Task SaveReconcileAsync(bool addAdjustment)
    {
        if (!TryReadStatement(out var row, out var date, out var statement))
        {
            return;
        }

        try
        {
            var record = await _reconciliation.RecordAsync(row.Account.Id, date, statement, addAdjustment, ReconcileReason);
            StatusMessage = Loc.T(record.IsMatched ? "Reconcile_SavedMatched" : "Reconcile_SavedDifference");
        }
        catch (DomainValidationException ex)
        {
            ReconcileError = Loc.Error(ex.Code);
            return;
        }

        ReconcileRow = null;
        await ReloadAsync();
    }

    private async Task UpdateReconcilePreviewAsync()
    {
        ReconcileError = null;
        if (!TryReadStatement(out var row, out var date, out var statement, quiet: true))
        {
            ReconcilePreview = null;
            ReconcileHasDifference = false;
            return;
        }

        try
        {
            var preview = await _reconciliation.PreviewAsync(row.Account.Id, date, statement);
            var culture = Loc.Instance.Culture;
            ReconcileHasDifference = !preview.Difference.IsZero;
            ReconcilePreview = ReconcileHasDifference
                ? Loc.Format("Reconcile_Difference", preview.Calculated.Format(culture), preview.Difference.Format(culture))
                : Loc.Format("Reconcile_Matches", preview.Calculated.Format(culture));
        }
        catch (DomainValidationException ex)
        {
            ReconcilePreview = null;
            ReconcileError = Loc.Error(ex.Code);
        }
    }

    private bool TryReadStatement(out AccountRowViewModel row, out DateOnly date, out Money statement, bool quiet = false)
    {
        row = ReconcileRow!;
        date = default;
        statement = default;
        var parsed = AmountParser.Parse(ReconcileBalanceText);
        if (ReconcileRow is null || ReconcileDate is not { } picked || !parsed.Success)
        {
            if (!quiet)
            {
                ReconcileError = Loc.Error(ReconcileDate is null ? "date.required" : "amount.invalid");
            }

            return false;
        }

        date = DateOnly.FromDateTime(picked);

        // For a credit card the statement shows what is owed; the ledger stores that as a negative balance.
        statement = ReconcileRow.IsLiability ? parsed.Value.Abs().Negate() : parsed.Value;
        return true;
    }

    private async Task ReloadAsync()
    {
        var list = await _accounts.ListAsync(ShowArchived);
        var balances = await _ledger.GetBalancesAsync();
        var reconciled = await _reconciliation.LatestAsync();
        var cards = new Dictionary<Guid, CreditCardProfile>();
        foreach (var account in list.Where(a => a.Type.IsLiability()))
        {
            if (await _cards.GetSettingsAsync(account.Id) is { } profile)
            {
                cards[account.Id] = profile;
            }
        }

        Rows.Clear();
        for (var i = 0; i < list.Count; i++)
        {
            var account = list[i];
            Rows.Add(new AccountRowViewModel(account, balances[account.Id], reconciled.GetValueOrDefault(account.Id), ArchiveAsync, OpenReconcileAsync, cards.GetValueOrDefault(account.Id), OpenCardAsync)
            {
                IsLast = i == list.Count - 1,
            });
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ResetForm()
    {
        NewName = string.Empty;
        NewType = TypeOptions[0];
        NewOpeningDate = _clock.Today.ToDateTime(TimeOnly.MinValue);
        NewBalanceText = string.Empty;
        NewShareText = "100";
        NewNotes = null;
        FormError = null;
        AmountHint = null;
        CardLimitText = string.Empty;
        CardStartDayText = string.Empty;
        CardSettlementDayText = string.Empty;
        CardError = null;
    }

    protected override void OnLanguageChanged()
    {
        var selected = NewType.Type;
        TypeOptions = AccountTypeOption.All();
        NewType = TypeOptions.First(o => o.Type == selected);
        OnPropertyChanged(nameof(BalanceLabel));
        OnPropertyChanged(nameof(ReconcileTitle));
        OnPropertyChanged(nameof(ReconcileBalanceLabel));
        OnPropertyChanged(nameof(CardEditTitle));
        StatusMessage = null;
        FormError = null;
        _ = ReloadAsync();
    }
}

/// <summary>Card settings in words, shared by the account list and the planning page.</summary>
internal static class CardSettingsText
{
    public static string Describe(CreditCardProfile card)
    {
        var culture = Loc.Instance.Culture;
        var periodEnd = card.StatementStartDay == 1 ? Loc.T("Card_MonthEnd") : (card.StatementStartDay - 1).ToString(culture);
        var text = Loc.Format("Accounts_CardSummary", card.CreditLimit.Format(culture), card.StatementStartDay, periodEnd);
        return card.SettlementDay is { } day ? text + " · " + Loc.Format("Accounts_CardDebitDay", day) : text + " · " + Loc.T("Accounts_CardDebitDayUnknown");
    }
}

/// <summary>Icon and colour per account type (original line icons, iOS-like tinted circles).</summary>
internal static class AccountTypeVisuals
{
    private static readonly Dictionary<AccountType, (string Path, string Color)> Map = new()
    {
        [AccountType.PersonalCurrent] = ("M3,8 H19 A2,2 0 0 1 21,10 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 Z M3,8 V7 A2,2 0 0 1 5,5 H16 M16,14 H17", "#007AFF"),
        [AccountType.JointCurrent] = ("M9,11 A3,3 0 1 0 9,5 A3,3 0 1 0 9,11 Z M3,20 A6,6 0 0 1 15,20 M16,11 A2.5,2.5 0 1 0 16,6 M16.5,14 A5,5 0 0 1 21,19", "#AF52DE"),
        [AccountType.Savings] = ("M12,4 C16.4,4 20,5.3 20,7 C20,8.7 16.4,10 12,10 C7.6,10 4,8.7 4,7 C4,5.3 7.6,4 12,4 Z M4,7 V12 C4,13.7 7.6,15 12,15 C16.4,15 20,13.7 20,12 V7 M4,12 V17 C4,18.7 7.6,20 12,20 C16.4,20 20,18.7 20,17 V12", "#34C759"),
        [AccountType.CreditCard] = ("M3,6 H21 V18 H3 Z M3,10 H21 M7,15 H10", "#FF9500"),
        [AccountType.Pension] = ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,7 V12 L15,14", "#30B0C7"),
        [AccountType.Investment] = ("M4,20 V12 M9,20 V8 M14,20 V11 M19,20 V4", "#5856D6"),
        [AccountType.Cash] = ("M2,7 H22 V17 H2 Z M12,9.5 A2.5,2.5 0 1 0 12,14.5 A2.5,2.5 0 1 0 12,9.5 Z", "#00C7BE"),
        [AccountType.Other] = ("M5,12 H5.01 M12,12 H12.01 M19,12 H19.01", "#8E8E93"),
    };

    private static readonly Dictionary<AccountType, Geometry> Geometries = new();

    public static Geometry Icon(AccountType type)
    {
        if (!Geometries.TryGetValue(type, out var geometry))
        {
            geometry = StreamGeometry.Parse(Map[type].Path);
            Geometries[type] = geometry;
        }

        return geometry;
    }

    public static IBrush Background(AccountType type) => new SolidColorBrush(Color.Parse(Map[type].Color));
}
