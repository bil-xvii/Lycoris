using Monolith.Core;
using Monolith.Modules.Dashboard;
using Monolith.Modules.Dashboard.Checks;
using Monolith.Modules.Dashboard.Monitoring;

namespace Monolith.Tests;

public sealed class DashboardMonitorMathTests
{
    [Fact]
    public void Gpu_load_sums_processes_per_engine_and_takes_the_busiest_3d_engine()
    {
        (string, double)[] engines =
        [
            ("pid_100_luid_0x00000000_0x0000E1AE_phys_0_eng_0_engtype_3D", 30),
            ("pid_200_luid_0x00000000_0x0000E1AE_phys_0_eng_0_engtype_3D", 25),
            ("pid_100_luid_0x00000000_0x0000E1AE_phys_0_eng_1_engtype_3D", 10),
            ("pid_100_luid_0x00000000_0x0000E1AE_phys_0_eng_5_engtype_VideoDecode", 90),
            ("pid_300_luid_0x00000000_0x0000F00D_phys_0_eng_0_engtype_3D", 70),
            ("pid_301_luid_0x00000000_0x0000F00D_phys_0_eng_0_engtype_3D", 50),
            ("garbage", 50),
        ];

        var load = MonitorMath.Gpu3DLoadByAdapter(engines);

        Assert.Equal(2, load.Count);
        Assert.Equal(55, load["0x00000000_0x0000e1ae"], 3);
        Assert.Equal(100, load["0x00000000_0x0000f00d"], 3); // capped like Task Manager
    }

    [Fact]
    public void Gpu_engine_instance_names_are_matched_case_insensitively()
    {
        var load = MonitorMath.Gpu3DLoadByAdapter([("PID_1_LUID_0X00000000_0X0000ABCD_PHYS_0_ENG_0_ENGTYPE_3d", 12)]);
        Assert.Equal(12, load["0x00000000_0x0000abcd"], 3);
    }

    [Fact]
    public void Dedicated_memory_is_summed_per_adapter()
    {
        var bytes = MonitorMath.BytesByAdapter(
        [
            ("luid_0x00000000_0x0000E1AE_phys_0", 1_000),
            ("luid_0x00000000_0x0000E1AE_phys_1", 500),
            ("luid_0x00000000_0x0000F00D_phys_0", 42),
        ]);
        Assert.Equal(1_500, bytes["0x00000000_0x0000e1ae"]);
        Assert.Equal(42, bytes["0x00000000_0x0000f00d"]);
    }

    [Fact]
    public void Primary_adapter_wins_otherwise_the_maximum()
    {
        var map = new Dictionary<string, double> { ["a"] = 10, ["b"] = 80 };
        Assert.Equal(10, MonitorMath.PickAdapterValue(map, "a"));
        Assert.Equal(80, MonitorMath.PickAdapterValue(map, "missing"));
        Assert.Equal(80, MonitorMath.PickAdapterValue(map, null));
        Assert.Null(MonitorMath.PickAdapterValue(new Dictionary<string, double>(), "a"));
    }

    [Fact]
    public void Luid_key_matches_the_counter_instance_format()
    {
        Assert.Equal("0x00000000_0x0000e1ae", MonitorMath.LuidKey(0, 0xE1AE));
        Assert.Equal("0x00000001_0xffffffff", MonitorMath.LuidKey(1, uint.MaxValue));
    }

    [Theory]
    [InlineData(100.0, 0.0)]
    [InlineData(37.5, 62.5)]
    [InlineData(0.0, 100.0)]
    [InlineData(120.0, 0.0)]  // PDH can overshoot: clamp
    [InlineData(-5.0, 100.0)]
    public void Disk_busy_is_100_minus_idle(double idle, double busy) => Assert.Equal(busy, MonitorMath.DiskBusyFromIdle(idle));

