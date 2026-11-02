using System.Diagnostics;
using System.IO;
using Lycoris.Core;
using Lycoris.Core.Devices;
using Lycoris.Modules.Devices.Displays;
using Xunit.Abstractions;

namespace Lycoris.Tests.Hardware;

/// <summary>Cases 2 and 3: display mode (real DisplaySafeApply + the real --display-guard child) and HDR.</summary>
public sealed class HwDisplayTests : HwBase
{
    public HwDisplayTests(ITestOutputHelper output) : base(output)
    {
        // The guard is the real Lycoris.exe next to the loaded Lycoris.dll (the xUnit host is not Lycoris.exe).
        var exe = Path.Combine(AppContext.BaseDirectory, "Lycoris.exe");
        Assert.True(File.Exists(exe), exe);
        DisplayWriter.GuardExecutableForTests = exe;
    }

    public override void Dispose()
    {
        DisplayWriter.GuardExecutableForTests = null;
        DisplaySafeApply.ConfirmWindow = TimeSpan.FromSeconds(15);
        base.Dispose();
    }

    private static MonitorInfo Primary() =>
        DisplayInventory.Read(Environment.OSVersion.Version.Build).FirstOrDefault(m => m is { IsPrimary: true, Current: not null, GdiName: not null })
        ?? throw new InvalidOperationException("no active primary monitor");

    private static OfferedMode LowerMode(MonitorInfo m)
    {
        var cur = m.Current!;
        var lower = m.Modes.Where(x => x.Width == cur.Width && x.Height == cur.Height && x.Hz < cur.Frequency).OrderByDescending(x => x.Hz).ToList();
        return lower.FirstOrDefault(x => x.Hz == 144) ?? lower.FirstOrDefault(x => x.Hz == 120) ?? lower.First();
    }

    private static DisplayModeSpec TargetFor(MonitorInfo m, OfferedMode o) =>
        new(o.Width, o.Height, o.Hz, 0, 0, 32, m.Current!.Orientation, m.Current.PositionX, m.Current.PositionY);

    private void Screen(string tag, MonitorInfo m)
    {
        var live = DeviceWriters.Display!.ReadCurrentMode(m.MonitorPath);
        Say($"  [{tag}] GDI live {RawMode(m.GdiName, -1)}, saved {RawMode(m.GdiName, -2)}; CCD live {live?.Describe()} (Frequency={live?.Frequency}, exact {live?.ExactHz:0.000}); persisted {DeviceWriters.Display.ReadPersistedMode(m.MonitorPath)?.Describe()}");
    }

    [HwFact]
    public void Enumerate_monitors_and_modes()
    {
        foreach (var m in DisplayInventory.Read(Environment.OSVersion.Version.Build))
        {
            Say($"Monitor {m.Name} primary={m.IsPrimary} gdi={m.GdiName} out={m.OutputName} current={m.Current?.Describe()} exact={m.Current?.ExactHz:0.000}");
            Say($"  EDID: {m.Edid?.Name} preferred={m.PreferredSize} scale={m.ScalePercent}% sdrWhite={m.SdrWhiteNits}");
            Say($"  modes at current size: {string.Join(", ", m.Modes.Where(x => m.Current is { } c && x.Width == c.Width && x.Height == c.Height).Select(x => x.Hz))}");
            Say($"  HDR: {(m.Color is null ? "n/a" : $"supported={m.Color.HdrSupported} enabled={m.Color.HdrEnabled} wcg={m.Color.WcgSupported}/{m.Color.WcgEnabled} policy={m.Color.LimitedByPolicy} bpc={m.Color.Bpc}")}");
            Say($"  DDC brightness={Fmt(m.Brightness)} contrast={Fmt(m.Contrast)}");
        }
        Say($"BlockReason: {DeviceWriters.Display!.BlockReason() ?? "none"}; GameBoost={GameBoostState.IsActive}");
    }

    private static string Fmt(VcpReading? r) => r is { } v ? $"{v.Value} ({v.Min}..{v.Max})" : "—";

