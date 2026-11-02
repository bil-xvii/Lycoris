using System.IO;
using System.Net;
using System.Net.Http;
using Monolith.Core;
using Monolith.Core.Updates;

namespace Monolith.Tests.Updates;

public sealed class UpdateDownloadTests : IDisposable
{
    private readonly string _dir = Kit.TempDir("dl");
    private readonly byte[] _exe = Kit.FakePe(4096);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SignatureInfo Unsigned => new("NotSigned", "", "");

    private UpdateDownloader Make(FakeHandler h, Func<string, SignatureInfo>? sig = null, UpdatePolicy? policy = null) =>
        new(new HttpClient(h), policy ?? Kit.SmallPolicy, p => Task.FromResult((sig ?? (_ => Unsigned))(p)), @"C:\Apps\Lycoris.exe");

    /// <summary>Serves the exe and the sha file; optional redirect through objects.githubusercontent.com like github.com does.</summary>
    private FakeHandler Serve(byte[]? exe = null, string? shaText = null, bool redirect = true)
    {
        exe ??= _exe;
        return new FakeHandler(req =>
        {
            var url = req.RequestUri!;
            if (url.Host == "github.com" && url.AbsolutePath.EndsWith("/Lycoris.exe.sha256"))
                return FakeHandler.Json(shaText ?? Kit.Sha(_exe) + "  Lycoris.exe\n");
            if (url.Host == "github.com" && url.AbsolutePath.EndsWith("/Lycoris.exe"))
                return redirect ? FakeHandler.Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/abc?token=1") : FakeHandler.Bytes(exe);
            if (url.Host == "release-assets.githubusercontent.com") return FakeHandler.Bytes(exe);
            return FakeHandler.Status(HttpStatusCode.NotFound);
        });
    }

    private Task<StagedUpdate> Run(UpdateDownloader d, ReleaseInfo? r = null) => d.DownloadAsync(r ?? Kit.Release(size: _exe.Length), _dir, null, CancellationToken.None);

    [Fact]
    public async Task A_good_release_is_downloaded_through_the_github_redirect_and_verified()
    {
        var h = Serve();
        var staged = await Run(Make(h));
        Assert.True(File.Exists(staged.Path));
        Assert.Equal(_exe, File.ReadAllBytes(staged.Path));
        Assert.Equal(Kit.Sha(_exe), staged.Sha256);
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        Assert.Contains(h.Requests, r => r.RequestUri!.Host == "release-assets.githubusercontent.com");
    }

