using System.IO;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Optimize;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

/// <summary>
/// «Lycoris.exe --apply-profile normal|gaming|recommended [--dry-run] [--yes]»: argument parsing (an allow-list — launch
/// arguments are attacker input), the printed plan, and the exit codes, with a fake host. Nothing touches the system.
/// </summary>
public sealed class ProfileCommandLineTests
{
    // ───────────────────────────── Parsing ─────────────────────────────

    [Theory]
    [InlineData(new[] { "--apply-profile", "normal" }, OptimizeProfile.Normal, false, false)]
    [InlineData(new[] { "--apply-profile", "gaming", "--dry-run" }, OptimizeProfile.Gaming, true, false)]
    [InlineData(new[] { "--yes", "--apply-profile", "recommended" }, OptimizeProfile.Recommended, false, true)]
    [InlineData(new[] { "--apply-profile", "gaming", "--yes", "--lang", "en" }, OptimizeProfile.Gaming, false, true)]
    public void Parses_the_allowed_forms(string[] args, OptimizeProfile profile, bool dry, bool yes)
    {
        var o = ProfileCommandLine.Parse(args, out var error);
        Assert.Null(error);
        Assert.NotNull(o);
        Assert.Equal(profile, o!.Profile);
        Assert.Equal(dry, o.DryRun);
        Assert.Equal(yes, o.Yes);
    }

    [Fact]
    public void Dry_run_wins_over_yes_and_no_yes_means_plan_only()
    {
        Assert.False(ProfileCommandLine.Parse(["--apply-profile", "gaming", "--yes", "--dry-run"], out _)!.Apply);
        Assert.False(ProfileCommandLine.Parse(["--apply-profile", "gaming"], out _)!.Apply);
        Assert.True(ProfileCommandLine.Parse(["--apply-profile", "gaming", "--yes"], out _)!.Apply);
    }

