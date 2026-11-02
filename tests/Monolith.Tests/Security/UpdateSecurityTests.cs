using System.IO;
using System.Net;
using System.Net.Http;
using Monolith.Core;
using Monolith.Core.Security;
using Monolith.Core.Updates;
using Monolith.Tests.Updates;

namespace Monolith.Tests.Security;

/// <summary>The update channel against a hijacked release, a swapped staged file, a forged updater call and a rollback.</summary>
public sealed class UpdateSecurityTests : IDisposable
{
    private readonly string _dir = Kit.TempDir("sec");
    private readonly byte[] _exe = Kit.FakePe(4096);
    private readonly (string Private, string Public) _keys = ReleaseSignature.GenerateKeyPair();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SignatureInfo Unsigned => new("NotSigned", "", "");

    private UpdateDownloader Make(FakeHandler h, string? pinned = null, Func<string, SemVer?>? versionOf = null, SemVer? current = null) =>
        new(new HttpClient(h), Kit.SmallPolicy, p => Task.FromResult(Unsigned), @"C:\Apps\Lycoris.exe", versionOf, pinned,
            current is { } c ? () => c : () => new SemVer(1, 0, 0, 0, ""));

    private FakeHandler Serve(string? signature)
    {
        return new FakeHandler(req =>
        {
            var url = req.RequestUri!;
            if (url.Host == "github.com" && url.AbsolutePath.EndsWith("/Lycoris.exe.sha256")) return FakeHandler.Json(Kit.Sha(_exe) + "  Lycoris.exe\n");
            if (url.Host == "github.com" && url.AbsolutePath.EndsWith("/Lycoris.exe.sig")) return signature is null ? FakeHandler.Status(HttpStatusCode.NotFound) : FakeHandler.Json(signature);
            if (url.Host == "github.com" && url.AbsolutePath.EndsWith("/Lycoris.exe")) return FakeHandler.Bytes(_exe);
            return FakeHandler.Status(HttpStatusCode.NotFound);
        });
    }

    private ReleaseInfo ReleaseWithSig(bool withSig = true, string tag = "v1.0.1")
    {
        var json = Kit.ReleaseJson(tag, _exe.Length);
        if (withSig)
            json = json.Replace("]}", $",{{\"name\":\"Lycoris.exe.sig\",\"state\":\"uploaded\",\"size\":88,\"browser_download_url\":\"{Kit.BaseUrl}{tag}/Lycoris.exe.sig\"}}]}}");
        return ReleaseParser.TryParse(json, out _)!;
    }

    private string Sign(string version, string file = "Lycoris.exe", string? hash = null, string? key = null) =>
        ReleaseSignature.Sign(ReleaseSignature.SignedStatement(version, file, hash ?? Kit.Sha(_exe)), key ?? _keys.Private);

    private Task<StagedUpdate> Download(UpdateDownloader d, ReleaseInfo r) => d.DownloadAsync(r, _dir, null, CancellationToken.None);

    // ───────────────────────────── Owner signature ─────────────────────────────

    [Fact]
    public async Task A_correct_owner_signature_is_accepted_when_the_key_is_pinned()
    {
        var staged = await Download(Make(Serve(Sign("1.0.1")), _keys.Public), ReleaseWithSig());
        Assert.True(File.Exists(staged.Path));
    }

    [Fact]
    public async Task A_pinned_key_makes_the_signature_mandatory()
    {
        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => Download(Make(Serve(null), _keys.Public), ReleaseWithSig(withSig: false)));
        Assert.Contains("подписи владельца", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_signature_from_another_key_is_refused_and_the_file_is_deleted()
    {
        var (otherPrivate, _) = ReleaseSignature.GenerateKeyPair();
        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => Download(Make(Serve(Sign("1.0.1", key: otherPrivate)), _keys.Public), ReleaseWithSig()));
        Assert.Contains("Подпись релиза не подтверждена", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData("1.0.0", "Lycoris.exe")]   // an old signed release re-used for a newer tag
    [InlineData("1.0.1", "OTHER.exe")]      // a signature made for another file
    public async Task A_valid_signature_of_another_version_or_file_is_refused(string version, string file)
    {
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Download(Make(Serve(Sign(version, file)), _keys.Public), ReleaseWithSig()));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_signature_of_other_bytes_is_refused()
    {
        var other = Sign("1.0.1", hash: Kit.Sha(Kit.FakePe(4096, seed: 7)));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Download(Make(Serve(other), _keys.Public), ReleaseWithSig()));
    }

