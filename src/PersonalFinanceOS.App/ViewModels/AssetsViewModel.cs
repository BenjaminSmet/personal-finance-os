using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Assets;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Assets;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record AssetKindOption(AssetKind Kind, string Name)
{
    public static IReadOnlyList<AssetKindOption> All() =>
        Enum.GetValues<AssetKind>().Select(k => new AssetKindOption(k, Loc.T("AssetKind_" + k))).ToList();
}

/// <summary>What the panel under the net worth lines is showing.</summary>
public enum AssetPanel
{
    None,
    Valuation,
    Share,
}

/// <summary>One line of net worth, explained in words: where the value comes from, the share, and whether it is out of date.</summary>
public sealed partial class NetWorthLineViewModel(NetWorthLine line, DateOnly today, Func<NetWorthLineViewModel, AssetPanel, Task> open) : ViewModelBase
{
    public NetWorthLine Line { get; } = line;

    public string Name => Line.Name;

    public string KindText => Line.Account is { } a ? Loc.T("AccountType_" + a.Type) : Loc.T("AssetKind_" + Line.Asset!.Kind);

    public string SourceText => Line.Source switch
    {
        ValueSource.Ledger => Loc.T("Assets_SourceLedger"),
        ValueSource.Valuation => Loc.Format("Assets_SourceValuation", LedgerText.Date(Line.ValuedOn!.Value, today)),
        ValueSource.ValuationPlusLedger => Loc.Format("Assets_SourceValuationPlus", LedgerText.Date(Line.ValuedOn!.Value, today)),
        ValueSource.LedgerWithoutValuation => Loc.T("Assets_SourceNoValuationYet"),
        _ => Loc.T("Assets_SourceNone"),
    };

    public bool HasPartialShare => !Line.Share.IsFull;

    public string ShareText => Loc.Format("Assets_ShareOf", Line.Share.Format(Loc.Instance.Culture), LedgerText.Amount(Line.FullValue));

    public string ValueText => LedgerText.Amount(Line.PersonalValue);

    public bool IsLiability => Line.IsLiability;

    public bool IsStale => Line.IsStale;

    public bool IsUncertain => Line.IsUncertain;

    /// <summary>Pension, investments and assets take manual values; everything else comes from the ledger.</summary>
    public bool CanValue => Line.Asset is not null || Line.Account?.Type.IsManuallyValued() == true;

    /// <summary>A card is always owed in full; every other account and asset can be shared.</summary>
    public bool CanChangeShare => Line.Asset is not null || Line.Account is { Type: not AccountType.CreditCard };

    public bool IsLast { get; set; }

    [RelayCommand]
    private Task Value() => open(this, AssetPanel.Valuation);

    [RelayCommand]
    private Task Share() => open(this, AssetPanel.Share);
}

/// <summary>One stored valuation, newest first, with a way to remove a mistake.</summary>
public sealed partial class ValuationRowViewModel(Guid id, string dateText, string valueText, string? note, Func<Guid, Task> remove) : ViewModelBase
{
    public string DateText { get; } = dateText;

    public string ValueText { get; } = valueText;

    public string? Note { get; } = note;

    [RelayCommand]
    private Task Remove() => remove(id);
}

/// <summary>One month of net worth, with a bar relative to the largest month shown.</summary>
public sealed record NetWorthBarViewModel(string Label, string AmountText, double Value, double Maximum, bool IsNegative, bool IsComplete);

/// <summary>
/// Assets &amp; investments (spec §17, §24 phase 6): net worth with its explanation and history, manual valuations for
/// pension, Saxo and other assets, liabilities, and ownership shares that change from a date on. Values are only
/// entered here; nothing is fetched.
/// </summary>
public sealed partial class AssetsViewModel : PageViewModel
{
    private readonly NetWorthService _netWorth;
    private readonly AssetService _assets;
    private readonly IClock _clock;
    private bool _active;

