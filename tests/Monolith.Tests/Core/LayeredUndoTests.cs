using System.IO;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Restore;
using Monolith.Tweaks;

namespace Monolith.Tests.Core;

/// <summary>
/// The layered undo model, as pure logic: an in-memory journal and a fake system side. "Revert one thing → exactly
/// that thing reverts; revert everything → the state from before MONOLITH", in any order, per Windows account.
/// </summary>
[Collection("Registry")] // changes the process-wide test account (UserScope.CurrentSidForTests)
public sealed class LayeredUndoTests : IDisposable
{
    private const string Alice = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private const string Bob = "S-1-5-21-1000000001-1000000002-1000000003-1002";
    private const string Path1 = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
    private const string Path2 = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";

    private string _sid = Alice;
    private readonly FakeSystem _system = new();
    private readonly ChangeJournal _journal;

    public LayeredUndoTests()
    {
        UserScope.CurrentSidForTests = () => _sid;
        _journal = ChangeJournal.ForTests();
        _journal.SystemAccess = _system;
    }

    public void Dispose() => UserScope.CurrentSidForTests = null;

    // ───────────────────────────── Fake system ─────────────────────────────

    /// <summary>One value per target (null = absent), the active plan and the existing plans.</summary>
    private sealed class FakeSystem : IJournalSystem
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ChangeRecord> Restored { get; } = [];
        public Guid Active { get; set; } = PowerSchemes.Balanced;
        public HashSet<Guid> Plans { get; } = [PowerSchemes.Balanced, PowerSchemes.HighPerformance];
        public List<Guid> Deleted { get; } = [];

        public Task<RevertOutcome> RestoreAsync(ChangeRecord r)
        {
            Restored.Add(r);
            switch (r.Kind)
            {
                case ChangeKind.Registry:
                    Values[r.Target] = r.Registry!.ValueExisted ? r.Registry.Data : null;
                    return Task.FromResult(RevertOutcome.Done);
                case ChangeKind.PowerScheme:
                    Active = r.PowerSchemeOriginal is { } o && Plans.Contains(o) ? o : PowerSchemes.Balanced;
                    var gone = new List<Guid>();
                    foreach (var c in ChangeJournal.CreatedSchemes(r))
                    {
                        DeletePowerScheme(c);
                        if (!Plans.Contains(c)) gone.Add(c);
                    }
                    return Task.FromResult(new RevertOutcome(DeletedSchemes: gone));
                default:
                    Values[r.Target] = r.OldValue;
                    return Task.FromResult(RevertOutcome.Done);
            }
        }

        public Guid ActivePowerScheme() => Active;
        public bool? PowerSchemeExists(Guid scheme) => Plans.Contains(scheme);

        public void DeletePowerScheme(Guid scheme)
        {
            if (scheme == Active || PowerSchemes.IsBuiltIn(scheme)) return;
            if (Plans.Remove(scheme)) Deleted.Add(scheme);
        }
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static string RegTarget(RegHive hive, string path, string name) => $@"reg:{hive}\{path}\{name}";

    /// <summary>What ChangeSession.SetRegistry journals and does, against the fake: snapshot → record → write.</summary>
    private ChangeRecord SetDword(ChangeBatch batch, string source, string path, string name, int? value, RegHive hive = RegHive.HKLM)
    {
        var target = RegTarget(hive, path, name);
        _system.Values.TryGetValue(target, out var old);
        var snapshot = new RegistryValueSnapshot
        {
            Hive = hive, Path = path, Name = name, KeyExisted = true, ValueExisted = old is not null,
            Kind = old is null ? default : RegistryValueKind.DWord, Data = old,
        };
        var incoming = new ChangeRecord
        {
            BatchId = batch.Id, Source = source, Title = source, Kind = ChangeKind.Registry, Target = target,
            OldValue = snapshot.Describe(), NewValue = value is null ? "удалено" : $"DWord: {value}", Registry = snapshot,
            UserSid = hive == RegHive.HKCU ? UserScope.CurrentSid : null,
        };
        var (rec, isNew) = _journal.Add(incoming);
        _system.Values[target] = value?.ToString();
        if (!isNew) _journal.MergeInto(rec, incoming);
        return rec;
    }

    private string? Value(string path, string name, RegHive hive = RegHive.HKLM) =>
        _system.Values.GetValueOrDefault(RegTarget(hive, path, name));

    private ChangeRecord SetPlan(ChangeBatch batch, string source, Guid scheme, Guid? created = null)
    {
        if (created is { } c) _system.Plans.Add(c);
        var incoming = new ChangeRecord
        {
            BatchId = batch.Id, Source = source, Title = source, Kind = ChangeKind.PowerScheme, Target = "power:active",
            OldValue = _system.Active.ToString(), NewValue = scheme.ToString(),
            PowerSchemeOriginal = _system.Active, PowerSchemeCreated = created,
        };
        var (rec, _) = _journal.Add(incoming);
        _system.Active = scheme;
        return rec;
    }

    // ───────────────────────────── Layers ─────────────────────────────

