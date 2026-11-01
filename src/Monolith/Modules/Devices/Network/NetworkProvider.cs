using Monolith.Tweaks;

namespace Monolith.Modules.Devices.Network;

/// <summary>
/// The «Сеть» group of «Устройства и сеть». Read-only and silent: the scan reads WMI, the routing table, the event log
/// and the Wi-Fi radio state — it never sends a packet (measurements are the tools, on click only).
/// </summary>
internal sealed class NetworkProvider : IDeviceProvider
{
    public string Name => "сеть";

    public Task<IReadOnlyList<DeviceCard>> DetectAsync(DeviceScanContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = NetSystem.ReadSnapshot();
        if (ctx.Results.Get<NetCheckResult>(NetKeys.Check) is { } check) NetworkEvidence.Ipv6Worse = NetFindings.Ipv6Penalty(check) is not null;
        return Task.FromResult<IReadOnlyList<DeviceCard>>(NetworkCards.Build(snapshot, ctx.Results));
    }
}