    public AssetsViewModel(NetWorthService netWorth, AssetService assets, LedgerService ledger, IClock clock)
    {
        _netWorth = netWorth;
        _assets = assets;
        _clock = clock;
        void Refresh(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (_active)
            {
                _ = ReloadAsync();
            }
        });
        ledger.Changed += Refresh;
        assets.Changed += Refresh;
        _kindOptions = AssetKindOption.All();
        _newKind = _kindOptions[0];
        _newValueDate = clock.Today.ToDateTime(TimeOnly.MinValue);
    }

    [ObservableProperty]
    private bool _hasAnything;

    [ObservableProperty]
    private string _totalText = string.Empty;

    [ObservableProperty]
    private bool _totalIsNegative;

    [ObservableProperty]
    private string _assetsText = string.Empty;

    [ObservableProperty]
    private string _liabilitiesText = string.Empty;

    [ObservableProperty]
    private string _householdText = string.Empty;

    [ObservableProperty]
    private bool _hasShared;

    /// <summary>Which values are missing, so the total is known to be incomplete (spec §17).</summary>
    [ObservableProperty]
    private string? _completenessNote;

    [ObservableProperty]
    private string? _staleNote;

    [ObservableProperty]
    private string? _historyNote;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<NetWorthLineViewModel> AssetLines { get; } = [];

    public ObservableCollection<NetWorthLineViewModel> LiabilityLines { get; } = [];

    public ObservableCollection<NetWorthBarViewModel> History { get; } = [];

    public bool HasLiabilities => LiabilityLines.Count > 0;

    public bool HasHistory => History.Count > 0;

    // ----- The panel for one line: valuation or share -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValuationOpen), nameof(IsShareOpen), nameof(PanelTitle), nameof(CanArchive))]
    private NetWorthLineViewModel? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValuationOpen), nameof(IsShareOpen), nameof(PanelTitle))]
    private AssetPanel _panel;

    public bool IsValuationOpen => Selected is not null && Panel == AssetPanel.Valuation;

    public bool IsShareOpen => Selected is not null && Panel == AssetPanel.Share;

    public string PanelTitle => Selected is null ? string.Empty
        : Loc.Format(Panel == AssetPanel.Share ? "Assets_ShareTitle" : "Assets_ValuationTitle", Selected.Name);

    public bool CanArchive => Selected?.Line.Asset is not null;

    [ObservableProperty]
    private DateTime? _valueDate;

    [ObservableProperty]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string? _valueNote;

    [ObservableProperty]
    private DateTime? _shareDate;

    [ObservableProperty]
    private string _sharePercentText = string.Empty;

    [ObservableProperty]
    private string? _panelError;

    public ObservableCollection<ValuationRowViewModel> Valuations { get; } = [];

    public ObservableCollection<AmountLineViewModel> ShareHistory { get; } = [];

    public bool HasValuations => Valuations.Count > 0;

    public bool HasShareHistory => ShareHistory.Count > 0;

    // ----- Adding an asset or liability -----

    [ObservableProperty]
    private bool _isAddOpen;

    [ObservableProperty]
    private IReadOnlyList<AssetKindOption> _kindOptions;

    [ObservableProperty]
    private AssetKindOption _newKind;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newShareText = "100";

    [ObservableProperty]
    private string _newValueText = string.Empty;

    [ObservableProperty]
    private DateTime? _newValueDate;

    [ObservableProperty]
    private string? _newNotes;

    [ObservableProperty]
    private string? _addError;

    public override Task ActivateAsync()
    {
        _active = true;
        return ReloadAsync();
    }

    [RelayCommand]
    private void OpenAdd()
    {
        AddError = null;
        StatusMessage = null;
        NewName = string.Empty;
        NewKind = KindOptions[0];
        NewShareText = "100";
        NewValueText = string.Empty;
        NewValueDate = _clock.Today.ToDateTime(TimeOnly.MinValue);
        NewNotes = null;
        IsAddOpen = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAddOpen = false;

    [RelayCommand]
    private async Task SaveAsset()
    {
        AddError = null;
        if (!TryShare(NewShareText, out var share))
        {
            AddError = Loc.Error("account.share.invalid");
            return;
        }

        Money? value = null;
        if (!string.IsNullOrWhiteSpace(NewValueText))
        {
            var parsed = AmountParser.Parse(NewValueText);
            if (!parsed.Success || parsed.Value.IsNegative)
            {
                AddError = Loc.Error("amount.invalid");
                return;
            }

            value = parsed.Value;
        }

        try
        {
            await _assets.CreateAsync(new AssetRequest(
                NewName, NewKind.Kind, share, value, NewValueDate is { } d ? DateOnly.FromDateTime(d) : null, NewNotes));
        }
        catch (DomainValidationException ex)
        {
            AddError = Loc.Error(ex.Code);
            return;
        }

        IsAddOpen = false;
        StatusMessage = Loc.Format("Assets_Added", NewName.Trim());
    }

    [RelayCommand]
    private void ClosePanel() => Selected = null;

    [RelayCommand]
    private async Task SaveValuation()
    {
        PanelError = null;
        if (Selected is not { } line)
        {
            return;
        }

        var parsed = AmountParser.Parse(ValueText);
        if (!parsed.Success || parsed.Value.IsNegative || string.IsNullOrWhiteSpace(ValueText))
        {
            PanelError = Loc.Error("amount.invalid");
            return;
        }

        if (ValueDate is not { } date)
        {
            PanelError = Loc.Error("date.required");
            return;
        }

        try
        {
            await _assets.SetValuationAsync(line.Line.OwnerId, DateOnly.FromDateTime(date), parsed.Value, ValueNote);
        }
        catch (DomainValidationException ex)
        {
            PanelError = Loc.Error(ex.Code);
            return;
        }

        StatusMessage = Loc.Format("Assets_ValuationSaved", line.Name, LedgerText.Amount(parsed.Value));
        Selected = null;
    }

    [RelayCommand]
    private async Task SaveShare()
    {
        PanelError = null;
        if (Selected is not { } line)
        {
            return;
        }

        if (!TryShare(SharePercentText, out var share))
        {
            PanelError = Loc.Error("account.share.invalid");
            return;
        }

        if (ShareDate is not { } date)
        {
            PanelError = Loc.Error("date.required");
            return;
        }

        try
        {
            await _assets.ChangeShareAsync(line.Line.OwnerId, DateOnly.FromDateTime(date), share);
        }
        catch (DomainValidationException ex)
        {
            PanelError = Loc.Error(ex.Code);
            return;
        }

        StatusMessage = Loc.Format("Assets_ShareSaved", line.Name, share.Format(Loc.Instance.Culture), LedgerText.Date(DateOnly.FromDateTime(date), _clock.Today));
        Selected = null;
    }

    [RelayCommand]
    private async Task ArchiveAsset()
    {
        PanelError = null;
        if (Selected?.Line.Asset is not { } asset)
        {
            return;
        }

        try
        {
            await _assets.ArchiveAsync(asset.Id);
        }
        catch (DomainValidationException ex)
        {
            PanelError = Loc.Error(ex.Code);
            return;
        }

        StatusMessage = Loc.Format("Assets_Archived", asset.Name);
        Selected = null;
    }

    private async Task OpenPanelAsync(NetWorthLineViewModel line, AssetPanel panel)
    {
        StatusMessage = null;
        PanelError = null;
        var today = _clock.Today;
        Panel = panel;
        Selected = line;
        if (panel == AssetPanel.Valuation)
        {
            ValueDate = today.ToDateTime(TimeOnly.MinValue);
            ValueText = string.Empty;
            ValueNote = null;
            await LoadValuationsAsync(line);
        }
        else
        {
            ShareDate = today.ToDateTime(TimeOnly.MinValue);
            SharePercentText = line.Line.Share.Percent.ToString("0.##", Loc.Instance.Culture);
            ShareHistory.Clear();
            foreach (var change in (await _assets.ListShareChangesAsync(line.Line.OwnerId)).OrderByDescending(c => c.EffectiveFrom))
            {
                ShareHistory.Add(new AmountLineViewModel(Loc.Format("Assets_ShareFrom", LedgerText.Date(change.EffectiveFrom, today)), change.Share.Format(Loc.Instance.Culture)));
            }

            OnPropertyChanged(nameof(HasShareHistory));
        }
    }

    private async Task LoadValuationsAsync(NetWorthLineViewModel line)
    {
        var today = _clock.Today;
        Valuations.Clear();
        foreach (var v in (await _assets.ListValuationsAsync(line.Line.OwnerId)).OrderByDescending(v => v.Date))
        {
            Valuations.Add(new ValuationRowViewModel(v.Id, LedgerText.Date(v.Date, today), LedgerText.Amount(v.Value), v.Note, RemoveValuationAsync));
        }

        OnPropertyChanged(nameof(HasValuations));
    }

    private async Task RemoveValuationAsync(Guid id)
    {
        PanelError = null;
        try
        {
            await _assets.RemoveValuationAsync(id);
        }
        catch (DomainValidationException ex)
        {
            PanelError = Loc.Error(ex.Code);
            return;
        }

        if (Selected is { } line)
        {
            await LoadValuationsAsync(line);
        }
    }

    private async Task ReloadAsync()
    {
        var today = _clock.Today;
        var worth = await _netWorth.CalculateAsync();
        HasAnything = worth.Lines.Count > 0;
        TotalText = LedgerText.Amount(worth.Total);
        TotalIsNegative = worth.Total.IsNegative;
        AssetsText = LedgerText.Amount(worth.Assets);
        LiabilitiesText = LedgerText.Amount(worth.Liabilities);
        HouseholdText = LedgerText.Amount(worth.HouseholdTotal);
        HasShared = worth.HasSharedItems;

        var uncertain = worth.Lines.Where(l => l.IsUncertain).Select(l => l.Name).ToList();
        CompletenessNote = uncertain.Count == 0 ? null : Loc.Format("Assets_Incomplete", string.Join(", ", uncertain));
        var stale = worth.Lines.Where(l => l.IsStale).Select(l => l.Name).ToList();
        StaleNote = stale.Count == 0 ? null : Loc.Format("Assets_Stale", string.Join(", ", stale), NetWorthService.StaleAfterDays);

        Fill(AssetLines, worth.Lines.Where(l => !l.IsLiability), today);
        Fill(LiabilityLines, worth.Lines.Where(l => l.IsLiability), today);

        var history = await _netWorth.HistoryAsync();
        History.Clear();
        var max = Math.Max(1, history.Points.Select(p => Math.Abs(p.Total.Cents)).DefaultIfEmpty(0).Max());
        foreach (var p in history.Points)
        {
            History.Add(new NetWorthBarViewModel(
                p.Month.ToString("MMM yyyy", Loc.Instance.Culture),
                LedgerText.Amount(p.Total),
                Math.Abs(p.Total.Cents),
                max,
                p.Total.IsNegative,
                p.IsComplete));
        }

        HistoryNote = history.IsComplete ? null : Loc.T("Assets_HistoryIncomplete");
        OnPropertyChanged(nameof(HasLiabilities));
        OnPropertyChanged(nameof(HasHistory));
    }

    private void Fill(ObservableCollection<NetWorthLineViewModel> target, IEnumerable<NetWorthLine> lines, DateOnly today)
    {
        target.Clear();
        foreach (var line in lines)
        {
            target.Add(new NetWorthLineViewModel(line, today, OpenPanelAsync));
        }

        if (target.Count > 0)
        {
            target[^1].IsLast = true;
        }
    }

    /// <summary>A percentage typed like an amount gives basis points exactly (50 → 5000).</summary>
    private static bool TryShare(string text, out Share share)
    {
        var parsed = AmountParser.Parse(text);
        if (!parsed.Success || parsed.IsAmbiguous || parsed.Value.Cents is < 1 or > Share.FullBasisPoints)
        {
            share = Share.Full;
            return false;
        }

        share = new Share((int)parsed.Value.Cents);
        return true;
    }

    protected override void OnLanguageChanged()
    {
        // Read the choice first: replacing the options clears the selection in the list.
        var kind = NewKind?.Kind ?? AssetKind.Property;
        KindOptions = AssetKindOption.All();
        NewKind = KindOptions.First(k => k.Kind == kind);
        StatusMessage = null;
        Selected = null;
        if (_active)
        {
            _ = ReloadAsync();
        }
    }
}