    [Fact]
    public async Task Reverting_the_older_of_two_changes_keeps_the_newer_value_and_hands_over_the_original()
    {
        _system.Values[RegTarget(RegHive.HKLM, Path1, "V")] = "7";
        var a = SetDword(_journal.BeginBatch("A"), "tweak:a", Path1, "V", 1);
        var b = SetDword(_journal.BeginBatch("B"), "tweak:b", Path1, "V", 2);
        Assert.NotSame(a, b); // a layer, not a merge
        Assert.Equal("7", a.Registry!.Data);
        Assert.Equal("1", b.Registry!.Data);

        var result = await _journal.RevertSourceAsync("tweak:a");

        Assert.True(result.Success);
        Assert.Equal(1, result.HandedOver);
        Assert.Empty(_system.Restored);             // the system was not touched
        Assert.Equal("2", Value(Path1, "V"));       // B's value stays
        Assert.True(a.Reverted);
        Assert.Equal(b.Id, a.HandedTo);
        Assert.Equal("7", b.Registry!.Data);        // B now holds the pre-A original
        Assert.Equal("DWord: 7", b.OldValue);
        Assert.Null(JournalTrust.Check(b));

        await _journal.RevertSourceAsync("tweak:b");
        Assert.Equal("7", Value(Path1, "V"));
        Assert.Empty(_journal.ActiveRecords);
    }