    [HwFact]
    public async Task Refresh_switch_keep_then_journal_undo()
    {
        var m = Primary();
        var path = m.MonitorPath;
        var original = m.Current!;
        var target = TargetFor(m, LowerMode(m));
        var rawLive0 = RawMode(m.GdiName, -1);
        var rawSaved0 = RawMode(m.GdiName, -2);
        Say($"DISPLAY keep+undo: original {original.Describe()} exact {original.ExactHz:0.0000} raw live {rawLive0} saved {rawSaved0}; target {target.Describe()}");
        var asked = new List<(string Title, double AtMs)>();
        var clock = Stopwatch.StartNew();
        DeviceConfirm.Countdown = async (req, ct) =>
        {
            asked.Add((req.Title, clock.Elapsed.TotalMilliseconds));
            Screen("during countdown", m);
            await Task.Delay(1500, CancellationToken.None);
            return true;
        };
        var failures = new List<string>();
        try
        {
            var s = await Begin("hw display");
            clock.Restart();
            var outcome = await s.SetDisplayModeAsync(path, m.Name, target, DeviceTargets.Source, "hw display mode");
            var tApply = clock.ElapsedMilliseconds;
            Say($"  apply outcome {outcome.Result} ({outcome.Message}) in {tApply} ms; errors: {string.Join("; ", s.Errors)}");
            Screen("after keep", m);
            Assert.Equal(DisplayApplyResult.Kept, outcome.Result);
            if (!DeviceWriters.Display!.ReadCurrentMode(path)!.SameMode(target)) failures.Add("live mode is not the target after keep");
            if (RawMode(m.GdiName, -2) != $"{target.Width}x{target.Height}@{target.Frequency}") failures.Add("saved mode (ENUM_REGISTRY_SETTINGS) is not the target after keep: " + RawMode(m.GdiName, -2));
            var rec = Journal.ActiveRecords.Single(r => r.Kind == ChangeKind.DisplayMode);
            if (rec.DisplayMode!.PendingConfirm) failures.Add("record still PendingConfirm after keep");

            clock.Restart();
            var undo = await Journal.RevertAsync([rec]);
            Say($"  undo success={undo.Success} errors=[{string.Join("; ", undo.Errors)}] in {clock.ElapsedMilliseconds} ms");
            Screen("after undo", m);
            if (!undo.Success) failures.Add("undo failed: " + string.Join("; ", undo.Errors));
            var back = DeviceWriters.Display.ReadCurrentMode(path)!;
            if (!back.SameMode(original)) failures.Add($"after undo live {back.Describe()} != original {original.Describe()}");
            if (Math.Abs(back.ExactHz - original.ExactHz) > 0.01) failures.Add($"exact refresh after undo {back.ExactHz:0.0000} != original {original.ExactHz:0.0000}");
            if (RawMode(m.GdiName, -1) != rawLive0) failures.Add("GDI live differs after undo: " + RawMode(m.GdiName, -1));
            if (RawMode(m.GdiName, -2) != rawSaved0) failures.Add("GDI saved differs after undo: " + RawMode(m.GdiName, -2));
        }
        finally
        {
            Restore(m, original);
        }
        Say("  countdown prompts: " + string.Join(" | ", asked.Select(a => $"{a.Title} @{a.AtMs:0} ms")));
        Assert.Empty(failures);
    }

    [HwFact]
    public async Task Refresh_switch_declined_reverts_to_the_original()
    {
        var m = Primary();
        var path = m.MonitorPath;
        var original = m.Current!;
        var target = TargetFor(m, LowerMode(m));
        var rawLive0 = RawMode(m.GdiName, -1);
        var rawSaved0 = RawMode(m.GdiName, -2);
        DeviceConfirm.Countdown = async (_, _) =>
        {
            Screen("during countdown", m);
            await Task.Delay(1500, CancellationToken.None);
            return false;
        };
        try
        {
            var s = await Begin("hw display decline");
            var clock = Stopwatch.StartNew();
            var outcome = await s.SetDisplayModeAsync(path, m.Name, target, DeviceTargets.Source, "hw display mode decline");
            Say($"DISPLAY decline: outcome {outcome.Result} ({outcome.Message}) in {clock.ElapsedMilliseconds} ms");
            Screen("after decline", m);
            Assert.Equal(DisplayApplyResult.Reverted, outcome.Result);
            Assert.Empty(Journal.ActiveRecords);
            Assert.True(DeviceWriters.Display!.ReadCurrentMode(path)!.SameMode(original));
            Assert.Equal(rawLive0, RawMode(m.GdiName, -1));
            Assert.Equal(rawSaved0, RawMode(m.GdiName, -2));
        }
        finally
        {
            Restore(m, original);
        }
    }

