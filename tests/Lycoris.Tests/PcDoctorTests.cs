using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Modules.Dashboard.Doctor;
using Xunit;

namespace Lycoris.Tests;

/// <summary>«Доктор ПК» (roadmap item 2): plain-language causes from a measurement; read-only, every button is an existing command.</summary>
public class PcDoctorTests
{
    private const ulong Gb = 1024UL * 1024 * 1024;

    private static SystemSnapshot Healthy() => new()
    {
        SystemDriveTotal = 500 * Gb, SystemDriveFree = 200 * Gb, Uptime = TimeSpan.FromHours(5),
        PowerPlan = PowerPlanKind.Balanced, OnAcPower = true, StartupCount = 4,
    };

    private static DoctorSample Calm(double cpu = 10, double ram = 40, double disk = 5, double? gpuTemp = 55,
        IReadOnlyList<ProcessUse>? cpuTop = null, IReadOnlyList<ProcessUse>? memTop = null) =>
        new(PcDoctor.Window, cpu, ram, disk, gpuTemp, cpuTop ?? [], memTop ?? []);

    private static IReadOnlyList<string> Ids(DoctorSample m, SystemSnapshot s) => PcDoctor.Diagnose(m, s).Select(r => r.Id).ToList();

    [Fact]
    public void Calm_pc_says_no_obvious_cause()
    {
        var r = PcDoctor.Diagnose(Calm(), Healthy());
        var only = Assert.Single(r);
        Assert.Equal("doctor-none", only.Id);
        Assert.Equal(CheckSeverity.Ok, only.Severity);
    }

    [Fact]
    public void Busy_cpu_names_the_heaviest_programs_and_opens_task_manager()
    {
        var top = new List<ProcessUse> { new("HandBrake", 62, 0), new("chrome", 9, 0), new("Discord", 1, 0) };
        var r = PcDoctor.Diagnose(Calm(cpu: 91, cpuTop: top), Healthy()).Single(x => x.Id == "doctor-cpu");
        Assert.Equal(CheckSeverity.Warning, r.Severity);
        Assert.Contains("HandBrake — 62 %", r.Detail);
        Assert.Contains("chrome", r.Detail);
        Assert.DoesNotContain("Discord", r.Detail); // below 3 % is noise
        Assert.EndsWith("Taskmgr.exe", r.Action!.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CheckActionKind.OpenTaskManager, r.Action.Kind);
    }

    [Fact]
    public void Medium_cpu_is_a_tip_and_low_cpu_is_silent()
    {
        Assert.DoesNotContain("doctor-cpu", Ids(Calm(cpu: 40, cpuTop: [new("a", 30, 0)]), Healthy()));
        var r = PcDoctor.Diagnose(Calm(cpu: 60, cpuTop: [new("game", 40, 0)]), Healthy()).Single(x => x.Id == "doctor-cpu");
        Assert.Equal(CheckSeverity.Info, r.Severity);
    }

    [Fact]
    public void Full_memory_lists_the_biggest_programs()
    {
        var r = PcDoctor.Diagnose(Calm(ram: 93, memTop: [new("chrome", 0, (long)(3.5 * Gb))]), Healthy()).Single(x => x.Id == "doctor-ram");
        Assert.Contains("chrome", r.Detail);
        Assert.Equal(CheckSeverity.Warning, r.Severity);
    }

    [Fact]
    public void Busy_disk_low_space_restart_power_startup_and_heat_each_have_their_row()
    {
        var s = new SystemSnapshot
        {
            SystemDriveTotal = 256 * Gb, SystemDriveFree = 9 * Gb, Uptime = TimeSpan.FromDays(12), PowerPlan = PowerPlanKind.PowerSaver,
            OnAcPower = true, StartupCount = HealthRules.StartupLimit + 5,
        };
        var ids = Ids(Calm(disk: 97, gpuTemp: 91), s);
        foreach (var id in new[] { "doctor-disk", "doctor-space", "doctor-restart", "doctor-power", "doctor-startup", "doctor-gpu-hot" })
            Assert.Contains(id, ids);
        Assert.DoesNotContain("doctor-none", ids);
    }

    [Fact]
    public void Every_button_is_an_existing_safe_command()
    {
        var s = new SystemSnapshot
        {
            SystemDriveTotal = 256 * Gb, SystemDriveFree = 1 * Gb, PendingReboot = ["Windows Update"], PowerPlan = PowerPlanKind.PowerSaver,
            OnAcPower = true, StartupCount = 30,
        };
        var rows = PcDoctor.Diagnose(Calm(cpu: 99, ram: 99, disk: 99, gpuTemp: 99, cpuTop: [new("x", 90, 0)], memTop: [new("x", 0, 1)]), s);
        Assert.All(rows, r => Assert.False(r.Action?.RequiresAdmin ?? false)); // the doctor never applies a tweak by itself
        Assert.Contains(rows, r => r.Action?.Kind == CheckActionKind.Restart);
        Assert.Contains(rows, r => r.Action is { Kind: CheckActionKind.FocusTweak, Target: "power-plan-balanced" });
        Assert.Contains(rows, r => r.Action is { Kind: CheckActionKind.Navigate, Target: "cleaner" });
        Assert.Equal(CheckSeverity.Warning, rows[0].Severity); // most serious first
        Assert.NotNull(Lycoris.Tweaks.TweakCatalog.Find("power-plan-balanced"));
    }

