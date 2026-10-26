using System.IO;

namespace Monolith.Core;

/// <summary>
/// Where MONOLITH keeps its files.
/// Machine-wide state (undo journal, registry backups) lives in %ProgramData%\MONOLITH,
/// per-user state (settings, logs) in %LocalAppData%\MONOLITH.
/// </summary>
public static class AppPaths
{
    public const string AppName = "MONOLITH";

    public static string MachineDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

    public static string UserDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string JournalFile => Path.Combine(MachineDir, "journal.json");
    public static string BackupsDir => Path.Combine(MachineDir, "Backups");
    public static string DriverBackupsDir => Path.Combine(MachineDir, "DriverBackups");
    public static string SettingsFile => Path.Combine(UserDir, "settings.json");
    public static string LogsDir => Path.Combine(UserDir, "Logs");
    public static string CacheDir => Path.Combine(UserDir, "Cache");

    public static string ExecutablePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, AppName + ".exe");

    /// <summary>Creates a directory if possible. Returns false instead of throwing (e.g. no admin rights).</summary>
    public static bool TryEnsureDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
