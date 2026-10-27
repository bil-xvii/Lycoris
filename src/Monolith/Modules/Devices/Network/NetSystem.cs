using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Games;

namespace Monolith.Modules.Devices.Network;

/// <summary>
/// Reads the network state for the scan (no packet is sent): adapters (MSFT_NetAdapter, locale-independent), their
/// whitelisted advanced properties, the default route, DNS, IPv6, link drops from the event log, Wi-Fi radio state.
/// </summary>
public static class NetSystem
{
    private const string Cim = @"\\.\root\StandardCimv2";

    /// <summary>The keywords the page reads (writes are limited further by DeviceTargets.NicKeywords).</summary>
    private static readonly HashSet<string> ReadKeywords = new(StringComparer.OrdinalIgnoreCase) { "*SpeedDuplex", "*EEE", "EEE", "AdvancedEEE", "*ReceiveBuffers" };

    private static readonly string[] VpnMarkers =
        ["vpn", "wireguard", "wintun", "tap-", "tap ", "openvpn", "tunnel", "anyconnect", "fortinet", "nordlynx", "zerotier", "tailscale", "hamachi", "radmin"];

    public static NetSnapshot ReadSnapshot()
    {
        var adapters = ReadAdapters();
        var nics = SafeInterfaces();
        var bestIndex = NetNative.BestInterfaceIndex(IPAddress.Parse("1.1.1.1"));
        NetworkInterface? best = null;
        if (bestIndex is { } bi)
            best = nics.FirstOrDefault(n => TryIndex(n) == bi);

        Guid? bestGuid = best is not null && Guid.TryParse(best.Id, out var g) ? g : null;
        var vpn = best is not null && (best.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                                       || VpnMarkers.Any((best.Name + " " + best.Description).ToLowerInvariant().Contains)
                                       || (bestGuid is { } bg && adapters.All(a => a.InterfaceGuid != bg)));

        string? gateway = null, suffix = null;
        var dns = new List<string>();
        var ipv6 = false;
        int? mtu = null;
        if (best is not null)
        {
            try
            {
                var props = best.GetIPProperties();
                gateway = props.GatewayAddresses.Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))?.ToString();
                dns.AddRange(props.DnsAddresses.Where(a => !a.IsIPv6SiteLocal).Select(a => a.ToString()).Distinct());
                suffix = string.IsNullOrWhiteSpace(props.DnsSuffix) ? null : props.DnsSuffix;
                ipv6 = props.UnicastAddresses.Any(u => IsGlobalV6(u.Address))
                       && props.GatewayAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6);
                mtu = props.GetIPv4Properties()?.Mtu;
            }
            catch (NetworkInformationException) { }
        }

        return new NetSnapshot
        {
            Adapters = adapters, BestInterface = bestGuid, BestDescription = best?.Description, ViaVpn = vpn, Gateway = gateway,
            HasIpv6 = ipv6, DnsServers = dns, DnsSuffix = suffix, DohConfigured = bestGuid is { } guid && DohConfigured(guid), Mtu = mtu,
            RunningGame = NetGames.RunningGame(), SteamInstalled = SteamInstalled(), Fingerprint = Fingerprint(bestGuid, dns),
        };
    }

    private static NetworkInterface[] SafeInterfaces()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }
    }

    private static int? TryIndex(NetworkInterface n)
    {
        try { return n.Supports(NetworkInterfaceComponent.IPv4) ? n.GetIPProperties().GetIPv4Properties()?.Index : null; }
        catch (NetworkInformationException) { return null; }
    }

    private static bool IsGlobalV6(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.IsIPv6Teredo
        && (a.GetAddressBytes()[0] & 0xE0) == 0x20; // 2000::/3

    // ───────────────────────────── Adapters ─────────────────────────────

    /// <summary>Physical Ethernet / Wi-Fi adapters, one per device (Wi-Fi 7 HBS cards expose several interfaces).</summary>
    public static IReadOnlyList<NetAdapterInfo> ReadAdapters()
    {
        var rows = new List<(NetAdapterInfo Info, string Pnp, int Status)>();
        try
        {
            using var searcher = SystemProfile.Searcher(Cim,
                "SELECT Name, InterfaceGuid, InterfaceDescription, NdisPhysicalMedium, MediaConnectState, Speed, MediaDuplexState, DriverProvider, " +
                "DriverVersionString, DriverDate, ComponentID, PnPDeviceID, InterfaceIndex, InterfaceOperationalStatus, Virtual, HardwareInterface FROM MSFT_NetAdapter");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["Virtual"] is true || mo["HardwareInterface"] is false) continue;
                    if (!Guid.TryParse(mo["InterfaceGuid"]?.ToString(), out var guid)) continue;
                    var medium = Convert.ToInt32(mo["NdisPhysicalMedium"] ?? 0) switch
                    {
                        14 or 0 => (NetMedium?)NetMedium.Ethernet,
                        9 or 1 => NetMedium.WiFi,
                        _ => null,
                    };
                    if (medium is null) continue;
                    var status = Convert.ToInt32(mo["InterfaceOperationalStatus"] ?? 0);
                    if (status == 6) continue; // not present
                    var duplex = Convert.ToInt32(mo["MediaDuplexState"] ?? 0);
                    var speed = mo["Speed"] is { } sp ? Convert.ToInt64(sp) : (long?)null;
                    var info = new NetAdapterInfo
                    {
                        InterfaceGuid = guid, Name = mo["Name"]?.ToString() ?? "", Description = mo["InterfaceDescription"]?.ToString() ?? "",
                        Medium = medium.Value, Connected = Convert.ToInt32(mo["MediaConnectState"] ?? 0) == 1,
                        SpeedBps = speed is > 0 and < long.MaxValue ? speed : null,
                        FullDuplex = duplex switch { 2 => true, 1 => false, _ => null },
                        DriverProvider = mo["DriverProvider"]?.ToString(), DriverVersion = mo["DriverVersionString"]?.ToString(),
                        DriverDate = mo["DriverDate"]?.ToString(), ComponentId = mo["ComponentID"]?.ToString(),
                        InterfaceIndex = mo["InterfaceIndex"] is { } idx ? Convert.ToInt32(idx) : null,
                    };
                    rows.Add((info, mo["PnPDeviceID"]?.ToString() ?? guid.ToString(), status));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Сеть: не удалось прочитать адаптеры — {ex.Message}");
            return [];
        }

        var wlan = SafeWlan();
        var result = new List<NetAdapterInfo>();
        foreach (var group in rows.GroupBy(r => r.Pnp, StringComparer.OrdinalIgnoreCase))
        {
            var pick = group.OrderByDescending(r => r.Info.Connected).ThenBy(r => r.Status == 1 ? 0 : 1).ThenBy(r => r.Info.InterfaceIndex ?? int.MaxValue).First();
            var info = pick.Info with { Properties = ReadProperties(pick.Info.InterfaceGuid) };
            if (info.Medium == NetMedium.Ethernet) info = info with { LinkDrops = LinkDrops(info.InterfaceGuid) };
            else
            {
                var radio = group.Select(r => wlan.TryGetValue(r.Info.InterfaceGuid, out var w) ? w.RadioOff : null).FirstOrDefault(r => r is not null);
                info = info with { RadioOff = radio };
            }
            result.Add(info);
        }
        return result.OrderBy(a => a.Medium).ThenByDescending(a => a.Connected).ToList();
    }

    private static IReadOnlyDictionary<Guid, (int State, bool? RadioOff)> SafeWlan()
    {
        try { return NetNative.WlanInterfaces(); }
        catch (Exception ex) { Log.Warn($"Сеть: Wi-Fi не прочитан — {ex.Message}"); return new Dictionary<Guid, (int, bool?)>(); }
    }

    /// <summary>The advanced properties MONOLITH reads, by InterfaceGuid (InstanceID = "{GUID}::keyword").</summary>
    public static IReadOnlyList<NicPropertyInfo> ReadProperties(Guid adapter, string? onlyKeyword = null)
    {
        var list = new List<NicPropertyInfo>();
        var id = adapter.ToString("B").ToUpperInvariant();
        var where = onlyKeyword is null ? $"InstanceID LIKE '{id}::%'" : $"InstanceID = '{id}::{onlyKeyword}'";
        try
        {
            using var searcher = SystemProfile.Searcher(Cim,
                "SELECT RegistryKeyword, RegistryValue, ValidRegistryValues, DefaultRegistryValue, DisplayName, NumericParameterMaxValue " +
                $"FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE {where}");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var keyword = mo["RegistryKeyword"]?.ToString();
                    if (keyword is null || !ReadKeywords.Contains(keyword)) continue;
                    var value = (mo["RegistryValue"] as string[])?.FirstOrDefault();
                    var valid = (mo["ValidRegistryValues"] as string[])?.Where(v => v is not null).ToList() ?? [];
                    if (valid.Count == 0 && mo["NumericParameterMaxValue"] is { } max) valid = ["max:" + Convert.ToInt64(max)];
                    list.Add(new NicPropertyInfo(keyword, value, valid, mo["DefaultRegistryValue"]?.ToString(), mo["DisplayName"]?.ToString()));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Сеть: свойства адаптера не прочитаны — {ex.Message}");
        }
        return list;
    }

    /// <summary>MediaConnectState and duplex of one adapter now (after a speed/duplex change).</summary>
    public static (bool Connected, bool? FullDuplex, long? SpeedBps)? ReadLink(Guid adapter)
    {
        try
        {
            using var searcher = SystemProfile.Searcher(Cim,
                $"SELECT MediaConnectState, MediaDuplexState, Speed FROM MSFT_NetAdapter WHERE InterfaceGuid = '{adapter.ToString("B").ToUpperInvariant()}'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var duplex = Convert.ToInt32(mo["MediaDuplexState"] ?? 0);
                    return (Convert.ToInt32(mo["MediaConnectState"] ?? 0) == 1, duplex switch { 2 => true, 1 => false, _ => null },
                        mo["Speed"] is { } s ? Convert.ToInt64(s) : null);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException) { }
        return null;
    }

    // ───────────────────────────── Link drops ─────────────────────────────

    /// <summary>
    /// «Network disconnected» events (NetworkProfile/Operational 10001) of the adapter's network in the last 7 days that
    /// were not part of a shutdown, restart or sleep. Locale-independent (event ids and GUIDs only). Null = unknown.
    /// </summary>
    internal static int? LinkDrops(Guid adapter)
    {
        try
        {
            var networks = new HashSet<Guid>();
            foreach (var p in global::Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles())
            {
                var na = p.NetworkAdapter;
                if (na is not null && na.NetworkAdapterId == adapter && na.NetworkItem is { } item) networks.Add(item.NetworkId);
            }
            if (networks.Count == 0) return null;

            var quiet = new List<DateTime>();
            const string week = "TimeCreated[timediff(@SystemTime) <= 604800000]";
            foreach (var e in Query("System",
                         $"*[System[((Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=13) or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=506 or EventID=109)) or (Provider[@Name='User32'] and EventID=1074)) and {week}]]"))
                quiet.Add(e);

            var drops = 0;
            using var reader = new EventLogReader(new EventLogQuery("Microsoft-Windows-NetworkProfile/Operational", PathType.LogName,
                $"*[System[EventID=10001 and {week}]]"));
            for (var rec = reader.ReadEvent(); rec is not null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    if (rec.TimeCreated is not { } t || rec.Properties.Count < 3) continue;
                    var raw = rec.Properties[2].Value;
                    var net = raw is Guid gg ? gg : Guid.TryParse(raw?.ToString(), out var pg) ? pg : Guid.Empty;
                    if (!networks.Contains(net)) continue;
                    if (quiet.Any(q => q >= t.AddMinutes(-2) && q <= t.AddMinutes(5))) continue;
                    drops++;
                }
            }
            return drops;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<DateTime> Query(string log, string xpath)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath));
        for (var rec = reader.ReadEvent(); rec is not null; rec = reader.ReadEvent())
            using (rec)
                if (rec.TimeCreated is { } t) yield return t;
    }

    // ───────────────────────────── DNS, Steam, fingerprint ─────────────────────────────

    /// <summary>DNS over HTTPS set for the adapter (per-interface DoH templates) or by policy.</summary>
    private static bool DohConfigured(Guid adapter)
    {
        try
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var policy = lm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient"))
                if (policy?.GetValue("DoHPolicy") is int p && p >= 2) return true;
            foreach (var family in new[] { "Doh", "Doh6" })
            {
                using var key = lm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{adapter:B}\DohInterfaceSettings\{family}");
                if (key is not null && key.SubKeyCount > 0) return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return false;
    }

    private static bool SteamInstalled()
    {
        var path = RegistryHelper.ReadString(RegHive.HKCU, @"Software\Valve\Steam", "SteamPath");
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "steam.exe")); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Journal sources of the network changes MONOLITH can make (Optimize tweaks shown on this page).</summary>
    public static readonly IReadOnlySet<string> TweakSources = new HashSet<string>(StringComparer.Ordinal)
    {
        "tweak:wifi-power-saving-off", "tweak:tcp-autotuning-normal", "tweak:dns-cloudflare", "tweak:dns-google",
        "tweak:delivery-optimization-local", "tweak:tcp-ack-frequency", "tweak:ipv6-prefer-ipv4",
    };

    /// <summary>What a re-check compares against: MONOLITH's active network records, the default route and its DNS.</summary>
    private static string Fingerprint(Guid? best, IReadOnlyList<string> dns)
    {
        IEnumerable<string> ids;
        try
        {
            ids = ChangeJournal.Instance.ActiveRecords
                .Where(r => TweakSources.Contains(r.Source) || r.Kind == ChangeKind.NicProperty)
                .Select(r => r.Id.ToString("N")).OrderBy(x => x, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ids = [];
        }
        return $"{best:N}|{string.Join(",", dns)}|{string.Join(",", ids)}";
    }

    // ───────────────────────────── Counters and traffic ─────────────────────────────

    public static NetInterfaceCounters? Counters(Guid? adapter)
    {
        if (adapter is not { } guid) return null;
        try
        {
            var n = SafeInterfaces().FirstOrDefault(i => Guid.TryParse(i.Id, out var g) && g == guid);
            if (n is null) return null;
            var s = n.GetIPStatistics();
            return new NetInterfaceCounters(s.BytesReceived, s.BytesSent, s.UnicastPacketsReceived + s.NonUnicastPacketsReceived,
                s.IncomingPacketsWithErrors, s.IncomingPacketsDiscarded);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Known updaters and launchers (process name → what the user sees).</summary>
    private static readonly Dictionary<string, string> KnownDownloaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steam"] = "Steam", ["steamservice"] = "Steam", ["Battle.net"] = "Battle.net", ["Agent"] = "Battle.net (агент обновлений)",
        ["EpicGamesLauncher"] = "Epic Games", ["EADesktop"] = "EA app", ["EABackgroundService"] = "EA app", ["upc"] = "Ubisoft Connect",
        ["UbisoftConnect"] = "Ubisoft Connect", ["RiotClientServices"] = "Riot Client", ["OneDrive"] = "OneDrive", ["Dropbox"] = "Dropbox",
        ["GoogleDriveFS"] = "Google Drive", ["qbittorrent"] = "qBittorrent", ["utorrent"] = "µTorrent", ["BitTorrent"] = "BitTorrent",
        ["MoUsoCoreWorker"] = "Центр обновления Windows", ["wuauclt"] = "Центр обновления Windows", ["GameInputSvc"] = "Xbox",
        ["XboxPcApp"] = "Xbox", ["GamingServices"] = "Xbox (Gaming Services)",
    };

    /// <summary>Starts per-connection byte counters (admin only; false without). Call <see cref="FinishTraffic"/> to read and switch them off.</summary>
    internal static IReadOnlyList<NetNative.TcpConn> StartTraffic(out bool counting)
    {
        var conns = NetNative.EstablishedConnections()
            .Where(c => !IPAddress.IsLoopback(c.Remote) && c.Pid > 4)
            .Take(256).ToList();
        counting = false;
        if (!Elevation.IsElevated) return conns;
        foreach (var c in conns)
            if (NetNative.SetCollection(c, true)) counting = true;
        return conns;
    }

    /// <summary>Reads the counters, switches every one of them off again and names the busiest processes.</summary>
    internal static IReadOnlyList<ProcessTraffic> FinishTraffic(IReadOnlyList<NetNative.TcpConn> conns, bool counting)
    {
        var bytes = new Dictionary<int, long>();
        if (counting)
        {
            foreach (var c in conns)
            {
                try
                {
                    if (NetNative.ReadBytes(c) is { } b) bytes[c.Pid] = bytes.GetValueOrDefault(c.Pid) + b;
                }
                finally
                {
                    NetNative.SetCollection(c, false);
                }
            }
        }
        var names = new Dictionary<int, string>();
        string Name(int pid)
        {
            if (names.TryGetValue(pid, out var n)) return n;
            try
            {
                using var p = Process.GetProcessById(pid);
                n = KnownDownloaders.TryGetValue(p.ProcessName, out var known) ? known : p.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                n = $"процесс {pid}";
            }
            return names[pid] = n;
        }
        return conns.GroupBy(c => c.Pid)
            .Select(g => (Name: Name(g.Key), Count: g.Count(), Bytes: counting ? bytes.GetValueOrDefault(g.Key) : (long?)null))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessTraffic(g.Key, g.Sum(x => x.Count), counting ? g.Sum(x => x.Bytes ?? 0) : null))
            .OrderByDescending(p => p.Bytes ?? 0).ThenByDescending(p => p.Connections)
            .Take(5).ToList();
    }

    /// <summary>Windows Update / Store downloads through Delivery Optimization now (only active jobs are listed); null = unknown.</summary>
    public static async Task<int?> DeliveryOptimizationJobsAsync(CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunPowerShellAsync("@(Get-DeliveryOptimizationStatus -ErrorAction Stop 3>$null).Count",
                new ProcessRunOptions { Encoding = OutputEncoding.Utf8, Timeout = TimeSpan.FromSeconds(15), LogCommand = false }, ct).ConfigureAwait(false);
            return r.ExitCode == 0 && int.TryParse(r.StdOut.Trim(), out var n) ? n : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Is a game running? Nothing that restarts the adapter or floods the line happens then.</summary>
public static class NetGames
{
    /// <summary>Tests replace the answer.</summary>
    internal static Func<string?>? RunningGameForTests;

    /// <summary>«игровой сеанс Game Boost», or the name of a running game from the library; null when none.</summary>
    public static string? RunningGame()
    {
        if (RunningGameForTests is { } fake) return fake();
        if (GameBoostState.IsActive) return "игровой сеанс Game Boost";
        return RunningGames().FirstOrDefault().Name;
    }

    /// <summary>Running processes inside a library game's folder: (game, pids).</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<int> Pids)> RunningGames()
    {
        LibraryScan? scan;
        try { scan = GameLibrary.LoadCache(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { scan = null; }
        if (scan is null) return [];
        var dirs = scan.Games.Select(g => (g.Name, Dir: GamePaths.NormalizeDir(g.InstallDir))).Where(d => d.Dir.Length > 4).ToList();
        if (dirs.Count == 0) return [];
        var found = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                var path = GamesNative.GetImagePath(p.Id);
                if (path is null) continue;
                var hit = dirs.FirstOrDefault(d => path.StartsWith(d.Dir, StringComparison.OrdinalIgnoreCase));
                if (hit.Name is null) continue;
                if (!found.TryGetValue(hit.Name, out var pids)) found[hit.Name] = pids = [];
                pids.Add(p.Id);
            }
        }
        return found.Select(kv => (kv.Key, (IReadOnlyList<int>)kv.Value)).ToList();
    }

    /// <summary>
    /// A running game that keeps an established TCP session to a non-web port of a remote server (research
    /// network_stack.md §28): only then can «Мгновенные TCP-подтверждения» matter. UDP games return null.
    /// </summary>
    internal static string? TcpGame(IReadOnlyList<NetNative.TcpConn> conns)
    {
        foreach (var (name, pids) in RunningGames())
        {
            var set = pids.ToHashSet();
            if (conns.Any(c => set.Contains(c.Pid) && c.RemotePort is not (80 or 443) && !IPAddress.IsLoopback(c.Remote)
                               && !(c.Remote.AddressFamily == AddressFamily.InterNetwork && DnsAdvice.IsPrivate(c.Remote))))
                return name;
        }
        return null;
    }
}
