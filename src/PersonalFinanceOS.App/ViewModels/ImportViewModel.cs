using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Import;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Imports;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.App.ViewModels;

public enum ImportStep
{
    Choose,
    Review,
    Done,
}

/// <summary>A column of the file, or "(none)".</summary>
public sealed record ColumnOption(int? Index, string Name);

public sealed record LayoutOption(AmountLayout Layout, string Name);

public sealed record CharOption(char Value, string Name);

public sealed record HeaderRowOption(int Index, string Name);

/// <summary>One line of the preview with its choices (spec §8 steps 6–8).</summary>
public sealed partial class ImportRowViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Category> _categories;
    private readonly Action _changed;

    public ImportRowViewModel(ImportCandidate candidate, Account account, IReadOnlyList<Account> others, IReadOnlyList<Category> categories, DateOnly today, Action changed)
    {
        Candidate = candidate;
        _categories = categories;
        _changed = changed;
        DateText = candidate.Status == ImportRowStatus.Invalid && candidate.Row.Date is null ? "—" : LedgerText.Date(candidate.Date, today);

        var positive = candidate.Amount.IsPositive;
        var types = positive
            ? (account.Type.IsLiability() ? new[] { TransactionType.Refund, TransactionType.Transfer } : [TransactionType.Income, TransactionType.Refund, TransactionType.Transfer])
            : [TransactionType.Expense, TransactionType.Transfer];
        TypeOptions = types.Select(t => new TypeOption(t, t == TransactionType.Transfer ? Loc.T(positive ? "Import_TransferFrom" : "Import_TransferTo") : LedgerText.Type(t))).ToList();
        TransferOptions = others.Select(a => new AccountOption(a.Id, a.Name)).ToList();
        _selectedType = TypeOptions.FirstOrDefault(o => o.Type == candidate.Type) ?? TypeOptions[0];
        _selectedTransfer = TransferOptions.FirstOrDefault(o => o.Id == candidate.TransferAccountId);
        CategoryOptions = BuildCategoryOptions();
        _selectedCategory = CategoryOptions.FirstOrDefault(o => o.Id == candidate.CategoryId) ?? CategoryOptions[0];
        _include = candidate.Include;
    }

    public ImportCandidate Candidate { get; }

    public int RowNumber => Candidate.RowNumber;

    public string DateText { get; }

    public string Title => Candidate.Description.Length > 0 ? Candidate.Description : Loc.T("Import_NoDescription");

    public string? Subtitle => Candidate.Note;

    public bool HasSubtitle => Candidate.Note is not null;

    public string AmountText => Candidate.Status == ImportRowStatus.Invalid && Candidate.Row.Amount is null ? "—" : LedgerText.Amount(Candidate.Amount);

    public bool IsOutflow => Candidate.Amount.IsNegative;

    public bool CanInclude => Candidate.CanInclude;

    /// <summary>What the preview found, in words (never colour alone, spec §20).</summary>
    public string StatusText => Candidate.Status switch
    {
        ImportRowStatus.Invalid => Loc.Error(Candidate.Problem ?? "unexpected"),
        ImportRowStatus.Duplicate when Candidate.MatchWasRemoved => Loc.T("Import_Status_Removed"),
        _ => Loc.T("Import_Status_" + Candidate.Status),
    };

    public bool ShowStatus => Candidate.Status != ImportRowStatus.New;

    public bool IsProblem => Candidate.Status == ImportRowStatus.Invalid;

    public string? SuggestionText => Candidate.Suggestion == ImportSuggestion.None ? null : Loc.T("Import_Suggestion_" + Candidate.Suggestion);

    public bool HasSuggestion => SuggestionText is not null;

    public IReadOnlyList<TypeOption> TypeOptions { get; }

    public IReadOnlyList<AccountOption> TransferOptions { get; }

    [ObservableProperty]
    private IReadOnlyList<CategoryOption> _categoryOptions;

    [ObservableProperty]
    private bool _include;

    [ObservableProperty]
    private TypeOption _selectedType;

    [ObservableProperty]
    private CategoryOption? _selectedCategory;

    [ObservableProperty]
    private AccountOption? _selectedTransfer;

    public bool IsTransfer => SelectedType.Type == TransactionType.Transfer;

    public bool ShowCategory => !IsTransfer;

    partial void OnIncludeChanged(bool value)
    {
        Candidate.Include = value;
        _changed();
    }

    partial void OnSelectedTypeChanged(TypeOption value)
    {
        Candidate.Type = value.Type;
        var keep = SelectedCategory?.Id;
        CategoryOptions = BuildCategoryOptions();
        SelectedCategory = CategoryOptions.FirstOrDefault(o => o.Id == keep) ?? CategoryOptions[0];
        OnPropertyChanged(nameof(IsTransfer));
        OnPropertyChanged(nameof(ShowCategory));
    }

    partial void OnSelectedCategoryChanged(CategoryOption? value) => Candidate.CategoryId = value?.Id;

    partial void OnSelectedTransferChanged(AccountOption? value) => Candidate.TransferAccountId = value?.Id;

    private List<CategoryOption> BuildCategoryOptions()
    {
        var kind = Candidate.Type.CategoryKind();
        var list = new List<CategoryOption> { new(null, Loc.T("Editor_NoCategory")) };
        list.AddRange(_categories.Where(c => c.Kind == kind).Select(c => new CategoryOption(c.Id, c.Name)));
        return list;
    }
}

