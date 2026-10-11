using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Infrastructure.Database;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record ReminderOption(int Days, string Name);

/// <summary>
/// Manual backup and guided restore on the Settings page (spec §4). A backup goes to a file the user picks and is only
/// reported as made after it was read back and checked. Restoring shows what the file holds, asks for an explicit
/// confirmation, keeps a verified copy of the current data, and then reopens the app on the restored data.
/// </summary>
public sealed partial class BackupViewModel : ViewModelBase
{
    private readonly BackupArchive _archive;
    private readonly AppSettingsService _settings;
    private readonly IClock _clock;
    private bool _loading;
    private string? _restoreFile;

    public BackupViewModel(BackupArchive archive, AppSettingsService settings, IClock clock)
    {
        _archive = archive;
        _settings = settings;
        _clock = clock;
        Reminders = ReminderOptions();
        _selectedReminder = Reminders.Single(r => r.Days == AppSettingsService.DefaultBackupReminderDays);
    }

    public IReadOnlyList<ReminderOption> Reminders { get; private set; }

    [ObservableProperty]
    private ReminderOption _selectedReminder;

    [ObservableProperty]
    private string _lastBackupText = string.Empty;

    [ObservableProperty]
    private bool _isBackupDue;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingRestore))]
    private BackupInfo? _restoreInfo;

    [ObservableProperty]
    private string _restoreSummary = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsConfirmingRestore => RestoreInfo is not null;

    /// <summary>Asks for the backup file to write; null when cancelled. Set by the view (tests set their own).</summary>
    public Func<string, Task<string?>>? PickBackupTarget { get; set; }

    /// <summary>Asks for a backup file to restore; null when cancelled. Set by the view.</summary>
    public Func<Task<string?>>? PickRestoreFile { get; set; }

    /// <summary>Reopens the app on the restored data. Set by the app.</summary>
    public Action? Restored { get; set; }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var days = await _settings.GetBackupReminderDaysAsync();
            SelectedReminder = Reminders.Single(r => r.Days == days);
            var last = await _settings.GetLastBackupAsync();
            LastBackupText = last is { } at
                ? Loc.Format("Backup_Last", at.ToLocalTime().ToString("d MMMM yyyy HH:mm", Loc.Instance.Culture))
                : Loc.T("Backup_Never");
            IsBackupDue = await _settings.IsBackupDueAsync(_clock.Now);
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    public async Task MakeBackup()
    {
        Clear();
        if (PickBackupTarget is not { } pick || await pick(BackupArchive.SuggestedFileName(_clock.Now)) is not { } target)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _archive.Create(target, _clock.Now));
            if (!result.Success)
            {
                Error = Loc.Error(result.ErrorCode!);
                return;
            }

            await _settings.SetLastBackupAsync(_clock.Now);
            Message = Loc.Format("Backup_Done", Path.GetFileName(target), result.Info!.Transactions);
            if (_archive.IsOnSameDrive(Path.GetDirectoryName(target)!))
            {
                Warning = Loc.T("Backup_SameDrive");
            }

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ChooseRestore()
    {
        Clear();
        if (PickRestoreFile is not { } pick || await pick() is not { } file)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var inspection = await Task.Run(() => _archive.Inspect(file));
            if (!inspection.IsValid)
            {
                Error = Loc.Error(inspection.ErrorCode!);
                return;
            }

            _restoreFile = file;
            var info = inspection.Info!;
            RestoreSummary = Loc.Format(
                "Backup_RestoreSummary",
                Path.GetFileName(file),
                info.CreatedAt.ToLocalTime().ToString("d MMMM yyyy HH:mm", Loc.Instance.Culture),
                info.Accounts,
                info.Transactions,
                info.AppVersion);
            RestoreInfo = info;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void CancelRestore()
    {
        RestoreInfo = null;
        _restoreFile = null;
    }

    /// <summary>The explicit confirmation: replaces the current data after a verified copy of it was made.</summary>
    [RelayCommand]
    public async Task ConfirmRestore()
    {
        if (_restoreFile is not { } file)
        {
            return;
        }

        Clear();
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _archive.Restore(file));
            RestoreInfo = null;
            _restoreFile = null;
            if (!result.Success)
            {
                Error = Loc.Error(result.ErrorCode!);
                return;
            }

            Message = Loc.T("Backup_Restored");
            Restored?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedReminderChanged(ReminderOption value)
    {
        if (!_loading)
        {
            _ = SaveReminderAsync(value.Days);
        }
    }

    private async Task SaveReminderAsync(int days)
    {
        await _settings.SetBackupReminderDaysAsync(days);
        IsBackupDue = await _settings.IsBackupDueAsync(_clock.Now);
    }

    public void OnLanguageChanged()
    {
        var days = SelectedReminder.Days;
        _loading = true;
        Reminders = ReminderOptions();
        OnPropertyChanged(nameof(Reminders));
        SelectedReminder = Reminders.Single(r => r.Days == days);
        _loading = false;
        _ = LoadAsync();
    }

    private void Clear()
    {
        Message = null;
        Warning = null;
        Error = null;
    }

    private static IReadOnlyList<ReminderOption> ReminderOptions() =>
        AppSettingsService.BackupReminderChoices
            .Select(d => new ReminderOption(d, d == 0 ? Loc.T("Backup_ReminderOff") : Loc.Format("Backup_ReminderDays", d)))
            .ToList();
}