    [Theory]
    [InlineData("--apply-profile")]
    [InlineData("--apply-profile Gaming")]
    [InlineData("--apply-profile streaming")]
    [InlineData("--apply-profile custom")]
    [InlineData("--apply-profile gaming --yes --yes")]
    [InlineData("--apply-profile gaming --apply-profile normal")]
    [InlineData("--apply-profile gaming --force")]
    [InlineData("--apply-profile gaming --lang de")]
    [InlineData("--apply-profile gaming --lang")]
    [InlineData("--APPLY-PROFILE gaming")]
    [InlineData("--yes")]
    [InlineData("--apply-profile gaming --elevated --replay-page optimize --replay-command Apply")]
    public void Refuses_anything_outside_the_allow_list(string commandLine)
    {
        var args = commandLine.Split(' ');
        Assert.Null(ProfileCommandLine.Parse(args, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Is_requested_in_any_position_and_case_and_only_by_its_switch()
    {
        Assert.True(ProfileCommandLine.IsRequested(["--lang", "en", "--apply-profile", "gaming"]));
        Assert.True(ProfileCommandLine.IsRequested(["--Apply-Profile"]));
        Assert.False(ProfileCommandLine.IsRequested(["--page", "optimize", "--snapshot", "x"]));
        Assert.False(ProfileCommandLine.IsRequested(["--apply-profiles"]));
    }

    [Fact]
    public void Hostile_values_are_never_echoed_raw()
    {
        var hostile = "\u001b]0;pwned\u0007\u202Eevil\r\nline2" + new string('x', 200);
        Assert.Null(ProfileCommandLine.Parse(["--apply-profile", hostile], out var e1));
        Assert.Null(ProfileCommandLine.Parse(["--apply-profile", "gaming", hostile], out var e2));
        foreach (var e in new[] { e1!, e2! })
        {
            Assert.DoesNotContain('\u001b', e);
            Assert.DoesNotContain('\u202E', e);
            Assert.DoesNotContain('\n', e);
            Assert.True(e.Length < 200, e);
        }
    }

    [Fact]
    public void Security_corpus_has_no_failures()
    {
        var result = SecurityCorpus.Run();
        Assert.Empty(result.Failures);
    }

    // ───────────────────────────── Plan and exit codes (fake host) ─────────────────────────────

    private static CustomTweak Tweak(string id, RestartRequirement restart = RestartRequirement.None) => new()
    {
        Id = id,
        Title = "Пункт " + id,
        Description = "",
        Category = TweakCategory.System,
        Restart = restart,
        DetectFunc = () => TweakState.NotApplied,
        ApplyAction = _ => { },
    };

    private sealed class FakeHost : IProfileCliHost
    {
        public bool IsElevated { get; set; } = true;
        public bool LockFree { get; set; } = true;
        public List<ProfileCliTarget> Targets { get; } = [];
        public Dictionary<string, TweakState> After { get; } = [];
        public List<string> Errors { get; } = [];
        public Exception? Throw { get; set; }
        public int Selects, Locks;
        public List<(IReadOnlyList<Tweak> Tweaks, string Title)> Applies { get; } = [];

        public Task<ProfileCliSelection> SelectAsync(OptimizeProfile profile)
        {
            Selects++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new ProfileCliSelection(Targets, AlreadyApplied: 7, NotApplicable: 2, Laptop: false));
        }

        public IReadOnlyList<ProfileCliLine> ReadChanges(Tweak tweak) => tweak.Id == "reg"
            ? [new ProfileCliLine(@"HKCU\Software\X\Value", "нет значения", "1 (DWORD)")]
            : [];

        public Task<string> RestorePointNoteAsync() => Task.FromResult("Перед изменениями будет создана точка восстановления Windows.");

        public bool TryLockInstance()
        {
            Locks++;
            return LockFree;
        }

        public Task<TweakRunResult> ApplyAsync(IReadOnlyList<Tweak> tweaks, string batchTitle, IProgress<string> progress)
        {
            Applies.Add((tweaks, batchTitle));
            foreach (var t in tweaks) progress.Report(t.Title);
            return Task.FromResult(new TweakRunResult(tweaks.Count, 0, Errors, "Точка восстановления создана.", RestartRequirement.None));
        }

        public Task<IReadOnlyDictionary<string, TweakState>> DetectAsync(IReadOnlyList<Tweak> tweaks) =>
            Task.FromResult<IReadOnlyDictionary<string, TweakState>>(tweaks.ToDictionary(t => t.Id, t => After.GetValueOrDefault(t.Id, TweakState.Applied)));
    }

    private static FakeHost HostWith(params string[] ids)
    {
        var host = new FakeHost();
        foreach (var id in ids) host.Targets.Add(new ProfileCliTarget(Tweak(id), TweakState.NotApplied));
        return host;
    }

    private static async Task<(int Code, string Text)> Run(FakeHost host, params string[] args)
    {
        var output = new StringWriter();
        var code = await ProfileCommandLine.RunSafeAsync(args, host, output);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Dry_run_prints_every_change_with_current_and_new_value_and_changes_nothing()
    {
        var host = HostWith("reg", "plan");
        var (code, text) = await Run(host, "--apply-profile", "gaming", "--dry-run");

        Assert.Equal(ProfileCommandLine.Exit.Ok, code);
        Assert.Contains("сценарий «Игры» (gaming) — пробный запуск", text);
        Assert.Contains("ИЗМЕНИТСЯ 2:", text);
        Assert.Contains(@"HKCU\Software\X\Value: нет значения → 1 (DWORD)", text);
        Assert.Contains("1. Пункт reg", text);
        Assert.Contains("состояние: НЕ ПРИМЕНЕНО → ПРИМЕНЕНО", text); // a row without registry lines still says what changes
        Assert.Contains("Изменится: 2 · уже применено: 7 · не подходит для этого ПК: 2.", text);
        Assert.Contains("--apply-profile gaming --yes", text);
        Assert.Empty(host.Applies);
        Assert.Equal(0, host.Locks);
    }

    [Fact]
    public async Task Without_yes_it_is_a_dry_run_even_when_elevated()
    {
        var host = HostWith("reg");
        var (code, _) = await Run(host, "--apply-profile", "normal");
        Assert.Equal(ProfileCommandLine.Exit.Ok, code);
        Assert.Empty(host.Applies);
    }

    [Fact]
    public async Task Yes_without_admin_rights_refuses_before_anything_else()
    {
        var host = HostWith("reg");
        host.IsElevated = false;
        var (code, text) = await Run(host, "--apply-profile", "gaming", "--yes");

        Assert.Equal(ProfileCommandLine.Exit.NeedsAdmin, code);
        Assert.Contains("Нужны права администратора", text);
        Assert.Equal(0, host.Selects);
        Assert.Empty(host.Applies);
    }

    [Fact]
    public async Task Dry_run_needs_no_admin_rights()
    {
        var host = HostWith("reg");
        host.IsElevated = false;
        var (code, _) = await Run(host, "--apply-profile", "gaming", "--dry-run", "--yes");
        Assert.Equal(ProfileCommandLine.Exit.Ok, code);
        Assert.Empty(host.Applies);
    }

    [Fact]
    public async Task An_open_Lycoris_blocks_the_apply()
    {
        var host = HostWith("reg");
        host.LockFree = false;
        var (code, text) = await Run(host, "--apply-profile", "gaming", "--yes");

        Assert.Equal(ProfileCommandLine.Exit.Busy, code);
        Assert.Contains("Lycoris уже открыт", text);
        Assert.Empty(host.Applies);
    }

    [Fact]
    public async Task Yes_applies_once_through_the_engine_with_the_page_batch_title_and_reports_each_item()
    {
        var host = HostWith("reg", "plan");
        var (code, text) = await Run(host, "--apply-profile", "gaming", "--yes");

        Assert.Equal(ProfileCommandLine.Exit.Ok, code);
        var apply = Assert.Single(host.Applies);
        Assert.Equal(["reg", "plan"], apply.Tweaks.Select(t => t.Id));
        Assert.Equal(OptimizeLogic.BatchTitle(OptimizeProfile.Gaming, false), apply.Title);
        Assert.Contains("ПРИМЕНЕНО — Пункт reg", text);
        Assert.Contains("ПРИМЕНЕНО — Пункт plan", text);
        Assert.Contains("  … Пункт reg", text); // progress
        Assert.Contains("ГОТОВО.", text);
        Assert.Contains("точка восстановления", text);
    }

    [Fact]
    public async Task Errors_and_unconfirmed_items_get_their_own_exit_codes()
    {
        var failing = HostWith("reg");
        failing.Errors.Add("Пункт reg: доступ запрещён");
        var (code1, text1) = await Run(failing, "--apply-profile", "gaming", "--yes");
        Assert.Equal(ProfileCommandLine.Exit.ApplyErrors, code1);
        Assert.Contains("ОШИБКА — Пункт reg: доступ запрещён", text1);

        var partial = HostWith("reg", "plan");
        partial.After["plan"] = TweakState.NotApplied;
        var (code2, text2) = await Run(partial, "--apply-profile", "gaming", "--yes");
        Assert.Equal(ProfileCommandLine.Exit.NotConfirmed, code2);
        Assert.Contains("НЕ ПОДТВЕРЖДЕНО — Пункт plan", text2);
    }

    [Fact]
    public async Task Nothing_to_change_exits_zero_without_applying()
    {
        var host = HostWith();
        var (code, text) = await Run(host, "--apply-profile", "recommended", "--yes");
        Assert.Equal(ProfileCommandLine.Exit.Ok, code);
        Assert.Contains("Менять нечего", text);
        Assert.Empty(host.Applies);
        Assert.Equal(0, host.Locks);
    }

    [Fact]
    public async Task Irreversible_data_loss_is_never_applied_by_a_blanket_yes()
    {
        var host = HostWith("recall-snapshots-off", "reg");
        var (code, text) = await Run(host, "--apply-profile", "gaming", "--yes");

        Assert.Equal(ProfileCommandLine.Exit.Ok, code);
        Assert.Equal(["reg"], Assert.Single(host.Applies).Tweaks.Select(t => t.Id));
        Assert.Contains("Пропущено — Пункт recall-snapshots-off", text);
    }

    [Fact]
    public async Task Bad_arguments_print_the_usage_and_exit_2()
    {
        var host = HostWith("reg");
        var (code, text) = await Run(host, "--apply-profile", "turbo");
        Assert.Equal(ProfileCommandLine.Exit.Usage, code);
        Assert.Contains("Использование: Lycoris.exe --apply-profile", text);
        Assert.Equal(0, host.Selects);
    }

    [Fact]
    public async Task A_crash_is_exit_6_with_a_message()
    {
        var host = HostWith("reg");
        host.Throw = new InvalidOperationException("boom");
        var (code, text) = await Run(host, "--apply-profile", "gaming");
        Assert.Equal(ProfileCommandLine.Exit.Failed, code);
        Assert.Contains("boom", text);
    }

    [Fact]
    public async Task English_output_has_no_Russian_text_of_its_own()
    {
        using var _ = Loc.UseLanguage("en");
        var host = HostWith("plan");
        var (code, text) = await Run(host, "--apply-profile", "gaming", "--dry-run");
        Assert.Equal(ProfileCommandLine.Exit.Ok, code);
        // The fake's titles are Russian; everything Lycoris itself prints must be English.
        var own = text.Replace("Пункт plan", "");
        Assert.DoesNotMatch("[А-Яа-яЁё]", own);
        Assert.DoesNotMatch("[А-Яа-яЁё]", ProfileCommandLine.Usage());
    }

    // ───────────────────────────── Selection = the Optimize page's own rules ─────────────────────────────
    // Windows only (the page view-model owns WPF DispatcherTimers).

    [Fact]
    public void Pending_for_profile_uses_the_page_selection_rules()
    {
        var vm = new OptimizeViewModel();
        vm.BuildCatalog([Tweak("todo"), Tweak("done"), Tweak("na"), Tweak("other")]);
        vm.SetProfileDefaults(OptimizeProfile.Gaming, ["todo", "done", "na"]);
        var states = new Dictionary<string, TweakState>
        {
            ["todo"] = TweakState.NotApplied, ["done"] = TweakState.Applied, ["na"] = TweakState.NotApplicable, ["other"] = TweakState.NotApplied,
        };

        var pending = vm.PendingForProfile(OptimizeProfile.Gaming, states, ["na"]);

        Assert.Equal(["todo"], pending.Select(p => p.Id));
    }
}
