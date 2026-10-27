using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Startup;

/// <summary>The places Task Manager reads autostart entries from (docs/research/general_tweaks.md §1).</summary>
public enum StartupSource
{
    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
    RunUser,
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run.</summary>
    RunMachine,
    /// <summary>HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run.</summary>
    Run32Machine,
    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup.</summary>
    FolderUser,
    /// <summary>%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp.</summary>
    FolderCommon,
    /// <summary>Startup task of a packaged (MSIX / Store) app: HKCU …\AppModel\SystemAppData\&lt;family&gt;\&lt;task&gt;, DWORD State.</summary>
    PackagedTask,
}

/// <summary>Where an entry lives and where Windows keeps its enabled/disabled mark.</summary>
public static class StartupLocations
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunKeyMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedUser = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string ApprovedMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static bool IsMachine(StartupSource s) => s is StartupSource.RunMachine or StartupSource.Run32Machine or StartupSource.FolderCommon;

    public static bool IsFolder(StartupSource s) => s is StartupSource.FolderUser or StartupSource.FolderCommon;

    public static bool IsPackaged(StartupSource s) => s == StartupSource.PackagedTask;

    public static RegHive ApprovedHive(StartupSource s) => IsMachine(s) ? RegHive.HKLM : RegHive.HKCU;

    /// <summary>The StartupApproved sub key: Run, Run32 or StartupFolder.</summary>
    public static string ApprovedKey(StartupSource s) => (IsMachine(s) ? ApprovedMachine : ApprovedUser) + s switch
    {
        StartupSource.Run32Machine => "Run32",
        StartupSource.FolderUser or StartupSource.FolderCommon => "StartupFolder",
        _ => "Run",
    };

    public static string FolderPath(StartupSource s) => s switch
    {
        StartupSource.FolderUser => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        StartupSource.FolderCommon => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        _ => "",
    };

    /// <summary>Full, copyable location of the entry itself.</summary>
    public static string Describe(StartupSource s) => s switch
    {
        StartupSource.RunUser => @"HKCU\" + RunKey,
        StartupSource.RunMachine => @"HKLM\" + RunKeyMachine,
        StartupSource.Run32Machine => @"HKLM\" + Run32Key,
        StartupSource.PackagedTask => @"HKCU\" + PackagedStartup.SystemAppDataKey,
        _ => FolderPath(s),
    };

    /// <summary>Full location of one entry's on/off mark: its StartupApproved value, or a packaged task's State.</summary>
    public static string DescribeMark(StartupEntry e) => IsPackaged(e.Source)
        ? $@"HKCU\{PackagedStartup.KeyPath(e.PackageFamilyName ?? "", e.Name)} → {PackagedStartup.StateValue}"
        : $@"{ApprovedHive(e.Source)}\{ApprovedKey(e.Source)} → «{e.Name}»";

    /// <summary>Short label for the table.</summary>
    public static string ShortLabel(StartupSource s) => s switch
    {
        StartupSource.RunUser or StartupSource.RunMachine => "Реестр · Run",
        StartupSource.Run32Machine => "Реестр · Run32",
        StartupSource.PackagedTask => "Приложение MSIX",
        _ => "Папка автозагрузки",
    };

    /// <summary>The key the journal uses for the StartupApproved value of this entry (same format as ChangeSession).</summary>
    public static string JournalTarget(StartupSource s, string name) => $"reg:{ApprovedHive(s)}\\{ApprovedKey(s)}\\{name}";

    /// <summary>The journal key of an entry's mark, for every source.</summary>
    public static string JournalTarget(StartupEntry e) => IsPackaged(e.Source)
        ? PackagedStartup.JournalTarget(e.PackageFamilyName ?? "", e.Name)
        : JournalTarget(e.Source, e.Name);
}

