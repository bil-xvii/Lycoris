using Lycoris.Core;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Modules.Dashboard.Monitoring;

namespace Lycoris.Tests;

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
    public void Slow_monitor_is_a_shortcut_to_the_monitor_cards()
    {
        var r = HealthRules.CheckRefreshRate(new SystemSnapshot { Displays = [new DisplayInfo("LG", 2560, 1440, 60, 165)] });
        Assert.Equal(CheckSeverity.Warning, r!.Severity);
        Assert.Equal(CheckActionKind.FocusDevice, r.Action!.Kind);
        Assert.Equal("kind:monitor", r.Action.Target);
        Assert.Equal("НАСТРОИТЬ МОНИТОР", r.Action.Label);
        Assert.False(r.Action.RequiresAdmin); // the button only opens the page; the change is made there
        Assert.Contains("165", r.Detail);
        Assert.Contains("«Устройства и сеть»", r.Detail);
    }

    [Fact]
    public void Refresh_check_follows_the_monitor_cards_on_battery_and_for_a_laptop_panel()
    {
        var external = new DisplayInfo("LG", 2560, 1440, 60, 165);
        var panel = new DisplayInfo("Встроенный экран", 2560, 1600, 60, 165, IsInternal: true);

        Assert.Null(HealthRules.CheckRefreshRate(new SystemSnapshot { IsLaptop = true, OnBattery = true, Displays = [external] }));
        Assert.Null(HealthRules.CheckRefreshRate(new SystemSnapshot { IsLaptop = true, Displays = [panel] }));
        var both = HealthRules.CheckRefreshRate(new SystemSnapshot { IsLaptop = true, Displays = [panel, external] });
        Assert.Equal(CheckSeverity.Warning, both!.Severity);
        Assert.DoesNotContain("Встроенный экран", both.Detail);
    }

    [Fact]
    public void Monitor_at_max_refresh_has_no_action()
    {
        var r = HealthRules.CheckRefreshRate(new SystemSnapshot { Displays = [new DisplayInfo("X27U W3", 2560, 1440, 239, 240)] });
        Assert.Equal(CheckSeverity.Ok, r!.Severity);
        Assert.Null(r.Action);
    }

    [Fact]
    public void Focus_device_action_explains_that_it_changes_nothing()
    {
        var item = new Lycoris.Modules.Dashboard.CheckItemViewModel(
            HealthRules.CheckRefreshRate(new SystemSnapshot { Displays = [new DisplayInfo("LG", 2560, 1440, 60, 165)] })!, _ => Task.CompletedTask);
        Assert.True(item.ActionAvailable); // no admin needed to open the page
        Assert.Contains("ничего не меняет", item.ActionTooltip);
        Assert.Contains("Устройства и сеть", item.ActionTooltip);
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
        var fix = HealthRules.CheckTrim(new SystemSnapshot { TrimDisabled = true, SystemDisk = nvme })!.Action!;
        Assert.Equal(CheckActionKind.ApplyTweak, fix.Kind); // fixed through the journaled «trim-on» tweak
        Assert.Equal(HealthRules.TweakTrim, fix.Target);
        Assert.NotNull(Lycoris.Tweaks.TweakCatalog.Find(HealthRules.TweakTrim));
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
        Assert.NotNull(Lycoris.Tweaks.TweakCatalog.Find(HealthRules.TweakBalancedPlan));
        Assert.NotNull(Lycoris.Tweaks.TweakCatalog.Find(HealthRules.TweakBalancedPlanX3D));
        Assert.NotNull(Lycoris.Tweaks.TweakCatalog.Find(HealthRules.TweakPowerModeBestPerformance));
        Assert.NotNull(Lycoris.Tweaks.TweakCatalog.Find(HealthRules.TweakHibernationOff));
        Assert.NotNull(Lycoris.Tweaks.TweakCatalog.Find(HealthRules.TweakPagefileSystemManaged));
    }

    [Fact]
    public void Energy_saver_on_mains_power_is_a_warning_on_desktops_and_info_on_laptops()
    {
        var desktop = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "Intel Core i7-14700K", CpuCores = 20, PowerPlan = PowerPlanKind.Balanced, EnergySaverOn = true, OnAcPower = true,
        })!;
        Assert.Equal("power-plan", desktop.Id);
        Assert.Equal(CheckSeverity.Warning, desktop.Severity);
        Assert.Equal(8, desktop.Weight);
        Assert.Contains("ЭНЕРГОСБЕРЕЖЕНИЕ", desktop.Title);
        // No tweak controls energy saver: the button only opens the Windows settings page.
        Assert.Equal(CheckActionKind.OpenUri, desktop.Action!.Kind);
        Assert.Equal("ms-settings:powersleep", desktop.Action.Target);
        Assert.False(desktop.Action.RequiresAdmin);

        var laptop = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            IsLaptop = true, CpuName = "Intel Core i7-13700H", CpuCores = 14, PowerPlan = PowerPlanKind.Balanced, EnergySaverOn = true, OnAcPower = true,
        })!;
        Assert.Equal(CheckSeverity.Info, laptop.Severity);
        Assert.Equal(5, laptop.Weight);
        Assert.Equal("ms-settings:powersleep", laptop.Action!.Target);

        // It wins over the X3D advice and is reported even when the plan itself could not be read.
        var x3d = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuCores = 16, PowerPlan = PowerPlanKind.HighPerformance, EnergySaverOn = true, OnAcPower = true,
        })!;
        Assert.Equal(CheckActionKind.OpenUri, x3d.Action!.Kind);
        Assert.NotNull(HealthRules.CheckPowerPlan(new SystemSnapshot { PowerPlan = PowerPlanKind.Unknown, EnergySaverOn = true, OnAcPower = true }));

        // Energy saver on battery (or with an unknown mains state) is normal: the existing logic runs.
        var battery = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            IsLaptop = true, CpuName = "Intel Core i7-13700H", CpuCores = 14, PowerPlan = PowerPlanKind.Balanced, PowerPlanName = "Сбалансированная",
            EnergySaverOn = true, OnAcPower = false,
        })!;
        Assert.Equal(CheckSeverity.Ok, battery.Severity);
        Assert.Equal("ПЛАН ПИТАНИЯ «СБАЛАНСИРОВАННАЯ»", battery.Title);
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "Intel Core i7-14700K", CpuCores = 20, PowerPlan = PowerPlanKind.Balanced, EnergySaverOn = true,
        })!.Severity);
    }

    [Fact]
    public void Best_efficiency_mode_on_balanced_is_info_with_the_power_mode_tweak()
    {
        var efficiency = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "Intel Core i7-14700K", CpuCores = 20, PowerPlan = PowerPlanKind.Balanced, PowerPlanName = "Сбалансированная",
            AcPowerMode = Lycoris.Tweaks.TweakCatalog.PowerModeBestEfficiency, OnAcPower = true, EnergySaverOn = false,
        })!;
        Assert.Equal(CheckSeverity.Info, efficiency.Severity);
        Assert.Equal(5, efficiency.Weight);
        Assert.Contains("МАКСИМАЛЬНАЯ ЭФФЕКТИВНОСТЬ", efficiency.Title);
        // Dashboard is a shortcut into the one tweak that changes the mode; the button itself changes nothing.
        Assert.Equal(CheckActionKind.FocusTweak, efficiency.Action!.Kind);
        Assert.Equal(HealthRules.TweakPowerModeBestPerformance, efficiency.Action.Target);
        Assert.False(efficiency.Action.RequiresAdmin);

        // An unknown mains state (desktops without a battery) still counts: only "on battery" is excluded.
        var unknownAc = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "Intel Core i7-14700K", CpuCores = 20, PowerPlan = PowerPlanKind.Balanced,
            AcPowerMode = Lycoris.Tweaks.TweakCatalog.PowerModeBestEfficiency,
        })!;
        Assert.Equal(HealthRules.TweakPowerModeBestPerformance, unknownAc.Action!.Target);

        // Energy saver locks the mode to best efficiency: that case is reported as energy saver, not as the mode.
        var saver = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "Intel Core i7-14700K", CpuCores = 20, PowerPlan = PowerPlanKind.Balanced,
            AcPowerMode = Lycoris.Tweaks.TweakCatalog.PowerModeBestEfficiency, OnAcPower = true, EnergySaverOn = true,
        })!;
        Assert.Equal(CheckActionKind.OpenUri, saver.Action!.Kind);

        // On battery nothing new: efficiency is the point there.
        var battery = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            IsLaptop = true, CpuName = "Intel Core i7-13700H", CpuCores = 14, PowerPlan = PowerPlanKind.Balanced, PowerPlanName = "Сбалансированная",
            AcPowerMode = Lycoris.Tweaks.TweakCatalog.PowerModeBestEfficiency, OnAcPower = false, EnergySaverOn = false,
        })!;
        Assert.Equal(CheckSeverity.Ok, battery.Severity);
        Assert.Equal("ПЛАН ПИТАНИЯ «СБАЛАНСИРОВАННАЯ»", battery.Title);
        Assert.Null(battery.Action);

        // Other modes change nothing in the existing logic.
        var performance = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "Intel Core i7-14700K", CpuCores = 20, PowerPlan = PowerPlanKind.Balanced,
            AcPowerMode = Lycoris.Tweaks.TweakCatalog.PowerModeBestPerformance, OnAcPower = true, EnergySaverOn = false,
        })!;
        Assert.Equal(CheckSeverity.Ok, performance.Severity);
        Assert.Null(performance.Action);

        // Dual-CCD X3D: the catalog does not offer the mode there, so the dashboard does not point to it either.
        var x3d = HealthRules.CheckPowerPlan(new SystemSnapshot
        {
            CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuCores = 16, PowerPlan = PowerPlanKind.Balanced,
            AcPowerMode = Lycoris.Tweaks.TweakCatalog.PowerModeBestEfficiency, OnAcPower = true, EnergySaverOn = false,
        })!;
        Assert.Equal(CheckSeverity.Ok, x3d.Severity);
    }

    [Fact]
    public void Low_space_lists_system_files_and_offers_the_one_safe_lever()
    {
        const ulong gb = 1UL << 30;
        var desktop = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, HiberfilBytes = 13 * gb, PagefileBytes = 9 * gb, PagefileSystemManaged = true,
        })!;
        Assert.Equal(CheckSeverity.Warning, desktop.Severity);
        Assert.Equal("cleaner", desktop.Action!.Target); // the primary stays «К ОЧИСТКЕ»
        Assert.Contains("hiberfil.sys — 13,0 ГБ", desktop.Detail);
        Assert.Contains("«Отключить гибернацию» (ЭКСПЕРТ)", desktop.Detail);
        Assert.Contains("pagefile.sys — 9,0 ГБ: размером управляет Windows", desktop.Detail);
        Assert.DoesNotContain("Зарезервированное хранилище", desktop.Detail); // state not read: not mentioned
        Assert.Equal(CheckActionKind.FocusTweak, desktop.SecondaryAction!.Kind);
        Assert.Equal(HealthRules.TweakHibernationOff, desktop.SecondaryAction.Target);
        Assert.False(desktop.SecondaryAction.RequiresAdmin);

        // Laptop: hibernation-off is DO-NOT-INCLUDE there — the size is shown with the reason and no button.
        var laptop = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            IsLaptop = true, SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, HiberfilBytes = 13 * gb,
        })!;
        Assert.Contains("hiberfil.sys — 13,0 ГБ", laptop.Detail);
        Assert.Contains("На ноутбуке он нужен", laptop.Detail);
        Assert.DoesNotContain("Отключить гибернацию", laptop.Detail);
        Assert.Null(laptop.SecondaryAction);

        // A hand-set page file points to the existing repair; hibernation wins when both apply.
        var manual = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            IsLaptop = true, SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, PagefileBytes = 2 * gb, PagefileSystemManaged = false,
        })!;
        Assert.Contains("pagefile.sys — 2,0 ГБ: размер задан вручную", manual.Detail);
        Assert.Equal(HealthRules.TweakPagefileSystemManaged, manual.SecondaryAction!.Target);
        var twoLevers = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, HiberfilBytes = 13 * gb, PagefileSystemManaged = false, PagefileOnOtherDrive = false,
        })!;
        Assert.Equal(HealthRules.TweakHibernationOff, twoLevers.SecondaryAction!.Target);
        // No page file at all («Без файла подкачки» set by another optimizer): that is what the repair item exists for.
        Assert.Contains("Файл подкачки отключён — так игры и браузеры падают", twoLevers.Detail);
        Assert.DoesNotContain("задан вручную", twoLevers.Detail);
        Assert.Equal(HealthRules.TweakPagefileSystemManaged, HealthRules.SystemFileLever(new SystemSnapshot
        {
            PagefileSystemManaged = false, PagefileOnOtherDrive = false,
        })!.Target);

        // A page file moved to another drive (the usual small-SSD setup) takes no space on the system drive, and the repair
        // would create a multi-GB file on the drive that is already short of space — no lever, no «вручную» sentence.
        var moved = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, PagefileSystemManaged = false, PagefileOnOtherDrive = true,
        })!;
        Assert.Contains("Файл подкачки размещён на другом диске — место на системном диске он не занимает", moved.Detail);
        Assert.DoesNotContain("задан вручную", moved.Detail);
        Assert.DoesNotContain("отключён", moved.Detail);
        Assert.Null(moved.SecondaryAction);
        var movedAndLocal = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, PagefileBytes = 4 * gb, PagefileSystemManaged = false, PagefileOnOtherDrive = true,
        })!;
        Assert.Contains("pagefile.sys — 4,0 ГБ: файл подкачки настроен вручную и размещён ещё и на другом диске", movedAndLocal.Detail);
        Assert.Null(movedAndLocal.SecondaryAction);
        // Placement unknown (the WMI query failed): the fact stays honest and the item is still pointed to, with the caveat.
        var unknownPlace = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, PagefileSystemManaged = false,
        })!;
        Assert.Contains("Файл подкачки настроен вручную;", unknownPlace.Detail);
        Assert.Contains("перенесли его на другой диск", unknownPlace.Detail);
        Assert.Equal(HealthRules.TweakPagefileSystemManaged, unknownPlace.SecondaryAction!.Target);

        // Laptop/desktop fact unknown (battery/chassis WMI failed): hibernation off is not applicable in the catalog, so the
        // text shows the size without the desktop tail and no «ГИБЕРНАЦИЯ» button; the page-file lever still applies.
        var unknownHardware = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            HardwareFactsKnown = false, SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, HiberfilBytes = 13 * gb,
            PagefileBytes = 2 * gb, PagefileSystemManaged = false, PagefileOnOtherDrive = false,
        })!;
        Assert.Contains("hiberfil.sys — 13,0 ГБ: файл гибернации и быстрого запуска.", unknownHardware.Detail);
        Assert.DoesNotContain("Отключить гибернацию", unknownHardware.Detail);
        Assert.DoesNotContain("На ноутбуке он нужен", unknownHardware.Detail);
        Assert.Equal(HealthRules.TweakPagefileSystemManaged, unknownHardware.SecondaryAction!.Target);
        Assert.Null(HealthRules.SystemFileLever(new SystemSnapshot { HardwareFactsKnown = false, HiberfilBytes = 13 * gb }));

        // Reserved storage only when the state is known; no size figures are invented.
        var reserved = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, ReservedStorageState = "Enabled",
        })!;
        Assert.Contains("Зарезервированное хранилище включено: Windows освобождает его сама", reserved.Detail);
        Assert.Null(reserved.SecondaryAction);
        Assert.Contains("Зарезервированное хранилище отключено", HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb, ReservedStorageState = "Disabled",
        })!.Detail);

        // Nothing of this on a drive with enough space, and nothing when no fact is known.
        var fine = HealthRules.CheckDiskSpace(new SystemSnapshot { SystemDriveFree = 200 * gb, SystemDriveTotal = 500 * gb, HiberfilBytes = 13 * gb })!;
        Assert.DoesNotContain("hiberfil", fine.Detail);
        Assert.Null(fine.SecondaryAction);
        var bare = HealthRules.CheckDiskSpace(new SystemSnapshot { SystemDriveFree = 12 * gb, SystemDriveTotal = 238 * gb })!;
        Assert.EndsWith("что именно будет удалено.", bare.Detail);
        Assert.Null(bare.SecondaryAction);
    }

    [Theory]
    [InlineData("Enabled\r\n", "Enabled")]
    [InlineData("\r\nDisabled\r\n\r\n", "Disabled")]
    [InlineData("enabled", "Enabled")]
    [InlineData("Get-WindowsReservedStorageState : not recognized", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Reserved_storage_state_is_parsed_from_powershell_output(string? output, string? expected) =>
        Assert.Equal(expected, HealthRules.ParseReservedStorageState(output));

    [Fact]
    public void Memory_integrity_is_information_only()
    {
        var r = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.MemoryIntegrity })!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Null(r.Action); // never offered to switch off
        Assert.Equal(0, r.Weight);
        Assert.Contains("«Оптимизации»", r.Detail); // the only way to turn it off stays the expert item
        // Memory integrity off is reported too (information only, no score penalty), but only when the state is known.
        var off = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.Off })!;
        Assert.Equal(CheckSeverity.Info, off.Severity);
        Assert.Equal(0, off.Weight);
        Assert.Null(off.Action);
        Assert.Null(HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.Unknown }));
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
        Assert.Null(pending.Action); // Lycoris never reboots by itself
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
        Assert.Contains(results, r => r.Id == "vbs" && r.Severity == CheckSeverity.Info && r.Weight == 0);
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
        Assert.DoesNotContain(results, r => r.Id is "ram-speed" or "refresh-rate" or "restore" or "trim" or "devices" or "vbs"
            or "boot-security" or "monitor-gpu" or "disk-health" or "stability" or "rebar" or "pcie-link");
    }

    [Theory]
    [InlineData("ВРЕМЕННЫЕ ФАЙЛЫ WINDOWS", "Временные файлы Windows")]
    [InlineData("КЭШ ИНТЕРНЕТА WINDOWS (WININET)", "Кэш интернета Windows (WinINet)")]
    [InlineData("X3D С ДВУМЯ CCD", "X3D с двумя CCD")]
    [InlineData("НАСТРОЙКИ DIRECTX ДЛЯ ПК", "Настройки DirectX для ПК")]
    [InlineData("LYCORIS ЖДЁТ", "Lycoris ждёт")]
    [InlineData("Уже Нормальный текст", "Уже Нормальный текст")]
    [InlineData("", "")]
    public void Capital_headings_become_sentence_case(string input, string expected) =>
        Assert.Equal(expected, Lycoris.UI.SentenceCaseConverter.Apply(input));
}

