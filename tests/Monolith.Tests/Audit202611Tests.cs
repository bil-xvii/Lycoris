using System.ServiceProcess;
using Monolith.Core;
using Monolith.Modules.Restore;

namespace Monolith.Tests;

/// <summary>Regression tests for the defects of the correctness audit (docs/audit-2026-11.md).</summary>
public sealed class Audit202611Tests
{
    [Fact]
    public void Journal_service_texts_are_Russian_whatever_the_interface_language()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Вручную", ServiceControl.DescribeStartNeutral(ServiceStartMode.Manual, false));
        Assert.Equal("Автоматически (отложенный запуск)", ServiceControl.DescribeStartNeutral(ServiceStartMode.Automatic, true));
        var snapshot = new ServiceStartSnapshot("X", ServiceStartMode.Disabled, false);
        Assert.Equal("Отключена", snapshot.DescribeNeutral());
    }

    [Theory]
    [InlineData("Manual", "Вручную")]
    [InlineData("Вручную", "Вручную")]
    [InlineData(" disabled ", "Отключена")]
    [InlineData("Automatic (delayed start)", "Автоматически (отложенный запуск)")]
    [InlineData("что-то своё", "что-то своё")]
    public void Start_type_text_of_either_language_normalizes(string text, string expected)
    {
        Assert.Equal(expected, ServiceControl.NormalizeStart(text));
        using var _ = Loc.UseLanguage("en");
        Assert.Equal(expected, ServiceControl.NormalizeStart(text));
    }

    [Fact]
    public void A_packed_list_of_many_checked_items_survives_the_relaunch_with_admin_rights()
    {
        // «Отключить» 15 autostart entries without admin rights: the list (base64) is far longer than the old 200-char limit.
        var keys = Enumerable.Range(1, 15).Select(i => $@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Entry{i}");
        var packed = Monolith.Modules.Startup.IntentList.Pack(keys)!;
        Assert.True(packed.Length > 200);
        var intent = new Monolith.Shell.AdminIntent("services", "DisableSelectedStartup", packed);
        var parsed = Monolith.Shell.AdminIntent.Parse(intent.ToArgs());
        Assert.NotNull(parsed);
        Assert.Equal(15, Monolith.Modules.Startup.IntentList.Unpack(parsed!.Parameter).Count);
    }

    [Theory]
    [InlineData("UnityCrashHandler64", true)]
    [InlineData("Crashlands", false)]
    [InlineData("Crash Bandicoot", false)]
    public void Only_real_crash_reporters_are_helpers(string name, bool helper) =>
        Assert.Equal(helper, Monolith.Modules.Games.GameProcessFinder.IsHelper(name));

    [Fact]
    public void Unknown_link_speed_does_not_overflow()
    {
        Assert.Equal(long.MaxValue, Monolith.Modules.Devices.Network.NetSystem.ToLongClamped(ulong.MaxValue));
        Assert.Equal(1_000_000_000L, Monolith.Modules.Devices.Network.NetSystem.ToLongClamped(1_000_000_000UL));
        Assert.Equal(5L, Monolith.Modules.Devices.Network.NetSystem.ToLongClamped(5));
    }

    [Fact]
    public void A_reboot_is_not_offered_while_a_task_or_a_game_session_runs()
    {
        var running = new OperationInfo(1, "Установка Steam", OperationClass.Install, OperationState.Running, "apps", null, null, false, DateTimeOffset.Now);
        var probe = new OperationInfo(2, "Пинг", OperationClass.Measure, OperationState.Running, null, null, null, false, DateTimeOffset.Now);
        Assert.Null(Monolith.Modules.Optimize.SessionActions.RebootBlockerText([], false));
        Assert.Null(Monolith.Modules.Optimize.SessionActions.RebootBlockerText([probe], false));
        Assert.Contains("Установка Steam", Monolith.Modules.Optimize.SessionActions.RebootBlockerText([running], false));
        Assert.NotNull(Monolith.Modules.Optimize.SessionActions.RebootBlockerText([], true));
    }

    [Fact]
    public void Only_a_real_no_such_task_answer_counts_as_gone()
    {
        Assert.True(ScheduledTasks.IsNotFound(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070002))));
        Assert.True(ScheduledTasks.IsNotFound(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070003))));
        // Scheduler stopped / access denied / anything else: the task may well exist — never «gone».
        Assert.False(ScheduledTasks.IsNotFound(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070005))));
        Assert.False(ScheduledTasks.IsNotFound(new InvalidOperationException("no scheduler")));
        Assert.False(ScheduledTasks.IsNotFound(new UnauthorizedAccessException()));
    }

    [Fact]
    public void A_record_written_in_English_is_not_drift_and_can_be_reapplied()
    {
        // An English-interface session wrote "Manual"; the live read is the neutral Russian text.
        Assert.False(LiveState.IsDrift(ChangeKind.Service, "Manual", "Вручную"));
        Assert.True(LiveState.IsDrift(ChangeKind.Service, "Manual", "Отключена"));
        Assert.True(DriftDetector.TryParseServiceStart("Manual", out var mode, out var delayed));
        Assert.Equal(ServiceStartMode.Manual, mode);
        Assert.False(delayed);
    }
}