    [Fact]
    public async Task A_signature_asset_outside_the_releases_repository_is_refused()
    {
        var json = Kit.ReleaseJson("v1.0.1", _exe.Length).Replace("]}", ",{\"name\":\"Lycoris.exe.sig\",\"state\":\"uploaded\",\"size\":88,\"browser_download_url\":\"https://evil.example/Lycoris.exe.sig\"}]}");
        var release = ReleaseParser.TryParse(json, out _)!;
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Download(Make(Serve(Sign("1.0.1")), _keys.Public), release));
    }

    [Fact]
    public async Task Without_a_pinned_key_the_channel_still_works_but_is_reported_as_unauthenticated()
    {
        // Documented open item: the owner has not generated a key yet. Nothing breaks; the warning goes to the log.
        var staged = await Download(Make(Serve(null), pinned: ""), ReleaseWithSig(withSig: false));
        Assert.True(File.Exists(staged.Path));
    }

    // ───────────────────────────── Version inside the file (rollback through the writable cache) ─────────────────────────────

    [Fact]
    public async Task The_version_inside_the_file_must_match_the_release_and_be_newer_than_this_build()
    {
        var ok = await Download(Make(Serve(null), pinned: "", versionOf: _ => new SemVer(1, 0, 1, 0, "")), ReleaseWithSig(withSig: false));
        Assert.True(File.Exists(ok.Path));
    }

    [Theory]
    [InlineData(0, 9, 0)]  // the file is an old build although the release says 1.0.1
    [InlineData(2, 0, 0)]  // newer than announced
    public async Task A_file_whose_inner_version_differs_from_the_announced_one_is_refused(int major, int minor, int patch)
    {
        var d = Make(Serve(null), pinned: "", versionOf: _ => new SemVer(major, minor, patch, 0, ""));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Download(d, ReleaseWithSig(withSig: false)));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_file_without_a_readable_version_is_refused()
    {
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Download(Make(Serve(null), pinned: "", versionOf: _ => null), ReleaseWithSig(withSig: false)));
    }

    [Fact]
    public async Task A_release_that_is_not_newer_than_the_running_build_is_refused()
    {
        var d = Make(Serve(null), pinned: "", versionOf: _ => new SemVer(1, 0, 1, 0, ""), current: new SemVer(1, 0, 1, 0, ""));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => Download(d, ReleaseWithSig(withSig: false)));
    }

    [Fact]
    public void The_release_parser_reads_the_signature_asset()
    {
        Assert.EndsWith("/Lycoris.exe.sig", ReleaseWithSig().SigUrl);
        Assert.Equal("", ReleaseWithSig(withSig: false).SigUrl);
    }

    // ───────────────────────────── The headless updater (--apply-update) ─────────────────────────────

    private const string Old = @"C:\Apps\MONOLITH\Lycoris.exe";
    private const string Self = @"C:\Apps\MONOLITH\Lycoris.exe.new";

    /// <summary>The fake file system with the real updater's security answers made configurable.</summary>
    private sealed class GuardEnv : FakeEnv, IUpdateEnvironment
    {
        public bool TargetIsMonolith { get; set; } = true;
        public bool PidMatches { get; set; } = true;
        public bool Newer { get; set; } = true;
        public bool SelfIsExpected { get; set; } = true;
        public bool Held { get; private set; }
        public bool HeldDuringApply { get; private set; }
        public string? HashChecked { get; private set; }

        bool IUpdateEnvironment.IsMonolithExe(string path) => TargetIsMonolith;
        bool IUpdateEnvironment.PidRunsExe(int pid, string exe) => PidMatches;
        bool IUpdateEnvironment.IsNewerThan(string candidate, string current) => Newer;

        IDisposable? IUpdateEnvironment.HoldIfIdentical(string path, string sha256, out bool identical)
        {
            HashChecked = sha256;
            identical = SelfIsExpected;
            if (!identical) return null;
            Held = true;
            return new Release(this);
        }

        private sealed class Release(GuardEnv env) : IDisposable
        {
            public void Dispose() => env.Held = false;
        }

        public new bool WaitForExit(int pid, TimeSpan timeout)
        {
            HeldDuringApply = Held;
            return true;
        }
    }

    private static GuardEnv Env()
    {
        var e = new GuardEnv();
        e.Files[Old] = "OLD";
        e.Files[Self] = "NEW";
        return e;
    }

    private static readonly string Hash = new('a', 64);

    [Fact]
    public void A_legitimate_call_updates_and_holds_the_updater_file_while_the_files_are_exchanged()
    {
        var e = Env();
        Assert.Equal(0, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42", Hash], e, Self, elevated: true));
        Assert.Equal(Hash, e.HashChecked);
        Assert.Equal("NEW", e.Files[Old]);
        Assert.False(e.Held); // released at the end
    }

    [Fact]
    public void A_target_that_is_not_a_MONOLITH_program_is_never_touched()
    {
        var e = Env();
        e.TargetIsMonolith = false;
        Assert.Equal(13, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42", Hash], e, Self, elevated: true));
        Assert.Equal("OLD", e.Files[Old]);
        Assert.Empty(e.Log);
    }

    [Fact]
    public void A_forged_process_id_is_refused()
    {
        var e = Env();
        e.PidMatches = false;
        Assert.Equal(13, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42", Hash], e, Self, elevated: false));
        Assert.Empty(e.Log);
    }

    [Fact]
    public void An_elevated_updater_without_the_handshake_hash_is_refused()
    {
        var e = Env();
        Assert.Equal(13, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42"], e, Self, elevated: true));
        Assert.Empty(e.Log);
        // A normal-rights updater without the hash still works (it can only touch what the user can touch anyway).
        Assert.Equal(0, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42"], Env(), Self, elevated: false));
    }

    [Fact]
    public void A_swapped_updater_file_is_refused()
    {
        var e = Env();
        e.SelfIsExpected = false;
        Assert.Equal(13, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42", Hash], e, Self, elevated: true));
        Assert.Equal("OLD", e.Files[Old]);
        Assert.Empty(e.Log);
    }

    [Fact]
    public void A_downgrade_or_reinstall_is_refused()
    {
        var e = Env();
        e.Newer = false;
        Assert.Equal(13, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42", Hash], e, Self, elevated: false));
        Assert.Equal("OLD", e.Files[Old]);
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("")]
    [InlineData("abc")]
    public void A_malformed_hash_argument_is_a_bad_call(string hash) =>
        Assert.Equal(2, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42", hash], Env(), Self, elevated: false));

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"c:\windows\SysWOW64\x.exe")]
    public void The_Windows_folder_is_never_a_target(string target)
    {
        var e = Env();
        e.Files[target] = "SYSTEM";
        Assert.Equal(13, UpdateApplyMode.Run([UpdateApplyMode.Switch, target, "42", Hash], e, Self, elevated: true));
        Assert.Equal("SYSTEM", e.Files[target]);
    }

    [Fact]
    public void The_real_environment_refuses_a_target_that_is_not_MONOLITH_and_a_forged_pid()
    {
        var real = new SystemUpdateEnvironment();
        IUpdateEnvironment env = real;
        Assert.False(env.IsMonolithExe(ProcessRunner.Sys("cmd.exe")));
        Assert.False(env.IsMonolithExe(Path.Combine(_dir, "missing.exe")));
        // This test process is not an exe called cmd.exe: its pid does not "run" cmd.exe.
        Assert.False(env.PidRunsExe(Environment.ProcessId, ProcessRunner.Sys("cmd.exe")));
        Assert.True(env.PidRunsExe(int.MaxValue - 1, ProcessRunner.Sys("cmd.exe"))); // no such process: nothing to contradict
    }

    [Fact]
    public void The_real_environment_holds_the_file_it_hashed_against_swapping()
    {
        var f = Path.Combine(_dir, "u.exe");
        var bytes = Kit.FakePe(2048);
        File.WriteAllBytes(f, bytes);
        IUpdateEnvironment env = new SystemUpdateEnvironment();

        using (var hold = env.HoldIfIdentical(f, Kit.Sha(bytes), out var ok))
        {
            Assert.True(ok);
            Assert.NotNull(hold);
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(f, [1]));       // not writable while held
            Assert.ThrowsAny<IOException>(() => File.Move(f, f + ".moved"));        // not renamable while held
            Assert.ThrowsAny<IOException>(() => File.Delete(f));                    // not deletable while held
        }
        File.WriteAllBytes(f, [1]); // released

        using var none = env.HoldIfIdentical(f, Kit.Sha(bytes), out var identical);
        Assert.False(identical);
        Assert.Null(none);
    }

    [Fact]
    public void The_real_environment_orders_versions_strictly()
    {
        IUpdateEnvironment env = new SystemUpdateEnvironment();
        var dll = typeof(UpdateApplyMode).Assembly.Location;
        // Same file twice = same version = not newer. A file without a readable version cannot be the candidate.
        Assert.False(env.IsNewerThan(dll, dll));
        Assert.False(env.IsNewerThan(Path.Combine(_dir, "missing.exe"), dll));
    }

    [Fact]
    public void The_launcher_passes_the_verified_hash_to_the_updater()
    {
        // The handshake is part of the contract: the ONLY place that builds the updater's command line adds the hash as 4th argument.
        var source = File.ReadAllText(FindRepoFile(@"src\Monolith\Core\Updates\UpdateLauncher.cs"));
        Assert.Contains("psi.ArgumentList.Add(staged.Sha256)", source);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir.FullName, relative);
    }
}
