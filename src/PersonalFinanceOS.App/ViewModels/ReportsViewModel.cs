using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Reports;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Infrastructure.Reports;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record ReportTypeOption(ReportType Type, string Name, string Help);

public enum ReportPeriod
{
    ThisMonth,
    LastMonth,
    ThisYear,
    LastYear,
    Last12Months,
    Custom,
}

public sealed record ReportPeriodOption(ReportPeriod Period, string Name);

/// <summary>An account or category the user can tick to limit the report.</summary>
public sealed partial class ReportChoice(Guid id, string name, Action changed) : ViewModelBase
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => changed();
}

/// <summary>The kinds of file a report can be saved as.</summary>
public enum ReportFileKind
{
    Pdf,
    Csv,
}

/// <summary>
/// The Reports page (spec §18): choose a report, a period and filters, see it on screen, and save exactly that as PDF
/// or CSV where the user chooses. The on-screen report is the print preview: the PDF is made from the same text.
/// Nothing is uploaded and generating a report never changes data.
/// </summary>
public sealed partial class ReportsViewModel : PageViewModel
{
    private readonly ReportService _reports;
    private readonly IAccountRepository _accounts;
    private readonly ICategoryRepository _categories;
    private readonly IClock _clock;
    private Report? _report;
    private bool _loading;
    private bool _applyingPeriod;

    public ReportsViewModel(ReportService reports, IAccountRepository accounts, ICategoryRepository categories, IClock clock)
    {
        _reports = reports;
        _accounts = accounts;
        _categories = categories;
        _clock = clock;
        Types = new(TypeOptions());
        Periods = new(PeriodOptions());
        _selectedType = Types[0];
        _selectedPeriod = Periods[0];
        ApplyPeriod(ReportPeriod.ThisMonth);
    }

    public ObservableCollection<ReportTypeOption> Types { get; }

    public ObservableCollection<ReportPeriodOption> Periods { get; }

    public ObservableCollection<ReportChoice> AccountChoices { get; } = [];

    public ObservableCollection<ReportChoice> CategoryChoices { get; } = [];

