using System.Net;
using Lycoris.Core;
using Lycoris.Core.Devices;
using Lycoris.Modules.Devices.Network;
using Lycoris.Modules.Devices.Vendors;
using Lycoris.Modules.Drivers.Hardware;
using Lycoris.Modules.Games;
using Xunit.Abstractions;

namespace Lycoris.Tests.Hardware;

/// <summary>Cases 6 and 7: everything the app only reads or measures. Nothing here changes anything.</summary>
public sealed class HwReadOnlyTests(ITestOutputHelper output) : HwBase(output)
{
    [HwFact]
    public void Network_adapters_properties_and_link_are_read_only()
    {
        var snap = NetSystem.ReadSnapshot();
        Say($"NET best={snap.BestInterface} '{snap.BestDescription}' gw={snap.Gateway} dns=[{string.Join(", ", snap.DnsServers)}] ipv6={snap.HasIpv6} vpn={snap.ViaVpn} mtu={snap.Mtu} doh={snap.DohConfigured} game={snap.RunningGame} steam={snap.SteamInstalled}");
        foreach (var a in snap.Adapters)
        {
            Say($"NET adapter '{a.Name}' / '{a.Description}' medium={a.Medium} connected={a.Connected} speed={(a.SpeedBps is { } s ? s / 1_000_000 + " Mbit/s" : "?")} fullDuplex={a.FullDuplex} index={a.InterfaceIndex} driver={a.DriverProvider} {a.DriverVersion} {a.DriverDate} drops7d={a.LinkDrops}");
            foreach (var p in a.Properties)
                Say($"     property {p.Keyword} = {p.Value} (default {p.DefaultValue}; valid [{string.Join(",", p.ValidValues)}]) {p.DisplayName}");
        }
        Assert.NotEmpty(snap.Adapters);
    }

    [HwFact]
    public async Task Ping_probe_two_targets_one_per_second()
    {
        var snap = NetSystem.ReadSnapshot();
        // Two targets, one echo per second each... which is at most 2 pings/s in total, over 5 s.
        var targets = NetPing.Targets(snap).Where(t => !t.Ipv6).Take(2).ToList();
        var res = await NetPing.RunAsync(targets, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), null, CancellationToken.None);
        foreach (var r in res)
            Say($"PING {r.Target.Label} {r.Target.Address}: samples [{string.Join(", ", r.Samples.Select(x => x?.ToString("0.0") ?? "lost"))}] ms; stats {r.Stats}");
        Assert.All(res, r => Assert.True(r.Received.Count > 0, r.Target.Label + " never answered"));
    }

    [HwFact]
    public async Task Dns_bench_is_plausible()
    {
        var snap = NetSystem.ReadSnapshot();
        var r = await DnsBench.RunAsync(snap, null, CancellationToken.None);
        Say($"DNS skip={r.SkipReason}");
        foreach (var s in r.Servers)
            Say($"DNS {s.Label} {s.Address} current={s.IsCurrent}: median {s.MedianMs:0.0} ms, timeouts {s.TimeoutPercent:0.#}% of {s.Queries}");
        Assert.NotEmpty(r.Servers);
    }

    [HwFact]
    public void Vendor_apps_game_library_power_plan_and_hardware_analysis()
    {
        var vendors = VendorApps.DetectNow();
        foreach (var v in vendors.States)
            Say($"VENDOR {v.App.Name}: installed={v.Installed} launchable={v.Launchable} running={v.Running} version={v.Version}");

        var plans = PowerSchemes.List();
        Say($"POWER active={PowerSchemes.GetActive()}; plans: {string.Join(", ", plans.Select(p => $"{p.Name}={p.Id}"))}");

        var scan = GameLibrary.Scan([]);
        Say($"GAMES found {scan.Games.Count}; sources: {string.Join(", ", scan.Sources.Select(s => $"{s.Name} installed={s.Installed} count={s.Count}{(s.Error is null ? "" : " ERR " + s.Error)}"))}");
        foreach (var g in scan.Games.Take(40)) Say($"  game {g.Platform}: {g.Name}");

        var profile = SystemProfile.LoadAsync().GetAwaiter().GetResult();
        var hw = HardwareScanner.Scan(profile);
        Say($"HW cpu={hw.Cpu.Name} cores={hw.Cpu.Cores}/{hw.Cpu.Threads} board={hw.Board.Manufacturer} {hw.Board.Product} bios={hw.Board.BiosVersion} laptop={hw.IsLaptop}");
        foreach (var g in hw.Gpus) Say($"HW gpu {g.Name} vendor={g.Vendor} driver={g.DriverVersion} {g.DriverDate:yyyy-MM-dd} vram={g.DedicatedMemoryBytes / 1048576} MB pnp={g.PnpDeviceId}");
        Say($"HW nvidia package={hw.NvidiaPackageVersion}");
        foreach (var n in hw.Network) Say($"HW net {n.Name} / {n.Manufacturer} bt={n.IsBluetooth} {n.PnpDeviceId}");
        foreach (var a in hw.Audio) Say($"HW audio {a.Name} / {a.Manufacturer} {a.PnpDeviceId}");
        foreach (var d in hw.Displays) Say($"HW display {d}");
        foreach (var d in hw.Disks) Say($"HW disk {d.Model} {d.MediaType}/{d.BusType} {d.SizeBytes / 1_000_000_000} GB fw {d.Firmware}");
        foreach (var r in hw.Ram) Say($"HW ram {r}");
        Say($"HW runtimes vc={hw.Runtimes.VcX64} directx={hw.Runtimes.DirectX} desktop=[{string.Join(", ", hw.Runtimes.DesktopRuntimes.Select(kv => $"{kv.Key}:{kv.Value}"))}]");
        Say($"HW pnp devices: {hw.Devices.Count}");
        foreach (var d in hw.Devices.Where(d => d.PnpClass is "Display" or "Net" or "MEDIA" or "System" or "USB" && d.Present != false).Take(80))
            Say($"  pnp [{d.PnpClass}] {d.Name} | {d.Manufacturer} | {d.DeviceId} | err={d.ErrorCode} virtual={d.Virtual}");
        Assert.NotEmpty(hw.Gpus);
    }
}
