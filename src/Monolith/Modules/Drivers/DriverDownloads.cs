using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Monolith.Core;

namespace Monolith.Modules.Drivers;

/// <summary>An official GPU driver installer that MONOLITH can download directly (no browser).</summary>
internal sealed record DriverPackage(
    GpuVendor Vendor,
    string Version,
    Uri Url,
    string FileName,
    string? SizeText,
    /// <summary>Organization that must have signed the file (Authenticode CN/O), e.g. "NVIDIA Corporation".</summary>
    string Signer,
    /// <summary>AMD serves the installer only with a Referer from amd.com.</summary>
    string? Referer)
{
    /// <summary>Exact size reported by the vendor's server (HEAD request), when known.</summary>
    public long? SizeBytes { get; init; }

    public string VendorName => Vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => Loc.T("производителя"),
    };

    /// <summary>Size for the UI ("989 МБ"), or null when the server has not told it yet.</summary>
    public string? SizeDisplay => SizeBytes is > 0 ? SystemProfile.FormatBytes(SizeBytes.Value) : DriverDownloads.LocalizeSize(SizeText);
}

/// <summary>
/// Downloads official GPU driver installers straight from the vendor's servers through the shared
/// <see cref="VendorDownloads"/> mechanism:
///  • HTTPS only, host must be NVIDIA's / AMD's download server (allow-list), also after redirects;
///  • the file must carry a VALID Authenticode signature from the vendor, otherwise it is deleted;
///  • files live in %ProgramData%\MONOLITH\Downloads (admin-only write — nobody can swap them after the check);
///  • the vendor's own installer is then started interactively (the user sees and controls it).
/// </summary>
internal static partial class DriverDownloads
{
    private static readonly string[] AllowedHosts =
    [
        "us.download.nvidia.com", "international.download.nvidia.com", "download.nvidia.com", "uk.download.nvidia.com",
        "drivers.amd.com",
    ];

    private const string AmdDriversPage = "https://www.amd.com/en/support/download/drivers.html";
    private const string NvidiaSigner = "NVIDIA Corporation";
    private const string AmdSigner = "Advanced Micro Devices";

    /// <summary>Driver installers sit directly in the downloads root (app installers have their own sub-folder).</summary>
    public static string DownloadsDir => VendorDownloads.RootDir;

    public static bool IsAllowedDownload(Uri? url) => VendorDownloads.IsAllowedUrl(url, AllowedHosts);

    /// <summary>Safe local file name taken from the URL (no path parts, only [A-Za-z0-9._-], starts with a letter or digit).</summary>
    public static string? SafeFileName(Uri url) => VendorDownloads.SafeFileName(url);

    /// <summary>"1.04 GB" (NVIDIA API) → "1.04 ГБ"; unknown formats are dropped rather than shown half-translated.</summary>
    public static string? LocalizeSize(string? text) => VendorDownloads.LocalizeSize(text);

    /// <summary>The shared download request for a driver package.</summary>
    public static VendorDownloadRequest ToRequest(DriverPackage p) =>
        new(p.Url, p.FileName, AllowedHosts, [p.Signer], DownloadsDir)
        {
            Referer = p.Referer,
            RequiredFreeSpace = RequiredFreeSpace(p.Vendor),
            Label = Loc.Tf("драйвер {0} {1}", p.VendorName, p.Version),
        };

    // ───────────────────────────── NVIDIA ─────────────────────────────

