using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Restore;

namespace Monolith.Tests;

/// <summary>Pure parts of the undo center's live read (drift detection, research services.md §3.5).</summary>
public sealed class RestoreLiveStateTests
{
    [Theory]
    [InlineData(ChangeKind.Service, "Вручную", "Вручную", false)]
    [InlineData(ChangeKind.Service, "Вручную", "Отключена", true)]
    [InlineData(ChangeKind.ScheduledTask, "Отключена", "Включена", true)]
    [InlineData(ChangeKind.Registry, "DWord: 0", "DWord: 0", false)]
    [InlineData(ChangeKind.Registry, "DWord: -1", "DWord: 4294967295", false)]
    [InlineData(ChangeKind.Registry, "DWord: 0", "DWord: 1", true)]
    [InlineData(ChangeKind.Registry, "удалено", "удалено", false)]
    [InlineData(ChangeKind.Registry, "DWord: 0", "удалено", true)]
    [InlineData(ChangeKind.PowerScheme, "Высокая производительность", "Сбалансированная", true)]
    public void Drift_is_detected(ChangeKind kind, string applied, string live, bool drifted) =>
        Assert.Equal(drifted, LiveState.IsDrift(kind, applied, live));

    [Theory]
    [InlineData("Вручную", LiveState.Unknown)]
    [InlineData("", "Вручную")]
    public void Unknown_values_are_not_drift(string applied, string live) =>
        Assert.Null(LiveState.IsDrift(ChangeKind.Service, applied, live));

    [Fact]
    public void Revert_check_compares_the_restored_registry_value_exactly()
    {
        RegistryValueSnapshot Snap(bool existed, RegistryValueKind kind = RegistryValueKind.DWord, string? data = null) =>
            new() { Hive = RegHive.HKCU, Path = "P", Name = "N", ValueExisted = existed, Kind = kind, Data = data };

        Assert.True(LiveState.CompareRegistry(Snap(false), Snap(false)).Matches);
        Assert.True(LiveState.CompareRegistry(Snap(true, data: "7"), Snap(true, data: "7")).Matches);
        var changed = LiveState.CompareRegistry(Snap(true, data: "7"), Snap(true, data: "1"));
        Assert.False(changed.Matches);
        Assert.Equal("сейчас DWord: 1, ожидалось DWord: 7", changed.MismatchText);
        Assert.False(LiveState.CompareRegistry(Snap(false), Snap(true, data: "0")).Matches);
        Assert.Equal("сейчас DWord: 0, ожидалось значения нет", LiveState.CompareRegistry(Snap(false), Snap(true, data: "0")).MismatchText);
        Assert.False(LiveState.CompareRegistry(Snap(true, RegistryValueKind.String, "7"), Snap(true, RegistryValueKind.DWord, "7")).Matches);
    }

    [Fact]
    public void Revert_check_compares_the_service_start_and_its_delayed_flag()
    {
        var original = new ServiceStartSnapshot("Svc", System.ServiceProcess.ServiceStartMode.Manual, false, RawDelayed: true);
        Assert.True(LiveState.CompareService(original, original).Matches);
        var flagLost = LiveState.CompareService(original, original with { RawDelayed = false });
        Assert.False(flagLost.Matches);
        Assert.Contains("флаг отложенного запуска 0", flagLost.Live);
        Assert.False(LiveState.CompareService(original, original with { StartMode = System.ServiceProcess.ServiceStartMode.Disabled }).Matches);
        // Records from older builds did not capture the flag: only the start type is compared.
        var legacy = original with { RawDelayed = null };
        Assert.True(LiveState.CompareService(legacy, original with { RawDelayed = false }).Matches);
    }

    [Fact]
    public void Registry_snapshot_uses_change_session_wording()
    {
        Assert.Equal("удалено", LiveState.FormatRegistry(new RegistryValueSnapshot { ValueExisted = false }));
        Assert.Equal("DWord: -1", LiveState.FormatRegistry(new RegistryValueSnapshot
            { ValueExisted = true, Kind = RegistryValueKind.DWord, Data = "4294967295" }));
        Assert.Equal("Binary: 01 FF", LiveState.FormatRegistry(new RegistryValueSnapshot
            { ValueExisted = true, Kind = RegistryValueKind.Binary, Data = Convert.ToBase64String(new byte[] { 1, 255 }) }));
        Assert.Equal("MultiString: a; b", LiveState.FormatRegistry(new RegistryValueSnapshot
            { ValueExisted = true, Kind = RegistryValueKind.MultiString, Data = "[\"a\",\"b\"]" }));
    }
}
