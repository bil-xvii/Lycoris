using System.IO;
using System.Net;
using System.Net.Http;
using Lycoris.Core;
using Lycoris.Core.Updates;

namespace Lycoris.Tests.Updates;

public sealed class SemVerTests
{
    [Theory]
    [InlineData("v1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0.0+abc123", "1.0.0", 0)]
    [InlineData("1.0.0.1", "1.0.0", 1)]
    public void Versions_compare_like_semver(string a, string b, int expected)
    {
        Assert.True(SemVer.TryParse(a, out var x));
        Assert.True(SemVer.TryParse(b, out var y));
        Assert.Equal(expected, Math.Sign(x.CompareTo(y)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    [InlineData("v1.2.3.4.5")]
    [InlineData("1.0.0 beta")]
    public void Garbage_is_not_a_version(string text) => Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void Prerelease_label_is_detected_and_printed()
    {
        Assert.True(SemVer.TryParse("v2.1.0-rc.1", out var v));
        Assert.True(v.IsPrerelease);
        Assert.Equal("2.1.0-rc.1", v.ToString());
        Assert.Equal("1.0.1", SemVer.Parse("v1.0.1")!.Value.ToString());
    }

    [Fact]
    public void The_running_version_is_the_csproj_version_single_source_of_truth()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lycoris.slnx"))) dir = dir.Parent;
        var csproj = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Lycoris", "Lycoris.csproj"));
        var m = System.Text.RegularExpressions.Regex.Match(csproj, @"<Version>([^<]+)</Version>");
        Assert.True(m.Success);
        Assert.Equal(SemVer.Parse(m.Groups[1].Value), AppVersion.Current);
        Assert.DoesNotContain("<AssemblyVersion>", csproj);
        Assert.DoesNotContain("<FileVersion>", csproj);
    }
}

public sealed class ReleaseParserTests
{
    [Fact]
    public void A_normal_release_is_parsed_with_assets_and_digest()
    {
        var sha = new string('a', 64);
        var r = ReleaseParser.TryParse(Kit.ReleaseJson("v1.2.3", 70_000_000, sha), out var problem);
        Assert.NotNull(r);
        Assert.Null(problem);
        Assert.Equal("1.2.3", r!.VersionText);
        Assert.Equal(70_000_000, r.ExeSize);
        Assert.Equal(sha, r.ExeDigest);
        Assert.EndsWith("/v1.2.3/Lycoris.exe", r.ExeUrl);
        Assert.EndsWith("/v1.2.3/Lycoris.exe.sha256", r.ShaUrl);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("tag-prerelease")]
    [InlineData("no-exe")]
    [InlineData("bad-tag")]
    public void Unusable_releases_are_ignored(string kind)
    {
        var json = kind switch
        {
            "draft" => Kit.ReleaseJson(draft: true),
            "prerelease" => Kit.ReleaseJson(prerelease: true),
            "tag-prerelease" => Kit.ReleaseJson("v1.1.0-beta.1"),
            "no-exe" => Kit.ReleaseJson(exe: false),
            _ => Kit.ReleaseJson("nightly"),
        };
        Assert.Null(ReleaseParser.TryParse(json, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"tag_name\": 5}")]
    [InlineData("{\"tag_name\":\"v1.0.1\",\"assets\":\"x\"}")]
    [InlineData("{\"tag_name\":\"v1.0.1\",\"assets\":[1,null,{\"name\":5}]}")]
    public void Malformed_json_never_throws(string json) => Assert.Null(ReleaseParser.TryParse(json, out _));

    [Fact]
    public void A_bad_digest_is_dropped_not_trusted()
    {
        var r = ReleaseParser.TryParse(Kit.ReleaseJson(digest: "nothex"), out _);
        Assert.NotNull(r);
        Assert.Equal("", r!.ExeDigest);
    }

    [Fact]
    public void Notes_preview_has_at_most_fifteen_lines()
    {
        var notes = string.Join("\n", Enumerable.Range(1, 40).Select(i => "- line " + i));
        var r = ReleaseParser.TryParse(Kit.ReleaseJson(notes: notes), out _)!;
        var preview = r.NotesPreview().Split('\n');
        Assert.Equal(16, preview.Length); // 15 lines + the ellipsis line
        Assert.Equal("…", preview[^1]);
    }

    [Fact]
    public void Cache_roundtrip_and_a_damaged_cache_is_just_no_cache()
    {
        var r = Kit.Release("v1.0.4", 5000, new string('b', 64));
        var back = ReleaseParser.Deserialize(ReleaseParser.Serialize(r));
        Assert.Equal(r, back);
        Assert.Null(ReleaseParser.Deserialize("{broken"));
        Assert.Null(ReleaseParser.Deserialize(""));
        Assert.Null(ReleaseParser.Deserialize("{\"Tag\":\"zzz\"}"));
    }
}

public sealed class UpdateServiceTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static (UpdateService Service, FakeStore Store, Clock Clock, FakeHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> answer, string current = "1.0.0")
    {
        var store = new FakeStore();
        var clock = new Clock(T0);
        var handler = new FakeHandler(answer);
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Lycoris/" + current);
        var service = new UpdateService(http, store, () => SemVer.Parse(current)!.Value, clock.Get);
        return (service, store, clock, handler);
    }

    [Fact]
    public async Task A_newer_release_is_found_cached_and_announced()
    {
        var (s, store, _, h) = Make(_ => FakeHandler.Json(Kit.ReleaseJson("v1.0.1"), "\"abc\""));
        var r = await s.CheckAsync(manual: false);
        Assert.Equal(UpdateCheckStatus.Available, r.Status);
        Assert.Equal("1.0.1", r.Release!.VersionText);
        Assert.True(s.ShouldAnnounce);
        Assert.Equal("\"abc\"", store.ETag);
        Assert.Equal(T0, store.LastCheckUtc);
        Assert.NotEmpty(store.CachedRelease);

        var req = Assert.Single(h.Requests);
        Assert.Equal("api.github.com", req.RequestUri!.Host);
        Assert.Equal("/repos/bil-xvii/Lycoris-releases/releases/latest", req.RequestUri.AbsolutePath);
        Assert.StartsWith("Lycoris/", req.Headers.UserAgent.ToString());
        Assert.False(req.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Background_checks_happen_at_most_every_12_hours()
    {
        var (s, _, clock, h) = Make(_ => FakeHandler.Json(Kit.ReleaseJson("v1.0.1")));
        await s.CheckAsync(false);
        clock.Now = T0.AddHours(11.9);
        var skipped = await s.CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.Skipped, skipped.Status);
        Assert.NotNull(skipped.Release); // still known from the cache
        Assert.Single(h.Requests);

        clock.Now = T0.AddHours(12.1);
        await s.CheckAsync(false);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task A_clock_set_back_does_not_block_checks_forever()
    {
        var (s, store, clock, h) = Make(_ => FakeHandler.Json(Kit.ReleaseJson("v1.0.1")));
        store.LastCheckUtc = T0.AddDays(30); // far in the future
        Assert.True(s.IsDue());
        await s.CheckAsync(false);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task The_switch_in_settings_turns_background_checks_off_but_not_the_button()
    {
        var (s, store, _, h) = Make(_ => FakeHandler.Json(Kit.ReleaseJson("v1.0.1")));
        store.AutoCheck = false;
        Assert.Equal(UpdateCheckStatus.Skipped, (await s.CheckAsync(false)).Status);
        Assert.Empty(h.Requests);
        Assert.Equal(UpdateCheckStatus.Available, (await s.CheckAsync(true)).Status);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task A_manual_check_sends_the_etag_and_a_304_keeps_the_cached_release()
    {
        var calls = 0;
        var (s, store, clock, h) = Make(req =>
        {
            calls++;
            return calls == 1 ? FakeHandler.Json(Kit.ReleaseJson("v1.0.1"), "\"v1\"") : FakeHandler.Status(HttpStatusCode.NotModified);
        });
        await s.CheckAsync(true);
        clock.Now = T0.AddMinutes(5);
        var second = await s.CheckAsync(true);
        Assert.Equal(UpdateCheckStatus.Available, second.Status);
        Assert.Equal("\"v1\"", h.Requests[1].Headers.GetValues("If-None-Match").Single());
        Assert.Equal(T0.AddMinutes(5), store.LastCheckUtc);
        Assert.Equal("1.0.1", s.Available!.VersionText);
    }

    [Fact]
    public async Task Rate_limit_is_not_an_error_and_backs_off_until_the_reset_time()
    {
        var reset = new DateTimeOffset(T0.AddMinutes(40)).ToUnixTimeSeconds();
        var (s, store, clock, h) = Make(_ =>
        {
            var r = FakeHandler.Status(HttpStatusCode.Forbidden);
            r.Headers.Add("X-RateLimit-Reset", reset.ToString());
            return r;
        });
        var r1 = await s.CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.Failed, r1.Status);
        Assert.Null(store.LastCheckUtc); // a failure is not a successful check
        clock.Now = T0.AddMinutes(20);
        Assert.Equal(UpdateCheckStatus.Skipped, (await s.CheckAsync(false)).Status); // still backing off
        Assert.Single(h.Requests);
        clock.Now = T0.AddMinutes(41);
        await s.CheckAsync(false);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Offline_and_malformed_answers_never_throw_and_keep_the_previous_state()
    {
        var mode = 0;
        var (s, store, clock, _) = Make(_ =>
        {
            if (mode == 0) return FakeHandler.Json(Kit.ReleaseJson("v1.0.1"));
            if (mode == 1) throw new HttpRequestException("no network");
            if (mode == 2) return FakeHandler.Json("{ this is not json");
            return FakeHandler.Status(HttpStatusCode.InternalServerError);
        });
        await s.CheckAsync(true);
        foreach (var m in new[] { 1, 2, 3 })
        {
            mode = m;
            clock.Now = clock.Now.AddHours(13);
            var r = await s.CheckAsync(true);
            Assert.Equal(UpdateCheckStatus.Failed, r.Status);
            Assert.False(string.IsNullOrEmpty(r.Message));
            Assert.Equal("1.0.1", s.Available!.VersionText); // the cache survives
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_the_only_exception()
    {
        var (s, _, _, _) = Make(_ => FakeHandler.Json(Kit.ReleaseJson()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.CheckAsync(true, cts.Token));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("1.2.0", "1.0.1")]
    public async Task Same_or_older_release_is_up_to_date(string current, string tag)
    {
        var (s, _, _, _) = Make(_ => FakeHandler.Json(Kit.ReleaseJson("v" + tag)), current);
        var r = await s.CheckAsync(true);
        Assert.Equal(UpdateCheckStatus.UpToDate, r.Status);
        Assert.Null(s.Available);
    }

    [Fact]
    public async Task Prerelease_and_draft_answers_offer_nothing()
    {
        var (s, _, _, _) = Make(_ => FakeHandler.Json(Kit.ReleaseJson("v2.0.0", prerelease: true)));
        var r = await s.CheckAsync(true);
        Assert.Equal(UpdateCheckStatus.UpToDate, r.Status);
        Assert.Null(s.Available);
    }

    [Fact]
    public async Task No_release_yet_is_up_to_date()
    {
        var (s, _, _, _) = Make(_ => FakeHandler.Status(HttpStatusCode.NotFound));
        Assert.Equal(UpdateCheckStatus.UpToDate, (await s.CheckAsync(true)).Status);
    }

    [Fact]
    public async Task Dismissing_a_version_hides_only_that_version()
    {
        var tag = "v1.0.1";
        var (s, store, clock, _) = Make(_ => FakeHandler.Json(Kit.ReleaseJson(tag)));
        await s.CheckAsync(true);
        Assert.True(s.ShouldAnnounce);
        s.Dismiss(s.Available!);
        Assert.False(s.ShouldAnnounce);
        Assert.NotNull(s.Available); // Settings → О программе still shows it

        tag = "v1.0.2";
        clock.Now = T0.AddHours(13);
        await s.CheckAsync(false);
        Assert.Equal("1.0.2", s.Available!.VersionText);
        Assert.True(s.ShouldAnnounce); // a newer version is announced again

        s.ClearReminders();
        Assert.Equal("", store.DismissedVersion);
    }

    [Fact]
    public async Task Remind_later_hides_the_button_for_a_day()
    {
        var (s, _, clock, _) = Make(_ => FakeHandler.Json(Kit.ReleaseJson("v1.0.1")));
        await s.CheckAsync(true);
        s.Snooze();
        Assert.False(s.ShouldAnnounce);
        clock.Now = T0.AddHours(23);
        Assert.False(s.ShouldAnnounce);
        clock.Now = T0.AddHours(25);
        Assert.True(s.ShouldAnnounce);
    }

    [Fact]
    public void A_cached_release_shows_the_button_at_once_without_any_request()
    {
        var store = new FakeStore { CachedRelease = ReleaseParser.Serialize(Kit.Release("v1.0.1")) };
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no network allowed"));
        var s = new UpdateService(new HttpClient(handler), store, () => new SemVer(1, 0, 0, 0, ""), () => T0);
        Assert.True(s.ShouldAnnounce);
        Assert.Empty(handler.Requests);
    }
}
