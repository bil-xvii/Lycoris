using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lycoris.Modules.Devices.Network;

/// <summary>A minimal DNS A-query over UDP (RFC 1035): build the packet, check the reply. Pure.</summary>
public static class DnsWire
{
    /// <summary>A standard query with recursion desired, one question, class IN.</summary>
    public static byte[] BuildQuery(ushort id, string name, ushort qtype = 1)
    {
        var labels = name.TrimEnd('.').Split('.');
        if (labels.Length == 0 || labels.Any(l => l.Length is 0 or > 63) || name.Length > 253)
            throw new ArgumentException(Loc.Tf("некорректное имя: {0}", name), nameof(name));
        var buf = new List<byte>(18 + name.Length)
        {
            (byte)(id >> 8), (byte)id,
            0x01, 0x00, // RD = 1
            0x00, 0x01, // QDCOUNT
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.Add(0);
        buf.Add((byte)(qtype >> 8));
        buf.Add((byte)qtype);
        buf.Add(0x00);
        buf.Add(0x01); // IN
        return buf.ToArray();
    }

    /// <summary>
    /// The reply's RCODE when it is a response to <paramref name="id"/> (0 = answer, 3 = no such name); null when the
    /// packet is not a reply to this query. Both count as «the resolver answered» — only the time matters.
    /// </summary>
    public static int? ReplyCode(ReadOnlySpan<byte> reply, ushort id)
    {
        if (reply.Length < 12) return null;
        if (((reply[0] << 8) | reply[1]) != id) return null;
        if ((reply[2] & 0x80) == 0) return null; // QR bit: must be a response
        return reply[3] & 0x0F;
    }
}

/// <summary>Whether to offer a public DNS (finding F19). Pure; the benchmark is shown either way.</summary>
public static class DnsAdvice
{
    public const string Cloudflare = "1.1.1.1";
    public const string Google = "8.8.8.8";
    public const string Quad9 = "9.9.9.9";

    private static readonly HashSet<string> PublicResolvers = new(StringComparer.OrdinalIgnoreCase)
    {
        "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112",
        "2606:4700:4700::1111", "2606:4700:4700::1001", "2001:4860:4860::8888", "2001:4860:4860::8844",
    };

    /// <summary>
    /// Why no public resolver may be offered here, or null. A filter on the LAN (Pi-hole / AdGuard: a private resolver
    /// that is not the gateway), DoH, a VPN or an organisation's domain means the current DNS is deliberate.
    /// </summary>
    public static string? SkipReason(NetSnapshot s)
    {
        if (s.ViaVpn) return Loc.T("трафик идёт через VPN — у него свой DNS");
        if (s.DohConfigured) return Loc.T("настроено шифрование DNS (DoH)");
        if (!string.IsNullOrWhiteSpace(s.Domain)) return Loc.T("компьютер в домене организации — у неё свой DNS");
        foreach (var server in s.DnsServers)
        {
            if (!IPAddress.TryParse(server, out var ip)) continue;
            if (PublicResolvers.Contains(ip.ToString())) return Loc.T("уже используется публичный DNS");
            if (IsPrivate(ip) && !string.Equals(ip.ToString(), s.Gateway, StringComparison.OrdinalIgnoreCase))
                return Loc.T("DNS-сервер в вашей сети — похоже на фильтр рекламы или родительский контроль");
        }
        return null;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
    }

