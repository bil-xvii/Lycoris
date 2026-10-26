using System.Globalization;
using System.IO;

namespace Monolith.Modules.Games;

/// <summary>Where a game came from. The order is also the de-duplication priority.</summary>
public enum GamePlatform { Steam, Epic, Gog, Ubisoft, Ea, BattleNet, Riot, Custom }

/// <summary>One installed game, as found by a library source (or added by the user).</summary>
public sealed record GameEntry
{
    /// <summary>Stable id: "steam:570", "epic:Fortnite", "custom:C:\Games\x.exe".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required GamePlatform Platform { get; init; }
    /// <summary>Install folder; Game Boost detects the game process by this path prefix.</summary>
    public string InstallDir { get; init; } = "";
    /// <summary>Launcher protocol URL (steam://, com.epicgames.launcher://, uplay://). Null when the exe is started directly.</summary>
    public string? LaunchUri { get; init; }
    /// <summary>Executable started directly (GOG, EA, Battle.net, Riot client, user-added games).</summary>
    public string? ExePath { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDir { get; init; }
    /// <summary>Size on disk when a launcher reports it cheaply; 0 = unknown.</summary>
    public long SizeBytes { get; init; }
    /// <summary>False when a user-added exe no longer exists.</summary>
    public bool IsAvailable { get; init; } = true;
}

/// <summary>Result of one library source (Steam, Epic…).</summary>
public sealed record SourceResult(GamePlatform Platform, bool Installed, IReadOnlyList<GameEntry> Games, string? Error = null)
{
    public static SourceResult NotInstalled(GamePlatform p) => new(p, false, []);
}

/// <summary>Text helpers for game cards and platform labels (pure, unit-tested).</summary>
public static class GameText
{
    private static readonly HashSet<string> SkipWords = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "of" };

    /// <summary>Two-letter monogram for the typographic cover: "Counter-Strike 2" → "CS", "Dota 2" → "D2".</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) current.Append(ch);
            else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());
        if (words.Count == 0) return "?";

        var meaningful = words.Where(w => !SkipWords.Contains(w)).ToList();
        if (meaningful.Count == 0) meaningful = words;

        if (meaningful.Count == 1)
        {
            var w = meaningful[0];
            return (w.Length >= 2 ? w[..2] : w).ToUpper(CultureInfo.CurrentCulture);
        }
        return (char.ToUpper(meaningful[0][0], CultureInfo.CurrentCulture).ToString() +
                char.ToUpper(meaningful[1][0], CultureInfo.CurrentCulture));
    }

    public static string PlatformLabel(GamePlatform p) => p switch
    {
        GamePlatform.Steam => "STEAM",
        GamePlatform.Epic => "EPIC GAMES",
        GamePlatform.Gog => "GOG",
        GamePlatform.Ubisoft => "UBISOFT",
        GamePlatform.Ea => "EA APP",
        GamePlatform.BattleNet => "BATTLE.NET",
        GamePlatform.Riot => "RIOT",
        _ => "СВОИ",
    };

    public static string SourceName(GamePlatform p) => p switch
    {
        GamePlatform.Steam => "Steam",
        GamePlatform.Epic => "Epic Games",
        GamePlatform.Gog => "GOG Galaxy",
        GamePlatform.Ubisoft => "Ubisoft Connect",
        GamePlatform.Ea => "EA app",
        GamePlatform.BattleNet => "Battle.net",
        GamePlatform.Riot => "Riot Client",
        _ => "Добавлены вручную",
    };

    /// <summary>Russian plural: 1 игра, 2 игры, 5 игр.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var n100 = Math.Abs(n) % 100;
        var n10 = n100 % 10;
        if (n100 is >= 11 and <= 14) return many;
        return n10 switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }
}

/// <summary>Path helpers used for matching game processes to install folders (pure, unit-tested).</summary>
public static class GamePaths
{
    /// <summary>Full path with backslashes and exactly one trailing backslash, or "" if invalid.</summary>
    public static string NormalizeDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        try
        {
            var full = Path.GetFullPath(dir.Trim().Trim('"').Replace('/', '\\'));
            return full.TrimEnd('\\') + "\\";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="normalizedDir"/> (case-insensitive).</summary>
    public static bool IsUnder(string? path, string normalizedDir)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(normalizedDir)) return false;
        return path.Replace('/', '\\').StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A folder specific enough to identify one game: not a drive root and not a shared top-level
    /// folder like "C:\Games\" or "C:\Program Files\". Process matching by prefix is only safe for those.
    /// </summary>
    public static bool IsSpecificDir(string normalizedDir)
    {
        if (string.IsNullOrEmpty(normalizedDir)) return false;
        var parts = normalizedDir.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false; // "C:\"
        var system = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        foreach (var s in system)
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (string.Equals(NormalizeDir(s), normalizedDir, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>"C:/x/y.exe,0" or "\"C:\x\y.exe\"" → "C:\x\y.exe"; null when not an .exe.</summary>
    public static string? ExeFromIconValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.StartsWith('"'))
        {
            var end = v.IndexOf('"', 1);
            v = end > 1 ? v[1..end] : v.Trim('"');
        }
        else
        {
            var comma = v.LastIndexOf(',');
            if (comma > 0 && v[(comma + 1)..].Trim().TrimStart('-').All(char.IsDigit)) v = v[..comma];
        }
        v = v.Trim().Replace('/', '\\');
        return v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v : null;
    }
}
