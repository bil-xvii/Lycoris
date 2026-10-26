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