    [Fact]
    public void Effective_clock_scales_the_nominal_frequency()
    {
        Assert.Equal(5080.7, MonitorMath.EffectiveClockMhz(108.1, 4700)!.Value, 1);
        Assert.Null(MonitorMath.EffectiveClockMhz(null, 4700));
        Assert.Null(MonitorMath.EffectiveClockMhz(100, 0));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", "NVIDIA GeForce RTX 5070 Ti", true)]
    [InlineData("AMD Radeon(TM) Graphics", "AMD Radeon Graphics", true)]
    [InlineData("NVIDIA  GeForce RTX 4090", "nvidia geforce rtx 4090", true)]
    [InlineData("NVIDIA GeForce RTX 4090", "AMD Radeon RX 7900 XTX", false)]
    [InlineData("", "AMD Radeon RX 7900 XTX", false)]
    public void Gpu_names_are_compared_loosely(string a, string b, bool same) => Assert.Equal(same, MonitorMath.SameGpuName(a, b));

    [Fact]
    public void History_keeps_the_last_points_only()
    {
        var list = new List<double>();
        for (var i = 0; i < 75; i++) MonitorMath.Push(list, i, 60);
        Assert.Equal(60, list.Count);
        Assert.Equal(15, list[0]);
        Assert.Equal(74, list[^1]);
    }
}

public sealed class DashboardFormatTests
{
    private const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024, Tb = Gb * 1024;

    [Theory]
    [InlineData(0.0, "0 Б/с")]
    [InlineData(850.0, "850 Б/с")]
    [InlineData(12.4 * Kb, "12,4 КБ/с")]
    [InlineData(1.2 * Mb, "1,2 МБ/с")]
    [InlineData(124 * Mb, "124 МБ/с")]
    [InlineData(1.5 * Gb, "1,5 ГБ/с")]
    public void Rates(double bps, string expected) => Assert.Equal(expected, DashFormat.Rate(bps));

    [Fact]
    public void Missing_values_show_a_dash()
    {
        Assert.Equal("—", DashFormat.Rate(null));
        Assert.Equal("—", DashFormat.Percent(null));
        Assert.Equal("—", DashFormat.Clock(null));
        Assert.Equal("—", DashFormat.Temperature(null));
        Assert.Equal("—", DashFormat.UsedOfTotalGb(null, 1));
        Assert.Equal("—", DashFormat.Size(double.NaN));
    }

    [Theory]
    [InlineData(512 * Mb, "512 МБ")]
    [InlineData(12.4 * Gb, "12,4 ГБ")]
    [InlineData(476 * Gb, "476 ГБ")]
    [InlineData(1.8 * Tb, "1,8 ТБ")]
    public void Sizes(double bytes, string expected) => Assert.Equal(expected, DashFormat.Size(bytes));

    [Fact]
    public void Used_of_total()
    {
        Assert.Equal("9,8 / 30,9 ГБ", DashFormat.UsedOfTotalGb(9.8 * Gb, 30.9 * Gb));
        Assert.Equal("9,8 / 32 ГБ", DashFormat.UsedOfTotalGb(9.8 * Gb, 32 * Gb));
        Assert.Equal("0,9 / 16 ГБ", DashFormat.UsedOfCapacityGb(0.9 * Gb, 15.62 * Gb));
        Assert.Equal("32 ГБ", DashFormat.WholeGb(31.9 * Gb));
    }

    [Fact]
    public void Percent_clock_temperature()
    {
        Assert.Equal("37", DashFormat.Percent(36.6));
        Assert.Equal("100", DashFormat.Percent(130));
        Assert.Equal("5,08 ГГц", DashFormat.Clock(5083));
        Assert.Equal("800 МГц", DashFormat.Clock(800));
        Assert.Equal("41 °C", DashFormat.Temperature(40.9));
    }

    [Fact]
    public void Durations()
    {
        Assert.Equal("3 д 04 ч 12 мин", DashFormat.Duration(new TimeSpan(3, 4, 12, 0)));
        Assert.Equal("4 ч 07 мин", DashFormat.Duration(new TimeSpan(4, 7, 0)));
        Assert.Equal("12 мин", DashFormat.Duration(TimeSpan.FromMinutes(12)));
    }

    [Theory]
    [InlineData(1, "день")]
    [InlineData(2, "дня")]
    [InlineData(4, "дня")]
    [InlineData(5, "дней")]
    [InlineData(11, "дней")]
    [InlineData(12, "дней")]
    [InlineData(21, "день")]
    [InlineData(22, "дня")]
    [InlineData(112, "дней")]
    public void Russian_plurals(int n, string expected) => Assert.Equal(expected, DashFormat.Plural(n, "день", "дня", "дней"));

