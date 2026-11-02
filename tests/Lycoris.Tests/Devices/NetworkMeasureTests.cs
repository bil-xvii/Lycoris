using Lycoris.Modules.Devices.Network;

namespace Lycoris.Tests.Devices;

/// <summary>The pure parts of the network measurements: statistics, grades, before/after, DNS wire and offer rule, Valve list, MTU search.</summary>
public sealed class NetworkMeasureTests
{
    // ───────────────────────────── Statistics ─────────────────────────────

    [Fact]
    public void Stats_count_loss_and_use_received_samples_only()
    {
        var s = NetStats.Compute([1.0, null, 3.0, 2.0, null]);
        Assert.Equal(5, s.Sent);
        Assert.Equal(3, s.Received);
        Assert.Equal(2, s.Lost);
        Assert.Equal(40, s.LossPercent, 3);
        Assert.Equal(1.0, s.Min);
        Assert.Equal(2.0, s.P50);
        Assert.Equal(3.0, s.Max);
        Assert.Equal(1.5, s.Jitter!.Value, 3); // |3−1| + |2−3| over 2
    }

    [Fact]
    public void Stats_of_no_replies_and_no_samples()
    {
        Assert.False(NetStats.Compute([null, null]).HasData);
        Assert.Equal(100, NetStats.Compute([null, null]).LossPercent);
        Assert.Equal(0, NetStats.Compute([]).Sent);
    }

