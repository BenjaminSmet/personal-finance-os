using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record AccountFilterOption(Guid? Id, string Name);

public sealed partial class TransactionRowViewModel(
    LedgerEntry entry,
    DateOnly today,
    Func<TransactionRowViewModel, Task> edit,
    Func<TransactionRowViewModel, Task> @void) : ViewModelBase
{
    public LedgerEntry Entry { get; } = entry;

    public Transaction Transaction => Entry.Transaction;

    public bool IsLast { get; set; }

    public string DateText => LedgerText.Date(Transaction.Date, today);

    public string Title => Transaction.Description.Length > 0 ? Transaction.Description : LedgerText.Kind(Entry.Kind);

    /// <summary>"Current account → Joint account · Groceries".</summary>
    public string Subtitle
    {
        get
        {
            var where = Entry.Counterpart is null
                ? Entry.Account.Name
                : Transaction.Amount.IsNegative ? $"{Entry.Account.Name} → {Entry.Counterpart.Name}" : $"{Entry.Counterpart.Name} → {Entry.Account.Name}";
            return Entry.Category is null ? where : $"{where} · {Entry.Category.Name}";
        }
    }

    /// <summary>Signed from the account's point of view; a transfer shown once is shown without sign.</summary>
    public string AmountText => LedgerText.Amount(ShowsTransferOnce ? Transaction.Amount.Abs() : Transaction.Amount);

    public bool IsOutflow => Transaction.Amount.IsNegative && !ShowsTransferOnce;

    /// <summary>Badge for lines that are not plain income or spending, so meaning never depends on colour.</summary>
    public string KindText => LedgerText.Kind(Entry.Kind);

    public bool ShowKind => Entry.Kind is not (EntryKind.Expense or EntryKind.Income) && Transaction.Description.Length > 0;

    public bool CanEdit => Transaction.Type != TransactionType.Adjustment;

    /// <summary>In the all-accounts view a transfer appears once, as "from → to".</summary>
    public bool ShowsTransferOnce { get; init; }

    [ObservableProperty]
    private bool _isConfirmingVoid;

    [RelayCommand]
    private Task Edit() => edit(this);

    /// <summary>Voiding a transfer changes two accounts, so it asks once more (spec §7).</summary>
    [RelayCommand]
    private Task Void()
    {
        if (Transaction.IsTransfer && !IsConfirmingVoid)
        {
            IsConfirmingVoid = true;
            return Task.CompletedTask;
        }

        return @void(this);
    }

    [RelayCommand]
    private void KeepIt() => IsConfirmingVoid = false;
}

/// <summary>
/// The ledger: recent transactions, this month's income and spending (transfers excluded), new
/// transfers, corrections and voids. Undo lives in the Quick Add bar, which is on every page.
/// </summary>
public sealed partial class TransactionsViewModel : PageViewModel
{
    public const int MaxRows = 300;

    private static readonly TransactionType[] SpendingTypes = [TransactionType.Expense, TransactionType.Income, TransactionType.Refund];

    private readonly LedgerService _ledger;
    private readonly AccountService _accounts;
    private readonly CategoryService _categories;
    private readonly IClock _clock;
    private LedgerEntry? _editing;
    private bool _active;

