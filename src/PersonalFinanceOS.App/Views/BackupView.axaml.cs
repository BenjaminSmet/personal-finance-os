using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;

namespace PersonalFinanceOS.App.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BackupViewModel vm)
        {
            vm.PickBackupTarget ??= PickTargetAsync;
            vm.PickRestoreFile ??= PickRestoreAsync;
        }
    }

    private async void OnMakeBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BackupViewModel vm)
        {
            await vm.MakeBackupCommand.ExecuteAsync(null);
        }
    }

    private async void OnChooseRestore(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BackupViewModel vm)
        {
            await vm.ChooseRestoreCommand.ExecuteAsync(null);
        }
    }

    /// <summary>The user picks where the backup goes, ideally another drive or a USB stick.</summary>
    private async Task<string?> PickTargetAsync(string suggestedName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Backup_Make"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(Loc.T("Backup_FileType")) { Patterns = ["*.zip"] }],
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickRestoreAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Backup_Choose"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.T("Backup_FileType")) { Patterns = ["*.zip"] }],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