public sealed class DashboardBootSecurityTests
{
    private static SystemSnapshot Pc(FirmwareKind fw, bool? secureBoot, TpmState tpm, int build = 26100, params string[] games) =>
        new() { Firmware = fw, SecureBootOn = secureBoot, Tpm = tpm, OsBuild = build, InstalledGames = games };

    [Fact]
    public void Uefi_secure_boot_and_tpm2_are_ok()
    {
        var r = HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, true, TpmState.V20))!;
        Assert.Equal(CheckSeverity.Ok, r.Severity);
        Assert.Equal("boot-security", r.Id);
        Assert.Null(r.Action);
    }

    [Fact]
    public void Unknown_state_gives_no_verdict()
    {
        Assert.Null(HealthRules.CheckBootSecurity(new SystemSnapshot()));
        Assert.Null(HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, null, TpmState.V20)));   // no admin, no registry value
        Assert.Null(HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, true, TpmState.Unknown)));
    }

    [Fact]
    public void Secure_boot_off_without_a_game_that_needs_it_is_information_without_penalty()
    {
        var r = HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, false, TpmState.V20, 26100, "Counter-Strike 2"))!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Equal(0, r.Weight);
        Assert.Equal("SECURE BOOT ВЫКЛЮЧЕН", r.Title);
        Assert.Contains("Install default keys", r.Detail);
        Assert.Contains("aka.ms/myrecoverykey", r.Detail);   // BitLocker recovery warning before any BIOS step
        Assert.Contains("Lycoris прошивку", r.Detail);
        Assert.Null(r.Action);                               // firmware is changed by the user only
    }

    [Fact]
    public void A_library_game_that_needs_secure_boot_makes_it_a_warning()
    {
        var r = HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, false, TpmState.V20, 26100, "Battlefield™ 6"))!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("Battlefield 6 (EA Javelin)", r.Detail);
        Assert.Null(r.Action);
    }

    [Fact]
    public void Legacy_bios_warns_about_mbr_and_never_offers_a_conversion()
    {
        var r = HealthRules.CheckBootSecurity(Pc(FirmwareKind.LegacyBios, null, TpmState.V20))!;
        Assert.Equal("ЗАГРУЗКА В РЕЖИМЕ LEGACY BIOS", r.Title);
        Assert.Contains("MBR", r.Detail);
        Assert.Contains("mbr2gpt /validate", r.Detail);
        Assert.Null(r.Action);
    }

    [Fact]
    public void Vanguard_requirement_applies_on_windows_11_only()
    {
        var win10 = HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, true, TpmState.NotFound, 19045, "VALORANT"))!;
        var win11 = HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, true, TpmState.NotFound, 22631, "VALORANT"))!;
        Assert.Equal(CheckSeverity.Info, win10.Severity);
        Assert.Equal(CheckSeverity.Warning, win11.Severity);
        Assert.Equal("TPM 2.0 НЕДОСТУПЕН", win11.Title);
        Assert.Contains("fTPM/PTT", win11.Detail);
        Assert.Contains("Clear TPM", win11.Detail); // told never to clear it
    }

    [Fact]
    public void Tpm_12_and_secure_boot_off_together()
    {
        var r = HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, false, TpmState.V12, 26100, "Call of Duty®"))!;
        Assert.Equal("SECURE BOOT И TPM 2.0 НЕДОСТУПНЫ", r.Title);
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("TPM 1.2", r.Detail);
        Assert.Contains("Call of Duty (Ricochet)", r.Detail);
    }

    [Fact]
    public void Anti_cheat_games_are_matched_by_name()
    {
        var found = HealthRules.AntiCheatGames(["Battlefield™  6", "call of duty®", "VALORANT", "Dota 2", "Battlefield 2042"], 26100);
        Assert.Equal(["Battlefield 6", "Call of Duty", "VALORANT"], found.Select(g => g.Title));
        Assert.DoesNotContain(HealthRules.AntiCheatGames(["VALORANT"], 19045), g => g.Title == "VALORANT");
        Assert.Empty(HealthRules.AntiCheatGames([], 26100));
    }

    [Theory]
    [InlineData("Call of Duty®")]                  // the HQ app that hosts Black Ops 7 and Warzone
    [InlineData("Call of Duty HQ")]
    [InlineData("Call of Duty®: Black Ops 7")]
    [InlineData("Call of Duty: Black Ops 7 - Vault Edition")]
    [InlineData("Call of Duty®: Warzone™")]
    public void Call_of_duty_titles_that_need_ricochet_requirements_match(string title) =>
        Assert.Equal("Call of Duty", Assert.Single(HealthRules.AntiCheatGames([title], 26100)).Title);

    [Fact]
    public void Older_call_of_duty_titles_and_lookalikes_do_not_match()
    {
        // Per Activision only Black Ops 7 and Warzone need TPM 2.0 + Secure Boot; older titles start without them.
        Assert.Empty(HealthRules.AntiCheatGames(
            ["Call of Duty®: Black Ops III", "Call of Duty: WWII", "Call of Duty 4: Modern Warfare", "Call of Duty®: Black Ops 6",
             "Call of Duty®: Modern Warfare® III", "Call of Duty: Black Ops 70", "Warzone 2100", "Battlefield 60", "Battlefield 2042"], 26100));

        var r = HealthRules.CheckBootSecurity(Pc(FirmwareKind.Uefi, false, TpmState.NotFound, 26100, "Call of Duty®: Black Ops III"))!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Equal(0, r.Weight);
        Assert.DoesNotContain("не запустятся", r.Detail);
        Assert.Contains("Black Ops 7 и Warzone", r.Detail);
    }

    [Theory]
    [InlineData("SB=ON\r\n", true)]
    [InlineData("SB=OFF", false)]
    [InlineData("SB=UNKNOWN", null)]
    [InlineData("Cmdlet not supported on this platform", null)]
    [InlineData("", null)]
    public void Confirm_secure_boot_output(string output, bool? expected) => Assert.Equal(expected, HealthRules.ParseSecureBootOutput(output));
}

