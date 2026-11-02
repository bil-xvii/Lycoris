using System.ServiceProcess;
using Monolith.Core;
using Monolith.Modules.Restore;

namespace Monolith.Tests;

/// <summary>Pure parts of drift detection: which active records changed outside Lycoris and how they are re-applied.</summary>
public sealed class DriftDetectorTests
{
    private static ChangeRecord Rec(ChangeKind kind, string source, string newValue, string target = "x", bool reverted = false) =>
        new() { Kind = kind, Source = source, NewValue = newValue, Target = target, Title = "T", Reverted = reverted };

    private static IReadOnlyList<DriftItem> Detect(IEnumerable<ChangeRecord> records, Func<ChangeRecord, LiveValue> read,
        IReadOnlyCollection<string>? ignored = null) =>
        DriftDetector.Detect(records, read, id => id == "known", ignored);

    [Fact]
    public void Only_active_comparable_drifted_records_are_reported()
    {
        var drifted = Rec(ChangeKind.Service, "services", "Отключена", "svc:A");
        var same = Rec(ChangeKind.Service, "services", "Отключена", "svc:B");
        var reverted = Rec(ChangeKind.Service, "services", "Отключена", "svc:C", reverted: true);
        var command = Rec(ChangeKind.Command, "tweak:known", "on");
        var gone = Rec(ChangeKind.ScheduledTask, "services-tasks", "Отключена", "task:\\X");
        var boost = Rec(ChangeKind.PowerScheme, "gameboost", "Высокая производительность");

        var live = new Dictionary<ChangeRecord, LiveValue>
        {
            [drifted] = new("Вручную", true),
            [same] = new("Отключена", false),
            [reverted] = new("Вручную", true),
            [command] = new("—", true),
            [gone] = new("задача не найдена", null),
            [boost] = new("Сбалансированная", true),
        };
        var result = Detect(live.Keys, r => live[r]);

        var d = Assert.Single(result);
        Assert.Same(drifted, d.Record);
        Assert.Equal(DriftFix.Service, d.Fix);
        Assert.Equal("services", d.OwnerPage);
        Assert.Equal("Lycoris установил: Отключена · сейчас: Вручную", DriftDetector.RowText(d));
    }

    [Fact]
    public void Accepted_drift_is_hidden_until_the_value_changes_again()
    {
        var r = Rec(ChangeKind.Service, "services", "Отключена");
        var ignored = new[] { DriftDetector.IgnoreKey(r, "Вручную") };
        Assert.Empty(Detect([r], _ => new("Вручную", true), ignored));
        Assert.Single(Detect([r], _ => new("Автоматически", true), ignored));
    }

    [Fact]
    public void Prune_keeps_only_keys_of_active_records()
    {
        var active = Rec(ChangeKind.Service, "services", "Отключена");
        var reverted = Rec(ChangeKind.Service, "services", "Отключена", reverted: true);
        var keys = new[] { DriftDetector.IgnoreKey(active, "a"), DriftDetector.IgnoreKey(reverted, "b"), "garbage" };
        Assert.Equal([DriftDetector.IgnoreKey(active, "a")], DriftDetector.PruneIgnored(keys, [active, reverted]));
    }

    [Theory]
    [InlineData("tweak:known", ChangeKind.Registry, "DWord: 0", DriftFix.Tweak)]
    [InlineData("tweak:missing", ChangeKind.Registry, "DWord: 0", DriftFix.None)]
    [InlineData("services", ChangeKind.Service, "Автоматически (отложенный запуск)", DriftFix.Service)]
    [InlineData("services", ChangeKind.Service, "что-то другое", DriftFix.None)]
    [InlineData("services-tasks", ChangeKind.ScheduledTask, "Отключена", DriftFix.Task)]
    [InlineData("startup-tasks", ChangeKind.ScheduledTask, "Включена", DriftFix.Task)]
    [InlineData("drivers", ChangeKind.Registry, "DWord: 1", DriftFix.None)]
    public void Fix_is_classified_by_source(string source, ChangeKind kind, string newValue, DriftFix expected) =>
        Assert.Equal(expected, DriftDetector.ClassifyFix(Rec(kind, source, newValue), DriftDetector.TweakIdOf(Rec(kind, source, newValue)) == "known"));

    [Fact]
    public void Latest_source_decides_the_owner()
    {
        var r = Rec(ChangeKind.Service, "services", "Отключена");
        r.AlsoSources.Add("tweak:known");
        Assert.Equal("known", DriftDetector.TweakIdOf(r));
        Assert.Equal("optimize", DriftDetector.OwnerPage(DriftDetector.LatestSource(r)));
    }

    [Fact]
    public void Service_start_text_round_trips()
    {
        Assert.True(DriftDetector.TryParseServiceStart("Вручную", out var m, out var d));
        Assert.Equal((ServiceStartMode.Manual, false), (m, d));
        Assert.True(DriftDetector.TryParseServiceStart("Автоматически (отложенный запуск)", out m, out d));
        Assert.Equal((ServiceStartMode.Automatic, true), (m, d));
        Assert.False(DriftDetector.TryParseServiceStart("Загрузка", out _, out _));
    }

