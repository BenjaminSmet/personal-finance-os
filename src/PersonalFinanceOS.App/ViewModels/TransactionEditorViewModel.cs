using CommunityToolkit.Mvvm.ComponentModel;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record TypeOption(TransactionType Type, string Name);

public sealed record AccountOption(Guid Id, string Name);

public sealed record CategoryOption(Guid? Id, string Name);

/// <summary>What the editor holds once it is valid. <see cref="Amount"/> is positive.</summary>
public sealed record EditorValues(
    TransactionType Type,
    Money Amount,
    string Description,
    Guid AccountId,
    Guid? DestinationId,
    Guid? CategoryId,
    DateOnly Date,
    string? Note);

/// <summary>
/// One form for every way a transaction is entered or corrected: the Quick Add preview, a new
/// transfer and editing an existing line. It only collects and checks input; saving is up to the owner.
/// </summary>
public sealed partial class TransactionEditorViewModel : ViewModelBase
{
    private IReadOnlyList<Category> _categories = [];

    [ObservableProperty]
    private IReadOnlyList<TypeOption> _typeOptions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransfer), nameof(ShowCategory), nameof(AccountLabel))]
    private TypeOption? _selectedType;

    [ObservableProperty]
    private string _amountText = string.Empty;

    [ObservableProperty]
    private string? _amountHint;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _accountOptions = [];

    [ObservableProperty]
    private AccountOption? _selectedAccount;

    [ObservableProperty]
    private AccountOption? _selectedDestination;

    [ObservableProperty]
    private IReadOnlyList<CategoryOption> _categoryOptions = [];

    [ObservableProperty]
    private CategoryOption? _selectedCategory;

    [ObservableProperty]
    private DateTime? _date;

    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private string? _error;

    /// <summary>Shown when the change affects linked records (spec §7 "warn before changing a linked record").</summary>
    [ObservableProperty]
    private string? _warning;

    public bool IsTransfer => SelectedType?.Type == TransactionType.Transfer;

    public bool ShowCategory => !IsTransfer;

    public string AccountLabel => Loc.T(IsTransfer ? "Editor_From" : "Editor_Account");

    public void Load(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Category> categories,
        IReadOnlyList<TransactionType> allowedTypes,
        TransactionType type,
        Money amount,
        string description,
        Guid? accountId,
        Guid? destinationId,
        Guid? categoryId,
        DateOnly date,
        string? note = null,
        string? warning = null)
    {
        _categories = categories;
        TypeOptions = allowedTypes.Select(t => new TypeOption(t, LedgerText.Type(t))).ToList();
        AccountOptions = accounts.Select(a => new AccountOption(a.Id, a.Name)).ToList();
        SelectedType = TypeOptions.FirstOrDefault(o => o.Type == type) ?? TypeOptions.FirstOrDefault();
        AmountText = amount.IsZero ? string.Empty : amount.ToEuros().ToString("0.00", Loc.Instance.Culture);
        Description = description;
        SelectedAccount = AccountOptions.FirstOrDefault(o => o.Id == accountId);
        SelectedDestination = AccountOptions.FirstOrDefault(o => o.Id == destinationId);
        RefreshCategories(categoryId);
        Date = date.ToDateTime(TimeOnly.MinValue);
        Note = note;
        Warning = warning;
        Error = null;
    }

    partial void OnSelectedTypeChanged(TypeOption? value) => RefreshCategories(SelectedCategory?.Id);

    partial void OnAmountTextChanged(string value)
    {
        var parsed = AmountParser.Parse(value);
        AmountHint = parsed is { Success: true, IsAmbiguous: true }
            ? Loc.Format("Warning_amount_ambiguous", LedgerText.Amount(parsed.Value))
            : null;
    }

    /// <summary>Checks the input; on failure <see cref="Error"/> says what to fix.</summary>
    public bool TryRead(out EditorValues values)
    {
        values = null!;
        Error = null;

        var amount = AmountParser.Parse(AmountText);
        if (!amount.Success || !amount.Value.IsPositive)
        {
            Error = Loc.Error(amount.Success ? "transaction.amount.zero" : "amount.invalid");
            return false;
        }

        if (SelectedType is null || SelectedAccount is null)
        {
            Error = Loc.Error("quickAdd.noAccount");
            return false;
        }

        if (IsTransfer && SelectedDestination is null)
        {
            Error = Loc.Error("quickAdd.noDestination");
            return false;
        }

        if (Date is not { } date)
        {
            Error = Loc.Error("date.required");
            return false;
        }

        values = new EditorValues(
            SelectedType.Type,
            amount.Value,
            Description,
            SelectedAccount.Id,
            IsTransfer ? SelectedDestination!.Id : null,
            ShowCategory ? SelectedCategory?.Id : null,
            DateOnly.FromDateTime(date),
            Note);
        return true;
    }

    private void RefreshCategories(Guid? keep)
    {
        var kind = SelectedType?.Type.CategoryKind();
        var options = new List<CategoryOption> { new(null, Loc.T("Editor_NoCategory")) };
        if (kind is not null)
        {
            options.AddRange(_categories.Where(c => c.Kind == kind).Select(c => new CategoryOption(c.Id, c.Name)));
        }

        CategoryOptions = options;
        SelectedCategory = options.FirstOrDefault(o => o.Id == keep) ?? options[0];
    }
}