    [Fact]
    public void Too_few_samples_give_no_diagnosis_only_repeat()
    {
        var s = new SystemSnapshot { SystemDriveTotal = 256 * Gb, SystemDriveFree = 1 * Gb };
        var only = Assert.Single(PcDoctor.Diagnose(Calm() with { Samples = 1 }, s));
        Assert.Equal("doctor-interrupted", only.Id); // not «nothing is overloaded», not a half diagnosis
    }

    [Fact]
    public void Busy_cpu_without_a_visible_culprit_is_still_reported_and_says_why()
    {
        var r = PcDoctor.Diagnose(Calm(cpu: 70, cpuTop: [new("a", 8, 0)]), Healthy()).Single(x => x.Id == "doctor-cpu");
        Assert.Equal(CheckSeverity.Info, r.Severity);
        Assert.Contains("службы Windows", r.Detail);
        Assert.DoesNotContain("doctor-none", Ids(Calm(cpu: 70), Healthy()));
    }

    [Fact]
    public void Energy_saver_opens_settings_and_power_saver_plan_uses_the_panel_button()
    {
        var saver = PcDoctor.Diagnose(Calm(), Healthy() with { EnergySaverOn = true }).Single(x => x.Id == "doctor-power");
        Assert.Equal(CheckActionKind.OpenUri, saver.Action!.Kind);
        Assert.Equal(HealthRules.PowerSettingsUri, saver.Action.Target);

        var plan = Healthy() with { PowerPlan = PowerPlanKind.PowerSaver };
        var r = PcDoctor.Diagnose(Calm(), plan).Single(x => x.Id == "doctor-power");
        Assert.Equal(HealthRules.CheckPowerPlan(plan)!.Action, r.Action); // X3D-aware choice lives in one place
    }

    [Fact]
    public void Task_manager_button_has_its_own_kind_and_full_path()
    {
        var r = PcDoctor.Diagnose(Calm(cpu: 95, cpuTop: [new("x", 90, 0)]), Healthy()).Single(x => x.Id == "doctor-cpu");
        Assert.Equal(CheckActionKind.OpenTaskManager, r.Action!.Kind);
        Assert.True(System.IO.Path.IsPathRooted(r.Action.Target));
    }

    [Fact]
    public void Focus_argument_is_not_carried_into_the_elevated_copy()
    {
        var args = Lycoris.Core.Elevation.RelaunchArguments(["--page", "dashboard", "--focus", "doctor"], null, 42);
        Assert.DoesNotContain("--focus", args);
        Assert.DoesNotContain("doctor", args);
    }

    [Fact]
    public void Power_saving_on_battery_is_not_blamed()
    {
        var s = Healthy() with { PowerPlan = PowerPlanKind.PowerSaver, OnAcPower = false };
        Assert.DoesNotContain("doctor-power", Ids(Calm(), s));
    }

    [Fact]
    public void Group_sums_processes_of_one_program_and_scales_to_all_cores()
    {
        var items = new List<(string, TimeSpan, long)>
        {
            ("chrome", TimeSpan.FromSeconds(4), 100), ("Chrome", TimeSpan.FromSeconds(4), 50),
        };
        var g = PcDoctor.Group(items, TimeSpan.FromSeconds(10), cores: 8);
        var chrome = Assert.Single(g);
        Assert.Equal(10, chrome.CpuPercent, 3); // 8 s of 80 core-seconds
        Assert.Equal(150, chrome.MemoryBytes);
    }

    /// <summary>A program chooses its own name: one disguised as «LycorisHelper» or «Idle» must not hide from the culprit list.</summary>
    [Fact]
    public void Programs_are_never_hidden_by_their_name()
    {
        var items = new List<(string, TimeSpan, long)>
        {
            ("LycorisHelper", TimeSpan.FromSeconds(40), 0), ("Idle", TimeSpan.FromSeconds(20), 0),
        };
        var names = PcDoctor.Group(items, TimeSpan.FromSeconds(10), cores: 8).Select(p => p.Name).ToList();
        Assert.Contains("LycorisHelper", names);
        Assert.Contains("Idle", names);
    }

    [Fact]
    public void English_texts_exist()
    {
        using var _ = Lycoris.Core.Localization.Loc.UseLanguage("en");
        var s = new SystemSnapshot { SystemDriveTotal = 256 * Gb, SystemDriveFree = 1 * Gb, Uptime = TimeSpan.FromDays(9), StartupCount = 40,
            PowerPlan = PowerPlanKind.PowerSaver, OnAcPower = true };
        var rows = PcDoctor.Diagnose(Calm(cpu: 95, ram: 95, disk: 95, gpuTemp: 95, cpuTop: [new("x", 80, 0)], memTop: [new("x", 0, 1)]), s)
            .Concat(PcDoctor.Diagnose(Calm(), Healthy()));
        foreach (var r in rows)
        {
            Assert.DoesNotMatch("[А-Яа-яЁё]", r.Title);
            Assert.DoesNotMatch("[А-Яа-яЁё]", r.Detail);
            if (r.Action is { } a) Assert.DoesNotMatch("[А-Яа-яЁё]", a.Label);
        }
    }
}
