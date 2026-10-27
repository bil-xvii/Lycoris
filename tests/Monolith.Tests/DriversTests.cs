using System.IO;
using System.Runtime.InteropServices;
using Monolith.Core;
using Monolith.Modules.Drivers;

namespace Monolith.Tests;

public sealed class DriversTests
{
    // ───────────── Device problem codes ─────────────

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(24)]
    [InlineData(28)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(37)]
    [InlineData(39)]
    [InlineData(43)]
    [InlineData(45)]
    [InlineData(48)]
    [InlineData(52)]
    public void Common_problem_codes_have_russian_explanations(int code)
    {
        var info = DeviceProblemCodes.Describe(code);
        Assert.Equal(code, info.Code);
        Assert.NotEqual(ProblemRemedy.Unknown, info.Remedy);
        Assert.Matches("[А-Яа-яЁё]", info.Title);
        Assert.False(string.IsNullOrWhiteSpace(info.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(info.Action));
        Assert.Equal($"КОД {code}", info.CodeText);
    }

    [Fact]
    public void Code_28_means_driver_not_installed_and_points_to_windows_update()
    {
        var info = DeviceProblemCodes.Describe(28);
        Assert.Equal("Драйвер не установлен", info.Title);
        Assert.Equal(ProblemRemedy.InstallDriver, info.Remedy);
        Assert.Contains("Windows Update", info.Action);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(52)]
    public void Blocked_and_unsigned_drivers_are_never_bypassed(int code)
    {
        var info = DeviceProblemCodes.Describe(code);
        Assert.Equal(ProblemRemedy.DoNotBypass, info.Remedy);
        Assert.Contains("Не ", info.Action);
    }

    [Fact]
    public void Unknown_problem_code_gets_a_generic_explanation()
    {
        var info = DeviceProblemCodes.Describe(999);
        Assert.Equal(ProblemRemedy.Unknown, info.Remedy);
        Assert.Contains("999", info.Title);
    }

    [Theory]
    [InlineData(28, true, true)]
    [InlineData(28, null, true)]
    [InlineData(28, false, false)] // not present → not an error
    [InlineData(45, true, false)]  // "not connected" → not an error
    [InlineData(0, true, false)]
    [InlineData(43, true, true)]
    public void Only_present_devices_with_real_errors_are_reported(int code, bool? present, bool expected) =>
        Assert.Equal(expected, DeviceProblemCodes.IsReportable(code, present));

    // ───────────── Naming ─────────────

    [Theory]
    [InlineData("SYSTEM", "Системные устройства")]
    [InlineData("Display", "Видеоадаптеры")]
    [InlineData("OtherHardware", "Прочее оборудование")]
    [InlineData("HIDCLASS", "Устройства HID")]
    [InlineData("SomethingNew", "SomethingNew")]
    [InlineData("", "Без класса")]
    [InlineData(null, "Без класса")]
    public void Device_classes_are_named_in_russian(string? cls, string expected) =>
        Assert.Equal(expected, DeviceNaming.ClassName(cls));

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2C05&SUBSYS_417F1458&REV_A1", "NVIDIA")]
    [InlineData(@"PCI\VEN_1022&DEV_790B", "AMD")]
    [InlineData(@"ACPI\VEN_AMDI&DEV_0052", "AMD")]
    [InlineData(@"PCI\VEN_8086&DEV_A780", "Intel")]
    [InlineData(@"PCI\VEN_10EC&DEV_8125", "Realtek")]
    [InlineData(@"USB\VID_0BDA&PID_8771", "Realtek")]
    [InlineData(@"USB\VID_046D&PID_C339", "VID_046D")]
    [InlineData(@"PCI\VEN_1AF4&DEV_1000", "VEN_1AF4")]
    public void Hardware_vendor_comes_from_the_ven_vid_table(string hwid, string expected) =>
        Assert.Equal(expected, DeviceNaming.VendorName(hwid));

    [Fact]
    public void Ids_without_vendor_part_have_no_vendor()
    {
        Assert.Null(DeviceNaming.VendorName(@"ROOT\AMDLOG"));
        Assert.Null(DeviceNaming.VendorName(null));
        Assert.Equal(@"PCI\VEN_10EC&DEV_8125&REV_05", DeviceNaming.PrimaryHardwareId(["", @"PCI\VEN_10EC&DEV_8125&REV_05", @"PCI\VEN_10EC"]));
        Assert.Null(DeviceNaming.PrimaryHardwareId(null));
    }

    // ───────────── Driver age ─────────────

    [Fact]
    public void Driver_age_in_whole_months()
    {
        var now = new DateTime(2026, 10, 26);
        Assert.Equal(3, DriverAge.Months(new DateTime(2026, 7, 22), now));
        Assert.Equal(2, DriverAge.Months(new DateTime(2026, 7, 27), now)); // day not reached yet
        Assert.Equal(0, DriverAge.Months(new DateTime(2026, 10, 25), now));
        Assert.Equal(0, DriverAge.Months(new DateTime(2027, 1, 1), now));  // future date
        Assert.Equal(28, DriverAge.Months(new DateTime(2024, 6, 25), now));
        Assert.Null(DriverAge.Months(null, now));
        Assert.Null(DriverAge.Months(new DateTime(1980, 1, 1), now));
    }

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(0, "Fresh")]
    [InlineData(5, "Fresh")]
    [InlineData(6, "Aging")]
    [InlineData(11, "Aging")]
    [InlineData(12, "Old")]
    [InlineData(40, "Old")]
    public void Driver_age_verdict_thresholds(int? months, string expected) =>
        Assert.Equal(Enum.Parse<DriverAgeVerdict>(expected), DriverAge.Verdict(months));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0, "меньше месяца")]
    [InlineData(3, "3 мес.")]
    [InlineData(12, "1 год")]
    [InlineData(16, "1 год 4 мес.")]
    [InlineData(24, "2 года")]
    [InlineData(28, "2 года 4 мес.")]
    [InlineData(60, "5 лет")]
    [InlineData(132, "11 лет")]
    [InlineData(252, "21 год")]
    public void Driver_age_is_formatted_in_russian(int? months, string expected) =>
        Assert.Equal(expected, DriverAge.Format(months));

    // ───────────── GPU versions ─────────────

    [Theory]
    [InlineData("32.0.16.1088", "610.88")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("32.0.15.6094", "560.94")]
    public void Nvidia_version_is_derived_from_windows_version(string windows, string expected) =>
        Assert.Equal(expected, GpuDriverVersions.NvidiaFromWindows(windows));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("32.0.16")]
    [InlineData("32.0.x.1088")]
    public void Invalid_nvidia_versions_are_rejected(string? windows) =>
        Assert.Null(GpuDriverVersions.NvidiaFromWindows(windows));

    [Fact]
    public void Nvidia_releases_compare_numerically()
    {
        Assert.True(GpuDriverVersions.CompareNvidia("621.01", "610.88") > 0);
        Assert.Equal(0, GpuDriverVersions.CompareNvidia("610.88", "610.88"));
        Assert.True(GpuDriverVersions.CompareNvidia("599.99", "610.88") < 0);
        Assert.Null(GpuDriverVersions.CompareNvidia("НЕТ", "610.88"));
    }

    [Fact]
    public void Gpu_display_version_uses_vendor_numbers()
    {
        Assert.Equal("610.88", GpuDriverVersions.DisplayVersion(GpuVendor.Nvidia, "32.0.16.1088", null, null));
        Assert.Equal("610.88", GpuDriverVersions.DisplayVersion(GpuVendor.Nvidia, "32.0.16.1088", null, "610.88"));
        Assert.Equal("24.10.24", GpuDriverVersions.DisplayVersion(GpuVendor.Amd, "32.0.11024.2", "24.10.24", null));
        Assert.Equal("32.0.11024.2", GpuDriverVersions.DisplayVersion(GpuVendor.Amd, "32.0.11024.2", null, null));
        Assert.Equal("32.0.101.6083", GpuDriverVersions.DisplayVersion(GpuVendor.Intel, "32.0.101.6083", null, null));
    }

    // ───────────── Windows Update mapping ─────────────

    [Fact]
    public void Driver_search_criteria_follow_the_research() =>
        Assert.Equal("IsInstalled=0 and Type='Driver' and IsHidden=0", WuText.DriverCriteria);

    [Theory]
    [InlineData("NVIDIA - Display - 32.0.15.6094", "32.0.15.6094")]
    [InlineData("Advanced Micro Devices, Inc - System - 2.2.0.133", "2.2.0.133")]
    [InlineData("Logitech USB Driver Update (1.1.84.7627)", "1.1.84.7627")]
    [InlineData("Intel - net - 23.60.0.10 ", "23.60.0.10")]
    [InlineData("Realtek Audio Driver", null)]
    [InlineData("", null)]
    public void Driver_version_is_parsed_from_the_title(string title, string? expected) =>
        Assert.Equal(expected, WuText.VersionFromTitle(title));

    [Theory]
    [InlineData(2, true, "УСТАНОВЛЕНО")]
    [InlineData(3, true, "УСТАНОВЛЕНО С ОШИБКАМИ")]
    [InlineData(4, false, "ОШИБКА")]
    [InlineData(5, false, "ПРЕРВАНО")]
    [InlineData(0, false, "НЕ НАЧАТО")]
    public void Operation_result_codes_are_mapped(int code, bool success, string text)
    {
        Assert.Equal(success, WuText.IsSuccess(code));
        Assert.Equal(text, WuText.ResultText(code));
    }

    [Theory]
    [InlineData(0, "без перезагрузки")]
    [InlineData(1, "нужна перезагрузка")]
    [InlineData(2, "может потребоваться перезагрузка")]
    public void Reboot_behavior_is_described(int behavior, string expected) =>
        Assert.Equal(expected, WuText.RebootText(behavior));

    [Fact]
    public void Windows_update_errors_have_actionable_messages()
    {
        Assert.Contains("wuauserv", WuText.DescribeHResult(unchecked((int)0x80070422)));
        Assert.Contains("интернет", WuText.DescribeHResult(unchecked((int)0x8024402C)));
        Assert.Contains("интернет", WuText.DescribeHResult(unchecked((int)0x80072EE7)));
        Assert.Contains("администратора", WuText.DescribeHResult(unchecked((int)0x80070005)));
        Assert.Contains("перезагрузки", WuText.DescribeHResult(unchecked((int)0x80240016)));
        Assert.Contains("0x80241234", WuText.DescribeHResult(unchecked((int)0x80241234)));
        Assert.True(WuText.IsNetworkError(unchecked((int)0x80072EE7)));
        Assert.False(WuText.IsNetworkError(unchecked((int)0x80070422)));
    }

    [Fact]
    public void Exceptions_from_windows_update_become_russian_messages()
    {
        Assert.Contains("wuauserv", WuText.DescribeException(new COMException("x", unchecked((int)0x80070422))));
        Assert.Equal(unchecked((int)0x80070422), WuText.HResultOf(new COMException("x", unchecked((int)0x80070422))));
        Assert.Contains("отменена", WuText.DescribeException(new OperationCanceledException()));
        Assert.Contains("не ответил", WuText.DescribeException(new TimeoutException("The operation has timed out.")));
        Assert.Equal("Своё сообщение", WuText.DescribeException(new WuException(0, "Своё сообщение")));
        Assert.Contains("интернет", WuText.DescribeException(new WuException(unchecked((int)0x8024402C), "x")));
        Assert.Equal("Сначала перезагрузите", WuText.DescribeException(new InvalidOperationException("Сначала перезагрузите")));
        Assert.Contains("администратора", WuText.DescribeException(new UnauthorizedAccessException("denied")));
    }

    [Fact]
    public void Firmware_is_recognised_and_sizes_are_read_from_decimal()
    {
        Assert.True(WuText.IsFirmware("Firmware"));
        Assert.True(WuText.IsFirmware(" firmware "));
        Assert.False(WuText.IsFirmware("Display"));
        Assert.False(WuText.IsFirmware(null));
        Assert.Equal(41567d, WuText.SizeBytes(41567m));
        Assert.Equal(0d, WuText.SizeBytes(null));
        Assert.Equal(0d, WuText.SizeBytes("не число"));
    }

    [Fact]
    public void Recommended_drivers_are_preselected_optional_ones_are_not()
    {
        static WuDriverUpdate U(bool optional, int problem = 0, string cls = "System") =>
            new("id", "T - System - 1.0", "1.0", cls, "M", "P", "Model", @"acpi\x", null, problem, optional, 1000, true, null, 2, null, null);

        Assert.True(new WuUpdateItem(U(optional: false)).IsSelected);
        Assert.False(new WuUpdateItem(U(optional: true)).IsSelected);

        // Research drivers.md §1: optional drivers stay unchecked even for a problem device — the user ticks them.
        var forProblem = new WuUpdateItem(U(optional: true, problem: 28));
        Assert.False(forProblem.IsSelected);
        Assert.True(forProblem.FixesProblem);
        Assert.Contains("отметьте его сами", forProblem.KindHint);
        forProblem.IsSelected = true;
        Assert.True(forProblem.IsSelected);

        var firmware = new WuUpdateItem(U(optional: false, cls: "Firmware"));
        Assert.False(firmware.IsSelected);
        Assert.False(firmware.CanSelect);
        firmware.IsSelected = true;
        Assert.False(firmware.IsSelected);
        Assert.Equal("ПРОШИВКА", firmware.KindText);
    }

    // ───────────── Backups ─────────────

    [Fact]
    public void Backup_folder_names_use_the_timestamp_and_avoid_collisions()
    {
        var now = new DateTime(2026, 10, 26, 21, 30, 15);
        Assert.Equal("20261026-2130", BackupFolders.NewName(now, BackupKind.Full, _ => false));
        Assert.Equal("20261026-2130-wu", BackupFolders.NewName(now, BackupKind.BeforeUpdate, _ => false));
        var taken = new HashSet<string> { "20261026-2130", "20261026-2130-2" };
        Assert.Equal("20261026-2130-3", BackupFolders.NewName(now, BackupKind.Full, taken.Contains));
    }

    [Theory]
    [InlineData("20261026-2130", true, "Full")]
    [InlineData("20261026-2130-2", true, "Full")]
    [InlineData("20261026-2130-wu", true, "BeforeUpdate")]
    [InlineData("20261026-2130-wu-2", true, "BeforeUpdate")]
    [InlineData("20261026-2130-xyz", false, "Full")]
    [InlineData("20261340-2130", false, "Full")]
    [InlineData("Drivers", false, "Full")]
    [InlineData("", false, "Full")]
    public void Backup_folder_names_are_parsed(string name, bool ok, string kind)
    {
        Assert.Equal(ok, BackupFolders.TryParse(name, out var created, out var parsedKind));
        if (!ok) return;
        Assert.Equal(new DateTime(2026, 10, 26, 21, 30, 0), created);
        Assert.Equal(Enum.Parse<BackupKind>(kind), parsedKind);
    }

    [Fact]
    public void Path_guard_allows_only_direct_children_of_the_backup_root()
    {
        const string root = @"C:\ProgramData\MONOLITH\DriverBackups";
        Assert.True(BackupFolders.IsDirectChild(root, root + @"\20261026-2130"));
        Assert.True(BackupFolders.IsDirectChild(root + @"\", root + @"\20261026-2130\"));
        Assert.False(BackupFolders.IsDirectChild(root, root));
        Assert.False(BackupFolders.IsDirectChild(root, root + @"\20261026-2130\nv_dispig.inf_amd64"));
        Assert.False(BackupFolders.IsDirectChild(root, root + @"\..\Backups"));
        Assert.False(BackupFolders.IsDirectChild(root, @"C:\Windows\System32"));
        Assert.False(BackupFolders.IsDirectChild(root, @"D:\ProgramData\MONOLITH\DriverBackups\x"));
        Assert.False(BackupFolders.IsDirectChild(root, ""));
    }

    [Fact]
    public void Inf_version_section_is_parsed_with_string_tokens()
    {
        const string inf = """
            ; AMD sample
            [Version]
            Signature="$WINDOWS NT$"
            Class=System
            ClassGuid={4d36e97d-e325-11ce-bfc1-08002be10318}
            Provider=%ManufacturerName%   ; who ships it
            DriverVer = 04/15/2024,1.0.0.9

            [Strings]
            ManufacturerName="Advanced Micro Devices, Inc."
            DiskName = "Disk; with semicolon"
            """;
        var v = InfParser.Parse(inf);
        Assert.Equal("Advanced Micro Devices, Inc.", v.Provider);
        Assert.Equal("System", v.Class);
        Assert.Equal(new DateTime(2024, 4, 15), v.Date);
        Assert.Equal("1.0.0.9", v.Version);
    }

    [Fact]
    public void Inf_parser_handles_localized_strings_and_missing_sections()
    {
        const string inf = "[Version]\r\nProvider    = %NVIDIA%\r\nClass       = Display\r\nDriverVer   = 07/22/2026, 32.0.16.1088\r\n[Strings.0409]\r\nNVIDIA = \"NVIDIA\"\r\n";
        var v = InfParser.Parse(inf);
        Assert.Equal("NVIDIA", v.Provider);
        Assert.Equal("Display", v.Class);
        Assert.Equal(new DateTime(2026, 7, 22), v.Date);
        Assert.Equal("32.0.16.1088", v.Version);

        var empty = InfParser.Parse("[Manufacturer]\nx=y");
        Assert.Null(empty.Provider);
        Assert.Null(empty.Date);
        Assert.Null(empty.Version);
    }

    [Fact]
    public void Pnputil_exit_codes_are_explained()
    {
        Assert.True(DriverBackupService.DescribeExit(0, export: true).Success);
        var reboot = DriverBackupService.DescribeExit(3010, export: false);
        Assert.True(reboot.Success);
        Assert.True(reboot.RebootRequired);
        Assert.True(DriverBackupService.DescribeExit(259, export: false).Success);
        Assert.False(DriverBackupService.DescribeExit(259, export: true).Success);
        Assert.Contains("администратора", DriverBackupService.DescribeExit(5, export: true).Message);
        Assert.Contains("87", DriverBackupService.DescribeExit(87, export: false).Message);
    }

    [Fact]
    public void Backup_scan_reads_package_inf_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "monolith-drv-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pkg = Directory.CreateDirectory(Path.Combine(dir, "amd3dvcache.inf_amd64_abc"));
            File.WriteAllText(Path.Combine(pkg.FullName, "amd3dvcache.inf"),
                "[Version]\nClass=System\nProvider=%M%\nDriverVer=04/15/2024,1.0.0.9\n[Strings]\nM=\"Advanced Micro Devices, Inc.\"\n",
                System.Text.Encoding.Unicode);
            var packages = DriverBackupService.ScanPackages(dir);
            var p = Assert.Single(packages);
            Assert.Equal("amd3dvcache.inf", p.InfFile);
            Assert.Equal("amd3dvcache.inf_amd64_abc", p.Folder);
            Assert.Equal("Advanced Micro Devices, Inc.", p.Provider);
            Assert.Equal("1.0.0.9", p.Version);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ───────────── Backup integrity (restore only what MONOLITH exported) ─────────────

    /// <summary>A fake pnputil export: two packages, one with a nested INF, plus a format-2 manifest.</summary>
    private static string MakeBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "monolith-drv-backup-" + Guid.NewGuid().ToString("N"));
        var nv = Directory.CreateDirectory(Path.Combine(dir, "nv_dispig.inf_amd64_1"));
        File.WriteAllText(Path.Combine(nv.FullName, "nv_dispig.inf"), "[Version]\nClass=Display\nProvider=NVIDIA\nDriverVer=10/01/2026,32.0.16.2101\n");
        File.WriteAllBytes(Path.Combine(nv.FullName, "nvlddmkm.sys"), [1, 2, 3, 4, 5]);
        var sub = Directory.CreateDirectory(Path.Combine(nv.FullName, "Display.NvContainer"));
        File.WriteAllText(Path.Combine(sub.FullName, "NvContainer.inf"), "[Version]\nClass=SoftwareComponent\nProvider=NVIDIA\n");
        var amd = Directory.CreateDirectory(Path.Combine(dir, "amdppm.inf_amd64_2"));
        File.WriteAllText(Path.Combine(amd.FullName, "amdppm.inf"), "[Version]\nClass=Processor\nProvider=AMD\nDriverVer=04/15/2024,1.0.0.9\n");
        File.WriteAllBytes(Path.Combine(amd.FullName, "amdppm.cat"), [9, 9, 9]);
        DriverBackupService.SaveManifest(dir, DriverBackupService.CreateManifest(dir, BackupKind.Full, null, "PC", "Windows 11"));
        return dir;
    }

    [Fact]
    public void Backup_manifest_lists_every_file_with_sha256_and_every_inf_as_a_package()
    {
        var dir = MakeBackup();
        try
        {
            var manifest = DriverBackupService.ReadManifest(dir)!;
            Assert.Equal(BackupManifest.CurrentFormat, manifest.Format);
            Assert.Equal(5, manifest.Files.Count);
            Assert.DoesNotContain(manifest.Files, f => f.Path.Contains(BackupFolders.ManifestFile));
            var sys = manifest.Files.Single(f => f.Path.EndsWith("nvlddmkm.sys"));
            Assert.Equal(5, sys.Size);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3, 4, 5 })), sys.Sha256);
            Assert.Equal(3, manifest.Packages.Count);
            Assert.Contains(manifest.Packages, p => p.InfFile == "NvContainer.inf" && p.Folder == @"nv_dispig.inf_amd64_1\Display.NvContainer");
            Assert.Null(DriverBackupService.RestoreBlockedReason(manifest));

            var ok = DriverBackupService.Verify(dir);
            Assert.True(ok.Ok, ok.Problem);
            Assert.Equal(5, ok.FileCount);
            Assert.Equal(3, ok.Packages.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Backup_with_a_changed_file_is_refused()
    {
        var dir = MakeBackup();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "nv_dispig.inf_amd64_1", "nvlddmkm.sys"), [1, 2, 3, 4, 6]);
            var r = DriverBackupService.Verify(dir);
            Assert.False(r.Ok);
            Assert.Contains("изменён", r.Problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Backup_with_an_added_driver_is_refused()
    {
        var dir = MakeBackup();
        try
        {
            // What an attacker would do: drop an extra signed package into the folder.
            var evil = Directory.CreateDirectory(Path.Combine(dir, "evil.inf_amd64_3"));
            File.WriteAllText(Path.Combine(evil.FullName, "evil.inf"), "[Version]\nClass=System\n");
            var r = DriverBackupService.Verify(dir);
            Assert.False(r.Ok);
            Assert.Contains("лишний файл", r.Problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Backup_with_a_missing_file_is_refused()
    {
        var dir = MakeBackup();
        try
        {
            File.Delete(Path.Combine(dir, "amdppm.inf_amd64_2", "amdppm.cat"));
            var r = DriverBackupService.Verify(dir);
            Assert.False(r.Ok);
            Assert.Contains("нет файла", r.Problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Backup_whose_manifest_hides_an_inf_is_refused()
    {
        var dir = MakeBackup();
        try
        {
            // Files list matches, but the package list "forgets" one INF that pnputil /subdirs would still install.
            var manifest = DriverBackupService.ReadManifest(dir)!;
            manifest.Packages.RemoveAll(p => p.InfFile == "NvContainer.inf");
            DriverBackupService.SaveManifest(dir, manifest);
            var r = DriverBackupService.Verify(dir);
            Assert.False(r.Ok);
            Assert.Contains("не указан в описи", r.Problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Backups_without_an_integrity_list_cannot_be_restored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "monolith-drv-old-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pkg = Directory.CreateDirectory(Path.Combine(dir, "a.inf_amd64_1"));
            File.WriteAllText(Path.Combine(pkg.FullName, "a.inf"), "[Version]\nClass=System\n");
            Assert.False(DriverBackupService.Verify(dir).Ok);                      // no manifest at all
            Assert.NotNull(DriverBackupService.RestoreBlockedReason(null));

            // An old (format 1) manifest: packages only, no hashes.
            File.WriteAllText(Path.Combine(dir, BackupFolders.ManifestFile),
                """{ "Created": "2026-10-01T10:00:00", "Kind": "full", "Packages": [ { "Folder": "a.inf_amd64_1", "InfFile": "a.inf" } ] }""");
            var old = DriverBackupService.ReadManifest(dir)!;
            Assert.Equal(1, old.Format);
            Assert.Contains("проверки целостности", DriverBackupService.RestoreBlockedReason(old));
            var r = DriverBackupService.Verify(dir);
            Assert.False(r.Ok);
            Assert.Contains("проверки целостности", r.Problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Restore_confirmation_describes_each_package()
    {
        var line = BackupItem.DescribePackage(new BackupPackage(@"nv_dispig.inf_amd64_1", "nv_dispig.inf", "NVIDIA", "Display", "32.0.16.2101", new DateTime(2026, 10, 1)));
        Assert.Contains("NVIDIA", line);
        Assert.Contains("32.0.16.2101", line);
        Assert.Contains("01.10.2026", line);
        Assert.EndsWith(@"(nv_dispig.inf_amd64_1\nv_dispig.inf)", line);
        Assert.Equal("(x.inf)", BackupItem.DescribePackage(new BackupPackage("", "x.inf", null, null, null, null)).Trim());
    }

    // ───────────── Enable device ─────────────

    [Theory]
    [InlineData("Включение устройства «X»: код выхода 3010: Устройство будет включено после перезагрузки.", true)]
    [InlineData("Включение устройства «X»: код выхода 30100: ошибка", false)]
    [InlineData("Включение устройства «X»: код выхода 1: ошибка", false)]
    public void Pnputil_reboot_required_exit_is_recognised(string error, bool expected) =>
        Assert.Equal(expected, DeviceEnable.IsRebootRequiredError(error));

    [Theory]
    [InlineData("26.10.1", "25.9.1", 1)]
    [InlineData("26.10.1", "26.10.1", 0)]
    [InlineData("25.12.1", "26.1.1", -1)]
    [InlineData("26.10.1", "32.0.21001.9", null)]
    [InlineData("26.10.1", null, null)]
    public void Amd_software_versions_compare(string a, string? b, int? expected)
    {
        var cmp = GpuDriverVersions.CompareAmd(a, b);
        Assert.Equal(expected, cmp is null ? null : Math.Sign(cmp.Value));
    }

    // ───────────── NVIDIA lookup parsing ─────────────

    private const string LookupXml = """
        <?xml version="1.0" encoding="UTF-8"?><LookupValueSearch><LookupValues>
        <LookupValue ParentID="129"><Name>GeForce RTX 4060 Laptop GPU</Name><Value>1007</Value></LookupValue>
        <LookupValue ParentID="131"><Name>NVIDIA GeForce RTX 5070 Ti</Name><Value>1068</Value></LookupValue>
        <LookupValue ParentID="131"><Name>NVIDIA GeForce RTX 5070</Name><Value>1070</Value></LookupValue>
        <LookupValue ParentID="133"><Name>NVIDIA GeForce RTX 5070 Ti Laptop GPU</Name><Value>1075</Value></LookupValue>
        </LookupValues></LookupValueSearch>
        """;

    [Fact]
    public void Nvidia_products_are_matched_by_normalized_name()
    {
        Assert.Equal("GEFORCE RTX 5070 TI", NvidiaLookup.NormalizeName("NVIDIA  GeForce RTX 5070 Ti"));
        var p = NvidiaLookup.FindProduct(LookupXml, "NVIDIA GeForce RTX 5070 Ti");
        Assert.NotNull(p);
        Assert.Equal(131, p!.Psid);
        Assert.Equal(1068, p.Pfid);
        Assert.Equal(1007, NvidiaLookup.FindProduct(LookupXml, "NVIDIA GeForce RTX 4060 Laptop GPU")!.Pfid);
        Assert.Equal(1075, NvidiaLookup.FindProduct(LookupXml, "NVIDIA GeForce RTX 5070 Ti Laptop GPU")!.Pfid);
        Assert.Null(NvidiaLookup.FindProduct(LookupXml, "NVIDIA GeForce RTX 9999"));
    }

    [Fact]
    public void Nvidia_driver_answer_is_parsed_defensively()
    {
        const string json = """
            { "Success" : "1", "IDS" : [{ "downloadInfo": { "Success" : "1", "Name" : "GeForce%20Game%20Ready%20Driver",
              "Version" : "621.01", "IsBeta" : "0", "IsWHQL" : "1", "ReleaseDateTime" : "Wed Oct 21, 2026",
              "DetailsURL" : "https://www.nvidia.com/en-us/drivers/details/282412/", "DownloadURLFileSize" : "1.04 GB" } }] }
            """;
        var r = NvidiaLookup.ParseRelease(json);
        Assert.NotNull(r);
        Assert.Equal("621.01", r!.Version);
        Assert.Equal(new DateTime(2026, 10, 21), r.Released);
        Assert.True(r.IsWhql);
        Assert.False(r.IsBeta);
        Assert.Equal("GeForce Game Ready Driver", r.Name);
        Assert.Equal("https://www.nvidia.com/en-us/drivers/details/282412/", r.DetailsUrl);

        Assert.Null(NvidiaLookup.ParseRelease("""{ "Success" : "0" }"""));
        Assert.Null(NvidiaLookup.ParseRelease("""{ "Success" : "1", "IDS" : [] }"""));
        Assert.Null(NvidiaLookup.ParseRelease("""{ "Success" : "1", "IDS" : [{ "downloadInfo": { "Version" : "<script>" } }] }"""));
        var foreign = NvidiaLookup.ParseRelease(json.Replace("https://www.nvidia.com/", "https://nvidia.com.example.org/"));
        Assert.Null(foreign!.DetailsUrl);
    }

    // ───────────── Sources ─────────────

    [Theory]
    [InlineData("https://www.nvidia.com/en-us/drivers/", true)]
    [InlineData("https://www.amd.com/en/support/download/drivers.html", true)]
    [InlineData("https://www.intel.com/content/www/us/en/support/detect.html", true)]
    [InlineData("https://www.wagnardsoft.com/", true)]
    [InlineData("ms-settings:windowsupdate-optionalupdates", true)]
    [InlineData("http://www.nvidia.com/", false)]
    [InlineData("https://nvidia.com.evil.example/", false)]
    [InlineData("https://drivers-booster.example/nvidia.com", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_official_links_can_be_opened(string? url, bool expected) =>
        Assert.Equal(expected, DriverSources.IsAllowedUrl(url));

    [Fact]
    public void Vendor_tools_use_verified_ids_and_driver_packs_are_blocked()
    {
        Assert.Equal("XP8CLZL93F5Z4P", DriverSources.ToolFor(GpuVendor.Nvidia)!.WingetId);
        Assert.Equal("msstore", DriverSources.ToolFor(GpuVendor.Nvidia)!.WingetSource);
        Assert.Equal("Intel.IntelDriverAndSupportAssistant", DriverSources.ToolFor(GpuVendor.Intel)!.WingetId);
        Assert.Null(DriverSources.ToolFor(GpuVendor.Amd)!.WingetId); // no Adrenalin package in winget
        Assert.Null(DriverSources.ToolFor(GpuVendor.Microsoft));
        Assert.True(DriverSources.IsBlocked("GlennDelahoy.SnappyDriverInstallerOrigin"));
        Assert.True(DriverSources.IsBlocked("amd.amdsoftwarecloudedition"));
        Assert.False(DriverSources.IsBlocked(DriverSources.IntelDsaWingetId));
        Assert.All(new[] { DriverSources.NvidiaDriversPage, DriverSources.AmdDriversPage, DriverSources.IntelDetectPage, DriverSources.DduPage },
            u => Assert.True(DriverSources.IsAllowedUrl(u)));
    }

    // ───────────── Chipset ─────────────

    private static SignedDriverInfo Drv(string name, string hwid, string inf, string provider = "Advanced Micro Devices, Inc.", string cls = "SYSTEM", string? deviceId = null) =>
        new(name, deviceId ?? hwid + @"\0", cls, "1.0.0.0", new DateTime(2024, 4, 15), provider, provider, inf, true, "Microsoft Windows Hardware Compatibility Publisher", hwid);

    [Theory]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", true, true)]
    [InlineData("AMD Ryzen 9 7900X3D 12-Core Processor", true, true)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", true, false)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", true, false)]
    [InlineData("AMD Ryzen 9 9950X 16-Core Processor", false, false)]
    public void X3D_parts_are_classified(string cpu, bool x3d, bool dualCcd)
    {
        Assert.Equal(x3d, ChipsetAdvisor.IsX3D(cpu));
        Assert.Equal(dualCcd, ChipsetAdvisor.IsDualCcdX3D(cpu));
    }

    [Fact]
    public void Amd_chipset_is_ok_when_ppm_and_vcache_use_vendor_infs()
    {
        var drivers = new List<SignedDriverInfo>
        {
            Drv("AMD PPM Provisioning File", @"ACPI\VEN_AMDI&DEV_0052", "oem5.inf"),
            Drv("AMD 3D V-Cache Performance Optimizer", @"ACPI\VEN_AMDI&DEV_0101", "oem4.inf"),
            Drv("AMD I2C Controller", @"ACPI\VEN_AMDI&DEV_0010", "amdi2c.inf"),
            Drv("AMD Processor", @"ACPI\VEN_ACPI&DEV_0007", "cpu.inf", "Microsoft", "PROCESSOR"),
        };
        var r = ChipsetAdvisor.Analyze("AMD Ryzen 7 9800X3D 8-Core Processor", true, false, drivers, [], null);
        Assert.Equal(ChipsetPlatform.Amd, r.Platform);
        Assert.True(r.DriverOk);
        Assert.Contains(r.Components, c => c.Title.StartsWith("PPM") && c.State == ComponentState.Installed);
        Assert.Contains(r.Components, c => c.Title == "I2C" && c.State == ComponentState.Inbox);
        Assert.NotNull(r.Note); // X3D note
        Assert.Contains("не найден", r.PackageText);
    }

    [Fact]
    public void Amd_chipset_is_missing_when_ppm_runs_on_an_inbox_driver_or_has_code_28()
    {
        var inbox = new List<SignedDriverInfo> { Drv("AMD PPM Provisioning File", @"ACPI\VEN_AMDI&DEV_0052", "amdppm.inf") };
        var r1 = ChipsetAdvisor.Analyze("AMD Ryzen 5 7600 6-Core Processor", true, false, inbox, [], "7.06.02.123");
        Assert.False(r1.DriverOk);
        Assert.Equal("УСТАНОВИТЕ ДРАЙВЕР ЧИПСЕТА AMD", r1.Verdict);
        Assert.Contains("7.06.02.123", r1.PackageText);

        var problem = new ProblemDeviceInfo("Неизвестное устройство", @"ACPI\AMDI0052\2&DABA3FF&0", "", [@"ACPI\VEN_AMDI&DEV_0052", @"ACPI\AMDI0052"], 28, true, "");
        var r2 = ChipsetAdvisor.Analyze("AMD Ryzen 5 7600 6-Core Processor", true, false, [], [problem], null);
        Assert.False(r2.DriverOk);
        Assert.Contains(r2.Components, c => c.State == ComponentState.Problem && c.ProblemCode == 28);
    }

    [Fact]
    public void X3D_without_vcache_driver_is_reported_and_unknown_state_is_honest()
    {
        var drivers = new List<SignedDriverInfo> { Drv("AMD PPM Provisioning File", @"ACPI\VEN_AMDI&DEV_0052", "oem5.inf") };
        var r = ChipsetAdvisor.Analyze("AMD Ryzen 9 9950X3D 16-Core Processor", true, false, drivers, [], null);
        Assert.False(r.DriverOk);
        Assert.Contains("V-Cache", r.Recommendation);
        Assert.Contains("Game Bar", r.Note);

        var unknown = ChipsetAdvisor.Analyze("AMD Ryzen 5 5600 6-Core Processor", true, false, [], [], null);
        Assert.Null(unknown.DriverOk);
    }

    [Fact]
    public void Intel_and_other_platforms_get_their_own_advice()
    {
        var drivers = new List<SignedDriverInfo>
        {
            Drv("Intel(R) SMBus - 7A23", @"PCI\VEN_8086&DEV_7A23&SUBSYS_00000000", "oem30.inf", "INTEL", "SYSTEM"),
        };
        var intel = ChipsetAdvisor.Analyze("Intel(R) Core(TM) i7-14700K", false, true, drivers, [], null);
        Assert.Equal(ChipsetPlatform.Intel, intel.Platform);
        Assert.True(intel.DriverOk);
        Assert.Single(intel.Components);

        var other = ChipsetAdvisor.Analyze("Qualcomm Snapdragon X Elite", false, false, [], [], null);
        Assert.Equal(ChipsetPlatform.Other, other.Platform);
        Assert.Null(other.DriverOk);
    }

    // ───────────── Inventory helpers ─────────────

    [Theory]
    [InlineData("oem14.inf", true)]
    [InlineData("OEM5.INF", true)]
    [InlineData("oem.inf", false)]
    [InlineData("oemx.inf", false)]
    [InlineData("cpu.inf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Oem_inf_names_are_recognised(string? inf, bool expected) =>
        Assert.Equal(expected, SignedDriverInfo.IsOemInf(inf));

    [Fact]
    public void Microsoft_drivers_are_inbox_or_microsoft_provided()
    {
        Assert.True(Drv("AMD Processor", @"ACPI\VEN_ACPI&DEV_0007", "cpu.inf", "Microsoft").IsMicrosoft);
        Assert.True(Drv("AMD GPIO Controller", @"ACPI\VEN_AMDI&DEV_0030", "amdgpio2.inf").IsMicrosoft); // inbox INF
        Assert.True(Drv("Surface Firmware", @"ACPI\MSHW0001", "oem40.inf", "Microsoft Corporation").IsMicrosoft);
        Assert.False(Drv("NVIDIA GeForce RTX 5070 Ti", @"PCI\VEN_10DE&DEV_2C05", "oem14.inf", "NVIDIA").IsMicrosoft);
    }

    [Fact]
    public void Windows_update_drivers_are_matched_to_the_devices_they_replace()
    {
        var drivers = new List<SignedDriverInfo>
        {
            Drv("AMD GPIO Controller", @"ACPI\VEN_AMDI&DEV_0030", "amdgpio2.inf", deviceId: @"ACPI\AMDI0030\0"),
            Drv("AMD GPIO Controller 2", @"ACPI\VEN_AMDI&DEV_0031", "oem19.inf", deviceId: @"ACPI\AMDI00301\0"),
            Drv("Logitech PRO", @"USB\VID_046D&PID_C339&REV_0102", "oem33.inf", "Logitech", "HIDClass", @"USB\VID_046D&PID_C339\5&1A2B"),
            Drv("NVIDIA", @"PCI\VEN_10DE&DEV_2C05&SUBSYS_417F1458&REV_A1", "oem14.inf", "NVIDIA", "Display", @"PCI\VEN_10DE&DEV_2C05&SUBSYS_417F1458&REV_A1\4&1"),
        };
        Assert.Equal(new[] { "AMD GPIO Controller" }, DriverMatching.FindAffected(drivers, @"acpi\amdi0030").Select(d => d.DeviceName));
        Assert.Equal(new[] { "Logitech PRO" }, DriverMatching.FindAffected(drivers, @"usb\vid_046d&pid_c339").Select(d => d.DeviceName));
        Assert.Equal(new[] { "NVIDIA" }, DriverMatching.FindAffected(drivers, @"pci\ven_10de&dev_2c05&subsys_417f1458&rev_a1").Select(d => d.DeviceName));
        Assert.Empty(DriverMatching.FindAffected(drivers, null));
        Assert.Empty(DriverMatching.FindAffected(drivers, @"pci\ven_8086"));
    }
}
