using Lycoris.Core.Devices;
using Lycoris.Modules.Devices;
using Lycoris.Modules.Devices.Network;
using Lycoris.Tweaks;

namespace Lycoris.Tests.Devices;

/// <summary>
/// Findings (research network_measure.md F1–F22 with the skeptic's gates) and the «Сеть» cards built from a snapshot and
/// stored results: what is planned, what is only advice, what appears only when its condition was measured.
/// </summary>
public sealed class NetworkFindingsTests
{
    private static readonly Guid EthId = new("21ca98f6-bc9b-4db8-bc1f-2595dcb69e43");
    private static readonly Guid WifiId = new("cfcf9fba-bc4a-4947-a5f9-f25071b19244");

    private static NicPropertyInfo Prop(string kw, string? value, string? def, params string[] valid) => new(kw, value, valid, def);

    private static NicPropertyInfo SpeedDuplex(string value) => Prop("*SpeedDuplex", value, "0", "0", "1", "2", "3", "4", "6", "2500");

    private static NetAdapterInfo Eth(string sd = "0", bool connected = true, long speed = 1_000_000_000, bool? full = true, int? drops = 0,
        params NicPropertyInfo[] extra) => new()
    {
        InterfaceGuid = EthId, Name = "Ethernet", Description = "Realtek(R) PCI(e) Ethernet Controller", Medium = NetMedium.Ethernet,
        Connected = connected, SpeedBps = speed, FullDuplex = full, LinkDrops = drops, DriverProvider = "Microsoft", DriverVersion = "9.1.412.2015",
        DriverDate = "2015-03-31", Properties = [SpeedDuplex(sd), .. extra],
    };

    private static NetAdapterInfo Wifi(bool connected = false) => new()
    {
        InterfaceGuid = WifiId, Name = "Беспроводная сеть", Description = "Qualcomm FastConnect 7800 Wi-Fi 7 High Band Simultaneous (HBS) Network Adapter",
        Medium = NetMedium.WiFi, Connected = connected, RadioOff = !connected,
    };

    /// <summary>The owner's PC: wired 1 Gbit/s full duplex, Wi-Fi radio off, router DNS, IPv6.</summary>
    private static NetSnapshot Owner(params NetAdapterInfo[] adapters) => new()
    {
        Adapters = adapters.Length > 0 ? adapters : [Eth(), Wifi()], BestInterface = EthId, Gateway = "192.168.1.1", HasIpv6 = true,
        DnsServers = ["192.168.1.1"], Mtu = 1500, Fingerprint = "f0", SteamInstalled = true,
    };

    private static NetTargetResult Target(string label, string addr, NetTargetRole role, double ms, int lost = 0, int sent = 60, bool v6 = false, double spread = 0.2) =>
        new(new NetTarget(label, addr, role, v6),
            Enumerable.Range(0, sent).Select(i => i < lost ? (double?)null : ms + (i % 3) * spread).ToList());

    private static NetCheckResult Check(double gw = 1.0, int gwLost = 0, double cf = 2.4, int cfLost = 0, double g = 3.8, int gLost = 0,
        double? cf6 = null, double? g6 = null, bool wifi = false, TrafficInfo? traffic = null, WifiLink? link = null, MtuResult? mtu = null,
        string? tcpGame = null, string fingerprint = "f0", double gwSpread = 0.2)
    {
        var targets = new List<NetTargetResult>
        {
            Target("Роутер", "192.168.1.1", NetTargetRole.Gateway, gw, gwLost, spread: gwSpread),
            Target("Cloudflare", "1.1.1.1", NetTargetRole.Internet, cf, cfLost),
            Target("Google", "8.8.8.8", NetTargetRole.Internet, g, gLost),
        };
        if (cf6 is { } a) targets.Add(Target("Cloudflare IPv6", "2606:4700:4700::1111", NetTargetRole.Internet, a, v6: true));
        if (g6 is { } b) targets.Add(Target("Google IPv6", "2001:4860:4860::8888", NetTargetRole.Internet, b, v6: true));
        return new NetCheckResult
        {
            At = DateTime.Now, Duration = TimeSpan.FromSeconds(30), Targets = targets, OnWifi = wifi, Traffic = traffic, Wifi = link, Mtu = mtu,
            TcpGame = tcpGame, Fingerprint = fingerprint,
        };
    }

