using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Import;

namespace PersonalFinanceOS.App.Views;

public partial class ImportView : UserControl
{
    public ImportView()
    {
        InitializeComponent();
    }

    /// <summary>Opens the system file dialog. The file is read into memory once and never copied or kept.</summary>
    private async void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImportViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Import_ChooseFile"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.T("Import_FileType")) { Patterns = ["*.csv", "*.txt", "*.xlsx"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0)
        {
            return;
        }

        byte[] content;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > ImportFile.MaxBytes)
                {
                    vm.Error = Loc.Error("import.file.tooLarge");
                    return;
                }

                buffer.Write(chunk, 0, read);
            }

            content = buffer.ToArray();
        }
        catch (IOException)
        {
            vm.Error = Loc.Error("import.file.unreadable");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            vm.Error = Loc.Error("import.file.unreadable");
            return;
        }

        await vm.LoadFileAsync(files[0].Name, content);
    }
}
