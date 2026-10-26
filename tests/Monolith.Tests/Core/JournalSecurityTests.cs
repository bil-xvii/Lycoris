using System.IO;
using System.ServiceProcess;
using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Tests.Core;

/// <summary>A forged or tampered journal must never be able to run programs or hijack Windows on undo.</summary>
[Collection("Registry")]
public sealed class JournalSecurityTests : IDisposable
{
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"monolith-sec-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe", "Debugger")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "evil")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", "evil")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\SomeSvc", "ImagePath")]
    [InlineData(@"SOFTWARE\Classes\exefile\shell\open\command", "")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL")]
    public void Hijack_locations_are_refused(string path, string name) =>
        Assert.True(ChangeJournal.IsForbiddenRestoreTarget(new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = path, Name = name }));

    [Theory]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Discord")]
    [InlineData(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry")]
    public void Normal_tweak_locations_are_allowed(string path, string name) =>
        Assert.False(ChangeJournal.IsForbiddenRestoreTarget(new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = path, Name = name }));

    [Fact]
    public async Task Forged_command_record_is_not_executed()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"monolith-pwned-{Guid.NewGuid():N}.txt");
        var journal = new ChangeJournal(_journalPath);
        var batch = journal.BeginBatch("forged");
        journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = "gameboost", Title = "forged", Kind = ChangeKind.Command, Target = "cmd:forged",
            // Not one of the allowed System32 tools: must be refused before anything runs.
            RevertFile = Path.Combine(Path.GetTempPath(), "x.exe"), RevertArgs = $"> \"{marker}\"",
        });
        var result = await journal.RevertSourceAsync("gameboost");
        Assert.False(result.Success);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Forged_protected_service_record_is_refused()
    {
        var journal = new ChangeJournal(_journalPath);
        var batch = journal.BeginBatch("forged");
        journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = "x", Title = "forged", Kind = ChangeKind.Service, Target = "svc:WinDefend",
            Service = new ServiceStartSnapshot("WinDefend", ServiceStartMode.Disabled, false),
        });
        var result = await journal.RevertSourceAsync("x");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("WinDefend") || e.Contains("критична"));
    }

    [Fact]
    public void Corrupt_journal_falls_back_to_backup()
    {
        var good = new ChangeJournal(_journalPath);
        good.BeginBatch("first");
        good.BeginBatch("second"); // second save rotates the first version into .bak
        File.WriteAllText(_journalPath, "{ broken");
        var reloaded = new ChangeJournal(_journalPath);
        Assert.NotEmpty(reloaded.Batches);
        foreach (var c in Directory.GetFiles(Path.GetDirectoryName(_journalPath)!, Path.GetFileName(_journalPath) + ".broken-*"))
            File.Delete(c);
    }

    [Fact]
    public void Machine_dir_paths_are_recognized()
    {
        Assert.True(AppPaths.IsUnderMachineDir(AppPaths.JournalFile));
        Assert.True(AppPaths.IsUnderMachineDir(AppPaths.BackupsDir));
        Assert.False(AppPaths.IsUnderMachineDir(AppPaths.SettingsFile));
        Assert.False(AppPaths.IsUnderMachineDir(AppPaths.MachineDir + "-evil\\x"));
    }

    [Theory]
    [InlineData(ServiceStartMode.Manual, ServiceStartMode.Manual, true)]
    [InlineData(ServiceStartMode.Disabled, ServiceStartMode.Manual, true)]
    [InlineData(ServiceStartMode.Automatic, ServiceStartMode.Manual, false)]
    [InlineData(ServiceStartMode.Manual, ServiceStartMode.Disabled, false)]
    public void Stricter_service_state_is_never_loosened(ServiceStartMode current, ServiceStartMode target, bool satisfied) =>
        Assert.Equal(satisfied, ServiceTweak.Satisfies(current, target));
}
