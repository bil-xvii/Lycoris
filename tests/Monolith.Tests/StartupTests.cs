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

public sealed class PackagedStartupTests
{
    private const string Teams = "MSTeams_8wekyb3d8bbwe";

    /// <summary>The shape JournalTrust accepts for a packaged startup mark (HKCU, value "State", DWORD).</summary>
    private static readonly System.Text.RegularExpressions.Regex JournalShape = new(
        @"^Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData\\" +
        @"[A-Za-z0-9.\-]{3,50}_[a-z0-9]{13}\\[A-Za-z0-9._\-]{1,128}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static StartupEntry Packaged(int? state, string family = Teams, string task = "TeamsTfwStartupTask") => new()
    {
        Source = StartupSource.PackagedTask, Name = task, PackageFamilyName = family, PackageState = state,
        DisplayName = "Microsoft Teams", Command = "ms-teams.exe", Enabled = PackagedStartup.IsEnabled(state),
    };

    [Fact]
    public void Key_and_journal_target_match_what_change_session_and_journal_trust_use()
    {
        var path = PackagedStartup.KeyPath(Teams, "TeamsTfwStartupTask");
        Assert.Equal(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\MSTeams_8wekyb3d8bbwe\TeamsTfwStartupTask", path);
        Assert.Matches(JournalShape, path);
        Assert.Equal($@"reg:HKCU\{path}\State", PackagedStartup.JournalTarget(Teams, "TeamsTfwStartupTask"));
        Assert.Equal(PackagedStartup.JournalTarget(Teams, "TeamsTfwStartupTask"), StartupLocations.JournalTarget(Packaged(2)));
        Assert.Equal("State", PackagedStartup.StateValue);
    }

    [Theory]
    [InlineData(Teams, "TeamsTfwStartupTask", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "Spotify", true)]
    [InlineData("Microsoft.6365217CE6EB4_8wekyb3d8bbwe", "microsoftdefender", true)]
    [InlineData("AppUp.IntelManagementandSecurityStatus_8j3eq9eme6ctt", "IntelManagementandSecurityStatus", true)]
    [InlineData(@"..\Run_8wekyb3d8bbwe", "x", false)]
    [InlineData(Teams, @"a\b", false)]
    [InlineData(Teams, "with space", false)]
    [InlineData("MSTeams_short", "x", false)]
    [InlineData(null, "x", false)]
    [InlineData(Teams, null, false)]
    public void Only_names_the_journal_can_undo_are_writable(string? family, string? task, bool safe)
    {
        Assert.Equal(safe, PackagedStartup.IsJournalSafe(family, task));
        if (safe) Assert.Matches(JournalShape, PackagedStartup.KeyPath(family!, task!));
    }

    [Theory]
    [InlineData(0, false, false, true)]
    [InlineData(1, false, false, true)]
    [InlineData(2, true, false, true)]
    [InlineData(3, false, true, false)]
    [InlineData(4, true, true, false)]
    [InlineData(5, false, false, false)]
    [InlineData(-1, false, false, false)]
    [InlineData(null, false, false, false)]
    public void State_values_follow_StartupTaskState(int? state, bool enabled, bool policy, bool changeable)
    {
        Assert.Equal(enabled, PackagedStartup.IsEnabled(state));
        Assert.Equal(policy, PackagedStartup.IsPolicy(state));
        Assert.Equal(changeable, PackagedStartup.IsUserChangeable(state));
        Assert.Equal(changeable, Packaged(state).CanChange);
        Assert.Equal(changeable, PackagedStartup.WriteRefusal(state) is null);
    }

    [Fact]
    public void Writes_are_what_task_manager_writes_and_refusals_explain_why()
    {
        Assert.Equal(2, PackagedStartup.TargetState(enable: true));
        Assert.Equal(1, PackagedStartup.TargetState(enable: false)); // DisabledByUser: the app cannot turn itself back on
        Assert.Contains("не найдена", PackagedStartup.WriteRefusal(null));
        Assert.Contains("политик", PackagedStartup.WriteRefusal(3));
        Assert.Contains("политик", PackagedStartup.WriteRefusal(4));
        Assert.Contains("неизвестное", PackagedStartup.WriteRefusal(9));
    }

    [Fact]
    public void Unsafe_names_are_never_changeable_even_in_a_user_state()
    {
        Assert.False(Packaged(2, task: "with space").CanChange);
        Assert.True(Packaged(2).CanChange);
        // Classic entries are always changeable (security lock is separate: StartupClassifier.CanDisable).
        Assert.True(new StartupEntry { Source = StartupSource.RunUser, Name = "x", DisplayName = "x", Command = "x" }.CanChange);
    }

    [Fact]
    public void Original_state_from_a_dword_snapshot()
    {
        static RegistryValueSnapshot Dw(string data) => new() { ValueExisted = true, Kind = RegistryValueKind.DWord, Data = data };
        Assert.True(PackagedStartup.OriginalEnabled(Dw("2")));
        Assert.True(PackagedStartup.OriginalEnabled(Dw("4")));
        Assert.False(PackagedStartup.OriginalEnabled(Dw("1")));
        Assert.False(PackagedStartup.OriginalEnabled(Dw("0")));
        Assert.Null(PackagedStartup.OriginalEnabled(Dw("x")));
        Assert.Null(PackagedStartup.OriginalEnabled(new RegistryValueSnapshot { ValueExisted = false }));
        Assert.Null(PackagedStartup.OriginalEnabled(new RegistryValueSnapshot { ValueExisted = true, Kind = RegistryValueKind.Binary, Data = "AgAAAA==" }));

        // StartupChanges picks the rule by source: a DWORD is "enabled" for StartupApproved but means State for packaged tasks.
        Assert.False(StartupChanges.OriginalEnabled(Packaged(2), Dw("1")));
        Assert.True(StartupChanges.OriginalEnabled(new StartupEntry { Source = StartupSource.RunUser, Name = "x", DisplayName = "x", Command = "x" }, Dw("1")));
    }

    [Fact]
    public void Row_identity_and_labels()
    {
        var e = Packaged(2);
        Assert.Equal(@"PackagedTask|MSTeams_8wekyb3d8bbwe\TeamsTfwStartupTask", e.Key);
        Assert.Equal("RunUser|Steam", new StartupEntry { Source = StartupSource.RunUser, Name = "Steam", DisplayName = "Steam", Command = "s" }.Key);
        Assert.False(StartupLocations.IsMachine(StartupSource.PackagedTask));
        Assert.True(StartupLocations.IsPackaged(StartupSource.PackagedTask));
        Assert.Equal("Приложение MSIX", StartupLocations.ShortLabel(StartupSource.PackagedTask));
        Assert.EndsWith(@"SystemAppData\MSTeams_8wekyb3d8bbwe\TeamsTfwStartupTask → State", StartupLocations.DescribeMark(e));
        Assert.Equal("отключено пользователем", PackagedStartup.StateText(1));
        Assert.Equal("отключено политикой", PackagedStartup.StateText(3));
    }

    [Theory]
    [InlineData("MSTeams_24215.1007.3082.1590_x64__8wekyb3d8bbwe", "MSTeams", "x64", "", "8wekyb3d8bbwe", true)]
    [InlineData("NcsiUwpApp_1000.19041.423.0_neutral_neutral_8wekyb3d8bbwe", "NcsiUwpApp", "neutral", "neutral", "8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsTerminal_1.18.0.0_neutral_split.scale-100_8wekyb3d8bbwe", "Microsoft.WindowsTerminal", "neutral", "split.scale-100", "8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.WindowsTerminal_2023.1.0.0_neutral_~_8wekyb3d8bbwe", "Microsoft.WindowsTerminal", "neutral", "~", "8wekyb3d8bbwe", false)]
    public void Package_full_names_are_split(string full, string name, string arch, string resource, string publisher, bool main)
    {
        var p = PackagedStartup.ParseFullName(full);
        Assert.NotNull(p);
        Assert.Equal(name, p!.Name);
        Assert.Equal(arch, p.Architecture);
        Assert.Equal(resource, p.ResourceId);
        Assert.Equal(publisher, p.PublisherId);
        Assert.Equal($"{name}_{publisher}", p.FamilyName);
        Assert.Equal(main, p.IsMain);
    }

    [Fact]
    public void Bad_full_names_and_the_main_package_choice()
    {
        Assert.Null(PackagedStartup.ParseFullName("MSTeams_8wekyb3d8bbwe"));
        Assert.Null(PackagedStartup.ParseFullName(""));
        Assert.Null(PackagedStartup.ParseFullName(null));

        var chosen = PackagedStartup.ChooseMainPackage([
            "Vendor.App_3.0.0.0_neutral_split.scale-150_abcdefghjkmnp",
            "Vendor.App_1.0.0.0_x64__abcdefghjkmnp",
            "Vendor.App_2.5.0.0_x64__abcdefghjkmnp",
            "Vendor.App_9.0.0.0_neutral_~_abcdefghjkmnp",
        ]);
        Assert.Equal("Vendor.App_2.5.0.0_x64__abcdefghjkmnp", chosen?.FullName);
        Assert.Null(PackagedStartup.ChooseMainPackage([]));
    }

    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
                 IgnorableNamespaces="uap uap5 desktop">
          <Identity Name="MSTeams" Publisher="CN=Microsoft Corporation" Version="24215.1007.3082.1590" ProcessorArchitecture="x64" />
          <Properties>
            <DisplayName>Microsoft Teams</DisplayName>
            <PublisherDisplayName>Microsoft Corporation</PublisherDisplayName>
          </Properties>
          <Applications>
            <Application Id="MSTeams" Executable="ms-teams.exe" EntryPoint="Windows.FullTrustApplication">
              <Extensions>
                <uap5:Extension Category="windows.startupTask">
                  <uap5:StartupTask TaskId="TeamsTfwStartupTask" Enabled="true" DisplayName="ms-resource:StartupTaskName" />
                </uap5:Extension>
                <uap:Extension Category="windows.protocol"><uap:Protocol Name="msteams" /></uap:Extension>
              </Extensions>
            </Application>
            <Application Id="Helper" Executable="helper\tool.exe" EntryPoint="Windows.FullTrustApplication">
              <Extensions>
                <desktop:Extension Category="windows.startupTask" Executable="VFS/ProgramFilesX64/Vendor/tray.exe" EntryPoint="Windows.FullTrustApplication">
                  <desktop:StartupTask TaskId="TrayTask" Enabled="false" DisplayName="Vendor tray" />
                </desktop:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void Manifest_startup_tasks_are_read_with_their_executable()
    {
        var m = PackagedStartup.ParseManifest(Manifest);
        Assert.NotNull(m);
        Assert.Equal("MSTeams", m!.Name);
        Assert.Equal("Microsoft Teams", m.DisplayName);
        Assert.Equal("Microsoft Corporation", m.PublisherDisplayName);
        Assert.Equal(2, m.Tasks.Count);

        var teams = m.Tasks[0];
        Assert.Equal("TeamsTfwStartupTask", teams.TaskId);
        Assert.Equal("ms-teams.exe", teams.Executable); // from the Application
        Assert.True(teams.EnabledByDefault);
        Assert.Equal("ms-resource:StartupTaskName", teams.DisplayName);

        var tray = m.Tasks[1];
        Assert.Equal("TrayTask", tray.TaskId);
        Assert.Equal("VFS/ProgramFilesX64/Vendor/tray.exe", tray.Executable); // the extension's own
        Assert.False(tray.EnabledByDefault);
        Assert.Equal("Vendor tray", tray.DisplayName);
    }

    [Fact]
    public void Manifests_with_a_dtd_or_broken_xml_are_refused()
    {
        Assert.Null(PackagedStartup.ParseManifest("<!DOCTYPE Package [<!ENTITY x \"y\">]><Package><Identity Name=\"&x;\" /></Package>"));
        Assert.Null(PackagedStartup.ParseManifest("<Package><Applications>"));
        Assert.Null(PackagedStartup.ParseManifest(""));
        Assert.Empty(PackagedStartup.ParseManifest("<Package xmlns=\"urn:x\"><Identity Name=\"A\" /></Package>")!.Tasks);
    }

    [Fact]
    public void Package_paths_are_joined_with_windows_separators_and_never_climb_out()
    {
        const string root = @"C:\Program Files\WindowsApps\MSTeams_24215.1007.3082.1590_x64__8wekyb3d8bbwe";
        Assert.Equal(root + @"\VFS\ProgramFilesX64\Vendor\tray.exe", PackagedStartup.JoinPackagePath(root, "VFS/ProgramFilesX64/Vendor/tray.exe"));
        Assert.Equal(root + @"\ms-teams.exe", PackagedStartup.JoinPackagePath(root + @"\", @"\ms-teams.exe"));
        Assert.Null(PackagedStartup.JoinPackagePath(root, @"..\..\Windows\System32\cmd.exe"));
        Assert.Null(PackagedStartup.JoinPackagePath(null, "a.exe"));
        Assert.Null(PackagedStartup.JoinPackagePath(root, null));
        Assert.Equal("MSTeams", PackagedStartup.NamePart(Teams));
    }

    [Fact]
    public void Resource_references_become_indirect_strings_and_literals_stay_literal()
    {
        const string full = "MSTeams_24215.1007.3082.1590_x64__8wekyb3d8bbwe";
        Assert.Null(PackagedStartup.IndirectSources("Microsoft Teams", full, "MSTeams"));
        Assert.Equal(new[] { "@{X?ms-resource://a/b}" }, PackagedStartup.IndirectSources("@{X?ms-resource://a/b}", null, null));
        Assert.Equal($"@{{{full}?ms-resource://MSTeams/resources/AppName}}", PackagedStartup.IndirectSources("ms-resource:AppName", full, "MSTeams")![0]);
        Assert.Equal($"@{{{full}?ms-resource://MSTeams/Resources/AppName}}", PackagedStartup.IndirectSources("ms-resource:/Resources/AppName", full, "MSTeams")![0]);
        Assert.Equal($"@{{{full}?ms-resource://Other/res/x}}", PackagedStartup.IndirectSources("ms-resource://Other/res/x", full, "MSTeams")![0]);
        var nested = PackagedStartup.IndirectSources("ms-resource:Strings/Name", full, "MSTeams")!;
        Assert.Equal($"@{{{full}?ms-resource://MSTeams/Strings/Name}}", nested[0]);
        Assert.Equal($"@{{{full}?ms-resource://MSTeams/resources/Strings/Name}}", nested[1]);
        Assert.Empty(PackagedStartup.IndirectSources("ms-resource:AppName", null, null)!);
    }

    [Theory]
    [InlineData("Microsoft.6365217CE6EB4_8wekyb3d8bbwe", "microsoftdefender", null, "Microsoft Corporation", StartupRisk.Security)]
    [InlineData("ESET.Security_abcdefghjkmnp", "Tray", null, null, StartupRisk.Security)]
    [InlineData(Teams, "TeamsTfwStartupTask", @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", "Microsoft Corporation", StartupRisk.Microsoft)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "Spotify", @"C:\Program Files\WindowsApps\Spotify\Spotify.exe", "Spotify AB", StartupRisk.Normal)]
    [InlineData("AppUp.IntelManagementandSecurityStatus_8j3eq9eme6ctt", "IntelManagementandSecurityStatus", null, "INTEL CORP", StartupRisk.Driver)]
    public void Packaged_tasks_are_classified(string family, string task, string? target, string? publisher, StartupRisk expected) =>
        Assert.Equal(expected, StartupClassifier.ClassifyPackagedTask(family, task, target, publisher));
}

public sealed class BootPerformanceTests
{
    private static readonly DateTime BootA = new(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BootB = new(2026, 10, 25, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BootC = new(2026, 10, 20, 7, 0, 0, DateTimeKind.Utc);
    private static readonly Func<DateTime, DateTime> Utc = t => t;

    /// <summary>The log's format: ISO UTC with nine fraction digits.</summary>
    private static string Iso(DateTime t) => t.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'00Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static PerfEvent Boot(DateTime start, int total, int main, int post, bool afterInstall = false, bool degraded = false) =>
        new(100, start.AddMinutes(3), new Dictionary<string, string>
        {
            ["BootStartTime"] = Iso(start), ["BootTime"] = $"{total}", ["MainPathBootTime"] = $"{main}", ["BootPostBootTime"] = $"{post}",
            ["BootIsRebootAfterInstall"] = afterInstall ? "true" : "false", ["BootIsDegradation"] = degraded ? "True" : "False",
        });

    private static PerfEvent Delay(int id, DateTime boot, string name, string? path, int total, int degradation, string? friendly = null) =>
        new(id, boot.AddMinutes(3), new Dictionary<string, string>
        {
            ["StartTime"] = Iso(boot), ["Name"] = name, ["FriendlyName"] = friendly ?? "", ["Path"] = path ?? "",
            ["TotalTime"] = $"{total}", ["DegradationTime"] = $"{degradation}",
        });

    private const string OneDrive = @"C:\Users\u\AppData\Local\Microsoft\OneDrive\OneDrive.exe";

    [Fact]
    public void Event_xml_from_the_log_is_parsed()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-Diagnostics-Performance" Guid="{cfc18ec0-96b1-4eba-961b-622caee05b0a}" />
                <EventID>101</EventID>
                <TimeCreated SystemTime="2023-03-27T08:01:38.1133629Z" />
                <Channel>Microsoft-Windows-Diagnostics-Performance/Operational</Channel>
              </System>
              <EventData>
                <Data Name="StartTime">2023-03-27T07:53:28.803772500Z</Data>
                <Data Name="Name">OneDrive.exe</Data>
                <Data Name="FriendlyName" />
                <Data Name="TotalTime">9658</Data>
                <Data Name="DegradationTime">8658</Data>
                <Data Name="Path">"C:\Users\u\AppData\Local\Microsoft\OneDrive\OneDrive.exe"</Data>
              </EventData>
            </Event>
            """;
        var e = BootPerformance.ParseEventXml(xml);
        Assert.NotNull(e);
        Assert.Equal(101, e!.Id);
        Assert.Equal(new DateTime(2023, 3, 27, 8, 1, 38, DateTimeKind.Utc).AddTicks(1133629), e.CreatedUtc);
        Assert.Equal("9658", e.Data["TotalTime"]);
        Assert.Equal("", e.Data["FriendlyName"]);

        var delay = Assert.Single(BootPerformance.Build([e]).Delays);
        Assert.Equal(OneDrive, delay.Path); // quotes removed
        Assert.Null(delay.FriendlyName);
        Assert.Equal(8658, delay.DegradationMs);
        Assert.Equal(new DateTime(2023, 3, 27, 7, 53, 28, DateTimeKind.Utc).AddTicks(8037725), delay.BootStartUtc);

        Assert.Null(BootPerformance.ParseEventXml("<Event><System><EventID>x</EventID></System></Event>"));
        Assert.Null(BootPerformance.ParseEventXml("<!DOCTYPE e [<!ENTITY a \"b\">]><Event />"));
        Assert.Null(BootPerformance.ParseEventXml(null));
    }

    [Fact]
    public void Nine_fraction_digits_are_read_as_utc()
    {
        var t = BootPerformance.ParseUtc("2014-03-15T11:56:33.687200400Z");
        Assert.Equal(new DateTime(2014, 3, 15, 11, 56, 33, DateTimeKind.Utc).AddTicks(6872004), t);
        Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
        Assert.Null(BootPerformance.ParseUtc("not a date"));
        Assert.Null(BootPerformance.ParseUtc(""));
    }

    [Fact]
    public void Boots_are_newest_first_and_slowdowns_are_kept_only_for_those_boots()
    {
        var events = new[]
        {
            Delay(101, BootA, "OneDrive.exe", OneDrive, 4100, 2400, "Microsoft OneDrive"),
            Delay(103, BootA, "wsearch", @"C:\Windows\system32\SearchIndexer.exe", 900, 800, "Windows Search"),
            Boot(BootA, 41234, 18012, 23222),
            Boot(BootB, 39000, 17000, 22000),
            Delay(101, BootC, "OneDrive.exe", OneDrive, 3000, 1000),
            Boot(BootC, 52000, 20000, 32000, afterInstall: true),
            Boot(BootC, 52000, 20000, 32000, afterInstall: true), // same boot twice
            Delay(101, new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc), "Old.exe", @"C:\Old\Old.exe", 5000, 3000),
            Boot(new DateTime(2026, 9, 1), 0, 0, 0), // invalid duration
        };

        var two = BootPerformance.Build(events, maxBoots: 2);
        Assert.Equal(new[] { BootA, BootB }, two.Boots.Select(b => b.StartUtc));
        Assert.Equal(new[] { "OneDrive.exe", "wsearch" }, two.Delays.Select(d => d.Name)); // only boot A, biggest delay first

        var all = BootPerformance.Build(events);
        Assert.Equal(3, all.Boots.Count);
        Assert.Equal(3, all.Delays.Count); // boot C's slowdown is in, the one before the oldest boot is not
        Assert.DoesNotContain(all.Delays, d => d.Name == "Old.exe");
        Assert.True(all.Boots[2].AfterInstall);
        Assert.Equal(18012, all.Last!.MainPathMs);
    }

    [Fact]
    public void Without_boot_events_only_the_newest_boots_slowdowns_are_kept()
    {
        var r = BootPerformance.Build([
            Delay(101, BootA, "A.exe", @"C:\A\A.exe", 2000, 1000),
            Delay(101, BootB, "B.exe", @"C:\B\B.exe", 2000, 1000),
            Delay(101, BootC, "C.exe", @"C:\C\C.exe", 2000, 1000),
        ], maxBoots: 2);
        Assert.Empty(r.Boots);
        Assert.Null(r.Last);
        Assert.Equal(new[] { "A.exe", "B.exe" }, r.Delays.Select(d => d.Name));
        Assert.Null(BootPerformance.SummaryText(r));
        Assert.Empty(BootPerformance.Build([]).Delays);
    }

    [Fact]
    public void Rows_match_by_full_path_or_by_a_specific_file_name()
    {
        var delays = BootPerformance.Build([
            Delay(101, BootA, "OneDrive.exe", OneDrive, 4100, 2400),
            Delay(101, BootB, "OneDrive.exe", OneDrive, 3000, 900),
            Delay(101, BootA, "Update.exe", @"C:\Users\u\AppData\Local\Discord\Update.exe", 3000, 2000),
            Delay(103, BootA, "Steam.exe", @"C:\Steam\Steam.exe", 3000, 2000), // a service, not an app
            Boot(BootA, 41234, 18012, 23222), Boot(BootB, 39000, 17000, 22000),
        ]).Delays;

        var m = BootPerformance.Match(OneDrive.ToUpperInvariant(), delays);
        Assert.NotNull(m);
        Assert.Equal(BootA, m!.Latest.BootStartUtc);
        Assert.Equal(2400, m.Latest.DegradationMs);
        Assert.Equal(2, m.Boots);

        Assert.NotNull(BootPerformance.Match(@"D:\Other\OneDrive.exe", delays));                       // same specific name
        Assert.NotNull(BootPerformance.Match("\"" + OneDrive + "\"", delays));                           // quoted
        Assert.NotNull(BootPerformance.Match(@"C:\Users\u\AppData\Local\Discord\Update.exe", delays));  // generic name, same path
        Assert.Null(BootPerformance.Match(@"C:\Users\u\AppData\Local\Slack\Update.exe", delays));       // generic name, other path
        Assert.Null(BootPerformance.Match(@"C:\Steam\Steam.exe", delays));                              // only app events tag rows
        Assert.Null(BootPerformance.Match(null, delays));
        Assert.Null(BootPerformance.Match(@"C:\Nothing\here.exe", delays));
    }

    [Theory]
    [InlineData(41234, "41,2 с")]
    [InlineData(18049, "18,0 с")]
    [InlineData(950, "1,0 с")]
    [InlineData(0, "0,0 с")]
    [InlineData(-5, "0,0 с")]
    [InlineData(110680, "110,7 с")]
    public void Seconds_use_a_russian_comma(int ms, string expected) => Assert.Equal(expected, BootPerformance.Seconds(ms));

    [Fact]
    public void Summary_line_and_tooltips()
    {
        var two = BootPerformance.Build([
            Delay(101, BootA, "OneDrive.exe", OneDrive, 4100, 2400, "Microsoft OneDrive"),
            Boot(BootA, 41234, 18012, 23222), Boot(BootB, 39000, 17000, 22000),
        ]);
        Assert.Equal("Последняя загрузка (26.10 07:00): 41,2 с — экран входа через 18,0 с, ещё 23,2 с до готовности",
            BootPerformance.SummaryText(two, Utc));

        var three = BootPerformance.Build([Boot(BootA, 41000, 18000, 23000, afterInstall: true), Boot(BootB, 39000, 17000, 22000), Boot(BootC, 49000, 20000, 29000)]);
        Assert.Equal("Последняя загрузка (26.10 07:00): 41,0 с — экран входа через 18,0 с, ещё 23,0 с до готовности, " +
                     "после установки обновлений · в среднем 43,0 с за 3 загрузки", BootPerformance.SummaryText(three, Utc));
        Assert.Null(BootPerformance.SummaryText(null));

        var tip = BootPerformance.SummaryTooltip(two, Utc)!;
        Assert.Contains("26.10 07:00 — 41,2 с", tip);
        Assert.Contains("25.10 07:00 — 39,0 с", tip);
        Assert.Contains("Программа Microsoft OneDrive: +2,4 с", tip);
        Assert.Contains("быстрого запуска", tip);
        Assert.Null(BootPerformance.SummaryTooltip(null));

        var match = BootPerformance.Match(OneDrive, two.Delays)!;
        var tag = BootPerformance.DelayTooltip(match, Utc);
        Assert.Contains("26.10 07:00", tag);
        Assert.Contains("4,1 с", tag);
        Assert.Contains("на 2,4 с дольше обычного", tag);
        Assert.Contains("событие 101", tag);
        Assert.DoesNotContain("Такое было", tag);
        Assert.Contains("в 2 загрузках", BootPerformance.DelayTooltip(match with { Boots = 2 }, Utc));
    }

    [Fact]
    public void File_names_split_on_windows_separators_on_every_os()
    {
        Assert.Equal("OneDrive.exe", BootPerformance.FileName(OneDrive));
        Assert.Equal("a.exe", BootPerformance.FileName("C:/x/a.exe"));
        Assert.Null(BootPerformance.FileName(@"C:\dir\"));
        Assert.Equal(@"C:\x\a.exe", BootPerformance.NormalizePath(@" ""\\?\C:\x\a.exe"" "));
        Assert.Equal(@"C:\Windows\system32\drivers\x.sys", BootPerformance.NormalizePath(@"\??\C:\Windows\system32\drivers\x.sys"));
    }
}