    private static IReadOnlyList<string> Ids(IEnumerable<NetFinding> f) => f.Select(x => x.Id).ToList();

    // ───────────────────────────── Findings ─────────────────────────────

    [Fact]
    public void Healthy_pc_has_no_findings()
    {
        Assert.Empty(NetFindings.ForSnapshot(Owner()));
        Assert.Empty(NetFindings.ForCheck(Check(cf6: 2.2, g6: 3.2, mtu: new MtuResult(1500, 1500, false),
            traffic: new TrafficInfo { DownMbps = 0.4, UpMbps = 4.4, InErrors = 0, InPackets = 50_000 })));
    }

    [Fact]
    public void Lan_loss_needs_two_lost_echoes_and_three_percent()
    {
        Assert.DoesNotContain("lan-loss", Ids(NetFindings.ForCheck(Check(gwLost: 1))));
        var f = NetFindings.ForCheck(Check(gwLost: 2)).Single(x => x.Id == "lan-loss");
        Assert.Equal(NetSeverity.Warning, f.Severity);
        Assert.Equal(NetSeverity.Critical, NetFindings.ForCheck(Check(gwLost: 4)).Single(x => x.Id == "lan-loss").Severity);
        Assert.Contains("кабель", NetFindings.ForCheck(Check(gwLost: 2)).Single(x => x.Id == "lan-loss").Reason);
        Assert.Contains("5 ГГц", NetFindings.ForCheck(Check(gwLost: 2, wifi: true)).Single(x => x.Id == "lan-loss").Reason);
    }

    [Fact]
    public void Router_spikes_count_on_cable_only_when_they_reach_the_internet()
    {
        // Router slow-path ICMP: spikes to the router only (research §1.6) — not a finding.
        var routerOnly = Check() with
        {
            Targets =
            [
                new(new NetTarget("Роутер", "192.168.1.1", NetTargetRole.Gateway), Enumerable.Range(0, 60).Select(i => (double?)(i % 10 == 0 ? 20 : 1)).ToList()),
                .. Check().Targets.Skip(1),
            ],
        };
        Assert.True(routerOnly.Gateway!.Stats.P95 > 10);
        Assert.DoesNotContain("lan-spikes", Ids(NetFindings.ForCheck(routerOnly)));
        var propagated = Check() with
        {
            Targets =
            [
                routerOnly.Targets[0],
                new(new NetTarget("Cloudflare", "1.1.1.1", NetTargetRole.Internet), Enumerable.Range(0, 60).Select(i => (double?)(i % 10 == 0 ? 22 : 2.4)).ToList()),
                .. Check().Targets.Skip(2),
            ],
        };
        Assert.Contains("lan-spikes", Ids(NetFindings.ForCheck(propagated)));
        Assert.Contains("lan-spikes", Ids(NetFindings.ForCheck(Check(gw: 3))));
        Assert.Contains("lan-spikes", Ids(NetFindings.ForCheck(Check(gw: 6, wifi: true))));
        Assert.DoesNotContain("lan-spikes", Ids(NetFindings.ForCheck(Check(gw: 3, wifi: true))));
    }

    [Fact]
    public void Isp_loss_needs_two_internet_targets_and_a_clean_router()
    {
        Assert.DoesNotContain("isp-loss", Ids(NetFindings.ForCheck(Check(cfLost: 3)))); // one anycast target = its own policy
        Assert.Contains("isp-loss", Ids(NetFindings.ForCheck(Check(cfLost: 3, gLost: 2))));
        Assert.DoesNotContain("isp-loss", Ids(NetFindings.ForCheck(Check(gwLost: 3, cfLost: 3, gLost: 3)))); // that is the LAN
    }

    [Fact]
    public void High_base_latency_and_isp_jitter_are_information()
    {
        var f = NetFindings.ForCheck(Check(cf: 62, g: 70)).Single(x => x.Id == "high-base");
        Assert.Equal(NetSeverity.Info, f.Severity);
        Assert.Contains("isp-jitter", Ids(NetFindings.ForCheck(new NetCheckResult
        {
            At = DateTime.Now, Duration = TimeSpan.FromSeconds(30),
            Targets =
            [
                Target("Роутер", "192.168.1.1", NetTargetRole.Gateway, 1),
                new(new NetTarget("Cloudflare", "1.1.1.1", NetTargetRole.Internet), Enumerable.Range(0, 60).Select(i => (double?)(i % 2 == 0 ? 5 : 30)).ToList()),
                Target("Google", "8.8.8.8", NetTargetRole.Internet, 40),
            ],
        })));
    }

