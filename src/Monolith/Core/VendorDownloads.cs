using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monolith.Core;

/// <summary>Authenticode facts of one file: Windows status text ("Valid", "NotSigned", …), signer organization, certificate thumbprint.</summary>
public sealed record SignatureInfo(string Status, string Org, string Thumbprint)
{
    public bool HasSigner => Org.Length > 0 || Thumbprint.Length > 0;
    public bool IsValid => Status.Equals("Valid", StringComparison.OrdinalIgnoreCase);
}

public readonly record struct DownloadProgress(long Done, long? Total)
{
    public double Percent => Total is > 0 ? Math.Clamp(Done * 100d / Total.Value, 0, 100) : 0;

    /// <summary>The file is complete and its digital signature is being checked.</summary>
    public bool Verifying { get; init; }
}

/// <summary>One official installer to fetch straight from its vendor's server.</summary>
/// <param name="Url">HTTPS link on one of <paramref name="AllowedHosts"/>, path ending in .exe.</param>
/// <param name="FileName">Local file name; must equal <see cref="VendorDownloads.SafeFileName"/> of the URL.</param>
/// <param name="AllowedHosts">Exact host names the download (and every redirect's final answer) may come from.</param>
/// <param name="Signers">Organizations one of which must have signed the file (Authenticode CN/O, legal form ignored).</param>
/// <param name="Folder"><see cref="VendorDownloads.RootDir"/> or a direct sub-folder of it (admin-only).</param>
public sealed record VendorDownloadRequest(
    Uri Url, string FileName, IReadOnlyList<string> AllowedHosts, IReadOnlyList<string> Signers, string Folder)
{
    /// <summary>Some servers (AMD) serve the installer only with a Referer from their own site.</summary>
    public string? Referer { get; init; }

    /// <summary>Free space the download (and what the installer unpacks) needs on the system drive.</summary>
    public long RequiredFreeSpace { get; init; } = 1L << 30;

    /// <summary>What the file is, for the log ("драйвер NVIDIA 621.01", "Logitech G HUB").</summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// Reuse an already downloaded, validly signed copy. Right for versioned file names (drivers); wrong for app links
    /// that always have the same name ("lghub_installer.exe") while the vendor replaces the file behind them.
    /// </summary>
    public bool ReuseVerifiedCopy { get; init; } = true;
}

/// <summary>
/// The one mechanism for downloading official installers (CLAUDE.md rule 9):
///  • HTTPS only, the host must be on the request's allow-list — also the final host after redirects;
///  • the file must carry a VALID Authenticode signature from an expected vendor, otherwise it is deleted;
///  • files live in %ProgramData%\MONOLITH\Downloads (admin-only write: nobody can swap them after the check);
///    a process without admin rights, or a folder that could not be locked down, is refused.
/// Used by the Drivers page (DriverDownloads) and by the Apps page (installer from the vendor when the winget
/// manifest is outdated).
/// </summary>
public static partial class VendorDownloads
{
    /// <summary>Legal-form suffixes a vendor certificate may carry after the company name ("…, Inc.").</summary>
    private static readonly string[] LegalSuffixes = ["inc", "incorporated", "corp", "corporation", "ltd", "llc"];

    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    /// <summary>A download that receives nothing for this long is treated as broken.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>No vendor installer is larger (the biggest GPU driver packages are ~1 GB); a stream that never ends cannot fill the disk.</summary>
    internal const long MaxDownloadBytes = 6L << 30;

