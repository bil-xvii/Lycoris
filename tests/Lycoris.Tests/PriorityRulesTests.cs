using System.IO;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Modules.Games;

namespace Lycoris.Tests;

/// <summary>«Приоритет программ»: which names may get a rule, the journal shape (IFEO stays forbidden except this one value), decisions.</summary>
public sealed class PriorityRulesTests
{
    private static RegistryValueSnapshot Rule(string exe, string name = PriorityRules.ValueName, bool existed = false,
        RegistryValueKind kind = RegistryValueKind.DWord, string? data = null, RegHive hive = RegHive.HKLM, string? path = null, string? root = null) => new()
    {
        Hive = hive, Path = path ?? PriorityRules.KeyPath(exe), Name = name, KeyExisted = existed, ValueExisted = existed,
        Kind = kind, Data = data, CreatedKeyRoot = root,
    };

    [Theory]
    [InlineData("Game.exe")]
    [InlineData("game.EXE")]
    [InlineData("My Game (x64).exe")]
    [InlineData("Игра-2.exe")]
    [InlineData("qbittorrent.exe")]
    public void Plain_exe_names_are_valid(string name) => Assert.True(PriorityRules.IsValidExeName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".exe")]
    [InlineData("game")]
    [InlineData(@"..\game.exe")]
    [InlineData(@"C:\Games\game.exe")]
    [InlineData("dir/game.exe")]
    [InlineData("game .exe")]
    [InlineData(" game.exe")]
    [InlineData("game..x.exe")]
    [InlineData("game.exe:stream.exe")]
    [InlineData("ga\0me.exe")]
    [InlineData("ga\nme.exe")]
    [InlineData("game*.exe")]
    [InlineData("game\".exe")]
    [InlineData("con.exe")]
    [InlineData("NUL.exe")]
    public void Hostile_names_are_refused(string? name) => Assert.False(PriorityRules.IsValidExeName(name));

    [Fact]
    public void Very_long_names_are_refused() => Assert.False(PriorityRules.IsValidExeName(new string('a', 200) + ".exe"));

    [Theory]
    [InlineData("svchost.exe")]
    [InlineData("LSASS.EXE")]
    [InlineData("csrss.exe")]
    [InlineData("explorer.exe")]
    [InlineData("dwm.exe")]
    [InlineData("audiodg.exe")]
    [InlineData("MsMpEng.exe")]
    [InlineData("Lycoris.exe")]
    public void Windows_programs_and_lycoris_are_refused(string name) => Assert.NotNull(PriorityRules.WhyNot(name, _ => false));

    [Theory]
    [InlineData("EasyAntiCheat.exe")]
    [InlineData("EasyAntiCheat_EOS.exe")]
    [InlineData("BEService.exe")]
    [InlineData("vgc.exe")]
    [InlineData("FACEIT.exe")]
    public void Anti_cheat_programs_are_refused(string name) => Assert.NotNull(PriorityRules.WhyNot(name, _ => false));

    [Fact]
    public void A_name_that_exists_in_the_windows_folder_is_refused()
    {
        Assert.NotNull(PriorityRules.WhyNot("notepad.exe", n => n.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(PriorityRules.WhyNot("MyGame.exe", _ => false));
        // Unknown (the check threw) = refused.
        Assert.NotNull(PriorityRules.WhyNot("MyGame.exe", _ => throw new IOException()));
    }

    [Fact]
    public void The_real_windows_folder_check_finds_system32_programs() =>
        Assert.True(PriorityRules.IsSystemName("notepad.exe"));

    [Fact]
    public void Offered_levels_never_include_realtime_and_high_and_idle_are_expert()
    {
        Assert.DoesNotContain(4, PriorityRules.OfferedLevels);
        Assert.False(PriorityRules.IsKnownValue(4));
        Assert.Equal([3, 6, 5, 1], PriorityRules.OfferedLevels);
        Assert.True(PriorityRules.IsExpertLevel(PriorityRules.High));
        Assert.True(PriorityRules.IsExpertLevel(PriorityRules.Idle));
        Assert.False(PriorityRules.IsExpertLevel(PriorityRules.AboveNormal));
        Assert.False(PriorityRules.IsExpertLevel(PriorityRules.BelowNormal));
    }

    [Fact]
    public void Rule_value_shape_is_exact()
    {
        Assert.True(PriorityRules.IsRuleValue(Rule("MyGame.exe")));
        Assert.True(PriorityRules.IsRuleValue(Rule("MyGame.exe", existed: true, data: "5")));
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", existed: true, data: "4")), "realtime original");
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", existed: true, data: "x")));
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", existed: true, kind: RegistryValueKind.String, data: "3")));
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", name: "Debugger")));
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", name: "IoPriority")));
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", hive: RegHive.HKCU)));
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", path: $@"{PriorityRules.IfeoPath}\MyGame.exe")));
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", path: $@"{PriorityRules.IfeoPath}\MyGame.exe\PerfOptions\x")));
        Assert.False(PriorityRules.IsRuleValue(Rule("MyGame.exe", path: $@"{PriorityRules.IfeoPath}\MyGame.exe\FilterFullPath")));
        Assert.False(PriorityRules.IsRuleValue(Rule("svchost.exe")));
        Assert.False(PriorityRules.IsRuleValue(Rule(@"..\MyGame.exe")));
    }

    [Fact]
    public void Ifeo_stays_forbidden_except_the_rule_value()
    {
        Assert.False(ChangeJournal.IsForbiddenRestoreTarget(Rule("MyGame.exe")));
        Assert.True(ChangeJournal.IsForbiddenRestoreTarget(Rule("MyGame.exe", name: "Debugger", path: $@"{PriorityRules.IfeoPath}\MyGame.exe")));
        Assert.True(ChangeJournal.IsForbiddenRestoreTarget(Rule("MyGame.exe", name: "IoPriority")));
        Assert.True(ChangeJournal.IsForbiddenRestoreTarget(Rule("sethc.exe", name: "Debugger", path: $@"{PriorityRules.IfeoPath}\sethc.exe")));
        Assert.True(ChangeJournal.IsForbiddenRestoreTarget(Rule("lsass.exe")));
        Assert.True(ChangeJournal.IsForbiddenRestoreTarget(Rule("MyGame.exe", existed: true, data: "4")));
    }

    private static ChangeRecord Record(RegistryValueSnapshot reg, string source = PriorityRules.JournalSource) => new()
    {
        Source = source, Kind = ChangeKind.Registry, Target = $@"reg:{reg.Hive}\{reg.Path}\{reg.Name}", Registry = reg,
    };

    [Fact]
    public void Journal_accepts_the_rule_only_from_the_games_page_and_only_its_own_keys()
    {
        Assert.Null(JournalTrust.Check(Record(Rule("MyGame.exe"))));
        Assert.Null(JournalTrust.Check(Record(Rule("MyGame.exe", root: $@"{PriorityRules.IfeoPath}\MyGame.exe"))));
        Assert.Null(JournalTrust.Check(Record(Rule("MyGame.exe", root: PriorityRules.KeyPath("MyGame.exe")))));
        Assert.NotNull(JournalTrust.Check(Record(Rule("MyGame.exe"), source: "tweak:some-tweak")));
        Assert.NotNull(JournalTrust.Check(Record(Rule("MyGame.exe"), source: "startup")));
        // The created root may never be the whole IFEO key or above it.
        Assert.NotNull(JournalTrust.Check(Record(Rule("MyGame.exe", root: PriorityRules.IfeoPath))));
        Assert.NotNull(JournalTrust.Check(Record(Rule("MyGame.exe", root: @"SOFTWARE\Microsoft\Windows NT"))));
        Assert.NotNull(JournalTrust.Check(Record(Rule("MyGame.exe", name: "Debugger", path: $@"{PriorityRules.IfeoPath}\MyGame.exe"))));
    }

    [Fact]
    public void Decide_reverts_lycoris_own_record_when_its_original_is_wanted()
    {
        Assert.Equal(PriorityChange.None, PriorityRules.Decide(3, 3, null));
        Assert.Equal(PriorityChange.None, PriorityRules.Decide(null, null, null));
        Assert.Equal(PriorityChange.Write, PriorityRules.Decide(3, null, null));
        Assert.Equal(PriorityChange.Write, PriorityRules.Decide(null, 5, null)); // another program's rule: deleted through the journal
        Assert.Equal(PriorityChange.Revert, PriorityRules.Decide(null, 3, Rule("MyGame.exe")));
        Assert.Equal(PriorityChange.Revert, PriorityRules.Decide(5, 3, Rule("MyGame.exe", existed: true, data: "5")));
        Assert.Equal(PriorityChange.Write, PriorityRules.Decide(null, 3, Rule("MyGame.exe", existed: true, data: "5")));
        Assert.Equal(PriorityChange.Write, PriorityRules.Decide(6, 3, Rule("MyGame.exe")));
    }

    [Fact]
    public void Levels_have_names_in_both_languages()
    {
        Assert.Equal("Высокий", PriorityRules.Describe(PriorityRules.High));
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("High", PriorityRules.Describe(PriorityRules.High));
        Assert.Equal("Below normal", PriorityRules.Describe(PriorityRules.BelowNormal));
    }
}

/// <summary>
/// Rules against a redirected registry (HKCU\Software\Lycoris-Tests\Redirect-*) and a temp journal: never the real IFEO key.
/// Add → remove must leave nothing behind; another program's rule comes back exactly on revert.
/// </summary>
[Collection("Registry")]
public sealed class PriorityRuleRegistryTests : IDisposable
{
    private const string Exe = "LycorisPriorityTestGame.exe";
    private readonly string _root = $@"Software\Lycoris-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"lycoris-prioritytest-{Guid.NewGuid():N}.json");

    public PriorityRuleRegistryTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
        // The IFEO key exists on every Windows; the program's own key usually does not.
        RegistryHelper.Write(RegHive.HKLM, PriorityRules.IfeoPath, "Sentinel", RegistryValueKind.String, "keep");
    }

    public void Dispose()
    {
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); } catch { }
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    private ChangeJournal Journal() => new(_journalPath);
    private static object? Read() => RegistryHelper.Read(RegHive.HKLM, PriorityRules.KeyPath(Exe), PriorityRules.ValueName);

    private async Task<PriorityRuleOutcome> Set(int? level, bool expert = true, string exe = Exe) =>
        await PriorityRuleStore.SetAsync(exe, level, Journal(), _ => false, expert);

    [Fact]
    public async Task Add_then_remove_leaves_nothing_behind()
    {
        var added = await Set(PriorityRules.High);
        Assert.True(added.Success, string.Join("; ", added.Errors));
        Assert.Equal(3, Read());
        var rule = Assert.Single(PriorityRuleStore.ReadAll(Journal()));
        Assert.Equal(Exe, rule.Exe);
        Assert.True(rule.ByLycoris);
        var record = Assert.Single(Journal().ActiveRecords);
        Assert.Equal(PriorityRules.JournalTarget(Exe), record.Target);
        Assert.Equal(PriorityRules.JournalSource, record.Source);

        var removed = await Set(null);
        Assert.True(removed.Success, string.Join("; ", removed.Errors));
        Assert.Null(Read());
        Assert.False(RegistryHelper.KeyExists(RegHive.HKLM, $@"{PriorityRules.IfeoPath}\{Exe}"));
        Assert.Equal("keep", RegistryHelper.ReadString(RegHive.HKLM, PriorityRules.IfeoPath, "Sentinel"));
        Assert.Empty(Journal().ActiveRecords);
        Assert.Empty(PriorityRuleStore.ReadAll(Journal()));
    }

    [Fact]
    public async Task Changing_a_level_and_reverting_all_brings_back_the_other_programs_rule_exactly()
    {
        RegistryHelper.Write(RegHive.HKLM, PriorityRules.KeyPath(Exe), PriorityRules.ValueName, RegistryValueKind.DWord, 5);
        RegistryHelper.Write(RegHive.HKLM, PriorityRules.KeyPath(Exe), "IoPriority", RegistryValueKind.DWord, 1);
        var foreign = Assert.Single(PriorityRuleStore.ReadAll(Journal()));
        Assert.False(foreign.ByLycoris);

        Assert.True((await Set(PriorityRules.AboveNormal, expert: false)).Success);
        Assert.Equal(6, Read());

        var result = await Journal().RevertAllAsync();
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(5, Read());
        Assert.Equal(1, RegistryHelper.Read(RegHive.HKLM, PriorityRules.KeyPath(Exe), "IoPriority"));
    }

    [Fact]
    public async Task Removing_another_programs_rule_is_journaled_and_undoable()
    {
        RegistryHelper.Write(RegHive.HKLM, PriorityRules.KeyPath(Exe), PriorityRules.ValueName, RegistryValueKind.DWord, 3);
        Assert.True((await Set(null)).Success);
        Assert.Null(Read());
        Assert.Single(Journal().ActiveRecords);

        Assert.True((await Journal().RevertAllAsync()).Success);
        Assert.Equal(3, Read());
    }

    [Fact]
    public async Task Expert_levels_are_refused_in_simple_mode_and_nothing_changes()
    {
        var outcome = await Set(PriorityRules.High, expert: false);
        Assert.False(outcome.Success);
        Assert.Null(Read());
        Assert.Empty(Journal().ActiveRecords);
        Assert.False((await Set(PriorityRules.Idle, expert: false)).Success);
    }

    [Theory]
    [InlineData("svchost.exe")]
    [InlineData("EasyAntiCheat.exe")]
    [InlineData(@"..\x.exe")]
    public async Task Refused_names_write_nothing(string exe)
    {
        var outcome = await Set(PriorityRules.AboveNormal, exe: exe);
        Assert.False(outcome.Success);
        Assert.Empty(Journal().ActiveRecords);
        Assert.Empty(RegistryHelper.SubKeyNames(RegHive.HKLM, PriorityRules.IfeoPath));
    }

    [Fact]
    public async Task Realtime_and_unknown_levels_are_never_written()
    {
        Assert.False((await Set(4)).Success);
        Assert.False((await Set(2)).Success);
        Assert.Null(Read());
    }

    [Fact]
    public async Task An_unusual_existing_value_is_left_alone()
    {
        RegistryHelper.Write(RegHive.HKLM, PriorityRules.KeyPath(Exe), PriorityRules.ValueName, RegistryValueKind.DWord, 4);
        Assert.False((await Set(PriorityRules.AboveNormal)).Success);
        Assert.Equal(4, Read());
        RegistryHelper.Write(RegHive.HKLM, PriorityRules.KeyPath(Exe), PriorityRules.ValueName, RegistryValueKind.String, "3");
        Assert.False((await Set(PriorityRules.AboveNormal)).Success);
        Assert.Equal("3", Read());
        Assert.Empty(Journal().ActiveRecords);
    }

    [Fact]
    public async Task Other_ifeo_values_are_never_touched()
    {
        RegistryHelper.Write(RegHive.HKLM, $@"{PriorityRules.IfeoPath}\{Exe}", "MitigationOptions", RegistryValueKind.QWord, 0x100L);
        Assert.True((await Set(PriorityRules.BelowNormal)).Success);
        Assert.True((await Set(null)).Success);
        Assert.Null(Read());
        Assert.Equal(0x100L, RegistryHelper.Read(RegHive.HKLM, $@"{PriorityRules.IfeoPath}\{Exe}", "MitigationOptions"));
    }
}
