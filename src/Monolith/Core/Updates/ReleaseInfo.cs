using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monolith.Core.Updates;

/// <summary>One published Lycoris release as far as the updater needs it (parsed from the GitHub API, cached in settings).</summary>
public sealed record ReleaseInfo
{
    public string Tag { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Release notes (Markdown text as written by the owner).</summary>
    public string Notes { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string ExeUrl { get; init; } = "";
    public long ExeSize { get; init; }
    /// <summary>Lower-case hex SHA-256 from the asset's "digest": "sha256:…" (GitHub computes it), or "".</summary>
    public string ExeDigest { get; init; } = "";
    /// <summary>URL of Lycoris.exe.sha256 ("" when the release has none).</summary>
    public string ShaUrl { get; init; } = "";

    [JsonIgnore]
    public SemVer Version => SemVer.TryParse(Tag, out var v) ? v : default;

    [JsonIgnore]
    public string VersionText => Version.ToString();

    /// <summary>The first <paramref name="maxLines"/> non-empty lines of the notes, Markdown marks stripped lightly.</summary>
    public string NotesPreview(int maxLines = 15)
    {
        var lines = Notes.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("`", ""))
            .Where(l => l.Length > 0)
            .ToList();
        var text = string.Join("\n", lines.Take(maxLines));
        if (text.Length > 1500) text = text[..1500] + "…";
        return lines.Count > maxLines ? text + "\n…" : text;
    }
}

public static class ReleaseParser
{
    public const string ExeAssetName = "Lycoris.exe";
    public const string ShaAssetName = "Lycoris.exe.sha256";

    private static readonly JsonSerializerOptions CacheOptions = new() { WriteIndented = false };

    /// <summary>
    /// Reads the answer of GET /repos/…/releases/latest. A draft, a pre-release, a tag that is not a version or a release
    /// without the Lycoris.exe asset is "no usable release" (null + reason); malformed JSON never throws.
    /// </summary>
    public static ReleaseInfo? TryParse(string? json, out string? problem)
    {
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { problem = "not an object"; return null; }
            var tag = Str(root, "tag_name");
            if (!SemVer.TryParse(tag, out var version)) { problem = "tag is not a version: " + Short(tag); return null; }
            if (Bool(root, "draft")) { problem = "draft"; return null; }
            if (Bool(root, "prerelease") || version.IsPrerelease) { problem = "prerelease"; return null; }

            string exeUrl = "", digest = "", shaUrl = "";
            long exeSize = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (Str(a, "state") is { Length: > 0 } st && !st.Equals("uploaded", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals(ExeAssetName, StringComparison.Ordinal))
                    {
                        exeUrl = url;
                        exeSize = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var s) ? s : 0;
                        var d = Str(a, "digest");
                        if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && UpdateVerifier.IsSha256Hex(d[7..]))
                            digest = d[7..].ToLowerInvariant();
                    }
                    else if (name.Equals(ShaAssetName, StringComparison.Ordinal))
                    {
                        shaUrl = url;
                    }
                }
            }
            if (exeUrl.Length == 0) { problem = "no " + ExeAssetName + " asset"; return null; }

            return new ReleaseInfo
            {
                Tag = tag,
                Name = Str(root, "name"),
                Notes = Str(root, "body"),
                PageUrl = Str(root, "html_url"),
                ExeUrl = exeUrl,
                ExeSize = exeSize,
                ExeDigest = digest,
                ShaUrl = shaUrl,
            };
        }
        catch (JsonException ex)
        {
            problem = "malformed JSON: " + ex.Message;
            return null;
        }
    }

    public static string Serialize(ReleaseInfo r) => JsonSerializer.Serialize(r, CacheOptions);

    /// <summary>The cached copy kept in settings; a damaged value is simply "no cache".</summary>
    public static ReleaseInfo? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<ReleaseInfo>(json, CacheOptions);
            return r is not null && SemVer.TryParse(r.Tag, out _) && r.ExeUrl.Length > 0 ? r : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Short(string s) => s.Length > 40 ? s[..40] : s;
}
