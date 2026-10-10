using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record AccountTypeOption(AccountType Type, string Name)
{
    public static IReadOnlyList<AccountTypeOption> All() =>
        Enum.GetValues<AccountType>().Select(t => new AccountTypeOption(t, Loc.T("AccountType_" + t))).ToList();
}

public sealed partial class AccountRowViewModel(Account account, Func<AccountRowViewModel, Task> archive) : ViewModelBase
{
    public Account Account { get; } = account;

    /// <summary>Last row in the grouped list (no separator below it).</summary>
    public bool IsLast { get; set; }

    public Geometry Icon => AccountTypeVisuals.Icon(Account.Type);

    public IBrush IconBackground => AccountTypeVisuals.Background(Account.Type);

    public string Name => Account.Name;
    public string TypeName => Loc.T("AccountType_" + Account.Type);
    public bool IsArchived => Account.Status == AccountStatus.Archived;
    public bool IsLiability => Account.Type.IsLiability();

    // Phase 1 has no transactions yet, so the shown balance is the opening balance.
    public string BalanceText => Account.OpeningBalance.Format(Loc.Instance.Culture);

    public string SinceText => Loc.Format("Accounts_Since", Account.OpeningDate.ToString("d", Loc.Instance.Culture));

    public bool HasPartialShare => !Account.OwnershipShare.IsFull;

    public string ShareText => HasPartialShare
        ? $"{Account.OwnershipShare.Format(Loc.Instance.Culture)} · {Loc.Format("Accounts_YourPart", Account.PersonalPart(Account.OpeningBalance).Format(Loc.Instance.Culture))}"
        : string.Empty;

    [RelayCommand]
    private Task Archive() => archive(this);
}

public sealed partial class AccountsViewModel : PageViewModel
{
    private readonly AccountService _accounts;
    private readonly AppSettingsService _settings;
    private readonly IClock _clock;

    public AccountsViewModel(AccountService accounts, AppSettingsService settings, IClock clock)
    {
        _accounts = accounts;
        _settings = settings;
        _clock = clock;
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

    public bool IsCreditCard => NewType.Type.IsLiability();

    public string BalanceLabel => Loc.T(IsCreditCard ? "Accounts_OutstandingBalance" : "Accounts_OpeningBalance");

    public override Task ActivateAsync() => ReloadAsync();

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
    private void OpenForm()
    {
        ResetForm();
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

        try
        {
            await _accounts.CreateAsync(new CreateAccountRequest(
                NewName,
                NewType.Type,
                DateOnly.FromDateTime(date),
                opening,
                new Share((int)share.Value.Cents),
                NewNotes));
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

    private async Task ReloadAsync()
    {
        var list = await _accounts.ListAsync(ShowArchived);
        Rows.Clear();
        for (var i = 0; i < list.Count; i++)
        {
            Rows.Add(new AccountRowViewModel(list[i], ArchiveAsync) { IsLast = i == list.Count - 1 });
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
    }

    protected override void OnLanguageChanged()
    {
        var selected = NewType.Type;
        TypeOptions = AccountTypeOption.All();
        NewType = TypeOptions.First(o => o.Type == selected);
        OnPropertyChanged(nameof(BalanceLabel));
        StatusMessage = null;
        FormError = null;
        _ = ReloadAsync();
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