    [Fact]
    public void Ages_in_words()
    {
        var now = new DateTime(2026, 10, 26);
        Assert.Equal("3 дня", DashFormat.Age(now.AddDays(-3), now));
        Assert.Equal("2 месяца", DashFormat.Age(now.AddDays(-60), now));
        Assert.Equal("1 год", DashFormat.Age(now.AddDays(-400), now));
    }
}

public sealed class DashboardHealthScoreTests
{
    private static HealthCheckResult R(CheckSeverity s, int w) => new("x", "T", "D", s, w);

    [Fact]
    public void All_ok_is_100() => Assert.Equal(100, HealthScore.Calculate([R(CheckSeverity.Ok, 15), R(CheckSeverity.Ok, 10)]));

    [Fact]
    public void Warnings_cost_their_weight_and_information_a_fifth()
    {
        Assert.Equal(79, HealthScore.Calculate([R(CheckSeverity.Warning, 15), R(CheckSeverity.Warning, 6), R(CheckSeverity.Ok, 10)]));
        Assert.Equal(77, HealthScore.Calculate([R(CheckSeverity.Warning, 15), R(CheckSeverity.Warning, 6), R(CheckSeverity.Info, 10)]));
        Assert.Equal(100, HealthScore.Calculate([R(CheckSeverity.Info, 0)]));
    }

    [Fact]
    public void Score_is_clamped() =>
        Assert.Equal(0, HealthScore.Calculate(Enumerable.Range(0, 10).Select(_ => R(CheckSeverity.Warning, 25))));

    [Theory]
    [InlineData(100, "ОТЛИЧНО")]
    [InlineData(90, "ОТЛИЧНО")]
    [InlineData(89, "ХОРОШО")]
    [InlineData(70, "ХОРОШО")]
    [InlineData(69, "ТРЕБУЕТ ВНИМАНИЯ")]
    [InlineData(0, "ТРЕБУЕТ ВНИМАНИЯ")]
    public void Verdicts(int score, string verdict) => Assert.Equal(verdict, HealthScore.Verdict(score));

    [Fact]
    public void Order_is_warnings_then_information_then_ok()
    {
        var ordered = HealthScore.Ordered([R(CheckSeverity.Ok, 10), R(CheckSeverity.Info, 5), R(CheckSeverity.Warning, 4), R(CheckSeverity.Warning, 15)]).ToList();
        Assert.Equal(new[] { CheckSeverity.Warning, CheckSeverity.Warning, CheckSeverity.Info, CheckSeverity.Ok }, ordered.Select(r => r.Severity));
        Assert.Equal(15, ordered[0].Weight);
    }
}

public sealed class DashboardRamTests
{
    [Theory]
    [InlineData("F5-6000J3636F16G", 6000)]
    [InlineData("F4-3600C16D-32GTZN", 3600)]
    [InlineData("CMK32GX5M2B6000C36", 6000)]
    [InlineData("CMW32GX4M2E3200C16", 3200)]
    [InlineData("KF560C40BBK2-32", 6000)]
    [InlineData("KF436C17BBK2/16", 3600)]
    [InlineData("BL2K16G36C16U4B", 3600)]
    [InlineData("FF3D532G6000HC38ACDC01", 6000)]
    [InlineData("AX5U6000C3016G-DCLARWH", 6000)]
    public void Part_number_speed(string part, int speed) => Assert.Equal(speed, HealthRules.PartNumberSpeed(part));