    [Fact]
    public async Task The_github_digest_alone_is_enough_and_both_sources_must_agree()
    {
        var release = Kit.Release(size: _exe.Length, digest: Kit.Sha(_exe), sha: false);
        Assert.NotNull(await Run(Make(Serve()), release));

        var disagree = Kit.Release(size: _exe.Length, digest: new string('c', 64), sha: true);
        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(Serve()), disagree));
        Assert.Contains("не совпадают между собой", ex.Message);
    }

    [Fact]
    public async Task A_release_without_any_checksum_is_refused()
    {
        var release = Kit.Release(size: _exe.Length, sha: false);
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(Serve()), release));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_hash_mismatch_is_refused_and_nothing_stays_on_disk()
    {
        var tampered = Kit.FakePe(4096, seed: 9); // same size, other bytes
        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(Serve(exe: tampered, redirect: false))));
        Assert.Contains("Контрольная сумма", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_redirect_to_a_foreign_host_is_refused_before_it_is_requested()
    {
        var h = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(".sha256")) return FakeHandler.Json(Kit.Sha(_exe));
            if (req.RequestUri.Host == "github.com") return FakeHandler.Redirect("https://evil.example.com/Lycoris.exe");
            return FakeHandler.Bytes(_exe);
        });
        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(h)));
        Assert.Contains("посторонн", ex.Message);
        Assert.DoesNotContain(h.Requests, r => r.RequestUri!.Host == "evil.example.com");
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData("http://github.com/bil-xvii/Lycoris-releases/releases/download/v1.0.1/Lycoris.exe")]
    [InlineData("https://github.com/someone-else/Lycoris-releases/releases/download/v1.0.1/Lycoris.exe")]
    [InlineData("https://evil.example.com/bil-xvii/Lycoris-releases/releases/download/v1.0.1/Lycoris.exe")]
    [InlineData("https://github.com/bil-xvii/Lycoris-releases/releases/download/v1.0.1/Other.exe")]
    [InlineData("https://github.com:8443/bil-xvii/Lycoris-releases/releases/download/v1.0.1/Lycoris.exe")]
    [InlineData("https://user:pw@github.com/bil-xvii/Lycoris-releases/releases/download/v1.0.1/Lycoris.exe")]
    public async Task A_link_outside_the_official_releases_is_refused_without_any_request(string url)
    {
        var h = Serve();
        var release = Kit.Release(size: _exe.Length) with { ExeUrl = url };
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(h), release));
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData(100)]        // below the minimum of the test policy
    [InlineData(2_000_000)]  // above the maximum
    public async Task The_size_must_be_sane(int size)
    {
        var h = Serve();
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(h), Kit.Release(size: size)));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task A_file_that_is_not_the_announced_size_is_refused()
    {
        var release = Kit.Release(size: _exe.Length + 10);
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(Serve(redirect: false)), release));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_file_that_is_not_a_x64_pe_is_refused_even_with_a_matching_hash()
    {
        var junk = new byte[4096];
        Random.Shared.NextBytes(junk);
        var h = Serve(exe: junk, shaText: Kit.Sha(junk), redirect: false);
        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(h), Kit.Release(size: junk.Length)));
        Assert.Contains("x64", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_signed_application_accepts_only_the_same_signer()
    {
        var current = new SignatureInfo("Valid", "MONOLITH Software", "AAA");
        // another publisher
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(Serve(), p => p.EndsWith(".part") ? new("Valid", "Somebody Else", "BBB") : current)));
        Assert.Empty(Directory.GetFiles(_dir));
        // unsigned replacement of a signed app
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(Serve(), p => p.EndsWith(".part") ? Unsigned : current)));
        // same signer, valid
        var ok = await Run(Make(Serve(), p => p.EndsWith(".part") ? new("Valid", "MONOLITH Software, Inc.", "CCC") : current));
        Assert.True(File.Exists(ok.Path));
    }

    [Fact]
    public async Task A_stalled_or_failing_server_gives_a_plain_error_and_cleans_up()
    {
        var h = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith(".sha256")
            ? FakeHandler.Json(Kit.Sha(_exe)) : FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Run(Make(h)));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    // ───────── pure verifier pieces ─────────

    [Theory]
    [InlineData("NotSigned", "", "NotSigned", "", true)]
    [InlineData("NotSigned", "", "Valid", "Acme", true)]
    [InlineData("NotSigned", "", "HashMismatch", "Acme", false)]
    [InlineData("Valid", "Acme", "Valid", "Acme", true)]
    [InlineData("Valid", "Acme", "Valid", "Acme, Inc.", true)]
    [InlineData("Valid", "Acme", "Valid", "Evil", false)]
    [InlineData("Valid", "Acme", "NotSigned", "", false)]
    [InlineData("Valid", "Acme", "HashMismatch", "Acme", false)]
    [InlineData("Valid", "Acme", "UnknownError", "Acme", false)]
    [InlineData("UnknownError", "Owner", "UnknownError", "Owner", true)]   // self-signed by the owner on a PC that does not trust it
    [InlineData("UnknownError", "Owner", "UnknownError", "Evil", false)]
    public void Signer_policy(string curStatus, string curOrg, string newStatus, string newOrg, bool allowed)
    {
        SignatureInfo S(string st, string org) => new(st, org, org.Length > 0 ? "T" + org : "");
        Assert.Equal(allowed, UpdateVerifier.CheckSigners(S(curStatus, curOrg), S(newStatus, newOrg)) is null);
    }

    [Fact]
    public void Sha_file_formats_are_understood()
    {
        var h = new string('A', 64);
        Assert.Equal(h.ToLowerInvariant(), UpdateVerifier.ParseShaFile(h + "  Lycoris.exe\r\n"));
        Assert.Equal(h.ToLowerInvariant(), UpdateVerifier.ParseShaFile(h));
        Assert.Equal(h.ToLowerInvariant(), UpdateVerifier.ParseShaFile(h + " *Lycoris.exe"));
        Assert.Null(UpdateVerifier.ParseShaFile("<html>rate limited</html>"));
        Assert.Null(UpdateVerifier.ParseShaFile(new string('z', 64)));
    }

    [Fact]
    public void Pe_header_check()
    {
        Assert.True(UpdateVerifier.IsPe64(Kit.FakePe(1024)));
        Assert.False(UpdateVerifier.IsPe64(new byte[1024]));
        var x86 = Kit.FakePe(1024);
        BitConverter.GetBytes((ushort)0x14C).CopyTo(x86, 0x84);
        Assert.False(UpdateVerifier.IsPe64(x86));
        var pe32 = Kit.FakePe(1024);
        BitConverter.GetBytes((ushort)0x10B).CopyTo(pe32, 0x98);
        Assert.False(UpdateVerifier.IsPe64(pe32));
        Assert.False(UpdateVerifier.IsPe64(Kit.FakePe(1024)[..30]));
    }

    [Fact]
    public void Default_policy_limits_and_hosts_match_the_spec()
    {
        var p = new UpdatePolicy();
        Assert.Equal(40L << 20, p.MinSize);
        Assert.Equal(300L << 20, p.MaxSize);
        Assert.Equal(["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"], p.Hosts);
        Assert.True(UpdateVerifier.IsAllowedHop(new Uri("https://objects.githubusercontent.com/x"), p));
        Assert.False(UpdateVerifier.IsAllowedHop(new Uri("https://githubusercontent.com/x"), p));
        Assert.False(UpdateVerifier.IsAllowedHop(new Uri("https://github.com.evil.com/x"), p));
        Assert.False(UpdateVerifier.IsAllowedHop(new Uri("http://github.com/x"), p));
    }
}
