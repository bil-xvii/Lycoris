using System.Diagnostics;
using System.IO;
using Monolith.Core;

namespace Monolith.Modules.Games;

/// <summary>A running user application (all processes with the same name grouped).</summary>
public sealed record RunningApp(string Name, string Description, int Count, long MemoryBytes, bool HasWindow);

/// <summary>
/// Which running processes Game Boost may close. Strict filter — a process is closable only when it:
/// runs in this interactive session (never session 0 / services), belongs to the logged-in user,
/// is not critical, is not Lycoris, and is not on the protected list (Windows shell and security,
/// drivers' helpers, anti-cheat, game launchers that the game itself needs).
/// </summary>
public static class UserProcesses
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Kernel, session and shell
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "fontdrvhost", "dwm", "explorer", "sihost", "taskhostw", "taskmgr", "ctfmon", "runtimebroker", "searchhost", "searchapp",
        "searchui", "searchindexer", "startmenuexperiencehost", "shellexperiencehost", "shellhost", "textinputhost",
        "applicationframehost", "lockapp", "logonui", "userinit", "conhost", "openconsole", "dllhost", "audiodg", "spoolsv",
        "wmiprvse", "wudfhost", "dashost", "memcompression", "memory compression", "sgrmbroker", "sppsvc", "backgroundtaskhost",
        "useroobebroker", "comppkgsrv", "mousocoreworker", "tiworker", "trustedinstaller", "unsecapp", "systemsettingsbroker",
        "smartscreen", "securityhealthsystray", "securityhealthservice", "securityhealthhost", "msmpeng", "mpdefendercoreservice",
        "nissrv", "widgetservice", "crossdeviceservice", "aggregatorhost", "wlanext", "dwmcore", "consent", "credentialuibroker",
        // Game Bar / Game Mode detection (the X3D driver relies on it)
        "gamebar", "gamebarftserver", "gamebarpresencewriter", "gameinputsvc", "xboxpcappft",
        // GPU / audio driver helpers
        "nvcontainer", "nvdisplay.container", "nvsphelper64", "rtkaudueservice64", "rtkauduservice64", "atieclxx", "atiesrxx",
        "amdfendrsr", "igfxem", "igfxhk", "igfxtray", "radeonsoftware",
        // Anti-cheat
        "easyanticheat", "easyanticheat_eos", "beservice", "beservice_x64", "vgc", "vgtray", "faceitclient", "faceitservice",
        // Launchers the game needs while it runs
        "steam", "steamservice", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "galaxyclient", "galaxyclient helper",
        "galaxycommunication", "eadesktop", "eabackgroundservice", "ealocalhostsvc", "upc", "uplaywebcore", "battle.net", "agent",
        "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler",
        // Tools that host other apps' UI
        "msedgewebview2",
    };

    private static readonly string[] ProtectedPrefixes =
    [
        "easyanticheat", "eosoverlayrenderer", "epiconlineservices", "battleye", "beservice", "faceit", "vgc",
        "nvcontainer", "nvdisplay", "searchprotocolhost", "searchfilterhost", "msedgewebview2", "steamwebhelper",
        "riotclient", "gamebar", "monolith", "lycoris",
    ];

    /// <summary>
    /// Close-list apps that are never put into efficiency mode (EcoQoS), even when the user listed them: voice and
    /// conferencing (their audio would stutter), capture and streaming (dropped frames), audio routing and effects, GPU
    /// vendor capture/broadcast helpers, overlays and frame limiters, peripheral and macro software (input latency).
    /// Closing them stays the user's choice; throttling them silently would be felt in the game.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverThrottle = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg", "discord", "teamspeak3", "ts3client_win64", "ts3client_win32", "teamspeak", "slack", "zoom", "teams", "ms-teams",
        "obs64", "obs32", "streamlabs obs", "streamlabs", "nvcontainer", "nvidia share", "nvidia broadcast", "nvidia broadcast ui",
        "wavelink", "sonar", "steelseriesgg", "steelseriesengine", "rtss", "rtsshooks", "msiafterburner", "encoderserver",
        "xsplit.core", "elgato 4k capture utility", "mumble", "skype", "amdrsserv", "amdrssrcext", "amdow",
    };

    private static readonly string[] NeverThrottlePrefixes =
    [
        "voicemeeter", "nahimic", "lghub", "razer", "icue", "corsair", "logioptions", "nvidia", "discord", "teamspeak", "obs64", "obs32",
        "obs-", "streamlabs", "steelseries", "wavelink", "rtss", "msiafterburner",
    ];

    /// <summary>True for the voice/stream/audio/overlay/peripheral software of <see cref="NeverThrottle"/> (name normalised like the close list).</summary>
    public static bool IsNeverThrottled(string? name)
    {
        var n = NormalizeName(name);
        if (n.Length == 0) return true;
        if (NeverThrottle.Contains(n)) return true;
        return NeverThrottlePrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"C:\x\Discord.exe" / "Discord.EXE " → "Discord".</summary>
    public static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var n = s.Trim().Trim('"');
        var slash = Math.Max(n.LastIndexOf('\\'), n.LastIndexOf('/'));
        if (slash >= 0) n = n[(slash + 1)..];
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n.Trim();
    }

    /// <summary>System, security, driver, anti-cheat and launcher processes — never closed, never offered.</summary>
    public static bool IsProtectedName(string? name)
    {
        var n = NormalizeName(name);
        if (n.Length == 0) return true;
        if (ProtectedNames.Contains(n)) return true;
        return ProtectedPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Running closable apps grouped by name, biggest memory first. Blocking — call from a background thread.</summary>
    public static IReadOnlyList<RunningApp> ListClosable()
    {
        var groups = new Dictionary<string, (string Desc, int Count, long Mem, bool Window, string? Title)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, path) in EnumerateClosable(excludeDir: null))
        {
            using (p)
            {
                try
                {
                    var name = p.ProcessName;
                    var hasWindow = p.MainWindowHandle != IntPtr.Zero;
                    var title = hasWindow ? p.MainWindowTitle : null;
                    groups.TryGetValue(name, out var g);
                    if (g.Count == 0) g.Desc = Describe(path);
                    g.Count++;
                    g.Mem += SafeWorkingSet(p);
                    g.Window |= hasWindow;
                    if (string.IsNullOrWhiteSpace(g.Title) && !string.IsNullOrWhiteSpace(title)) g.Title = title;
                    groups[name] = g;
                }
                catch { /* process exited meanwhile */ }
            }
        }
        return groups
            .Select(kv => new RunningApp(kv.Key,
                !string.IsNullOrWhiteSpace(kv.Value.Desc) ? kv.Value.Desc : kv.Value.Title ?? "",
                kv.Value.Count, kv.Value.Mem, kv.Value.Window))
            .OrderByDescending(a => a.HasWindow).ThenByDescending(a => a.MemoryBytes)
            .ToList();
    }

    /// <summary>
    /// Processes matching the close list that are safe to close. Never returns processes inside the game folder.
    /// The caller owns (disposes) the returned Process objects.
    /// </summary>
    public static IReadOnlyList<Process> FindClosable(IEnumerable<string> names, string? excludeDir)
    {
        var wanted = new HashSet<string>(names.Select(NormalizeName).Where(n => n.Length > 0 && !IsProtectedName(n)), StringComparer.OrdinalIgnoreCase);
        var result = new List<Process>();
        if (wanted.Count == 0) return result;
        foreach (var (p, _) in EnumerateClosable(excludeDir, wanted))
            result.Add(p);
        return result;
    }

    private static IEnumerable<(Process Process, string? Path)> EnumerateClosable(string? excludeDir, HashSet<string>? onlyNames = null)
    {
        int session, self;
        using (var current = Process.GetCurrentProcess())
        {
            session = current.SessionId;
            self = current.Id;
        }
        if (session == 0) yield break; // never act from / on the services session
        // The logged-in user: our own token plus the owner of explorer.exe (UAC may run us as another admin).
        var users = new HashSet<string>(InteractiveUser.AllSids, StringComparer.OrdinalIgnoreCase);
        if (users.Count == 0) yield break;
        var gameDir = GamePaths.NormalizeDir(excludeDir);

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex)
        {
            Log.Warn("Не удалось получить список процессов: {0}", ex.Message);
            yield break;
        }

        foreach (var p in all)
        {
            string? path = null;
            var keep = false;
            try
            {
                if (p.Id == self || p.Id <= 4 || p.SessionId != session) continue;
                var name = p.ProcessName;
                if (onlyNames is not null && !onlyNames.Contains(name)) continue;
                if (IsProtectedName(name)) continue;
                var owner = GamesNative.GetOwnerSid(p.Id);
                if (owner is null || !users.Contains(owner)) continue; // other users / unreadable → never touch
                if (GamesNative.IsCritical(p.Id)) continue;
                path = GamesNative.GetImagePath(p.Id);
                if (path is not null && IsUnderWindowsDir(path)) continue;
                if (gameDir.Length > 0 && GamePaths.IsUnder(path, gameDir)) continue;
                keep = true;
            }
            catch
            {
                // exited or access denied → skip
            }
            finally
            {
                if (!keep) p.Dispose();
            }
            if (keep) yield return (p, path);
        }
    }

    private static bool IsUnderWindowsDir(string path)
    {
        var win = GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return win.Length > 0 && GamePaths.IsUnder(path, win);
    }

    private static string Describe(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            var d = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription : vi.ProductName;
            return d?.Trim() ?? "";
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }
}
