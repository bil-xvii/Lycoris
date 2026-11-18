using System.Xml.Linq;
using Lycoris.Core;
using Lycoris.Core.Background;
using Lycoris.Shell;

namespace Lycoris.Tests;

[CollectionDefinition("BackgroundStartup", DisableParallelization = true)]
public sealed class BackgroundStartupCollection { }

[Collection("BackgroundStartup")]
public sealed class BackgroundStartupIntegrationTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Exe = @"C:\Program Files\Lycoris & Tools\Lycoris.exe";
    private const string OtherSid = "S-1-5-21-1-2-3-1002";
    private readonly FakeStore _store = new();
    private readonly ChangeJournal _journal = ChangeJournal.ForTests();
    private readonly BackgroundStartupRegistration _startup;
    private readonly Func<string?>? _originalSid = UserScope.CurrentSidForTests;
    private readonly bool _originalBypass = ChangeSession.BypassElevationForTests;

    public BackgroundStartupIntegrationTests()
    {
        _startup = BackgroundStartupRegistration.TryCreate(Sid, Exe, _store)!;
        _journal.SystemAccess = new FakeRestore(_startup);
        UserScope.CurrentSidForTests = () => Sid;
        ChangeSession.BypassElevationForTests = true;
    }

    public void Dispose()
    {
        UserScope.CurrentSidForTests = _originalSid;
        ChangeSession.BypassElevationForTests = _originalBypass;
    }

    [Fact]
    public void Logon_task_uses_only_the_current_account_and_a_separate_literal_executable_path()
    {
        var xml = _startup.BuildDefinition(BackgroundStartupState.Enabled);
        var task = XDocument.Parse(xml).Root!;
        var ns = task.Name.Namespace;
        Assert.Equal([Sid, Sid], task.Descendants(ns + "UserId").Select(e => e.Value).ToArray());
        Assert.Single(task.Descendants(ns + "LogonTrigger"));
        Assert.Equal("LeastPrivilege", Assert.Single(task.Descendants(ns + "RunLevel")).Value);
        Assert.Equal("InteractiveToken", Assert.Single(task.Descendants(ns + "LogonType")).Value);
        Assert.Equal(Exe, Assert.Single(task.Descendants(ns + "Command")).Value);
        Assert.Equal("--background --no-elevate", Assert.Single(task.Descendants(ns + "Arguments")).Value);
        Assert.Equal(BackgroundStartupState.Enabled, _startup.ParseDefinition(xml));
        Assert.Equal(BackgroundStartupState.Disabled, _startup.ParseDefinition(_startup.BuildDefinition(BackgroundStartupState.Disabled)));
    }

    [Fact]
    public void Readback_accepts_exported_defaults_and_a_quoted_path()
    {
        var task = XElement.Parse(_startup.BuildDefinition(BackgroundStartupState.Enabled));
        var ns = task.Name.Namespace;
        task.SetAttributeValue("version", "1.4");
        task.Element(ns + "RegistrationInfo")!.Add(new XElement(ns + "URI", BackgroundStartupRegistration.PathFor(Sid)));
        var settings = task.Element(ns + "Settings")!;
        settings.Element(ns + "Enabled")!.Remove();
        settings.AddFirst(new XElement(ns + "Priority", 7), new XElement(ns + "IdleSettings",
            new XElement(ns + "StopOnIdleEnd", true), new XElement(ns + "RestartOnIdle", false)));
        Assert.Single(task.Descendants(ns + "Command")).Value = '"' + Exe + '"';
        Assert.Equal(BackgroundStartupState.Enabled, _startup.ParseDefinition(task.ToString()));
    }

    [Theory]
    [InlineData("LeastPrivilege", "HighestAvailable")]
    [InlineData(Sid, OtherSid)]
    [InlineData("--no-elevate", "--e2e x")]
    [InlineData("Program Files", "Users\\Public")]
    [InlineData("</Exec>", "</Exec><Exec><Command>unexpected.exe</Command></Exec>")]
    [InlineData("</Settings>", "<RestartOnFailure><Interval>PT5M</Interval><Count>2</Count></RestartOnFailure></Settings>")]
    public void Foreign_task_xml_is_rejected(string from, string to)
    {
        var xml = _startup.BuildDefinition(BackgroundStartupState.Enabled).Replace(from, to);
        Assert.Null(_startup.ParseDefinition(xml));
    }

    [Fact]
    public void Malformed_dtd_oversized_and_deep_definitions_are_rejected()
    {
        Assert.Null(_startup.ParseDefinition("<!DOCTYPE x><Task />"));
        Assert.Null(_startup.ParseDefinition(new string('x', 65537)));
        var xml = _startup.BuildDefinition(BackgroundStartupState.Enabled);
        var deep = string.Concat(Enumerable.Repeat("<a>", 3000)) + string.Concat(Enumerable.Repeat("</a>", 3000));
        Assert.Null(_startup.ParseDefinition(xml.Replace("</Settings>", deep + "</Settings>")));
    }

    [Theory]
    [InlineData(@"\\server\share\Lycoris.exe")]
    [InlineData(@"C:\%temp%\Lycoris.exe")]
    [InlineData(@"C:\tools\..\Lycoris.exe")]
    [InlineData(@"C:\tools\payload.exe")]
    [InlineData(@"C:\tools\a:alt\Lycoris.exe")]
    [InlineData("\"C:\\tools\\Lycoris.exe\"")]
    public void Unsafe_or_ambiguous_action_paths_are_rejected(string path) =>
        Assert.Null(BackgroundStartupRegistration.TryCreate(Sid, path, _store));

    [Fact]
    public async Task Enable_remove_and_each_undo_use_the_same_owned_task()
    {
        Assert.True((await Begin()).SetBackgroundStartup(BackgroundStartupState.Absent, BackgroundStartupState.Enabled, "on", _startup));
        var enabled = Assert.Single(_journal.ActiveRecords);
        Assert.Equal(BackgroundStartupState.Enabled, _startup.ReadState());
        Assert.Equal(Sid, enabled.UserSid);
        Assert.True(enabled.IsPerUser);
        Assert.Equal(BackgroundStartupRegistration.PathFor(Sid), _store.Path);

        Assert.True((await Begin()).SetBackgroundStartup(BackgroundStartupState.Enabled, BackgroundStartupState.Absent, "off", _startup));
        var disabled = _journal.ActiveRecords.Last();
        Assert.Null(_store.Xml);
        Assert.True((await _journal.RevertAsync([disabled])).Success);
        Assert.Equal(BackgroundStartupState.Enabled, _startup.ReadState());
        Assert.True((await _journal.RevertAsync([enabled])).Success);
        Assert.Equal(BackgroundStartupState.Absent, _startup.ReadState());
    }

    [Fact]
    public async Task An_older_startup_record_cannot_replace_undo_data_for_a_newer_copy()
    {
        Assert.True((await Begin()).SetBackgroundStartup(BackgroundStartupState.Absent, BackgroundStartupState.Enabled, "first copy", _startup));
        var first = Assert.Single(_journal.ActiveRecords);
        _store.Xml = null; // The previous task was removed before setting up logon for the moved app.
        var moved = BackgroundStartupRegistration.TryCreate(Sid, @"C:\Moved\Lycoris.exe", _store)!;
        _journal.SystemAccess = new FakeRestore(moved);
        Assert.True((await Begin()).SetBackgroundStartup(BackgroundStartupState.Absent, BackgroundStartupState.Enabled, "moved copy", moved));
        var second = _journal.ActiveRecords.Last();
        var secondOriginal = second.BackgroundStartup;
        Assert.NotEqual(first.BackgroundStartup?.ExecutableHash, secondOriginal?.ExecutableHash);

        var refusal = await _journal.RevertAsync([first]);
        Assert.False(refusal.Success);
        Assert.Contains(second.Title, Assert.Single(refusal.Errors));
        Assert.False(first.Reverted);
        Assert.Null(first.HandedTo);
        Assert.Equal(secondOriginal, second.BackgroundStartup);
        Assert.Equal(BackgroundStartupState.Enabled, moved.ReadState());
        Assert.True((await _journal.RevertAsync([second])).Success);
        Assert.Null(_store.Xml);
    }

    [Fact]
    public async Task An_older_startup_record_hands_over_undo_data_for_the_same_copy()
    {
        Assert.True((await Begin()).SetBackgroundStartup(BackgroundStartupState.Absent, BackgroundStartupState.Enabled, "on", _startup));
        var first = Assert.Single(_journal.ActiveRecords);
        Assert.True((await Begin()).SetBackgroundStartup(BackgroundStartupState.Enabled, BackgroundStartupState.Absent, "off", _startup));
        var second = _journal.ActiveRecords.Last();

        var result = await _journal.RevertAsync([first]);
        Assert.True(result.Success);
        Assert.Equal(1, result.HandedOver);
        Assert.Equal(second.Id, first.HandedTo);
        Assert.Equal(first.BackgroundStartup, second.BackgroundStartup);
        Assert.True((await _journal.RevertAsync([second])).Success);
        Assert.Null(_store.Xml);
    }

    [Fact]
    public async Task A_deleted_disabled_task_is_restored_disabled()
    {
        _store.Xml = _startup.BuildDefinition(BackgroundStartupState.Disabled);
        Assert.True((await Begin()).SetBackgroundStartup(BackgroundStartupState.Disabled, BackgroundStartupState.Absent, "off", _startup));
        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);
        Assert.Equal(BackgroundStartupState.Disabled, _startup.ReadState());
    }

    [Fact]
    public async Task A_changed_or_foreign_task_is_never_overwritten()
    {
        var session = await Begin();
        _store.Xml = _startup.BuildDefinition(BackgroundStartupState.Enabled);
        Assert.False(session.SetBackgroundStartup(BackgroundStartupState.Absent, BackgroundStartupState.Enabled, "on", _startup));
        var foreign = _store.Xml.Replace("LeastPrivilege", "HighestAvailable");
        _store.Xml = foreign;
        Assert.False(session.SetBackgroundStartup(BackgroundStartupState.Enabled, BackgroundStartupState.Absent, "off", _startup));
        Assert.Equal(foreign, _store.Xml);
        Assert.Equal(0, _store.Writes);
        Assert.Empty(_journal.ActiveRecords);
    }

    [Fact]
    public async Task A_scheduler_failure_after_a_write_preserves_undo()
    {
        _store.ThrowAfterCreate = true;
        Assert.False((await Begin()).SetBackgroundStartup(BackgroundStartupState.Absent, BackgroundStartupState.Enabled, "on", _startup));
        var record = Assert.Single(_journal.ActiveRecords);
        Assert.Equal(BackgroundStartupState.Enabled, _startup.ReadState());
        Assert.True((await _journal.RevertAsync([record])).Success);
        Assert.Null(_store.Xml);
    }

    [Fact]
    public void Forged_restore_fails_for_another_account_path_or_untyped_record()
    {
        var snapshot = _startup.Snapshot(BackgroundStartupState.Enabled);
        Assert.Throws<InvalidOperationException>(() => _startup.Restore(OtherSid, snapshot));
        Assert.Throws<InvalidOperationException>(() => _startup.Restore(Sid, snapshot with { ExecutableHash = new string('a', 64) }));
        Assert.NotNull(JournalTrust.Check(new ChangeRecord
        {
            Kind = ChangeKind.ScheduledTask, Source = "startup-tasks", TaskPath = BackgroundStartupRegistration.PathFor(Sid),
            Target = "task:" + BackgroundStartupRegistration.PathFor(Sid), TaskWasEnabled = true,
        }));
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void Background_launch_is_opt_in_and_disabled_for_qa_elevated_and_restart_copies()
    {
        Assert.False(BackgroundModePolicy.IsLogonLaunch([], qaMode: false, elevated: false));
        Assert.True(BackgroundModePolicy.IsLogonLaunch(["--background", "--no-elevate"], qaMode: false, elevated: false));
        Assert.False(BackgroundModePolicy.IsLogonLaunch(["--background", "--elevated"], qaMode: false, elevated: false));
        Assert.False(BackgroundModePolicy.IsLogonLaunch(["--background"], qaMode: false, elevated: true));
        Assert.False(BackgroundModePolicy.IsLogonLaunch(["--background"], qaMode: true, elevated: false));
    }

    [Fact]
    public void Only_a_ready_tray_can_hide_and_exit_dialogs_or_losing_the_tray_keep_the_app_visible()
    {
        Assert.False(BackgroundModePolicy.CanStartHidden(true, false, false));
        Assert.False(BackgroundModePolicy.CanStartHidden(true, true, true));
        Assert.True(BackgroundModePolicy.CanStartHidden(true, true, false));
        Assert.True(BackgroundModePolicy.CanHideOnClose(true, true, false, false));
        Assert.False(BackgroundModePolicy.CanHideOnClose(true, true, true, false));
        Assert.False(BackgroundModePolicy.CanHideOnClose(true, true, false, true));
        Assert.False(BackgroundModePolicy.CanHideOnClose(false, true, false, false));
        Assert.True(BackgroundModePolicy.ShouldReveal(true, trayReady: false));
        Assert.False(BackgroundModePolicy.ShouldReveal(true, trayReady: true));
        Assert.False(BackgroundModePolicy.ShouldReveal(false, trayReady: false));
    }

    [Fact]
    public void Cross_instance_activation_requires_the_same_executable_path()
    {
        Assert.True(BackgroundWindowActivation.IsSameImage(Exe, Exe.ToLowerInvariant()));
        Assert.False(BackgroundWindowActivation.IsSameImage(Exe, @"C:\Elsewhere\Lycoris.exe"));
        Assert.False(BackgroundWindowActivation.IsSameImage(null, null));
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(41, false, true)]
    [InlineData(41, true, false)]
    public void After_failed_activation_only_a_marked_window_that_can_respond_prevents_recovery(
        int handle, bool mayRespond, bool expectedRecovery)
    {
        nint probed = 0;
        var recovery = BackgroundWindowActivation.CanOfferWindowlessRecovery(handle, window =>
        {
            probed = window;
            return mayRespond;
        });
        Assert.Equal(expectedRecovery, recovery);
        Assert.Equal((nint)handle, probed);
    }

    [Fact]
    public void Instance_names_use_the_production_name_unless_a_debug_root_is_set()
    {
        var normal = BackgroundModePolicy.NormalInstanceName;
        Assert.Equal(normal, BackgroundModePolicy.InstanceNameFor(null));
        Assert.Equal(normal, BackgroundModePolicy.InstanceNameFor(" "));
        var qa = BackgroundModePolicy.InstanceNameFor(@"C:\LycorisQA");
#if DEBUG
        Assert.NotEqual(normal, qa);
        Assert.Equal(qa, BackgroundModePolicy.InstanceNameFor(@"c:\LYCORISQA\"));
        Assert.NotEqual(qa, BackgroundModePolicy.InstanceNameFor(@"C:\LycorisQA2"));
#else
        Assert.Equal(normal, qa);
#endif
    }

    private Task<ChangeSession> Begin() => ChangeSession.BeginAsync("test", createRestorePoint: false, journal: _journal);

    private sealed class FakeStore : IBackgroundStartupStore
    {
        public string? Xml { get; set; }
        public string? Path { get; private set; }
        public int Writes { get; private set; }
        public bool ThrowAfterCreate { get; set; }

        public string? ReadXml(string taskPath) => Xml;

        public void Create(string taskPath, string xml)
        {
            Assert.Null(Xml);
            Path = taskPath;
            Writes++;
            Xml = xml;
            if (ThrowAfterCreate) throw new InvalidOperationException("scheduler disconnected after create");
        }

        public void SetEnabled(string taskPath, bool enabled)
        {
            Writes++;
            var task = XElement.Parse(Xml!);
            var ns = task.Name.Namespace;
            task.Element(ns + "Settings")!.SetElementValue(ns + "Enabled", enabled);
            Xml = task.ToString();
        }

        public void Delete(string taskPath)
        {
            Writes++;
            Xml = null;
        }
    }

    private sealed class FakeRestore(BackgroundStartupRegistration startup) : IJournalSystem
    {
        public Task<RevertOutcome> RestoreAsync(ChangeRecord r)
        {
            startup.Restore(r.UserSid!, r.BackgroundStartup!);
            return Task.FromResult(RevertOutcome.Done);
        }

        public Guid ActivePowerScheme() => throw new NotSupportedException();
        public bool? PowerSchemeExists(Guid scheme) => throw new NotSupportedException();
        public void DeletePowerScheme(Guid scheme) => throw new NotSupportedException();
    }
}