    [Fact]
    public async Task Reverting_newest_first_steps_back_through_each_layer()
    {
        var a = SetDword(_journal.BeginBatch("A"), "tweak:a", Path1, "V", 1);
        SetDword(_journal.BeginBatch("B"), "tweak:b", Path1, "V", 2);

        await _journal.RevertSourceAsync("tweak:b");
        Assert.Equal("1", Value(Path1, "V"));
        Assert.False(a.Reverted);

        await _journal.RevertSourceAsync("tweak:a");
        Assert.Null(Value(Path1, "V")); // absent before MONOLITH
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public async Task Any_revert_order_leaves_the_newest_remaining_value_and_ends_at_the_original(int first, int second, int third)
    {
        _system.Values[RegTarget(RegHive.HKLM, Path1, "V")] = "9";
        var layers = new[]
        {
            SetDword(_journal.BeginBatch("1"), "tweak:one", Path1, "V", 1),
            SetDword(_journal.BeginBatch("2"), "tweak:two", Path1, "V", 2),
            SetDword(_journal.BeginBatch("3"), "tweak:three", Path1, "V", 3),
        };

        var values = new[] { "1", "2", "3" };
        var remaining = new List<int> { 0, 1, 2 };
        foreach (var i in new[] { first, second, third })
        {
            var result = await _journal.RevertAsync([layers[i]]);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            remaining.Remove(i);
            Assert.Equal(remaining.Count == 0 ? "9" : values[remaining.Max()], Value(Path1, "V"));
        }
        Assert.Empty(_journal.ActiveRecords);
    }

    [Fact]
    public async Task Batch_revert_undoes_exactly_that_batch()
    {
        var batchA = _journal.BeginBatch("A");
        SetDword(batchA, "tweak:a", Path1, "V", 1);
        SetDword(batchA, "tweak:a", Path2, "W", 5);
        var batchB = _journal.BeginBatch("B");
        SetDword(batchB, "tweak:b", Path1, "V", 2);

        Assert.Single(_journal.BatchCandidates(batchB.Id)); // B owns its own record (no merge into A)
        await _journal.RevertBatchAsync(batchB.Id);
        Assert.Equal("1", Value(Path1, "V"));
        Assert.Equal("5", Value(Path2, "W"));
        Assert.Equal(2, _journal.ActiveRecords.Count);

        await _journal.RevertBatchAsync(batchA.Id);
        Assert.Null(Value(Path1, "V"));
        Assert.Null(Value(Path2, "W"));
    }

    [Fact]
    public async Task Reverting_an_older_batch_leaves_the_newer_batch_working()
    {
        var batchA = _journal.BeginBatch("A");
        SetDword(batchA, "tweak:a", Path1, "V", 1);
        var batchB = _journal.BeginBatch("B");
        var b = SetDword(batchB, "tweak:b", Path1, "V", 2);

        await _journal.RevertBatchAsync(batchA.Id);
        Assert.Equal("2", Value(Path1, "V"));
        Assert.False(b.Reverted);

        await _journal.RevertAllAsync();
        Assert.Null(Value(Path1, "V"));
    }

    [Fact]
    public void Same_source_in_the_same_batch_merges_and_keeps_the_first_original()
    {
        _system.Values[RegTarget(RegHive.HKLM, Path1, "V")] = "7";
        var batch = _journal.BeginBatch("A");
        var first = SetDword(batch, "tweak:a", Path1, "V", 1);
        var second = SetDword(batch, "tweak:a", Path1, "V", 2);
        Assert.Same(first, second);
        Assert.Equal("7", first.Registry!.Data);
        Assert.Equal("DWord: 2", first.NewValue);
        Assert.Single(_journal.ActiveRecords);
    }

    [Fact]
    public void Top_layers_are_the_newest_per_target_and_account()
    {
        var a = SetDword(_journal.BeginBatch("A"), "tweak:a", Path1, "V", 1);
        var b = SetDword(_journal.BeginBatch("B"), "tweak:b", Path1, "V", 2);
        var c = SetDword(_journal.BeginBatch("C"), "tweak:c", Path2, "W", 3);
        Assert.Equal([b, c], ChangeJournal.TopLayers(_journal.Records, Alice));
        Assert.Same(b, _journal.NewerLayer(a));
        Assert.Null(_journal.NewerLayer(b));
        Assert.Same(b, _journal.FindActive(a.Target));
        Assert.Equal([a, b], _journal.ActiveLayers(a.Target));

        var covered = RestoreFormat.CoveredBy(_journal.Records, Alice);
        Assert.Same(b, covered[a.Id]);
        Assert.False(covered.ContainsKey(b.Id));
    }

    [Fact]
    public void Drift_is_judged_only_on_the_newest_layer()
    {
        var a = SetDword(_journal.BeginBatch("A"), "tweak:a", Path1, "V", 1);
        var b = SetDword(_journal.BeginBatch("B"), "tweak:b", Path1, "V", 2);
        // The live value is B's: A's NewValue no longer matches on purpose.
        var drift = DriftDetector.Detect(_journal.ActiveRecords, r => new LiveValue("DWord: 2", LiveState.IsDrift(r.Kind, r.NewValue, "DWord: 2")), _ => true);
        Assert.Empty(drift);
        Assert.False(a.Reverted);
        Assert.False(b.Reverted);
    }

    [Fact]
    public async Task Legacy_merged_record_still_reverts_from_every_source_it_names()
    {
        var batch = _journal.BeginBatch("old");
        var rec = SetDword(batch, "tweak:a", Path1, "V", 1);
        rec.AlsoSources.Add("tweak:b"); // shape written by older builds
        Assert.True(rec.IsLegacyMerged);
        Assert.False(ChangeJournal.CanMerge(rec, new ChangeRecord { BatchId = batch.Id, Source = "tweak:a", Kind = ChangeKind.Registry }));

        var result = await _journal.RevertSourceAsync("tweak:b");
        Assert.Equal(1, result.Reverted);
        Assert.Null(Value(Path1, "V"));
    }

    // ───────────────────────────── Accounts ─────────────────────────────

    [Fact]
    public async Task Another_accounts_change_is_never_offered_or_reverted()
    {
        var alices = SetDword(_journal.BeginBatch("A"), "tweak:a", @"Software\Microsoft\GameBar", "X", 1, RegHive.HKCU);
        Assert.Equal(Alice, alices.UserSid);
        Assert.True(_journal.HasActiveSource("tweak:a"));

        _sid = Bob;
        Assert.False(_journal.HasActiveSource("tweak:a"));
        Assert.Null(_journal.FindActive(alices.Target));
        Assert.Empty(_journal.RevertAllCandidates());
        Assert.False(ChangeJournal.IsMine(alices));

        var refused = await _journal.RevertAsync([alices]);
        Assert.Equal(1, refused.Failed);
        Assert.Contains("войдите под ней, чтобы отменить", refused.Errors[0]);
        Assert.StartsWith("Изменение сделано для учётной записи", alices.RevertError);
        Assert.False(alices.Reverted);
        Assert.Empty(_system.Restored);

        // Bob's own change of the same value is its own record with Bob's original — never a layer over Alice's.
        _system.Values[alices.Target] = "4"; // Bob's hive
        var bobs = SetDword(_journal.BeginBatch("B"), "tweak:a", @"Software\Microsoft\GameBar", "X", 1, RegHive.HKCU);
        Assert.NotSame(alices, bobs);
        Assert.Equal(Bob, bobs.UserSid);
        Assert.Null(_journal.NewerLayer(alices));
        Assert.Equal("4", bobs.Registry!.Data);

        await _journal.RevertSourceAsync("tweak:a");
        Assert.True(bobs.Reverted);
        Assert.False(alices.Reverted); // still waiting for Alice
        Assert.Equal("4", Value(@"Software\Microsoft\GameBar", "X", RegHive.HKCU));

        _sid = Alice;
        Assert.True(_journal.HasActiveSource("tweak:a"));
    }

    [Fact]
    public async Task Per_user_record_without_an_account_needs_its_own_row()
    {
        var legacy = new ChangeRecord
        {
            BatchId = _journal.BeginBatch("old").Id, Source = "tweak:a", Title = "old", Kind = ChangeKind.Registry,
            Target = RegTarget(RegHive.HKCU, @"Software\Microsoft\GameBar", "X"),
            Registry = new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = @"Software\Microsoft\GameBar", Name = "X", KeyExisted = true },
        };
        _journal.Add(legacy);
        Assert.True(legacy.IsLegacyPerUser);
        Assert.False(ChangeJournal.IsBulkRevertible(legacy));
        Assert.Empty(_journal.RevertAllCandidates());
        Assert.Empty(_journal.BatchCandidates(legacy.BatchId));
        Assert.True(_journal.HasActiveSource("tweak:a")); // the row still offers it (with a note)
        Assert.Null(JournalTrust.Check(legacy));

        var all = await _journal.RevertAllAsync();
        Assert.Equal(0, all.Reverted);
        Assert.False(legacy.Reverted);

        var row = await _journal.RevertAsync([legacy]);
        Assert.Equal(1, row.Reverted);
    }

