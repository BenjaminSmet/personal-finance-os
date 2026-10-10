using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.QuickAdd;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>
/// The global Quick Add bar (spec §7). Shows live how a line is read; Enter posts a clear entry, while
/// anything uncertain opens an editable preview first. Nothing is saved before the user confirms.
/// </summary>
public sealed partial class QuickAddViewModel : ViewModelBase
{
    private static readonly TransactionType[] EditableTypes =
        [TransactionType.Expense, TransactionType.Income, TransactionType.Refund, TransactionType.Transfer];

    private readonly QuickAddService _quickAdd;
    private readonly LedgerService _ledger;
    private QuickAddContext? _context;

    public QuickAddViewModel(QuickAddService quickAdd, LedgerService ledger)
    {
        _quickAdd = quickAdd;
        _ledger = ledger;
        ledger.History.Changed += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CanUndo)));
        Loc.Instance.LanguageChanged += (_, _) => RefreshTexts();
    }

    public TransactionEditorViewModel Editor { get; } = new();

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraft))]
    private QuickAddDraft? _draft;

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string? _issueText;

    [ObservableProperty]
    private bool _isReviewOpen;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _error;

    public bool HasDraft => Draft is not null && !IsReviewOpen;

    public bool CanUndo => _ledger.History.CanUndo;

    /// <summary>Raised when the text box should take keyboard focus (Ctrl+K).</summary>
    public event EventHandler? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    async partial void OnTextChanged(string value)
    {
        Error = null;
        if (IsReviewOpen)
        {
            IsReviewOpen = false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            Draft = null;
            RefreshTexts();
            return;
        }

        StatusMessage = null;
        if (_context is null || value.Trim().Length == 1)
        {
            // Accounts, categories and learned rules are read fresh when a new entry starts.
            _context = await _quickAdd.LoadContextAsync();
        }

        if (value != Text)
        {
            return; // a newer keystroke already took over
        }

        Draft = QuickAddParser.Parse(value, _context);
        RefreshTexts();
    }

    partial void OnIsReviewOpenChanged(bool value) => OnPropertyChanged(nameof(HasDraft));

    /// <summary>Enter: post a clear entry, or open the preview when something needs a look.</summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsReviewOpen)
        {
            await SaveReviewAsync();
            return;
        }

        if (Draft is not { } draft)
        {
            return;
        }

        if (draft.NeedsReview)
        {
            OpenReview(draft);
            return;
        }

        await ConfirmAsync(draft);
    }

    [RelayCommand]
    private void Review()
    {
        if (Draft is { } draft)
        {
            OpenReview(draft);
        }
    }

    [RelayCommand]
    private async Task SaveReviewAsync()
    {
        if (Draft is not { } draft || !Editor.TryRead(out var v))
        {
            return;
        }

        await ConfirmAsync(draft with
        {
            Type = v.Type,
            Amount = v.Amount,
            Description = v.Description,
            AccountId = v.AccountId,
            DestinationAccountId = v.DestinationId,
            CategoryId = v.CategoryId,
            Date = v.Date,
        });
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsReviewOpen)
        {
            IsReviewOpen = false;
            return;
        }

        Text = string.Empty;
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        try
        {
            if (await _ledger.UndoAsync())
            {
                StatusMessage = Loc.T("QuickAdd_Undone");
            }
        }
        catch (DomainValidationException ex)
        {
            Error = Loc.Error(ex.Code);
        }
    }

    private void OpenReview(QuickAddDraft draft)
    {
        if (_context is null)
        {
            return;
        }

        Editor.Load(
            _context.Accounts,
            _context.Categories,
            EditableTypes,
            draft.Type,
            draft.Amount,
            draft.Description,
            draft.AccountId,
            draft.DestinationAccountId,
            draft.CategoryId,
            draft.Date);
        IsReviewOpen = true;
    }

    private async Task ConfirmAsync(QuickAddDraft draft)
    {
        Error = null;
        try
        {
            await _quickAdd.ConfirmAsync(draft);
        }
        catch (DomainValidationException ex)
        {
            if (IsReviewOpen)
            {
                Editor.Error = Loc.Error(ex.Code);
            }
            else
            {
                Error = Loc.Error(ex.Code);
            }

            return;
        }

        var saved = Describe(draft);
        IsReviewOpen = false;
        _context = null; // learned rules and last-used account changed
        Text = string.Empty;
        StatusMessage = Loc.Format("QuickAdd_Saved", saved);
    }

    private void RefreshTexts()
    {
        Summary = Draft is null ? null : Describe(Draft);
        IssueText = Draft is { Issues.Count: > 0 } d ? string.Join(" · ", d.Issues.Select(LedgerText.Issue)) : null;
    }

    /// <summary>"Expense · €42.80 · Supermarkt · Current account · Groceries · today".</summary>
    private string Describe(QuickAddDraft draft)
    {
        string? Account(Guid? id) => _context?.Accounts.FirstOrDefault(a => a.Id == id)?.Name;

        var parts = new List<string?> { LedgerText.Type(draft.Type), draft.Amount.IsZero ? "?" : LedgerText.Amount(draft.Amount) };
        if (draft.Description.Length > 0)
        {
            parts.Add(draft.Description);
        }

        parts.Add(draft.IsTransfer
            ? $"{Account(draft.AccountId) ?? "?"} → {Account(draft.DestinationAccountId) ?? "?"}"
            : Account(draft.AccountId) ?? "?");
        parts.Add(_context?.Categories.FirstOrDefault(c => c.Id == draft.CategoryId)?.Name);
        parts.Add(LedgerText.Date(draft.Date, _context?.Today ?? draft.Date));
        return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}
