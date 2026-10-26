using System.Globalization;
using System.IO;
using System.Text;
using Monolith.Core;

namespace Monolith.Modules.Games.Sources;

/// <summary>One parsed appmanifest_&lt;id&gt;.acf.</summary>
public sealed record SteamAppManifest(int AppId, string Name, string InstallDir, int StateFlags, long SizeOnDisk)
{
    /// <summary>StateFlags bit 4 = fully installed (an update may still be pending).</summary>
    public bool IsInstalled => (StateFlags & 4) != 0;
}

/// <summary>
/// Steam library (docs/research/tech.md §6):
/// HKCU\Software\Valve\Steam SteamPath → steamapps\libraryfolders.vdf → each library's appmanifest_*.acf.
/// Launch: steam://rungameid/&lt;appid&gt;.
/// </summary>
public static class SteamSource
{
    /// <summary>Tools and runtimes that Steam installs like games but that are not games.</summary>
    private static readonly HashSet<int> ToolAppIds =
    [
        228980,  // Steamworks Common Redistributables (research: skip)
        250820,  // SteamVR
        1070560, // Steam Linux Runtime
        1391110, // Steam Linux Runtime — Soldier
        1628350, // Steam Linux Runtime — Sniper
    ];

    private static readonly string[] ToolNamePrefixes =
    [
        "Steamworks Common Redistributables", "Steamworks SDK Redist", "Steam Linux Runtime", "Proton ", "Proton-",
        "SteamVR", "Source SDK Base",
    ];

    public static bool IsToolOrRedistributable(int appId, string? name)
    {
        if (ToolAppIds.Contains(appId)) return true;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        if (n.Equals("Proton Experimental", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Equals("Proton", StringComparison.OrdinalIgnoreCase)) return true;
        if (ToolNamePrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
        if (n.EndsWith("Dedicated Server", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Library roots from libraryfolders.vdf (new "path" format and the pre-2021 flat format). Always includes the Steam folder.</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string? vdfText, string steamPath)
    {
        var result = new List<string>();
        void AddPath(string? p)
        {
            var n = GamePaths.NormalizeDir(p);
            if (n.Length > 0 && !result.Contains(n, StringComparer.OrdinalIgnoreCase)) result.Add(n);
        }

        AddPath(steamPath);
        var root = VdfParser.Parse(vdfText);
        var lib = root["libraryfolders"] ?? root["LibraryFolders"];
        if (lib is null) return result;

        foreach (var (key, node) in lib.Children)
        {
            if (!key.All(char.IsDigit)) continue; // skip TimeNextStatsReport, ContentStatsID…
            if (node.Value is { Length: > 0 } flat) AddPath(flat);  // old: "1" "D:\\SteamLibrary"
            else AddPath(node.Get("path"));                         // new: "0" { "path" "…" }
        }
        return result;
    }

    public static SteamAppManifest? ParseAppManifest(string? acfText)
    {
        var root = VdfParser.Parse(acfText);
        var app = root["AppState"];
        if (app is null) return null;
        if (!int.TryParse(app.Get("appid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
        var name = app.Get("name")?.Trim();
        var dir = app.Get("installdir")?.Trim();
        if (string.IsNullOrEmpty(dir)) return null;
        if (string.IsNullOrEmpty(name)) name = dir;
        _ = int.TryParse(app.Get("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags);
        _ = long.TryParse(app.Get("SizeOnDisk"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        if (app["StateFlags"] is null) flags = 4; // very old manifests: treat as installed when the folder exists
        return new SteamAppManifest(id, name, dir, flags, Math.Max(0, size));
    }

    public static string? FindSteamPath()
    {
        var p = InteractiveUser.ReadString(@"Software\Valve\Steam", "SteamPath");
        if (string.IsNullOrWhiteSpace(p))
            p = RegistryHelper.ReadString(RegHive.HKLM, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (string.IsNullOrWhiteSpace(p)) return null;
        var dir = GamePaths.NormalizeDir(p);
        return dir.Length > 0 && Directory.Exists(dir) ? dir : null;
    }

    public static SourceResult Scan()
    {
        var steam = FindSteamPath();
        if (steam is null) return SourceResult.NotInstalled(GamePlatform.Steam);

        var games = new List<GameEntry>();
        string? vdf = null;
        try
        {
            var vdfPath = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath)) vdf = File.ReadAllText(vdfPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam: не удалось прочитать libraryfolders.vdf — {ex.Message}");
        }

        foreach (var lib in ParseLibraryFolders(vdf, steam))
        {
            var apps = Path.Combine(lib, "steamapps");
            IEnumerable<string> manifests;
            try
            {
                if (!Directory.Exists(apps)) continue;
                manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam: библиотека {lib} недоступна — {ex.Message}");
                continue;
            }

            foreach (var file in manifests)
            {
                try
                {
                    var m = ParseAppManifest(File.ReadAllText(file, Encoding.UTF8));
                    if (m is null || !m.IsInstalled || IsToolOrRedistributable(m.AppId, m.Name)) continue;
                    var folder = Path.Combine(apps, "common", m.InstallDir);
                    if (!Directory.Exists(folder)) continue;
                    games.Add(new GameEntry
                    {
                        Id = $"steam:{m.AppId}",
                        Name = m.Name,
                        Platform = GamePlatform.Steam,
                        InstallDir = GamePaths.NormalizeDir(folder),
                        LaunchUri = $"steam://rungameid/{m.AppId}",
                        SizeBytes = m.SizeOnDisk,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Steam: пропущен {Path.GetFileName(file)} — {ex.Message}");
                }
            }
        }
        return new SourceResult(GamePlatform.Steam, true, games);
    }
}
