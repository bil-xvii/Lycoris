using System.IO;
using System.Text.Json;
using Lycoris.Core;

namespace Lycoris.Modules.Optimize;

/// <summary>
/// Remembers the "СВОЙ НАБОР" selection between launches (%LocalAppData%\Lycoris\optimize-custom.json).
/// Only ids are stored; expert tweaks are never restored automatically (their warning must be accepted again).
/// </summary>
internal static class CustomSelectionStore
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(AppPaths.UserDir, "optimize-custom.json");

    public static HashSet<string>? Load() => Load(FilePath);

    /// <summary>Load from a specific path (testing overload). Always returns null on error.</summary>
    internal static HashSet<string>? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(Lycoris.Core.Security.SafeIo.ReadText(path)) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Оптимизация: не удалось прочитать свой набор ({0}).", ex.Message);
            return null;
        }
    }

    /// <summary>Parses a JSON array of ids; anything malformed yields null (= no saved selection).</summary>
    internal static HashSet<string>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<string?[]>(json);
            return ids?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Serialize(IEnumerable<string> ids) =>
        JsonSerializer.Serialize(ids.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray());

    public static void Save(IEnumerable<string> ids)
    {
        var json = Serialize(ids);
        lock (Gate)
        {
            try
            {
                if (!AppPaths.TryEnsureDir(AppPaths.UserDir)) return;
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn("Оптимизация: не удалось сохранить свой набор ({0}).", ex.Message);
            }
        }
    }
}