    [Theory]
    [InlineData("Binary: 03 00 00 00 11 22 33 44 55 66 77 88", false)]
    [InlineData("Binary: 02 00 00 00 00 00 00 00 00 00 00 00", true)]
    [InlineData("удалено", true)]
    [InlineData("отсутствовало", true)]
    [InlineData("DWord: 0", false)] // packaged startup task: disabled by the app
    [InlineData("DWord: 1", false)] // disabled by the user
    [InlineData("DWord: 2", true)]  // enabled
    [InlineData("DWord: 3", false)] // disabled by policy
    [InlineData("DWord: 4", true)]  // enabled by policy
    [InlineData("DWord: 7", null)]
    [InlineData("—", null)]
    public void Startup_mark_state_is_parsed(string text, bool? enabled) =>
        Assert.Equal(enabled, DriftDetector.StartupEnabledOf(text));

    private static ChangeRecord Packaged(string newValue) => new()
    {
        Kind = ChangeKind.Registry, Source = "startup", Title = "Teams", NewValue = newValue,
        Target = @"reg:HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\MSTeams_8wekyb3d8bbwe\TeamsTfwStartupTask\State",
        Registry = new RegistryValueSnapshot
        {
            Hive = RegHive.HKCU, Name = "State", KeyExisted = true, ValueExisted = true, Kind = Microsoft.Win32.RegistryValueKind.DWord, Data = "2",
            Path = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\MSTeams_8wekyb3d8bbwe\TeamsTfwStartupTask",
        },
    };

    [Fact]
    public void Packaged_startup_task_drift_is_reported_and_reapplied_as_a_dword()
    {
        var r = Packaged("DWord: 1"); // Lycoris turned it off
        Assert.True(DriftDetector.IsPackagedMark(r));

        var d = Assert.Single(Detect([r], _ => new("DWord: 2", true)));   // turned back on in Settings
        Assert.Equal(DriftFix.StartupEntry, d.Fix);
        Assert.Equal("автозапуск отключён", d.Applied);
        Assert.Equal("автозапуск включён", d.Live);

        var policy = Assert.Single(Detect([r], _ => new("DWord: 4", true))); // a policy turned it on: never written
        Assert.Equal(DriftFix.None, policy.Fix);
        Assert.Equal("автозапуск включён политикой", policy.Live);

        Assert.Empty(Detect([r], _ => new("DWord: 0", true)));   // still off (the app's own off state)
        Assert.Empty(Detect([r], _ => new("удалено", true)));    // the app is gone: nothing to compare
    }

    [Fact]
    public void Startup_mark_with_new_timestamp_is_not_drift()
    {
        var r = Rec(ChangeKind.Registry, "startup", "Binary: 03 00 00 00 11 11 11 11 11 11 11 11");
        Assert.Empty(Detect([r], _ => new("Binary: 03 00 00 00 22 22 22 22 22 22 22 22", true)));
        var d = Assert.Single(Detect([r], _ => new("Binary: 02 00 00 00 00 00 00 00 00 00 00 00", true)));
        Assert.Equal("автозапуск отключён", d.Applied);
        Assert.Equal("автозапуск включён", d.Live);
    }

    [Fact]
    public void Notice_names_the_build_change()
    {
        Assert.StartsWith("После обновления Windows (сборка 22631.4317 → 26100.2314) система изменила 3 настройки Lycoris.",
            DriftDetector.NoticeText(3, "22631.4317", "26100.2314"));
        Assert.StartsWith("Windows или другая программа изменила 1 настройку Lycoris.", DriftDetector.NoticeText(1, "26100.1", "26100.1"));
        Assert.StartsWith("Windows или другая программа изменила 5 настроек Lycoris.", DriftDetector.NoticeText(5, "", "26100.1"));
    }

    [Theory]
    [InlineData(26100, 2314, "26100.2314")]
    [InlineData(19045, 0, "19045")]
    [InlineData(0, 5, "")]
    public void Build_text(int build, int ubr, string expected) => Assert.Equal(expected, DriftDetector.BuildText(build, ubr));

    [Fact]
    public void Drifted_filter_keeps_only_drifted_active_records()
    {
        var batch = new ChangeBatch { Title = "B" };
        var a = new ChangeRecord { BatchId = batch.Id, Title = "a" };
        var b = new ChangeRecord { BatchId = batch.Id, Title = "b" };
        var groups = RestoreFormat.Group([batch], [a, b], JournalFilter.Drifted, new HashSet<Guid> { b.Id });
        var g = Assert.Single(groups);
        Assert.Same(b, Assert.Single(g.Records));
        Assert.Empty(RestoreFormat.Group([batch], [a, b], JournalFilter.Drifted, new HashSet<Guid>()));
    }
}
