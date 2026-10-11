using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Assistant;
using PersonalFinanceOS.Application.Scenarios;
using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>Turns the assistant's text keys and values into words in the current language. Display only.</summary>
internal static class AnswerFormat
{
    public static string Text(AnswerText text, DateOnly today) =>
        Loc.Format(text.Key, text.Args.Select(a => Value(a, today)).ToArray());

    public static string Value(object value, DateOnly today) => value switch
    {
        Money m => LedgerText.Amount(m),
        DateOnly d => LedgerText.Date(d, today),
        MonthRef m => m.Month.ToString("MMMM yyyy", Loc.Instance.Culture),
        AnswerText t => Text(t, today),
        int i => i.ToString(Loc.Instance.Culture),
        _ => value.ToString() ?? string.Empty,
    };
}

public abstract class ChatMessageViewModel : ViewModelBase
{
    /// <summary>Re-reads every text, for example after a language change.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class UserMessageViewModel(string text) : ChatMessageViewModel
{
    public string Text { get; } = text;
}

public sealed record FigureViewModel(string Label, string Value);

/// <summary>A button under an answer: a next step, a short reply or an example question.</summary>
public sealed class ChoiceViewModel(string text, IAsyncRelayCommand command, bool isPrimary = false)
{
    public string Text { get; } = text;

    public IAsyncRelayCommand Command { get; } = command;

    public bool IsPrimary { get; } = isPrimary;
}

/// <summary>One answer: direct or qualified, key figures, what was taken into account, assumptions and next steps (spec §15).</summary>
public sealed partial class AnswerMessageViewModel(AssistantAnswer answer, DateOnly today, Func<AnswerMessageViewModel, AssistantAction, Task> act, Func<string, Task> ask) : ChatMessageViewModel
{
    public AssistantAnswer Answer { get; } = answer;

    /// <summary>Set once the what-if was saved as a scenario.</summary>
    public Guid? SavedScenarioId { get; set; }

    public string Headline => AnswerFormat.Text(Answer.Headline, today);

    /// <summary>Shown above the answer in words, so the kind never depends on colour.</summary>
    public string? KindText => Answer.Kind switch
    {
        AnswerKind.Qualified => Loc.T("Ask_Kind_Qualified"),
        AnswerKind.Question => Loc.T("Ask_Kind_Question"),
        _ => null,
    };

    public bool IsQualified => Answer.Kind == AnswerKind.Qualified;

    public IReadOnlyList<FigureViewModel> Figures =>
        Answer.Figures.Select(f => new FigureViewModel(AnswerFormat.Text(f.Label, today), AnswerFormat.Value(f.Value, today))).ToList();

    public bool HasFigures => Answer.Figures.Count > 0;

    public IReadOnlyList<string> Considered => Answer.Considered.Select(t => AnswerFormat.Text(t, today)).ToList();

    public bool HasConsidered => Answer.Considered.Count > 0;

    public IReadOnlyList<string> Assumptions => Answer.Assumptions.Select(t => AnswerFormat.Text(t, today)).ToList();

    public bool HasAssumptions => Answer.Assumptions.Count > 0;

    public string? Explanation => Answer.Explanation is { } e ? AnswerFormat.Text(e, today) : null;

    public string? SavedText => SavedScenarioId is null ? null : Loc.Format("Ask_ScenarioSaved", Answer.ScenarioName ?? string.Empty);

    public IReadOnlyList<ChoiceViewModel> Actions => Answer.Actions
        .Select(a => a == AssistantAction.SaveScenario && SavedScenarioId is not null
            ? new ChoiceViewModel(Loc.T("Ask_Action_OpenSaved"), new AsyncRelayCommand(() => act(this, AssistantAction.OpenScenarios)), true)
            : new ChoiceViewModel(Loc.T("Ask_Action_" + a), new AsyncRelayCommand(() => act(this, a)), a == AssistantAction.SaveScenario))
        .ToList();

    public IReadOnlyList<ChoiceViewModel> Suggestions => Answer.Suggestions
        .Select(s => Answer.SuggestionsAreExamples ? Loc.T(s) : s)
        .Select(text => new ChoiceViewModel(text, new AsyncRelayCommand(() => ask(text))))
        .ToList();

    public bool HasActions => Answer.Actions.Count > 0;

    public bool HasSuggestions => Answer.Suggestions.Count > 0;
}

/// <summary>
/// The assistant page (spec §15, §24 phase 7): questions in plain Dutch or English, answered on this computer by the
/// rule-based <see cref="AssistantService"/> with figures from the calculation services. It never changes transactions;
/// saving a what-if as a scenario happens only when the user clicks it.
/// </summary>
public sealed partial class AssistantViewModel : PageViewModel
{
    private readonly AssistantService _assistant;
    private readonly ScenarioService _scenarios;
    private readonly IClock _clock;
    private PendingQuestion? _pending;

    public AssistantViewModel(AssistantService assistant, ScenarioService scenarios, IClock clock)
    {
        _assistant = assistant;
        _scenarios = scenarios;
        _clock = clock;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));
    }

    /// <summary>Opens a page for an answer's next step; the scenario id is set when one was just saved.</summary>
    public Func<AssistantAction, Guid?, Task>? Navigate { get; set; }

    /// <summary>Raised after an answer is added, so the view can scroll to it.</summary>
    public event EventHandler? AnswerAdded;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    public IReadOnlyList<ChoiceViewModel> Examples =>
        AssistantService.Examples.Select(k => new ChoiceViewModel(Loc.T(k), new AsyncRelayCommand(() => AskTextAsync(Loc.T(k))))).ToList();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private string _question = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private bool _isBusy;

    private bool CanAsk => !IsBusy && !string.IsNullOrWhiteSpace(Question);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task Ask()
    {
        var text = Question.Trim();
        Question = string.Empty;
        await AskTextAsync(text);
    }

    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
        _pending = null;
    }

    /// <summary>Asks a question as if typed; used by the buttons under answers.</summary>
    public async Task AskTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Messages.Add(new UserMessageViewModel(text));
            var answer = await _assistant.AskAsync(text, _pending);
            _pending = answer.Pending;
            Messages.Add(new AnswerMessageViewModel(answer, _clock.Today, ActAsync, AskTextAsync));
            AnswerAdded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ActAsync(AnswerMessageViewModel message, AssistantAction action)
    {
        if (action == AssistantAction.SaveScenario)
        {
            if (message.SavedScenarioId is null && message.Answer.Scenario is { } changes)
            {
                var scenario = await _scenarios.CreateAsync(message.Answer.ScenarioName ?? Loc.T("Nav_Assistant"), null, changes);
                message.SavedScenarioId = scenario.Id;
                message.Refresh();
            }

            return;
        }

        if (Navigate is not null)
        {
            await Navigate(action, message.SavedScenarioId);
        }
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Examples));
        foreach (var message in Messages)
        {
            message.Refresh();
        }
    }
}