    [Theory]
    [InlineData("M378A1K43EB2-CWE")]
    [InlineData("CT16G4DFRA32A")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_part_numbers(string? part) => Assert.Null(HealthRules.PartNumberSpeed(part));

    [Fact]
    public void Expo_active_is_ok()
    {
        var v = HealthRules.AnalyzeRam([new RamModule("F5-6000J3636F16G", 6000, 6000, 34, 16UL << 30), new RamModule("F5-6000J3636F16G", 6000, 6000, 34, 16UL << 30)]);
        Assert.NotNull(v);
        Assert.False(v.BelowRated);
        Assert.Equal(6000, v.Rated);
        Assert.Equal("DDR5", v.Type);
        Assert.Equal(32UL << 30, v.Capacity);
    }

    [Fact]
    public void Expo_off_is_detected_from_the_part_number()
    {
        // With XMP/EXPO off both SMBIOS fields often read the JEDEC speed.
        var v = HealthRules.AnalyzeRam([new RamModule("F5-6000J3636F16G", 4800, 4800, 34, 16UL << 30)]);
        Assert.True(v!.BelowRated);
        Assert.Equal(6000, v.Rated);
        Assert.Equal(4800, v.Configured);
    }

    [Fact]
    public void Xmp_off_is_detected_from_smbios_speed()
    {
        var v = HealthRules.AnalyzeRam([new RamModule("", 3600, 2133, 26, 8UL << 30)]);
        Assert.True(v!.BelowRated);
    }

    [Fact]
    public void Ddr4_reported_in_mhz_is_doubled()
    {
        var v = HealthRules.AnalyzeRam([new RamModule("", 1600, 1600, 26, 8UL << 30)]);
        Assert.Equal(3200, v!.Configured);
        Assert.False(v.BelowRated);
    }

    [Fact]
    public void Mixed_kits_use_the_slowest_module()
    {
        var v = HealthRules.AnalyzeRam([new RamModule("F5-6000J3636F16G", 6000, 5600, 34, 0), new RamModule("KF556C40BBK2-32", 5600, 5600, 34, 0)]);
        Assert.Equal(5600, v!.Rated);
        Assert.False(v.BelowRated);
    }

    [Fact]
    public void Non_jedec_configured_speed_is_not_flagged()
    {
        // Running a manual 5800 on a 6400 kit is a deliberate setting, not "XMP off".
        var v = HealthRules.AnalyzeRam([new RamModule("F5-6400J3239G16G", 6400, 5800, 34, 0)]);
        Assert.False(v!.BelowRated);
    }

    [Fact]
    public void No_modules_means_no_check()
    {
        Assert.Null(HealthRules.AnalyzeRam([]));
        Assert.Null(HealthRules.CheckRam(new SystemSnapshot()));
    }

    [Fact]
    public void Laptops_get_information_instead_of_a_warning()
    {
        var modules = new[] { new RamModule("", 5600, 4800, 34, 0) };
        Assert.Equal(CheckSeverity.Warning, HealthRules.CheckRam(new SystemSnapshot { RamModules = modules })!.Severity);
        Assert.Equal(CheckSeverity.Info, HealthRules.CheckRam(new SystemSnapshot { RamModules = modules, IsLaptop = true })!.Severity);
        Assert.Null(HealthRules.CheckRam(new SystemSnapshot { RamModules = modules })!.Action); // BIOS only: never an automatic action
    }
}

public sealed class DashboardDisplayTests
{
    [Fact]
    public void Max_refresh_is_taken_at_the_current_resolution_only()
    {
        DisplayMode[] modes =
        [
            new(2560, 1440, 60), new(2560, 1440, 144), new(2560, 1440, 240),
            new(1920, 1080, 360), new(2560, 1440, 300, Interlaced: true),
        ];
        Assert.Equal(240, HealthRules.MaxRefreshAt(modes, 2560, 1440));
        Assert.Equal(360, HealthRules.MaxRefreshAt(modes, 1920, 1080));
        Assert.Equal(0, HealthRules.MaxRefreshAt(modes, 3840, 2160));
    }

    [Theory]
    [InlineData(60, 144, true)]
    [InlineData(144, 144, false)]
    [InlineData(239, 240, false)]  // fractional 239.76 Hz timing
    [InlineData(120, 240, true)]
    [InlineData(0, 144, false)]    // unknown current rate
    public void Below_max_refresh(int current, int max, bool below) => Assert.Equal(below, HealthRules.IsBelowMaxRefresh(current, max));