public sealed class DashboardKernelProtectionTests
{
    private const string Blocklist = "{d2bda982-ccf6-4344-ac5b-0b44427b6816}";

    private static string Json(params (string Id, bool Enforced)[] policies) =>
        "{\"Policies\":[" + string.Join(",", policies.Select(p =>
            $"{{\"PolicyID\":\"{p.Id}\",\"FriendlyName\":\"x\",\"IsEnforced\":{(p.Enforced ? "true" : "false")},\"IsAuthorized\":true}}")) +
        "],\"OperationResult\":0}";

    [Fact]
    public void Citool_json_states_are_parsed()
    {
        var on = HealthRules.ParseCiPolicies(Json((Blocklist, true), ("0283ac0f-fff1-49ae-ada1-8a933130cad6", true)))!;
        Assert.True(on.Blocklist);
        Assert.Equal(SmartAppControlState.On, on.Sac);

        var eval = HealthRules.ParseCiPolicies(Json((Blocklist, false), ("1283AC0F-FFF1-49AE-ADA1-8A933130CAD6", true)))!;
        Assert.False(eval.Blocklist);
        Assert.Equal(SmartAppControlState.Evaluation, eval.Sac);

        // The newer WHCP driver policy is a different policy and must not count as the blocklist.
        var other = HealthRules.ParseCiPolicies(Json(("784c4414-79f4-4c32-a6a5-f0fb42a51d0d", true)))!;
        Assert.False(other.Blocklist);
        Assert.Equal(SmartAppControlState.Off, other.Sac);
    }

