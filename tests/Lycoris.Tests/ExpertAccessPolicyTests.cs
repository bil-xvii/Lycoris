using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Shell;
using Lycoris.Tweaks;
using Lycoris.UI;
using Microsoft.Win32;

namespace Lycoris.Tests;

[CollectionDefinition("ExpertAccessPolicy", DisableParallelization = true)]
public sealed class ExpertAccessPolicyCollection { }

[Collection("ExpertAccessPolicy")]
public sealed class ExpertAccessPolicyTests : IDisposable
{
    private const string Account = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private const string OtherAccount = "S-1-5-21-1000000001-1000000002-1000000003-1002";
    private const string Machine = "01234567-89ab-cdef-0123-456789abcdef";
    private const string OtherMachine = "01234567-89ab-cdef-0123-456789abcdee";
    private const string FixtureFingerprint = "49C4DBE56FB430EEB1D12829AD57D956DAAA1C5558FFEDDEBD93844253A975BE";
    private static readonly PropertyInfo SettingsProperty = typeof(AppSettings).GetProperty(nameof(AppSettings.Current))!;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private readonly AppSettings _previousSettings = AppSettings.Current;
    private readonly AppSettings _settings = new();
    private readonly bool _previousElevationBypass = ChangeSession.BypassElevationForTests;

    public ExpertAccessPolicyTests() => SettingsProperty.SetValue(null, _settings);

    public void Dispose()
    {
        SettingsProperty.SetValue(null, _previousSettings);
        ChangeSession.BypassElevationForTests = _previousElevationBypass;
    }

    private static IDisposable AsOwner() =>
        ExpertAccessPolicy.OverrideFingerprintForTests(() => ExpertAccessPolicy.OwnerFingerprint);

    private static IDisposable AsOtherAccount() =>
        ExpertAccessPolicy.OverrideFingerprintForTests(() => ExpertAccessPolicy.ComputeFingerprint(OtherAccount, Machine));

    [Fact]
    public void Fingerprint_matches_the_pair_and_changes_with_either_identity()
    {
        Assert.Equal(FixtureFingerprint, ExpertAccessPolicy.ComputeFingerprint(Account, Machine));
        Assert.Equal(FixtureFingerprint, ExpertAccessPolicy.ComputeFingerprint(Account, " {01234567-89AB-CDEF-0123-456789ABCDEF} "));
        Assert.NotEqual(FixtureFingerprint, ExpertAccessPolicy.ComputeFingerprint(OtherAccount, Machine));
        Assert.NotEqual(FixtureFingerprint, ExpertAccessPolicy.ComputeFingerprint(Account, OtherMachine));
        Assert.True(ExpertAccessPolicy.MatchesFingerprint(ExpertAccessPolicy.OwnerFingerprint));
        Assert.False(ExpertAccessPolicy.MatchesFingerprint(ExpertAccessPolicy.ComputeFingerprint(OtherAccount, Machine)));
        Assert.False(ExpertAccessPolicy.MatchesFingerprint(ExpertAccessPolicy.ComputeFingerprint(Account, OtherMachine)));
    }

    [Theory]
    [InlineData(null, Machine)]
    [InlineData("", Machine)]
    [InlineData("not-a-sid", Machine)]
    [InlineData(Account, null)]
    [InlineData(Account, "")]
    [InlineData(Account, "not-a-guid")]
    [InlineData(Account, "00000000-0000-0000-0000-000000000000")]
    public void Unreadable_identity_denies_access(string? sid, string? guid)
    {
        var fingerprint = ExpertAccessPolicy.ComputeFingerprint(sid, guid);
        Assert.Null(fingerprint);
        using var missing = ExpertAccessPolicy.OverrideFingerprintForTests(() => fingerprint);
        Assert.False(ExpertAccessPolicy.CanUseExpertMode);
    }

