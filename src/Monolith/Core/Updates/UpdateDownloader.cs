using System.IO;
using System.Net;
using System.Net.Http;

namespace Monolith.Core.Updates;

/// <summary>A downloaded update that passed every check (size, host, SHA-256, PE header, signer).</summary>
public sealed record StagedUpdate(string Path, string Sha256, long Size, ReleaseInfo Release);

/// <summary>
/// Downloads Lycoris.exe of a release into a folder and verifies it (CLAUDE.md rule 9 spirit, extended for the app itself):
/// HTTPS only, every redirect hop's host on the allow-list BEFORE it is requested, size window, SHA-256 against the
/// .sha256 asset and GitHub's digest, MZ/PE x64 header, Authenticode signer compared with the running exe. A file that
/// fails any check is deleted. Streams through <see cref="VendorDownloads.CopyResponseToFileAsync"/>.
/// </summary>
public sealed class UpdateDownloader
{
    private const int MaxHops = 5;
    private readonly HttpClient _http;
    private readonly UpdatePolicy _policy;
    private readonly Func<string, Task<SignatureInfo>> _inspect;
    private readonly string _currentExe;

    public UpdateDownloader(HttpClient http, UpdatePolicy policy, Func<string, Task<SignatureInfo>> inspectSignature, string currentExe)
    {
        _http = http;
        _policy = policy;
        _inspect = inspectSignature;
        _currentExe = currentExe;
    }

    public static UpdateDownloader CreateDefault() =>
        new(UpdateHttp.CreateDownloadClient(), new UpdatePolicy(), VendorDownloads.InspectSignatureAsync, AppPaths.ExecutablePath);

    /// <summary>Folder for staged updates: %LocalAppData%\MONOLITH\Updates.</summary>
    public static string DefaultFolder => System.IO.Path.Combine(AppPaths.UserDir, "Updates");

    public string FileNameFor(ReleaseInfo r) => "Lycoris-" + r.VersionText + ".exe";

    public async Task<StagedUpdate> DownloadAsync(ReleaseInfo release, string folder, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!UpdateVerifier.IsAssetUrl(release.ExeUrl, _policy, ReleaseParser.ExeAssetName))
            throw new UpdateRefusedException(Loc.T("Ссылка на файл обновления ведёт не на официальную страницу релизов — обновление отменено."));
        if (release.ShaUrl.Length > 0 && !UpdateVerifier.IsAssetUrl(release.ShaUrl, _policy, ReleaseParser.ShaAssetName))
            throw new UpdateRefusedException(Loc.T("Ссылка на контрольную сумму ведёт не на официальную страницу релизов — обновление отменено."));
        if (release.ExeSize > 0) UpdateVerifier.CheckSize(release.ExeSize, _policy);

        Directory.CreateDirectory(folder);
        if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UpdateRefusedException(Loc.T("Папка загрузки обновлений подменена ссылкой — обновление отменено."));
        var drive = SafeDrive(folder);
        var need = Math.Max(release.ExeSize, _policy.MinSize) * 2;
        if (drive is not null && drive.AvailableFreeSpace < need)
            throw new UpdateRefusedException(Loc.Tf("Мало места на диске {0}: нужно хотя бы {1}.", drive.Name, SystemProfile.FormatBytes(need)));