    [Fact]
    public void Citool_output_with_noise_and_lowercase_keys_is_accepted()
    {
        var r = HealthRules.ParseCiPolicies("Operation successful\r\n{\"policies\":[{\"policyid\":\"d2bda982-ccf6-4344-ac5b-0b44427b6816\",\"isenforced\":\"True\"}]}\r\n");
        Assert.True(r!.Blocklist);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Доступ запрещён")]
    [InlineData("{not json}")]
    [InlineData("{\"Policies\":[]}")]
    [InlineData("{\"OperationResult\":0}")]
    public void Unusable_citool_output_is_unknown(string? output) => Assert.Null(HealthRules.ParseCiPolicies(output));

    [Fact]
    public void Blocklist_off_with_memory_integrity_off_is_the_only_warning()
    {
        var r = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.Off, DriverBlocklistEnforced = false })!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Equal("vbs", r.Id);
        Assert.Equal(CheckActionKind.OpenUri, r.Action!.Kind);
        Assert.Equal("ms-settings:windowsdefender", r.Action.Target);
        Assert.False(r.Action.RequiresAdmin);
        Assert.Contains("Lycoris эту защиту не меняет", r.Detail);

        var vbsOnly = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.VbsOnly, DriverBlocklistEnforced = false })!;
        Assert.Equal(CheckSeverity.Warning, vbsOnly.Severity);
    }

    [Fact]
    public void Memory_integrity_on_implies_the_blocklist()
    {
        // Documented: HVCI on enforces the blocklist, whatever an unreadable or odd CiTool answer says.
        var r = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.MemoryIntegrity, DriverBlocklistEnforced = false })!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Null(r.Action);
        Assert.Contains("действует всегда", r.Detail);
    }

    [Fact]
    public void Smart_app_control_on_implies_the_blocklist()
    {
        // Documented: Smart App Control on enforces the blocklist too, whatever CiTool lists.
        var r = HealthRules.CheckVbs(new SystemSnapshot
        {
            Vbs = VbsState.Off, SmartAppControl = SmartAppControlState.On, DriverBlocklistEnforced = false,
        })!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Null(r.Action);
        Assert.Contains("список блокировки уязвимых драйверов Microsoft действует", r.Detail);

        // Evaluation mode does not enforce anything yet: the warning stays.
        var eval = HealthRules.CheckVbs(new SystemSnapshot
        {
            Vbs = VbsState.Off, SmartAppControl = SmartAppControlState.Evaluation, DriverBlocklistEnforced = false,
        })!;
        Assert.Equal(CheckSeverity.Warning, eval.Severity);
    }

    [Fact]
    public void Unknown_blocklist_makes_no_claim()
    {
        var r = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.Off })!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.DoesNotContain("список блокировки", r.Detail);

        var known = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.Off, DriverBlocklistEnforced = true })!;
        Assert.Equal(CheckSeverity.Info, known.Severity);
        Assert.Contains("список блокировки уязвимых драйверов Microsoft действует", known.Detail);
    }

    [Fact]
    public void Smart_app_control_is_mentioned_when_active()
    {
        var on = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.MemoryIntegrity, SmartAppControl = SmartAppControlState.On })!;
        Assert.Contains("Интеллектуальное управление приложениями включено", on.Detail);
        var off = HealthRules.CheckVbs(new SystemSnapshot { Vbs = VbsState.MemoryIntegrity, SmartAppControl = SmartAppControlState.Off })!;
        Assert.DoesNotContain("Интеллектуальное управление", off.Detail);
    }
}

public sealed class DashboardMonitorGpuTests
{
    private const string DKey = "0x00000000_0x0000e1ae", IKey = "0x00000000_0x0000f00d";
    private static readonly GpuInfo Rtx = new("NVIDIA GeForce RTX 5070 Ti", GpuVendor.Nvidia, "32.0.15.8180", DateTime.Today, 16UL << 30, "PCI\\VEN_10DE");
    private static readonly GpuInfo Apu = new("AMD Radeon(TM) Graphics", GpuVendor.Amd, "31.0.24033.1003", DateTime.Today, 512UL << 20, "PCI\\VEN_1002");

    private static DisplayInfo Display(string name, string key, int hz, bool primary = false, bool internalPanel = false) =>
        new(name, 2560, 1440, hz, hz, null, key, internalPanel, primary);

    private static SystemSnapshot Pc(int cardOutputs = 4, bool laptop = false, params DisplayInfo[] displays) => new()
    {
        IsLaptop = laptop,
        Gpus = [Rtx, Apu],
        Adapters = [new GpuAdapter(DKey, "NVIDIA GeForce RTX 5070 Ti", cardOutputs), new GpuAdapter(IKey, "AMD Radeon(TM) Graphics", 3)],
        Displays = displays,
    };

    [Fact]
    public void Primary_monitor_on_the_igpu_warns()
    {
        var r = HealthRules.CheckMonitorOnGpu(Pc(4, false, Display("X27U", IKey, 240, primary: true)))!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Equal("monitor-gpu", r.Id);
        Assert.Contains("X27U", r.Detail);
        Assert.Contains("AMD Radeon(TM) Graphics", r.Detail);
        Assert.Contains("NVIDIA GeForce RTX 5070 Ti", r.Detail);
        Assert.Contains("кабель монитора", r.Detail);
        Assert.Null(r.Action); // a physical fix: nothing to click
    }

    [Fact]
    public void Monitor_on_the_card_is_ok()
    {
        var r = HealthRules.CheckMonitorOnGpu(Pc(4, false, Display("X27U", DKey, 240, primary: true)))!;
        Assert.Equal(CheckSeverity.Ok, r.Severity);
        Assert.Contains("NVIDIA GeForce RTX 5070 Ti", r.Detail);
    }

    [Fact]
    public void A_second_monitor_on_the_igpu_is_a_normal_setup()
    {
        var r = HealthRules.CheckMonitorOnGpu(Pc(4, false,
            Display("X27U", DKey, 240, primary: true), Display("Office 60", IKey, 60)))!;
        Assert.Equal(CheckSeverity.Ok, r.Severity);
    }

    [Fact]
    public void The_fastest_monitor_counts_even_when_it_is_not_primary()
    {
        var r = HealthRules.CheckMonitorOnGpu(Pc(4, false,
            Display("Office 60", DKey, 60, primary: true), Display("Gaming 240", IKey, 240)))!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("Gaming 240", r.Detail);
    }

    [Fact]
    public void Laptops_internal_panels_unknown_adapters_and_cards_without_outputs_never_warn()
    {
        Assert.Null(HealthRules.CheckMonitorOnGpu(Pc(4, true, Display("Panel", IKey, 165, primary: true))));
        Assert.Null(HealthRules.CheckMonitorOnGpu(Pc(4, false, Display("Встроенный дисплей", IKey, 60, primary: true, internalPanel: true))));
        Assert.Null(HealthRules.CheckMonitorOnGpu(Pc(4, false, Display("Parsec Virtual", "0x00000000_0x00001234", 60, primary: true))));
        Assert.Null(HealthRules.CheckMonitorOnGpu(Pc(0, false, Display("X27U", IKey, 240, primary: true)))); // dGPU with no outputs
        Assert.Null(HealthRules.CheckMonitorOnGpu(Pc(4, false, Display("X27U", "", 240, primary: true))));      // adapter unknown
    }

    [Fact]
    public void Pcs_without_a_graphics_card_get_no_row()
    {
        var s = Pc(4, false, Display("X27U", IKey, 144, primary: true)) with { Gpus = [Apu], Adapters = [new GpuAdapter(IKey, Apu.Name, 3)] };
        Assert.Null(HealthRules.CheckMonitorOnGpu(s));
    }

    [Fact]
    public void Basic_display_adapter_is_neither_integrated_nor_a_card()
    {
        var basic = new GpuInfo("Microsoft Basic Display Adapter", GpuVendor.Microsoft, "10.0", null, 0, "PCI\\VEN_10DE");
        Assert.False(HealthRules.IsOnboardGpu(basic));
        Assert.False(HealthRules.IsDiscreteCard(basic));
        var s = Pc(4, false, Display("X27U", IKey, 144, primary: true)) with
        {
            Gpus = [Rtx, basic],
            Adapters = [new GpuAdapter(DKey, Rtx.Name, 4), new GpuAdapter(IKey, basic.Name, 2)],
        };
        Assert.Null(HealthRules.CheckMonitorOnGpu(s));
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon(TM) 780M Graphics", true)]
    [InlineData("AMD Radeon(TM) RX Vega 11 Graphics", true)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("Radeon RX Vega", false)]
    [InlineData("AMD Radeon RX 9070 XT", false)]
    [InlineData(null, false)]
    public void Amd_apus_are_recognised_by_name(string? name, bool apu) => Assert.Equal(apu, HealthRules.IsAmdApuName(name));
}

public sealed class DashboardDiskHealthTests
{
    private static DiskHealth Nvme(int? wear = 3, int? temp = 41, int? max = 82, ulong? read = 0, ulong? write = 0,
        DiskHealthStatus status = DiskHealthStatus.Healthy) =>
        new("Samsung SSD 990 PRO 2TB", "NVMe", "SSD", status, wear, temp, max, read, write);