    [Fact]
    public void Receive_errors_count_but_discards_do_not()
    {
        Assert.Contains("rx-errors", Ids(NetFindings.ForCheck(Check(traffic: new TrafficInfo { InErrors = 5, InPackets = 100_000 }))));
        Assert.DoesNotContain("rx-errors", Ids(NetFindings.ForCheck(Check(traffic: new TrafficInfo { InErrors = 0, InDiscards = 12_411, InPackets = 100_000 }))));
    }

    [Fact]
    public void Background_downloads_name_the_busiest_programs_and_windows_update()
    {
        var f = NetFindings.ForCheck(Check(traffic: new TrafficInfo
        {
            DownMbps = 80, UpMbps = 1, WindowsUpdateJobs = 2, HasBytes = true,
            Top = [new ProcessTraffic("Steam", 4, 250_000_000), new ProcessTraffic("Discord", 3, 1_000_000)],
        })).Single(x => x.Id == "background");
        Assert.Contains("Steam", f.Reason);
        Assert.Contains("Windows Update", f.Reason);
        Assert.Equal(DeviceLinkKind.Settings, f.Link?.Kind);
        Assert.Null(DeviceApply.ValidateLink(f.Link!));
    }

    [Fact]
    public void Wifi_signal_and_band_findings()
    {
        Assert.Equal(NetSeverity.Info, NetFindings.ForCheck(Check(wifi: true, link: new WifiLink(-70, 36, null))).Single(x => x.Id == "wifi-weak").Severity);
        Assert.Equal(NetSeverity.Warning, NetFindings.ForCheck(Check(wifi: true, link: new WifiLink(-80, 36, null))).Single(x => x.Id == "wifi-weak").Severity);
        Assert.Contains("wifi-24", Ids(NetFindings.ForCheck(Check(wifi: true, link: new WifiLink(-55, 6, null)))));
        Assert.Empty(NetFindings.ForCheck(Check(wifi: true, link: new WifiLink(-55, 149, null))));
        var gated = NetFindings.ForCheck(Check(wifi: true, link: new WifiLink(null, null, "нет доступа"))).Single(x => x.Id == "wifi-location");
        Assert.Null(DeviceApply.ValidateLink(gated.Link!));
    }

    [Fact]
    public void Mtu_is_reported_only_as_a_black_hole()
    {
        Assert.DoesNotContain("mtu-blackhole", Ids(NetFindings.ForCheck(Check(mtu: new MtuResult(1500, 1492, false)))));
        Assert.Contains("mtu-blackhole", Ids(NetFindings.ForCheck(Check(mtu: new MtuResult(1500, 1400, true)))));
    }

    [Fact]
    public void Bufferbloat_and_receive_buffers_under_load()
    {
        LoadTestResult Load(double idle, double loaded, long packets = 0, long discards = 0) => new()
        {
            At = DateTime.Now, IdleMedian = idle, LoadedMedian = loaded, InPacketsDelta = packets, InDiscardsDelta = discards, InErrorsDelta = 0,
        };
        Assert.Null(NetFindings.LoadFinding(Load(2.4, 2.7)));
        Assert.Equal(NetSeverity.Info, NetFindings.LoadFinding(Load(10, 50))!.Severity);
        Assert.Equal(NetSeverity.Warning, NetFindings.LoadFinding(Load(10, 130))!.Severity);
        Assert.Contains("SQM", NetFindings.LoadFinding(Load(10, 600))!.Reason);

        var low = Eth(extra: Prop("*ReceiveBuffers", "256", "512", "max:2048"));
        Assert.NotNull(NetFindings.BuffersFinding(Load(2, 3, packets: 100_000, discards: 2_000), low));
        Assert.Null(NetFindings.BuffersFinding(Load(2, 3, packets: 100_000, discards: 100), low)); // under 0,5 %
        Assert.Null(NetFindings.BuffersFinding(Load(2, 3, packets: 100_000, discards: 2_000), Eth(extra: Prop("*ReceiveBuffers", "512", "512", "max:512"))));
    }

