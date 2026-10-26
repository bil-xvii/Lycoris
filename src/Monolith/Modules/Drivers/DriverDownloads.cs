using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
    /// <summary>Text that must appear in the Authenticode signer subject (e.g. "NVIDIA Corporation").</summary>
    string Signer,
    /// <summary>AMD serves the installer only with a Referer from amd.com.</summary>
    string? Referer);

internal readonly record struct DownloadProgress(long Done, long? Total)
{
    public double Percent => Total is > 0 ? Math.Clamp(Done * 100d / Total.Value, 0, 100) : 0;
}

/// <summary>
/// Downloads official GPU driver installers straight from the vendor's servers:
///  • HTTPS only, host must be NVIDIA's / AMD's download server (allow-list);
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

    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private const string AmdDriversPage = "https://www.amd.com/en/support/download/drivers.html";

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return c;
    });

    public static string DownloadsDir => Path.Combine(AppPaths.MachineDir, "Downloads");

    public static bool IsAllowedDownload(Uri? url) =>
        url is not null && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort &&
        AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase) &&
        url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Safe local file name taken from the URL (no path parts, only [A-Za-z0-9._-]).</summary>
    public static string? SafeFileName(Uri url)
    {
        var name = Path.GetFileName(url.AbsolutePath);
        if (string.IsNullOrEmpty(name) || !SafeNameRegex().IsMatch(name)) return null;
        return name;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{5,160}\.exe$")]
    private static partial Regex SafeNameRegex();

    // ───────────────────────────── NVIDIA ─────────────────────────────

    public static DriverPackage? NvidiaPackage(NvidiaRelease release)
    {
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var url) || !IsAllowedDownload(url)) return null;
        var file = SafeFileName(url);
        return file is null ? null : new DriverPackage(GpuVendor.Nvidia, release.Version, url, file, release.FileSize, "NVIDIA Corporation", null);
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
            "Advanced Micro Devices", "https://www.amd.com/");
    }

    public static async Task<DriverPackage?> GetAmdInstallerAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AmdDriversPage);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await Http.Value.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseAmdPage(html);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Info($"AMD: не удалось получить ссылку на установщик ({ex.GetType().Name}).");
            return null;
        }
    }

    // ───────────────────────────── Download ─────────────────────────────

    /// <summary>
    /// Downloads the package (or reuses an already verified copy). Returns the local path of a file whose
    /// vendor signature has been verified. Throws with a Russian message on any problem.
    /// </summary>
    public static async Task<string> DownloadAsync(DriverPackage p, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!IsAllowedDownload(p.Url)) throw new InvalidOperationException("Ссылка ведёт не на официальный сервер производителя — загрузка отменена.");
        SafetyLock.ThrowIfLocked("загрузка драйвера");
        Directory.CreateDirectory(DownloadsDir);
        var target = Path.Combine(DownloadsDir, p.FileName);

        // Already downloaded and still validly signed → reuse.
        if (File.Exists(target))
        {
            var (ok, _) = await VerifySignatureAsync(target, p.Signer).ConfigureAwait(false);
            if (ok)
            {
                progress?.Report(new DownloadProgress(new FileInfo(target).Length, new FileInfo(target).Length));
                return target;
            }
            File.Delete(target);
        }

        var drive = new DriveInfo(Path.GetPathRoot(DownloadsDir)!);
        var needed = p.Vendor == GpuVendor.Nvidia ? 4L << 30 : 1L << 30; // NVIDIA unpacks ~2 GB more
        if (drive.AvailableFreeSpace < needed)
            throw new InvalidOperationException($"Мало места на диске {drive.Name}: нужно хотя бы {SystemProfile.FormatBytes(needed)}.");

        var part = target + ".part";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, p.Url);
            if (p.Referer is not null) req.Headers.Referrer = new Uri(p.Referer);
            using var resp = await Http.Value.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Сервер производителя ответил ошибкой {(int)resp.StatusCode}.");
            // A redirect must not leave the vendor's servers.
            if (resp.RequestMessage?.RequestUri is { } final && !IsAllowedDownload(final))
                throw new InvalidOperationException("Сервер перенаправил загрузку на постороннюю ссылку — загрузка отменена.");
            if (resp.Content.Headers.ContentType?.MediaType is { } media && media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Вместо установщика сервер вернул веб-страницу. Попробуйте позже или откройте официальный сайт.");

            var total = resp.Content.Headers.ContentLength;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var sw = Stopwatch.StartNew();
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (sw.ElapsedMilliseconds > 250)
                    {
                        progress?.Report(new DownloadProgress(done, total));
                        sw.Restart();
                    }
                }
                progress?.Report(new DownloadProgress(done, total));
                if (total is { } t && done != t) throw new InvalidOperationException("Загрузка оборвалась — файл неполный.");
            }

            File.Move(part, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }

        var (valid, signer) = await VerifySignatureAsync(target, p.Signer).ConfigureAwait(false);
        if (!valid)
        {
            try { File.Delete(target); } catch { }
            throw new InvalidOperationException($"Цифровая подпись файла не подтверждена ({signer}). Файл удалён — устанавливать его небезопасно.");
        }
        Log.Success($"Драйвер скачан и подпись проверена: {p.FileName} ({signer}).");
        CleanupOld(p);
        return target;
    }

    /// <summary>
    /// Authenticode check through Windows (Get-AuthenticodeSignature): status must be Valid and the signer
    /// certificate must belong to the vendor.
    /// </summary>
    public static async Task<(bool Ok, string Signer)> VerifySignatureAsync(string path, string expectedSigner)
    {
        var escaped = path.Replace("'", "''");
        var script = $"$s = Get-AuthenticodeSignature -LiteralPath '{escaped}'; " +
                     "[pscustomobject]@{ Status = \"$($s.Status)\"; Subject = \"$($s.SignerCertificate.Subject)\" } | ConvertTo-Json -Compress";
        var r = await ProcessRunner.RunPowerShellAsync(script, new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromMinutes(2) }).ConfigureAwait(false);
        try
        {
            var json = r.StdOut.Trim();
            var start = json.IndexOf('{');
            if (start < 0) return (false, "нет данных о подписи");
            using var doc = JsonDocument.Parse(json[start..]);
            var status = doc.RootElement.GetProperty("Status").GetString() ?? "";
            var subject = doc.RootElement.GetProperty("Subject").GetString() ?? "";
            var ok = status.Equals("Valid", StringComparison.OrdinalIgnoreCase) &&
                     subject.Contains(expectedSigner, StringComparison.OrdinalIgnoreCase);
            var org = Regex.Match(subject, @"O=""?([^,""]+)").Groups[1].Value;
            return (ok, ok ? $"подписан: {org}" : $"статус {status}, подписант «{(org.Length > 0 ? org : subject)}»");
        }
        catch
        {
            return (false, "не удалось прочитать подпись");
        }
    }

    /// <summary>Starts the vendor's installer interactively (MONOLITH already runs elevated).</summary>
    public static void LaunchInstaller(string path)
    {
        SafetyLock.ThrowIfLocked("запуск установщика драйвера");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
        Log.Info($"Запущен официальный установщик: {Path.GetFileName(path)}");
    }

    /// <summary>Keeps only the newest installer per vendor to save disk space.</summary>
    private static void CleanupOld(DriverPackage keep)
    {
        try
        {
            var prefix = keep.Vendor == GpuVendor.Nvidia ? "*-dch-*.exe" : "amd-software-*.exe";
            foreach (var f in Directory.EnumerateFiles(DownloadsDir, prefix))
            {
                if (!string.Equals(Path.GetFileName(f), keep.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    public static string FormatProgress(DownloadProgress p) =>
        p.Total is > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{p.Percent:0}% · {SystemProfile.FormatBytes(p.Done)} из {SystemProfile.FormatBytes(p.Total.Value)}")
            : SystemProfile.FormatBytes(p.Done);
}
