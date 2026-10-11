using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.Application.Settings;

public enum AppLanguage
{
    Dutch,
    English,
}

/// <summary>Light/dark appearance. <see cref="System"/> follows the Windows setting.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Typed access to user settings. Keys are stable; never rename them after release.</summary>
public sealed class AppSettingsService(ISettingsStore store)
{
    public const string LanguageKey = "ui.language";
    public const string JointDefaultShareKey = "joint.defaultShareBasisPoints";
    public const string ThemeKey = "ui.theme";
    public const string MinimumBufferKey = "forecast.minimumBufferCents";
    public const string RoundingAccountKey = "import.roundingAccountId";
    public const string BackupReminderDaysKey = "backup.reminderDays";
    public const string LastBackupKey = "backup.lastAt";

    /// <summary>The reminder choices for manual backups, in days; 0 is off.</summary>
    public static readonly IReadOnlyList<int> BackupReminderChoices = [0, 7, 14, 30, 90];

    public const int DefaultBackupReminderDays = 30;

    public async Task<AppTheme> GetThemeAsync(CancellationToken ct = default) =>
        await store.GetAsync(ThemeKey, ct) switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };

    public Task SetThemeAsync(AppTheme theme, CancellationToken ct = default) =>
        store.SetAsync(ThemeKey, theme switch
        {
            AppTheme.Light => "light",
            AppTheme.Dark => "dark",
            _ => "system",
        }, ct);

    public async Task<AppLanguage> GetLanguageAsync(CancellationToken ct = default) =>
        await store.GetAsync(LanguageKey, ct) switch
        {
            "en" => AppLanguage.English,
            _ => AppLanguage.Dutch,
        };

    public Task SetLanguageAsync(AppLanguage language, CancellationToken ct = default) =>
        store.SetAsync(LanguageKey, language == AppLanguage.English ? "en" : "nl", ct);

    /// <summary>
    /// Suggested share for new joint accounts. 50% is only the initial suggestion (spec §9);
    /// the user can change it and every account keeps its own share.
    /// </summary>
    public async Task<Share> GetJointDefaultShareAsync(CancellationToken ct = default)
    {
        var raw = await store.GetAsync(JointDefaultShareKey, ct);
        return int.TryParse(raw, out var bp) && bp is > 0 and <= Share.FullBasisPoints
            ? new Share(bp)
            : new Share(5_000);
    }

    public Task SetJointDefaultShareAsync(Share share, CancellationToken ct = default) =>
        store.SetAsync(JointDefaultShareKey, share.BasisPoints.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

    /// <summary>Money that must stay on the current account; safe-to-spend never counts it (spec §13). Zero until set.</summary>
    public async Task<Money> GetMinimumBufferAsync(CancellationToken ct = default) =>
        long.TryParse(await store.GetAsync(MinimumBufferKey, ct), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cents) && cents >= 0
            ? Money.FromCents(cents)
            : Money.Zero;

    public Task SetMinimumBufferAsync(Money buffer, CancellationToken ct = default)
    {
        if (buffer.IsNegative)
        {
            throw new DomainValidationException("forecast.buffer.negative");
        }

        return store.SetAsync(MinimumBufferKey, buffer.Cents.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    /// <summary>The savings account the bank sends rounding transfers to (spec §11). Null when not chosen.</summary>
    public async Task<Guid?> GetRoundingAccountIdAsync(CancellationToken ct = default) =>
        Guid.TryParse(await store.GetAsync(RoundingAccountKey, ct), out var id) ? id : null;

    public Task SetRoundingAccountIdAsync(Guid? accountId, CancellationToken ct = default) =>
        store.SetAsync(RoundingAccountKey, accountId?.ToString("D") ?? string.Empty, ct);

    /// <summary>How many days after the last manual backup the dashboard reminds the user; 0 is off. 30 until changed.</summary>
    public async Task<int> GetBackupReminderDaysAsync(CancellationToken ct = default) =>
        int.TryParse(await store.GetAsync(BackupReminderDaysKey, ct), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var days)
            && BackupReminderChoices.Contains(days)
            ? days
            : DefaultBackupReminderDays;

    public Task SetBackupReminderDaysAsync(int days, CancellationToken ct = default)
    {
        if (!BackupReminderChoices.Contains(days))
        {
            throw new DomainValidationException("backup.reminder.invalid");
        }

        return store.SetAsync(BackupReminderDaysKey, days.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    /// <summary>When the last verified manual backup was made; null when never.</summary>
    public async Task<DateTimeOffset?> GetLastBackupAsync(CancellationToken ct = default) =>
        DateTimeOffset.TryParse(await store.GetAsync(LastBackupKey, ct), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    public Task SetLastBackupAsync(DateTimeOffset at, CancellationToken ct = default) =>
        store.SetAsync(LastBackupKey, at.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct);

    /// <summary>True when a reminder is due: it is on, and there is no backup yet or the last one is older than the interval.</summary>
    public async Task<bool> IsBackupDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var days = await GetBackupReminderDaysAsync(ct);
        return days > 0 && (await GetLastBackupAsync(ct) is not { } last || (now - last).TotalDays >= days);
    }
}