    [Fact]
    public void Ipv6_penalty_needs_both_pairs_to_be_worse()
    {
        Assert.Null(NetFindings.Ipv6Penalty(Check(cf6: 2.2, g6: 3.2)));
        Assert.Null(NetFindings.Ipv6Penalty(Check(cf6: 20, g6: 3.2)));
        Assert.Equal(15, NetFindings.Ipv6Penalty(Check(cf: 2, g: 4, cf6: 17, g6: 19))!.Value, 0);
        Assert.Null(NetFindings.Ipv6Penalty(Check())); // no IPv6 measured
    }

    [Fact]
    public void Snapshot_findings_cable_vpn_and_link()
    {
        var onWifi = Owner(Eth(connected: false), Wifi(connected: true)) with { BestInterface = WifiId };
        Assert.Contains("wifi-instead-of-cable", Ids(NetFindings.ForSnapshot(onWifi)));
        Assert.Contains("vpn", Ids(NetFindings.ForSnapshot(Owner() with { ViaVpn = true, BestDescription = "WireGuard Tunnel" })));

        Assert.DoesNotContain("link-drops", Ids(NetFindings.ForSnapshot(Owner(Eth(drops: 1)))));
        var drops = NetFindings.ForSnapshot(Owner(Eth(drops: 3))).Single(f => f.Id == "link-drops");
        Assert.Equal(EthId, drops.Adapter);
        Assert.Equal(DeviceLinkKind.Navigate, drops.Link?.Kind); // the driver comes first (skeptic)
        Assert.Contains("драйвер", drops.Reason);

        Assert.Contains("half-duplex", Ids(NetFindings.ForSnapshot(Owner(Eth(full: false)))));
        Assert.DoesNotContain("half-duplex", Ids(NetFindings.ForSnapshot(Owner(Eth(sd: "4", full: false))))); // forced: the plan repairs it
        Assert.Contains("slow-link", Ids(NetFindings.ForSnapshot(Owner(Eth(speed: 100_000_000)))));
        Assert.DoesNotContain("slow-link", Ids(NetFindings.ForSnapshot(Owner(Eth(sd: "4", speed: 100_000_000)))));
        Assert.Contains("duplex-regression", Ids(NetFindings.ForSnapshot(Owner(Eth(full: false)), _ => true)));
    }

    [Theory]
    [InlineData("0", true, false)]
    [InlineData("1", true, true)]
    [InlineData("4", true, true)]
    [InlineData("6", true, false)]    // forced 1G full duplex cannot mismatch (and may be the I225 workaround)
    [InlineData("2500", true, false)]
    [InlineData("6", false, true)]    // …unless the link is measured half duplex
    public void Speed_duplex_repair_only_for_forced_10_100_or_measured_half_duplex(string value, bool fullDuplex, bool expected) =>
        Assert.Equal(expected, NetFindings.SpeedDuplexNeedsRepair(Eth(sd: value, full: fullDuplex)));

    [Fact]
    public void Speed_duplex_repair_needs_auto_among_the_valid_values()
    {
        var a = Eth() with { Properties = [Prop("*SpeedDuplex", "4", "4", "1", "2", "3", "4")] };
        Assert.False(NetFindings.SpeedDuplexNeedsRepair(a));
    }

    // ───────────────────────────── Cards ─────────────────────────────

    private static DeviceSetting Row(DeviceCard c, string id) => c.Settings.Single(s => s.Id == id);

    [Fact]
    public void Before_a_measurement_the_plan_asks_for_one_and_offers_no_dns()
    {
        var cards = NetworkCards.Build(Owner(), new DeviceResultStore());
        var conn = cards.Single(c => c.Key == NetworkCards.ConnectionKey);
        Assert.Equal(3, cards.Count); // connection + Ethernet + Wi-Fi
        Assert.All(cards, c => Assert.Equal(DeviceKind.Network, c.Kind));

        var ping = Row(conn, "net-ping");
        Assert.True(ping.Recommended!.NeedsMeasurement);
        Assert.Equal(DeviceLinkKind.Tool, ping.Recommended.Link!.Kind);
        Assert.Equal("net-ping", ping.Recommended.Link.FocusId);
        Assert.Equal(RecommendPolicy.None, Row(conn, "tweak:dns-cloudflare").TweakPolicy);
        Assert.Equal(RecommendPolicy.None, Row(conn, "tweak:dns-google").TweakPolicy);
        Assert.Equal(RecommendPolicy.None, Row(conn, "tweak:wifi-power-saving-off").TweakPolicy); // Wi-Fi present but not used
        Assert.Null(Row(conn, "tweak:tcp-autotuning-normal").TweakPolicy); // Safe → Include when it is not in the default state
        Assert.DoesNotContain(conn.Settings, s => s.Id is "tweak:tcp-ack-frequency" or "tweak:ipv6-prefer-ipv4");
        Assert.DoesNotContain(conn.Settings, s => s.Id == "tweak:network-throttling-off"); // ADVANCED without a measurable condition
        Assert.DoesNotContain(conn.Settings, s => s.Id.StartsWith("finding:", StringComparison.Ordinal));
        Assert.All(conn.Settings.Where(s => s.Tool is not null), s => Assert.False(s.Tool!.IsRunning)); // nothing starts by itself
    }

