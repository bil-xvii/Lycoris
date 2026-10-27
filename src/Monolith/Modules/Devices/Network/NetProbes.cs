using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Monolith.Modules.Devices.Network;

// The measurements that send packets. They run only on a click (never in a scan or the self-test), off the UI thread,
// and stop on the token. ICMP is gentle: at most 2 echoes per second per target (5 during the load test, one target),
// because faster probing measures the targets' ICMP rate limits, not the line (research network_adapter.md §1).

/// <summary>ICMP echo with Stopwatch timing (PingReply.RoundtripTime has whole-millisecond resolution).</summary>
public static class NetPing
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);
    private static readonly byte[] Payload = new byte[32];

    /// <summary>Pings every target every <paramref name="interval"/> for <paramref name="duration"/> (staggered starts).</summary>
    public static async Task<IReadOnlyList<NetTargetResult>> RunAsync(IReadOnlyList<NetTarget> targets, TimeSpan duration, TimeSpan interval,
        Action<double>? progress, CancellationToken ct)
    {
        var count = Math.Max(1, (int)(duration.TotalMilliseconds / interval.TotalMilliseconds));
        var started = Stopwatch.StartNew();
        using var ticker = progress is null ? null : new Timer(_ =>
        {
            try { progress(Math.Min(1, started.Elapsed.TotalMilliseconds / duration.TotalMilliseconds)); }
            catch { /* progress is best-effort */ }
        }, null, 250, 250);
        var runs = targets.Select((t, i) => RunOneAsync(t, count, interval, TimeSpan.FromMilliseconds(interval.TotalMilliseconds * i / Math.Max(1, targets.Count)), ct));
        return await Task.WhenAll(runs).ConfigureAwait(false);
    }

    private static async Task<NetTargetResult> RunOneAsync(NetTarget target, int count, TimeSpan interval, TimeSpan offset, CancellationToken ct)
    {
        var samples = new List<double?>(count);
        if (!IPAddress.TryParse(target.Address, out var ip)) return new NetTargetResult(target, samples);
        using var ping = new Ping();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var due = offset + interval * i;
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            samples.Add(await OnceAsync(ping, ip, 1000, Payload).ConfigureAwait(false));
        }
        return new NetTargetResult(target, samples);
    }

    /// <summary>One echo; the round trip in ms, or null when there was no reply.</summary>
    public static async Task<double?> OnceAsync(Ping ping, IPAddress ip, int timeoutMs, byte[] buffer, PingOptions? options = null)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var reply = await (options is null ? ping.SendPingAsync(ip, timeoutMs, buffer) : ping.SendPingAsync(ip, timeoutMs, buffer, options)).ConfigureAwait(false);
            var elapsed = sw.Elapsed.TotalMilliseconds;
            return reply.Status == IPStatus.Success ? Math.Max(0.01, elapsed) : null;
        }
        catch (PingException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The probe set of «Замер пинга»: the router, Cloudflare and Google (IPv4), and both over IPv6 when it works.</summary>
    public static IReadOnlyList<NetTarget> Targets(NetSnapshot s)
    {
        var list = new List<NetTarget>();
        if (s.Gateway is { } gw && IPAddress.TryParse(gw, out _)) list.Add(new NetTarget("Роутер", gw, NetTargetRole.Gateway));
        list.Add(new NetTarget("Cloudflare", "1.1.1.1", NetTargetRole.Internet));
        list.Add(new NetTarget("Google", "8.8.8.8", NetTargetRole.Internet));
        if (s.HasIpv6)
        {
            list.Add(new NetTarget("Cloudflare IPv6", "2606:4700:4700::1111", NetTargetRole.Internet, Ipv6: true));
            list.Add(new NetTarget("Google IPv6", "2001:4860:4860::8888", NetTargetRole.Internet, Ipv6: true));
        }
        return list;
    }
}

/// <summary>What a Don't-Fragment echo got back.</summary>
public enum DfReply { Ok, TooBig, Timeout }

/// <summary>Path MTU by Don't-Fragment echoes (finding F20: only a black hole is worth reporting; MONOLITH never changes the MTU).</summary>
public static class MtuSearch
{
    private const int Overhead = 28; // IPv4 header 20 + ICMP 8
    private const int Floor = 1200;

