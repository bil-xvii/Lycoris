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
            BatchId = batch.Id, Source = "services", Title = "forged", Kind = ChangeKind.Service, Target = "svc:WinDefend",
            Service = new ServiceStartSnapshot("WinDefend", ServiceStartMode.Disabled, false),
        });
        var result = await journal.RevertSourceAsync("services");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("WinDefend") || e.Contains("критична"));
    }

    private static ChangeRecord Command(string source, string target, string file, string args) => new()
    {
        Source = source, Title = "t", Kind = ChangeKind.Command, Target = "cmd:" + target, RevertFile = file, RevertArgs = args,
    };

    private static string Cmd => ProcessRunner.Sys("cmd.exe");
    private static string Q(string exe) => "\"" + ProcessRunner.Sys(exe) + "\"";

    [Fact]
    public void Catalog_undo_commands_are_accepted()
    {
        var scheme = Guid.NewGuid(); var sub = Guid.NewGuid(); var setting = Guid.NewGuid();
        var pc = Q("powercfg.exe");
        var powercfg = TweakCatalog.CmdArgs($"{pc} /setacvalueindex {scheme:D} {sub:D} {setting:D} 3 && {pc} /setactive SCHEME_CURRENT");
        Assert.Null(JournalTrust.Check(Command("tweak:x", $"powercfg:{scheme:D}:{setting:D}:ac", Cmd, powercfg)));

        Assert.Null(JournalTrust.Check(Command("tweak:hibernate-off", "hibernate", Cmd, TweakCatalog.CmdArgs($"{pc} /hibernate on"))));

        var g4 = $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}";
        Assert.Null(JournalTrust.Check(Command("tweak:dns", $"dns4:{g4}", TweakCatalog.PowerShellExe,
            TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript("ipv4", g4, ["1.1.1.1", "8.8.8.8"])))));
        var g6 = $"{{{Guid.NewGuid()}}}";
        Assert.Null(JournalTrust.Check(Command("tweak:dns", $"dns6:{g6}", TweakCatalog.PowerShellExe,
            TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript("ipv6", g6, [])))));

        foreach (var value in new[] { null, "0", "it's;odd" })
            Assert.Null(JournalTrust.Check(Command("tweak:dx", "dx:VRROptimizeEnable", TweakCatalog.PowerShellExe,
                TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("VRROptimizeEnable", value)))));

        var spi = TweakCatalog.VisualEffectFlags.Select((f, i) => (f.Set, i % 2 == 0)).ToList();
        Assert.Null(JournalTrust.Check(Command("tweak:visual", "spi:visual-effects", TweakCatalog.PowerShellExe,
            TweakCatalog.PowerShellArgs(TweakCatalog.SpiScript(spi, 1)))));

        var pnputil = ProcessRunner.Sys("pnputil.exe");
        Assert.Null(JournalTrust.Check(Command("drivers", @"device-enable:USB\VID_046D&PID_C52B\5&2B3C&0&3", pnputil,
            @"/disable-device ""USB\VID_046D&PID_C52B\5&2B3C&0&3""")));
    }

    [Fact]
    public void Tampered_undo_commands_are_refused()
    {
        var pc = Q("powercfg.exe");
        // Right program, appended payload.
        Assert.NotNull(JournalTrust.Check(Command("tweak:hibernate-off", "hibernate", Cmd,
            TweakCatalog.CmdArgs($"{pc} /hibernate on & C:\\Users\\Public\\x.exe"))));
        // Allowed program, arbitrary command.
        Assert.NotNull(JournalTrust.Check(Command("tweak:x", "anything", Cmd, TweakCatalog.CmdArgs("C:\\Users\\Public\\x.exe"))));
        // PowerShell with a foreign script.
        Assert.NotNull(JournalTrust.Check(Command("tweak:dx", "dx:VRROptimizeEnable", TweakCatalog.PowerShellExe,
            TweakCatalog.PowerShellArgs("Start-Process C:\\Users\\Public\\x.exe"))));
        // A dx script with something added after the catalog script.
        Assert.NotNull(JournalTrust.Check(Command("tweak:dx", "dx:VRROptimizeEnable", TweakCatalog.PowerShellExe,
            TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("VRROptimizeEnable", "0") + ";Start-Process x.exe"))));
        // DNS undo script with an extra command, or one aimed at another adapter.
        var g = $"{{{Guid.NewGuid()}}}";
        Assert.NotNull(JournalTrust.Check(Command("tweak:dns", $"dns4:{g}", TweakCatalog.PowerShellExe,
            TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript("ipv4", g, ["1.1.1.1"]) + ";Start-Process calc.exe"))));
        Assert.NotNull(JournalTrust.Check(Command("tweak:dns", $"dns4:{g}", TweakCatalog.PowerShellExe,
            TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript("ipv4", $"{{{Guid.NewGuid()}}}", ["1.1.1.1"])))));
        // Device ID that breaks out of the quotes.
        var pnputil = ProcessRunner.Sys("pnputil.exe");
        Assert.NotNull(JournalTrust.Check(Command("drivers", "device-enable:X\" /restart", pnputil, "/disable-device \"X\" /restart\"")));
        // Command records from a module that never writes commands.
        Assert.NotNull(JournalTrust.Check(Command("gameboost", "hibernate", Cmd, TweakCatalog.CmdArgs($"{pc} /hibernate on"))));
    }

    [Fact]
    public void Unknown_sources_and_mismatched_records_are_refused()
    {
        var reg = new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = @"Software\Microsoft\GameBar", Name = "AutoGameModeEnabled" };
        ChangeRecord Registry(string source, string target) => new()
            { Source = source, Kind = ChangeKind.Registry, Target = target, Registry = reg };
        var target = @"reg:HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled";

        Assert.Null(JournalTrust.Check(Registry("tweak:game-mode", target)));
        Assert.NotNull(JournalTrust.Check(Registry("evil", target)));
        Assert.NotNull(JournalTrust.Check(Registry("tweak:game-mode", @"reg:HKCU\Software\Other\Name")));
        Assert.NotNull(JournalTrust.Check(Registry("startup", target))); // startup writes only StartupApproved marks
        Assert.NotNull(JournalTrust.Check(Registry("services", target))); // services writes only service records

        var approved = new RegistryValueSnapshot
            { Hive = RegHive.HKLM, Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", Name = "App" };
        Assert.Null(JournalTrust.Check(new ChangeRecord
        {
            Source = "startup", Kind = ChangeKind.Registry, Registry = approved,
            Target = @"reg:HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32\App",
        }));

        ChangeRecord Service(string name) => new()
        {
            Source = "services", Kind = ChangeKind.Service, Target = "svc:" + name,
            Service = new ServiceStartSnapshot(name, ServiceStartMode.Manual, false),
        };
        Assert.Null(JournalTrust.Check(Service("DiagTrack")));
        Assert.NotNull(JournalTrust.Check(Service("mpssvc")));   // protected
        Assert.NotNull(JournalTrust.Check(Service("SysMain")));  // ServiceGuard hard block
        Assert.Null(JournalTrust.Check(Service("W32Time")));     // hard-blocked later: an old record may restore Manual
        Assert.NotNull(JournalTrust.Check(new ChangeRecord
        {
            Source = "services", Kind = ChangeKind.Service, Target = "svc:W32Time",
            Service = new ServiceStartSnapshot("W32Time", ServiceStartMode.Disabled, false),
        }));                                                     // …but never Disabled
    }

    [Fact]
    public void Record_is_not_kept_when_the_journal_cannot_be_written()
    {
        // The journal's folder is a file, so nothing can ever be saved.
        var blocker = Path.Combine(Path.GetTempPath(), $"monolith-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "");
        try
        {
            var journal = new ChangeJournal(Path.Combine(blocker, "journal.json"));
            Assert.Throws<InvalidOperationException>(() => journal.BeginBatch("x"));
            Assert.Throws<InvalidOperationException>(() => journal.Add(new ChangeRecord { Source = "tweak:x", Target = "reg:x" }));
            Assert.Empty(journal.Records);
            Assert.Empty(journal.Batches);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Both_copies_damaged_keeps_files_and_blocks_changes()
    {
        File.WriteAllText(_journalPath, "{ broken");
        File.WriteAllText(_journalPath + ".bak", "also broken");
        var journal = new ChangeJournal(_journalPath);
        Assert.True(journal.IsUnavailable);
        Assert.Throws<InvalidOperationException>(() => journal.BeginBatch("x"));
        Assert.False(journal.Save());
        Assert.Equal("{ broken", File.ReadAllText(_journalPath));
        Assert.Equal("also broken", File.ReadAllText(_journalPath + ".bak"));
        foreach (var c in Directory.GetFiles(Path.GetDirectoryName(_journalPath)!, Path.GetFileName(_journalPath) + ".broken-*"))
            File.Delete(c);
    }

    [Fact]
    public void Missing_journal_uses_the_backup()
    {
        var good = new ChangeJournal(_journalPath);
        good.BeginBatch("first");
        good.BeginBatch("second");
        File.Delete(_journalPath);
        Assert.NotEmpty(new ChangeJournal(_journalPath).Batches);
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
