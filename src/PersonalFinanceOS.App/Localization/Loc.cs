using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using PersonalFinanceOS.Application.Settings;

namespace PersonalFinanceOS.App.Localization;

/// <summary>
/// UI text lookup for Dutch and English. Switching language only changes what is displayed;
/// stored values are never affected (spec §20).
/// Strings live in Resources/Strings*.resx, generated from docs/strings.tsv.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources =
        new("PersonalFinanceOS.App.Resources.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    public static CultureInfo DutchCulture { get; } = CultureInfo.GetCultureInfo("nl-BE");

    // Irish English: English wording with euro formatting conventions (€1,234.56).
    public static CultureInfo EnglishCulture { get; } = CultureInfo.GetCultureInfo("en-IE");

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public AppLanguage Language { get; private set; } = AppLanguage.Dutch;

    public CultureInfo Culture => Language == AppLanguage.English ? EnglishCulture : DutchCulture;

    public string this[string key] => Resources.GetString(key, Culture) ?? $"[{key}]";

    public static string T(string key) => Instance[key];

    public static string Format(string key, params object?[] args) =>
        string.Format(Instance.Culture, Instance[key], args);

    /// <summary>Translates a stable error code such as "account.name.required".</summary>
    public static string Error(string code)
    {
        var key = "Error_" + code.Replace('.', '_');
        var text = Resources.GetString(key, Instance.Culture);
        return text ?? Instance["Error_unexpected"];
    }

    public void SetLanguage(AppLanguage language)
    {
        Language = language;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        // Indexer change notifications: different binding engines listen for different names.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Best guess before settings are readable (e.g. startup problem screen).</summary>
    public static AppLanguage FromSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? AppLanguage.English : AppLanguage.Dutch;
}

/// <summary>
/// XAML usage: <c>Text="{l:T Nav_Accounts}"</c>. Updates live when the language changes.
/// </summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBindingExtension($"[{Key}]")
        {
            Mode = BindingMode.OneWay,
            Source = Loc.Instance,
        }.ProvideValue(serviceProvider);
}