    [Fact]
    public void Journal_trust_accepts_account_bound_records_only_where_an_account_makes_sense()
    {
        var reg = new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = @"Software\Microsoft\GameBar", Name = "X" };
        ChangeRecord Reg(RegistryValueSnapshot s, string? sid) => new()
        {
            Source = "tweak:a", Kind = ChangeKind.Registry, Target = RegTarget(s.Hive, s.Path, s.Name), Registry = s, UserSid = sid,
        };
        Assert.Null(JournalTrust.Check(Reg(reg, Alice)));
        Assert.NotNull(JournalTrust.Check(Reg(reg, "S-1-5-21-x; evil")));
        var machine = new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = Path1, Name = "X" };
        Assert.NotNull(JournalTrust.Check(Reg(machine, Alice)));

        var dx = new ChangeRecord
        {
            Source = "tweak:dx", Kind = ChangeKind.Command, Target = "cmd:dx:VRROptimizeEnable", UserSid = Alice,
            RevertFile = TweakCatalog.PowerShellExe, RevertArgs = TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("VRROptimizeEnable", "0")),
        };
        Assert.True(dx.IsPerUser);
        Assert.Null(JournalTrust.Check(dx));
    }

    [Fact]
    public void Per_user_command_targets_are_recognized()
    {
        Assert.True(UserScope.IsPerUserCommand("dx:VRROptimizeEnable"));
        Assert.True(UserScope.IsPerUserCommand("spi:visual-effects"));
        Assert.False(UserScope.IsPerUserCommand("hibernate"));
        Assert.False(UserScope.IsPerUserCommand("powercfg:x"));
        Assert.False(UserScope.IsPerUserCommand("dns4:{x}"));
    }

    // ───────────────────────────── Commands ─────────────────────────────

    [Fact]
    public async Task Hand_over_of_a_command_keeps_a_valid_catalog_undo_command()
    {
        ChangeRecord Dx(ChangeBatch batch, string source, string? original, string value)
        {
            var rec = new ChangeRecord
            {
                BatchId = batch.Id, Source = source, Title = source, Kind = ChangeKind.Command, Target = "cmd:dx:VRROptimizeEnable",
                OldValue = original ?? "не задано", NewValue = value, UserSid = UserScope.CurrentSid,
                RevertFile = TweakCatalog.PowerShellExe,
                RevertArgs = TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("VRROptimizeEnable", original)),
            };
            _journal.Add(rec);
            return rec;
        }
        var a = Dx(_journal.BeginBatch("A"), "tweak:a", null, "1");
        var b = Dx(_journal.BeginBatch("B"), "tweak:b", "1", "0");

        await _journal.RevertAsync([a]);
        Assert.Equal(b.Id, a.HandedTo);
        Assert.Equal(a.RevertArgs, b.RevertArgs);   // B's undo now removes the token, as before A
        Assert.Null(JournalTrust.Check(b));
        var undo = JournalTrust.DxUndo(b);
        Assert.NotNull(undo);
        Assert.Equal("VRROptimizeEnable", undo.Value.Token);
        Assert.Null(undo.Value.Value);
    }

    [Fact]
    public void Undo_commands_are_read_back_for_verification()
    {
        var scheme = Guid.NewGuid(); var sub = Guid.NewGuid(); var setting = Guid.NewGuid();
        var pc = "\"" + ProcessRunner.Sys("powercfg.exe") + "\"";
        var rec = new ChangeRecord
        {
            Source = "tweak:x", Kind = ChangeKind.Command, Target = $"cmd:powercfg:{scheme:D}:{setting:D}:ac",
            RevertFile = ProcessRunner.Sys("cmd.exe"),
            RevertArgs = TweakCatalog.CmdArgs($"{pc} /setacvalueindex {scheme:D} {sub:D} {setting:D} 3 && {pc} /setactive SCHEME_CURRENT"),
        };
        Assert.Equal((scheme, sub, setting, 3u), JournalTrust.PowercfgUndo(rec)!.Value);
        Assert.Equal((scheme, setting), JournalTrust.PowercfgTarget(rec.Target)!.Value);

        var g = $"{{{Guid.NewGuid()}}}";
        var dns = new ChangeRecord
        {
            Source = "tweak:dns", Kind = ChangeKind.Command, Target = $"cmd:dns4:{g}", RevertFile = TweakCatalog.PowerShellExe,
            RevertArgs = TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript("ipv4", g, ["1.1.1.1", "8.8.8.8"])),
        };
        var undo = JournalTrust.DnsUndo(dns);
        Assert.NotNull(undo);
        Assert.False(undo.Value.Ipv6);
        Assert.Equal(g, undo.Value.Adapter);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], undo.Value.Servers);
    }

    [Fact]
    public void Timed_out_or_running_commands_have_an_unknown_outcome()
    {
        Assert.True(ChangeSession.OutcomeUnknown(new ProcessResult(-1, "", "", TimedOut: true, Cancelled: false)));
        Assert.True(ChangeSession.OutcomeUnknown(new ProcessResult(-1, "", "", TimedOut: false, Cancelled: true)));
        Assert.True(ChangeSession.OutcomeUnknown(new ProcessResult(-1, "", "", TimedOut: true, Cancelled: false) { StillRunning = true }));
        Assert.False(ChangeSession.OutcomeUnknown(new ProcessResult(1, "", "boom", TimedOut: false, Cancelled: false)));
        Assert.False(ChangeSession.OutcomeUnknown(ProcessResult.Failed("not started")));
        Assert.False(ChangeSession.OutcomeUnknown(null));
    }

    // ───────────────────────────── Power plans ─────────────────────────────

    [Fact]
    public async Task Ending_a_game_session_never_undoes_a_plan_tweak_applied_during_it()
    {
        var ultimate = Guid.NewGuid();
        var session = SetPlan(_journal.BeginBatch("game"), "gameboost", PowerSchemes.HighPerformance);
        var tweak = SetPlan(_journal.BeginBatch("optimize"), "tweak:power-plan-ultimate", ultimate, created: ultimate);

        await _journal.RevertSourceAsync("gameboost"); // the session ends
        Assert.True(session.Reverted);
        Assert.Equal(ultimate, _system.Active);           // the tweak keeps working
        Assert.Contains(ultimate, _system.Plans);         // and its copy is kept
        Assert.Equal(PowerSchemes.Balanced, tweak.PowerSchemeOriginal);
        Assert.Null(JournalTrust.Check(tweak));

        await _journal.RevertSourceAsync("tweak:power-plan-ultimate");
        Assert.Equal(PowerSchemes.Balanced, _system.Active);
        Assert.DoesNotContain(ultimate, _system.Plans);
    }

    [Fact]
    public async Task Reverting_the_plan_tweak_under_a_running_session_deletes_its_unused_copy()
    {
        var ultimate = Guid.NewGuid();
        var tweak = SetPlan(_journal.BeginBatch("optimize"), "tweak:power-plan-ultimate", ultimate, created: ultimate);
        var session = SetPlan(_journal.BeginBatch("game"), "gameboost", PowerSchemes.HighPerformance);

        await _journal.RevertAsync([tweak]);
        Assert.Equal(session.Id, tweak.HandedTo);
        Assert.Equal(PowerSchemes.HighPerformance, _system.Active); // the session still runs
        Assert.Contains(ultimate, _system.Deleted);                 // the copy is not in use: gone now
        Assert.Equal(PowerSchemes.Balanced, session.PowerSchemeOriginal);
        Assert.Null(session.PowerSchemeCreated);                    // a session record never owns plans
        Assert.Null(JournalTrust.Check(session));

        await _journal.RevertSourceAsync("gameboost");
        Assert.Equal(PowerSchemes.Balanced, _system.Active);
    }

    [Fact]
    public async Task A_plan_copy_still_in_use_moves_to_the_newer_layer()
    {
        var ultimate = Guid.NewGuid();
        var first = SetPlan(_journal.BeginBatch("1"), "tweak:power-plan-ultimate", ultimate, created: ultimate);
        _system.Active = PowerSchemes.Balanced; // switched away outside MONOLITH, then re-applied (copy found, not created)
        var second = SetPlan(_journal.BeginBatch("2"), "tweak:power-plan-ultimate", ultimate);

        await _journal.RevertAsync([first]);
        Assert.Equal(ultimate, second.PowerSchemeCreated);
        Assert.Contains(ultimate, _system.Plans);

        await _journal.RevertAsync([second]);
        Assert.DoesNotContain(ultimate, _system.Plans);
    }

    [Fact]
    public async Task Settings_of_a_deleted_plan_copy_are_closed_with_it()
    {
        var ultimate = Guid.NewGuid();
        var plan = SetPlan(_journal.BeginBatch("1"), "tweak:power-plan-ultimate", ultimate, created: ultimate);
        var setting = Guid.NewGuid();
        var pc = "\"" + ProcessRunner.Sys("powercfg.exe") + "\"";
        var usb = new ChangeRecord
        {
            BatchId = _journal.BeginBatch("2").Id, Source = "tweak:usb", Title = "usb", Kind = ChangeKind.Command,
            Target = $"cmd:powercfg:{ultimate:D}:{setting:D}:ac", RevertFile = ProcessRunner.Sys("cmd.exe"),
            RevertArgs = TweakCatalog.CmdArgs($"{pc} /setacvalueindex {ultimate:D} {Guid.NewGuid():D} {setting:D} 1 && {pc} /setactive SCHEME_CURRENT"),
        };
        _journal.Add(usb);

        var result = await _journal.RevertAsync([plan]);
        Assert.True(result.Success);
        Assert.True(usb.Reverted);
        Assert.NotNull(usb.RevertNote);
        Assert.DoesNotContain(usb, _system.Restored);
    }

    [Fact]
    public void Journal_trust_refuses_built_in_or_foreign_plans_to_delete()
    {
        ChangeRecord Plan(string source, Guid? created, List<Guid>? more = null) => new()
        {
            Source = source, Kind = ChangeKind.PowerScheme, Target = "power:active", PowerSchemeOriginal = PowerSchemes.Balanced,
            PowerSchemeCreated = created, PowerSchemesCreatedMore = more,
        };
        Assert.Null(JournalTrust.Check(Plan("tweak:power-plan-ultimate", Guid.NewGuid(), [Guid.NewGuid()])));
        Assert.NotNull(JournalTrust.Check(Plan("gameboost", Guid.NewGuid())));
        Assert.NotNull(JournalTrust.Check(Plan("gameboost", null, [Guid.NewGuid()])));
        Assert.NotNull(JournalTrust.Check(Plan("tweak:power-plan-ultimate", PowerSchemes.Balanced)));
        Assert.NotNull(JournalTrust.Check(Plan("tweak:power-plan-ultimate", null, [PowerSchemes.UltimateTemplate])));
    }

    // ───────────────────────────── Verification and storage ─────────────────────────────

    [Fact]
    public async Task Revert_reads_the_value_back_and_marks_mismatches()
    {
        var ok = SetDword(_journal.BeginBatch("A"), "tweak:a", Path1, "V", 1);
        var bad = SetDword(_journal.BeginBatch("B"), "tweak:b", Path2, "W", 1);

        var result = await _journal.RevertAllAsync(r => ReferenceEquals(r, bad)
            ? new RevertCheck(false, "DWord: 1", "значения нет")
            : new RevertCheck(true));

        Assert.True(result.Success);
        Assert.Equal(1, result.Confirmed);
        Assert.Single(result.Mismatches);
        Assert.Equal("сейчас DWord: 1, ожидалось значения нет", bad.RevertMismatch);
        Assert.NotNull(ok.RevertVerifiedUtc);
        Assert.Null(ok.RevertMismatch);
        Assert.Equal("Откат подтверждён: 1, не совпадает с исходным: 1.", result.VerificationText);
        Assert.Contains("Не совпадает: сейчас DWord: 1", RestoreFormat.RecordNote(bad, Alice, null, null));
        Assert.Contains("Откат подтверждён", RestoreFormat.RecordNote(ok, Alice, null, null));
    }

    [Fact]
    public void Record_notes_explain_accounts_and_layers()
    {
        var a = SetDword(_journal.BeginBatch("A"), "tweak:a", @"Software\Microsoft\GameBar", "X", 1, RegHive.HKCU);
        var b = SetDword(_journal.BeginBatch("B"), "tweak:b", @"Software\Microsoft\GameBar", "X", 2, RegHive.HKCU);
        Assert.Contains($"более позднее изменение «{b.Title}»", RestoreFormat.RecordNote(a, Alice, b, null));
        Assert.Equal(
            "Изменение сделано для учётной записи PC\\alice; войдите под ней, чтобы отменить.",
            RestoreFormat.RecordNote(b, Bob, null, null, _ => "PC\\alice"));
        a.Reverted = true;
        a.HandedTo = b.Id;
        Assert.StartsWith($"Откат передан: значение задаёт более позднее изменение «{b.Title}»", RestoreFormat.RecordNote(a, Alice, null, b.Title));
    }

    [Fact]
    public async Task Layers_accounts_and_created_roots_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"monolith-layers-{Guid.NewGuid():N}.json");
        try
        {
            var journal = ChangeJournal.ForTests(path);
            journal.SystemAccess = _system;
            var batch = journal.BeginBatch("A");
            var snapshot = new RegistryValueSnapshot
            {
                Hive = RegHive.HKCU, Path = @"Software\Policies\Microsoft\Windows\WindowsAI", Name = "X",
                CreatedKeyRoot = @"Software\Policies\Microsoft\Windows",
            };
            journal.Add(new ChangeRecord
            {
                BatchId = batch.Id, Source = "tweak:a", Title = "a", Kind = ChangeKind.Registry, UserSid = Alice,
                Target = RegTarget(snapshot.Hive, snapshot.Path, snapshot.Name), Registry = snapshot,
            });
            journal.Add(new ChangeRecord
            {
                BatchId = journal.BeginBatch("B").Id, Source = "tweak:b", Title = "b", Kind = ChangeKind.Registry, UserSid = Alice,
                Target = RegTarget(snapshot.Hive, snapshot.Path, snapshot.Name), Registry = snapshot,
            });

            var reloaded = ChangeJournal.ForTests(path);
            reloaded.SystemAccess = _system;
            Assert.Equal(2, reloaded.ActiveRecords.Count);
            Assert.All(reloaded.Records, r => Assert.Equal(Alice, r.UserSid));
            Assert.Equal(@"Software\Policies\Microsoft\Windows", reloaded.Records[0].Registry!.CreatedKeyRoot);

            await reloaded.RevertAsync([reloaded.Records[0]]);
            var again = ChangeJournal.ForTests(path);
            Assert.True(again.Records[0].Reverted);   // saved right after the record, not only at the end
            Assert.Equal(again.Records[1].Id, again.Records[0].HandedTo);
        }
        finally
        {
            foreach (var f in new[] { path, path + ".bak", path + ".tmp" })
                try { File.Delete(f); } catch { }
        }
    }

    // ───────────────────────────── Created keys ─────────────────────────────

    [Fact]
    public void A_write_into_a_key_monolith_created_inherits_the_created_root()
    {
        var batch = _journal.BeginBatch("A");
        _journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = "tweak:a", Title = "a", Kind = ChangeKind.Registry,
            Target = RegTarget(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "Off"),
            Registry = new RegistryValueSnapshot
            {
                Hive = RegHive.HKLM, Path = @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", Name = "Off",
                CreatedKeyRoot = @"SOFTWARE\Policies\Microsoft\Windows",
            },
        });
        // A sibling key under the created parent, the same key, and another hive.
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\Windows", _journal.InheritedKeyRoot(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", null));
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\Windows", _journal.InheritedKeyRoot(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", null));
        Assert.Null(_journal.InheritedKeyRoot(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\WindowsStore", null));
        Assert.Null(_journal.InheritedKeyRoot(RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", Alice));

        // Older records count with their own key only.
        _journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = "tweak:b", Title = "b", Kind = ChangeKind.Registry,
            Target = RegTarget(RegHive.HKLM, @"SOFTWARE\Legacy\Created", "X"),
            Registry = new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = @"SOFTWARE\Legacy\Created", Name = "X", KeyExisted = false },
        });
        Assert.Equal(@"SOFTWARE\Legacy\Created", _journal.InheritedKeyRoot(RegHive.HKLM, @"SOFTWARE\Legacy\Created", null));
        Assert.Null(_journal.InheritedKeyRoot(RegHive.HKLM, @"SOFTWARE\Legacy", null));
    }

    [Fact]
    public void Reverted_records_pass_on_no_created_root()
    {
        var rec = new ChangeRecord
        {
            BatchId = _journal.BeginBatch("A").Id, Source = "tweak:a", Title = "a", Kind = ChangeKind.Registry,
            Target = RegTarget(RegHive.HKLM, @"SOFTWARE\Policies\X\Y", "V"),
            Registry = new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = @"SOFTWARE\Policies\X\Y", Name = "V", CreatedKeyRoot = @"SOFTWARE\Policies\X" },
        };
        _journal.Add(rec);
        rec.Reverted = true;
        Assert.Null(_journal.InheritedKeyRoot(RegHive.HKLM, @"SOFTWARE\Policies\X\Y", null));
    }

    [Fact]
    public void Journal_trust_checks_the_created_root()
    {
        ChangeRecord Rec(string? root) => new()
        {
            Source = "tweak:a", Kind = ChangeKind.Registry, Target = RegTarget(RegHive.HKLM, Path1, "V"),
            Registry = new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = Path1, Name = "V", CreatedKeyRoot = root },
        };
        Assert.Null(JournalTrust.Check(Rec(null)));
        Assert.Null(JournalTrust.Check(Rec(@"SOFTWARE\Policies\Microsoft\Windows")));
        Assert.Null(JournalTrust.Check(Rec(Path1)));
        Assert.NotNull(JournalTrust.Check(Rec("SOFTWARE")));                              // a top-level key
        Assert.NotNull(JournalTrust.Check(Rec(@"SOFTWARE\Microsoft")));                   // not a parent of the path
        Assert.NotNull(JournalTrust.Check(Rec(@"SOFTWARE\Policies\Microsoft\Win")));      // not on a segment boundary
        Assert.NotNull(JournalTrust.Check(Rec(@"SOFTWARE\Policies\Microsoft\Windows Defender"))); // not a parent, and protected
    }
}

