using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;

namespace Monolith.Core;

public enum LogLevel { Info, Success, Warning, Error, Command }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelTag => Level switch
    {
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERR",
        LogLevel.Command => "EXEC",
        _ => "INFO",
    };
}

/// <summary>
/// Application log: written to a daily file and mirrored into <see cref="Entries"/> for the in-app console.
/// Safe to call from any thread.
/// </summary>
public static class Log
{
    private const int MaxUiEntries = 600;
    private static readonly object FileLock = new();

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    public static event Action<LogEntry>? Written;

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Success(string message) => Write(LogLevel.Success, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex.Message}", ex);
    public static void Command(string message) => Write(LogLevel.Command, message);

    public static void Write(LogLevel level, string message, Exception? ex = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        WriteToFile(entry, ex);

        var app = Application.Current;
        if (app?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted)
        {
            if (dispatcher.CheckAccess())
                AddToUi(entry);
            else
                dispatcher.BeginInvoke(() => AddToUi(entry));
        }

        try { Written?.Invoke(entry); } catch { /* listeners must not break logging */ }
    }

    private static void AddToUi(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxUiEntries)
            Entries.RemoveAt(0);
    }

    private static void WriteToFile(LogEntry entry, Exception? ex)
    {
        try
        {
            lock (FileLock)
            {
                if (!AppPaths.TryEnsureDir(AppPaths.LogsDir)) return;
                var file = Path.Combine(AppPaths.LogsDir, $"monolith-{DateTime.Now:yyyyMMdd}.log");
                var sb = new StringBuilder();
                sb.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(entry.LevelTag).Append("] ").Append(entry.Message);
                if (ex is not null) sb.AppendLine().Append(ex);
                sb.AppendLine();
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    /// <summary>Deletes log files older than <paramref name="days"/> days.</summary>
    public static void PruneOldLogs(int days = 14)
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return;
            foreach (var f in Directory.EnumerateFiles(AppPaths.LogsDir, "monolith-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-days))
                    File.Delete(f);
            }
        }
        catch { }
    }
}
