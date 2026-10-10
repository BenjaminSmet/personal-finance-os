using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Infrastructure;
using PersonalFinanceOS.Infrastructure.Database;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>
/// Shown instead of the main window when the database cannot be opened safely.
/// The app never continues on a database it could not verify (spec §4).
/// </summary>
public sealed partial class StartupProblemViewModel(
    DatabaseStartupResult result,
    AppDataPaths paths,
    Func<DatabaseStartupResult> startNewProfile,
    Action<DatabaseStartupResult> onRecovered,
    Action close) : ViewModelBase
{
    public string Message => Loc.T(result.Status switch
    {
        DatabaseStartupStatus.DatabaseMissing => "Startup_DatabaseMissing",
        DatabaseStartupStatus.DatabaseNewerThanApp => "Startup_DatabaseNewer",
        DatabaseStartupStatus.BackupFailed => "Startup_BackupFailed",
        DatabaseStartupStatus.MigrationFailedRestored => "Startup_MigrationRestored",
        DatabaseStartupStatus.MigrationFailedManualRecovery => "Startup_MigrationManual",
        _ => "Startup_DatabaseUnreadable",
    });

    public string DataFolderText => Loc.Format("Startup_DataFolder", paths.Root);

    public string? BackupText => result.BackupFile is null ? null : Loc.Format("Startup_BackupFile", result.BackupFile);

    public bool HasBackup => result.BackupFile is not null;

    public bool CanStartNew => result.Status == DatabaseStartupStatus.DatabaseMissing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNewCommand))]
    private bool _confirmStartNew;

    [RelayCommand]
    private void OpenFolder() => FolderOpener.Open(paths.Root);

    [RelayCommand]
    private void Close() => close();

    [RelayCommand(CanExecute = nameof(ConfirmStartNew))]
    private void StartNew()
    {
        var created = startNewProfile();
        if (created.CanOpen)
        {
            onRecovered(created);
        }
    }
}
