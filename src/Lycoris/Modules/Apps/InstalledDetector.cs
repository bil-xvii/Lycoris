using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Lycoris.Core;

namespace Lycoris.Modules.Apps;

/// <summary>Result of one detection pass: package id → installed version ("" = installed, version unknown).</summary>
public sealed record InstalledSnapshot(IReadOnlyDictionary<string, string> Packages, DateTime CheckedAt, bool FromCache)
{
    public static InstalledSnapshot Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), DateTime.MinValue, false);

    /// <summary>
    /// Ids found only by the read-only cross-checks, not by winget: the component is on the PC, but winget does not
    /// list it as its package (e.g. the .NET Desktop Runtime that the .NET SDK or Visual Studio brought along — winget
    /// sees only "Microsoft.DotNet.SDK.10"). "winget upgrade --id" answers "no package found" (0x8A150014) for these,
    /// so the page never offers to update them.
    /// </summary>
    public IReadOnlySet<string> Unmanaged { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Finds out which catalog packages are installed — read-only, never parses winget's localized tables:
///  1. "winget export --include-versions" (structured JSON) for everything winget can match to a source;
///  2. documented read-only cross-checks for runtimes winget often misses (WebView2/Edge EdgeUpdate "pv",
///     VC++ 2015+ runtime keys, .NET shared-framework folders, .NET Framework "Release", D3DX9 files);
///  3. "winget list --id X --exact" exit codes for Microsoft Store entries the export did not list.
/// The last result is cached so the page shows real data instantly on the next start.
/// </summary>
public static class InstalledDetector
{
    private const string WebView2ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string EdgeClientGuid = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";

    /// <summary>D3DX9 June 2010 — the last legacy DirectX SDK runtime (winget manifest Microsoft.DirectX 9.29.1974).</summary>
    public const string DirectXLegacyVersion = "9.29.1974";

    private static string CacheFile => Path.Combine(AppPaths.CacheDir, "apps-installed.json");

    /// <summary>
    /// Runs the full detection. <paramref name="progress"/> receives the intermediate result after the export and
    /// cross-checks (fast), before the per-package Store checks.
    /// </summary>
    public static async Task<InstalledSnapshot> DetectAsync(IReadOnlyCollection<CatalogEntry> catalog,
        Action<InstalledSnapshot>? progress, CancellationToken ct)
    {
        var ids = new HashSet<string>(catalog.SelectMany(e => e.AllIds), StringComparer.OrdinalIgnoreCase);

        var exported = await WingetClient.ExportInstalledAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in exported)
            if (ids.Contains(id)) map[id] = version;
        var managed = new HashSet<string>(map.Keys, StringComparer.OrdinalIgnoreCase);
        // An empty export means "winget export" failed (a working winget always lists something). Then "found only by
        // the cross-checks" would wrongly mark every runtime as not managed by winget: keep the last known list instead.
        var exportOk = exported.Count > 0;
        IReadOnlySet<string> Unmanaged() => exportOk ? UnmanagedIds(map, managed) : PreviousUnmanaged();

        await Task.Run(() => ApplyCrossChecks(map), ct).ConfigureAwait(false);
        progress?.Invoke(new InstalledSnapshot(new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase), DateTime.Now, false)
        {
            Unmanaged = Unmanaged(),
        });

        // Store apps are often missing from the export; "winget list --id X -e" exit 0 = installed (verified).
        foreach (var store in catalog.Where(e => e.Source == PackageSource.MsStore))
        {
            ct.ThrowIfCancellationRequested();
            if (map.ContainsKey(store.Id)) continue;
            if (await WingetClient.IsInstalledAsync(store.Id, ct).ConfigureAwait(false))
            {
                map[store.Id] = "";
                managed.Add(store.Id);
            }
        }

        var snapshot = new InstalledSnapshot(map, DateTime.Now, false) { Unmanaged = Unmanaged() };
        // A failed export (no network, sources not set up yet) gives a degraded list: it must not replace the last good one.
        if (exportOk) SaveCache(snapshot);
        else Log.Warn("winget export не вернул списка — результат проверки не сохранён в кэш.");
        return snapshot;
    }

    private static IReadOnlySet<string> PreviousUnmanaged()
    {
        try { return LoadCache()?.Unmanaged ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Installed ids that winget itself did not report (found only by the cross-checks).</summary>
    internal static HashSet<string> UnmanagedIds(IReadOnlyDictionary<string, string> installed, IReadOnlySet<string> fromWinget) =>
        new(installed.Keys.Where(id => !fromWinget.Contains(id)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Read-only checks documented by Microsoft for components that winget does not always correlate.</summary>
    internal static void ApplyCrossChecks(Dictionary<string, string> map)
    {
        // WebView2 Runtime and Edge: EdgeUpdate client "pv" (machine-wide, then per-user install).
        SetIfMissing(map, "Microsoft.EdgeWebView2Runtime", ReadEdgeUpdateVersion(WebView2ClientGuid));
        SetIfMissing(map, "Microsoft.Edge", ReadEdgeUpdateVersion(EdgeClientGuid));

        // Visual C++ 2015–2022 (v14) runtime keys.
        SetIfMissing(map, "Microsoft.VCRedist.2015+.x64", ReadVcRuntime(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"));
        SetIfMissing(map, "Microsoft.VCRedist.2015+.x86", ReadVcRuntime(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86"));

        // .NET Desktop Runtime: shared framework folders (what "dotnet --list-runtimes" reads).
        foreach (var (major, version) in ReadDesktopRuntimes())
            SetIfMissing(map, $"Microsoft.DotNet.DesktopRuntime.{major}", version);

        // .NET Framework 4.8.1: NDP v4 "Release" ≥ 533320.
        var release = RegistryHelper.ReadInt(RegHive.HKLM, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release");
        if (release >= 533320) SetIfMissing(map, "Microsoft.DotNet.Framework.Runtime", "4.8.1");

        // DirectX End-User Runtime: the classic D3DX9 / XAudio 2.7 DLLs decide, not winget. On Windows 10 1809+/11
        // winget may match the MSIX "DirectXRuntime" framework, which does not contain these DLLs (research §4).
        if (HasLegacyDirectX()) map["Microsoft.DirectX"] = DirectXLegacyVersion;
        else map.Remove("Microsoft.DirectX");
    }

    private static void SetIfMissing(Dictionary<string, string> map, string id, string? version)
    {
        if (version is null || map.ContainsKey(id)) return;
        map[id] = version;
    }

    private static string? ReadEdgeUpdateVersion(string clientGuid)
    {
        foreach (var (hive, path) in new[]
        {
            (RegHive.HKLM, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{clientGuid}"),
            (RegHive.HKLM, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{clientGuid}"),
            (RegHive.HKCU, $@"Software\Microsoft\EdgeUpdate\Clients\{clientGuid}"),
        })
        {
            var pv = RegistryHelper.ReadString(hive, path, "pv");
            if (!string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0") return pv.Trim();
        }
        return null;
    }

    private static string? ReadVcRuntime(string path)
    {
        if (RegistryHelper.ReadInt(RegHive.HKLM, path, "Installed") != 1) return null;
        var v = RegistryHelper.ReadString(RegHive.HKLM, path, "Version");
        return string.IsNullOrWhiteSpace(v) ? "" : v.Trim().TrimStart('v', 'V');
    }

    private static IEnumerable<(int Major, string Version)> ReadDesktopRuntimes()
    {
        var found = new Dictionary<int, Version>();
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var dir = Path.Combine(root, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    var cut = name.IndexOf('-');
                    if (cut > 0) continue; // previews ("10.0.0-rc.2") do not count
                    if (!Version.TryParse(name, out var v)) continue;
                    if (!found.TryGetValue(v.Major, out var best) || v > best) found[v.Major] = v;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable folder: treat as not installed
            }
        }
        return found.Select(kv => (kv.Key, kv.Value.ToString()));
    }

    internal static bool HasLegacyDirectX()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        bool Has(string dir) => File.Exists(Path.Combine(windows, dir, "d3dx9_43.dll")) && File.Exists(Path.Combine(windows, dir, "xaudio2_7.dll"));
        var native = Has("System32");
        return Environment.Is64BitOperatingSystem ? native && Has("SysWOW64") : native;
    }

    // ─────────────────────────── cache ───────────────────────────

    private sealed class CacheDto
    {
        public DateTime CheckedAt { get; set; }
        public Dictionary<string, string> Packages { get; set; } = new();
        public List<string>? Unmanaged { get; set; }
    }

    public static InstalledSnapshot? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var dto = JsonSerializer.Deserialize<CacheDto>(Lycoris.Core.Security.SafeIo.ReadText(CacheFile));
            if (dto?.Packages is null) return null;
            return new InstalledSnapshot(new Dictionary<string, string>(dto.Packages, StringComparer.OrdinalIgnoreCase), dto.CheckedAt, true)
            {
                Unmanaged = new HashSet<string>(dto.Unmanaged ?? [], StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Приложения: кэш установленных программ не прочитан ({0})", ex.Message);
            return null;
        }
    }

    private static void SaveCache(InstalledSnapshot snapshot)
    {
        try
        {
            if (!AppPaths.TryEnsureDir(AppPaths.CacheDir)) return;
            var dto = new CacheDto
            {
                CheckedAt = snapshot.CheckedAt,
                Packages = new Dictionary<string, string>(snapshot.Packages),
                Unmanaged = snapshot.Unmanaged.ToList(),
            };
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Приложения: не удалось сохранить кэш ({0})", ex.Message);
        }
    }

    /// <summary>Display form of a winget version: export may write "&gt; 1.8.10" or "Unknown".</summary>
    public static string CleanVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var v = version.Trim().TrimStart('>', '<', '=', ' ', 'v', 'V').Trim();
        if (v.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "";
        return v.Length > 24 ? v[..24] : v;
    }
}