    public TransactionsViewModel(LedgerService ledger, AccountService accounts, CategoryService categories, IClock clock)
    {
        _ledger = ledger;
        _accounts = accounts;
        _categories = categories;
        _clock = clock;
        ledger.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_active)
            {
                _ = ReloadAsync();
            }
        });
    }

    public ObservableCollection<TransactionRowViewModel> Rows { get; } = [];

    public TransactionEditorViewModel Editor { get; } = new();

    [ObservableProperty]
    private IReadOnlyList<AccountFilterOption> _filterOptions = [];

    [ObservableProperty]
    private AccountFilterOption? _selectedFilter;

    [ObservableProperty]
    private string _incomeText = string.Empty;

    [ObservableProperty]
    private string _expensesText = string.Empty;

    [ObservableProperty]
    private string _netText = string.Empty;

    [ObservableProperty]
    private bool _netIsNegative;

    [ObservableProperty]
    private PeriodTotals _monthTotals;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private string _editorTitle = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _error;

    public bool IsEmpty => Rows.Count == 0;

    public string MonthTitle => _clock.Today.ToString("MMMM yyyy", Loc.Instance.Culture);

    public override async Task ActivateAsync()
    {
        _active = true;
        await ReloadAsync();
    }

    partial void OnSelectedFilterChanged(AccountFilterOption? value)
    {
        if (value is not null)
        {
            _ = ReloadRowsAsync();
        }
    }

    [RelayCommand]
    private async Task NewTransferAsync()
    {
        var accounts = await _accounts.ListAsync();
        _editing = null;
        Editor.Load(accounts, [], [TransactionType.Transfer], TransactionType.Transfer, Money.Zero, string.Empty,
            accounts.FirstOrDefault(a => a.Type == AccountType.PersonalCurrent)?.Id, null, null, _clock.Today);
        EditorTitle = Loc.T("Transactions_NewTransfer");
        StatusMessage = null;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (!Editor.TryRead(out var v))
        {
            return;
        }

        try
        {
            if (v.Type == TransactionType.Transfer)
            {
                var request = new TransferRequest(v.AccountId, v.DestinationId!.Value, v.Date, v.Amount, v.Description, v.Note);
                if (_editing?.Transaction.TransferId is { } transferId)
                {
                    await _ledger.UpdateTransferAsync(transferId, request);
                }
                else
                {
                    await _ledger.RecordTransferAsync(request);
                }
            }
            else if (_editing is not null)
            {
                await _ledger.UpdateAsync(_editing.Transaction.Id,
                    new TransactionRequest(v.AccountId, v.Date, v.Amount, v.Type, v.Description, v.CategoryId, v.Note));
            }
        }
        catch (DomainValidationException ex)
        {
            Editor.Error = Loc.Error(ex.Code);
            return;
        }

        IsEditorOpen = false;
        StatusMessage = Loc.T("Transactions_Saved");
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    private async Task EditAsync(TransactionRowViewModel row)
    {
        var entry = row.Entry;
        var t = entry.Transaction;
        var accounts = (await _accounts.ListAsync()).ToList();
        foreach (var involved in new[] { entry.Account, entry.Counterpart })
        {
            if (involved is not null && accounts.All(a => a.Id != involved.Id))
            {
                accounts.Add(involved); // archived, but still on this line
            }
        }

        var categories = (await _categories.ListAsync()).ToList();
        if (entry.Category is { IsActive: false } archived)
        {
            categories.Add(archived);
        }

        _editing = entry;
        if (t.IsTransfer)
        {
            var outgoing = t.Amount.IsNegative;
            Editor.Load(accounts, categories, [TransactionType.Transfer], TransactionType.Transfer, t.Amount.Abs(), t.Description,
                outgoing ? entry.Account.Id : entry.Counterpart?.Id,
                outgoing ? entry.Counterpart?.Id : entry.Account.Id,
                null, t.Date, t.Note, Loc.T("Editor_TransferWarning"));
        }
        else
        {
            Editor.Load(accounts, categories, SpendingTypes, t.Type, t.Amount.Abs(), t.Description, t.AccountId, null, t.CategoryId, t.Date, t.Note);
        }

        EditorTitle = Loc.T("Transactions_Edit");
        StatusMessage = null;
        IsEditorOpen = true;
    }

    private async Task VoidAsync(TransactionRowViewModel row)
    {
        Error = null;
        try
        {
            await _ledger.VoidAsync(row.Transaction.Id);
            StatusMessage = Loc.T("Transactions_Voided");
        }
        catch (DomainValidationException ex)
        {
            Error = Loc.Error(ex.Code);
        }
    }

    private async Task ReloadAsync()
    {
        var accounts = await _accounts.ListAsync(includeArchived: true);
        var selected = SelectedFilter?.Id;
        var options = new List<AccountFilterOption> { new(null, Loc.T("Transactions_AllAccounts")) };
        options.AddRange(accounts.Select(a => new AccountFilterOption(a.Id, a.Name)));
        FilterOptions = options;
        SelectedFilter = options.FirstOrDefault(o => o.Id == selected) ?? options[0];
        await ReloadRowsAsync();
    }

    private async Task ReloadRowsAsync()
    {
        var accountId = SelectedFilter?.Id;
        var today = _clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var totals = await _ledger.GetTotalsAsync(monthStart, monthStart.AddMonths(1).AddDays(-1), accountId);
        IncomeText = LedgerText.Amount(totals.Income);
        ExpensesText = LedgerText.Amount(totals.Expenses);
        NetText = LedgerText.Amount(totals.Net);
        NetIsNegative = totals.Net.IsNegative;
        MonthTotals = totals;

        var entries = await _ledger.ListEntriesAsync(new TransactionQuery(AccountId: accountId));
        Rows.Clear();
        foreach (var entry in entries)
        {
            var once = accountId is null && entry.Transaction.IsTransfer;
            if (once && entry.Transaction.Amount.IsPositive)
            {
                continue; // the outgoing side represents the transfer
            }

            if (Rows.Count == MaxRows)
            {
                break;
            }

            Rows.Add(new TransactionRowViewModel(entry, today, EditAsync, VoidAsync) { ShowsTransferOnce = once });
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsLast = i == Rows.Count - 1;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(MonthTitle));
        StatusMessage = null;
        Error = null;
        if (_active)
        {
            _ = ReloadAsync();
        }
    }
}
