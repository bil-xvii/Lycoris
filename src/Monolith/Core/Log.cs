using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Globalization;
using System.Windows;

namespace Monolith.Core;

public enum LogLevel { Info, Success, Warning, Error, Command }

/// <summary>
/// <see cref="Message"/> is the Russian text (what the log FILE gets: files stay Russian, language-neutral for support);
/// <see cref="Display"/> is the same line in the interface language (what the in-app console shows).
/// </summary>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    private readonly string? _display;
    public string Display { get => _display ?? Message; init => _display = value; }

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

    // The message is the RUSSIAN text: the file keeps it, the console shows Loc.Sink(message) (English if the table knows it).
    // With arguments the Russian text is a format ("Запущено: {0}") and the console shows Loc.Tf of it. See docs/localization.md.
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Success(string message) => Write(LogLevel.Success, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}: {ex.Message}", ex, $"{Loc.Sink(message)}: {ex.Message}");
    public static void Command(string message) => Write(LogLevel.Command, message);

    public static void Info(string ruFormat, params object?[] args) => WriteFormat(LogLevel.Info, ruFormat, args);
    public static void Success(string ruFormat, params object?[] args) => WriteFormat(LogLevel.Success, ruFormat, args);
    public static void Warn(string ruFormat, params object?[] args) => WriteFormat(LogLevel.Warning, ruFormat, args);
    public static void Error(string ruFormat, params object?[] args) => WriteFormat(LogLevel.Error, ruFormat, args);

    /// <summary>An error with a Russian format and an exception: the file gets the Russian text + the exception message.</summary>
    public static void Error(string ruFormat, Exception ex, params object?[] args)
    {
        string file, display;
        try
        {
            file = string.Format(Loc.IsEnglish ? Loc.RuCulture : CultureInfo.CurrentCulture, ruFormat, args);
            display = Loc.Tf(ruFormat, args);
        }
        catch (FormatException)
        {
            file = display = ruFormat;
        }
        Write(LogLevel.Error, $"{file}: {ex.Message}", ex, $"{display}: {ex.Message}");
    }

    private static void WriteFormat(LogLevel level, string ruFormat, object?[] args)
    {
        string file, display;
        try
        {
            file = string.Format(Loc.IsEnglish ? Loc.RuCulture : CultureInfo.CurrentCulture, ruFormat, args);
            display = Loc.Tf(ruFormat, args);
        }
        catch (FormatException)
        {
            file = display = ruFormat;
        }
        Write(level, file, null, display);
    }

    public static void Write(LogLevel level, string message, Exception? ex = null, string? display = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message) { Display = display ?? Loc.Sink(message) };
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
