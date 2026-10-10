using System.Globalization;

namespace PersonalFinanceOS.Infrastructure.Logging;

/// <summary>
/// Minimal local log for technical events (startup, migrations, backups).
/// Rule (spec §4, §21): never pass balances, payees, amounts or transaction text to this log.
/// Nothing is ever sent anywhere; the file stays in the user's data folder.
/// </summary>
public sealed class SafeLog(string logsDirectory)
{
    private static readonly Lock Gate = new();

    public string FilePath => Path.Combine(logsDirectory, "app.log");

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})");

    private void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(logsDirectory);
                RotateIfLarge();
                var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {level} {message}{Environment.NewLine}");
                File.AppendAllText(FilePath, line);
            }
        }
        catch (IOException)
        {
            // Logging must never break the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RotateIfLarge()
    {
        var info = new FileInfo(FilePath);
        if (info.Exists && info.Length > 1_000_000)
        {
            File.Move(FilePath, Path.Combine(logsDirectory, "app.1.log"), overwrite: true);
        }
    }
}