    /// <summary>
    /// Binary search between 1200 and the interface MTU with two tries per size. A black hole = big probes vanish
    /// (time out) while small ones pass and no «packet too big» ever comes back. Pure over <paramref name="probe"/>.
    /// </summary>
    public static async Task<MtuResult> SearchAsync(int interfaceMtu, Func<int, Task<DfReply>> probe, CancellationToken ct)
    {
        var mtu = interfaceMtu is >= Floor and <= 9000 ? interfaceMtu : 1500;
        var tooBigSeen = false;

        async Task<DfReply> Try(int payload)
        {
            var result = DfReply.Timeout;
            for (var i = 0; i < 2; i++)
            {
                ct.ThrowIfCancellationRequested();
                var r = await probe(payload).ConfigureAwait(false);
                if (r == DfReply.Ok) return DfReply.Ok;
                if (r == DfReply.TooBig) { tooBigSeen = true; result = DfReply.TooBig; }
            }
            return result;
        }

        var hi = mtu - Overhead;
        if (await Try(hi).ConfigureAwait(false) == DfReply.Ok) return new MtuResult(mtu, mtu, false);
        var lo = Floor - Overhead;
        if (await Try(lo).ConfigureAwait(false) != DfReply.Ok) return new MtuResult(mtu, null, false); // the target does not answer: unknown
        while (hi - lo > 4)
        {
            var mid = (lo + hi) / 2;
            if (await Try(mid).ConfigureAwait(false) == DfReply.Ok) lo = mid;
            else hi = mid;
        }
        return new MtuResult(mtu, lo + Overhead, !tooBigSeen);
    }

