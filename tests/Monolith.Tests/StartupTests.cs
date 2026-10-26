using System.IO;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Startup;

namespace Monolith.Tests;

public sealed class StartupApprovedTests
{
    [Fact]
    public void Missing_or_empty_value_means_enabled()
    {
        Assert.True(StartupApproved.IsEnabled(null));
        Assert.True(StartupApproved.IsEnabled([]));
    }

    [Theory]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x04, true)]   // HKLM SecurityHealth on 26300: shown as enabled by Task Manager
    [InlineData((byte)0x06, true)]
    [InlineData((byte)0x07, false)]
    [InlineData((byte)0x01, false)]
    [InlineData((byte)0x00, true)]
    public void Low_bit_of_first_byte_is_the_disabled_flag(byte flag, bool enabled)
    {
        var data = new byte[12];
        data[0] = flag;
        Assert.Equal(enabled, StartupApproved.IsEnabled(data));
    }

    [Fact]
    public void Enabled_bytes_match_task_manager()
    {
        Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupApproved.Build(true, DateTime.UtcNow));
    }

    [Fact]
    public void Disabled_bytes_carry_the_filetime_little_endian()
    {
        var when = new DateTime(2026, 10, 26, 18, 30, 15, DateTimeKind.Utc);
        var data = StartupApproved.Build(false, when);

        Assert.Equal(12, data.Length);
        Assert.Equal(new byte[] { 0x03, 0, 0, 0 }, data[..4]);
        Assert.Equal(when.ToFileTimeUtc(), BitConverter.ToInt64(data, 4));
        Assert.False(StartupApproved.IsEnabled(data));
        Assert.Equal(when, StartupApproved.DisabledAtUtc(data));
    }

    [Fact]
    public void Disabled_time_is_parsed_from_a_real_task_manager_value()
    {
        // 03 00 00 00 + FILETIME 0x01DC4697_8F1C2E00
        byte[] data = [0x03, 0x00, 0x00, 0x00, 0x00, 0x2E, 0x1C, 0x8F, 0x97, 0x46, 0xDC, 0x01];
        var at = StartupApproved.DisabledAtUtc(data);
        Assert.NotNull(at);
        Assert.Equal(DateTime.FromFileTimeUtc(0x01DC46978F1C2E00), at);
        Assert.Equal(DateTimeKind.Utc, at!.Value.Kind);
    }

    [Fact]
    public void No_disabled_time_for_enabled_short_zero_or_invalid_values()
    {
        Assert.Null(StartupApproved.DisabledAtUtc(StartupApproved.Build(true, DateTime.UtcNow)));
        Assert.Null(StartupApproved.DisabledAtUtc([0x03, 0, 0, 0]));                       // too short
        Assert.Null(StartupApproved.DisabledAtUtc([0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])); // zero FILETIME
        Assert.Null(StartupApproved.DisabledAtUtc([0x03, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F])); // out of range
        Assert.Null(StartupApproved.DisabledAtUtc([0x03, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])); // negative
    }

    [Fact]
    public void Round_trip_build_parse()
    {
        var now = DateTime.UtcNow;
        var off = StartupApproved.Build(false, now);
        var on = StartupApproved.Build(true, now);
        Assert.False(StartupApproved.IsEnabled(off));
        Assert.True(StartupApproved.IsEnabled(on));
        Assert.Equal(now.ToFileTimeUtc(), StartupApproved.DisabledAtUtc(off)!.Value.ToFileTimeUtc());
    }

    [Fact]
    public void Hex_dump_is_spaced()
    {
        Assert.Equal("02 00 00 00 00 00 00 00 00 00 00 00", StartupApproved.ToHex(StartupApproved.Build(true, DateTime.UtcNow)));
        Assert.Equal("", StartupApproved.ToHex([]));
    }

    [Fact]
    public void Original_state_from_journal_snapshot()
    {
        Assert.True(StartupChanges.OriginalEnabled(new RegistryValueSnapshot { ValueExisted = false }));
        Assert.True(StartupChanges.OriginalEnabled(new RegistryValueSnapshot
        {
            ValueExisted = true, Kind = RegistryValueKind.Binary, Data = Convert.ToBase64String(StartupApproved.Build(true, DateTime.UtcNow)),
        }));
        Assert.False(StartupChanges.OriginalEnabled(new RegistryValueSnapshot
        {
            ValueExisted = true, Kind = RegistryValueKind.Binary, Data = Convert.ToBase64String(StartupApproved.Build(false, DateTime.UtcNow)),
        }));
        Assert.True(StartupChanges.OriginalEnabled(new RegistryValueSnapshot { ValueExisted = true, Kind = RegistryValueKind.String, Data = "x" }));
        Assert.True(StartupChanges.OriginalEnabled(new RegistryValueSnapshot { ValueExisted = true, Kind = RegistryValueKind.Binary, Data = "%%%" }));
    }
}