    [Fact]
    public void Healthy_drives_are_ok_and_list_their_counters()
    {
        var r = HealthRules.CheckDiskHealth(new SystemSnapshot { Elevated = true, Disks = [Nvme()] })!;
        Assert.Equal(CheckSeverity.Ok, r.Severity);
        Assert.Equal("disk-health", r.Id);
        Assert.Contains("Samsung SSD 990 PRO 2TB: износ 3 %, 41 °C, ошибок нет", r.Detail);
        Assert.Null(r.Action);
    }

    [Fact]
    public void Wear_errors_and_status_warn()
    {
        var worn = HealthRules.CheckDiskHealth(new SystemSnapshot { Elevated = true, Disks = [Nvme(wear: 85)] })!;
        Assert.Equal(CheckSeverity.Warning, worn.Severity);
        Assert.Equal(12, worn.Weight);
        Assert.Contains("износ 85 %", worn.Detail);
        Assert.Contains("резервную копию", worn.Detail);

        var dead = HealthRules.CheckDiskHealth(new SystemSnapshot { Elevated = true, Disks = [Nvme(wear: 100)] })!;
        Assert.Equal(20, dead.Weight);

        var errors = HealthRules.CheckDiskHealth(new SystemSnapshot { Elevated = true, Disks = [Nvme(read: 2, write: 1)] })!;
        Assert.Equal(CheckSeverity.Warning, errors.Severity);
        Assert.Contains("3 неисправленные ошибки", errors.Detail);

        var unhealthy = HealthRules.CheckDiskHealth(new SystemSnapshot { Disks = [new DiskHealth("WD Blue", "SATA", "HDD", DiskHealthStatus.Unhealthy)] })!;
        Assert.Equal(CheckSeverity.Warning, unhealthy.Severity);
        Assert.Equal(20, unhealthy.Weight);
        Assert.Contains("неисправный", unhealthy.Detail);
    }

    [Fact]
    public void Hot_drive_gets_cooling_advice()
    {
        var r = HealthRules.CheckDiskHealth(new SystemSnapshot { Elevated = true, Disks = [Nvme(temp: 79, max: 82)] })!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("79 °C при пределе 82 °C", r.Detail);
        Assert.Contains("обдув", r.Detail);
        Assert.DoesNotContain("резервную копию", r.Detail);
        Assert.False(HealthRules.IsDiskHot(Nvme(temp: 60, max: 82)));
        Assert.False(HealthRules.IsDiskHot(Nvme(temp: 80, max: null))); // unknown limit: never "hot"
    }

    [Fact]
    public void Without_admin_only_the_windows_verdict_is_shown()
    {
        var r = HealthRules.CheckDiskHealth(new SystemSnapshot
        {
            Elevated = false,
            Disks = [new DiskHealth("Samsung SSD 990 PRO 2TB", "NVMe", "SSD", DiskHealthStatus.Healthy)],
        })!;
        Assert.Equal(CheckSeverity.Ok, r.Severity);
        Assert.Contains("исправен по данным Windows", r.Detail);
        Assert.Contains("правами администратора", r.Detail);
        Assert.DoesNotContain("ошибок нет", r.Detail); // never claimed without the counters
    }

    [Fact]
    public void Missing_values_are_unknown_not_zero()
    {
        var hdd = new DiskHealth("WD Blue", "SATA", "HDD", DiskHealthStatus.Healthy, WearPercent: null, TemperatureC: 35, ReadErrorsUncorrected: 0);
        var text = HealthRules.DiskSummary(hdd);
        Assert.DoesNotContain("износ", text);      // HDDs report no wear
        Assert.DoesNotContain("ошибок нет", text);  // only one of the two error counters is known
        Assert.Contains("35 °C", text);
        Assert.Null(HealthRules.CheckDiskHealth(new SystemSnapshot { Disks = [new DiskHealth("USB", "USB", "", DiskHealthStatus.Unknown)] }));
        Assert.Null(HealthRules.CheckDiskHealth(new SystemSnapshot { Disks = [] }));
        Assert.Null(HealthRules.CheckDiskHealth(new SystemSnapshot()));
    }