    [Fact]
    public void Percentile_interpolates_between_ranks()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(5.5, NetStats.Percentile(sorted, 0.5), 3);
        Assert.Equal(9.55, NetStats.Percentile(sorted, 0.95), 3);
        Assert.Equal(10, NetStats.Percentile(sorted, 1), 3);
    }

    [Theory]
    [InlineData(0.4, "<1 мс")]
    [InlineData(2.43, "2,4 мс")]
    [InlineData(54.2, "54 мс")]
    public void Milliseconds_read_like_the_lan_needs(double v, string expected) => Assert.Equal(expected, NetStats.Ms(v));

    [Theory]
    [InlineData(0.3, "A+")]
    [InlineData(4.9, "A+")]
    [InlineData(5, "A")]
    [InlineData(29.9, "A")]
    [InlineData(30, "B")]
    [InlineData(59, "B")]
    [InlineData(60, "C")]
    [InlineData(199, "C")]
    [InlineData(200, "D")]
    [InlineData(400, "F")]
    public void Bufferbloat_grade_follows_the_waveform_bands(double increase, string grade) => Assert.Equal(grade, NetStats.Grade(increase));

    [Fact]
    public void Grade_severity_b_is_info_c_warning_d_and_f_critical()
    {
        Assert.Equal(NetSeverity.Ok, NetStats.GradeSeverity("A+"));
        Assert.Equal(NetSeverity.Ok, NetStats.GradeSeverity("A"));
        Assert.Equal(NetSeverity.Info, NetStats.GradeSeverity("B"));
        Assert.Equal(NetSeverity.Warning, NetStats.GradeSeverity("C"));
        Assert.Equal(NetSeverity.Critical, NetStats.GradeSeverity("D"));
        Assert.Equal(NetSeverity.Critical, NetStats.GradeSeverity("F"));
    }

    [Fact]
    public void Before_after_needs_a_clear_and_large_enough_change()
    {
        var before = Enumerable.Range(0, 60).Select(i => 20.0 + (i % 5) * 0.2).ToList();
        var faster = Enumerable.Range(0, 60).Select(i => 15.0 + (i % 5) * 0.2).ToList();
        var same = Enumerable.Range(0, 60).Select(i => 20.1 + (i % 5) * 0.2).ToList();
        Assert.Equal(ChangeVerdict.Better, NetStats.CompareMedians(before, faster));
        Assert.Equal(ChangeVerdict.Worse, NetStats.CompareMedians(faster, before));
        Assert.Equal(ChangeVerdict.NoDifference, NetStats.CompareMedians(before, same)); // < 1 ms
        Assert.Equal(ChangeVerdict.NoDifference, NetStats.CompareMedians([1, 2], [5, 6])); // too few samples
    }

    [Fact]
    public void Spread_and_loss_verdicts_ignore_noise()
    {
        Assert.Equal(ChangeVerdict.NoDifference, NetStats.CompareSpread(0.6, 0.5));
        Assert.Equal(ChangeVerdict.Better, NetStats.CompareSpread(6, 2));
        Assert.Equal(ChangeVerdict.Worse, NetStats.CompareSpread(2, 6));
        var clean = NetStats.Compute(Enumerable.Repeat<double?>(2, 60).ToList());
        var oneLost = NetStats.Compute(Enumerable.Repeat<double?>(2, 59).Append(null).ToList());
        var fiveLost = NetStats.Compute(Enumerable.Repeat<double?>(2, 55).Concat(Enumerable.Repeat<double?>(null, 5)).ToList());
        Assert.Equal(ChangeVerdict.NoDifference, NetStats.CompareLoss(clean, oneLost));
        Assert.Equal(ChangeVerdict.Better, NetStats.CompareLoss(fiveLost, clean));
    }

    // ───────────────────────────── DNS ─────────────────────────────

    [Fact]
    public void Dns_query_is_a_standard_recursive_a_question()
    {
        var q = DnsWire.BuildQuery(0x1234, "battle.net");
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, q[..12]);
        Assert.Equal(new byte[] { 6, (byte)'b', (byte)'a', (byte)'t', (byte)'t', (byte)'l', (byte)'e', 3, (byte)'n', (byte)'e', (byte)'t', 0, 0, 1, 0, 1 }, q[12..]);
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(1, "a..b"));
    }

    [Fact]
    public void Dns_reply_must_answer_this_query()
    {
        byte[] reply = [0x12, 0x34, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0];
        Assert.Equal(3, DnsWire.ReplyCode(reply, 0x1234)); // NXDOMAIN still counts as an answer
        Assert.Null(DnsWire.ReplyCode(reply, 0x9999));
        Assert.Null(DnsWire.ReplyCode([0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0], 0x1234)); // a query, not a response
        Assert.Null(DnsWire.ReplyCode([0x12, 0x34], 0x1234));
    }

    private static DnsBenchResult Bench(double? router, double? cloudflare, double? google, double routerTimeouts = 0, string? skip = null) =>
        new([
            new DnsServerResult("Роутер (192.168.1.1)", "192.168.1.1", true, router, routerTimeouts, 12),
            new DnsServerResult("Cloudflare", "1.1.1.1", false, cloudflare, 0, 12),
            new DnsServerResult("Google", "8.8.8.8", false, google, 0, 12),
            new DnsServerResult("Quad9", "9.9.9.9", false, 3, 0, 12),
        ], DateTime.Now, skip);

    [Fact]
    public void Dns_offer_needs_a_big_measured_gain_and_picks_the_faster_provider()
    {
        // This PC (research §2.4): router 1,4 ms, Cloudflare 4,4, Google 51 → nothing to offer.
        Assert.Null(DnsAdvice.Offer(Bench(1.4, 4.4, 51.3)));
        // Slow ISP resolver: 60 ms vs Cloudflare 5 / Google 9 → Cloudflare.
        var offer = DnsAdvice.Offer(Bench(60, 5, 9));
        Assert.Equal("dns-cloudflare", offer?.TweakId);
        Assert.Contains("На пинг в играх не влияет", offer?.Reason);
        Assert.Equal("dns-google", DnsAdvice.Offer(Bench(60, 30, 8))?.TweakId);
        // Faster, but not by 20 ms and twice: no offer.
        Assert.Null(DnsAdvice.Offer(Bench(25, 6, 9)));
        // Timeouts on the current resolver.
        Assert.Equal("dns-cloudflare", DnsAdvice.Offer(Bench(5, 4, 9, routerTimeouts: 8))?.TweakId);
        // Quad9 is information only: never offered.
        Assert.Null(DnsAdvice.Offer(Bench(60, null, null)));
        // A skip reason wins.
        Assert.Null(DnsAdvice.Offer(Bench(60, 5, 9, skip: "VPN")));
    }

    private static NetSnapshot Snap(string[] dns, string? gateway = "192.168.1.1", bool vpn = false, bool doh = false, string? domain = null) =>
        new() { DnsServers = dns, Gateway = gateway, ViaVpn = vpn, DohConfigured = doh, Domain = domain };

    [Fact]
    public void Dns_offer_is_skipped_when_the_current_dns_is_deliberate()
    {
        Assert.Null(DnsAdvice.SkipReason(Snap(["192.168.1.1"])));
        Assert.NotNull(DnsAdvice.SkipReason(Snap(["192.168.1.1"], vpn: true)));
        Assert.NotNull(DnsAdvice.SkipReason(Snap(["192.168.1.1"], doh: true)));
        Assert.NotNull(DnsAdvice.SkipReason(Snap(["10.0.0.5"], domain: "corp.example")));
        Assert.Contains("фильтр", DnsAdvice.SkipReason(Snap(["192.168.1.53"]))); // Pi-hole on the LAN
        Assert.NotNull(DnsAdvice.SkipReason(Snap(["8.8.8.8"])));
        Assert.Null(DnsAdvice.SkipReason(Snap(["203.0.113.53"]))); // the ISP's resolver
    }

    // ───────────────────────────── Valve SDR ─────────────────────────────

    [Fact]
    public void Valve_relay_list_is_parsed_defensively()
    {
        const string json = """
            {"revision":1,"pops":{
              "fra":{"desc":"Frankfurt (Germany)","geo":[8.6,50.1],"relays":[{"ipv4":"155.133.226.68","port_range":[27015,27060]}]},
              "par":{"desc":"Paris (France)","relays":[{"ipv4":"not-an-ip"},{"ipv4":"155.133.249.98"}]},
              "pw":{"desc":"Perfect World","partners":2},
              "dxb":{"relays":[{"ipv6":"::1"}]},
              "bad":"x"
            },"typical_pings":[]}
            """;
        var pops = ValveRegions.Parse(json);
        Assert.Equal(2, pops.Count);
        Assert.Equal(new SdrPop("fra", "Frankfurt (Germany)", "155.133.226.68"), pops[0]);
        Assert.Equal("155.133.249.98", pops[1].RelayAddress);
        Assert.Equal("Frankfurt", NetFindings.RegionName(pops[0]));
        Assert.Empty(ValveRegions.Parse("{}"));
    }

    // ───────────────────────────── MTU ─────────────────────────────

    private static Func<int, Task<DfReply>> Path(int pathMtu, DfReply above, int floorAnswers = 1172) =>
        payload => Task.FromResult(payload + 28 <= pathMtu && payload >= Math.Min(floorAnswers, payload) ? DfReply.Ok : above);

    [Fact]
    public async Task Mtu_full_path_needs_one_probe()
    {
        var probes = 0;
        var r = await MtuSearch.SearchAsync(1500, p => { probes++; return Task.FromResult(DfReply.Ok); }, CancellationToken.None);
        Assert.Equal(new MtuResult(1500, 1500, false), r);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Mtu_pppoe_path_with_too_big_replies_is_not_a_black_hole()
    {
        var r = await MtuSearch.SearchAsync(1500, Path(1492, DfReply.TooBig), CancellationToken.None);
        Assert.InRange(r.PathMtu!.Value, 1488, 1492);
        Assert.False(r.BlackHole);
    }

    [Fact]
    public async Task Mtu_silent_drops_of_big_packets_are_a_black_hole()
    {
        var r = await MtuSearch.SearchAsync(1500, Path(1400, DfReply.Timeout), CancellationToken.None);
        Assert.InRange(r.PathMtu!.Value, 1396, 1400);
        Assert.True(r.BlackHole);
    }

    [Fact]
    public async Task Mtu_unknown_when_even_small_probes_get_no_answer()
    {
        var r = await MtuSearch.SearchAsync(1500, _ => Task.FromResult(DfReply.Timeout), CancellationToken.None);
        Assert.Null(r.PathMtu);
        Assert.False(r.BlackHole);
    }
}