    [Fact]
    public void Every_row_explains_itself_briefly()
    {
        var results = new DeviceResultStore();
        results.Set(NetKeys.Check, Check(gwLost: 3, cf6: 20, g6: 22, tcpGame: "AION 2"));
        var cards = NetworkCards.Build(Owner(Eth(sd: "4", drops: 4, extra: [Prop("*EEE", "1", "1", "0", "1")]), Wifi()), results);
        foreach (var s in cards.SelectMany(c => c.Settings))
        {
            Assert.Null(DeviceScan.HintProblem(s));
            Assert.InRange(s.Hint.Length, 1, 120);
        }
    }

    [Fact]
    public void Wifi_power_saving_is_planned_only_on_wifi()
    {
        var onWifi = Owner(Eth(connected: false), Wifi(connected: true)) with { BestInterface = WifiId };
        var conn = NetworkCards.BuildConnection(onWifi, new DeviceResultStore());
        Assert.Null(Row(conn, "tweak:wifi-power-saving-off").TweakPolicy); // Safe → Include
        var noWifi = NetworkCards.BuildConnection(Owner(Eth()), new DeviceResultStore());
        Assert.DoesNotContain(noWifi.Settings, s => s.Id == "tweak:wifi-power-saving-off");
    }

    [Fact]
    public void Dns_is_offered_unchecked_only_after_a_benchmark_shows_a_gain()
    {
        var results = new DeviceResultStore();
        results.Set(NetKeys.Dns, new DnsBenchResult([
            new DnsServerResult("Роутер (192.168.1.1)", "192.168.1.1", true, 60, 0, 12),
            new DnsServerResult("Cloudflare", "1.1.1.1", false, 5, 0, 12),
            new DnsServerResult("Google", "8.8.8.8", false, 9, 0, 12),
        ], DateTime.Now));
        var conn = NetworkCards.BuildConnection(Owner(), results);
        var cf = Row(conn, "tweak:dns-cloudflare");
        Assert.Equal(RecommendPolicy.Suggest, cf.TweakPolicy);
        Assert.Contains("На пинг в играх не влияет", cf.Hint);
        Assert.Equal(RecommendPolicy.None, Row(conn, "tweak:dns-google").TweakPolicy);

        // The same numbers behind a VPN: shown, never offered.
        Assert.Equal(RecommendPolicy.None, Row(NetworkCards.BuildConnection(Owner() with { ViaVpn = true }, results), "tweak:dns-cloudflare").TweakPolicy);
    }

    [Fact]
    public void Advanced_rows_appear_only_when_their_condition_was_measured()
    {
        var results = new DeviceResultStore();
        results.Set(NetKeys.Check, Check(cf: 2, g: 4, cf6: 17, g6: 19, tcpGame: "Tibia"));
        var conn = NetworkCards.BuildConnection(Owner(), results);
        var ack = Row(conn, "tweak:tcp-ack-frequency");
        var v4 = Row(conn, "tweak:ipv6-prefer-ipv4");
        Assert.Contains("Tibia", ack.Hint);
        Assert.True(ack.IsReadOnly && v4.IsReadOnly); // «В ОПТИМИЗАЦИИ», never a second toggle
        Assert.Equal(TweakRisk.Advanced, TweakCatalog.Find("tcp-ack-frequency")!.Risk); // DeviceScan.ReadTweakRows never plans Advanced
        Assert.Equal(TweakRisk.Advanced, TweakCatalog.Find("ipv6-prefer-ipv4")!.Risk);
    }