    [Fact]
    public void Slow_monitor_offers_the_display_settings()
    {
        var r = HealthRules.CheckRefreshRate(new SystemSnapshot { Displays = [new DisplayInfo("LG", 2560, 1440, 60, 165)] });
        Assert.Equal(CheckSeverity.Warning, r!.Severity);
        Assert.Equal(CheckActionKind.OpenUri, r.Action!.Kind);
        Assert.Equal("ms-settings:display-advanced", r.Action.Target);
        Assert.False(r.Action.RequiresAdmin);
        Assert.Contains("165", r.Detail);
    }
}

public sealed class DashboardRulesTests
{
    [Theory]
    [InlineData("NTFS DisableDeleteNotify = 0  (Allows TRIM operations to be sent to the storage device)\r\nReFS DisableDeleteNotify = 0", false)]
    [InlineData("NTFS DisableDeleteNotify = 1  (Отключено)\r\nReFS DisableDeleteNotify = 0  (Включено)", true)]
    [InlineData("DisableDeleteNotify = 1", true)]
    [InlineData("DisableDeleteNotify = 0", false)]
    public void Trim_state_is_parsed(string output, bool disabled) => Assert.Equal(disabled, HealthRules.ParseTrimDisabled(output));

    [Theory]
    [InlineData("Ошибка: Отказано в доступе.")]
    [InlineData("")]
    [InlineData(null)]
    public void Unparseable_trim_output_is_unknown(string? output) => Assert.Null(HealthRules.ParseTrimDisabled(output));

    [Fact]
    public void Trim_is_checked_on_ssd_only()
    {
        var hdd = new SystemDisk("WD Blue", "HDD", "SATA", 1, 2);
        var nvme = new SystemDisk("Samsung 990 PRO", "", "NVMe", 1, 2);
        Assert.Null(HealthRules.CheckTrim(new SystemSnapshot { TrimDisabled = true, SystemDisk = hdd }));
        Assert.Equal(CheckSeverity.Warning, HealthRules.CheckTrim(new SystemSnapshot { TrimDisabled = true, SystemDisk = nvme })!.Severity);
        Assert.Null(HealthRules.CheckTrim(new SystemSnapshot { TrimDisabled = true, SystemDisk = nvme })!.Action); // read-only advice
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckTrim(new SystemSnapshot { TrimDisabled = false, SystemDisk = nvme })!.Severity);
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    [InlineData(new byte[0], true)]
    [InlineData(null, true)]
    public void Startup_approved_flags(byte[]? data, bool enabled) => Assert.Equal(enabled, HealthRules.IsStartupEntryEnabled(data));

    [Fact]
    public void Startup_count_check()
    {
        var many = HealthRules.CheckStartup(new SystemSnapshot { StartupCount = 14 })!;
        Assert.Equal(CheckSeverity.Warning, many.Severity);
        Assert.Equal("startup", many.Action!.Target);
        Assert.Contains("14 программ", many.Detail);
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckStartup(new SystemSnapshot { StartupCount = 10 })!.Severity);
        Assert.Null(HealthRules.CheckStartup(new SystemSnapshot()));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(22, false)]
    [InlineData(45, false)]
    [InlineData(28, true)]
    [InlineData(43, true)]
    public void Problem_device_codes(int code, bool problem) => Assert.Equal(problem, HealthRules.IsProblemCode(code));

    [Fact]
    public void Device_errors_are_described()
    {
        Assert.Equal("драйвер не установлен", HealthRules.DeviceErrorText(28));
        Assert.Equal("ошибка 99", HealthRules.DeviceErrorText(99));
        var r = HealthRules.CheckDevices(new SystemSnapshot
        {
            ProblemDevices = [new ProblemDevice("Disabled", 22), new ProblemDevice("PCI Device", 28)],
        })!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("PCI Device (код 28", r.Detail);
        Assert.DoesNotContain("Disabled", r.Detail);
        Assert.Equal("drivers", r.Action!.Target);
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckDevices(new SystemSnapshot { ProblemDevices = [new ProblemDevice("USB", 45)] })!.Severity);
    }

    [Theory]
    [InlineData("32.0.15.8180", "581.80")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("32.0.16.1088", "610.88")]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Nvidia_versions(string windows, string? nvidia) => Assert.Equal(nvidia, HealthRules.NvidiaDriverVersion(windows));