    public static DriverPackage? NvidiaPackage(NvidiaRelease release)
    {
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var url) || !IsAllowedDownload(url)) return null;
        var file = SafeFileName(url);
        return file is null ? null : new DriverPackage(GpuVendor.Nvidia, release.Version, url, file, release.FileSize, NvidiaSigner, null);
    }

    // ───────────────────────────── AMD ─────────────────────────────

    [GeneratedRegex(@"https://drivers\.amd\.com/drivers/installer/[A-Za-z0-9./_-]+?minimalsetup[A-Za-z0-9._-]*\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex AmdInstallerRegex();

    [GeneratedRegex(@"adrenalin-edition-(\d{2}\.\d{1,2}\.\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex AmdVersionRegex();

    /// <summary>Finds the official AMD "minimal setup" (auto-detect) installer link on amd.com.</summary>
    public static DriverPackage? ParseAmdPage(string html)
    {
        var m = AmdInstallerRegex().Match(html);
        if (!m.Success || !Uri.TryCreate(m.Value, UriKind.Absolute, out var url) || !IsAllowedDownload(url)) return null;
        var file = SafeFileName(url);
        if (file is null) return null;
        var v = AmdVersionRegex().Match(file);
        return new DriverPackage(GpuVendor.Amd, v.Success ? v.Groups[1].Value : "—", url, file, null,
            AmdSigner, "https://www.amd.com/");
    }

    public static async Task<DriverPackage?> GetAmdInstallerAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AmdDriversPage);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await VendorDownloads.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseAmdPage(html);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Info("AMD: не удалось получить ссылку на установщик ({0}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Asks the vendor's server for the exact file size (HEAD, nothing is downloaded). Null when the server does not
    /// say or the answer comes from a host outside the allow-list.
    /// </summary>
    public static Task<long?> GetRemoteSizeAsync(DriverPackage p, CancellationToken ct) =>
        VendorDownloads.GetRemoteSizeAsync(ToRequest(p), ct);

    // ───────────────────────────── Download ─────────────────────────────

    /// <summary>
    /// Downloads the package (or reuses an already verified copy). Returns the local path of a file whose
    /// vendor signature has been verified. Throws with a Russian message on any problem; a partial or
    /// unverified file never stays on disk.
    /// </summary>
    public static async Task<string> DownloadAsync(DriverPackage p, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!IsAllowedDownload(p.Url) || !string.Equals(SafeFileName(p.Url), p.FileName, StringComparison.Ordinal))
            throw new InvalidOperationException(Loc.T("Ссылка ведёт не на официальный сервер производителя — загрузка отменена."));
        SafetyLock.ThrowIfLocked(Loc.T("загрузка драйвера"));
        Elevation.Demand(Loc.T("загрузка драйвера"));
        var path = await VendorDownloads.DownloadAsync(ToRequest(p), progress, ct).ConfigureAwait(false);
        CleanupOld(p);
        return path;
    }

    /// <summary>Free space needed on the system drive: NVIDIA unpacks the ~1 GB package into ~2 GB more.</summary>
    public static long RequiredFreeSpace(GpuVendor vendor) => vendor == GpuVendor.Nvidia ? 4L << 30 : 1L << 30;

    // ───────────────────────────── Signature ─────────────────────────────

    /// <summary>Authenticode check: status Valid and the signer is exactly the vendor (see <see cref="VendorDownloads"/>).</summary>
    public static Task<(bool Ok, string Signer)> VerifySignatureAsync(string path, string expectedSigner) =>
        VendorDownloads.VerifySignatureAsync(path, [expectedSigner]);

    /// <summary>Interprets the JSON written by <see cref="VerifySignatureAsync"/>. Pure — unit-tested.</summary>
    internal static (bool Ok, string Signer) EvaluateSignature(string? output, string expectedSigner) =>
        VendorDownloads.EvaluateSignature(output, [expectedSigner]);

    /// <summary>"O=NVIDIA Corporation" lines of X500DistinguishedName.Format(true) → { O: "NVIDIA Corporation" } (first value wins).</summary>
    internal static Dictionary<string, string> ParseNameLines(string text) => VendorDownloads.ParseNameLines(text);

    /// <summary>Exact company-name match, ignoring case, punctuation and a trailing legal form.</summary>
    internal static bool SignerMatches(string? actual, string expected) => VendorDownloads.SignerMatches(actual, expected);

    // ───────────────────────────── Install ─────────────────────────────

    /// <summary>
    /// Starts the vendor's installer interactively. Only files inside MONOLITH's admin-only downloads folder are
    /// accepted (that is where <see cref="DownloadAsync"/> verified them). MONOLITH is elevated, so is the installer —
    /// it installs a driver and would ask for administrator rights anyway.
    /// </summary>
    public static void LaunchInstaller(string path)
    {
        SafetyLock.ThrowIfLocked(Loc.T("запуск установщика драйвера"));
        Elevation.Demand(Loc.T("запуск установщика драйвера"));
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(DownloadsDir), StringComparison.OrdinalIgnoreCase) ||
            !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException(Loc.T("Установщик не найден в папке загрузок MONOLITH."));
        using var _ = Process.Start(new ProcessStartInfo(full) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(full)! });
        Log.Info("Запущен официальный установщик: {0}", Path.GetFileName(full));
    }

    /// <summary>Keeps only the newest installer per vendor (and no leftover partial files) to save disk space.</summary>
    private static void CleanupOld(DriverPackage keep)
    {
        try
        {
            var pattern = keep.Vendor == GpuVendor.Nvidia ? "*-dch-*.exe" : "amd-software-*.exe";
            foreach (var f in Directory.EnumerateFiles(DownloadsDir, pattern).Concat(Directory.EnumerateFiles(DownloadsDir, "*.part")))
            {
                if (!string.Equals(Path.GetFileName(f), keep.FileName, StringComparison.OrdinalIgnoreCase)) VendorDownloads.TryDelete(f);
            }
        }
        catch (Exception ex)
        {
            Log.Info("Старые установщики не удалены: {0}", ex.Message);
        }
    }

    public static string FormatProgress(DownloadProgress p) => VendorDownloads.FormatProgress(p);
}