    /// <summary>
    /// The Optimize tweak to offer (dns-cloudflare / dns-google) and the measured reason, or null. Only the faster of the
    /// two, and only when it beats the current resolver by more than 20 ms AND is more than twice as fast, or the current
    /// one times out on ≥ 5 % of the queries while that one does not (research network_measure.md F19).
    /// </summary>
    public static (string TweakId, string Reason)? Offer(DnsBenchResult r)
    {
        if (r.SkipReason is not null) return null;
        if (r.Current is not { } cur) return null;
        var candidates = r.Servers
            .Where(s => !s.IsCurrent && s.Answered && s.Address is Cloudflare or Google)
            .OrderBy(s => s.MedianMs)
            .ToList();
        if (candidates.Count == 0) return null;
        var best = candidates[0];
        var bestMs = best.MedianMs!.Value;
        var faster = cur.MedianMs is { } curMs && bestMs + 20 < curMs && bestMs * 2 < curMs;
        var curFails = cur.TimeoutPercent >= 5 && best.TimeoutPercent < 1 && (cur.MedianMs is null || bestMs <= cur.MedianMs);
        if (!faster && !curFails) return null;
        var id = best.Address == Cloudflare ? "dns-cloudflare" : "dns-google";
        var reason = faster
            ? Loc.Tf("{0} отвечает за {1}, ваш DNS — за {2}. На пинг в играх не влияет", best.Label, NetStats.Ms(bestMs), NetStats.Ms(cur.MedianMs))
            : Loc.Tf("Ваш DNS не ответил на {0} запросов, {1} — на все. На пинг в играх не влияет", NetStats.Percent(cur.TimeoutPercent), best.Label);
        return (id, reason);
    }
}

/// <summary>Times name lookups against each resolver with raw UDP queries (no cache of this PC in the way).</summary>
public static class DnsBench
{
    /// <summary>Names gamers' launchers and sites look up; the first round is discarded (warms the resolver's cache).</summary>
    public static readonly IReadOnlyList<string> Names =
        ["steamcommunity.com", "api.steampowered.com", "www.google.com", "battle.net", "store.epicgames.com", "www.youtube.com"];

    private const int Rounds = 3;

    public static async Task<DnsBenchResult> RunAsync(NetSnapshot s, IProgress<double>? progress, CancellationToken ct)
    {
        var servers = new List<(string Label, string Address, bool Current)>();
        foreach (var d in s.DnsServers.Where(d => IPAddress.TryParse(d, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork).Take(2))
            servers.Add((string.Equals(d, s.Gateway, StringComparison.OrdinalIgnoreCase) ? Loc.Tf("Роутер ({0})", d) : Loc.Tf("Ваш DNS ({0})", d), d, true));
        foreach (var (label, address) in new[] { ("Cloudflare", DnsAdvice.Cloudflare), ("Google", DnsAdvice.Google), ("Quad9", DnsAdvice.Quad9) })
            if (!servers.Any(x => x.Address == address)) servers.Add((label, address, false));

        var done = 0;
        var tasks = servers.Select(async sv =>
        {
            var r = await BenchOneAsync(sv.Label, sv.Address, sv.Current, ct).ConfigureAwait(false);
            progress?.Report(Interlocked.Increment(ref done) / (double)servers.Count);
            return r;
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new DnsBenchResult(results, DateTime.Now, DnsAdvice.SkipReason(s));
    }

    private static async Task<DnsServerResult> BenchOneAsync(string label, string address, bool current, CancellationToken ct)
    {
        var times = new List<double>();
        int timeouts = 0, queries = 0;
        var ip = IPAddress.Parse(address);
        for (var round = 0; round < Rounds; round++)
        {
            foreach (var name in Names)
            {
                ct.ThrowIfCancellationRequested();
                var ms = await QueryAsync(ip, name, ct).ConfigureAwait(false);
                if (round == 0) continue; // warm-up
                queries++;
                if (ms is { } v) times.Add(v);
                else timeouts++;
            }
        }
        return new DnsServerResult(label, address, current, times.Count == 0 ? null : NetStats.Median(times),
            queries == 0 ? 0 : timeouts * 100.0 / queries, queries);
    }

    /// <summary>One A-query; the reply time in ms, or null after 1 s without an answer.</summary>
    internal static async Task<double?> QueryAsync(IPAddress server, string name, CancellationToken ct)
    {
        using var udp = new UdpClient(server.AddressFamily);
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = DnsWire.BuildQuery(id, name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(query, new IPEndPoint(server, 53), timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (DnsWire.ReplyCode(reply.Buffer, id) is not null) return sw.Elapsed.TotalMilliseconds;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
