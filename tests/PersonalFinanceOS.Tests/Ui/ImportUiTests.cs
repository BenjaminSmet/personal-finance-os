using Avalonia.Headless.XUnit;
using PersonalFinanceOS.App;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Import;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Tests.Infrastructure;
using static PersonalFinanceOS.Tests.Ui.LedgerUiTests;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Phase 3 through the real UI: choose a file, review every line, import, undo.</summary>
[Collection("UI")]
public class ImportUiTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Import", name));

    private static ImportViewModel OpenImport(MainWindowViewModel vm, string account)
    {
        var import = Navigate<ImportViewModel>(vm);
        HeadlessSetup.PumpUntil(() => import.AccountOptions.Count == 4);
        import.SelectedAccount = import.AccountOptions.Single(a => a.Name == account);
        return import;
    }

    [AvaloniaFact]
    public async Task Bank_file_is_previewed_line_by_line_imported_and_undone()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);
        var import = OpenImport(vm, "Zichtrekening");

        await import.LoadFileAsync("kbc-style.csv", Fixture("kbc-style.csv"));

        Assert.Equal(ImportStep.Review, import.Step);
        Assert.True(import.IsMappingOpen); // a new layout: the columns are shown to check
        Assert.Equal(5, import.Rows.Count);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM transactions;")); // a preview is not a posting
        Assert.Contains("4 nieuw", import.SummaryText, StringComparison.Ordinal);
        Assert.Contains("1 onleesbaar", import.SummaryText, StringComparison.Ordinal);

        var dollars = import.Rows[3];
        Assert.False(dollars.CanInclude);
        Assert.Equal("Niet in euro: niet geïmporteerd", dollars.StatusText);

        var joint = import.Rows[4];
        Assert.Equal(TransactionType.Transfer, joint.SelectedType.Type);
        Assert.Equal("Gezamenlijke rekening", joint.SelectedTransfer?.Name);
        Assert.True(joint.HasSuggestion);

        import.Rows[0].Include = false; // the user leaves one line out
        Assert.Equal("3 regels importeren", import.ConfirmText);
        Save(window, "16-import-columns");
        import.ToggleMappingCommand.Execute(null);
        window.Height = 1250;
        Save(window, "17-import-review");

        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStep.Done, import.Step);
        Assert.Equal("3 regels zijn toegevoegd.", import.ResultText);
        Assert.Equal(4, temp.Scalar("SELECT COUNT(*) FROM transactions;")); // the transfer has two sides
        Assert.Equal(240_000 - 1_799 - 60_000, temp.Scalar("SELECT SUM(amount_cents) FROM transactions WHERE account_id = (SELECT id FROM accounts WHERE name = 'Zichtrekening');"));
        HeadlessSetup.PumpUntil(() => import.History.Count == 1);
        window.Height = 900;
        Save(window, "18-import-done");

        // The same file again: nothing new.
        import.StartOverCommand.Execute(null);
        await import.LoadFileAsync("kbc-style.csv", Fixture("kbc-style.csv"));
        Assert.False(import.IsMappingOpen); // the layout is now remembered
        Assert.True(import.MappingRemembered);
        Assert.Equal("1 regel importeren", import.ConfirmText); // only the line left out last time
        import.CancelCommand.Execute(null);

        // Undo the whole import from the history list.
        var batch = import.History[0];
        await batch.StartUndoCommand.ExecuteAsync(null);
        Assert.True(batch.IsConfirming);
        Assert.False(batch.NeedsChoice);
        await batch.UndoUnchangedCommand.ExecuteAsync(null);

        Assert.Equal("Import ongedaan gemaakt: 4 regels verwijderd.", import.StatusMessage);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM transactions WHERE status = 'Active';"));
        HeadlessSetup.PumpUntil(() => import.History.Count == 1 && import.History[0].IsUndone);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Wrong_choices_are_explained_with_the_line_number_and_nothing_is_saved()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);
        var import = OpenImport(vm, "Zichtrekening");
        await import.LoadFileAsync("kbc-style.csv", Fixture("kbc-style.csv"));

        import.Rows[4].SelectedTransfer = null; // a transfer without the other account

        await import.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(ImportStep.Review, import.Step);
        Assert.Equal("Regel 6: Kies de andere rekening van deze overschrijving.", import.Error);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM transactions;"));

        await import.LoadFileAsync("notes.pdf", [0x25, 0x50, 0x44, 0x46, 0x00, 0x01]);
        Assert.Equal("Dit bestandstype wordt niet ondersteund. Exporteer een CSV- of XLSX-bestand bij je bank.", import.Error);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Import_screenshots_in_dark_and_english()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);
        try
        {
            ThemeManager.Apply(AppTheme.Dark);
            var import = OpenImport(vm, "Zichtrekening");
            await import.LoadFileAsync("belfius-style.csv", Fixture("belfius-style.csv"));
            Assert.Equal(4, import.Rows.Count);
            import.IsMappingOpen = false;
            window.Height = 1100;
            Save(window, "dark-8-import-review");

            PersonalFinanceOS.App.Localization.Loc.Instance.SetLanguage(AppLanguage.English);
            HeadlessSetup.PumpUntil(() => import.SummaryText.Contains("new", StringComparison.Ordinal));
            Assert.Equal("Date not readable", import.Rows[3].StatusText);
            Assert.Equal(4, import.Rows.Count);
            Save(window, "dark-9-import-review-english");
        }
        finally
        {
            PersonalFinanceOS.App.Localization.Loc.Instance.SetLanguage(AppLanguage.Dutch);
            ThemeManager.Apply(AppTheme.System);
            window.Close();
        }
    }
}
