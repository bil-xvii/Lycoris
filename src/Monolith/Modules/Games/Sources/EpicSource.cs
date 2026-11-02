using System.IO;
using System.Text.Json;
using Monolith.Core;

namespace Monolith.Modules.Games.Sources;

/// <summary>One Epic Games Launcher install manifest (*.item, JSON).</summary>
public sealed record EpicManifest(
    string DisplayName,
    string InstallLocation,
    string LaunchExecutable,
    string AppName,
    string? CatalogNamespace,
    string? CatalogItemId,
    bool IsIncompleteInstall,
    long InstallSize,
    string? MainGameAppName)
{
    /// <summary>DLC manifests point at their base game through MainGameAppName.</summary>
    public bool IsDlc => !string.IsNullOrEmpty(MainGameAppName) &&
                         !string.Equals(MainGameAppName, AppName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Launch URL per research: com.epicgames.launcher://apps/&lt;ns&gt;%3A&lt;itemId&gt;%3A&lt;AppName&gt;?action=launch&amp;silent=true,
    /// or the short form with only the AppName when the catalog ids are missing.
    /// </summary>
    public string LaunchUri
    {
        get
        {
            var app = Uri.EscapeDataString(AppName);
            if (!string.IsNullOrEmpty(CatalogNamespace) && !string.IsNullOrEmpty(CatalogItemId))
                return $"com.epicgames.launcher://apps/{Uri.EscapeDataString(CatalogNamespace)}%3A{Uri.EscapeDataString(CatalogItemId)}%3A{app}?action=launch&silent=true";
            return $"com.epicgames.launcher://apps/{app}?action=launch&silent=true";
        }
    }
}

/// <summary>Epic Games Launcher: %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item (docs/research/tech.md §6).</summary>
public static class EpicSource
{
    public static string LauncherDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher");

    public static string ManifestDir => Path.Combine(LauncherDataDir, "Data", "Manifests");

    public static EpicManifest? ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            var appName = Str(r, "AppName");
            var display = Str(r, "DisplayName");
            var location = Str(r, "InstallLocation");
            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(location)) return null;

            return new EpicManifest(
                string.IsNullOrWhiteSpace(display) ? appName : display.Trim(),
                location,
                Str(r, "LaunchExecutable") ?? "",
                appName,
                Str(r, "CatalogNamespace"),
                Str(r, "CatalogItemId"),
                Bool(r, "bIsIncompleteInstall"),
                Long(r, "InstallSize"),
                Str(r, "MainGameAppName"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static SourceResult Scan()
    {
        if (!Directory.Exists(LauncherDataDir)) return SourceResult.NotInstalled(GamePlatform.Epic);
        var games = new List<GameEntry>();
        if (!Directory.Exists(ManifestDir)) return new SourceResult(GamePlatform.Epic, true, games);

        List<string> files;
        try { files = Directory.EnumerateFiles(ManifestDir, "*.item").ToList(); }
        catch (Exception ex) { return new SourceResult(GamePlatform.Epic, true, games, ex.Message); }

        foreach (var file in files)
        {
            try
            {
                var m = ParseManifest(Monolith.Core.Security.SafeIo.ReadText(file));
                if (m is null || m.IsIncompleteInstall || m.IsDlc || string.IsNullOrWhiteSpace(m.LaunchExecutable)) continue;
                var dir = GamePaths.NormalizeDir(m.InstallLocation);
                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                games.Add(new GameEntry
                {
                    Id = $"epic:{m.AppName}",
                    Name = m.DisplayName,
                    Platform = GamePlatform.Epic,
                    InstallDir = dir,
                    LaunchUri = m.LaunchUri,
                    SizeBytes = m.InstallSize,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("Epic Games: пропущен {0} — {1}", Path.GetFileName(file), ex.Message);
            }
        }
        return new SourceResult(GamePlatform.Epic, true, games);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return Math.Max(0, l);
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return Math.Max(0, s);
        return 0;
    }
}