/// <summary>One autostart entry as found on disk / in the registry.</summary>
public sealed record StartupEntry
{
    public required StartupSource Source { get; init; }
    /// <summary>Registry value name, the file name (with extension) inside the Startup folder, or a packaged app's TaskId.</summary>
    public required string Name { get; init; }
    /// <summary>Package family name (packaged tasks only).</summary>
    public string? PackageFamilyName { get; init; }
    /// <summary>Live DWORD State of a packaged task (<see cref="PackagedTaskState"/>); null when unreadable or not a packaged task.</summary>
    public int? PackageState { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Raw command line (registry) or "target arguments" of the shortcut.</summary>
    public required string Command { get; init; }
    /// <summary>The file that identifies the program (exe, or the DLL/script a host runs). Null when unknown.</summary>
    public string? TargetPath { get; init; }
    public bool TargetExists { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    /// <summary>The shortcut / file in the Startup folder (folder sources only).</summary>
    public string? ItemPath { get; init; }
    public bool Enabled { get; init; }
    public DateTime? DisabledAtUtc { get; init; }
    public StartupRisk Risk { get; init; }

    /// <summary>Unique row key (a TaskId is unique only inside its package).</summary>
    public string Key => PackageFamilyName is { } family ? $"{Source}|{family}\\{Name}" : $"{Source}|{Name}";

    /// <summary>
    /// Whether MONOLITH may switch this entry at all: Run / Startup-folder marks always; packaged tasks only in a user
    /// state (0/1/2) and with names the undo journal accepts. Policy-managed tasks (3/4) are never written.
    /// </summary>
    public bool CanChange => !StartupLocations.IsPackaged(Source)
                             || (PackagedStartup.IsUserChangeable(PackageState) && PackagedStartup.IsJournalSafe(PackageFamilyName, Name));
}

/// <summary>Reads autostart entries exactly where Task Manager does. Read-only.</summary>
public static class StartupScanner
{
    public static List<StartupEntry> Scan()
    {
        var list = new List<StartupEntry>();
        ScanRegistry(list, StartupSource.RunUser, RegHive.HKCU, StartupLocations.RunKey);
        ScanRegistry(list, StartupSource.RunMachine, RegHive.HKLM, StartupLocations.RunKeyMachine);
        ScanRegistry(list, StartupSource.Run32Machine, RegHive.HKLM, StartupLocations.Run32Key);
        ScanFolder(list, StartupSource.FolderUser);
        ScanFolder(list, StartupSource.FolderCommon);
        list.AddRange(PackagedStartup.Scan());
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>The entry with its on/off state read again (StartupApproved mark, or a packaged task's State).</summary>
    public static StartupEntry Refresh(StartupEntry e)
    {
        if (StartupLocations.IsPackaged(e.Source))
        {
            var state = PackagedStartup.ReadState(e.PackageFamilyName ?? "", e.Name);
            return e with { PackageState = state, Enabled = PackagedStartup.IsEnabled(state), DisabledAtUtc = null };
        }
        var (enabled, at) = ReadState(e.Source, e.Name);
        return e with { Enabled = enabled, DisabledAtUtc = at };
    }

    /// <summary>Current enabled state of one Run / Startup-folder entry, straight from StartupApproved.</summary>
    public static (bool Enabled, DateTime? DisabledAtUtc) ReadState(StartupSource source, string name)
    {
        var raw = RegistryHelper.Read(StartupLocations.ApprovedHive(source), StartupLocations.ApprovedKey(source), name) as byte[];
        return (StartupApproved.IsEnabled(raw), StartupApproved.DisabledAtUtc(raw));
    }

    private static void ScanRegistry(List<StartupEntry> list, StartupSource source, RegHive hive, string path)
    {
        try
        {
            using var root = RegistryHelper.OpenBase(hive);
            using var key = root.OpenSubKey(path, false);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue; // (Default) is not an autostart entry Task Manager lists
                try
                {
                    var kind = key.GetValueKind(name);
                    if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command) continue;
                    if (string.IsNullOrWhiteSpace(command)) continue;

                    var parsed = CommandLineParser.Parse(command);
                    list.Add(Build(source, name, command.Trim(), parsed?.Target, null, name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    Log.Warn($"Автозапуск: не удалось прочитать {StartupLocations.Describe(source)}\\{name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Автозапуск: нет доступа к {StartupLocations.Describe(source)}: {ex.Message}");
        }
    }

    private static void ScanFolder(List<StartupEntry> list, StartupSource source)
    {
        var dir = StartupLocations.FolderPath(source);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Автозапуск: нет доступа к папке {dir}: {ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var title = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            try
            {
                switch (ext)
                {
                    case ".lnk":
                    {
                        var link = ShellLink.Read(file);
                        if (link?.TargetPath is { } target)
                        {
                            var command = CommandLineParser.Join(target, link.Arguments);
                            var parsed = CommandLineParser.Parse(command);
                            list.Add(Build(source, fileName, command, parsed?.Target ?? target, file, title));
                        }
                        else
                        {
                            // Shortcut to a shell item (no file path) or an unreadable shortcut: list it, nothing to resolve.
                            list.Add(Build(source, fileName, link is null ? file : $"{file} {link.Arguments}".Trim(), null, file, title));
                        }
                        break;
                    }
                    case ".url":
                    {
                        var url = ReadUrl(file) ?? file;
                        list.Add(Build(source, fileName, url, null, file, title));
                        break;
                    }
                    default:
                        list.Add(Build(source, fileName, file, file, file, title));
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Автозапуск: не удалось прочитать {file}: {ex.Message}");
            }
        }
    }

    private static StartupEntry Build(StartupSource source, string name, string command, string? target, string? itemPath, string fallbackTitle)
    {
        target = ResolveAppPath(target);
        var exists = target is null || FileProbe.FileExists(target);
        var (company, description) = exists ? ReadVersion(target) : (null, null);
        var raw = RegistryHelper.Read(StartupLocations.ApprovedHive(source), StartupLocations.ApprovedKey(source), name) as byte[];

        return new StartupEntry
        {
            Source = source,
            Name = name,
            DisplayName = !string.IsNullOrWhiteSpace(description) && description.Length <= 80 ? description : fallbackTitle,
            Command = command,
            TargetPath = target,
            TargetExists = exists,
            Publisher = company,
            Description = description,
            ItemPath = itemPath,
            Enabled = StartupApproved.IsEnabled(raw),
            DisabledAtUtc = StartupApproved.DisabledAtUtc(raw),
            Risk = StartupClassifier.ClassifyEntry(target, company),
        };
    }

    /// <summary>
    /// Bare program names that are not in System32 / Windows / PATH may still be registered under "App Paths"
    /// (the shell resolves them there), so a name like "chrome.exe" is not reported as missing.
    /// </summary>
    internal static string? ResolveAppPath(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathFullyQualified(target) || target.Contains('\\')) return target;
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var hive in new[] { RegHive.HKCU, RegHive.HKLM })
        {
            if (RegistryHelper.ReadString(hive, appPaths + target, "") is { } registered && registered.Trim().Length > 0)
                return Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
        }
        return target;
    }

    /// <summary>CompanyName and FileDescription of a PE file (empty for scripts / unreadable files).</summary>
    internal static (string? Company, string? Description) ReadVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return (null, null);
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            return (Clean(vi.CompanyName), Clean(vi.FileDescription));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static string? ReadUrl(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var t = line.Trim();
            if (t.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return t[4..].Trim();
        }
        return null;
    }
}