        // 1. The expected hash: the small .sha256 asset and GitHub's digest.
        string? fromFile = null;
        if (release.ShaUrl.Length > 0)
        {
            try
            {
                fromFile = UpdateVerifier.ParseShaFile(await GetSmallTextAsync(new Uri(release.ShaUrl), ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new UpdateRefusedException(Loc.T("Не удалось получить контрольную сумму релиза — обновление отменено."));
            }
            if (fromFile is null)
                throw new UpdateRefusedException(Loc.T("Файл контрольной суммы релиза повреждён — обновление отменено."));
        }
        var expected = UpdateVerifier.ExpectedHash(fromFile, release.ExeDigest);

        // 2. The file itself.
        var final = System.IO.Path.Combine(folder, FileNameFor(release));
        var part = final + ".part";
        VendorDownloads.TryDelete(part);
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(VendorDownloads.StallLimit);
            try
            {
                using var resp = await SendFollowingAsync(new Uri(release.ExeUrl), stall.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new UpdateRefusedException(Loc.Tf("Сервер {0} ответил ошибкой {1}. Попробуйте позже.", resp.RequestMessage?.RequestUri?.Host ?? "github.com", (int)resp.StatusCode));
                if (resp.Content.Headers.ContentLength is { } len) UpdateVerifier.CheckSize(len, _policy);
                await VendorDownloads.CopyResponseToFileAsync(resp, resp.RequestMessage?.RequestUri?.Host ?? "github.com", part, progress, stall, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpdateRefusedException(Loc.T("Сервер больше минуты не передаёт данные — загрузка прервана. Проверьте подключение к интернету и повторите."));
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateRefusedException(Loc.Tf("Не удалось скачать обновление: {0}", ex.Message));
            }

            var size = new FileInfo(part).Length;
            UpdateVerifier.CheckSize(size, _policy);
            if (release.ExeSize > 0 && size != release.ExeSize)
                throw new UpdateRefusedException(Loc.T("Размер скачанного файла не совпал с объявленным — файл удалён, обновление отменено."));

            progress?.Report(new DownloadProgress(size, size) { Verifying = true });
            var actual = await UpdateVerifier.Sha256Async(part, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateRefusedException(Loc.T("Контрольная сумма скачанного файла не совпала — файл удалён, обновление отменено."));
            if (!UpdateVerifier.FileIsPe64(part))
                throw new UpdateRefusedException(Loc.T("Скачанный файл не является программой Windows x64 — файл удалён, обновление отменено."));

            var currentSig = await _inspect(_currentExe).ConfigureAwait(false);
            var newSig = await _inspect(part).ConfigureAwait(false);
            if (UpdateVerifier.CheckSigners(currentSig, newSig) is { } problem)
                throw new UpdateRefusedException(problem);

            File.Move(part, final, overwrite: true);
            Log.Success("Обновление {0} скачано и проверено (SHA-256 совпал, {1}).", release.VersionText, SystemProfile.FormatBytes(size));
            return new StagedUpdate(final, expected, size, release);
        }
        catch
        {
            VendorDownloads.TryDelete(part);
            throw;
        }
    }

    /// <summary>GET with manual redirects: the host of every hop is checked BEFORE the request, at most 5 hops.</summary>
    private async Task<HttpResponseMessage> SendFollowingAsync(Uri url, CancellationToken ct)
    {
        for (var hop = 0; hop <= MaxHops; hop++)
        {
            if (!UpdateVerifier.IsAllowedHop(url, _policy))
                throw new UpdateRefusedException(Loc.T("Сервер перенаправил загрузку на постороннюю ссылку — обновление отменено."));
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = resp.Headers.Location;
                resp.Dispose();
                if (next is null) throw new UpdateRefusedException(Loc.T("Сервер вернул некорректное перенаправление — обновление отменено."));
                url = next.IsAbsoluteUri ? next : new Uri(url, next);
                continue;
            }
            // Report the final URL even if a handler left RequestMessage empty.
            resp.RequestMessage ??= req;
            return resp;
        }
        throw new UpdateRefusedException(Loc.T("Слишком много перенаправлений — обновление отменено."));
    }

    private async Task<string> GetSmallTextAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await SendFollowingAsync(url, timeout.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)resp.StatusCode);
        if (resp.Content.Headers.ContentLength is > 4096) throw new InvalidOperationException("too large");
        var bytes = await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (bytes.Length > 4096) throw new InvalidOperationException("too large");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static DriveInfo? SafeDrive(string folder)
    {
        try { return new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))!); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return null; }
    }
}
