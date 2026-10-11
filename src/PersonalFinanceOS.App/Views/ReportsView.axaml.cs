using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;

namespace PersonalFinanceOS.App.Views;

public partial class ReportsView : UserControl
{
    private IStorageFile? _saved;

    public ReportsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ReportsViewModel vm)
        {
            vm.SaveTarget ??= PickAsync;
            vm.OpenSaved ??= OpenAsync;
        }
    }

    private async void OnSavePdf(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ReportsViewModel vm)
        {
            await vm.SavePdfCommand.ExecuteAsync(null);
        }
    }

    private async void OnSaveCsv(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ReportsViewModel vm)
        {
            await vm.SaveCsvCommand.ExecuteAsync(null);
        }
    }

    /// <summary>The system save dialog: the user always chooses where a report goes. Nothing is saved anywhere else.</summary>
    private async Task<Stream?> PickAsync(string fileName, ReportFileKind kind)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var pdf = kind == ReportFileKind.Pdf;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T(pdf ? "Reports_SavePdf" : "Reports_SaveCsv"),
            SuggestedFileName = fileName,
            DefaultExtension = pdf ? "pdf" : "csv",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                pdf
                    ? new FilePickerFileType(Loc.T("Reports_PdfType")) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }
                    : new FilePickerFileType(Loc.T("Reports_CsvType")) { Patterns = ["*.csv"], MimeTypes = ["text/csv"] },
            ],
        });
        if (file is null)
        {
            return null;
        }

        _saved = file;
        return await file.OpenWriteAsync();
    }

    private async Task OpenAsync()
    {
        if (_saved is not null && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchFileAsync(_saved);
        }
    }
}
