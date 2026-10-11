using System.Text.RegularExpressions;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Assets;
using PersonalFinanceOS.Application.Assistant;
using PersonalFinanceOS.Application.Import;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.QuickAdd;
using PersonalFinanceOS.Application.Reports;
using PersonalFinanceOS.Application.Scenarios;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Assets;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Scenarios;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>
/// Release hardening (spec §24 phase 8): every text exists in both languages, the app makes no network calls, and every
/// input has a name for screen readers. These read the source tree, so a mistake fails the build instead of a user.
/// </summary>
public sealed partial class ReleaseChecksTests
{
    private static readonly string Root = FindRoot();

    private static readonly Dictionary<string, (string English, string Dutch)> Strings = File.ReadLines(Path.Combine(Root, "docs", "strings.tsv"))
        .Where(l => l.Trim().Length > 0)
        .Select(l => l.Split('\t'))
        .ToDictionary(p => p[0], p => (p[1], p[2]));

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Name(string file) => Path.GetRelativePath(Root, file);

    // ===== Localization =====

    [Fact]
    public void Every_text_has_english_and_dutch_with_the_same_placeholders()
    {
        Assert.True(Strings.Count > 1_000);
        foreach (var (key, (english, dutch)) in Strings)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), $"{key} has no English text.");
            Assert.False(string.IsNullOrWhiteSpace(dutch), $"{key} has no Dutch text.");
            Assert.True(
                Placeholders(english).SetEquals(Placeholders(dutch)),
                $"{key}: the English and Dutch texts use different placeholders.");
        }

        static HashSet<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).ToHashSet();
    }

    [Fact]
    public void Every_text_key_and_error_code_used_in_the_code_exists()
    {
        var missing = new List<string>();
        foreach (var file in SourceFiles("*.cs"))
        {
            var code = File.ReadAllText(file);
            if (file.Contains($"{Path.DirectorySeparatorChar}Reports{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                missing.AddRange(ReportKeyPattern().Matches(code).Select(m => m.Groups["key"].Value).Where(k => !Strings.ContainsKey(k)).Select(k => $"{Name(file)}: {k}"));
            }

            missing.AddRange(LiteralKeyPattern().Matches(code).Select(m => m.Groups["key"].Value).Where(k => !Strings.ContainsKey(k)).Select(k => $"{Name(file)}: {k}"));
            missing.AddRange(ErrorCodePattern().Matches(code)
                .Select(m => "Error_" + m.Groups["code"].Value.Replace('.', '_'))
                .Where(k => !Strings.ContainsKey(k))
                .Select(k => $"{Name(file)}: {k}"));
        }

        foreach (var file in SourceFiles("*.axaml"))
        {
            missing.AddRange(MarkupKeyPattern().Matches(File.ReadAllText(file)).Select(m => m.Groups["key"].Value).Where(k => !Strings.ContainsKey(k)).Select(k => $"{Name(file)}: {k}"));
        }

        Assert.True(missing.Count == 0, "Missing texts:\n" + string.Join('\n', missing));
    }

    /// <summary>Texts picked by enum value ("ReportType_" + type) exist for every value the code can produce.</summary>
    [Fact]
    public void Every_enum_value_shown_to_the_user_has_a_text()
    {
        var map = new Dictionary<string, (Type Enum, string[] Except)>
        {
            ["AccountType_"] = (typeof(AccountType), []),
            ["AssetKind_"] = (typeof(AssetKind), []),
            ["Ask_Action_"] = (typeof(AssistantAction), []),
            ["CategoryKind_"] = (typeof(CategoryKind), []),
            ["EntryKind_"] = (typeof(EntryKind), []),
            ["ForecastCaveat_"] = (typeof(ForecastCaveat), []),
            ["Frequency_"] = (typeof(RecurrenceFrequency), []),
            ["Import_Status_"] = (typeof(ImportRowStatus), ["Invalid", "New"]),
            ["Import_Suggestion_"] = (typeof(ImportSuggestion), ["None"]),
            ["Outcome_"] = (typeof(OutcomeState), []),
            ["Priority_"] = (typeof(WishlistPriority), []),
            ["QuickAddIssue_"] = (typeof(QuickAddIssue), []),
            ["RecurringKind_"] = (typeof(RecurringKind), []),
            ["Report_Event_"] = (typeof(CashEventKind), []),
            ["ReportType_"] = (typeof(ReportType), []),
            ["ReportTypeHelp_"] = (typeof(ReportType), []),
            ["Reports_Period_"] = (typeof(ReportPeriod), []),
            ["ScenarioKind_"] = (typeof(ScenarioChangeKind), []),
            ["ScenarioKindTitle_"] = (typeof(ScenarioChangeKind), []),
            ["ScenarioNote_"] = (typeof(ScenarioNote), ["None"]),
            ["Theme_"] = (typeof(AppTheme), []),
            ["TxType_"] = (typeof(TransactionType), []),
            ["ValueSource_"] = (typeof(ValueSource), []),
            ["Verdict_"] = (typeof(AffordabilityVerdict), []),
            ["WishlistStatus_"] = (typeof(WishlistStatus), []),
        };

        // Every prefix the code builds a key from is covered above.
        var prefixes = SourceFiles("*.cs").SelectMany(f => PrefixPattern().Matches(File.ReadAllText(f)).Select(m => m.Groups["prefix"].Value)).ToHashSet();
        prefixes.Remove("DefaultCategory_"); // a fixed list of keys, checked below
        prefixes.Remove("Error_");
        Assert.Empty(prefixes.Except(map.Keys));

        var missing = map.SelectMany(p => Enum.GetNames(p.Value.Enum).Except(p.Value.Except).Select(v => p.Key + v)).Where(k => !Strings.ContainsKey(k)).ToList();
        Assert.Empty(missing);
        Assert.Equal(14, Strings.Keys.Count(k => k.StartsWith("DefaultCategory_", StringComparison.Ordinal)));
    }

    // ===== Offline audit =====

    /// <summary>Spec §4: the app works fully offline and never sends data anywhere. No network API is used anywhere.</summary>
    [Fact]
    public void The_app_contains_no_network_code()
    {
        string[] forbidden =
        [
            "HttpClient", "HttpWebRequest", "WebRequest.", "WebClient", "System.Net.Http", "System.Net.Sockets", "TcpClient",
            "UdpClient", "Dns.", "WebSocket", "SmtpClient", "System.Net.Mail", "NetworkStream",
        ];
        var hits = SourceFiles("*.cs")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .SelectMany(l => forbidden.Where(f => l.Text.Contains(f, StringComparison.Ordinal)).Select(f => $"{Name(l.File)}:{l.Line} uses {f}"))
            .ToList();
        Assert.Empty(hits);

        // No package that phones home: only UI, database, MVVM and test packages.
        var packages = File.ReadAllText(Path.Combine(Root, "Directory.Packages.props"));
        Assert.DoesNotContain("ApplicationInsights", packages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sentry", packages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Telemetry", packages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Analytics", packages, StringComparison.OrdinalIgnoreCase);

        // Links in the app only open local files or folders.
        Assert.DoesNotContain(SourceFiles("*.axaml"), f => File.ReadAllText(f).Contains("http://", StringComparison.Ordinal) && UrlPattern().IsMatch(File.ReadAllText(f)));
    }

    // ===== Accessibility =====

    /// <summary>Every text box, list and date field has a name a screen reader reads out (spec §22).</summary>
    [Fact]
    public void Every_input_has_a_name_for_screen_readers()
    {
        var unnamed = new List<string>();
        foreach (var file in SourceFiles("*.axaml"))
        {
            foreach (Match m in InputPattern().Matches(File.ReadAllText(file)))
            {
                if (!m.Value.Contains("AutomationProperties.Name", StringComparison.Ordinal))
                {
                    unnamed.Add($"{Name(file)}: {m.Value[..Math.Min(80, m.Value.Length)]}");
                }
            }
        }

        Assert.Empty(unnamed);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "strings.tsv")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"Loc\.(?:T|Format)\(""(?<key>\w+)""(?!\s*\+)")]
    private static partial Regex LiteralKeyPattern();

    [GeneratedRegex(@"\b(?:T|Col|Num)\(""(?<key>\w+)""(?!\s*\+)")]
    private static partial Regex ReportKeyPattern();

    [GeneratedRegex(@"(?:DomainValidationException|Loc\.Error)\(""(?<code>[\w.]+)""\)")]
    private static partial Regex ErrorCodePattern();

    [GeneratedRegex(@"\{l:T (?<key>\w+)\}")]
    private static partial Regex MarkupKeyPattern();

    [GeneratedRegex(@"""(?<prefix>[A-Za-z]+(?:_[A-Za-z]+)*_)""\s*\+")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"<(?:TextBox|ComboBox|CalendarDatePicker)(?=[\s/>])[^>]*>", RegexOptions.Singleline)]
    private static partial Regex InputPattern();

    [GeneratedRegex(@"https?://(?!schemas\.microsoft\.com|github\.com/avaloniaui)")]
    private static partial Regex UrlPattern();
}
