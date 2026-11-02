using System.Diagnostics;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Devices.Input;
using Xunit.Abstractions;

namespace Monolith.Tests.Hardware;

/// <summary>Case 1: the per-user SystemParametersInfo parameters, through ChangeSession and the journal undo, on the real session.</summary>
public sealed class HwSpiTests(ITestOutputHelper output) : HwBase(output)
{
    // Parameters whose change is harmless to hold for a moment. swap-buttons is deliberately absent (can lock the owner out).
    private static readonly string[] Ids =
        ["mouse-speed", "wheel-lines", "wheel-chars", "wheel-routing", "double-click", "mouse-trails", "mouse-sonar", "mouse-vanish", "keyboard-delay", "keyboard-speed"];

    private static int PickTarget(SpiEntry e, int original)
    {
        foreach (var t in new[] { original + 1, original - 1, original + 2, original - 2 })
            if (e.IsAllowedTarget(t) && e.IsValidOriginal(t) && t != original) return t;
        throw new InvalidOperationException("no target for " + e.Id);
    }

    [HwFact]
    public async Task Each_parameter_changes_reads_back_and_undoes_to_the_exact_original()
    {
        var writer = DeviceWriters.Spi ?? throw new InvalidOperationException("no SPI writer");
        Assert.Null(Elevation.InteractiveUserProblem);
        var failures = new List<string>();

        foreach (var id in Ids)
        {
            var entry = SpiCatalog.Find(id)!;
            var original = writer.Get(entry);
            Assert.NotNull(original);
            var regBefore = entry.Persisted is { } p0 ? RegString(p0.Path, p0.Name) : null;
            var target = PickTarget(entry, original!.Value);
            var clock = Stopwatch.StartNew();
            try
            {
                var s = await Begin("hw spi " + id);
                Assert.True(s.SetUserParameter(id, target, DeviceTargets.Source, "hw " + id), string.Join("; ", s.Errors));
                var live = writer.Get(entry);
                var regDuring = entry.Persisted is { } p1 ? RegString(p1.Path, p1.Name) : null;
                Hold(TimeSpan.FromMilliseconds(400));
                var rec = Journal.ActiveRecords.Single(r => r.Target == DeviceTargets.Spi(id));
                var held = clock.ElapsedMilliseconds;
                var undo = await Journal.RevertAsync([rec]);
                var after = writer.Get(entry);
                var regAfter = entry.Persisted is { } p2 ? RegString(p2.Path, p2.Name) : null;
                Say($"SPI {id,-15} orig={original} target={target} live={live} reg {regBefore} -> {regDuring} -> {regAfter}; after undo={after}; undo ok={undo.Success}; held {held} ms");
                if (live != target) failures.Add($"{id}: live {live} != target {target}");
                if (!undo.Success) failures.Add($"{id}: undo failed: {string.Join("; ", undo.Errors)}");
                if (after != original) failures.Add($"{id}: after undo {after} != original {original}");
                if (regAfter != regBefore) failures.Add($"{id}: registry after undo {regAfter} != before {regBefore}");
            }
            finally
            {
                // Whatever happened: the original, live and persisted.
                if (writer.Get(entry) != original) writer.Set(entry, original!.Value);
                if (entry.Persisted is { } p && RegString(p.Path, p.Name) != regBefore)
                    failures.Add($"{id}: registry still differs after the final restore ({RegString(p.Path, p.Name)} vs {regBefore})");
                Assert.Equal(original, writer.Get(entry));
            }
        }
        Assert.Empty(failures);
    }

    [HwFact]
    public void Keyboard_mapping_matches_the_real_values()
    {
        var writer = DeviceWriters.Spi!;
        var delay = writer.Get(SpiCatalog.Find("keyboard-delay")!)!.Value;
        var speed = writer.Get(SpiCatalog.Find("keyboard-speed")!)!.Value;
        Say($"Keyboard SPI delay={delay} -> {InputAdvice.KeyboardDelayText(delay)}; speed={speed} -> {InputAdvice.RepeatSpeedText(speed)} / {InputAdvice.RepeatRateText(speed)}");
        // Windows documents 0..3 as 250/500/750/1000 ms and 0..31 as about 2.5..30 repeats per second.
        Assert.Contains((delay + 1) * 250 + "", InputAdvice.KeyboardDelayText(delay));
        Assert.InRange(InputAdvice.RepeatRate(0), 2.4, 2.6);
        Assert.InRange(InputAdvice.RepeatRate(31), 29.9, 30.1);
    }
}