    [Fact]
    public void Driver_age()
    {
        var now = new DateTime(2026, 10, 26);
        Assert.True(HealthRules.IsDriverOutdated(now.AddDays(-200), now));
        Assert.False(HealthRules.IsDriverOutdated(now.AddDays(-30), now));
        Assert.False(HealthRules.IsDriverOutdated(null, now));

        var old = new GpuInfo("NVIDIA GeForce RTX 3080", GpuVendor.Nvidia, "31.0.15.5222", now.AddYears(-1), 10UL << 30, "PCI\\VEN_10DE");
        var r = HealthRules.CheckGpuDriver(new SystemSnapshot { Now = now, PrimaryGpu = old })!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("552.22", r.Detail);
        Assert.Equal("drivers", r.Action!.Target);

        var basic = old with { Vendor = GpuVendor.Microsoft };
        Assert.Null(HealthRules.CheckGpuDriver(new SystemSnapshot { Now = now, PrimaryGpu = basic }));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", true)]
    [InlineData("NVIDIA GeForce RTX 4090", true)]
    [InlineData("NVIDIA GeForce RTX 3080", false)]
    [InlineData("NVIDIA RTX 4000 Ada Generation", false)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData(null, false)]
    public void Rtx_40_50(string? name, bool expected) => Assert.Equal(expected, HealthRules.IsRtx40Or50(name));

    [Theory]
    [InlineData("Micro-Star International Co., Ltd. PRO X870-P WIFI (MS-7E47)", "MSI PRO X870-P WIFI (MS-7E47)")]
    [InlineData("ASUSTeK COMPUTER INC. ROG STRIX B650E-F GAMING WIFI", "ASUS ROG STRIX B650E-F GAMING WIFI")]
    [InlineData("Gigabyte Technology Co., Ltd. B550 AORUS ELITE", "Gigabyte B550 AORUS ELITE")]
    [InlineData("ASRock X670E Taichi", "ASRock X670E Taichi")]
    [InlineData("", "")]
    public void Vendor_names_are_shortened(string input, string expected) => Assert.Equal(expected, HealthRules.ShortVendor(input));

    [Fact]
    public void Free_space_thresholds()
    {
        const ulong gb = 1UL << 30;
        Assert.True(HealthRules.IsLowSpace(10 * gb, 500 * gb));    // < 20 GB
        Assert.True(HealthRules.IsLowSpace(60 * gb, 500 * gb));    // < 15 %
        Assert.False(HealthRules.IsLowSpace(200 * gb, 500 * gb));
        Assert.True(HealthRules.IsCriticalSpace(3 * gb, 500 * gb));

        var low = HealthRules.CheckDiskSpace(new SystemSnapshot { SystemDriveFree = 60 * gb, SystemDriveTotal = 500 * gb })!; // 12 %
        Assert.Equal(CheckSeverity.Warning, low.Severity);
        Assert.Equal(15, low.Weight);
        Assert.Equal("cleaner", low.Action!.Target);
        Assert.Equal(25, HealthRules.CheckDiskSpace(new SystemSnapshot { SystemDriveFree = 3 * gb, SystemDriveTotal = 500 * gb })!.Weight);
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckDiskSpace(new SystemSnapshot { SystemDriveFree = 200 * gb, SystemDriveTotal = 500 * gb })!.Severity);
    }

    [Fact]
    public void Restore_off_points_to_the_restore_page_and_policy_is_respected()
    {
        var off = HealthRules.CheckRestore(new SystemSnapshot { RestoreEnabled = false })!;
        Assert.Equal(CheckSeverity.Warning, off.Severity);
        // System protection is managed in one place only: the Restore page.
        Assert.Equal(CheckActionKind.Navigate, off.Action!.Kind);
        Assert.Equal("restore", off.Action.Target);
        Assert.False(off.Action.RequiresAdmin);

        var policy = HealthRules.CheckRestore(new SystemSnapshot { RestoreEnabled = false, RestoreBlockedByPolicy = true })!;
        Assert.Equal(CheckSeverity.Info, policy.Severity);
        Assert.Null(policy.Action);
    }

