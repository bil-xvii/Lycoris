using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
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
        _ => "производителя",
    };

    /// <summary>Size for the UI ("989 МБ"), or null when the server has not told it yet.</summary>
    public string? SizeDisplay => SizeBytes is > 0 ? SystemProfile.FormatBytes(SizeBytes.Value) : DriverDownloads.LocalizeSize(SizeText);
}

internal readonly record struct DownloadProgress(long Done, long? Total)
{
    public double Percent => Total is > 0 ? Math.Clamp(Done * 100d / Total.Value, 0, 100) : 0;

    /// <summary>The file is complete and its digital signature is being checked.</summary>
    public bool Verifying { get; init; }
}

/// <summary>
/// Downloads official GPU driver installers straight from the vendor's servers:
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

    /// <summary>Legal-form suffixes a vendor certificate may carry after the company name ("…, Inc.").</summary>
    private static readonly string[] LegalSuffixes = ["inc", "incorporated", "corp", "corporation", "ltd", "llc"];

    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private const string AmdDriversPage = "https://www.amd.com/en/support/download/drivers.html";
    private const string NvidiaSigner = "NVIDIA Corporation";
    private const string AmdSigner = "Advanced Micro Devices";

    /// <summary>A download that receives nothing for this long is treated as broken.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return c;
    });

    public static string DownloadsDir => Path.Combine(AppPaths.MachineDir, "Downloads");

    public static bool IsAllowedDownload(Uri? url) =>
        url is { IsAbsoluteUri: true } &&
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) &&
        AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase) &&
        url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Safe local file name taken from the URL (no path parts, only [A-Za-z0-9._-], starts with a letter or digit).</summary>
    public static string? SafeFileName(Uri url)
    {
        if (!url.IsAbsoluteUri) return null;
        var name = Path.GetFileName(url.AbsolutePath);
        if (string.IsNullOrEmpty(name) || !SafeNameRegex().IsMatch(name)) return null;
        return name;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{4,159}\.exe$")]
    private static partial Regex SafeNameRegex();

    /// <summary>"1.04 GB" (NVIDIA API) → "1.04 ГБ"; unknown formats are dropped rather than shown half-translated.</summary>
    public static string? LocalizeSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SizeTextRegex().Match(text.Trim());
        if (!m.Success) return null;
        var unit = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => "КБ",
            "MB" => "МБ",
            "GB" => "ГБ",
            _ => null,
        };
        return unit is null ? null : $"{m.Groups[1].Value.Replace('.', ',')} {unit}";
    }

    [GeneratedRegex(@"^(\d{1,4}(?:[.,]\d{1,2})?)\s*(KB|MB|GB)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeTextRegex();

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

    /// <summary>
    /// Asks the vendor's server for the exact file size (HEAD, nothing is downloaded). Null when the server does not
    /// say or the answer comes from a host outside the allow-list.
    /// </summary>
    public static async Task<long?> GetRemoteSizeAsync(DriverPackage p, CancellationToken ct)
    {
        if (!IsAllowedDownload(p.Url)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, p.Url);
            if (p.Referer is not null) req.Headers.Referrer = new Uri(p.Referer);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await Http.Value.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.RequestMessage?.RequestUri is { } final && !IsAllowedDownload(final)) return null;
            return resp.Content.Headers.ContentLength is long len && len > 0 ? len : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Info($"{p.VendorName}: размер установщика неизвестен ({ex.GetType().Name}).");
            return null;
        }
    }

    // ───────────────────────────── Download ─────────────────────────────

    /// <summary>
    /// Downloads the package (or reuses an already verified copy). Returns the local path of a file whose
    /// vendor signature has been verified. Throws with a Russian message on any problem; a partial or
    /// unverified file never stays on disk.
    /// </summary>
    public static async Task<string> DownloadAsync(DriverPackage p, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!IsAllowedDownload(p.Url) || !string.Equals(SafeFileName(p.Url), p.FileName, StringComparison.Ordinal))
            throw new InvalidOperationException("Ссылка ведёт не на официальный сервер производителя — загрузка отменена.");
        SafetyLock.ThrowIfLocked("загрузка драйвера");
        Elevation.Demand("загрузка драйвера");
        var dir = PrepareDownloadsDir();
        var target = Path.Combine(dir, p.FileName);

        // Already downloaded and still validly signed → reuse.
        if (File.Exists(target))
        {
            var length = new FileInfo(target).Length;
            progress?.Report(new DownloadProgress(length, length) { Verifying = true });
            var (ok, _) = await VerifySignatureAsync(target, p.Signer).ConfigureAwait(false);
            if (ok)
            {
                Log.Info($"Установщик уже скачан и подпись подтверждена: {p.FileName}");
                return target;
            }
            TryDelete(target);
        }

        var drive = new DriveInfo(Path.GetPathRoot(dir)!);
        var needed = RequiredFreeSpace(p.Vendor);
        if (drive.AvailableFreeSpace < needed)
            throw new InvalidOperationException($"Мало места на диске {drive.Name}: нужно хотя бы {SystemProfile.FormatBytes(needed)}.");

        var part = target + ".part";
        try
        {
            await DownloadToFileAsync(p, part, progress, ct).ConfigureAwait(false);
            File.Move(part, target, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        var total = new FileInfo(target).Length;
        progress?.Report(new DownloadProgress(total, total) { Verifying = true });
        var (valid, signer) = await VerifySignatureAsync(target, p.Signer).ConfigureAwait(false);
        if (!valid)
        {
            TryDelete(target);
            throw new InvalidOperationException($"Цифровая подпись файла не подтверждена ({signer}). Файл удалён — устанавливать его небезопасно.");
        }
        Log.Success($"Драйвер скачан и подпись проверена: {p.FileName} ({signer}).");
        CleanupOld(p);
        return target;
    }

    /// <summary>Free space needed on the system drive: NVIDIA unpacks the ~1 GB package into ~2 GB more.</summary>
    public static long RequiredFreeSpace(GpuVendor vendor) => vendor == GpuVendor.Nvidia ? 4L << 30 : 1L << 30;

    /// <summary>Creates the admin-only downloads folder (inside the locked machine folder) and refuses links.</summary>
    private static string PrepareDownloadsDir()
    {
        var dir = DownloadsDir;
        if (!AppPaths.TryEnsureDir(dir))
            throw new InvalidOperationException("Не удалось создать папку загрузок MONOLITH — нужны права администратора.");
        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Папка загрузок MONOLITH подменена ссылкой — загрузка отменена.");
        return dir;
    }

    private static async Task DownloadToFileAsync(DriverPackage p, string part, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // Stall watchdog: re-armed after every chunk, so only a connection that stops delivering data is aborted.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, p.Url);
            if (p.Referer is not null) req.Headers.Referrer = new Uri(p.Referer);
            using var resp = await Http.Value.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Сервер {p.Url.Host} ответил ошибкой {(int)resp.StatusCode}. Попробуйте позже или откройте официальный сайт.");
            // A redirect must not leave the vendor's servers.
            if (resp.RequestMessage?.RequestUri is { } final && !IsAllowedDownload(final))
                throw new InvalidOperationException("Сервер перенаправил загрузку на постороннюю ссылку — загрузка отменена.");
            if (resp.Content.Headers.ContentType?.MediaType is { } media && media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Вместо установщика сервер вернул веб-страницу. Попробуйте позже или откройте официальный сайт.");

            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            var sw = Stopwatch.StartNew();
            int read;
            while ((read = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                done += read;
                stall.CancelAfter(StallTimeout);
                if (sw.ElapsedMilliseconds > 250)
                {
                    progress?.Report(new DownloadProgress(done, total));
                    sw.Restart();
                }
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(done, total));
            if (total is { } t && done != t) throw new InvalidOperationException("Загрузка оборвалась — файл неполный. Повторите попытку.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Сервер больше минуты не передаёт данные — загрузка прервана. Проверьте подключение к интернету и повторите.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Не удалось скачать файл с {p.Url.Host}: {ex.Message}", ex);
        }
        catch (IOException ex) when (ex is HttpIOException || ex.InnerException is System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException($"Соединение с {p.Url.Host} оборвалось — загрузка прервана. Повторите попытку.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Не удалось записать файл на диск: {ex.Message}", ex);
        }
    }

    // ───────────────────────────── Signature ─────────────────────────────

    /// <summary>
    /// Authenticode check through Windows (Get-AuthenticodeSignature): status must be Valid and the signer
    /// certificate's CN or O must be exactly the vendor (legal-form suffixes such as ", Inc." allowed).
    /// </summary>
    public static async Task<(bool Ok, string Signer)> VerifySignatureAsync(string path, string expectedSigner)
    {
        var escaped = path.Replace("'", "''");
        var script = $"$s = Get-AuthenticodeSignature -LiteralPath '{escaped}'; $c = $s.SignerCertificate; " +
                     "[pscustomobject]@{ Status = \"$($s.Status)\"; " +
                     "Subject = $(if ($c) { $c.Subject } else { '' }); " +
                     "Names = $(if ($c) { $c.SubjectName.Format($true) } else { '' }) } | ConvertTo-Json -Compress";
        var r = await ProcessRunner.RunPowerShellAsync(script, new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromMinutes(2) }).ConfigureAwait(false);
        return EvaluateSignature(r.StdOut, expectedSigner);
    }

    /// <summary>Interprets the JSON written by <see cref="VerifySignatureAsync"/>. Pure — unit-tested.</summary>
    internal static (bool Ok, string Signer) EvaluateSignature(string? output, string expectedSigner)
    {
        try
        {
            var json = (output ?? "").Trim();
            var start = json.IndexOf('{');
            if (start < 0) return (false, "нет данных о подписи");
            using var doc = JsonDocument.Parse(json[start..]);
            var root = doc.RootElement;
            var status = root.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "";
            var subject = root.TryGetProperty("Subject", out var su) ? su.GetString() ?? "" : "";
            var names = root.TryGetProperty("Names", out var na) ? na.GetString() ?? "" : "";

            var fields = ParseNameLines(names);
            var org = fields.TryGetValue("O", out var o) ? o : fields.TryGetValue("CN", out var cn) ? cn : "";
            var display = org.Length > 0 ? org : subject.Length > 0 ? subject : "без подписи";
            if (!status.Equals("Valid", StringComparison.OrdinalIgnoreCase))
                return (false, $"статус «{(status.Length > 0 ? status : "нет")}», подписант «{display}»");

            var matches = (fields.TryGetValue("O", out var org2) && SignerMatches(org2, expectedSigner)) ||
                          (fields.TryGetValue("CN", out var cn2) && SignerMatches(cn2, expectedSigner));
            return matches ? (true, $"подписан: {display}") : (false, $"подписант «{display}», а ожидался «{expectedSigner}»");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (false, "не удалось прочитать подпись");
        }
    }

    /// <summary>"O=NVIDIA Corporation" lines of X500DistinguishedName.Format(true) → { O: "NVIDIA Corporation" } (first value wins).</summary>
    internal static Dictionary<string, string> ParseNameLines(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Replace("\"\"", "\"");
            result.TryAdd(key, value);
        }
        return result;
    }

    /// <summary>
    /// Exact company-name match, ignoring case, punctuation and a trailing legal form:
    /// "Advanced Micro Devices, Inc." matches "Advanced Micro Devices"; "Not NVIDIA Corporation Fans" does not match "NVIDIA Corporation".
    /// </summary>
    internal static bool SignerMatches(string? actual, string expected)
    {
        var a = NormalizeOrg(actual);
        var e = NormalizeOrg(expected);
        if (a.Length == 0 || e.Length == 0) return false;
        // Only the certificate may add a legal form; a shorter name ("NVIDIA" for "NVIDIA Corporation") never matches.
        return a == e || LegalSuffixes.Any(suffix => a == e + " " + suffix);
    }

    private static string NormalizeOrg(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // ───────────────────────────── Install ─────────────────────────────

    /// <summary>
    /// Starts the vendor's installer interactively. Only files inside MONOLITH's admin-only downloads folder are
    /// accepted (that is where <see cref="DownloadAsync"/> verified them). MONOLITH is elevated, so is the installer —
    /// it installs a driver and would ask for administrator rights anyway.
    /// </summary>
    public static void LaunchInstaller(string path)
    {
        SafetyLock.ThrowIfLocked("запуск установщика драйвера");
        Elevation.Demand("запуск установщика драйвера");
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(DownloadsDir), StringComparison.OrdinalIgnoreCase) ||
            !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("Установщик не найден в папке загрузок MONOLITH.");
        using var _ = Process.Start(new ProcessStartInfo(full) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(full)! });
        Log.Info($"Запущен официальный установщик: {Path.GetFileName(full)}");
    }

    /// <summary>Keeps only the newest installer per vendor (and no leftover partial files) to save disk space.</summary>
    private static void CleanupOld(DriverPackage keep)
    {
        try
        {
            var pattern = keep.Vendor == GpuVendor.Nvidia ? "*-dch-*.exe" : "amd-software-*.exe";
            foreach (var f in Directory.EnumerateFiles(DownloadsDir, pattern).Concat(Directory.EnumerateFiles(DownloadsDir, "*.part")))
            {
                if (!string.Equals(Path.GetFileName(f), keep.FileName, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Старые установщики не удалены: {ex.Message}");
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) { Log.Warn($"Не удалось удалить {Path.GetFileName(file)}: {ex.Message}"); }
    }

    public static string FormatProgress(DownloadProgress p) =>
        p.Verifying
            ? "Проверка цифровой подписи…"
            : p.Total is > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{p.Percent:0}% · {SystemProfile.FormatBytes(p.Done)} из {SystemProfile.FormatBytes(p.Total.Value)}")
                : SystemProfile.FormatBytes(p.Done);
}
