using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PersonalFinanceOS.App.ViewModels;

namespace PersonalFinanceOS.App.Views;

public partial class AssistantView : UserControl
{
    private AssistantViewModel? _viewModel;

    public AssistantView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.AnswerAdded -= OnAnswerAdded;
            }

            _viewModel = DataContext as AssistantViewModel;
            if (_viewModel is not null)
            {
                _viewModel.AnswerAdded += OnAnswerAdded;
            }
        };
    }

    /// <summary>Keeps the newest answer and the question box in view.</summary>
    private void OnAnswerAdded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(
        () =>
        {
            this.FindAncestorOfType<ScrollViewer>()?.ScrollToEnd();
            QuestionBox.Focus();
        },
        DispatcherPriority.Background);
}
