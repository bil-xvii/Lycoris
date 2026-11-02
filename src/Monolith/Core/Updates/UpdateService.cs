using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;

namespace Monolith.Core.Updates;

/// <summary>Where the update check keeps its small state (AppSettings in the app, a fake in tests).</summary>
public interface IUpdateStore
{
    bool AutoCheck { get; }
    DateTime? LastCheckUtc { get; set; }
    string ETag { get; set; }
    string DismissedVersion { get; set; }
    DateTime? SnoozedUntilUtc { get; set; }
    string CachedRelease { get; set; }
}

public sealed class SettingsUpdateStore : IUpdateStore
{
    private static AppSettings S => AppSettings.Current;

    public bool AutoCheck => S.AutoCheckUpdates;
    public DateTime? LastCheckUtc { get => ParseUtc(S.UpdateLastCheckUtc); set => S.UpdateLastCheckUtc = FormatUtc(value); }
    public string ETag { get => S.UpdateETag; set => S.UpdateETag = value; }
    public string DismissedVersion { get => S.UpdateDismissedVersion; set => S.UpdateDismissedVersion = value; }
    public DateTime? SnoozedUntilUtc { get => ParseUtc(S.UpdateSnoozedUntilUtc); set => S.UpdateSnoozedUntilUtc = FormatUtc(value); }
    public string CachedRelease { get => S.UpdateCachedRelease; set => S.UpdateCachedRelease = value; }

    internal static DateTime? ParseUtc(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    internal static string FormatUtc(DateTime? d) => d?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? "";
}

public enum UpdateCheckStatus
{
    /// <summary>A newer release exists (<see cref="UpdateCheckResult.Release"/>).</summary>
    Available,
    /// <summary>This build is the newest one.</summary>
    UpToDate,
    /// <summary>Automatic check off, or done less than 12 hours ago: nothing was requested.</summary>
    Skipped,
    /// <summary>Offline, rate-limited, bad answer: nothing changed, <see cref="UpdateCheckResult.Message"/> says why.</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release, string Message)
{
    /// <summary>True when the answer came from the network or from a 304 (not from the local cache).</summary>
    public bool Contacted { get; init; }
}

/// <summary>
/// "Is there a newer Lycoris?" — one anonymous GET of the public releases repository
/// (<see cref="LatestReleaseUrl"/>), at most once per 12 hours for background checks, with ETag. Never throws (except for the
/// caller's own cancellation) and never shows an error: a failure is a log line and a <see cref="UpdateCheckStatus.Failed"/> result.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "bil-xvii/Lycoris-releases";
    public static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    public static readonly TimeSpan SnoozeLength = TimeSpan.FromHours(24);
    private const int MaxAnswerBytes = 1 << 20;

    public static UpdateService Instance { get; } = new(UpdateHttp.CreateApiClient(), new SettingsUpdateStore(), () => AppVersion.Current, () => DateTime.UtcNow);

    private readonly HttpClient _http;
    private readonly IUpdateStore _store;
    private readonly Func<SemVer> _current;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private DateTime _backoffUntil = DateTime.MinValue;
    private ReleaseInfo? _available;

    public UpdateService(HttpClient http, IUpdateStore store, Func<SemVer> current, Func<DateTime> utcNow)
    {
        _http = http;
        _store = store;
        _current = current;
        _utcNow = utcNow;
        Reevaluate();
    }

    /// <summary>The newer release known right now (from the last check or the cache), or null.</summary>
    public ReleaseInfo? Available { get { lock (_gate) return _available; } }

    public event Action? Changed;

    /// <summary>Available and neither skipped nor snoozed: the title-bar button is shown.</summary>
    public bool ShouldAnnounce => Available is { } r && !IsDismissed(r) && !IsSnoozed();

    public bool IsDismissed(ReleaseInfo r) =>
        SemVer.TryParse(_store.DismissedVersion, out var d) && d == r.Version;

    public bool IsSnoozed() => _store.SnoozedUntilUtc is { } until && until > _utcNow();

    public void Dismiss(ReleaseInfo r)
    {
        _store.DismissedVersion = r.VersionText;
        Changed?.Invoke();
    }

    public void Snooze()
    {
        _store.SnoozedUntilUtc = _utcNow() + SnoozeLength;
        Changed?.Invoke();
    }

    /// <summary>A skipped or snoozed state is cleared when the user asks for the update himself.</summary>
    public void ClearReminders()
    {
        _store.DismissedVersion = "";
        _store.SnoozedUntilUtc = null;
        Changed?.Invoke();
    }

    /// <summary>Is a background request due (auto-check on, last success older than 12 h, no back-off)? Pure of network.</summary>
    public bool IsDue()
    {
        var now = _utcNow();
        if (!_store.AutoCheck || now < _backoffUntil) return false;
        return _store.LastCheckUtc is not { } last || now - last >= CheckInterval || last > now.AddMinutes(5);
    }

