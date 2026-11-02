using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lycoris.Modules.Cleaner;

/// <summary>Finds browser profiles (Chromium "Local State", Firefox "profiles.ini").</summary>
public static class BrowserProfiles
{
    /// <summary>Profile folder names from a Chromium "Local State" JSON (profile.info_cache keys).</summary>
    public static List<string> ParseLocalStateProfiles(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object &&
                profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in cache.EnumerateObject())
                    if (IsSafeFolderName(p.Name))
                        result.Add(p.Name);
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    /// <summary>
    /// Profile folders of a Chromium "User Data" directory: names from Local State plus every sub-folder that has a
    /// "Preferences" file. "System Profile" is skipped. Only folders that exist are returned.
    /// </summary>
    public static List<string> ChromiumProfiles(string userDataDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var localState = Path.Combine(userDataDir, "Local State");
            if (File.Exists(localState))
                foreach (var n in ParseLocalStateProfiles(ReadShared(localState)))
                    names.Add(n);
        }
        catch { }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(userDataDir))
            {
                var name = Path.GetFileName(dir);
                if (File.Exists(Path.Combine(dir, "Preferences"))) names.Add(name);
            }
        }
        catch { }

        return names
            .Where(n => !n.Equals("System Profile", StringComparison.OrdinalIgnoreCase) && IsSafeFolderName(n))
            .Where(n => Directory.Exists(Path.Combine(userDataDir, n)))
            .OrderBy(n => n.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>[ProfileN] entries of Firefox profiles.ini: (Path, IsRelative).</summary>
    public static List<(string Path, bool IsRelative)> ParseProfilesIni(string text)
    {
        var result = new List<(string, bool)>();
        string? section = null;
        string? path = null;
        var relative = true;

        void Flush()
        {
            if (section is not null && section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(path))
                result.Add((path!.Trim(), relative));
            path = null;
            relative = true;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                section = line[1..^1].Trim();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("Path", StringComparison.OrdinalIgnoreCase)) path = value;
            else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase)) relative = value != "0";
        }
        Flush();
        return result;
    }

    /// <summary>A plain single folder name: no separators, no "..", no drive.</summary>
    public static bool IsSafeFolderName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." &&
        name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0;

    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>Minimal reader for Valve's KeyValues text format (libraryfolders.vdf).</summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text);
        var pos = 0;
        return ParseBody(tokens, ref pos);
    }

    private static VdfNode ParseBody(List<string?> tokens, ref int pos)
    {
        var node = new VdfNode();
        while (pos < tokens.Count)
        {
            var key = tokens[pos++];
            if (key is null) return node; // "}" closes this level
            if (pos >= tokens.Count) break;
            var next = tokens[pos];
            if (next == "{")
            {
                pos++;
                node.Children[key] = ParseBody(tokens, ref pos);
            }
            else if (next is null)
            {
                // malformed: key followed by "}"
                return node;
            }
            else
            {
                pos++;
                node.Values[key] = next;
            }
        }
        return node;
    }

    /// <summary>Quoted strings (with \\ and \" escapes) and braces; "}" becomes null.</summary>
    private static List<string?> Tokenize(string text)
    {
        var list = new List<string?>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{') { list.Add("{"); i++; continue; }
            if (c == '}') { list.Add(null); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        var n = text[i + 1];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
                        i += 2;
                        continue;
                    }
                    sb.Append(text[i++]);
                }
                i++; // closing quote
                list.Add(sb.ToString());
                continue;
            }
            // unquoted token
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
            list.Add(text[start..i]);
        }
        return list;
    }
}

/// <summary>Steam libraries and shader caches of uninstalled games.</summary>
public static class SteamLibraries
{
    public sealed record Library(string Path, IReadOnlySet<string> Apps);

    /// <summary>Libraries listed in libraryfolders.vdf (path + app ids the file says are installed there).</summary>
    public static List<Library> ParseLibraryFolders(string vdf)
    {
        var result = new List<Library>();
        var root = VdfNode.Parse(vdf);
        var folders = root.Children.TryGetValue("libraryfolders", out var lf) ? lf : root;
        foreach (var (_, node) in folders.Children)
        {
            if (!node.Values.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path)) continue;
            var apps = node.Children.TryGetValue("apps", out var a)
                ? a.Values.Keys.Where(IsAppId).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            result.Add(new Library(path, apps));
        }
        return result;
    }

    public static bool IsAppId(string s) => s.Length is > 0 and <= 10 && s.All(char.IsAsciiDigit) && s != "0";

    /// <summary>Shader-cache folder names (app ids) that belong to no installed game.</summary>
    public static List<string> FindOrphans(IEnumerable<string> shaderCacheFolders, IReadOnlySet<string> installedApps) =>
        shaderCacheFolders.Where(IsAppId).Where(id => !installedApps.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Every library with its shader-cache orphans. Returns null when a listed library is not reachable (unplugged
    /// drive): then nothing is reported, because the games on that drive cannot be checked.
    /// </summary>
    public static List<(string ShaderCache, List<string> Orphans)>? FindOrphanShaderCaches(string steamPath)
    {
        var libraries = new List<Library>();
        foreach (var vdfPath in new[] { Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"), Path.Combine(steamPath, "config", "libraryfolders.vdf") })
        {
            try
            {
                if (File.Exists(vdfPath))
                {
                    libraries.AddRange(ParseLibraryFolders(BrowserProfiles.ReadShared(vdfPath)));
                    break;
                }
            }
            catch { }
        }

        var paths = libraries.Select(l => PathGuard.Normalize(l.Path)).Where(p => p is not null).Select(p => p!).ToList();
        paths.Add(PathGuard.Normalize(steamPath)!);
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var installed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in libraries) installed.UnionWith(l.Apps);
        foreach (var lib in paths)
        {
            var steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) return null;
            try
            {
                foreach (var f in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    var id = Path.GetFileNameWithoutExtension(f)["appmanifest_".Length..];
                    if (IsAppId(id)) installed.Add(id);
                }
            }
            catch
            {
                return null;
            }
        }

        var result = new List<(string, List<string>)>();
        foreach (var lib in paths)
        {
            var sc = Path.Combine(lib, "steamapps", "shadercache");
            if (!Directory.Exists(sc)) continue;
            try
            {
                var orphans = FindOrphans(Directory.EnumerateDirectories(sc).Select(Path.GetFileName).Where(n => n is not null)!, installed);
                result.Add((sc, orphans));
            }
            catch { }
        }
        return result;
    }
}

/// <summary>Parses DISM /AnalyzeComponentStore and progress output (run with /English).</summary>
public static partial class DismOutput
{
    public sealed record ComponentStoreReport(bool? CleanupRecommended, string? ActualSize, string? Reclaimable);

    public static ComponentStoreReport ParseAnalyze(string output)
    {
        bool? recommended = null;
        string? actual = null, reclaimable = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.LastIndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("Component Store Cleanup Recommended", StringComparison.OrdinalIgnoreCase))
                recommended = value.Equals("Yes", StringComparison.OrdinalIgnoreCase);
            else if (key.Equals("Actual Size of Component Store", StringComparison.OrdinalIgnoreCase))
                actual = value;
            else if (key.Equals("Number of Reclaimable Packages", StringComparison.OrdinalIgnoreCase))
                reclaimable = value;
        }
        return new ComponentStoreReport(recommended, actual, reclaimable);
    }

    /// <summary>"[=====    23.4%     ]" → 23.4.</summary>
    public static double? ParsePercent(string line)
    {
        var m = PercentRegex().Match(line);
        if (!m.Success) return null;
        return double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 100)
            : null;
    }

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();
}