/// <summary>An earlier import in the history list, with a safe two-step undo.</summary>
public sealed partial class ImportBatchViewModel(ImportBatchInfo info, Func<ImportBatchViewModel, bool, Task> undo, Func<ImportBatchViewModel, Task> plan) : ViewModelBase
{
    public ImportBatch Batch => info.Batch;

    public bool IsLast { get; set; }

    public string Title => Batch.FileName;

    public string Subtitle => Loc.Format("Import_History_Line", info.Account.Name, Batch.ImportedAt.ToLocalTime().ToString("g", Loc.Instance.Culture), Batch.ImportedCount);

    public bool IsUndone => !Batch.IsActive;

    public string? UndoneText => IsUndone ? Loc.T("Import_History_Undone") : null;

    [ObservableProperty]
    private BatchUndoPlan? _plan;

    public bool IsConfirming => Plan is not null;

    public bool CanUndo => !IsUndone && Plan is null;

    public string PlanText => Plan switch
    {
        null => string.Empty,
        { NeedsChoice: true } p => Loc.Format("Import_Undo_PlanChanged", p.Unchanged, p.Changed),
        { } p => Loc.Format("Import_Undo_Plan", p.Unchanged),
    };

    public bool NeedsChoice => Plan?.NeedsChoice == true;

    public string UndoAllText => Loc.Format("Import_Undo_All", (Plan?.Unchanged ?? 0) + (Plan?.Changed ?? 0));

    public string UndoConfirmText => Loc.T(NeedsChoice ? "Import_Undo_Confirm" : "Import_Undo_ConfirmSimple");

    partial void OnPlanChanged(BatchUndoPlan? value)
    {
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(PlanText));
        OnPropertyChanged(nameof(NeedsChoice));
        OnPropertyChanged(nameof(UndoAllText));
        OnPropertyChanged(nameof(UndoConfirmText));
    }

    [RelayCommand]
    private Task StartUndo() => plan(this);

    [RelayCommand]
    private Task UndoUnchanged() => undo(this, false);

    [RelayCommand]
    private Task UndoAll() => undo(this, true);

    [RelayCommand]
    private void KeepIt() => Plan = null;
}

/// <summary>
/// Bank-file import (spec §8): choose account and file, check the detected columns, review every
/// line, confirm, and undo a whole import later if needed. Nothing is saved before confirming.
/// </summary>
public sealed partial class ImportViewModel : PageViewModel
{
    private readonly ImportService _import;
    private readonly AccountService _accounts;
    private readonly CategoryService _categories;
    private readonly IClock _clock;
    private byte[]? _content;
    private ImportFile? _file;
    private ImportPreview? _preview;

