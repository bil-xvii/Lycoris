using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monolith.Core;
using Monolith.Modules.Games.Sources;

namespace Monolith.Modules.Games;

/// <summary>Detection status of one launcher, shown in the empty state and the library footer.</summary>
public sealed record SourceStatus(GamePlatform Platform, bool Installed, int Count, string? Error)
{
    [JsonIgnore] public string Name => GameText.SourceName(Platform);
    [JsonIgnore] public bool HasGames => Count > 0;
    [JsonIgnore] public string StatusText => Error is not null ? "ОШИБКА ЧТЕНИЯ"
        : !Installed ? "НЕ НАЙДЕН"
        : $"{Count} {GameText.Plural(Count, "ИГРА", "ИГРЫ", "ИГР")}";
}

public sealed record LibraryScan(IReadOnlyList<GameEntry> Games, IReadOnlyList<SourceStatus> Sources, DateTime ScannedAt);

/// <summary>
/// Aggregates every library source, removes duplicates (the same folder seen by two launchers)
/// and caches the result in %LocalAppData%\MONOLITH\Cache\games.json so the page opens instantly.
/// Everything here is read-only and runs off the UI thread.
/// </summary>
public static class GameLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class CacheFile
    {
        public int Version { get; set; } = 1;
        public DateTime ScannedAt { get; set; }
        public List<GameEntry> Games { get; set; } = [];
        public List<SourceStatus> Sources { get; set; } = [];
    }

    public static string CacheFilePath => Path.Combine(AppPaths.CacheDir, "games.json");

    public static LibraryScan Scan(IReadOnlyList<string> customExes)
    {
        var results = new List<SourceResult>
        {
            Safe(GamePlatform.Steam, SteamSource.Scan),
            Safe(GamePlatform.Epic, EpicSource.Scan),
            Safe(GamePlatform.Gog, GogSource.Scan),
        };

        IReadOnlyList<UninstallEntry> entries = [];
        try { entries = UninstallEntries.ReadAll(); }
        catch (Exception ex) { Log.Warn($"Библиотека игр: список программ недоступен — {ex.Message}"); }

        results.Add(Safe(GamePlatform.Ubisoft, () => LauncherSources.ScanUbisoft(entries)));
        try { results.AddRange(LauncherSources.ScanUninstallLaunchers(entries)); }
        catch (Exception ex) { Log.Warn($"Библиотека игр: EA/Battle.net/Riot — {ex.Message}"); }
        results.Add(Safe(GamePlatform.Custom, () => ScanCustom(customExes)));

        var games = Deduplicate(results.SelectMany(r => r.Games));
        games = FillSizes(games, entries);
        var sources = results
            .Select(r => new SourceStatus(r.Platform, r.Installed, games.Count(g => g.Platform == r.Platform), r.Error))
            .ToList();
        var scan = new LibraryScan(games, sources, DateTime.Now);
        var parts = sources.Where(s => s.Count > 0).Select(s => $"{s.Name} {s.Count}").ToList();
        Log.Info($"Библиотека игр: найдено {games.Count}" + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : ""));
        return scan;
    }

    /// <summary>
    /// Keeps the first game per install folder in platform priority order (Steam, Epic, GOG, Ubisoft, EA, Battle.net, Riot).
    /// User-added games are unique per exe and are always kept. Sorted by name.
    /// </summary>
    public static IReadOnlyList<GameEntry> Deduplicate(IEnumerable<GameEntry> games)
    {
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GameEntry>();
        foreach (var g in games.OrderBy(g => (int)g.Platform))
        {
            if (!seenIds.Add(g.Id)) continue;
            if (g.Platform != GamePlatform.Custom)
            {
                var dir = GamePaths.NormalizeDir(g.InstallDir);
                if (dir.Length > 0 && !seenDirs.Add(dir)) continue;
            }
            result.Add(g);
        }
        return result.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Launchers that do not report a size: take Programs and Features' EstimatedSize for the same folder (cheap).</summary>
    private static IReadOnlyList<GameEntry> FillSizes(IReadOnlyList<GameEntry> games, IReadOnlyList<UninstallEntry> entries)
    {
        if (entries.Count == 0) return games;
        var byDir = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.EstimatedSizeKb <= 0) continue;
            var d = GamePaths.NormalizeDir(e.InstallLocation);
            if (d.Length > 0) byDir.TryAdd(d, e.EstimatedSizeKb * 1024);
        }
        return games.Select(g => g.SizeBytes > 0 || g.Platform == GamePlatform.Custom ? g
            : byDir.TryGetValue(GamePaths.NormalizeDir(g.InstallDir), out var s) ? g with { SizeBytes = s } : g).ToList();
    }

    public static SourceResult ScanCustom(IReadOnlyList<string> exes)
    {
        var list = new List<GameEntry>();
        foreach (var exe in exes.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase))
            list.Add(CustomEntry(exe));
        return new SourceResult(GamePlatform.Custom, list.Count > 0, list);
    }

    /// <summary>A user-added game: the name comes from the exe's version info, falling back to the file name.</summary>
    public static GameEntry CustomEntry(string exe)
    {
        var exists = File.Exists(exe);
        var name = Path.GetFileNameWithoutExtension(exe);
        if (exists)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                var candidate = !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName : vi.FileDescription;
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.Trim().Length is > 1 and < 80) name = candidate.Trim();
            }
            catch { /* keep the file name */ }
        }
        var dir = GamePaths.NormalizeDir(Path.GetDirectoryName(exe));
        return new GameEntry
        {
            Id = "custom:" + exe.ToLowerInvariant(),
            Name = name,
            Platform = GamePlatform.Custom,
            InstallDir = dir,
            ExePath = exe,
            WorkingDir = Path.GetDirectoryName(exe),
            IsAvailable = exists,
        };
    }

    public static LibraryScan? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath)) return null;
            var data = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CacheFilePath), JsonOptions);
            if (data is null || data.Version != 1) return null;
            return new LibraryScan(data.Games, data.Sources, data.ScannedAt);
        }
        catch (Exception ex)
        {
            Log.Warn($"Кэш библиотеки игр повреждён и будет пересоздан: {ex.Message}");
            return null;
        }
    }

    public static void SaveCache(LibraryScan scan)
    {
        try
        {
            if (!AppPaths.TryEnsureDir(AppPaths.CacheDir)) return;
            var tmp = CacheFilePath + ".tmp";
            var data = new CacheFile { ScannedAt = scan.ScannedAt, Games = scan.Games.ToList(), Sources = scan.Sources.ToList() };
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(tmp, CacheFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось сохранить кэш библиотеки игр: {ex.Message}");
        }
    }

    private static SourceResult Safe(GamePlatform platform, Func<SourceResult> scan)
    {
        try
        {
            return scan();
        }
        catch (Exception ex)
        {
            Log.Warn($"{GameText.SourceName(platform)}: ошибка поиска игр — {ex.Message}");
            return new SourceResult(platform, false, [], ex.Message);
        }
    }
}
