using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Monolith.Core;

namespace Monolith.Modules.Cleaner;

/// <summary>Names (and optionally image paths) of the running processes, captured once.</summary>
public sealed class ProcessSnapshot
{
    private readonly Dictionary<string, List<int>> _byName;
    private readonly Dictionary<int, string?> _paths = [];

    private ProcessSnapshot(Dictionary<string, List<int>> byName) => _byName = byName;

    public static ProcessSnapshot Empty { get; } = new(new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase));

    public static ProcessSnapshot Capture()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return Empty; }
        foreach (var p in all)
        {
            try
            {
                if (!map.TryGetValue(p.ProcessName, out var list)) map[p.ProcessName] = list = [];
                list.Add(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return new ProcessSnapshot(map);
    }

    /// <summary>Process ids for a name without ".exe" (case-insensitive).</summary>
    public IReadOnlyList<int> Find(string name) =>
        _byName.TryGetValue(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name, out var l) ? l : [];

    public bool Has(string name) => Find(name).Count > 0;

    public string? PathOf(int pid)
    {
        lock (_paths)
        {
            if (_paths.TryGetValue(pid, out var p)) return p;
        }
        var path = NativeCleaner.GetProcessImagePath(pid);
        lock (_paths) _paths[pid] = path;
        return path;
    }
}

/// <summary>An application whose cache may be cleaned only while it is closed.</summary>
public sealed class AppOwner
{
    public required string Name { get; init; }
    /// <summary>Process names without ".exe".</summary>
    public IReadOnlyList<string> Processes { get; init; } = [];
    /// <summary>When set, only processes whose image path contains this text count (generic names like "browser").</summary>
    public string? PathContains { get; init; }
    /// <summary>Processes whose image path contains this text do not count (Opera vs Opera GX).</summary>
    public string? PathExcludes { get; init; }
    /// <summary>Lock files held exclusively while the app runs (Chromium "lockfile", Firefox "parent.lock").</summary>
    public IReadOnlyList<string> LockFiles { get; init; } = [];

    public bool IsRunning(ProcessSnapshot snapshot)
    {
        foreach (var lf in LockFiles)
            if (IsLockHeld(lf))
                return true;

        foreach (var name in Processes)
        {
            foreach (var pid in snapshot.Find(name))
            {
                if (PathContains is null && PathExcludes is null) return true;
                var path = snapshot.PathOf(pid);
                // Unknown path for a generic name: assume it is ours (skipping is the safe side).
                if (path is null) return true;
                if (PathContains is not null && !path.Contains(PathContains, StringComparison.OrdinalIgnoreCase)) continue;
                if (PathExcludes is not null && path.Contains(PathExcludes, StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>Opens the lock file without sharing: a sharing violation means the owner holds it.</summary>
    public static bool IsLockHeld(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Signs that Windows is servicing itself right now (research §2.6): then system folders are left alone.</summary>
public sealed record ServicingState(bool RebootRequired, bool CbsRebootPending, bool PendingRenames, bool MsiRunning, bool TrustedInstallerBusy)
{
    public bool Any => RebootRequired || CbsRebootPending || PendingRenames || MsiRunning || TrustedInstallerBusy;

    public static ServicingState None { get; } = new(false, false, false, false, false);

    public static ServicingState Capture(ProcessSnapshot processes)
    {
        var rebootRequired = RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        var cbs = RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        var renames = RegistryHelper.Read(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations")
            is string[] ops && ops.Any(s => !string.IsNullOrWhiteSpace(s));
        var msi = NativeCleaner.MutexExists(@"Global\_MSIExecute");
        var ti = processes.Has("TiWorker") || ServiceControl.GetStatus("TrustedInstaller") is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        return new ServicingState(rebootRequired, cbs, renames, msi, ti);
    }

    /// <summary>Russian explanation of what is going on, for the UI.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (RebootRequired || CbsRebootPending) parts.Add(Loc.T("Windows ждёт перезагрузки после установки обновлений"));
        else if (PendingRenames) parts.Add(Loc.T("Windows ждёт перезагрузки, чтобы заменить файлы"));
        if (MsiRunning) parts.Add(Loc.T("идёт установка программы (Windows Installer)"));
        if (TrustedInstallerBusy) parts.Add(Loc.T("работает установщик модулей Windows (TrustedInstaller)"));
        return parts.Count == 0 ? "" : string.Join("; ", parts);
    }
}

/// <summary>
/// Everything the catalog needs to resolve its folders on this PC, captured once per analysis / cleaning
/// on a background thread.
/// </summary>
public sealed class CleanEnvironment
{
    public required string LocalAppData { get; init; }
    public required string RoamingAppData { get; init; }
    public required string LocalLow { get; init; }
    public required string Temp { get; init; }
    public required string Windows { get; init; }
    public required string ProgramData { get; init; }
    public string? SteamPath { get; init; }
    public required string MemoryDumpFile { get; init; }
    public required string MinidumpDir { get; init; }
    public bool IsElevated { get; init; }
    /// <summary>Set when MONOLITH runs elevated under another account than the signed-in user ("DOMAIN\user").</summary>
    public string? ForeignSessionUser { get; init; }
    public required ServicingState Servicing { get; init; }
    public bool WindowsUpdateRunning { get; init; }
    public required ProcessSnapshot Processes { get; init; }
    /// <summary>Top-level entries of the user TEMP folder referenced by RunOnce / pending renames (installers waiting to resume).</summary>
    public IReadOnlySet<string> TempReferences { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> WindowsTempReferences { get; init; } = new HashSet<string>();
    public string? DotnetPath { get; init; }
    public string? NpmPath { get; init; }
    public string? YarnPath { get; init; }
    /// <summary>pip launcher: (file, leading arguments) — "pip.exe" or "python.exe -m pip".</summary>
    public (string File, string Prefix)? Pip { get; init; }

    public bool UserScopeBlocked => ForeignSessionUser is not null;

    public static CleanEnvironment Capture()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var windows = F(Environment.SpecialFolder.Windows);
        var temp = PathGuard.Normalize(Path.GetTempPath()) ?? Path.Combine(local, "Temp");
        temp = NativeCleaner.GetLongPath(temp) is { } longTemp ? PathGuard.Normalize(longTemp) ?? temp : temp;

        const string crash = @"SYSTEM\CurrentControlSet\Control\CrashControl";
        var dumpFile = Environment.ExpandEnvironmentVariables(RegistryHelper.ReadString(RegHive.HKLM, crash, "DumpFile") ?? @"%SystemRoot%\MEMORY.DMP");
        var miniDir = Environment.ExpandEnvironmentVariables(RegistryHelper.ReadString(RegHive.HKLM, crash, "MinidumpDir") ?? @"%SystemRoot%\Minidump");

        var processes = ProcessSnapshot.Capture();
        var winTemp = Path.Combine(windows, "Temp");
        var references = TempReferenceScanner.ReadReferenceStrings();

        return new CleanEnvironment
        {
            LocalAppData = local,
            RoamingAppData = F(Environment.SpecialFolder.ApplicationData),
            LocalLow = NativeCleaner.GetKnownFolder(NativeCleaner.FolderLocalAppDataLow)
                       ?? Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            Temp = temp,
            Windows = windows,
            ProgramData = F(Environment.SpecialFolder.CommonApplicationData),
            SteamPath = FindSteam(),
            MemoryDumpFile = dumpFile,
            MinidumpDir = miniDir,
            IsElevated = Elevation.IsElevated,
            ForeignSessionUser = DetectForeignUser(),
            Servicing = ServicingState.Capture(processes),
            WindowsUpdateRunning = ServiceControl.GetStatus("wuauserv") is ServiceControllerStatus.Running
                or ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending,
            Processes = processes,
            TempReferences = TempReferenceScanner.TopLevelEntries(temp, NativeCleaner.GetShortPath(temp), references),
            WindowsTempReferences = TempReferenceScanner.TopLevelEntries(winTemp, NativeCleaner.GetShortPath(winTemp), references),
            DotnetPath = ToolLocator.FindDotnet(),
            NpmPath = ToolLocator.FindOnPath("npm.cmd"),
            YarnPath = ToolLocator.FindOnPath("yarn.cmd"),
            Pip = ToolLocator.FindPip(),
        };
    }

    private static string? FindSteam()
    {
        var reg = RegistryHelper.ReadString(RegHive.HKCU, @"Software\Valve\Steam", "SteamPath");
        if (PathGuard.Normalize(reg) is { } p && Directory.Exists(p)) return p;
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var fallback = string.IsNullOrEmpty(x86) ? null : Path.Combine(x86, "Steam");
        return fallback is not null && File.Exists(Path.Combine(fallback, "steam.exe")) ? fallback : null;
    }

    /// <summary>
    /// When a standard user elevates with another account's credentials, %LOCALAPPDATA% etc. point to that other
    /// profile. Per-user caches must then not be cleaned from this process.
    /// </summary>
    private static string? DetectForeignUser()
    {
        if (!Elevation.IsElevated) return null;
        var (domain, user) = NativeCleaner.GetSessionUser();
        if (string.IsNullOrWhiteSpace(user)) return null;
        if (string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase)) return null;
        return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
    }
}

/// <summary>Finds files referenced by pending installer work so the temp cleaner leaves them alone.</summary>
public static class TempReferenceScanner
{
    /// <summary>RunOnce commands (HKCU + HKLM) and PendingFileRenameOperations entries.</summary>
    public static List<string> ReadReferenceStrings()
    {
        var list = new List<string>();
        foreach (var (hive, path) in new[]
                 {
                     (RegHive.HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                     (RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                     (RegHive.HKLM, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
                 })
        {
            try
            {
                using var b = RegistryHelper.OpenBase(hive);
                using var k = b.OpenSubKey(path, false);
                if (k is null) continue;
                foreach (var name in k.GetValueNames())
                    if (k.GetValue(name) is string s)
                        list.Add(s);
            }
            catch { }
        }
        if (RegistryHelper.Read(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations") is string[] ops)
            list.AddRange(ops.Where(s => !string.IsNullOrWhiteSpace(s)));
        return list;
    }

    /// <summary>
    /// Names of the entries directly under <paramref name="baseDir"/> that the strings refer to.
    /// "…\Temp\{GUID}\setup.exe /resume" → "{GUID}". Unquoted arguments add both the full token and the part before
    /// the first space, so a reference is never missed (excluding a little too much is harmless).
    /// </summary>
    public static HashSet<string> TopLevelEntries(string baseDir, string? shortBaseDir, IEnumerable<string> references)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bases = new[] { baseDir, shortBaseDir }
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b!.TrimEnd('\\') + "\\")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var raw in references)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string text;
            try { text = Environment.ExpandEnvironmentVariables(raw); }
            catch { text = raw; }
            text = text.Replace('/', '\\').Replace(@"\??\", "", StringComparison.Ordinal).Replace(@"\\?\", "", StringComparison.Ordinal);

            foreach (var b in bases)
            {
                var from = 0;
                while (from < text.Length)
                {
                    var idx = text.IndexOf(b, from, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;
                    var rest = text[(idx + b.Length)..];
                    var end = rest.IndexOfAny(['\\', '"', '\0', '|', ',', ';']);
                    var token = (end < 0 ? rest : rest[..end]).Trim();
                    if (token.Length > 0)
                    {
                        result.Add(token);
                        var space = token.IndexOf(' ');
                        if (space > 0) result.Add(token[..space]);
                    }
                    from = idx + b.Length;
                }
            }
        }
        return result;
    }
}

/// <summary>Locates developer tools for the optional developer-cache items (never from the working directory).</summary>
public static class ToolLocator
{
    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The Store "python.exe" alias in WindowsApps opens the Microsoft Store instead of running.
            if (dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate;
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(dir.Trim('"'));
                if (!Path.IsPathFullyQualified(expanded)) continue;
                candidate = Path.Combine(expanded, fileName);
            }
            catch
            {
                continue;
            }
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? FindDotnet()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "dotnet", "dotnet.exe");
            if (File.Exists(p)) return p;
        }
        return FindOnPath("dotnet.exe");
    }

    public static (string File, string Prefix)? FindPip()
    {
        if (FindOnPath("pip.exe") is { } pip) return (pip, "");
        if (FindOnPath("python.exe") is { } python) return (python, "-m pip ");
        return null;
    }
}