    [Theory]
    [InlineData((ushort)0, DiskHealthStatus.Healthy)]
    [InlineData((ushort)1, DiskHealthStatus.Warning)]
    [InlineData((ushort)2, DiskHealthStatus.Unhealthy)]
    [InlineData((ushort)5, DiskHealthStatus.Unknown)]
    [InlineData(null, DiskHealthStatus.Unknown)]
    public void Wmi_health_status(ushort? value, DiskHealthStatus expected) => Assert.Equal(expected, HealthRules.DiskStatusFromWmi(value));
}

public sealed class DashboardStabilityTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0);

    private static SystemSnapshot Log(DateTime? oldest = null, params StabilityEvent[] events) =>
        new() { Now = Now, Stability = new StabilityLog(Now.AddDays(-30), oldest, events) };

    private static StabilityEvent Whea(int id, int level, int daysAgo = 3) => new(Now.AddDays(-daysAgo), StabilityEventKind.Whea, id, level);

    [Theory]
    [InlineData(0x1000007Eu, 0x7Eu)]
    [InlineData(0x124u, 0x124u)]
    [InlineData(0u, 0u)]
    public void Bug_check_variants_are_normalized(uint raw, uint code) => Assert.Equal(code, HealthRules.NormalizeBugCheck(raw));

    [Theory]
    [InlineData("0x0000009f (0xffffffffc000009a, 0x0000000000000001, 0x0000000000000000, 0x0000000000000000)", 0x9Fu)]
    [InlineData("0x00000124 (0x0, 0x1, 0x2, 0x3)", 0x124u)]
    [InlineData("0x1000007e (0x1, 0x2)", 0x7Eu)]
    [InlineData("", 0u)]
    [InlineData(null, 0u)]
    [InlineData("нет данных", 0u)]
    public void Wer_bug_check_codes_are_parsed(string? param1, uint code) => Assert.Equal(code, HealthRules.ParseWerBugCheck(param1));

    [Fact]
    public void Bug_check_names_and_causes()
    {
        Assert.Equal("0x124 WHEA_UNCORRECTABLE_ERROR", HealthRules.BugCheckText(0x124));
        Assert.Equal("0x1C4", HealthRules.BugCheckText(0x1C4));
        Assert.Equal(CrashCause.Gpu, HealthRules.BugCheckCause(0x116));
        Assert.Equal(CrashCause.Memory, HealthRules.BugCheckCause(0x1A));
        Assert.Equal(CrashCause.Unknown, HealthRules.BugCheckCause(0x1C4));
    }

    [Theory]
    [InlineData(19, 3, WheaComponent.Processor, WheaSeverity.Corrected)]
    [InlineData(18, 2, WheaComponent.Processor, WheaSeverity.Fatal)]
    [InlineData(17, 3, WheaComponent.Pcie, WheaSeverity.Corrected)]
    [InlineData(47, 3, WheaComponent.Memory, WheaSeverity.Corrected)]
    [InlineData(46, 1, WheaComponent.Memory, WheaSeverity.Fatal)]
    [InlineData(1, 4, WheaComponent.Other, WheaSeverity.Ignored)]
    public void Whea_is_classified_by_level_not_by_a_fixed_id_list(int id, int level, WheaComponent component, WheaSeverity severity) =>
        Assert.Equal((component, severity), HealthRules.ClassifyWhea(id, level));

    [Fact]
    public void One_crash_is_one_incident_and_a_crash_loop_is_several()
    {
        var t = Now.AddDays(-2);
        var merged = HealthRules.MergeCrashes(
        [
            new StabilityEvent(t, StabilityEventKind.KernelPower41, 41, 1, BugCheck: 0x9F),
            new StabilityEvent(t.AddSeconds(5), StabilityEventKind.UnexpectedShutdown, 6008, 2),
            new StabilityEvent(t.AddMinutes(2), StabilityEventKind.BugCheckReport, 1001, 2, BugCheck: 0x9F),
            // Crash loop: the next boot crashed again three minutes later.
            new StabilityEvent(t.AddMinutes(3), StabilityEventKind.KernelPower41, 41, 1, BugCheck: 0),
            // Hours later: a hard power-off.
            new StabilityEvent(t.AddHours(5), StabilityEventKind.KernelPower41, 41, 1, BugCheck: 0, PowerButton: true),
        ]);
        Assert.Equal(3, merged.Count);
        Assert.Equal(0x9Fu, merged[0].BugCheck);
        Assert.Equal(0u, merged[1].BugCheck);
        Assert.True(merged[2].PowerButton);
    }

    [Fact]
    public void Gpu_reset_records_within_a_minute_are_one_reset()
    {
        var t = Now.AddDays(-1);
        StabilityEvent R(double seconds) => new(t.AddSeconds(seconds), StabilityEventKind.GpuReset, 4101, 3);
        Assert.Equal(2, HealthRules.CountGpuResets([R(0), R(4), R(9), R(300)]));
        Assert.Equal(0, HealthRules.CountGpuResets([]));
    }

    [Fact]
    public void A_clean_log_is_ok_and_names_the_period()
    {
        var r = HealthRules.CheckStability(Log())!;
        Assert.Equal(CheckSeverity.Ok, r.Severity);
        Assert.Equal("СИСТЕМА РАБОТАЕТ СТАБИЛЬНО", r.Title);
        Assert.StartsWith("За 30 дней не было", r.Detail);
        Assert.Null(HealthRules.CheckStability(new SystemSnapshot()));
    }

    [Fact]
    public void A_short_log_reports_the_real_window()
    {
        var r = HealthRules.CheckStability(Log(Now.AddDays(-12)))!;
        Assert.StartsWith("За 12 дней (журнал Windows хранит события с 15.10.2026)", r.Detail);
    }

    [Fact]
    public void Blue_screens_warn_with_codes_and_point_to_drivers()
    {
        var r = HealthRules.CheckStability(Log(null,
            new StabilityEvent(Now.AddDays(-4), StabilityEventKind.KernelPower41, 41, 1, BugCheck: 0x116),
            new StabilityEvent(Now.AddDays(-4).AddMinutes(1), StabilityEventKind.BugCheckReport, 1001, 2, BugCheck: 0x116)))!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Equal(15, r.Weight);
        Assert.Equal("СБОИ ЗА 30 ДНЕЙ", r.Title);
        Assert.Contains("1 синий экран (0x116 VIDEO_TDR_FAILURE)", r.Detail);
        Assert.Equal(CheckActionKind.Navigate, r.Action!.Kind);
        Assert.Equal("drivers", r.Action.Target);
    }

    [Fact]
    public void Corrected_memory_errors_are_information_pointing_to_the_memory_test()
    {
        var r = HealthRules.CheckStability(Log(null, Whea(47, 3), Whea(47, 3, 5), Whea(47, 3, 6)))!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Contains("3 исправленные ошибки памяти (WHEA)", r.Detail);
        Assert.Equal("tools", r.Action!.Target);
        Assert.False(r.Action.RequiresAdmin);
    }

    [Fact]
    public void Processor_errors_get_bios_advice_not_the_memory_test()
    {
        var r = HealthRules.CheckStability(Log(null, Whea(19, 3)))!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Contains("Диагностика памяти такие ошибки не находит", r.Detail);
        Assert.Contains("Lycoris настройки BIOS не меняет", r.Detail);
        Assert.Null(r.Action);

        var fatal = HealthRules.CheckStability(Log(null, Whea(18, 2)))!;
        Assert.Equal(CheckSeverity.Warning, fatal.Severity);
        Assert.Contains("неисправимая аппаратная ошибка WHEA", fatal.Detail);
    }

    [Fact]
    public void A_few_corrected_pcie_errors_are_normal()
    {
        var few = HealthRules.CheckStability(Log(null, Enumerable.Range(0, 5).Select(_ => Whea(17, 3)).ToArray()))!;
        Assert.Equal(CheckSeverity.Ok, few.Severity);
        Assert.Contains("это нормально", few.Detail);

        var burst = HealthRules.CheckStability(Log(null, Enumerable.Range(0, HealthRules.PcieBurst).Select(_ => Whea(17, 3)).ToArray()))!;
        Assert.Equal(CheckSeverity.Info, burst.Severity);
        Assert.Contains("шины PCIe", burst.Detail);
    }

    [Fact]
    public void Restarts_without_a_code_explain_the_three_causes()
    {
        var one = HealthRules.CheckStability(Log(null, new StabilityEvent(Now.AddDays(-3), StabilityEventKind.KernelPower41, 41, 1)))!;
        Assert.Equal(CheckSeverity.Info, one.Severity);
        Assert.Contains("пропадание питания", one.Detail);
        Assert.Contains("после зависания", one.Detail);
        Assert.Contains("не успела записать код", one.Detail);

        var two = HealthRules.CheckStability(Log(null,
            new StabilityEvent(Now.AddDays(-3), StabilityEventKind.KernelPower41, 41, 1),
            new StabilityEvent(Now.AddDays(-9), StabilityEventKind.KernelPower41, 41, 1, PowerButton: true)))!;
        Assert.Equal(CheckSeverity.Warning, two.Severity);
        Assert.Equal(10, two.Weight);
        Assert.Contains("выключений кнопкой питания: 1", two.Detail);
    }

    [Fact]
    public void Gpu_driver_resets_point_to_the_drivers_page()
    {
        var one = HealthRules.CheckStability(Log(null, new StabilityEvent(Now.AddDays(-1), StabilityEventKind.GpuReset, 4101, 3)))!;
        Assert.Equal(CheckSeverity.Info, one.Severity);
        Assert.Equal("drivers", one.Action!.Target);

        var two = HealthRules.CheckStability(Log(null,
            new StabilityEvent(Now.AddDays(-1), StabilityEventKind.GpuReset, 4101, 3),
            new StabilityEvent(Now.AddDays(-2), StabilityEventKind.GpuReset, 4101, 3)))!;
        Assert.Equal(CheckSeverity.Warning, two.Severity);
        Assert.Contains("2 сброса драйвера видеокарты", two.Detail);
    }
}

public sealed class DashboardGpuLinkTests
{
    private const ulong MiB = 1UL << 20;

    [Theory]
    [InlineData(16384 * MiB, 16303 * MiB, RebarState.On)]
    [InlineData(256 * MiB, 8192 * MiB, RebarState.NotActive)]
    [InlineData(512 * MiB, 8192 * MiB, RebarState.Unknown)]
    [InlineData(256 * MiB, 512 * MiB, RebarState.Unknown)]
    [InlineData(0UL, 8192 * MiB, RebarState.Unknown)]
    public void Rebar_is_classified_from_bar1_and_vram(ulong bar1, ulong vram, RebarState expected) =>
        Assert.Equal(expected, HealthRules.ClassifyRebar(bar1, vram));

    [Fact]
    public void Rebar_unknown_values_give_no_row()
    {
        Assert.Equal(RebarState.Unknown, HealthRules.ClassifyRebar(null, 8192 * MiB));
        Assert.Null(HealthRules.CheckRebar(new SystemSnapshot()));
        Assert.Null(HealthRules.CheckRebar(new SystemSnapshot { GpuLink = new GpuLinkFacts("RTX", 512 * MiB, 8192 * MiB) }));
    }