    public ObservableCollection<ReportChoice> SingleAccounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeHelp), nameof(NeedsSingleAccount), nameof(UsesAccountFilter), nameof(UsesCategoryFilter), nameof(UsesPlanned))]
    private ReportTypeOption _selectedType;

    [ObservableProperty]
    private ReportPeriodOption _selectedPeriod;

    [ObservableProperty]
    private DateTime? _fromDate;

    [ObservableProperty]
    private DateTime? _toDate;

    [ObservableProperty]
    private ReportChoice? _selectedAccount;

    [ObservableProperty]
    private bool _includePlanned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private FormattedReport? _formatted;

    [ObservableProperty]
    private bool _isOutdated;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _canOpenSaved;

    public string TypeHelp => SelectedType.Help;

    public bool NeedsSingleAccount => SelectedType.Type == ReportType.Account;

    public bool UsesAccountFilter => SelectedType.Type is not ReportType.Account and not ReportType.Budget and not ReportType.NetWorth;

    public bool UsesCategoryFilter => SelectedType.Type is ReportType.MonthlySummary or ReportType.Budget or ReportType.AnnualOverview;

    public bool UsesPlanned => SelectedType.Type is not ReportType.Budget and not ReportType.JointFinance and not ReportType.CreditCard and not ReportType.NetWorth;

    public bool HasReport => Formatted is not null;

    /// <summary>Asks where to save: the file name without extension and the kind. Null when the user cancels. Set by the view (tests set their own).</summary>
    public Func<string, ReportFileKind, Task<Stream?>>? SaveTarget { get; set; }

    /// <summary>Opens the last saved file with the system's default program. Set by the view.</summary>
    public Func<Task>? OpenSaved { get; set; }

    public override async Task ActivateAsync()
    {
        _loading = true;
        try
        {
            var accounts = await _accounts.ListAsync(includeArchived: true);
            var categories = await _categories.ListAsync(includeArchived: false);
            var selectedAccounts = AccountChoices.Where(c => c.IsSelected).Select(c => c.Id).ToHashSet();
            var selectedCategories = CategoryChoices.Where(c => c.IsSelected).Select(c => c.Id).ToHashSet();
            var single = SelectedAccount?.Id;
            Replace(AccountChoices, accounts.Where(a => a.Status == Domain.Accounts.AccountStatus.Active || selectedAccounts.Contains(a.Id))
                .OrderBy(a => a.SortOrder).Select(a => new ReportChoice(a.Id, a.Name, Changed) { IsSelected = selectedAccounts.Contains(a.Id) }));
            Replace(SingleAccounts, accounts.OrderBy(a => a.Status).ThenBy(a => a.SortOrder).Select(a => new ReportChoice(a.Id, a.Name, Changed)));
            Replace(CategoryChoices, categories.Where(c => c.Kind is CategoryKind.Expense or CategoryKind.Income)
                .OrderBy(c => c.Kind).ThenBy(c => c.SortOrder).Select(c => new ReportChoice(c.Id, c.Name, Changed) { IsSelected = selectedCategories.Contains(c.Id) }));
            SelectedAccount = SingleAccounts.FirstOrDefault(a => a.Id == single) ?? SingleAccounts.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    public async Task Generate()
    {
        Error = null;
        StatusMessage = null;
        if (FromDate is not { } from || ToDate is not { } to)
        {
            Error = Loc.Error("report.period.invalid");
            return;
        }

        var request = new ReportRequest(
            SelectedType.Type,
            DateOnly.FromDateTime(from),
            DateOnly.FromDateTime(to),
            NeedsSingleAccount ? (SelectedAccount is { } a ? [a.Id] : null) : UsesAccountFilter ? Selected(AccountChoices) : null,
            UsesCategoryFilter ? Selected(CategoryChoices) : null,
            UsesPlanned && IncludePlanned);
        try
        {
            _report = await _reports.GenerateAsync(request);
            Formatted = ReportFormatter.Format(_report);
            IsOutdated = false;
            CanOpenSaved = false;
        }
        catch (DomainValidationException ex)
        {
            Error = Loc.Error(ex.Code);
        }
    }

    [RelayCommand]
    public Task SavePdf() => SaveAsync(ReportFileKind.Pdf);

    [RelayCommand]
    public Task SaveCsv() => SaveAsync(ReportFileKind.Csv);

    [RelayCommand]
    private async Task OpenFile()
    {
        if (OpenSaved is { } open)
        {
            await open();
        }
    }

    /// <summary>Writes the report on screen, exactly as shown, to the place the user picked.</summary>
    private async Task SaveAsync(ReportFileKind kind)
    {
        Error = null;
        StatusMessage = null;
        if (Formatted is not { } report || SaveTarget is not { } target)
        {
            return;
        }

        try
        {
            await using var stream = await target(report.FileName, kind);
            if (stream is null)
            {
                return;
            }

            var bytes = kind == ReportFileKind.Pdf ? PdfReportWriter.Write(report) : CsvReportWriter.Write(report);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            StatusMessage = Loc.Format("Reports_Saved", report.FileName + (kind == ReportFileKind.Pdf ? ".pdf" : ".csv"));
            CanOpenSaved = kind == ReportFileKind.Pdf && OpenSaved is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Error("report.save.failed");
        }
    }

    partial void OnSelectedTypeChanged(ReportTypeOption value)
    {
        if (value.Type == ReportType.AnnualOverview && SelectedPeriod.Period is ReportPeriod.ThisMonth or ReportPeriod.LastMonth)
        {
            SelectedPeriod = Periods.Single(p => p.Period == ReportPeriod.ThisYear);
        }

        Changed();
    }

    partial void OnSelectedPeriodChanged(ReportPeriodOption value)
    {
        ApplyPeriod(value.Period);
        Changed();
    }

    partial void OnFromDateChanged(DateTime? value) => DatesChanged();

    partial void OnToDateChanged(DateTime? value) => DatesChanged();

    /// <summary>Dates picked by hand make the period "custom".</summary>
    private void DatesChanged()
    {
        if (!_applyingPeriod && SelectedPeriod.Period != ReportPeriod.Custom)
        {
            SelectedPeriod = Periods.Single(p => p.Period == ReportPeriod.Custom);
        }

        Changed();
    }

    partial void OnSelectedAccountChanged(ReportChoice? value) => Changed();

    partial void OnIncludePlannedChanged(bool value) => Changed();

    /// <summary>The report on screen no longer matches the choices; the exports still write what is on screen.</summary>
    private void Changed()
    {
        if (!_loading && Formatted is not null)
        {
            IsOutdated = true;
        }
    }

    private void ApplyPeriod(ReportPeriod period)
    {
        var today = _clock.Today;
        var month = new DateOnly(today.Year, today.Month, 1);
        (DateOnly From, DateOnly To)? range = period switch
        {
            ReportPeriod.ThisMonth => (month, month.AddMonths(1).AddDays(-1)),
            ReportPeriod.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
            ReportPeriod.ThisYear => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            ReportPeriod.LastYear => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            ReportPeriod.Last12Months => (month.AddMonths(-11), month.AddMonths(1).AddDays(-1)),
            _ => null,
        };
        if (range is { } r)
        {
            _applyingPeriod = true;
            FromDate = r.From.ToDateTime(TimeOnly.MinValue);
            ToDate = r.To.ToDateTime(TimeOnly.MinValue);
            _applyingPeriod = false;
        }
    }

    protected override void OnLanguageChanged()
    {
        var type = SelectedType.Type;
        var period = SelectedPeriod.Period;
        _loading = true;
        Replace(Types, TypeOptions());
        Replace(Periods, PeriodOptions());
        SelectedType = Types.Single(t => t.Type == type);
        SelectedPeriod = Periods.Single(p => p.Period == period);
        _loading = false;
        if (_report is not null)
        {
            Formatted = ReportFormatter.Format(_report);
        }
    }

    private static List<Guid>? Selected(IEnumerable<ReportChoice> choices)
    {
        var ids = choices.Where(c => c.IsSelected).Select(c => c.Id).ToList();
        return ids.Count == 0 ? null : ids;
    }

    private static IEnumerable<ReportTypeOption> TypeOptions() =>
        Enum.GetValues<ReportType>().Select(t => new ReportTypeOption(t, Loc.T("ReportType_" + t), Loc.T("ReportTypeHelp_" + t)));

    private static IEnumerable<ReportPeriodOption> PeriodOptions() =>
        Enum.GetValues<ReportPeriod>().Select(p => new ReportPeriodOption(p, Loc.T("Reports_Period_" + p)));

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