    [Fact]
    public void A_change_after_the_measurement_asks_for_a_recheck()
    {
        var results = new DeviceResultStore();
        results.Set(NetKeys.Check, Check(fingerprint: "f0"));
        Assert.Null(Row(NetworkCards.BuildConnection(Owner(), results), "net-ping").Recommended);
        var after = Row(NetworkCards.BuildConnection(Owner() with { Fingerprint = "f1" }, results), "net-ping").Recommended;
        Assert.True(after!.NeedsMeasurement);
        Assert.Contains("было → стало", after.Reason);
    }

    [Fact]
    public void Measured_findings_become_advice_rows_most_severe_first()
    {
        var results = new DeviceResultStore();
        results.Set(NetKeys.Check, Check(gwLost: 4, cf: 62, g: 70));
        results.Set(NetKeys.Load, new LoadTestResult { At = DateTime.Now, IdleMedian = 10, LoadedMedian = 140 });
        var conn = NetworkCards.BuildConnection(Owner(), results);
        var findings = conn.Settings.Where(s => s.Id.StartsWith("finding:", StringComparison.Ordinal)).ToList();
        Assert.Equal("finding:lan-loss", findings[0].Id);
        Assert.Equal("ВАЖНО", findings[0].StateTag);
        Assert.All(findings, f => Assert.Equal(RecommendPolicy.Advice, f.Recommended!.Policy));
        Assert.All(findings, f => Assert.True(f.IsReadOnly));
        Assert.Equal(RecommendPolicy.Advice, Row(conn, "net-load").Recommended!.Policy); // bufferbloat rides on its own row…
        Assert.DoesNotContain(conn.Settings, s => s.Id == "finding:bufferbloat");            // …and is not listed twice
        Assert.StartsWith("Роутер: ", findings[0].Recommended!.Reason);                      // the plan line shows the measured value
        Assert.Contains("Оценка C", Row(conn, "net-load").Recommended!.Reason);
        Assert.True(Row(conn, "net-load").Order > findings[0].Order);                        // advice is listed by severity (critical first)
        Assert.True(Row(conn, "net-load").Order < conn.Settings.Single(s => s.Id == "finding:high-base").Order);
        Assert.Equal(RecommendPolicy.Suggest, Row(conn, "tweak:tcp-autotuning-normal").TweakPolicy); // not pre-checked on a bloated line

        var plan = DevicePlan.Build([conn], interactiveUser: true);
        Assert.All(plan.Where(p => p.Setting.Id.StartsWith("finding:", StringComparison.Ordinal)), p =>
        {
            Assert.Equal(PlanItemKind.Advice, p.Kind);
            Assert.False(p.Selectable);
        });
    }

    [Fact]
    public void Adapter_card_offers_auto_speed_duplex_only_as_an_unchecked_repair()
    {
        var forced = NetworkCards.Build(Owner(Eth(sd: "4", speed: 100_000_000)), new DeviceResultStore()).Single(c => c.Key == "net:" + EthId.ToString("D"));
        var sd = Row(forced, "nic:*SpeedDuplex");
        Assert.Equal(RecommendPolicy.Suggest, sd.Recommended!.Policy);
        Assert.Equal(0, sd.Recommended.Value.Number);
        Assert.Contains("перезапустится", sd.Recommended.Reason);
        Assert.Equal(DeviceTargets.NicProperty(EthId, "*SpeedDuplex"), sd.JournalTarget);
        // Options never offer forcing a speed: only the repair to auto, or leaving the current value.
        Assert.Equal(["0", "4"], sd.Options.Select(o => o.Value.Key));
        Assert.Equal("автосогласование", sd.FormatValue(DeviceValue.Of(0)));
        Assert.Equal("2,5 Гбит/с, дуплекс", sd.FormatValue(DeviceValue.Of(2500)));

        var plan = DevicePlan.Build([forced], interactiveUser: true);
        var item = Assert.Single(plan, p => p.Setting.Id == "nic:*SpeedDuplex");
        Assert.False(item.IsSelected);

        // Already auto: shown read-only, nothing to repair.
        var healthy = NetworkCards.Build(Owner(), new DeviceResultStore()).Single(c => c.Key == "net:" + EthId.ToString("D"));
        Assert.Null(Row(healthy, "nic:*SpeedDuplex").Recommended);
        Assert.Equal(SettingControl.Info, Row(healthy, "nic:*SpeedDuplex").Control);
        Assert.True(Row(healthy, "nic:*SpeedDuplex").IsReadOnly);
    }