    [Fact]
    public void Rebar_not_active_is_low_weight_information_with_safe_bios_advice()
    {
        var link = new GpuLinkFacts("NVIDIA GeForce RTX 3070", 256 * MiB, 8192 * MiB);
        var uefi = HealthRules.CheckRebar(new SystemSnapshot { GpuLink = link, Firmware = FirmwareKind.Uefi })!;
        Assert.Equal(CheckSeverity.Info, uefi.Severity);
        Assert.Equal("RESIZABLE BAR НЕ АКТИВЕН", uefi.Title);
        Assert.True(uefi.Weight <= 3);
        Assert.Contains("Above 4G Decoding", uefi.Detail);
        Assert.Contains("выключите его", uefi.Detail);
        Assert.DoesNotContain("выключен в BIOS", uefi.Detail); // a cause, not a claim
        Assert.Null(uefi.Action);

        // Legacy boot: turning CSM off would make Windows unbootable — never suggested.
        var legacy = HealthRules.CheckRebar(new SystemSnapshot { GpuLink = link, Firmware = FirmwareKind.LegacyBios })!;
        Assert.Contains("Не выключайте CSM", legacy.Detail);
        Assert.DoesNotContain("выключите его", legacy.Detail);

        var unknown = HealthRules.CheckRebar(new SystemSnapshot { GpuLink = link })!;
        Assert.DoesNotContain("CSM", unknown.Detail);

        var on = HealthRules.CheckRebar(new SystemSnapshot { GpuLink = new GpuLinkFacts("RTX", 16384 * MiB, 16303 * MiB) })!;
        Assert.Equal(CheckSeverity.Ok, on.Severity);
        Assert.Contains("16 ГБ", on.Detail);
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", true)]
    [InlineData("NVIDIA GeForce RTX 2070", true)]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", true)]
    [InlineData("NVIDIA GeForce GTX 1080", true)]
    [InlineData("NVIDIA GeForce GT 1030", true)]
    [InlineData("NVIDIA GeForce MX450", true)]
    [InlineData("NVIDIA TITAN RTX", true)]
    [InlineData("Quadro RTX 4000", true)]
    [InlineData("NVIDIA T1000", true)]
    [InlineData("NVIDIA GeForce RTX 3070", false)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", false)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", false)]
    [InlineData("NVIDIA RTX A4000", false)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell", false)]
    [InlineData("RTX", false)]
    [InlineData(null, false)]
    public void Nvidia_cards_without_rebar_are_recognized(string? name, bool expected) =>
        Assert.Equal(expected, HealthRules.NvidiaCardLacksRebar(name));

    [Fact]
    public void Cards_that_cannot_use_rebar_get_no_bios_advice()
    {
        foreach (var name in new[] { "NVIDIA GeForce GTX 1660 SUPER", "NVIDIA GeForce RTX 2070" })
        {
            var link = new GpuLinkFacts(name, 256 * MiB, 8192 * MiB);
            Assert.Null(HealthRules.CheckRebar(new SystemSnapshot { GpuLink = link, Firmware = FirmwareKind.Uefi }));
        }
        var rtx30 = HealthRules.CheckRebar(new SystemSnapshot
        {
            GpuLink = new GpuLinkFacts("NVIDIA GeForce RTX 3070", 256 * MiB, 8192 * MiB), Firmware = FirmwareKind.Uefi,
        })!;
        Assert.Contains("Above 4G Decoding", rtx30.Detail);
        Assert.Contains("только RTX 30 и новее", rtx30.Detail); // unknown names still hear that the card may be the cause
    }

    [Fact]
    public void Lower_generation_under_load_is_never_called_full_speed()
    {
        var gen3 = new GpuLinkFacts("RTX 4070", CurrentGen: 3, CurrentWidth: 16, MaxGen: 4, MaxWidth: 16, UnderLoad: true);
        Assert.True(HealthRules.IsLinkGenLowered(gen3));
        Assert.False(HealthRules.IsLinkDegraded(gen3));
        var r = HealthRules.CheckPcieLink(new SystemSnapshot { GpuLink = gen3 })!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Equal("ЛИНИЯ PCIE ВИДЕОКАРТЫ: 3.0 ВМЕСТО 4.0", r.Title);
        Assert.Contains("PCIe 3.0 ×16", r.Detail);
        Assert.Contains("райзер", r.Detail);
        Assert.DoesNotContain("работает полностью", r.Detail);
        Assert.Null(r.Action);

        // Narrower and slower: the width warning names both.
        var both = HealthRules.CheckPcieLink(new SystemSnapshot { GpuLink = gen3 with { CurrentWidth = 8 } })!;
        Assert.Equal(CheckSeverity.Warning, both.Severity);
        Assert.Contains("PCIe 3.0 ×8", both.Detail);
        Assert.Contains("4.0 ×16", both.Detail);

        // At idle a lower generation is power saving; the full-speed claim needs the generation sampled under load.
        Assert.False(HealthRules.IsLinkGenLowered(gen3 with { UnderLoad = false }));
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckPcieLink(new SystemSnapshot { GpuLink = gen3 with { UnderLoad = false } })!.Severity);
        Assert.Contains("работает полностью", HealthRules.CheckPcieLink(new SystemSnapshot { GpuLink = gen3 with { CurrentGen = 4 } })!.Detail);
        var noGen = HealthRules.CheckPcieLink(new SystemSnapshot { GpuLink = gen3 with { CurrentGen = null } })!;
        Assert.DoesNotContain("работает полностью", noGen.Detail);
        Assert.Contains("на полную ширину", noGen.Detail);
    }

    [Fact]
    public void Narrow_link_warns_only_under_load()
    {
        var loaded = new GpuLinkFacts("RTX 5070 Ti", CurrentGen: 5, CurrentWidth: 8, MaxGen: 5, MaxWidth: 16, UnderLoad: true);
        Assert.True(HealthRules.IsLinkDegraded(loaded));
        var r = HealthRules.CheckPcieLink(new SystemSnapshot { GpuLink = loaded })!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("×8", r.Detail);
        Assert.Contains("ниже возможностей карты", r.Detail);
        Assert.DoesNotContain("неправильн", r.Detail);
        Assert.Null(r.Action);

        // At idle the driver slows the link down: never a warning.
        var idle = loaded with { UnderLoad = false, CurrentGen = 1 };
        Assert.False(HealthRules.IsLinkDegraded(idle));
        var ok = HealthRules.CheckPcieLink(new SystemSnapshot { GpuLink = idle })!;
        Assert.Equal(CheckSeverity.Ok, ok.Severity);
        Assert.Equal("ЛИНИЯ PCIE ВИДЕОКАРТЫ: 5.0 ×16", ok.Title);
    }

    [Fact]
    public void Board_limited_generation_and_narrow_slots_are_explained_not_flagged()
    {
        var r = HealthRules.CheckPcieLink(new SystemSnapshot
        {
            GpuLink = new GpuLinkFacts("RTX 5060 Ti", CurrentGen: 4, CurrentWidth: 8, MaxGen: 4, MaxWidth: 8, GpuMaxGen: 5, UnderLoad: true),
        })!;
        Assert.Equal(CheckSeverity.Ok, r.Severity);
        Assert.Contains("Сама карта поддерживает PCIe 5.0", r.Detail);
        Assert.Contains("Если карта рассчитана на ×16", r.Detail); // natively ×8 cards exist: only a hint
        Assert.Null(HealthRules.CheckPcieLink(new SystemSnapshot { GpuLink = new GpuLinkFacts("RTX") }));
    }
}

public sealed class DashboardReadinessEvaluateTests
{
    [Fact]
    public void A_healthy_gaming_pc_passes_every_new_check()
    {
        const string dKey = "0x00000000_0x0000e1ae";
        var rtx = new GpuInfo("NVIDIA GeForce RTX 5070 Ti", GpuVendor.Nvidia, "32.0.15.8180", DateTime.Today, 16UL << 30, "PCI\\VEN_10DE");
        var now = new DateTime(2026, 10, 27, 12, 0, 0);
        var s = new SystemSnapshot
        {
            Now = now,
            Elevated = true,
            OsBuild = 26100,
            Gpus = [rtx],
            Adapters = [new GpuAdapter(dKey, rtx.Name, 4)],
            Displays = [new DisplayInfo("X27U", 2560, 1440, 240, 240, 239.97, dKey, false, true)],
            Firmware = FirmwareKind.Uefi,
            SecureBootOn = true,
            Tpm = TpmState.V20,
            InstalledGames = ["Battlefield 6"],
            Disks = [new DiskHealth("Samsung SSD 990 PRO 2TB", "NVMe", "SSD", DiskHealthStatus.Healthy, 3, 41, 82, 0, 0)],
            Stability = new StabilityLog(now.AddDays(-30), now.AddDays(-90), []),
            Vbs = VbsState.MemoryIntegrity,
            GpuLink = new GpuLinkFacts(rtx.Name, 16384UL << 20, 16303UL << 20, 5, 16, 5, 16, 5, true),
        };

        var results = HealthRules.Evaluate(s);
        string[] ids = ["boot-security", "monitor-gpu", "disk-health", "stability", "rebar", "pcie-link"];
        foreach (var id in ids)
            Assert.Equal(CheckSeverity.Ok, Assert.Single(results, r => r.Id == id).Severity);
        Assert.Equal(results.Count, results.Select(r => r.Id).Distinct().Count());
        Assert.All(results, r =>
        {
            Assert.Equal(r.Title.ToUpperInvariant(), r.Title);
            Assert.False(string.IsNullOrWhiteSpace(r.Detail));
        });
        Assert.Equal(100, HealthScore.Calculate(results));
    }

