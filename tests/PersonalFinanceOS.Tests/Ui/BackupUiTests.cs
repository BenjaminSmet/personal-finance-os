using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Tests.Infrastructure;
using static PersonalFinanceOS.Tests.Ui.LedgerUiTests;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Phase 8 through the real UI: a manual backup from Settings, the reminder, and a guided restore.</summary>
[Collection("UI")]
public class BackupUiTests
{
    [AvaloniaFact]
    public async Task A_backup_is_made_from_settings_and_restored_after_an_explicit_confirmation()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var usb = Path.Combine(Path.GetTempPath(), "pfos-usb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(usb);
        var (window, vm) = Start(temp);
        Window? reopened = null;
        try
        {
            // No backup yet: the dashboard reminds and leads to Settings.
            var dashboard = (DashboardViewModel)vm.CurrentPage;
            HeadlessSetup.PumpUntil(() => dashboard.Attention.Any(a => a.Target == DashboardTarget.Settings));
            dashboard.Attention.Single(a => a.Target == DashboardTarget.Settings).OpenCommand.Execute(null);
            var settings = Assert.IsType<SettingsViewModel>(vm.CurrentPage);
            var backup = settings.Backup;
            HeadlessSetup.PumpUntil(() => backup.LastBackupText == "Nog geen back-up gemaakt");
            Assert.True(backup.IsBackupDue);

            var target = Path.Combine(usb, "backup.zip");
            backup.PickBackupTarget = name =>
            {
                Assert.StartsWith("Personal Finance OS backup ", name, StringComparison.Ordinal);
                return Task.FromResult<string?>(target);
            };
            await backup.MakeBackupCommand.ExecuteAsync(null);
            Assert.Null(backup.Error);
            Assert.StartsWith("Back-up backup.zip gemaakt en gecontroleerd", backup.Message, StringComparison.Ordinal);
            Assert.NotNull(backup.Warning); // same drive as the data in this test
            Assert.True(File.Exists(target));
            HeadlessSetup.PumpUntil(() => backup.LastBackupText.StartsWith("Laatste back-up:", StringComparison.Ordinal) && !backup.IsBackupDue);
            window.Height = 1900;
            Save(window, "47-backup");

            // The reminder interval is a setting.
            backup.SelectedReminder = backup.Reminders.Single(r => r.Days == 7);
            HeadlessSetup.PumpUntil(() => temp.Scalar("SELECT COUNT(*) FROM app_settings WHERE key = 'backup.reminderDays' AND value = '7';") == 1);

            // Later: a transaction that the backup does not have.
            temp.Execute("""
                INSERT INTO transactions (id, account_id, date, amount_cents, type, description, source, status, created_at, updated_at)
                SELECT '33333333-3333-3333-3333-333333333333', id, '2026-10-05', -9999, 'Expense', 'Na de back-up', 'Manual', 'Active', '2026-10-05T00:00:00.0000000Z', '2026-10-05T00:00:00.0000000Z'
                FROM accounts ORDER BY sort_order LIMIT 1;
                """);
            Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM transactions;"));

            // Choosing a file only shows what it holds; nothing changes until the explicit confirmation.
            backup.PickRestoreFile = () => Task.FromResult<string?>(target);
            await backup.ChooseRestoreCommand.ExecuteAsync(null);
            Assert.True(backup.IsConfirmingRestore);
            Assert.Contains("4 rekeningen en 0 transacties", backup.RestoreSummary, StringComparison.Ordinal);
            Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM transactions;"));
            Save(window, "48-restore-confirm");
            backup.CancelRestoreCommand.Execute(null);
            Assert.False(backup.IsConfirmingRestore);

            await backup.ChooseRestoreCommand.ExecuteAsync(null);
            var app = (PersonalFinanceOS.App.App)Avalonia.Application.Current!;
            void OnRestarted(Window next) => reopened = next;
            app.Restarted += OnRestarted;
            try
            {
                await backup.ConfirmRestoreCommand.ExecuteAsync(null);
            }
            finally
            {
                app.Restarted -= OnRestarted;
            }

            Assert.Null(backup.Error);
            Assert.NotNull(reopened);
            Assert.False(window.IsVisible);
            Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM transactions;"));
            Assert.Single(Directory.GetFiles(temp.Paths.BackupsDirectory, "*before-restore*.db"));
            var restored = Assert.IsType<MainWindowViewModel>(reopened!.DataContext);
            var newDashboard = (DashboardViewModel)restored.CurrentPage;
            HeadlessSetup.PumpUntil(() => newDashboard.ActiveAccountCount == 4);
        }
        finally
        {
            reopened?.Close();
            window.Close();
            Directory.Delete(usb, recursive: true);
        }
    }
}
