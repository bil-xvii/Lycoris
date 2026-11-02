using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Devices.Audio;
using Monolith.Modules.Devices.Displays;
using Xunit.Abstractions;

namespace Monolith.Tests.Hardware;

/// <summary>Case 4 (DDC/CI brightness) and case 5 (capture endpoint level and mute).</summary>
public sealed class HwDdcAudioTests(ITestOutputHelper output) : HwBase(output)
{
    [DllImport("dxva2.dll")] private static extern bool GetCapabilitiesStringLength(IntPtr h, out uint len);
    [DllImport("dxva2.dll")] private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder s, uint len);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] Phys[] m);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint n, [In] Phys[] m);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct Phys
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    private static string? Capabilities(string gdi)
    {
        var hm = Ccd.MonitorHandle(gdi);
        if (hm == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out var n) || n != 1) return null;
        var mons = new Phys[1];
        if (!GetPhysicalMonitorsFromHMONITOR(hm, 1, mons)) return null;
        try
        {
            if (!GetCapabilitiesStringLength(mons[0].Handle, out var len) || len == 0 || len > 8192) return null;
            var sb = new StringBuilder((int)len);
            return CapabilitiesRequestAndCapabilitiesReply(mons[0].Handle, sb, len) ? sb.ToString() : null;
        }
        finally { DestroyPhysicalMonitors(1, mons); }
    }

    [HwFact]
    public async Task Ddc_reads_are_consistent_and_brightness_changes_and_restores_exactly()
    {
        var monitors = DisplayInventory.Read(Environment.OSVersion.Version.Build);
        var w = DeviceWriters.Monitor ?? throw new InvalidOperationException("no DDC writer");
        MonitorInfo? writable = null;
        foreach (var m in monitors)
        {
            var b1 = w.Get(m.MonitorPath, MonitorVcp.Brightness);
            Hold(TimeSpan.FromMilliseconds(150));
            var b2 = w.Get(m.MonitorPath, MonitorVcp.Brightness);
            var c1 = w.Get(m.MonitorPath, MonitorVcp.Contrast);
            var caps = Capabilities(m.GdiName!);
            Say($"DDC {m.Name}: brightness {b1} / again {b2}; contrast {c1}");
            Say($"  caps: {(caps is null ? "no answer" : caps.Length > 300 ? caps[..300] + "…" : caps)}");
            var consistent = b1 is { } a && b2 is { } b && a == b && a.Min < a.Max && a.Value >= a.Min && a.Value <= a.Max
                             && c1 is { } c && c.Min < c.Max && c.Value >= c.Min && c.Value <= c.Max;
            Say($"  consistent={consistent}");
            if (consistent && m.IsPrimary) writable = m;
        }
        if (writable is null)
        {
            Say("DDC: the primary monitor's reads are missing or inconsistent — nothing written");
            return;
        }

        var path = writable.MonitorPath;
        var orig = w.Get(path, MonitorVcp.Brightness)!.Value;
        var delta = orig.Value > orig.Min + 5 ? -1 : 1; // a one-step change, never towards «dark»
        var target = (uint)(orig.Value + delta);
        try
        {
            var s = await Begin("hw ddc");
            var clock = Stopwatch.StartNew();
            var outcome = await s.SetMonitorSettingAsync(path, writable.Name, MonitorVcp.Brightness, target, DeviceTargets.Source, "hw brightness");
            var set = clock.ElapsedMilliseconds;
            var live = w.Get(path, MonitorVcp.Brightness)!.Value;
            Hold(TimeSpan.FromMilliseconds(500));
            Say($"DDC brightness {orig.Value} -> {target}: {outcome.Result} ({outcome.Message}) in {set} ms, live {live.Value}; errors {string.Join("; ", s.Errors)}");
            Assert.Equal(DisplayApplyResult.Kept, outcome.Result);
            Assert.Equal(target, live.Value);
            var rec = Journal.ActiveRecords.Single(r => r.Kind == ChangeKind.MonitorSetting);
            clock.Restart();
            var undo = await Journal.RevertAsync([rec]);
            var after = w.Get(path, MonitorVcp.Brightness)!.Value;
            Say($"DDC undo: success={undo.Success} [{string.Join("; ", undo.Errors)}] in {clock.ElapsedMilliseconds} ms; brightness now {after.Value}");
            Assert.True(undo.Success);
            Assert.Equal(orig.Value, after.Value);
        }
        finally
        {
            if (w.Get(path, MonitorVcp.Brightness) is { } now && now.Value != orig.Value)
            {
                Say("  !! restoring brightness manually");
                w.Set(path, MonitorVcp.Brightness, orig.Value);
            }
            Assert.Equal(orig.Value, w.Get(path, MonitorVcp.Brightness)!.Value.Value);
        }
    }

    [HwFact]
    public async Task Capture_endpoint_level_and_mute_change_and_restore_exactly()
    {
        var snap = AudioInventory.Read();
        foreach (var e in snap.Endpoints.Where(e => e.Flow == AudioFlow.Capture))
            Say($"AUDIO capture: {e.Name} id={e.Id} default={e.IsDefault} scalar={e.Scalar} db={e.Db} ({e.DbMin}..{e.DbMax}) mute={e.Mute} hwSupport={e.HardwareSupport}");
        var ep = snap.Endpoints.FirstOrDefault(e => e.Flow == AudioFlow.Capture && e.IsDefault && e.Scalar is not null && e.Mute is not null);
        var w = DeviceWriters.Audio ?? throw new InvalidOperationException("no audio writer");
        if (ep is null)
        {
            Say("AUDIO: no readable default capture endpoint — nothing written");
            return;
        }
        var before = w.Read(ep.Id)!.Value;
        Say($"AUDIO: using '{ep.Name}': scalar {before.Scalar:0.0000} ({BitConverter.SingleToUInt32Bits(before.Scalar):X8}) mute {before.Mute}");
        var failures = new List<string>();
        try
        {
            // Level: +2 % (or -2 % near the top).
            var target = before.Scalar <= 0.95f ? before.Scalar + 0.02f : before.Scalar - 0.02f;
            var s = await Begin("hw audio level");
            Assert.True(s.SetAudioLevel(ep.Id, ep.Name, target, null, null, DeviceTargets.Source, "hw mic level"), string.Join("; ", s.Errors));
            var live = w.Read(ep.Id)!.Value;
            Hold(TimeSpan.FromMilliseconds(400));
            Say($"  level {before.Scalar:0.0000} -> {target:0.0000}: live {live.Scalar:0.0000}");
            var rec = Journal.ActiveRecords.Single(r => r.Kind == ChangeKind.AudioLevel && r.Audio!.Component == AudioComponent.Level);
            var undo = await Journal.RevertAsync([rec]);
            var after = w.Read(ep.Id)!.Value;
            Say($"  level undo success={undo.Success} -> {after.Scalar:0.0000} ({BitConverter.SingleToUInt32Bits(after.Scalar):X8})");
            if (!undo.Success) failures.Add("level undo: " + string.Join("; ", undo.Errors));
            if (BitConverter.SingleToUInt32Bits(after.Scalar) != BitConverter.SingleToUInt32Bits(before.Scalar))
                failures.Add($"level after undo {after.Scalar:R} is not bit-identical to {before.Scalar:R}");

            // Mute: flip and flip back.
            var s2 = await Begin("hw audio mute");
            Assert.True(s2.SetAudioLevel(ep.Id, ep.Name, null, null, !before.Mute, DeviceTargets.Source, "hw mic mute"), string.Join("; ", s2.Errors));
            var muted = w.Read(ep.Id)!.Value.Mute;
            Hold(TimeSpan.FromMilliseconds(400));
            var rec2 = Journal.ActiveRecords.Single(r => r.Kind == ChangeKind.AudioLevel && r.Audio!.Component == AudioComponent.Mute);
            var undo2 = await Journal.RevertAsync([rec2]);
            var after2 = w.Read(ep.Id)!.Value;
            Say($"  mute {before.Mute} -> {muted}; undo success={undo2.Success} -> {after2.Mute}; level still {after2.Scalar:0.0000}");
            if (muted == before.Mute) failures.Add("mute did not change");
            if (!undo2.Success || after2.Mute != before.Mute) failures.Add("mute undo failed");
        }
        finally
        {
            var now = w.Read(ep.Id);
            if (now is { } n && (BitConverter.SingleToUInt32Bits(n.Scalar) != BitConverter.SingleToUInt32Bits(before.Scalar) || n.Mute != before.Mute))
            {
                Say("  !! restoring the endpoint manually");
                w.SetScalar(ep.Id, before.Scalar);
                w.SetMute(ep.Id, before.Mute);
            }
            var end = w.Read(ep.Id)!.Value;
            Assert.Equal(BitConverter.SingleToUInt32Bits(before.Scalar), BitConverter.SingleToUInt32Bits(end.Scalar));
            Assert.Equal(before.Mute, end.Mute);
        }
        Assert.Empty(failures);
    }
}
