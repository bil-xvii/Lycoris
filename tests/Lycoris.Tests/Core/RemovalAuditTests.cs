using System.IO;
using Lycoris.Core;
using Lycoris.Modules.Games.Sources;
using Microsoft.Win32;

namespace Lycoris.Tests;

[Collection("Registry")]
public sealed class RemovalAuditTests : IDisposable
{
    private const string TestPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private readonly string _root = $@"Software\Lycoris-Tests\RemovalAudit-{Guid.NewGuid():N}";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"LycRemovalAudit-{Guid.NewGuid():N}");
    private readonly bool _previousBypass = ChangeSession.BypassElevationForTests;
    private readonly bool _previousLock = ChangeSession.Locked;

    public RemovalAuditTests()
    {
        Directory.CreateDirectory(_temp);
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
    }

    public void Dispose()
    {
        ChangeSession.BypassElevationForTests = _previousBypass;
        ChangeSession.Locked = _previousLock;
        RegistryHelper.Redirect = null;
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public async Task Removal_gateway_rechecks_guard_after_attempt_is_persisted()
    {
        var path = Path.Combine(_temp, "journal.json");
        var journal = ChangeJournal.ForTests(path);
        var attempt = new ProgramRemovalAudit(Guid.NewGuid(), Guid.NewGuid(), "VideoLAN.VLC", ProgramRemovalScope.User, DateTime.UtcNow);
        var checks = 0;

        bool Guard()
        {
            if (++checks == 1) return true;
            Assert.Equal(ProgramRemovalAuditOutcome.Attempted, Assert.Single(ChangeJournal.ForTests(path).ProgramRemovals).Outcome);
            return false;
        }
        var result = await ChangeSession.RunProgramRemovalAsync(attempt, Guard,
            () => throw new InvalidOperationException("A revoked guard must never dispatch"), journal);

        Assert.Equal(2, checks);
        Assert.Equal(ProgramRemovalAuditOutcome.NotStarted, result);
        Assert.Equal(result, Assert.Single(ChangeJournal.ForTests(path).ProgramRemovals).Outcome);
        Assert.Empty(journal.Records);
        Assert.Empty(journal.Batches);
    }

    [Fact]
    public async Task Removal_gateway_never_dispatches_when_self_test_locked()
    {
        var journal = ChangeJournal.ForTests(Path.Combine(_temp, "journal.json"));
        ChangeSession.Locked = true;
        var attempt = new ProgramRemovalAudit(Guid.NewGuid(), Guid.NewGuid(), "VideoLAN.VLC", ProgramRemovalScope.User, DateTime.UtcNow);

        var result = await ChangeSession.RunProgramRemovalAsync(attempt, () => true,
            () => throw new InvalidOperationException("Self-test must never dispatch"), journal);

        Assert.Equal(ProgramRemovalAuditOutcome.NotStarted, result);
        Assert.Empty(journal.ProgramRemovals);
    }

    [Fact]
    public async Task Registry_opt_in_guard_checks_exact_journaled_bytes_and_preserves_prior_layer_on_rejection()
    {
        const string entry = "OptionalStartup";
        var path = Path.Combine(_temp, "journal.json");
        var journal = ChangeJournal.ForTests(path);
        byte[] enabled = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] disabled = [3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0];
        RegistryHelper.Write(RegHive.HKCU, TestPath, entry, RegistryValueKind.Binary, enabled);
        var session = await ChangeSession.BeginAsync("Startup", createRestorePoint: false, journal: journal);
        Assert.True(session.SetRegistry(RegHive.HKCU, TestPath, entry, RegistryValueKind.Binary, disabled, "startup", "Normal write"));

        var called = false;
        Assert.False(session.SetRegistry(RegHive.HKCU, TestPath, entry, RegistryValueKind.Binary, enabled, "startup", "Guarded write", snapshot =>
        {
            called = true;
            Assert.Equal(Convert.ToBase64String(disabled), snapshot.Data);
            var durable = ChangeJournal.ForTests(path).Records;
            Assert.Equal(2, durable.Count);
            Assert.Equal(snapshot.Data, durable[^1].Registry?.Data);
            return false;
        }));

        Assert.True(called);
        Assert.Empty(session.Errors);
        Assert.Equal(disabled, Assert.IsType<byte[]>(RegistryHelper.Read(RegHive.HKCU, TestPath, entry)));
        var prior = Assert.Single(ChangeJournal.ForTests(path).Records);
        Assert.Equal(Convert.ToBase64String(enabled), prior.Registry?.Data);
    }

    [Fact]
    public void Uninstall_registry_fingerprint_changes_for_quiet_command_and_value_kind_and_refuses_portable()
    {
        const string path = "FakeUninstall";
        void Put(string name, string value, RegistryValueKind kind = RegistryValueKind.String) =>
            RegistryHelper.Write(RegHive.HKCU, path, name, kind, value);
        string? Fingerprint()
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{_root}\HKCU\{path}");
            return UninstallEntries.ReadUninstallFingerprint(key!);
        }
        Put("DisplayName", "VLC media player");
        Put("Publisher", "VideoLAN");
        Put("DisplayVersion", "3.0.21");
        Put("UninstallString", "opaque standard command");
        var before = Fingerprint();

        Put("QuietUninstallString", "opaque quiet command");
        var after = Fingerprint();
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);

        Put("QuietUninstallString", "opaque quiet command", RegistryValueKind.ExpandString);
        Assert.NotEqual(after, Fingerprint());
        Put("WinGetInstallerType", "portable");
        Assert.Null(Fingerprint());
    }
}