    [Fact]
    public void Gaming_switches_apply_catalog_tweaks()
    {
        var gm = HealthRules.CheckGameMode(new SystemSnapshot { GameModeOn = false })!;
        Assert.Equal(CheckActionKind.ApplyTweak, gm.Action!.Kind);
        Assert.Equal("game-mode", gm.Action.Target);
        Assert.True(gm.Action.RequiresAdmin);

        var dvr = HealthRules.CheckGameDvr(new SystemSnapshot { BackgroundRecordingOn = true })!;
        Assert.Equal("gamedvr-off", dvr.Action!.Target);

        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckGameMode(new SystemSnapshot { GameModeOn = true })!.Severity);
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckGameDvr(new SystemSnapshot { BackgroundRecordingOn = false })!.Severity);
    }

    [Fact]
    public void Hags_severity_depends_on_the_gpu()
    {
        GpuInfo Gpu(string name) => new(name, GpuVendor.Nvidia, "32.0.15.8180", DateTime.Today, 8UL << 30, "");
        var rtx50 = HealthRules.CheckHags(new SystemSnapshot { Hags = HagsState.Off, PrimaryGpu = Gpu("NVIDIA GeForce RTX 5070 Ti") })!;
        var rtx30 = HealthRules.CheckHags(new SystemSnapshot { Hags = HagsState.Off, PrimaryGpu = Gpu("NVIDIA GeForce RTX 3070") })!;
        Assert.Equal(CheckSeverity.Warning, rtx50.Severity);
        Assert.Equal(CheckSeverity.Info, rtx30.Severity);
        Assert.Equal("hags", rtx50.Action!.Target);
        // Other GPUs get the optional item: the «hags» tweak applies only to RTX 40/50, so its button would do nothing.
        Assert.Equal(HealthRules.TweakHagsOther, rtx30.Action!.Target);
        var ada = HealthRules.CheckHags(new SystemSnapshot { Hags = HagsState.Off, PrimaryGpu = Gpu("NVIDIA RTX 4000 Ada Generation") })!;
        Assert.Equal(CheckSeverity.Info, ada.Severity);
        Assert.Equal(HealthRules.TweakHagsOther, ada.Action!.Target);
        Assert.Null(HealthRules.CheckHags(new SystemSnapshot { Hags = HagsState.Unsupported }));
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckHags(new SystemSnapshot { Hags = HagsState.On })!.Severity);
    }

    [Fact]
    public void Power_plan_is_informational_and_follows_the_x3d_rules()
    {
        var dual = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuCores = 16,
            PowerPlan = PowerPlanKind.HighPerformance, PowerPlanName = "Высокая производительность",
        })!;
        Assert.Equal(CheckSeverity.Info, dual.Severity);
        // The button only shows the matching plan item in «Оптимизация»; it changes nothing by itself.
        Assert.Equal(CheckActionKind.FocusTweak, dual.Action!.Kind);
        Assert.Equal(HealthRules.TweakBalancedPlanX3D, dual.Action.Target);
        Assert.False(dual.Action.RequiresAdmin);

        var single = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "AMD Ryzen 7 9800X3D 8-Core Processor", CpuCores = 8, PowerPlan = PowerPlanKind.HighPerformance,
        })!;
        Assert.Equal(CheckSeverity.Info, single.Severity);
        Assert.Equal(0, single.Weight); // no score penalty: AMD says the plan barely matters here
        Assert.Equal(CheckActionKind.FocusTweak, single.Action!.Kind);
        Assert.Equal(HealthRules.TweakBalancedPlan, single.Action.Target);

        var saver = HealthRules.CheckPowerPlan(new SystemSnapshot { CpuName = "Intel Core i5-12400", CpuCores = 6, PowerPlan = PowerPlanKind.PowerSaver })!;
        Assert.Equal(HealthRules.TweakBalancedPlan, saver.Action!.Target);

        var desktop = HealthRules.CheckPowerPlan(new SystemSnapshot { CpuName = "Intel Core i7-14700K", CpuCores = 20, PowerPlan = PowerPlanKind.Balanced })!;
        Assert.Equal(CheckSeverity.Ok, desktop.Severity);

        Assert.Null(desktop.Action);

        var results = new[] { dual, single, desktop };
        Assert.DoesNotContain(results, r => r.Severity == CheckSeverity.Warning);
    }

    [Fact]
    public void Power_plan_targets_exist_in_the_catalog()
    {
        // A focus request with an unknown id is silently ignored by «Оптимизация» — keep the ids in sync.
        Assert.NotNull(Monolith.Tweaks.TweakCatalog.Find(HealthRules.TweakBalancedPlan));
        Assert.NotNull(Monolith.Tweaks.TweakCatalog.Find(HealthRules.TweakBalancedPlanX3D));
    }

    [Fact]
    public void Memory_integrity_is_information_only()
    {
        var r = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.MemoryIntegrity })!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Null(r.Action); // never offered to switch off
        Assert.Equal(0, r.Weight);
        Assert.Null(HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.Off }));
    }

    [Fact]
    public void Uptime_and_pending_reboot()
    {
        var week = HealthRules.CheckUptime(new SystemSnapshot { Uptime = TimeSpan.FromDays(9.5) })!;
        Assert.Equal(CheckSeverity.Warning, week.Severity);
        Assert.Contains("9 ДНЕЙ", week.Title);
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckUptime(new SystemSnapshot { Uptime = TimeSpan.FromHours(5) })!.Severity);

        var pending = HealthRules.CheckPendingReboot(new SystemSnapshot { PendingReboot = ["Центр обновления Windows"] })!;
        Assert.Equal(CheckSeverity.Warning, pending.Severity);
        Assert.Null(pending.Action); // MONOLITH never reboots by itself
    }

    [Fact]
    public void Evaluate_produces_complete_results_for_a_typical_pc()
    {
        var snapshot = new SystemSnapshot
        {
            Uptime = TimeSpan.FromHours(30),
            CpuName = "AMD Ryzen 7 9800X3D 8-Core Processor",
            CpuCores = 8,
            RamModules = [new RamModule("F5-6000J3636F16G", 6000, 6000, 34, 16UL << 30)],
            Displays = [new DisplayInfo("X27U", 2560, 1440, 240, 240)],
            RestoreEnabled = true,
            SystemDriveFree = 400UL << 30,
            SystemDriveTotal = 1000UL << 30,
            ProblemDevices = [],
            GameModeOn = true,
            BackgroundRecordingOn = false,
            Hags = HagsState.On,
            PowerPlan = PowerPlanKind.Balanced,
            PowerPlanName = "Сбалансированная",
            TrimDisabled = false,
            StartupCount = 4,
            Vbs = VbsState.Off,
        };

        var results = HealthRules.Evaluate(snapshot);

        Assert.True(results.Count >= 12);
        Assert.All(results, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.False(string.IsNullOrWhiteSpace(r.Detail));
            Assert.Equal(r.Title.ToUpperInvariant(), r.Title);
        });
        Assert.Equal(results.Count, results.Select(r => r.Id).Distinct().Count());
        Assert.Equal(100, HealthScore.Calculate(results));
    }

    [Fact]
    public void Evaluate_skips_unknown_data()
    {
        var results = HealthRules.Evaluate(new SystemSnapshot());
        // Only checks with defined defaults remain (pending reboot list is empty → OK).
        Assert.DoesNotContain(results, r => r.Id is "ram-speed" or "refresh-rate" or "restore" or "trim" or "devices" or "vbs");
    }

    [Theory]
    [InlineData("ВРЕМЕННЫЕ ФАЙЛЫ WINDOWS", "Временные файлы Windows")]
    [InlineData("КЭШ ИНТЕРНЕТА WINDOWS (WININET)", "Кэш интернета Windows (WinINet)")]
    [InlineData("X3D С ДВУМЯ CCD", "X3D с двумя CCD")]
    [InlineData("НАСТРОЙКИ DIRECTX ДЛЯ ПК", "Настройки DirectX для ПК")]
    [InlineData("MONOLITH ЖДЁТ", "MONOLITH ждёт")]
    [InlineData("Уже Нормальный текст", "Уже Нормальный текст")]
    [InlineData("", "")]
    public void Capital_headings_become_sentence_case(string input, string expected) =>
        Assert.Equal(expected, Monolith.UI.SentenceCaseConverter.Apply(input));
}