    [Fact]
    public void New_checks_never_offer_a_button_that_changes_the_system()
    {
        var now = new DateTime(2026, 10, 27);
        var bad = new SystemSnapshot
        {
            Now = now,
            Firmware = FirmwareKind.LegacyBios,
            Tpm = TpmState.NotFound,
            OsBuild = 26100,
            InstalledGames = ["VALORANT"],
            Disks = [new DiskHealth("WD Blue", "SATA", "HDD", DiskHealthStatus.Unhealthy)],
            Stability = new StabilityLog(now.AddDays(-30), null,
            [
                new StabilityEvent(now.AddDays(-1), StabilityEventKind.KernelPower41, 41, 1, 0x1A),
                new StabilityEvent(now.AddDays(-2), StabilityEventKind.GpuReset, 4101, 3),
                new StabilityEvent(now.AddDays(-2), StabilityEventKind.Whea, 18, 2),
            ]),
            Vbs = VbsState.Off,
            DriverBlocklistEnforced = false,
            GpuLink = new GpuLinkFacts("RTX", 256UL << 20, 8192UL << 20, 4, 4, 4, 16, 4, true),
        };
        var results = HealthRules.Evaluate(bad).Where(r => r.Id is "boot-security" or "disk-health" or "stability" or "rebar" or "pcie-link" or "vbs").ToList();
        Assert.Equal(6, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotEqual(CheckSeverity.Ok, r.Severity);
            if (r.Action is { } a)
            {
                Assert.False(a.RequiresAdmin);
                Assert.True(a.Kind == CheckActionKind.Navigate || (a.Kind == CheckActionKind.OpenUri && a.Target!.StartsWith("ms-settings:", StringComparison.Ordinal)));
            }
        });
    }
}

public sealed class DashboardProbeTests
{
    [Fact]
    public async Task A_late_result_is_cached_so_the_slow_source_is_not_read_again()
    {
        var name = "test-late-" + Guid.NewGuid().ToString("N");
        var release = new TaskCompletionSource();
        var reads = 0;
        async Task<string?> Slow()
        {
            Interlocked.Increment(ref reads);
            await release.Task;
            return "late";
        }

        var first = await Probe.RunAsync<string?>(name, Slow, TimeSpan.FromMilliseconds(50), null, TimeSpan.FromMinutes(30));
        Assert.Null(first);                                   // over the limit: unknown for now
        release.SetResult();
        for (var i = 0; i < 200 && await Probe.RunAsync<string?>(name, Slow, TimeSpan.FromSeconds(5), null, TimeSpan.FromMinutes(30)) is null; i++)
            await Task.Delay(10);

        Assert.Equal("late", await Probe.RunAsync<string?>(name, Slow, TimeSpan.FromSeconds(5), null, TimeSpan.FromMinutes(30)));
        Assert.Equal(1, Volatile.Read(ref reads));            // answered from the cache, never read a second time
    }

    [Fact]
    public async Task Fallback_and_failed_reads_are_never_cached()
    {
        var name = "test-fail-" + Guid.NewGuid().ToString("N");
        var reads = 0;
        Task<string?> Failing() { Interlocked.Increment(ref reads); throw new InvalidOperationException("no"); }
        Assert.Null(await Probe.RunAsync<string?>(name, Failing, TimeSpan.FromSeconds(5), null, TimeSpan.FromMinutes(30)));
        Assert.Null(await Probe.RunAsync<string?>(name, Failing, TimeSpan.FromSeconds(5), null, TimeSpan.FromMinutes(30)));
        Assert.Equal(2, reads);
    }
}

/// <summary>«Версия Windows закреплена»: the dashboard warns before a pinned version runs out of updates (HealthRules.CheckUpdatePin).</summary>
public sealed class DashboardUpdatePinTests
{
    private static SystemSnapshot Pinned(string? version, int y, int m, int d) =>
        new() { UpdatePinnedTo = version, OsDisplayVersion = version ?? "", Now = new DateTime(y, m, d, 12, 0, 0) };

    [Fact]
    public void Not_pinned_means_no_check()
    {
        Assert.Null(HealthRules.CheckUpdatePin(new SystemSnapshot()));
        Assert.Null(HealthRules.CheckUpdatePin(Pinned("", 2026, 11, 1)));
        Assert.DoesNotContain(HealthRules.Evaluate(new SystemSnapshot { OsDisplayVersion = "25H2" }), r => r.Id == "update-pin");
    }

    [Fact]
    public void Far_from_end_of_servicing_is_information_only()
    {
        var r = HealthRules.CheckUpdatePin(Pinned("25H2", 2026, 11, 1))!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Equal(0, r.Weight);
        Assert.Equal("ВЕРСИЯ WINDOWS ЗАКРЕПЛЕНА", r.Title);
        Assert.Contains("12.10.2027", r.Detail);
        Assert.Equal(CheckActionKind.FocusTweak, r.Action!.Kind);
        Assert.Equal("wu-target-version", r.Action.Target);
        Assert.Equal(HealthRules.TweakUpdatePin, r.Action.Target);
        Assert.NotNull(Lycoris.Tweaks.TweakCatalog.Find(r.Action.Target!));
    }

    [Theory]
    [InlineData(2027, 7, 14)]  // exactly 90 days before 12.10.2027
    [InlineData(2027, 10, 12)] // the day itself
    [InlineData(2028, 1, 1)]   // past: the 60-day gap without security updates may already be running
    public void Within_90_days_or_past_is_a_warning(int y, int m, int d)
    {
        var r = HealthRules.CheckUpdatePin(Pinned("25H2", y, m, d))!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Equal(10, r.Weight);
        Assert.Equal("ЗАКРЕПЛЁННАЯ ВЕРСИЯ WINDOWS СКОРО БЕЗ ОБНОВЛЕНИЙ", r.Title);
        Assert.Contains("25H2", r.Detail);
        Assert.Contains("12.10.2027", r.Detail);
        Assert.Contains("откат пункта", r.Detail);
        Assert.Equal(CheckActionKind.FocusTweak, r.Action!.Kind); // the dashboard never changes the policy itself
        Assert.False(r.Action.RequiresAdmin);
    }

    [Fact]
    public void Ninety_one_days_before_is_still_information() =>
        Assert.Equal(CheckSeverity.Info, HealthRules.CheckUpdatePin(Pinned("25H2", 2027, 7, 13))!.Severity);

    [Theory]
    [InlineData("25H2", "26H2", 2028, 1, 1)]  // Microsoft moved the PC on after 25H2's end of servicing: no «without updates» warning
    [InlineData("24H2", "25H2", 2026, 11, 1)] // the user upgraded; the stale pin would otherwise warn forever
    [InlineData("22H2", "25H2", 2026, 11, 1)] // unknown pinned version, known installed one
    public void Pin_to_another_version_than_the_installed_one_is_stale_information(string pinnedTo, string installed, int y, int m, int d)
    {
        var r = HealthRules.CheckUpdatePin(new SystemSnapshot { UpdatePinnedTo = pinnedTo, OsDisplayVersion = installed, Now = new DateTime(y, m, d, 12, 0, 0) })!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Equal(0, r.Weight);
        Assert.Equal("ЗАКРЕПЛЕНИЕ ВЕРСИИ WINDOWS УСТАРЕЛО", r.Title);
        Assert.Contains(pinnedTo, r.Detail);
        Assert.Contains(installed, r.Detail);
        Assert.Contains("удаление политики", r.Detail); // a pin set by an administrator has no Lycoris undo
        Assert.Equal(CheckActionKind.FocusTweak, r.Action!.Kind);
        Assert.Equal(HealthRules.TweakUpdatePin, r.Action.Target);
    }

    [Fact]
    public void Unknown_installed_version_does_not_make_a_pin_stale()
    {
        var r = HealthRules.CheckUpdatePin(new SystemSnapshot { UpdatePinnedTo = "25H2", OsDisplayVersion = "", Now = new DateTime(2026, 11, 1) })!;
        Assert.Equal("ВЕРСИЯ WINDOWS ЗАКРЕПЛЕНА", r.Title);
        Assert.Equal(CheckSeverity.Info, r.Severity);
    }

    [Fact]
    public void Already_expired_24H2_pin_warns_today()
    {
        var r = HealthRules.CheckUpdatePin(Pinned("24H2", 2026, 11, 1))!;
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("13.10.2026", r.Detail);
    }

    [Fact]
    public void Unknown_version_is_reported_without_a_date()
    {
        var r = HealthRules.CheckUpdatePin(Pinned("22H2", 2026, 11, 1))!;
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Equal(0, r.Weight);
        Assert.Contains("дата поддержки неизвестна", r.Detail);
        Assert.Equal(CheckActionKind.FocusTweak, r.Action!.Kind);
    }

    [Fact]
    public void Evaluate_includes_the_pin_check_after_pending_reboot()
    {
        var results = HealthRules.Evaluate(Pinned("25H2", 2027, 9, 1));
        var ids = results.Select(r => r.Id).ToList();
        Assert.Contains("update-pin", ids);
        Assert.True(ids.IndexOf("pending-reboot") < ids.IndexOf("update-pin"));
        Assert.True(HealthScore.Calculate(results) < 100);
    }
}