    public ImportViewModel(ImportService import, AccountService accounts, CategoryService categories, LedgerService ledger, IClock clock)
    {
        _import = import;
        _accounts = accounts;
        _categories = categories;
        _clock = clock;
        ledger.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadHistoryAsync());
    }

    [ObservableProperty]
    private ImportStep _step = ImportStep.Choose;

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _accountOptions = [];

    [ObservableProperty]
    private AccountOption? _selectedAccount;

    [ObservableProperty]
    private string? _fileName;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _statusMessage;

    // ----- column mapping -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MappingToggleText))]
    private bool _isMappingOpen;

    public string MappingToggleText => Loc.T(IsMappingOpen ? "Import_Columns_Hide" : "Import_Columns_Edit");

    [ObservableProperty]
    private bool _mappingRemembered;

    [ObservableProperty]
    private IReadOnlyList<ColumnOption> _columnOptions = [];

    [ObservableProperty]
    private IReadOnlyList<HeaderRowOption> _headerRowOptions = [];

    [ObservableProperty]
    private HeaderRowOption? _selectedHeaderRow;

    [ObservableProperty]
    private ColumnOption? _dateColumn;

    [ObservableProperty]
    private string _dateFormat = ImportMapping.DateFormats[0];

    [ObservableProperty]
    private LayoutOption? _selectedLayout;

    [ObservableProperty]
    private ColumnOption? _amountColumn;

    [ObservableProperty]
    private ColumnOption? _debitColumn;

    [ObservableProperty]
    private ColumnOption? _creditColumn;

    [ObservableProperty]
    private ColumnOption? _directionColumn;

    [ObservableProperty]
    private CharOption? _decimalSeparator;

    [ObservableProperty]
    private ColumnOption? _descriptionColumn;

    [ObservableProperty]
    private ColumnOption? _counterpartyColumn;

    [ObservableProperty]
    private ColumnOption? _referenceColumn;

    [ObservableProperty]
    private ColumnOption? _currencyColumn;

    [ObservableProperty]
    private CharOption? _delimiter;

    public IReadOnlyList<string> DateFormats => ImportMapping.DateFormats;

    public IReadOnlyList<LayoutOption> LayoutOptions { get; private set; } = Layouts();

    public IReadOnlyList<CharOption> DecimalOptions { get; } = [new(',', "1.234,56"), new('.', "1,234.56")];

    public IReadOnlyList<CharOption> DelimiterOptions { get; private set; } = Delimiters();

    public bool IsCsv => _file?.Format == "csv";

    public bool ShowSignedAmount => SelectedLayout?.Layout != AmountLayout.DebitCredit;

    public bool ShowDebitCredit => SelectedLayout?.Layout == AmountLayout.DebitCredit;

    public bool ShowDirection => SelectedLayout?.Layout == AmountLayout.WithDirection;

    // ----- preview -----

    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private bool _canConfirm;

    [ObservableProperty]
    private string _resultText = string.Empty;

    private ImportResult? _result;

    public ObservableCollection<ImportBatchViewModel> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    public bool IsChoosing => Step == ImportStep.Choose;

    public bool IsReviewing => Step == ImportStep.Review;

    public bool IsDone => Step == ImportStep.Done;

    public bool HasAccounts => AccountOptions.Count > 0;

    partial void OnStepChanged(ImportStep value)
    {
        OnPropertyChanged(nameof(IsChoosing));
        OnPropertyChanged(nameof(IsReviewing));
        OnPropertyChanged(nameof(IsDone));
    }

    partial void OnSelectedAccountChanged(AccountOption? value)
    {
        // Duplicates and suggestions depend on the account, so the preview is rebuilt for the new one.
        if (value is not null && _file is not null && Step == ImportStep.Review)
        {
            _ = BuildPreviewAsync(ReadMapping());
        }
    }

    partial void OnSelectedLayoutChanged(LayoutOption? value)
    {
        OnPropertyChanged(nameof(ShowSignedAmount));
        OnPropertyChanged(nameof(ShowDebitCredit));
        OnPropertyChanged(nameof(ShowDirection));
    }

    public override async Task ActivateAsync()
    {
        var accounts = await _accounts.ListAsync();
        var selected = SelectedAccount?.Id;
        AccountOptions = accounts.Select(a => new AccountOption(a.Id, a.Name)).ToList();
        SelectedAccount = AccountOptions.FirstOrDefault(o => o.Id == selected) ?? AccountOptions.FirstOrDefault();
        OnPropertyChanged(nameof(HasAccounts));
        await LoadHistoryAsync();
    }

    /// <summary>Called by the view after the user picked a file (and by tests). The file is read in memory only.</summary>
    public async Task LoadFileAsync(string fileName, byte[] content)
    {
        Error = null;
        StatusMessage = null;
        if (SelectedAccount is null)
        {
            Error = Loc.T("Import_ChooseAccountFirst");
            return;
        }

        try
        {
            _file = ImportFile.Read(fileName, content);
        }
        catch (DomainValidationException ex)
        {
            Error = Loc.Error(ex.Code);
            return;
        }

        _content = content;
        FileName = Path.GetFileName(fileName);
        OnPropertyChanged(nameof(IsCsv));
        var suggestion = await _import.SuggestMappingAsync(_file);
        MappingRemembered = suggestion.Remembered;
        ShowMapping(suggestion.Mapping);

        // An unknown layout opens the column check right away; a remembered one stays folded.
        IsMappingOpen = !suggestion.Remembered;
        await BuildPreviewAsync(suggestion.Mapping);
    }

    [RelayCommand]
    private async Task ApplyMappingAsync()
    {
        if (_file is null)
        {
            return;
        }

        if (IsCsv && Delimiter is { } d && _content is not null && d.Value != _file.Delimiter)
        {
            _file = _file.WithDelimiter(d.Value, _content);
            var detected = await _import.SuggestMappingAsync(_file);
            ShowMapping(detected.Mapping);
        }

        await BuildPreviewAsync(ReadMapping());
    }

    [RelayCommand]
    private void ToggleMapping() => IsMappingOpen = !IsMappingOpen;

    [RelayCommand]
    private void IncludeAll()
    {
        foreach (var row in Rows.Where(r => r.CanInclude))
        {
            row.Include = true;
        }
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_preview is null)
        {
            return;
        }

        Error = null;
        try
        {
            _result = await _import.ConfirmAsync(_preview);
        }
        catch (ImportRowException ex)
        {
            Error = Loc.Format("Import_RowError", ex.RowNumber, Loc.Error(ex.Code));
            return;
        }
        catch (DomainValidationException ex)
        {
            Error = Loc.Error(ex.Code);
            return;
        }

        ResultText = ResultSummary(_result);
        Step = ImportStep.Done;
        await LoadHistoryAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        _file = null;
        _content = null;
        _preview = null;
        FileName = null;
        Rows.Clear();
        Error = null;
        Step = ImportStep.Choose;
    }

    [RelayCommand]
    private void StartOver()
    {
        Cancel();
        StatusMessage = null;
    }

    [RelayCommand]
    private async Task UndoLastAsync()
    {
        if (_result is null)
        {
            return;
        }

        var item = History.FirstOrDefault(h => h.Batch.Id == _result.Batch.Id);
        if (item is not null)
        {
            await PlanUndoAsync(item);
            Step = ImportStep.Choose;
            StatusMessage = null;
        }
    }

    private async Task BuildPreviewAsync(ImportMapping mapping)
    {
        if (_file is null || SelectedAccount is null)
        {
            return;
        }

        Error = null;
        try
        {
            _preview = await _import.PreviewAsync(SelectedAccount.Id, _file, mapping);
        }
        catch (DomainValidationException ex)
        {
            Error = Loc.Error(ex.Code);
            IsMappingOpen = true;
            Rows.Clear();
            _preview = null;
            Step = ImportStep.Review;
            UpdateSummary();
            return;
        }

        await ShowRowsAsync();
        Step = ImportStep.Review;
    }

    /// <summary>Row view models over the preview's candidates; the choices live in the candidates, so nothing is lost.</summary>
    private async Task ShowRowsAsync()
    {
        if (_preview is null)
        {
            return;
        }

        var account = _preview.Account;
        var others = (await _accounts.ListAsync()).Where(a => a.Id != account.Id).ToList();
        var categories = await _categories.ListAsync();
        var today = _clock.Today;
        Rows.Clear();
        foreach (var candidate in _preview.Candidates)
        {
            Rows.Add(new ImportRowViewModel(candidate, account, others, categories, today, UpdateSummary));
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_preview is null)
        {
            SummaryText = string.Empty;
            ConfirmText = Loc.T("Import_Confirm");
            CanConfirm = false;
            return;
        }

        var parts = new List<string> { Loc.Format("Import_Count_New", _preview.Count(ImportRowStatus.New)) };
        void Add(ImportRowStatus status, string key)
        {
            var n = _preview.Count(status);
            if (n > 0)
            {
                parts.Add(Loc.Format(key, n));
            }
        }

        Add(ImportRowStatus.Duplicate, "Import_Count_Duplicate");
        Add(ImportRowStatus.PossibleDuplicate, "Import_Count_Possible");
        Add(ImportRowStatus.BeforeOpening, "Import_Count_BeforeOpening");
        Add(ImportRowStatus.Invalid, "Import_Count_Invalid");
        SummaryText = string.Join(" · ", parts);

        var included = _preview.IncludedCount;
        ConfirmText = included == 1 ? Loc.T("Import_ConfirmOne") : Loc.Format("Import_ConfirmCount", included);
        CanConfirm = included > 0;
    }

    private static string ResultSummary(ImportResult result)
    {
        var text = result.Imported == 1 ? Loc.T("Import_ResultOne") : Loc.Format("Import_Result", result.Imported);
        if (result.Linked > 0)
        {
            text += " " + Loc.Format("Import_Result_Linked", result.Linked);
        }

        if (result.NowDuplicate > 0)
        {
            text += " " + Loc.Format("Import_Result_NowDuplicate", result.NowDuplicate);
        }

        return text;
    }

    private async Task LoadHistoryAsync()
    {
        var batches = await _import.ListBatchesAsync();
        History.Clear();
        foreach (var info in batches.Take(30))
        {
            History.Add(new ImportBatchViewModel(info, UndoBatchAsync, PlanUndoAsync));
        }

        for (var i = 0; i < History.Count; i++)
        {
            History[i].IsLast = i == History.Count - 1;
        }

        OnPropertyChanged(nameof(HasHistory));
    }

    private async Task PlanUndoAsync(ImportBatchViewModel item)
    {
        Error = null;
        try
        {
            item.Plan = await _import.PlanUndoAsync(item.Batch.Id);
        }
        catch (DomainValidationException ex)
        {
            Error = Loc.Error(ex.Code);
        }
    }

    private async Task UndoBatchAsync(ImportBatchViewModel item, bool includeChanged)
    {
        Error = null;
        try
        {
            var removed = await _import.UndoAsync(item.Batch.Id, includeChanged);
            StatusMessage = Loc.Format("Import_Undone", removed);
        }
        catch (DomainValidationException ex)
        {
            Error = Loc.Error(ex.Code);
        }

        await LoadHistoryAsync();
    }

    private void ShowMapping(ImportMapping m)
    {
        if (_file is null)
        {
            return;
        }

        var header = m.HeaderRow >= 0 && m.HeaderRow < _file.Rows.Count ? _file.Rows[m.HeaderRow] : null;
        var sample = _file.Rows.Skip(m.FirstDataRow).FirstOrDefault() ?? [];
        var columns = new List<ColumnOption> { new(null, Loc.T("Import_Column_None")) };
        for (var i = 0; i < _file.ColumnCount; i++)
        {
            var name = header is not null && i < header.Count && header[i].Length > 0 ? header[i] : Loc.Format("Import_Column_Number", i + 1);
            var example = i < sample.Count && sample[i].Length > 0 ? $"  ({Shorten(sample[i])})" : string.Empty;
            columns.Add(new ColumnOption(i, name + example));
        }

        ColumnOptions = columns;
        ColumnOption Col(int? index) => columns.First(c => c.Index == index);

        var headerRows = new List<HeaderRowOption> { new(-1, Loc.T("Import_Header_None")) };
        for (var i = 0; i < Math.Min(_file.Rows.Count, 15); i++)
        {
            headerRows.Add(new HeaderRowOption(i, Loc.Format("Import_Header_Line", i + 1, Shorten(string.Join(" | ", _file.Rows[i].Take(4)), 40))));
        }

        HeaderRowOptions = headerRows;
        SelectedHeaderRow = headerRows.FirstOrDefault(h => h.Index == m.HeaderRow) ?? headerRows[0];
        DateColumn = Col(m.DateColumn);
        DateFormat = m.DateFormat;
        SelectedLayout = LayoutOptions.First(l => l.Layout == m.Layout);
        AmountColumn = Col(m.AmountColumn);
        DebitColumn = Col(m.DebitColumn);
        CreditColumn = Col(m.CreditColumn);
        DirectionColumn = Col(m.DirectionColumn);
        DecimalSeparator = DecimalOptions.First(o => o.Value == m.DecimalSeparator);
        DescriptionColumn = Col(m.DescriptionColumn);
        CounterpartyColumn = Col(m.CounterpartyColumn);
        ReferenceColumn = Col(m.ReferenceColumn);
        CurrencyColumn = Col(m.CurrencyColumn);
        Delimiter = DelimiterOptions.FirstOrDefault(o => o.Value == _file.Delimiter);
    }

    private ImportMapping ReadMapping() => new(
        SelectedHeaderRow?.Index ?? -1,
        DateColumn?.Index ?? -1,
        DateFormat,
        SelectedLayout?.Layout ?? AmountLayout.Signed,
        SelectedLayout?.Layout == AmountLayout.DebitCredit ? null : AmountColumn?.Index,
        SelectedLayout?.Layout == AmountLayout.DebitCredit ? DebitColumn?.Index : null,
        SelectedLayout?.Layout == AmountLayout.DebitCredit ? CreditColumn?.Index : null,
        SelectedLayout?.Layout == AmountLayout.WithDirection ? DirectionColumn?.Index : null,
        DecimalSeparator?.Value ?? ',',
        DescriptionColumn?.Index,
        CounterpartyColumn?.Index,
        ReferenceColumn?.Index,
        CurrencyColumn?.Index);

    private static string Shorten(string text, int max = 24) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static IReadOnlyList<CharOption> Delimiters() =>
    [
        new(';', Loc.T("Import_Delimiter_Semicolon")),
        new(',', Loc.T("Import_Delimiter_Comma")),
        new('\t', Loc.T("Import_Delimiter_Tab")),
        new('|', "|"),
    ];

    private static IReadOnlyList<LayoutOption> Layouts() =>
    [
        new(AmountLayout.Signed, Loc.T("Import_Layout_Signed")),
        new(AmountLayout.DebitCredit, Loc.T("Import_Layout_DebitCredit")),
        new(AmountLayout.WithDirection, Loc.T("Import_Layout_WithDirection")),
    ];

    protected override void OnLanguageChanged()
    {
        var mapping = _file is null ? null : ReadMapping();
        LayoutOptions = Layouts();
        DelimiterOptions = Delimiters();
        OnPropertyChanged(nameof(MappingToggleText));
        OnPropertyChanged(nameof(LayoutOptions));
        OnPropertyChanged(nameof(DelimiterOptions));
        if (mapping is not null)
        {
            ShowMapping(mapping);
        }

        if (Step == ImportStep.Review)
        {
            _ = ShowRowsAsync();
        }

        if (_result is not null)
        {
            ResultText = ResultSummary(_result);
        }

        _ = LoadHistoryAsync();
    }
}