    /// <summary>Re-reads the cached release and recomputes <see cref="Available"/> (no network).</summary>
    public void Reevaluate()
    {
        var cached = ReleaseParser.Deserialize(_store.CachedRelease);
        SetAvailable(cached is not null && cached.Version > _current() && !cached.Version.IsPrerelease ? cached : null);
    }

    /// <summary>
    /// Background (<paramref name="manual"/> false) or button-driven check. A background check answers
    /// <see cref="UpdateCheckStatus.Skipped"/> without any request when it is switched off or not due.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (!manual && !IsDue())
        {
            Reevaluate();
            return new UpdateCheckResult(UpdateCheckStatus.Skipped, Available, "");
        }

        var now = _utcNow();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            var hasCache = ReleaseParser.Deserialize(_store.CachedRelease) is not null;
            if (hasCache && _store.ETag.Length > 0) req.Headers.TryAddWithoutValidation("If-None-Match", _store.ETag);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotModified && hasCache)
            {
                _store.LastCheckUtc = now;
                Reevaluate();
                return Finish(Contacted: true);
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // No release published yet.
                _store.LastCheckUtc = now;
                _store.CachedRelease = "";
                _store.ETag = "";
                SetAvailable(null);
                return Finish(Contacted: true);
            }
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var until = RateLimitReset(resp, now);
                _backoffUntil = until;
                Log.Warn("Проверка обновлений: лимит запросов GitHub исчерпан, повтор после {0:HH:mm}.", until.ToLocalTime());
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Available, Loc.T("Сервер обновлений временно ограничил число запросов. Повторите позже.")) { Contacted = true };
            }
            if (!resp.IsSuccessStatusCode)
            {
                _backoffUntil = now.AddMinutes(30);
                Log.Info("Проверка обновлений: сервер ответил {0}.", (int)resp.StatusCode);
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Available, Loc.Tf("Сервер обновлений ответил ошибкой {0}.", (int)resp.StatusCode)) { Contacted = true };
            }
            if (resp.Content.Headers.ContentLength is > MaxAnswerBytes)
                return Fail(now, "answer too large", Loc.T("Сервер обновлений вернул слишком большой ответ."));

            var body = await ReadLimitedAsync(resp.Content, timeout.Token).ConfigureAwait(false);
            if (body is null) return Fail(now, "answer too large", Loc.T("Сервер обновлений вернул слишком большой ответ."));

            var release = ReleaseParser.TryParse(body, out var problem);
            if (release is null)
            {
                // A pre-release/draft/odd tag is not an error: there is just nothing to offer.
                if (problem is "prerelease" or "draft")
                {
                    _store.LastCheckUtc = now;
                    SetAvailable(null);
                    return Finish(Contacted: true);
                }
                return Fail(now, problem ?? "unreadable answer", Loc.T("Не удалось прочитать ответ сервера обновлений."));
            }

            _store.CachedRelease = ReleaseParser.Serialize(release);
            _store.ETag = resp.Headers.ETag?.ToString() ?? "";
            _store.LastCheckUtc = now;
            Reevaluate();
            return Finish(Contacted: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            _backoffUntil = now.AddMinutes(15);
            Log.Info("Проверка обновлений не удалась ({0}).", ex.GetType().Name);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Available, Loc.T("Не удалось связаться с сервером обновлений. Проверьте подключение к интернету.")) { Contacted = true };
        }

        UpdateCheckResult Finish(bool Contacted) =>
            Available is { } r
                ? new UpdateCheckResult(UpdateCheckStatus.Available, r, "") { Contacted = Contacted }
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, "") { Contacted = Contacted };

        UpdateCheckResult Fail(DateTime at, string logReason, string message)
        {
            _backoffUntil = at.AddHours(1);
            Log.Warn("Проверка обновлений: {0}.", logReason);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Available, message) { Contacted = true };
        }
    }

    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxAnswerBytes) return null;
        }
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>X-RateLimit-Reset (unix seconds) when present and sensible, otherwise one hour from now.</summary>
    private static DateTime RateLimitReset(HttpResponseMessage resp, DateTime now)
    {
        if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            if (at > now && at < now.AddHours(2)) return at.AddSeconds(5);
        }
        return now.AddHours(1);
    }

    private void SetAvailable(ReleaseInfo? release)
    {
        bool changed;
        lock (_gate)
        {
            changed = _available?.Tag != release?.Tag;
            _available = release;
        }
        if (changed) Changed?.Invoke();
    }
}

/// <summary>The HTTP clients of the updater: no automatic redirects (every hop is checked), fixed User-Agent.</summary>
public static class UpdateHttp
{
    public static HttpClient CreateApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return Configure(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
    }

    public static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return Configure(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
    }

    private static HttpClient Configure(HttpClient c)
    {
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Lycoris/" + AppVersion.CurrentText);
        return c;
    }
}
