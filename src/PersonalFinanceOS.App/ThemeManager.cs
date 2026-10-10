using Avalonia.Styling;
using PersonalFinanceOS.Application.Settings;

namespace PersonalFinanceOS.App;

/// <summary>Applies the chosen appearance to the whole app, live.</summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default, // follow Windows
            };
        }
    }
}
