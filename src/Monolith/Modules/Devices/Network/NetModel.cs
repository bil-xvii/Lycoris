using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Network;

// What the network part of «Устройства и сеть» knows: a read-only snapshot of the adapters (scan, no traffic) and the
// results of the measurements (tools, on click only), kept in the page's DeviceResultStore for the app session.

public enum NetMedium { Ethernet, WiFi }

/// <summary>A physical network adapter (MSFT_NetAdapter + its whitelisted advanced properties). No MAC, no serials.</summary>
public sealed record NetAdapterInfo
{
    public required Guid InterfaceGuid { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required NetMedium Medium { get; init; }
    /// <summary>MediaConnectState = 1 (the cable is in / Wi-Fi is associated).</summary>
    public bool Connected { get; init; }
    public long? SpeedBps { get; init; }
    /// <summary>MediaDuplexState: true = full, false = half, null = unknown.</summary>
    public bool? FullDuplex { get; init; }
    public string? DriverProvider { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverDate { get; init; }
    public string? ComponentId { get; init; }
    public int? InterfaceIndex { get; init; }
    /// <summary>Only the keywords MONOLITH reads: *SpeedDuplex, *EEE, EEE, AdvancedEEE, *ReceiveBuffers.</summary>
    public IReadOnlyList<NicPropertyInfo> Properties { get; init; } = [];
    /// <summary>Disconnects in the last 7 days that were not a shutdown or sleep (Windows event log); null = unknown.</summary>
    public int? LinkDrops { get; init; }
    /// <summary>Wi-Fi radio switched off (software or hardware); null = unknown / not Wi-Fi.</summary>
    public bool? RadioOff { get; init; }

    public NicPropertyInfo? Property(string keyword) =>
        Properties.FirstOrDefault(p => string.Equals(p.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Everything the scan reads about the network (no packets are sent).</summary>
public sealed record NetSnapshot
{
    public IReadOnlyList<NetAdapterInfo> Adapters { get; init; } = [];
    /// <summary>The interface the default route (to 1.1.1.1) uses; null = no route.</summary>
    public Guid? BestInterface { get; init; }
    public string? BestDescription { get; init; }
    /// <summary>The default route goes through a VPN / tunnel / virtual adapter.</summary>
    public bool ViaVpn { get; init; }
    public string? Gateway { get; init; }
    /// <summary>A global IPv6 address and an IPv6 gateway exist on the default-route interface.</summary>
    public bool HasIpv6 { get; init; }
    public IReadOnlyList<string> DnsServers { get; init; } = [];
    /// <summary>The PC's Active Directory domain (a DHCP suffix such as «lan» from a home router does not count).</summary>
    public string? Domain { get; init; }
    public bool DohConfigured { get; init; }
    public int? Mtu { get; init; }
    /// <summary>A game (Game Boost session or a running game from the library) — nothing restarts the adapter then.</summary>
    public string? RunningGame { get; init; }
    /// <summary>Steam is installed (the Valve region check is offered).</summary>
    public bool SteamInstalled { get; init; }
    /// <summary>MONOLITH's own active network changes (journal record ids): a change after a measurement asks for a re-check.</summary>
    public string Fingerprint { get; init; } = "";

    public NetAdapterInfo? Best => BestInterface is { } g ? Adapters.FirstOrDefault(a => a.InterfaceGuid == g) : null;
    public bool BestIsWifi => Best?.Medium == NetMedium.WiFi;
    public bool HasWifi => Adapters.Any(a => a.Medium == NetMedium.WiFi);
    public bool HasEthernet => Adapters.Any(a => a.Medium == NetMedium.Ethernet);
}

// ───────────────────────────── Measurements ─────────────────────────────

public enum NetTargetRole { Gateway, Internet }

public sealed record NetTarget(string Label, string Address, NetTargetRole Role, bool Ipv6 = false);

public sealed record NetTargetResult(NetTarget Target, IReadOnlyList<double?> Samples)
{
    public LatencyStats Stats => NetStats.Compute(Samples);
    public IReadOnlyList<double> Received => Samples.Where(s => s is not null).Select(s => s!.Value).ToList();
}

/// <summary>Wi-Fi link while measuring (Native Wi-Fi API: RSSI and channel only — no location-gated calls).</summary>
public sealed record WifiLink(int? RssiDbm, int? Channel, string? Error)
{
    public bool Is24GHz => Channel is >= 1 and <= 14;
    public string Band => Channel switch
    {
        null => "—",
        >= 1 and <= 14 => "2,4 ГГц",
        _ => "5/6 ГГц",
    };
}

/// <summary>Who used the network while measuring.</summary>
public sealed record TrafficInfo
{
    public double DownMbps { get; init; }
    public double UpMbps { get; init; }
    /// <summary>Busiest processes: name, established connections, bytes (null without admin rights).</summary>
    public IReadOnlyList<ProcessTraffic> Top { get; init; } = [];
    public bool HasBytes { get; init; }
    /// <summary>Delivery Optimization (Windows Update, Store) jobs downloading now; null = unknown.</summary>
    public int? WindowsUpdateJobs { get; init; }
    public long? InErrors { get; init; }
    public long? InPackets { get; init; }
    public long? InDiscards { get; init; }
}

public sealed record ProcessTraffic(string Name, int Connections, long? Bytes);

public sealed record MtuResult(int InterfaceMtu, int? PathMtu, bool BlackHole);

public sealed record DnsServerResult(string Label, string Address, bool IsCurrent, double? MedianMs, double TimeoutPercent, int Queries)
{
    public bool Answered => MedianMs is not null;
}

public sealed record DnsBenchResult(IReadOnlyList<DnsServerResult> Servers, DateTime At, string? SkipReason = null)
{
    public DnsServerResult? Current => Servers.Where(s => s.IsCurrent && s.Answered).OrderBy(s => s.MedianMs).FirstOrDefault()
                                       ?? Servers.FirstOrDefault(s => s.IsCurrent);
}

/// <summary>«Замер пинга»: idle latency + link audit + DNS + MTU + background traffic.</summary>
public sealed record NetCheckResult
{
    public required DateTime At { get; init; }
    public required TimeSpan Duration { get; init; }
    public IReadOnlyList<NetTargetResult> Targets { get; init; } = [];
    public WifiLink? Wifi { get; init; }
    public TrafficInfo? Traffic { get; init; }
    public MtuResult? Mtu { get; init; }
    public DnsBenchResult? Dns { get; init; }
    /// <summary>A running game keeps an established TCP session to its server (tcp-ack-frequency can matter).</summary>
    public string? TcpGame { get; init; }
    public bool OnWifi { get; init; }
    /// <summary>The snapshot fingerprint at measurement time.</summary>
    public string Fingerprint { get; init; } = "";

    public NetTargetResult? Gateway => Targets.FirstOrDefault(t => t.Target.Role == NetTargetRole.Gateway);
    public IEnumerable<NetTargetResult> Internet4 => Targets.Where(t => t.Target is { Role: NetTargetRole.Internet, Ipv6: false });
    public IEnumerable<NetTargetResult> Internet6 => Targets.Where(t => t.Target is { Role: NetTargetRole.Internet, Ipv6: true });

    /// <summary>The best IPv4 internet target (lowest median), the honest «интернет» number.</summary>
    public NetTargetResult? BestInternet => Internet4.Where(t => t.Stats.HasData).OrderBy(t => t.Stats.P50).FirstOrDefault();
}

public sealed record LoadTestResult
{
    public required DateTime At { get; init; }
    public double IdleMedian { get; init; }
    public double LoadedMedian { get; init; }
    public double Increase => Math.Max(0, LoadedMedian - IdleMedian);
    public string Grade => NetStats.Grade(Increase);
    public double Mbps { get; init; }
    public long Bytes { get; init; }
    public int LoadedSamples { get; init; }
    public long? InErrorsDelta { get; init; }
    public long? InDiscardsDelta { get; init; }
    public long? InPacketsDelta { get; init; }
}

public sealed record SdrPop(string Code, string Description, string RelayAddress);

public sealed record SdrRegion(SdrPop Pop, LatencyStats Stats);

public sealed record SdrResult(IReadOnlyList<SdrRegion> Regions, DateTime At)
{
    public IReadOnlyList<SdrRegion> Closest(int n) => Regions.Where(r => r.Stats.HasData).OrderBy(r => r.Stats.P50).Take(n).ToList();
}

/// <summary>A finding: a measured problem with its severity and what to do. Never applies anything by itself.</summary>
public sealed record NetFinding(string Id, NetSeverity Severity, string Title, string Value, string Hint, string Reason, DeviceLink? Link = null,
    Guid? Adapter = null);

/// <summary>Keys of the page's DeviceResultStore.</summary>
public static class NetKeys
{
    public const string Check = "net:check";
    public const string CheckBefore = "net:check:before";
    public const string Load = "net:load";
    public const string Dns = "net:dns";
    public const string Sdr = "net:sdr";
    public const string SdrConfig = "net:sdr:config";
    public static string DuplexRegression(Guid adapter) => $"net:duplex:{adapter:N}";
}