    /// <summary>The real probe: an echo of <paramref name="payload"/> bytes with DF set, at most 2 per second.</summary>
    public static async Task<MtuResult> RunAsync(IPAddress target, int interfaceMtu, CancellationToken ct)
    {
        using var ping = new Ping();
        var options = new PingOptions(64, dontFragment: true);
        var last = Stopwatch.StartNew();
        var first = true;
        return await SearchAsync(interfaceMtu, async payload =>
        {
            if (!first)
            {
                var wait = NetPing.Interval - last.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            first = false;
            last.Restart();
            try
            {
                var reply = await ping.SendPingAsync(target, 1000, new byte[payload], options).ConfigureAwait(false);
                return reply.Status switch
                {
                    IPStatus.Success => DfReply.Ok,
                    IPStatus.PacketTooBig => DfReply.TooBig,
                    _ => DfReply.Timeout,
                };
            }
            catch (PingException)
            {
                return DfReply.Timeout;
            }
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Latency under load (bufferbloat): an idle baseline, then parallel HTTPS downloads from Cloudflare's public speed
/// endpoint into memory (nothing is saved, nothing is uploaded, results are never reported anywhere) while pinging 1.1.1.1.
/// </summary>
public static class LoadTest
{
    public const string Host = "speed.cloudflare.com";
    /// <summary>Chunks stay under 100 MB: the endpoint answers 403 from 1e8 bytes (research §2.3).</summary>
    public const long ChunkBytes = 25_000_000;
    public const int Streams = 6;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(8);

    public static async Task<LoadTestResult> RunAsync(long capBytes, Func<NetInterfaceCounters?> counters, Action<double, string> progress, CancellationToken ct)
    {
        var target = IPAddress.Parse("1.1.1.1");
        using var ping = new Ping();
        var payload = new byte[32];

        // 1. Idle baseline: 5 s at 5 echoes per second.
        var idle = new List<double>();
        for (var i = 0; i < 25; i++)
        {
            ct.ThrowIfCancellationRequested();
            var started = Stopwatch.StartNew();
            if (await NetPing.OnceAsync(ping, target, 1000, payload).ConfigureAwait(false) is { } ms) idle.Add(ms);
            progress(0.3 * (i + 1) / 25, "Пинг без нагрузки…");
            var rest = TimeSpan.FromMilliseconds(200) - started.Elapsed;
            if (rest > TimeSpan.Zero) await Task.Delay(rest, ct).ConfigureAwait(false);
        }
        if (idle.Count < 5) throw new InvalidOperationException("1.1.1.1 не отвечает на ping — тест под нагрузкой сейчас невозможен.");

        // 2. Download while pinging.
        var before = counters();
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Streams,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MONOLITH");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long total = 0;
        var active = 0;
        string? refused = null;
        var clock = Stopwatch.StartNew();

        async Task Worker()
        {
            var buffer = new byte[81920];
            while (!stop.IsCancellationRequested && Interlocked.Read(ref total) < capBytes && clock.Elapsed < MaxDuration)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/__down?bytes={ChunkBytes}");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode is 403 or 429)
                {
                    refused = $"сервер ответил {(int)resp.StatusCode}";
                    stop.Cancel();
                    return;
                }
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref active);
                try
                {
                    int n;
                    while ((n = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
                    {
                        if (Interlocked.Add(ref total, n) >= capBytes || clock.Elapsed >= MaxDuration)
                        {
                            stop.Cancel();
                            break;
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        }

        var workers = Enumerable.Range(0, Streams).Select(_ => Task.Run(Worker, CancellationToken.None)).ToList();
        var loaded = new List<double>();
        while (!stop.IsCancellationRequested && workers.Any(w => !w.IsCompleted))
        {
            var tick = Stopwatch.StartNew();
            if (clock.Elapsed > TimeSpan.FromMilliseconds(500) && Volatile.Read(ref active) >= Math.Min(4, Streams))
                if (await NetPing.OnceAsync(ping, target, 1000, payload).ConfigureAwait(false) is { } ms) loaded.Add(ms);
            var share = Math.Max(Interlocked.Read(ref total) / (double)capBytes, clock.Elapsed / MaxDuration);
            progress(0.3 + 0.7 * Math.Min(1, share), $"Загрузка: {Interlocked.Read(ref total) / 1_000_000} МБ…");
            var rest = TimeSpan.FromMilliseconds(200) - tick.Elapsed;
            if (rest > TimeSpan.Zero)
            {
                try { await Task.Delay(rest, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        stop.Cancel();
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (HttpRequestException) when (Interlocked.Read(ref total) > 0) { }
        ct.ThrowIfCancellationRequested();
        var seconds = clock.Elapsed.TotalSeconds;
        var after = counters();

        if (refused is not null && total == 0) throw new InvalidOperationException($"Тест под нагрузкой сейчас недоступен ({refused}).");
        if (loaded.Count < 5)
            throw new InvalidOperationException(total == 0
                ? "Не удалось начать загрузку с speed.cloudflare.com — проверьте подключение."
                : "Загрузка закончилась слишком быстро для замера — выберите больший объём.");

        return new LoadTestResult
        {
            At = DateTime.Now, IdleMedian = NetStats.Median(idle), LoadedMedian = NetStats.Median(loaded), Mbps = total * 8 / 1_000_000.0 / Math.Max(0.1, seconds),
            Bytes = total, LoadedSamples = loaded.Count,
            InErrorsDelta = after?.InErrors - before?.InErrors, InDiscardsDelta = after?.InDiscards - before?.InDiscards,
            InPacketsDelta = after?.InPackets - before?.InPackets,
        };
    }
}

/// <summary>Interface counters (GetIfEntry2 through IPInterfaceStatistics; no admin).</summary>
public sealed record NetInterfaceCounters(long BytesIn, long BytesOut, long InPackets, long InErrors, long InDiscards);

/// <summary>Valve's public relay list (Steam Datagram Relay) and the ICMP distance to each region.</summary>
public static class ValveRegions
{
    public const string ConfigUrl = "https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730";

    /// <summary>The POPs that list a relay with an IPv4 address (the schema is undocumented: parsed defensively). Pure.</summary>
    public static IReadOnlyList<SdrPop> Parse(string json)
    {
        var list = new List<SdrPop>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("pops", out var pops) || pops.ValueKind != JsonValueKind.Object) return list;
        foreach (var pop in pops.EnumerateObject())
        {
            if (pop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!pop.Value.TryGetProperty("relays", out var relays) || relays.ValueKind != JsonValueKind.Array) continue;
            string? address = null;
            foreach (var relay in relays.EnumerateArray())
            {
                if (relay.ValueKind == JsonValueKind.Object && relay.TryGetProperty("ipv4", out var ip) && ip.ValueKind == JsonValueKind.String
                    && IPAddress.TryParse(ip.GetString(), out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    address = parsed.ToString();
                    break;
                }
            }
            if (address is null) continue;
            var desc = pop.Value.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (pop.Name.Length is 0 or > 12) continue;
            list.Add(new SdrPop(pop.Name, string.IsNullOrWhiteSpace(desc) ? pop.Name : desc!.Trim(), address));
        }
        return list;
    }

    public static async Task<IReadOnlyList<SdrPop>> FetchAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MONOLITH");
        var json = await http.GetStringAsync(ConfigUrl, ct).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>4 echoes per region, at most 5 regions at a time at 2 echoes per second each (≤ 10 per second in total).</summary>
    public static async Task<SdrResult> MeasureAsync(IReadOnlyList<SdrPop> pops, Action<double>? progress, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(5);
        var done = 0;
        var tasks = pops.Select(async pop =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var r = await NetPing.RunAsync([new NetTarget(pop.Description, pop.RelayAddress, NetTargetRole.Internet)],
                    TimeSpan.FromSeconds(2), NetPing.Interval, null, ct).ConfigureAwait(false);
                progress?.Invoke(Interlocked.Increment(ref done) / (double)pops.Count);
                return new SdrRegion(pop, r[0].Stats);
            }
            finally
            {
                gate.Release();
            }
        });
        return new SdrResult(await Task.WhenAll(tasks).ConfigureAwait(false), DateTime.Now);
    }
}
