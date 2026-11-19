using System.IO;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Removal;
using Lycoris.Modules.Restore;
using Lycoris.Modules.Startup;

namespace Lycoris.Tests;

[Collection("Registry")]
public sealed class StartupDisableBatchTests : IDisposable
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private readonly string _root = $@"Software\Lycoris-Tests\StartupBatch-{Guid.NewGuid():N}";
    private readonly string _files = Path.Combine(Path.GetTempPath(), $"lycoris-startup-{Guid.NewGuid():N}");
    private readonly ChangeJournal _journal = ChangeJournal.ForTests();
    private readonly List<StartupEntry> _rows = [];
    private readonly BatchClock _time = new();
    private readonly Func<RegHive, string, (RegHive Hive, string Path)>? _savedRedirect = RegistryHelper.Redirect;
    private readonly Func<bool?>? _savedInteractive = Elevation.InteractiveUserForTests;
    private readonly Func<string?>? _savedSid = UserScope.CurrentSidForTests;
    private readonly bool _savedBypass = ChangeSession.BypassElevationForTests;
    private readonly bool _savedLock = ChangeSession.Locked;
    private string? _sid = Sid;
    private bool? _interactive = true;
    private Func<string, (SignatureState?, string?)> _signature = TrustedSignature;

    public StartupDisableBatchTests()
    {
        Directory.CreateDirectory(_files);
        RegistryHelper.Redirect = Redirect;
        ChangeSession.BypassElevationForTests = true;
        UserScope.CurrentSidForTests = () => _sid;
        Elevation.InteractiveUserForTests = () => _interactive;
    }

    public void Dispose()
    {
        ChangeSession.Locked = _savedLock;
        Elevation.InteractiveUserForTests = _savedInteractive;
        UserScope.CurrentSidForTests = _savedSid;
        ChangeSession.BypassElevationForTests = _savedBypass;
        RegistryHelper.Redirect = _savedRedirect;
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        Directory.Delete(_files, recursive: true);
    }

    [Fact]
    public async Task Preview_requires_one_unambiguous_known_and_enabled_classic_entry()
    {
        var confirmed = AddRun("Confirmed");
        var duplicate = AddRun("Duplicate", StartupSource.RunMachine);
        _rows.Add(duplicate with { Name = "DUPLICATE", Signature = SignatureState.Tampered });
        var malformed = AddRun("Malformed", StartupSource.Run32Machine);
        SetMark(malformed, RegistryValueKind.DWord, 2);
        var text = AddRun("Text");
        SetMark(text, RegistryValueKind.String, "02");
        var none = AddRun("None");
        var mapped = Redirect(RegHive.HKCU, StartupLocations.ApprovedKey(none.Source));
        using (var key = Registry.CurrentUser.CreateSubKey(mapped.Path))
            key.SetValue(none.Name, StartupApproved.Build(true, _time.GetUtcNow().UtcDateTime), RegistryValueKind.None);
        var disabled = AddRun("Disabled");
        SetMark(disabled, RegistryValueKind.Binary, StartupApproved.Build(false, _time.GetUtcNow().UtcDateTime));
        var unfamiliar = AddRun("Unfamiliar", exe: "Unknown.exe");
        _rows.Add(unfamiliar with { Source = StartupSource.PackagedTask, PackageFamilyName = "Test", PackageState = 2 });

        var plan = await Action().PrepareAsync(CancellationToken.None);

        var preview = Assert.Single(plan.Items);
        Assert.Equal(confirmed.Key, preview.Key);
        Assert.Contains(StartupLocations.DescribeMark(confirmed), preview.Scope);
        Assert.Contains(confirmed.Command, preview.Note);
        Assert.Empty(_journal.ActiveRecords);
    }

    [Fact]
    public async Task Confirmed_subset_writes_only_the_two_marks_and_undo_restores_the_exact_prior_layers()
    {
        var absent = AddRun("Absent");
        var kept = AddRun("Kept", StartupSource.Run32Machine);
        var folder = AddFolder();
        var prior = StartupApproved.Build(true, _time.GetUtcNow().UtcDateTime);
        var previous = await Begin("Previous startup toggle", null);
        Assert.True(previous.SetRegistry(StartupLocations.ApprovedHive(folder.Source), StartupLocations.ApprovedKey(folder.Source),
            folder.Name, RegistryValueKind.Binary, prior, StartupChanges.EntrySource, folder.DisplayName));
        previous.Complete();
        var action = Action();
        var plan = await action.PrepareAsync(CancellationToken.None);
        Assert.Equal(3, plan.Items.Count);

        var result = await Execute(action, plan, absent.Key, folder.Key);

        Assert.Equal(2, result.Applied);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.False(result.UnknownOutcome);
        Assert.Null(Mark(kept));
        var expected = StartupApproved.Build(false, _time.GetUtcNow().UtcDateTime);
        Assert.Equal(expected, Assert.IsType<byte[]>(Mark(absent)));
        Assert.Equal(expected, Assert.IsType<byte[]>(Mark(folder)));
        Assert.Equal(absent.Command, RegistryHelper.ReadString(RegHive.HKCU, StartupLocations.RunKey, absent.Name));
        Assert.Equal("fixture executable", File.ReadAllText(folder.ItemPath!));
        var records = _journal.ActiveRecords.Where(record => record.BatchId != previous.Batch.Id).ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal(Sid, Assert.Single(records, record => record.Target == StartupLocations.JournalTarget(absent)).UserSid);
        var batch = Assert.Single(records.Select(record => record.BatchId).Distinct());

        var undo = await _journal.RevertBatchAsync(batch);

        Assert.Equal(2, undo.Reverted);
        Assert.Equal(0, undo.Failed);
        Assert.Null(Mark(absent));
        Assert.Equal(prior, Assert.IsType<byte[]>(Mark(folder)));
        Assert.Equal(previous.Batch.Id, Assert.Single(_journal.ActiveRecords).BatchId);
    }

    [Fact]
    public async Task Changed_command_file_signer_and_approval_after_preview_are_skipped_without_adopting_another_row()
    {
        var commandChanged = AddRun("Command");
        var fileChanged = AddRun("File");
        var signatureChanged = AddRun("Signature");
        var markChanged = AddRun("Mark");
        var action = Action();
        var plan = await action.PrepareAsync(CancellationToken.None);
        Assert.Equal(4, plan.Items.Count);

        WriteRun(commandChanged, commandChanged.Command + " -new");
        var originalTime = File.GetLastWriteTimeUtc(fileChanged.TargetPath!);
        var originalLength = new FileInfo(fileChanged.TargetPath!).Length;
        File.WriteAllText(fileChanged.TargetPath!, "changed executable");
        File.SetLastWriteTimeUtc(fileChanged.TargetPath!, originalTime);
        Assert.Equal(originalLength, new FileInfo(fileChanged.TargetPath!).Length);
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(fileChanged.TargetPath!));
        _signature = target => target == signatureChanged.TargetPath ? (SignatureState.Tampered, null) : TrustedSignature(target);
        SetMark(markChanged, RegistryValueKind.Binary, new byte[] { 2 });
        var newRow = AddRun("New");

        var result = await Execute(action, plan, plan.Items.Select(item => item.Key).ToArray());

        Assert.Equal(0, result.Applied);
        Assert.Equal(4, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.Empty(_journal.ActiveRecords);
        Assert.Null(Mark(commandChanged));
        Assert.Null(Mark(fileChanged));
        Assert.Null(Mark(signatureChanged));
        Assert.Equal(new byte[] { 2 }, Assert.IsType<byte[]>(Mark(markChanged)));
        Assert.Null(Mark(newRow));
    }

    [Fact]
    public async Task Restore_point_wait_is_followed_by_another_identity_check()
    {
        var replaced = AddRun("Replaced");
        var kept = AddRun("Kept", StartupSource.RunMachine);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = Action(async (title, progress) =>
        {
            entered.SetResult();
            await release.Task;
            return await Begin(title, progress);
        });
        var plan = await action.PrepareAsync(CancellationToken.None);
        var applying = Execute(action, plan, replaced.Key, kept.Key);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            WriteRun(replaced, replaced.Command + " -replaced");
        }
        finally { release.TrySetResult(); }

        var result = await applying;

        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Null(Mark(replaced));
        Assert.False(StartupApproved.IsEnabled(Assert.IsType<byte[]>(Mark(kept))));
        Assert.Equal(StartupLocations.JournalTarget(kept), Assert.Single(_journal.ActiveRecords).Target);
    }

    [Fact]
    public async Task Snapshot_failure_reports_two_applied_and_restores_both_on_undo()
    {
        var first = AddRun("First");
        var second = AddRun("Second", StartupSource.RunMachine);
        var failed = AddRun("Failed", StartupSource.Run32Machine);
        var prior = StartupApproved.Build(true, _time.GetUtcNow().UtcDateTime);
        SetMark(second, RegistryValueKind.Binary, prior);
        SetMark(failed, RegistryValueKind.Binary, prior);
        var action = Action();
        var plan = await action.PrepareAsync(CancellationToken.None);
        Assert.Equal(3, plan.Items.Count);
        var signatureChecks = 0;
        var markReadsAfterFinalSnapshot = 0;
        _signature = target =>
        {
            if (target == failed.TargetPath) signatureChecks++;
            return TrustedSignature(target);
        };
        RegistryHelper.Redirect = (hive, path) =>
        {
            // After the before/after-restore-point observations, fail the next snapshot that the real session takes.
            if (signatureChecks == 2 && path == StartupLocations.ApprovedKey(failed.Source)
                && ++markReadsAfterFinalSnapshot == 3)
                throw new IOException("Approval snapshot failed");
            return Redirect(hive, path);
        };

        var result = await Execute(action, plan, first.Key, second.Key, failed.Key);

        Assert.Equal(2, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Contains("Approval snapshot failed", Assert.Single(result.Errors));
        Assert.False(result.UnknownOutcome);
        Assert.Equal(prior, Assert.IsType<byte[]>(Mark(failed)));
        Assert.Equal(2, _journal.ActiveRecords.Count);
        var batch = Assert.Single(_journal.ActiveRecords.Select(record => record.BatchId).Distinct());
        var undo = await _journal.RevertBatchAsync(batch);
        Assert.Equal(2, undo.Reverted);
        Assert.Null(Mark(first));
        Assert.Equal(prior, Assert.IsType<byte[]>(Mark(second)));
    }

    [Fact]
    public async Task Unknown_readback_stops_later_writes_and_keeps_the_recovery_record()
    {
        var alreadyApplied = AddRun("Applied", StartupSource.Run32Machine);
        var uncertain = AddRun("Uncertain");
        var later = AddRun("Later", StartupSource.RunMachine);
        var action = Action();
        var plan = await action.PrepareAsync(CancellationToken.None);
        Assert.Equal(3, plan.Items.Count);
        var malformedPath = $@"{_root}\UnreadableMark";
        using (var key = Registry.CurrentUser.CreateSubKey(malformedPath))
            key.SetValue(uncertain.Name, "unrecognized state", RegistryValueKind.String);
        RegistryHelper.Redirect = (hive, path) =>
        {
            var mapped = Redirect(hive, path);
            if (hive != RegHive.HKCU || path != StartupLocations.ApprovedKey(uncertain.Source)) return mapped;
            using var key = Registry.CurrentUser.OpenSubKey(mapped.Path);
            return key?.GetValue(uncertain.Name) is byte[] data && !StartupApproved.IsEnabled(data)
                ? (RegHive.HKCU, malformedPath)
                : mapped;
        };

        var result = await Execute(action, plan, alreadyApplied.Key, uncertain.Key, later.Key);

        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.UnknownOutcome);
        Assert.Contains(uncertain.DisplayName, Assert.Single(result.Errors));
        Assert.Null(Mark(later));
        Assert.Equal(2, _journal.ActiveRecords.Count);
        var recovery = Assert.Single(_journal.ActiveRecords, record => record.Target == StartupLocations.JournalTarget(uncertain));
        RegistryHelper.Redirect = Redirect;
        Assert.False(StartupApproved.IsEnabled(Assert.IsType<byte[]>(Mark(uncertain))));
        Assert.Equal(2, (await _journal.RevertBatchAsync(recovery.BatchId)).Reverted);
        Assert.Null(Mark(alreadyApplied));
        Assert.Null(Mark(uncertain));
    }

    [Fact]
    public async Task Drift_from_a_replaced_run_command_opens_removal_without_direct_reapply()
    {
        var candidate = AddRun("Steam");
        var action = Action();
        var preview = await action.PrepareAsync(CancellationToken.None);
        Assert.Equal(1, (await Execute(action, preview, candidate.Key)).Applied);
        var replacement = candidate.Command + " -replacement";
        WriteRun(candidate, replacement);
        RegistryHelper.DeleteValue(RegHive.HKCU, StartupLocations.ApprovedKey(candidate.Source), candidate.Name);

        var drift = Assert.Single(DriftDetector.Detect(_journal.ActiveRecords, LiveState.Read, _ => false));

        Assert.Equal(StartupDisableBatchAction.JournalSource, drift.Record.Source);
        Assert.Equal("removal", drift.OwnerPage);
        Assert.Equal(DriftFix.None, drift.Fix);
        Assert.False(DriftReapply.CanReapply(drift));
        Assert.NotEqual(drift.Applied, drift.Live);
        Assert.Null(JournalTrust.Check(drift.Record));
        Assert.Equal(1, (await _journal.RevertBatchAsync(drift.Record.BatchId)).Reverted);
        Assert.Null(Mark(candidate));
        Assert.Equal(replacement, RegistryHelper.ReadString(RegHive.HKCU, StartupLocations.RunKey, candidate.Name));
    }

    [Fact]
    public async Task Undo_of_an_older_manual_layer_hands_off_its_exact_original_to_the_batch()
    {
        var candidate = AddRun("Steam");
        var original = StartupApproved.Build(false, _time.GetUtcNow().UtcDateTime.AddDays(-3));
        SetMark(candidate, RegistryValueKind.Binary, original);
        var manual = await Begin("Manual startup toggle", null);
        Assert.True(manual.SetRegistry(RegHive.HKCU, StartupLocations.ApprovedKey(candidate.Source), candidate.Name,
            RegistryValueKind.Binary, StartupApproved.Build(true, _time.GetUtcNow().UtcDateTime), StartupChanges.EntrySource, candidate.DisplayName));
        manual.Complete();
        var action = Action();
        var preview = await action.PrepareAsync(CancellationToken.None);
        Assert.Equal(1, (await Execute(action, preview, candidate.Key)).Applied);

        Assert.Equal(1, (await _journal.RevertBatchAsync(manual.Batch.Id)).HandedOver);
        var newer = Assert.Single(_journal.ActiveRecords);
        Assert.Null(JournalTrust.Check(newer));
        Assert.Equal(1, (await _journal.RevertBatchAsync(newer.BatchId)).Reverted);
        Assert.Equal(original, Assert.IsType<byte[]>(Mark(candidate)));
    }

    [Theory]
    [InlineData("wrong kind")]
    [InlineData("run command")]
    [InlineData("packaged mark")]
    [InlineData("wrong registry view")]
    [InlineData("missing user")]
    [InlineData("malformed mark")]
    [InlineData("wrong original type")]
    [InlineData("unrelated target")]
    public void Batch_journal_source_rejects_records_outside_the_classic_approval_contract(string failure)
    {
        var path = failure switch
        {
            "run command" => StartupLocations.RunKey,
            "packaged mark" => PackagedStartup.KeyPath("Test_abcdefghjkmnp", "StartupTask"),
            "wrong registry view" => StartupLocations.ApprovedKey(StartupSource.Run32Machine),
            _ => StartupLocations.ApprovedKey(StartupSource.RunUser),
        };
        var snapshot = new RegistryValueSnapshot
        {
            Hive = RegHive.HKCU, Path = path, Name = "Steam",
            KeyExisted = failure == "wrong original type", ValueExisted = failure == "wrong original type",
            Kind = failure == "wrong original type" ? RegistryValueKind.Binary : RegistryValueKind.Unknown,
            Data = failure == "wrong original type" ? "invalid base64" : null,
        };
        var record = new ChangeRecord
        {
            Source = StartupDisableBatchAction.JournalSource, Title = "Startup",
            Registry = snapshot, Kind = failure == "wrong kind" ? ChangeKind.Service : ChangeKind.Registry,
            Target = failure == "unrelated target" ? "reg:HKCU\\SomewhereElse\\Steam" : $"reg:{snapshot.FullName}",
            UserSid = failure == "missing user" ? null : Sid,
            NewValue = failure == "malformed mark" ? "Binary: 03" :
                $"Binary: {StartupApproved.ToHex(StartupApproved.Build(false, _time.GetUtcNow().UtcDateTime))}",
        };
        Assert.NotNull(JournalTrust.Check(record));
    }

    [Theory]
    [InlineData("changed account")]
    [InlineData("unknown account")]
    [InlineData("unknown desktop")]
    [InlineData("stale preview")]
    [InlineData("locked")]
    [InlineData("unknown key")]
    [InlineData("duplicate selection")]
    public async Task Invalid_selection_account_or_age_never_starts_a_change_session(string invalidation)
    {
        var candidate = AddRun("Candidate");
        var action = Action((_, _) => throw new InvalidOperationException("The change session must not start"));
        var plan = await action.PrepareAsync(CancellationToken.None);
        var selection = new List<string> { candidate.Key };
        switch (invalidation)
        {
            case "changed account": _sid = "S-1-5-21-100-200-300-1002"; break;
            case "unknown account": _sid = null; break;
            case "unknown desktop": _interactive = null; break;
            case "stale preview": _time.Elapsed = TimeSpan.FromMinutes(5); break;
            case "locked": ChangeSession.Locked = true; break;
            case "unknown key": selection.Add("This was never previewed"); break;
            case "duplicate selection": selection.Add(candidate.Key.ToUpperInvariant()); break;
        }

        var result = await Execute(action, plan, selection.ToArray());

        Assert.Equal(0, result.Applied);
        Assert.Equal(selection.Count, result.Skipped);
        Assert.DoesNotContain("The change session must not start", Assert.Single(result.Errors));
        Assert.Null(Mark(candidate));
        Assert.Empty(_journal.ActiveRecords);
    }

    [Theory]
    [InlineData(RegistryValueKind.DWord, 1)]
    [InlineData(RegistryValueKind.String, "0")]
    public async Task Active_or_unreadable_policy_blocks_the_confirmed_approved_row(RegistryValueKind kind, object value)
    {
        var candidate = AddRun("Candidate");
        var action = Action();
        var plan = await action.PrepareAsync(CancellationToken.None);
        Assert.Single(plan.Items);
        RegistryHelper.Write(RegHive.HKCU, StartupDisableObservation.ExplorerPolicy,
            StartupDisableObservation.UserRunPolicy, kind, value);

        var result = await Execute(action, plan, candidate.Key);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Null(Mark(candidate));
        Assert.Empty(_journal.ActiveRecords);
    }

    private StartupDisableBatchAction Action(Func<string, IProgress<string>?, Task<ChangeSession>>? begin = null) =>
        new(() => _rows, new(path => _signature(path), _ => _files), begin ?? Begin, _time);

    private Task<ChangeSession> Begin(string title, IProgress<string>? progress) =>
        ChangeSession.BeginAsync(title, createRestorePoint: false, progress: progress, journal: _journal);

    private static Task<RemovalBatchResult> Execute(StartupDisableBatchAction action, RemovalBatchPlan plan, params string[] keys) =>
        action.ExecuteAsync(plan, keys.ToHashSet(StringComparer.Ordinal), progress: null, CancellationToken.None);

    private (RegHive Hive, string Path) Redirect(RegHive hive, string path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");

    private StartupEntry AddRun(string name, StartupSource source = StartupSource.RunUser, string exe = "Steam.exe")
    {
        var folder = Directory.CreateDirectory(Path.Combine(_files, name));
        var path = Path.Combine(folder.FullName, exe);
        File.WriteAllText(path, "fixture executable");
        var entry = new StartupEntry
        {
            Source = source, Name = name, DisplayName = name, Command = $"\"{path}\" -silent",
            TargetPath = path, TargetExists = true, Enabled = true,
        };
        WriteRun(entry, entry.Command);
        _rows.Add(entry);
        return entry;
    }

    private StartupEntry AddFolder()
    {
        const string name = "Steam.exe";
        var path = Path.Combine(_files, name);
        File.WriteAllText(path, "fixture executable");
        var entry = new StartupEntry
        {
            Source = StartupSource.FolderCommon, Name = name, DisplayName = name, Command = path, ItemPath = path,
            TargetPath = path, TargetExists = true, Enabled = true,
        };
        _rows.Add(entry);
        return entry;
    }

    private static void WriteRun(StartupEntry entry, string command)
    {
        var path = entry.Source switch
        {
            StartupSource.RunUser => StartupLocations.RunKey,
            StartupSource.RunMachine => StartupLocations.RunKeyMachine,
            StartupSource.Run32Machine => StartupLocations.Run32Key,
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };
        RegistryHelper.Write(StartupLocations.ApprovedHive(entry.Source), path, entry.Name, RegistryValueKind.String, command);
    }

    private static (SignatureState?, string?) TrustedSignature(string path) =>
        Path.GetFileName(path) == "Steam.exe" ? (SignatureState.Valid, "Valve Corporation") : (SignatureState.Unsigned, null);

    private static object? Mark(StartupEntry entry) =>
        RegistryHelper.Read(StartupLocations.ApprovedHive(entry.Source), StartupLocations.ApprovedKey(entry.Source), entry.Name);

    private static void SetMark(StartupEntry entry, RegistryValueKind kind, object value) =>
        RegistryHelper.Write(StartupLocations.ApprovedHive(entry.Source), StartupLocations.ApprovedKey(entry.Source), entry.Name, kind, value);

    private sealed class BatchClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero) + Elapsed;
    }
}