    [Fact]
    public void Settings_json_and_legacy_migration_cannot_enable_expert_for_others()
    {
        using var otherAccount = AsOtherAccount();
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"ExpertMode":true,"ServicesExpertMode":true}""")!;
        Assert.False(settings.ExpertMode);
        Assert.True(settings.ServicesExpertMode);
        AppSettings.MigrateLegacyPreferences(settings, hasExpertMode: false);
        Assert.False(settings.ExpertMode);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        Assert.False(serialized.RootElement.GetProperty(nameof(AppSettings.ExpertMode)).GetBoolean());

        using var owner = AsOwner();
        Assert.False(settings.ExpertMode); // A denied write did not retain the preference.
        var changes = new List<string?>();
        settings.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        settings.ExpertMode = true;
        Assert.True(settings.ExpertMode);
        Assert.Equal(nameof(AppSettings.ExpertMode), Assert.Single(changes));
        Assert.True(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.ExpertMode);

        using (AsOtherAccount()) Assert.False(settings.ExpertMode); // Even a previously stored true needs access.
        Assert.True(settings.ExpertMode);
    }

    [Fact]
    public async Task Non_owner_request_returns_without_confirmation()
    {
        using var otherAccount = AsOtherAccount();
        var previousAnswer = Ui.AutoAnswer;
        var previousConfirmations = Ui.AutoAnswered.ToArray();
        try
        {
            Ui.AutoAnswer = false;
            Ui.AutoAnswered.Clear();
            // Exercise the real request without creating unrelated pages or attaching global handlers.
            var shell = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            Assert.False(shell.CanUseExpertMode);
            Assert.False(await shell.RequestExpertModeAsync());
            Assert.False(_settings.ExpertMode);
            Assert.Empty(Ui.AutoAnswered);
        }
        finally
        {
            Ui.AutoAnswer = previousAnswer;
            Ui.AutoAnswered.Clear();
            Ui.AutoAnswered.AddRange(previousConfirmations);
        }
    }

    [Fact]
    public async Task Direct_apply_requires_effective_expert_mode_and_keeps_simple_tweaks_available()
    {
        using var otherAccount = AsOtherAccount();
        var session = await BeginAsync(ChangeJournal.ForTests());
        var applied = new List<TweakRisk>();
        var advanced = FakeTweak(TweakRisk.Advanced, () => applied.Add(TweakRisk.Advanced));
        var optional = FakeTweak(TweakRisk.Optional, () => applied.Add(TweakRisk.Optional));
        _settings.ExpertMode = true;
        var denied = TweakEngine.ApplyInSession(session, [advanced, optional], new SystemProfile());
        Assert.Equal(1, denied.Skipped);
        Assert.Equal(TweakRisk.Optional, Assert.Single(applied));
        Assert.Empty(session.Errors);

        using var owner = AsOwner();
        Assert.False(_settings.ExpertMode);
        Assert.Equal(1, TweakEngine.ApplyInSession(session, [advanced], new SystemProfile()).Skipped);
        _settings.ExpertMode = true;
        Assert.Equal(0, TweakEngine.ApplyInSession(session, [advanced], new SystemProfile()).Skipped);
        Assert.Equal(new[] { TweakRisk.Optional, TweakRisk.Advanced }, applied);
        session.Complete();
    }

    [Fact]
    public async Task Downgrade_while_waiting_for_the_journal_gate_denies_the_final_advanced_apply()
    {
        using var owner = AsOwner();
        _settings.ExpertMode = true;
        var journal = ChangeJournal.ForTests();
        var session = await BeginAsync(journal);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = journal.ExclusiveAsync(async () => { await release.Task; return true; });
        var progress = new ApplyProgress();
        var applied = 0;
        var tweak = FakeTweak(TweakRisk.Advanced, () => applied++);
        var apply = Task.Factory.StartNew(() => TweakEngine.ApplyInSession(session, [tweak], new SystemProfile(), progress),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var applyThread = await progress.Applying.Task.WaitAsync(TestTimeout);
            // After synchronous progress returns the engine waits immediately on the occupied journal gate.
            Assert.True(SpinWait.SpinUntil(() => (applyThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TestTimeout));
            _settings.ExpertMode = false;
        }
        finally
        {
            release.TrySetResult();
            await gate.WaitAsync(TestTimeout);
            await apply.WaitAsync(TestTimeout);
        }
        Assert.Equal(1, (await apply).Skipped);
        Assert.Equal(0, applied);
        session.Complete();
    }

    [Theory]
    [InlineData("record")]
    [InlineData("source")]
    [InlineData("batch")]
    [InlineData("all")]
    public async Task Existing_expert_changes_remain_undoable_for_others(string undoKind)
    {
        using var otherAccount = AsOtherAccount();
        var journal = ChangeJournal.ForTests();
        var fakeSystem = new RestoreProbe();
        journal.SystemAccess = fakeSystem;
        var batch = journal.BeginBatch("Expert change from a previous release");
        const string source = "tweak:expert-svchost-split-off";
        const string path = @"SYSTEM\CurrentControlSet\Control";
        const string name = "SvcHostSplitThresholdInKB";
        var (record, _) = journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = source, Title = "Expert change", Kind = ChangeKind.Registry,
            Target = $@"reg:HKLM\{path}\{name}", OldValue = "DWord: 1000", NewValue = "DWord: 2000",
            Registry = new RegistryValueSnapshot
            {
                Hive = RegHive.HKLM, Path = path, Name = name, KeyExisted = true, ValueExisted = true,
                Kind = RegistryValueKind.DWord, Data = "1000",
            },
        });

        var result = await (undoKind switch
        {
            "record" => journal.RevertAsync([record]),
            "source" => journal.RevertSourceAsync(source),
            "batch" => journal.RevertBatchAsync(batch.Id),
            _ => journal.RevertAllAsync(),
        });
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(record.Id, Assert.Single(fakeSystem.Restored).Id);
        Assert.Empty(journal.ActiveRecords);
    }

    private static Task<ChangeSession> BeginAsync(ChangeJournal journal)
    {
        ChangeSession.BypassElevationForTests = true;
        return ChangeSession.BeginAsync("Expert access test", createRestorePoint: false, journal: journal);
    }

    private static CustomTweak FakeTweak(TweakRisk risk, Action apply) => new()
    {
        Id = $"test-{risk}", Title = "Test tweak", Description = "Test fixture",
        Risk = risk, DetectFunc = () => TweakState.NotApplied, ApplyAction = _ => apply(),
    };

    private sealed class ApplyProgress : IProgress<string>
    {
        public TaskCompletionSource<Thread> Applying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(string value) => Applying.TrySetResult(Thread.CurrentThread);
    }

    private sealed class RestoreProbe : IJournalSystem
    {
        public List<ChangeRecord> Restored { get; } = [];
        public Task<RevertOutcome> RestoreAsync(ChangeRecord record)
        {
            Restored.Add(record);
            return Task.FromResult(RevertOutcome.Done);
        }
        public Guid ActivePowerScheme() => throw new NotSupportedException();
        public bool? PowerSchemeExists(Guid scheme) => throw new NotSupportedException();
        public void DeletePowerScheme(Guid scheme) => throw new NotSupportedException();
    }
}
