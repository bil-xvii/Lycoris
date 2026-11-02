using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Monolith.Core;

namespace Monolith.Modules.Drivers;

internal sealed record NvidiaProduct(int Psid, int Pfid, string Name);

internal sealed record NvidiaRelease(string Version, DateTime? Released, bool IsWhql, bool IsBeta, string? DetailsUrl, string? Name, string? FileSize)
{
    /// <summary>Official installer on NVIDIA's download server (validated by DriverDownloads before use).</summary>
    public string? DownloadUrl { get; init; }
}

/// <summary>
/// "Update available" hint for NVIDIA GPUs. The endpoints are hosted by NVIDIA but undocumented
/// (research §3: "unofficial — use only for a hint"): any error or schema change silently returns null,
/// and the UI falls back to "check in NVIDIA App / on nvidia.com". Downloading itself is done by DriverDownloads,
/// which only accepts NVIDIA's download servers and a valid NVIDIA signature.
/// </summary>
internal static partial class NvidiaLookup
{
    private const string LookupUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    private const string DriverUrlFormat =
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
        "?func=DriverManualLookup&psid={0}&pfid={1}&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Lycoris/1.0");
        return c;
    });

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>"NVIDIA GeForce RTX 5070 Ti" and "GeForce RTX 5070 Ti" normalize to the same key.</summary>
    public static string NormalizeName(string name)
    {
        var n = name.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("®", "").Replace("™", "");
        n = Spaces().Replace(n, " ").Trim();
        if (n.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) n = n[7..];
        return n.Trim().ToUpperInvariant();
    }

    /// <summary>Finds psid/pfid for a GPU in the lookup XML. Several matches → the newest series (highest psid).</summary>
    public static NvidiaProduct? FindProduct(string xml, string gpuName)
    {
        var key = NormalizeName(gpuName);
        if (key.Length == 0) return null;
        var doc = XDocument.Parse(xml);
        NvidiaProduct? best = null;
        foreach (var lv in doc.Descendants("LookupValue"))
        {
            var name = lv.Element("Name")?.Value;
            if (name is null || NormalizeName(name) != key) continue;
            if (!int.TryParse(lv.Attribute("ParentID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var psid)) continue;
            if (!int.TryParse(lv.Element("Value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pfid)) continue;
            if (best is null || psid > best.Psid) best = new NvidiaProduct(psid, pfid, name);
        }
        return best;
    }

    /// <summary>
    /// Parses the DriverManualLookup JSON answer. Returns null on any unexpected shape (including invalid JSON).
    /// DownloadURL is kept only when it points to NVIDIA's download servers (<see cref="DriverDownloads.IsAllowedDownload"/>).
    /// </summary>
    public static NvidiaRelease? ParseRelease(string json)
    {
        try
        {
            return ParseReleaseCore(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static NvidiaRelease? ParseReleaseCore(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Str(root, "Success") != "1") return null;
        if (!root.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0) return null;
        if (ids[0].ValueKind != JsonValueKind.Object || !ids[0].TryGetProperty("downloadInfo", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        var version = Str(info, "Version");
        if (string.IsNullOrWhiteSpace(version) || !Regex.IsMatch(version, @"^\d{3,4}\.\d{2}$")) return null;

        DateTime? released = null;
        var dateText = Str(info, "ReleaseDateTime");
        if (dateText is not null &&
            DateTime.TryParseExact(dateText.Trim(), ["ddd MMM d, yyyy", "ddd MMM dd, yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            released = d;

        var details = Str(info, "DetailsURL");
        if (!DriverSources.IsAllowedUrl(details)) details = null;
        var name = Str(info, "Name");
        if (name is not null)
        {
            try { name = Uri.UnescapeDataString(name); } catch { }
        }

        var download = Str(info, "DownloadURL");
        if (!Uri.TryCreate(download, UriKind.Absolute, out var dl) || !DriverDownloads.IsAllowedDownload(dl)) download = null;

        return new NvidiaRelease(version, released, Str(info, "IsWHQL") == "1", Str(info, "IsBeta") == "1", details, name, Str(info, "DownloadURLFileSize"))
        {
            DownloadUrl = download,
        };
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    /// <summary>Latest WHQL Game Ready driver for this GPU, or null when unknown (offline, API change, no match).</summary>
    public static async Task<NvidiaRelease?> GetLatestAsync(string gpuName, CancellationToken ct = default)
    {
        try
        {
            var xml = await GetLookupXmlAsync(ct).ConfigureAwait(false);
            if (xml is null) return null;
            var product = FindProduct(xml, gpuName);
            if (product is null)
            {
                Log.Info("NVIDIA: «{0}» нет в справочнике nvidia.com — проверка версии пропущена.", gpuName);
                return null;
            }
            var url = string.Format(CultureInfo.InvariantCulture, DriverUrlFormat, product.Psid, product.Pfid);
            var json = await Http.Value.GetStringAsync(url, ct).ConfigureAwait(false);
            return ParseRelease(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Info("NVIDIA: не удалось узнать последнюю версию драйвера ({0}).", ex.GetType().Name);
            return null;
        }
    }

    private static async Task<string?> GetLookupXmlAsync(CancellationToken ct)
    {
        var cacheFile = Path.Combine(AppPaths.CacheDir, "nvidia-lookup.xml");
        try
        {
            if (File.Exists(cacheFile) && DateTime.Now - File.GetLastWriteTime(cacheFile) < CacheMaxAge)
                return await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false);
        }
        catch { }

        var xml = await Http.Value.GetStringAsync(LookupUrl, ct).ConfigureAwait(false);
        if (!xml.Contains("<LookupValue", StringComparison.Ordinal)) return null;
        try
        {
            if (AppPaths.TryEnsureDir(AppPaths.CacheDir))
                await File.WriteAllTextAsync(cacheFile, xml, ct).ConfigureAwait(false);
        }
        catch { }
        return xml;
    }
}
