namespace PersonalFinanceOS.Infrastructure;

/// <summary>
/// Where user data lives: the current Windows user's local app-data folder
/// (%LOCALAPPDATA%\PersonalFinanceOS), always separate from the program files (spec §4).
/// Each Windows user automatically gets their own database. Installing or updating the app never
/// touches this folder.
/// </summary>
public sealed class AppDataPaths
{
    public const string FolderName = "PersonalFinanceOS";

    /// <summary>Developer/test override. Not documented for end users.</summary>
    public const string OverrideEnvironmentVariable = "PFOS_DATA_DIR";

    public AppDataPaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(rootDirectory));
        }

        Root = Path.GetFullPath(rootDirectory);
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "finance.db");
    public string BackupsDirectory => Path.Combine(Root, "backups");
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Written once when a profile is first created. If this marker exists but the database is gone,
    /// the app refuses to silently create an empty one (spec §4: a missing file is not a new profile).
    /// </summary>
    public string ProfileMarkerFile => Path.Combine(Root, "profile.json");

    public static AppDataPaths ForCurrentUser()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppDataPaths(overridden);
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return new AppDataPaths(Path.Combine(localAppData, FolderName));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