    private static readonly Lazy<HttpClient> LazyHttp = new(() =>
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

    /// <summary>Shared client (browser user agent, no automatic decompression, at most 5 redirects).</summary>
    internal static HttpClient Http => LazyHttp.Value;

    /// <summary>%ProgramData%\MONOLITH\Downloads — driver installers directly inside, app installers in "Apps".</summary>
    public static string RootDir => Path.Combine(AppPaths.MachineDir, "Downloads");

    /// <summary>Official app installers fetched when the winget manifest is outdated.</summary>
    public static string AppsDir => Path.Combine(RootDir, "Apps");

    // ───────────────────────────── Links and names ─────────────────────────────

    /// <summary>HTTPS, default port, no credentials, host exactly on the list, path ending in .exe.</summary>
    public static bool IsAllowedUrl(Uri? url, IEnumerable<string> allowedHosts) =>
        url is { IsAbsoluteUri: true } &&
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) &&
        allowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase) &&
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

    /// <summary>"1.04 GB" (vendor APIs) → "1,04 ГБ"; unknown formats are dropped rather than shown half-translated.</summary>
    public static string? LocalizeSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SizeTextRegex().Match(text.Trim());
        if (!m.Success) return null;
        var unit = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => Loc.T("КБ"),
            "MB" => Loc.T("МБ"),
            "GB" => Loc.T("ГБ"),
            _ => null,
        };
        return unit is null ? null : $"{m.Groups[1].Value.Replace('.', ',')} {unit}";
    }

    [GeneratedRegex(@"^(\d{1,4}(?:[.,]\d{1,2})?)\s*(KB|MB|GB)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeTextRegex();

    /// <summary>True when <paramref name="folder"/> is the downloads root or a direct sub-folder of it.</summary>
    public static bool IsDownloadFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDir));
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
            var parent = Path.GetDirectoryName(full);
            return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True when the file lies directly inside <paramref name="folder"/> (no sub-folders, no "..\" tricks).</summary>
    public static bool IsDirectlyInFolder(string? path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return string.Equals(Path.GetDirectoryName(full) is { } d ? Path.TrimEndingDirectorySeparator(d) : null, dir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Rejects a request whose link, name or folder is not exactly what this mechanism allows. Pure.</summary>
    public static string? Validate(VendorDownloadRequest r)
    {
        if (!IsAllowedUrl(r.Url, r.AllowedHosts) || !string.Equals(SafeFileName(r.Url), r.FileName, StringComparison.Ordinal))
            return Loc.T("Ссылка ведёт не на официальный сервер производителя — загрузка отменена.");
        if (r.Signers.Count == 0 || r.Signers.Any(string.IsNullOrWhiteSpace))
            return Loc.T("Для этого файла не задан ожидаемый подписант — загрузка отменена.");
        if (!IsDownloadFolder(r.Folder))
            return Loc.T("Файл можно сохранить только в защищённую папку загрузок MONOLITH — загрузка отменена.");
        return null;
    }

    // ───────────────────────────── Size ─────────────────────────────

    /// <summary>
    /// Asks the vendor's server for the exact file size (HEAD, nothing is downloaded). Null when the server does not
    /// say or the answer comes from a host outside the allow-list.
    /// </summary>
    public static async Task<long?> GetRemoteSizeAsync(VendorDownloadRequest r, CancellationToken ct)
    {
        if (!IsAllowedUrl(r.Url, r.AllowedHosts)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, r.Url);
            if (r.Referer is not null) req.Headers.Referrer = new Uri(r.Referer);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.RequestMessage?.RequestUri is { } final && !IsAllowedUrl(final, r.AllowedHosts)) return null;
            return resp.Content.Headers.ContentLength is long len && len > 0 ? len : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Info("{0}: размер установщика неизвестен ({1}).", r.Label, ex.GetType().Name);
            return null;
        }
    }

    // ───────────────────────────── Download ─────────────────────────────

    /// <summary>
    /// Downloads the file (or reuses an already verified copy). Returns the local path of a file whose vendor signature
    /// has been verified. Throws with a Russian message on any problem; a partial or unverified file never stays on disk.
    /// </summary>
    public static async Task<string> DownloadAsync(VendorDownloadRequest r, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (Validate(r) is { } problem) throw new InvalidOperationException(problem);
        SafetyLock.ThrowIfLocked(Loc.T("загрузка установщика"));
        Elevation.Demand(Loc.T("загрузка установщика"));
        var dir = PrepareFolder(r.Folder);
        var target = Path.Combine(dir, r.FileName);

        // Unversioned links: always fetch the vendor's current file, never an old copy with the same name.
        if (!r.ReuseVerifiedCopy && File.Exists(target)) TryDelete(target);

        // Already downloaded and still validly signed → reuse.
        if (File.Exists(target))
        {
            var length = new FileInfo(target).Length;
            progress?.Report(new DownloadProgress(length, length) { Verifying = true });
            var (ok, _) = await VerifySignatureAsync(target, r.Signers).ConfigureAwait(false);
            if (ok)
            {
                Log.Info("Установщик уже скачан и подпись подтверждена: {0}", r.FileName);
                return target;
            }
            TryDelete(target);
        }

        var drive = new DriveInfo(Path.GetPathRoot(dir)!);
        if (drive.AvailableFreeSpace < r.RequiredFreeSpace)
            throw new InvalidOperationException(Loc.Tf("Мало места на диске {0}: нужно хотя бы {1}.", drive.Name, SystemProfile.FormatBytes(r.RequiredFreeSpace)));

        var part = target + ".part";
        try
        {
            await DownloadToFileAsync(r, part, progress, ct).ConfigureAwait(false);
            File.Move(part, target, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        var total = new FileInfo(target).Length;
        progress?.Report(new DownloadProgress(total, total) { Verifying = true });
        var (valid, signer) = await VerifySignatureAsync(target, r.Signers).ConfigureAwait(false);
        if (!valid)
        {
            TryDelete(target);
            throw new InvalidOperationException(Loc.Tf("Цифровая подпись файла не подтверждена ({0}). Файл удалён — устанавливать его небезопасно.", signer));
        }
        Log.Success("Установщик скачан и подпись проверена: {0} ({1}).", r.FileName, signer);
        return target;
    }

    /// <summary>Creates the admin-only downloads folder (inside the locked machine folder) and refuses links.</summary>
    private static string PrepareFolder(string folder)
    {
        if (!AppPaths.TryEnsureDir(RootDir) || !AppPaths.TryEnsureDir(folder))
            throw new InvalidOperationException(Loc.T("Не удалось создать защищённую папку загрузок MONOLITH (нужны права администратора) — загрузка отменена."));
        foreach (var d in new[] { RootDir, folder })
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException(Loc.T("Папка загрузок MONOLITH подменена ссылкой — загрузка отменена."));
        return folder;
    }

    private static async Task DownloadToFileAsync(VendorDownloadRequest r, string part, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // Stall watchdog: re-armed after every chunk, so only a connection that stops delivering data is aborted.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, r.Url);
            if (r.Referer is not null) req.Headers.Referrer = new Uri(r.Referer);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(Loc.Tf("Сервер {0} ответил ошибкой {1}. Попробуйте позже или откройте официальный сайт.", r.Url.Host, (int)resp.StatusCode));
            // A redirect must not leave the vendor's servers.
            if (resp.RequestMessage?.RequestUri is { } final && !IsAllowedUrl(final, r.AllowedHosts))
                throw new InvalidOperationException(Loc.T("Сервер перенаправил загрузку на постороннюю ссылку — загрузка отменена."));
            await CopyResponseToFileAsync(resp, r.Url.Host, part, progress, stall, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(Loc.T("Сервер больше минуты не передаёт данные — загрузка прервана. Проверьте подключение к интернету и повторите."));
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(Loc.Tf("Не удалось скачать файл с {0}: {1}", r.Url.Host, ex.Message), ex);
        }
    }

    /// <summary>
    /// Writes an HTTP answer to <paramref name="part"/> (FileShare.None) with progress and the stall watchdog; the caller has
    /// already checked status and host. Shared by the vendor installers and the MONOLITH self-update (Core/Updates).
    /// Throws InvalidOperationException with a Russian message; the stall token's cancellation passes through.
    /// </summary>
    internal static async Task CopyResponseToFileAsync(HttpResponseMessage resp, string host, string part, IProgress<DownloadProgress>? progress,
        CancellationTokenSource stall, CancellationToken ct)
    {
        try
        {
            if (resp.Content.Headers.ContentType?.MediaType is { } media && media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Loc.T("Вместо установщика сервер вернул веб-страницу. Попробуйте позже или откройте официальный сайт."));

            var total = resp.Content.Headers.ContentLength;
            if (total > MaxDownloadBytes) throw new InvalidOperationException(Loc.T("Сервер сообщает слишком большой размер файла — загрузка отменена."));
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
                if (done > MaxDownloadBytes) throw new InvalidOperationException(Loc.T("Файл больше допустимого размера — загрузка прервана."));
                stall.CancelAfter(StallTimeout);
                if (sw.ElapsedMilliseconds > 250)
                {
                    progress?.Report(new DownloadProgress(done, total));
                    sw.Restart();
                }
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(done, total));
            if (total is { } t && done != t) throw new InvalidOperationException(Loc.T("Загрузка оборвалась — файл неполный. Повторите попытку."));
        }
        catch (IOException ex) when (ex is HttpIOException || ex.InnerException is System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(Loc.Tf("Соединение с {0} оборвалось — загрузка прервана. Повторите попытку.", host), ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(Loc.Tf("Не удалось записать файл на диск: {0}", ex.Message), ex);
        }
    }

    /// <summary>A download that receives nothing for this long is treated as broken (the stall watchdog of <see cref="CopyResponseToFileAsync"/>).</summary>
    internal static TimeSpan StallLimit => StallTimeout;

    // ───────────────────────────── Signature ─────────────────────────────

    /// <summary>
    /// Authenticode check through Windows (Get-AuthenticodeSignature): status must be Valid and the signer
    /// certificate's CN or O must be exactly one of the expected vendors (legal-form suffixes such as ", Inc." allowed).
    /// </summary>
    public static async Task<(bool Ok, string Signer)> VerifySignatureAsync(string path, IReadOnlyList<string> expectedSigners)
    {
        var r = await ProcessRunner.RunPowerShellAsync(SignatureScript(path), new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromMinutes(2) }).ConfigureAwait(false);
        return EvaluateSignature(r.StdOut, expectedSigners);
    }

    /// <summary>The PowerShell that reads an Authenticode signature as one JSON line (Status, Subject, Thumbprint, Names).</summary>
    internal static string SignatureScript(string path)
    {
        return $"$s = Get-AuthenticodeSignature -LiteralPath {Security.PsText.Literal(path)}; $c = $s.SignerCertificate; " +
               "[pscustomobject]@{ Status = \"$($s.Status)\"; " +
               "Subject = $(if ($c) { $c.Subject } else { '' }); " +
               "Thumbprint = $(if ($c) { $c.Thumbprint } else { '' }); " +
               "Names = $(if ($c) { $c.SubjectName.Format($true) } else { '' }) } | ConvertTo-Json -Compress";
    }

    /// <summary>Signature facts of a file whatever its status (the self-update compares the signers of two files).</summary>
    public static async Task<SignatureInfo> InspectSignatureAsync(string path)
    {
        var r = await ProcessRunner.RunPowerShellAsync(SignatureScript(path), new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromMinutes(2) }).ConfigureAwait(false);
        return ParseSignatureInfo(r.StdOut);
    }

    /// <summary>Interprets the JSON of <see cref="SignatureScript"/>; unreadable output = status "Unknown" without a signer. Pure.</summary>
    public static SignatureInfo ParseSignatureInfo(string? output)
    {
        try
        {
            var json = (output ?? "").Trim();
            var start = json.IndexOf('{');
            if (start < 0) return new SignatureInfo("Unknown", "", "");
            using var doc = JsonDocument.Parse(json[start..]);
            var root = doc.RootElement;
            string Get(string n) => root.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
            var fields = ParseNameLines(Get("Names"));
            var org = fields.TryGetValue("O", out var o) ? o : fields.TryGetValue("CN", out var cn) ? cn : "";
            return new SignatureInfo(Get("Status"), org, Get("Thumbprint"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new SignatureInfo("Unknown", "", "");
        }
    }

    /// <summary>Interprets the JSON written by <see cref="VerifySignatureAsync"/>. Pure — unit-tested.</summary>
    public static (bool Ok, string Signer) EvaluateSignature(string? output, IReadOnlyList<string> expectedSigners)
    {
        try
        {
            var json = (output ?? "").Trim();
            var start = json.IndexOf('{');
            if (start < 0) return (false, Loc.T("нет данных о подписи"));
            using var doc = JsonDocument.Parse(json[start..]);
            var root = doc.RootElement;
            var status = root.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "";
            var subject = root.TryGetProperty("Subject", out var su) ? su.GetString() ?? "" : "";
            var names = root.TryGetProperty("Names", out var na) ? na.GetString() ?? "" : "";

            var fields = ParseNameLines(names);
            var org = fields.TryGetValue("O", out var o) ? o : fields.TryGetValue("CN", out var cn) ? cn : "";
            var display = org.Length > 0 ? org : subject.Length > 0 ? subject : Loc.T("без подписи");
            if (!status.Equals("Valid", StringComparison.OrdinalIgnoreCase))
                return (false, Loc.Tf("статус «{0}», подписант «{1}»", status.Length > 0 ? status : Loc.T("отсутствует"), display));

            var matches = expectedSigners.Any(expected =>
                (fields.TryGetValue("O", out var org2) && SignerMatches(org2, expected)) ||
                (fields.TryGetValue("CN", out var cn2) && SignerMatches(cn2, expected)));
            return matches
                ? (true, Loc.Tf("подписан: {0}", display))
                : (false, Loc.Tf("подписант «{0}», а ожидался {1}", display, string.Join(Loc.T(" или "), expectedSigners.Select(s => "«" + s + "»"))));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (false, Loc.T("не удалось прочитать подпись"));
        }
    }

    /// <summary>"O=NVIDIA Corporation" lines of X500DistinguishedName.Format(true) → { O: "NVIDIA Corporation" } (first value wins).</summary>
    public static Dictionary<string, string> ParseNameLines(string text)
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
    public static bool SignerMatches(string? actual, string expected)
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

    // ───────────────────────────── Helpers ─────────────────────────────

    public static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) { Log.Warn("Не удалось удалить {0}: {1}", Path.GetFileName(file), ex.Message); }
    }

    public static string FormatProgress(DownloadProgress p) =>
        p.Verifying
            ? Loc.T("Проверка цифровой подписи…")
            : p.Total is > 0
                ? Loc.Tf("{0:0}% · {1} из {2}", p.Percent, SystemProfile.FormatBytes(p.Done), SystemProfile.FormatBytes(p.Total.Value))
                : SystemProfile.FormatBytes(p.Done);
}