    [Fact]
    public void Eee_is_manual_only_and_only_after_recorded_link_drops()
    {
        NicPropertyInfo eee = Prop("*EEE", "1", "1", "0", "1");
        var stable = NetworkCards.Build(Owner(Eth(drops: 0, extra: eee)), new DeviceResultStore()).Single(c => c.Key.StartsWith("net:2", StringComparison.Ordinal));
        Assert.DoesNotContain(stable.Settings, s => s.Id == "nic:*EEE");

        var flapping = NetworkCards.Build(Owner(Eth(drops: 3, extra: eee)), new DeviceResultStore()).Single(c => c.Key.StartsWith("net:2", StringComparison.Ordinal));
        var row = Row(flapping, "nic:*EEE");
        Assert.Equal(SettingControl.Toggle, row.Control);
        Assert.Null(row.Recommended); // never in the plan
        Assert.Contains(flapping.Settings, s => s.Id == "finding:link-drops");
        Assert.DoesNotContain(DevicePlan.Build([flapping], true), p => p.Setting.Id == "nic:*EEE");
    }

    [Fact]
    public void A_running_game_disables_adapter_changes()
    {
        var card = NetworkCards.Build(Owner(Eth(sd: "4", speed: 100_000_000)) with { RunningGame = "Counter-Strike 2" }, new DeviceResultStore())
            .Single(c => c.Key.StartsWith("net:2", StringComparison.Ordinal));
        Assert.Contains("Counter-Strike 2", Row(card, "nic:*SpeedDuplex").DisabledReason);
    }

    [Fact]
    public void Sync_puts_new_results_into_the_live_card_and_keeps_its_tools()
    {
        var results = new DeviceResultStore();
        var live = NetworkCards.BuildConnection(Owner(), results);
        var liveTool = Row(live, "net-ping").Tool;
        results.Set(NetKeys.Check, Check(gwLost: 4, tcpGame: "Tibia"));
        var fresh = NetworkCards.BuildConnection(Owner(), results);

        NetworkCards.Sync(live, fresh);

        Assert.Equal(fresh.Settings.Select(s => s.Id), live.Settings.Select(s => s.Id));
        Assert.Same(liveTool, Row(live, "net-ping").Tool);
        Assert.Null(Row(live, "net-ping").Recommended);
        Assert.NotNull(Row(live, "net-ping").Current);
        Assert.Contains(live.Settings, s => s.Id == "finding:lan-loss");
        Assert.Contains(live.Settings, s => s.Id == "tweak:tcp-ack-frequency");
    }

    [Fact]
    public void Tools_restore_their_last_result_on_a_new_card()
    {
        var results = new DeviceResultStore();
        results.Set(NetKeys.Check, Check());
        results.Set(NetKeys.Sdr, new SdrResult([new SdrRegion(new SdrPop("fra", "Frankfurt (Germany)", "155.133.226.68"),
            NetStats.Compute([54.0, 55, 53, 54]))], DateTime.Now));
        var conn = NetworkCards.BuildConnection(Owner(), results);
        Assert.StartsWith("Сеть ПК в порядке", Row(conn, "net-ping").Tool!.ResultText);
        Assert.Contains("Frankfurt", Row(conn, "net-valve").Tool!.ResultText);
        Assert.Equal(RecommendPolicy.Advice, Row(conn, "net-valve").Recommended!.Policy); // closest region: an INFO row
    }

    [Fact]
    public void Before_after_text_is_honest_about_no_difference()
    {
        var before = Check(cf: 2.4);
        var after = Check(cf: 2.5);
        var text = NetworkTools.Compare(before, after);
        Assert.Contains("Было → стало", text);
        Assert.Contains("без измеримой разницы", text);
        Assert.Contains("в одно время суток", text);
        Assert.Contains("лучше", NetworkTools.Compare(Check(cf: 20, g: 25), Check(cf: 5, g: 25)));
    }

    [Fact]
    public void Adapter_names_are_shortened_for_cards()
    {
        Assert.Equal("Realtek PCI(e) Ethernet", NetFindings.Short("Realtek(R) PCI(e) Ethernet Controller"));
        Assert.Equal("Qualcomm FastConnect 7800 Wi-Fi 7 HBS", NetFindings.Short("Qualcomm FastConnect 7800 Wi-Fi 7 High Band Simultaneous (HBS) Network Adapter"));
    }
}