public sealed class StartupLocationTests
{
    [Theory]
    [InlineData(StartupSource.RunUser, RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")]
    [InlineData(StartupSource.RunMachine, RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")]
    [InlineData(StartupSource.Run32Machine, RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32")]
    [InlineData(StartupSource.FolderUser, RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder")]
    [InlineData(StartupSource.FolderCommon, RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder")]
    public void Approval_key_per_source(StartupSource source, RegHive hive, string key)
    {
        Assert.Equal(hive, StartupLocations.ApprovedHive(source));
        Assert.Equal(key, StartupLocations.ApprovedKey(source));
    }

    [Fact]
    public void Journal_target_matches_change_session_format()
    {
        Assert.Equal(@"reg:HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\Steam",
            StartupLocations.JournalTarget(StartupSource.RunUser, "Steam"));
        Assert.Equal(@"reg:HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder\Tool.lnk",
            StartupLocations.JournalTarget(StartupSource.FolderCommon, "Tool.lnk"));
    }

    [Fact]
    public void Scope_is_machine_for_hklm_and_common_folder()
    {
        Assert.False(StartupLocations.IsMachine(StartupSource.RunUser));
        Assert.False(StartupLocations.IsMachine(StartupSource.FolderUser));
        Assert.True(StartupLocations.IsMachine(StartupSource.RunMachine));
        Assert.True(StartupLocations.IsMachine(StartupSource.Run32Machine));
        Assert.True(StartupLocations.IsMachine(StartupSource.FolderCommon));
    }
}

public sealed class CommandLineParserTests
{
    private static readonly string[] Dirs = [@"C:\Windows\System32", @"C:\Windows"];

    private static CommandTarget Parse(string command, params string[] existing)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var r = CommandLineParser.Parse(command, set.Contains, Expand, Dirs);
        Assert.NotNull(r);
        return r!;
    }

    private static string Expand(string s) => s
        .Replace("%windir%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%LOCALAPPDATA%", @"C:\Users\u\AppData\Local", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Quoted_path_with_arguments()
    {
        var r = Parse("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent");
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", r.Executable);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", r.Target);
        Assert.Equal("-silent", r.Arguments);
        Assert.False(r.ViaHost);
    }

    [Fact]
    public void Quoted_path_without_arguments_and_unterminated_quote()
    {
        Assert.Equal(@"C:\Apps\My App\app.exe", Parse("\"C:\\Apps\\My App\\app.exe\"").Target);
        Assert.Equal("", Parse("\"C:\\Apps\\My App\\app.exe\"").Arguments);
        Assert.Equal(@"C:\Apps\My App\app.exe", Parse("\"C:\\Apps\\My App\\app.exe").Target);
    }

    [Fact]
    public void Unquoted_path_with_arguments()
    {
        var r = Parse(@"C:\Windows\System32\OneDriveSetup.exe /thfirstsetup");
        Assert.Equal(@"C:\Windows\System32\OneDriveSetup.exe", r.Target);
        Assert.Equal("/thfirstsetup", r.Arguments);
    }

    [Fact]
    public void Unquoted_path_with_spaces_uses_the_existing_file()
    {
        var r = Parse(@"C:\Program Files\Foo Bar\foo.exe --minimized --tray", @"C:\Program Files\Foo Bar\foo.exe");
        Assert.Equal(@"C:\Program Files\Foo Bar\foo.exe", r.Target);
        Assert.Equal("--minimized --tray", r.Arguments);
    }

    [Fact]
    public void Unquoted_path_with_spaces_and_missing_file_falls_back_to_the_exe_token()
    {
        var r = Parse(@"C:\Program Files\Gone App\gone.exe -background");
        Assert.Equal(@"C:\Program Files\Gone App\gone.exe", r.Target);
        Assert.Equal("-background", r.Arguments);
    }

    [Fact]
    public void Shortest_existing_prefix_wins_like_create_process()
    {
        // C:\Program.exe exists → CreateProcess would run it, so that is what the entry really starts.
        var r = Parse(@"C:\Program Files\Foo\foo.exe", @"C:\Program.exe", @"C:\Program Files\Foo\foo.exe");
        Assert.Equal(@"C:\Program.exe", r.Target);
        Assert.Equal(@"Files\Foo\foo.exe", r.Arguments);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var r = Parse(@"%windir%\system32\SecurityHealthSystray.exe");
        Assert.Equal(@"C:\Windows\system32\SecurityHealthSystray.exe", r.Target);

        var q = Parse("\"%ProgramFiles%\\Vendor\\tray.exe\" /start");
        Assert.Equal(@"C:\Program Files\Vendor\tray.exe", q.Target);
        Assert.Equal("/start", q.Arguments);
    }

    [Fact]
    public void Missing_extension_gets_exe_and_bare_names_use_the_search_path()
    {
        var r = Parse("ctfmon", @"C:\Windows\System32\ctfmon.exe");
        Assert.Equal(@"C:\Windows\System32\ctfmon.exe", r.Target);

        var s = Parse(@"C:\Tools\agent -q", @"C:\Tools\agent.exe");
        Assert.Equal(@"C:\Tools\agent.exe", s.Target);
        Assert.Equal("-q", s.Arguments);
    }

    [Fact]
    public void Bare_name_not_on_the_search_path_stays_bare()
    {
        var r = Parse("unknowntool.exe /x");
        Assert.Equal("unknowntool.exe", r.Target);
        Assert.Equal("/x", r.Arguments);
    }

    [Fact]
    public void Rundll32_target_is_the_dll()
    {
        var r = Parse(@"rundll32.exe C:\Windows\System32\shell32.dll,Control_RunDLL desk.cpl",
            @"C:\Windows\System32\rundll32.exe", @"C:\Windows\System32\shell32.dll");
        Assert.Equal(@"C:\Windows\System32\rundll32.exe", r.Executable);
        Assert.Equal(@"C:\Windows\System32\shell32.dll", r.Target);
        Assert.True(r.ViaHost);
    }

    [Fact]
    public void Rundll32_quoted_dll_with_spaces()
    {
        var r = Parse("\"C:\\Windows\\system32\\rundll32.exe\" \"C:\\Program Files\\Vendor\\helper.dll\",Start -x");
        Assert.Equal(@"C:\Program Files\Vendor\helper.dll", r.Target);
        Assert.True(r.ViaHost);
    }

    [Fact]
    public void Rundll32_unquoted_dll_with_spaces_is_cut_at_the_comma()
    {
        var r = Parse(@"rundll32 C:\Program Files\Vendor\helper.dll,Start", @"C:\Windows\System32\rundll32.exe");
        Assert.Equal(@"C:\Program Files\Vendor\helper.dll", r.Target);
    }

    [Fact]
    public void Rundll32_switches_and_bare_dll_names_are_resolved_through_system32()
    {
        var r = Parse(@"%windir%\system32\rundll32.exe /d acproxy.dll,PerformAutochkOperations",
            @"C:\Windows\system32\rundll32.exe", @"C:\Windows\System32\acproxy.dll");
        Assert.Equal(@"C:\Windows\System32\acproxy.dll", r.Target);

        var noExt = Parse("rundll32 printui,PrintUIEntry /il", @"C:\Windows\System32\printui.dll");
        Assert.Equal(@"C:\Windows\System32\printui.dll", noExt.Target);
    }

    [Fact]
    public void Rundll32_space_separated_entry_point()
    {
        Assert.Equal("lib.dll", CommandLineParser.ExtractRundllTarget("lib.dll Entry a,b"));
        Assert.Equal("lib.dll", CommandLineParser.ExtractRundllTarget(" lib.dll,Entry"));
        Assert.Null(CommandLineParser.ExtractRundllTarget("   "));
        Assert.Null(CommandLineParser.ExtractRundllTarget("/d"));
    }

    [Fact]
    public void Script_hosts_point_to_the_script()
    {
        var r = Parse("wscript.exe //B //NoLogo \"C:\\Users\\u\\Scripts\\start up.vbs\" arg", @"C:\Windows\System32\wscript.exe");
        Assert.Equal(@"C:\Users\u\Scripts\start up.vbs", r.Target);
        Assert.True(r.ViaHost);

        var c = Parse(@"cscript C:\s\run.js");
        Assert.Equal(@"C:\s\run.js", c.Target);
    }

    [Fact]
    public void Empty_commands_give_null()
    {
        Assert.Null(CommandLineParser.Parse(null));
        Assert.Null(CommandLineParser.Parse("   "));
        Assert.Null(CommandLineParser.Parse("\"\"", _ => false, s => s, Dirs));
    }

    [Fact]
    public void Join_quotes_paths_with_spaces()
    {
        Assert.Equal("\"C:\\Program Files\\A\\a.exe\" -x", CommandLineParser.Join(@"C:\Program Files\A\a.exe", "-x"));
        Assert.Equal(@"C:\A\a.exe", CommandLineParser.Join(@"C:\A\a.exe", null));
        Assert.Equal("\"C:\\B C\\b.exe\"", CommandLineParser.Join("\"C:\\B C\\b.exe\"", "  "));
    }

    [Fact]
    public void Real_file_system_resolution_finds_system32_tools()
    {
        var r = CommandLineParser.Parse("rundll32.exe shell32.dll,Control_RunDLL");
        Assert.NotNull(r);
        Assert.True(File.Exists(r!.Executable));
        Assert.EndsWith("shell32.dll", r.Target, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(r.Target));
    }
}

public sealed class StartupClassifierTests
{
    [Theory]
    [InlineData(@"C:\Windows\system32\SecurityHealthSystray.exe", "Microsoft Corporation", StartupRisk.Security)]
    [InlineData(@"C:\Program Files\Kaspersky Lab\avpui.exe", null, StartupRisk.Security)]
    [InlineData(@"C:\Program Files\ESET\egui.exe", "ESET, spol. s r.o.", StartupRisk.Security)]
    [InlineData(@"C:\x\tray.exe", "Kaspersky Lab", StartupRisk.Security)]
    [InlineData(@"C:\Program Files\Realtek\RtkAudUService64.exe", "Realtek Semiconductor", StartupRisk.Driver)]
    [InlineData(@"C:\x\nv.exe", "NVIDIA Corporation", StartupRisk.Driver)]
    [InlineData(@"C:\x\amd.exe", "Advanced Micro Devices, Inc.", StartupRisk.Driver)]
    [InlineData(@"C:\x\amd.exe", "AMD", StartupRisk.Driver)]
    [InlineData(@"C:\x\igfx.exe", "Intel Corporation", StartupRisk.Driver)]
    [InlineData(@"C:\x\game.exe", "Intelligent Systems", StartupRisk.Normal)]
    [InlineData(@"C:\x\msedge.exe", "Microsoft Corporation", StartupRisk.Microsoft)]
    [InlineData(@"C:\x\onedrive.exe", "Корпорация Майкрософт", StartupRisk.Microsoft)]
    [InlineData(@"C:\x\steam.exe", "Valve", StartupRisk.Normal)]
    [InlineData(@"C:\x\CCleaner64.exe", "Gen Digital Inc.", StartupRisk.Normal)]
    [InlineData(null, null, StartupRisk.Normal)]
    public void Entries(string? path, string? publisher, StartupRisk expected) =>
        Assert.Equal(expected, StartupClassifier.ClassifyEntry(path, publisher));

    [Theory]
    [InlineData("GoogleUpdateTaskMachineUA", @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "Google LLC", StartupRisk.Updater)]
    [InlineData("Adobe Acrobat Update Task", @"C:\x\AdobeARM.exe", "Adobe Inc.", StartupRisk.Updater)]
    [InlineData("Discord Launcher", @"C:\x\Updater.exe", "Discord Inc.", StartupRisk.Updater)]
    [InlineData("Kaspersky Tray", @"C:\x\avpui.exe", "AO Kaspersky Lab", StartupRisk.Security)]
    [InlineData("NvTmRep", @"C:\x\nvtm.exe", "NVIDIA Corporation", StartupRisk.Driver)]
    [InlineData("MyTool Autostart", @"C:\x\tool.exe", "Vendor", StartupRisk.Normal)]
    public void Tasks(string name, string? path, string? publisher, StartupRisk expected) =>
        Assert.Equal(expected, StartupClassifier.ClassifyTask(name, path, publisher));

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", null, true)]
    [InlineData(@"\MicrosoftEdgeUpdateTaskMachineCore{1A2B}", "", true)]
    [InlineData(@"\OneDrive Reporting Task-S-1-5-21", "Microsoft Corporation", true)]
    [InlineData(@"\Something", "Корпорация Майкрософт", true)]
    [InlineData(@"\Something", "$(@%SystemRoot%\\system32\\x.dll,-600)", true)]
    [InlineData(@"\GoogleUpdateTaskMachineUA", "Google LLC", false)]
    [InlineData(@"\Vendor\Tray", null, false)]
    [InlineData(@"\MicrosoftFake", "Vendor", false)]
    public void System_tasks_are_hidden(string path, string? author, bool expected) =>
        Assert.Equal(expected, StartupClassifier.IsSystemTask(path, author));

    [Fact]
    public void Lock_applies_to_security_and_updaters_only()
    {
        Assert.False(StartupClassifier.CanDisable(StartupRisk.Security));
        Assert.False(StartupClassifier.CanDisable(StartupRisk.Updater));
        Assert.True(StartupClassifier.CanDisable(StartupRisk.Normal));
        Assert.True(StartupClassifier.CanDisable(StartupRisk.Microsoft));
        Assert.True(StartupClassifier.CanDisable(StartupRisk.Driver));
    }
}

public sealed class StartupScanSmokeTests
{
    [Fact]
    public void Startup_scan_reads_the_real_system_without_throwing()
    {
        var entries = StartupScanner.Scan();
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(e.Command));
        });
    }

    [Fact]
    public void Task_scan_reads_the_scheduler_without_throwing()
    {
        var all = LogonTaskScanner.Scan(includeSystem: true);
        var thirdParty = LogonTaskScanner.Scan();
        Assert.All(thirdParty, t => Assert.False(StartupClassifier.IsSystemTask(t.Path, t.Author)));
        Assert.All(all, t => Assert.True(t.AtLogon || t.AtBoot));
        Assert.True(all.Count >= thirdParty.Count);
    }

    [Fact]
    public void File_probe_distinguishes_files_directories_and_missing()
    {
        Assert.True(FileProbe.FileExists(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
        Assert.False(FileProbe.FileExists(Environment.SystemDirectory));
        Assert.True(FileProbe.DirectoryExists(Environment.SystemDirectory));
        Assert.False(FileProbe.FileExists(Path.Combine(Environment.SystemDirectory, "monolith-no-such-file-7f3a.exe")));
        Assert.False(FileProbe.FileExists("relative.exe"));
        Assert.False(FileProbe.FileExists(null));
    }

    [Theory]
    [InlineData(1, "запись")]
    [InlineData(2, "записи")]
    [InlineData(5, "записей")]
    [InlineData(11, "записей")]
    [InlineData(21, "запись")]
    [InlineData(22, "записи")]
    [InlineData(112, "записей")]
    public void Russian_plural(int n, string expected) =>
        Assert.Equal(expected, StartupViewModel.Plural(n, "запись", "записи", "записей"));
}