    /// <summary>Last resort: put the original back (temporary revert + persisted) and verify.</summary>
    private void Restore(MonitorInfo m, DisplayModeSpec original)
    {
        var w = DeviceWriters.Display!;
        if (w.ReadCurrentMode(m.MonitorPath) is { } live && live.SameMode(original)
            && RawMode(m.GdiName, -2) == $"{original.Width}x{original.Height}@{original.Frequency}") return;
        Say("  !! restoring the original display mode manually");
        w.RevertTemporary(m.MonitorPath, original);
        w.Persist(m.MonitorPath, original);
        Assert.True(w.ReadCurrentMode(m.MonitorPath)!.SameMode(original));
    }

    /// <summary>
    /// The guard's safety path: the host is killed while a change is pending. Run ONLY by tools/hw-guard-kill.ps1 style runs
    /// (needs Lycoris_HW_KILL=1 because it ends the xUnit host on purpose): the guard process must put the saved mode back.
    /// </summary>
    [HwFact]
    public async Task Killing_the_host_while_a_change_is_pending_makes_the_guard_restore_the_mode()
    {
        if (Environment.GetEnvironmentVariable("Lycoris_HW_KILL") != "1")
        {
            Say("KILL test skipped: set Lycoris_HW_KILL=1 (it ends the test host on purpose)");
            return;
        }
        var m = Primary();
        var target = TargetFor(m, LowerMode(m));
        Say($"KILL: original {m.Current!.Describe()}, target {target.Describe()}; the host will be killed 1.5 s after the switch");
        var gdi = m.GdiName!;
        var raw0 = RawMode(gdi, -1);
        DeviceConfirm.Countdown = (_, _) => new TaskCompletionSource<bool>().Task; // nobody ever answers
        _ = Task.Run(() =>
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until && RawMode(gdi, -1) == raw0) Thread.Sleep(50);
            Thread.Sleep(1500);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "lycoris-hw-kill.txt"),
                $"killed at {DateTime.UtcNow:O}; mode at kill {RawMode(gdi, -1)}");
            Process.GetCurrentProcess().Kill();
        });
        var s = await Begin("hw display kill");
        await s.SetDisplayModeAsync(m.MonitorPath, m.Name, target, DeviceTargets.Source, "hw display kill");
        Assert.Fail("the host should have been killed");
    }

    // ───────────────────────────── HDR ─────────────────────────────

    [HwFact]
    public async Task Hdr_toggle_through_the_guard_and_undo_restores_the_original()
    {
        var m = Primary();
        var w = DeviceWriters.Display!;
        var original = w.ReadHdrEnabled(m.MonitorPath);
        Say($"HDR: supported={m.Color?.HdrSupported} enabled={original} acm={w.ReadAcmEnabled(m.MonitorPath)} policyLimited={m.Color?.LimitedByPolicy}");
        if (original is null || m.Color is not { HdrSupported: true, LimitedByPolicy: false })
        {
            Say("HDR: not supported or limited — nothing toggled");
            return;
        }
        var wantEnable = !original.Value;
        DeviceConfirm.Countdown = async (_, _) =>
        {
            await Task.Delay(1500, CancellationToken.None);
            return true;
        };
        try
        {
            var s = await Begin("hw hdr");
            var clock = Stopwatch.StartNew();
            var outcome = await s.SetDisplayHdrAsync(m.MonitorPath, m.Name, wantEnable, DeviceTargets.Source, "hw hdr");
            Say($"HDR toggle -> {wantEnable}: {outcome.Result} ({outcome.Message}) in {clock.ElapsedMilliseconds} ms; errors {string.Join("; ", s.Errors)}");
            Assert.Equal(DisplayApplyResult.Kept, outcome.Result);
            Assert.Equal(wantEnable, w.ReadHdrEnabled(m.MonitorPath));
            var rec = Journal.ActiveRecords.Single(r => r.Kind == ChangeKind.DisplayHdr);
            clock.Restart();
            var undo = await Journal.RevertAsync([rec]);
            Say($"HDR undo: success={undo.Success} [{string.Join("; ", undo.Errors)}] in {clock.ElapsedMilliseconds} ms; now {w.ReadHdrEnabled(m.MonitorPath)}");
            Assert.True(undo.Success);
            Assert.Equal(original, w.ReadHdrEnabled(m.MonitorPath));
        }
        finally
        {
            if (w.ReadHdrEnabled(m.MonitorPath) != original)
            {
                Say("  !! restoring HDR manually");
                w.SetHdr(m.MonitorPath, original.Value);
            }
            Assert.Equal(original, w.ReadHdrEnabled(m.MonitorPath));
        }
    }
}