/// <summary>Created-key cleanup as pure path logic over an in-memory key tree (any revert order, pre-existing parents kept).</summary>
public sealed class CreatedKeyTests
{
    /// <summary>key → value names; a key's subkeys are the keys below it.</summary>
    private sealed class Tree
    {
        private readonly Dictionary<string, HashSet<string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public Tree(params string[] keys)
        {
            foreach (var k in keys) Create(k);
        }

        public void Create(string key)
        {
            var parts = key.Split('\\');
            for (var i = 1; i <= parts.Length; i++) _keys.TryAdd(string.Join('\\', parts.Take(i)), []);
        }

        public bool Exists(string key) => _keys.ContainsKey(key);
        public void Write(string key, string value) { Create(key); _keys[key].Add(value); }
        public void DeleteValue(string key, string value) { if (_keys.TryGetValue(key, out var v)) v.Remove(value); }

        public bool? IsEmpty(string key) => !_keys.TryGetValue(key, out var values)
            ? null
            : values.Count == 0 && !_keys.Keys.Any(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase));

        public void Delete(string key)
        {
            Assert.True(IsEmpty(key) == true, $"deleting non-empty key {key}");
            _keys.Remove(key);
        }

        /// <summary>What RegistryHelper.Restore does for a value that did not exist.</summary>
        public void Restore(string path, string value, string? root, bool keyExisted = true)
        {
            DeleteValue(path, value);
            if (root is not null) RegistryHelper.DeleteEmptyChain(path, root, IsEmpty, Delete);
            else if (!keyExisted) RegistryHelper.DeleteEmptyChain(path, path, IsEmpty, Delete);
        }
    }

    private const string Policies = @"Software\Policies\Microsoft";
    private const string Ai = Policies + @"\Windows\WindowsAI";

    [Fact]
    public void Shallowest_missing_key_is_the_created_root()
    {
        var tree = new Tree(Policies);
        Assert.Equal(Policies + @"\Windows", RegistryHelper.ShallowestMissing(Ai, tree.Exists));
        Assert.Null(RegistryHelper.ShallowestMissing(Policies, tree.Exists));
        Assert.Equal(@"Software\Microsoft\Siuf", RegistryHelper.ShallowestMissing(@"Software\Microsoft\Siuf\Rules", new Tree(@"Software\Microsoft").Exists));
        Assert.Equal(@"Nothing\Here", RegistryHelper.ShallowestMissing(@"Nothing\Here\At\All", new Tree().Exists)); // never a top-level key
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_values_in_a_created_key_remove_it_in_either_revert_order(bool firstCreatorFirst)
    {
        var tree = new Tree(Policies);
        var root = RegistryHelper.ShallowestMissing(Ai, tree.Exists)!;
        tree.Write(Ai, "DisableAIDataAnalysis"); // record A: created root
        tree.Write(Ai, "DisableClickToDo");      // record B: inherits the root (the key existed by then)

        if (firstCreatorFirst)
        {
            tree.Restore(Ai, "DisableAIDataAnalysis", root);
            Assert.True(tree.Exists(Ai));
            tree.Restore(Ai, "DisableClickToDo", root);
        }
        else
        {
            tree.Restore(Ai, "DisableClickToDo", root);
            Assert.True(tree.Exists(Ai));
            tree.Restore(Ai, "DisableAIDataAnalysis", root);
        }
        Assert.False(tree.Exists(Ai));
        Assert.False(tree.Exists(Policies + @"\Windows"));
        Assert.True(tree.Exists(Policies)); // existed before MONOLITH
    }

    [Fact]
    public void Sibling_keys_under_a_created_parent_are_all_removed()
    {
        var tree = new Tree(Policies);
        var copilot = Policies + @"\Windows\WindowsCopilot";
        var rootA = RegistryHelper.ShallowestMissing(copilot, tree.Exists)!;
        tree.Write(copilot, "Off");
        var rootB = RegistryHelper.ShallowerRoot(RegistryHelper.ShallowestMissing(Ai, tree.Exists), rootA)!; // inherited
        tree.Write(Ai, "Off");
        Assert.Equal(rootA, rootB);

        tree.Restore(copilot, "Off", rootA);
        Assert.True(tree.Exists(Policies + @"\Windows"));
        tree.Restore(Ai, "Off", rootB);
        Assert.False(tree.Exists(Policies + @"\Windows"));
        Assert.True(tree.Exists(Policies));
    }

    [Fact]
    public void Empty_parents_that_existed_before_are_kept()
    {
        var tree = new Tree(@"Software\Microsoft\Empty\Parent");
        var path = @"Software\Microsoft\Empty\Parent\New";
        var root = RegistryHelper.ShallowestMissing(path, tree.Exists);
        tree.Write(path, "X");
        tree.Restore(path, "X", root);
        Assert.False(tree.Exists(path));
        Assert.True(tree.Exists(@"Software\Microsoft\Empty\Parent")); // empty, but not MONOLITH's
    }

    [Fact]
    public void A_key_someone_else_filled_is_kept()
    {
        var tree = new Tree(@"Software\Microsoft");
        var path = @"Software\Microsoft\Siuf\Rules";
        var root = RegistryHelper.ShallowestMissing(path, tree.Exists);
        tree.Write(path, "NumberOfSIUFInPeriod");
        tree.Write(@"Software\Microsoft\Siuf", "WrittenByWindows");
        tree.Restore(path, "NumberOfSIUFInPeriod", root);
        Assert.False(tree.Exists(path));
        Assert.True(tree.Exists(@"Software\Microsoft\Siuf"));
    }

    [Fact]
    public void Older_records_without_a_root_remove_only_their_empty_leaf()
    {
        var tree = new Tree(@"Software\Microsoft");
        var path = @"Software\Microsoft\Siuf\Rules";
        tree.Write(path, "X");
        tree.Restore(path, "X", root: null, keyExisted: false);
        Assert.False(tree.Exists(path));
        Assert.True(tree.Exists(@"Software\Microsoft\Siuf")); // never walks up without a recorded root
    }

    [Fact]
    public void Walk_never_leaves_the_path_or_goes_above_the_root()
    {
        var tree = new Tree(@"A\B\C\D");
        var deleted = new List<string>();
        RegistryHelper.DeleteEmptyChain(@"A\B\C\D", @"A\X", tree.IsEmpty, deleted.Add); // root not a parent: nothing
        Assert.Empty(deleted);
        RegistryHelper.DeleteEmptyChain(@"A\B\C\D", @"A\B\C", tree.IsEmpty, k => { deleted.Add(k); tree.Delete(k); });
        Assert.Equal([@"A\B\C\D", @"A\B\C"], deleted);
        Assert.True(tree.Exists(@"A\B"));
    }

    [Theory]
    [InlineData(@"SOFTWARE\Policies\Microsoft", @"SOFTWARE\Policies\Microsoft\Windows", true)]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows", @"SOFTWARE\Policies\Microsoft\Windows", true)]
    [InlineData(@"software\policies", @"SOFTWARE\Policies\Microsoft", true)]
    [InlineData(@"SOFTWARE\Policies\Micro", @"SOFTWARE\Policies\Microsoft", false)]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows", @"SOFTWARE\Policies\Microsoft", false)]
    public void Ancestor_test_uses_whole_segments(string ancestor, string path, bool expected) =>
        Assert.Equal(expected, RegistryHelper.IsSameOrAncestor(ancestor, path));
}
