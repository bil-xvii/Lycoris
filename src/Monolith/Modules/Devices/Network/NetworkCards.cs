using System.Globalization;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Devices.Network;

/// <summary>
/// The «Сеть» group: one card for the internet connection (measurements, Optimize network tweaks, measured findings,
/// ADVANCED rows that only appear when their condition was measured) and one card per physical adapter (facts,
/// whitelisted NDIS properties, link findings). Built from a snapshot and the session's results — pure apart from the
/// apply delegates, so the same builder serves the scan and the refresh after a measurement.
/// </summary>
public static class NetworkCards
{
    public const string ConnectionKey = "net:connection";
    public const int Order = 500;
    private static CultureInfo Ru => Loc.Culture;

    public static List<DeviceCard> Build(NetSnapshot s, DeviceResultStore results)
    {
        var cards = new List<DeviceCard> { BuildConnection(s, results) };
        var scanFindings = NetFindings.ForSnapshot(s, g => results.Get<bool>(NetKeys.DuplexRegression(g)));
        var i = 0;
        foreach (var a in s.Adapters)
            cards.Add(BuildAdapter(a, s, results, scanFindings.Where(f => f.Adapter == a.InterfaceGuid).ToList(), ++i));
        return cards;
    }

    // ───────────────────────────── Connection ─────────────────────────────

    public static DeviceCard BuildConnection(NetSnapshot s, DeviceResultStore results)
    {
        var check = results.Get<NetCheckResult>(NetKeys.Check);
        var load = results.Get<LoadTestResult>(NetKeys.Load);
        var dns = results.Get<DnsBenchResult>(NetKeys.Dns) ?? check?.Dns;
        var sdr = results.Get<SdrResult>(NetKeys.Sdr);
        var best = s.Best;

        var card = new DeviceCard
        {
            Key = ConnectionKey, Kind = DeviceKind.Network, Title = Loc.T("Подключение к интернету"), Order = Order, GlyphOverride = Glyphs.Globe,
            Subtitle = best is not null ? $"{(best.Medium == NetMedium.WiFi ? "Wi-Fi" : "Ethernet")} · {NetFindings.Short(best.Description)}"
                : s.ViaVpn ? $"VPN · {NetFindings.Short(s.BestDescription ?? "")}"
                : s.BestInterface is not null ? Loc.Tf("через {0}", NetFindings.Short(s.BestDescription ?? Loc.T("виртуальный адаптер"))) : Loc.T("нет подключения"),
            Facts = ConnectionFacts(s),
        };

        // Measurements (one row per measurement, the tool inline).
        var ping = new DeviceSetting
        {
            Id = "net-ping", Title = Loc.T("Пинг до роутера и интернета"), Order = 0,
            Hint = Loc.T("Роутер, 1.1.1.1 и 8.8.8.8 по 2 запроса в секунду. Это не пинг до игрового сервера"),
            Tool = NetworkTools.Ping(results), Current = check is null ? null : DeviceValue.Of(PingCurrent(check)),
        };
        ping.Recommended = MeasureRequest(check, s);
        card.Settings.Add(ping);

        // Findings (advice rows in «НАХОДКИ»): the measured ones of the connection, bufferbloat on its own row, the Valve
        // regions on theirs. The plan lists advice by Order, so they get their Order by severity (the view keeps this order).
        var loadFinding = load is null ? null : NetFindings.LoadFinding(load, backgroundToo: check is not null && NetFindings.ForCheck(check).Any(f => f.Id == "background"));
        var regions = s.SteamInstalled && sdr is not null ? NetFindings.RegionsFinding(sdr) : null;
        var findings = new List<NetFinding>();
        findings.AddRange(NetFindings.ForSnapshot(s).Where(f => f.Adapter is null));
        if (check is not null) findings.AddRange(NetFindings.ForCheck(check, load, includeLoad: false));
        if (load is not null && NetFindings.BuffersFinding(load, best) is { } buffers) findings.Add(buffers);
        var ranked = findings.Concat(new[] { loadFinding, regions }.OfType<NetFinding>()).OrderByDescending(f => f.Severity).Select(f => f.Id).ToList();
        int OrderOf(NetFinding? f, int fallback) => f is null ? fallback : 100 + ranked.IndexOf(f.Id);

        card.Settings.Add(new DeviceSetting
        {
            Id = "net-load", Title = Loc.T("Пинг под нагрузкой"), Order = OrderOf(loadFinding, 1),
            Hint = Loc.T("Скачивает данные и смотрит, растёт ли пинг: так находят bufferbloat — главную причину лагов при загрузках"),
            Tool = NetworkTools.Load(results), Current = load is null ? null : DeviceValue.Of(Loc.Tf("оценка {0} · {1}", load.Grade, NetStats.DeltaMs(load.Increase))),
            Recommended = loadFinding is null ? null : Advice(loadFinding),
        });

        card.Settings.Add(new DeviceSetting
        {
            Id = "net-dns", Title = Loc.T("DNS-серверы"), Order = 2,
            Hint = Loc.T("Скорость поиска имён (сайты, лаунчеры). На пинг в играх не влияет"),
            Tool = NetworkTools.Dns(results), Current = DeviceValue.Of(DnsText(s)),
        });

        if (s.SteamInstalled)
        {
            card.Settings.Add(new DeviceSetting
            {
                Id = "net-valve", Title = Loc.T("Ближайший регион Valve"), Order = OrderOf(regions, 3),
                Hint = Loc.T("Пинг до ретрансляторов Steam (CS2, Dota 2): какой регион ближе. Регион задаёт расстояние, не ПК"),
                Tool = NetworkTools.Valve(results), Current = regions is null ? null : DeviceValue.Of(regions.Value),
                Recommended = regions is null ? null : Advice(regions),
            });
        }

        // Measured findings, most severe first.
        foreach (var f in findings.OrderByDescending(f => f.Severity))
            card.Settings.Add(FindingRow(f, OrderOf(f, 100)));

        // Optimize tweaks shown here read-only («В ОПТИМИЗАЦИИ»); the plan applies them through TweakEngine.
        var offer = dns is null ? null : DnsAdvice.Offer(dns with { SkipReason = dns.SkipReason ?? DnsAdvice.SkipReason(s) });
        var bloated = load is not null && NetStats.GradeSeverity(load.Grade) >= NetSeverity.Warning;
        if (s.HasWifi)
            card.Settings.Add(DeviceSetting.ForTweak("wifi-power-saving-off", Loc.T("Wi-Fi без энергосбережения"),
                Loc.T("От сети адаптер Wi-Fi не «засыпает» между пакетами — меньше скачков пинга на Wi-Fi"), 20,
                s.BestIsWifi ? null : RecommendPolicy.None));
        card.Settings.Add(DeviceSetting.ForTweak("tcp-autotuning-normal", Loc.T("Автонастройка окна TCP"),
            bloated ? Loc.T("Быстрее загрузки, но на линии с bufferbloat они поднимут пинг — сначала SQM на роутере")
                    : Loc.T("Возвращает стандартную автонастройку TCP: быстрее загрузки. На пинг в играх не влияет"), 21,
            bloated ? RecommendPolicy.Suggest : null));
        card.Settings.Add(DeviceSetting.ForTweak("delivery-optimization-local", Loc.T("Обновления без P2P-раздачи"),
            Loc.T("Windows не раздаёт обновления другим ПК. На пинг почти не влияет; полностью — после перезагрузки"), 22));
        foreach (var (id, title, address) in new[] { ("dns-cloudflare", Loc.T("DNS Cloudflare (1.1.1.1)"), DnsAdvice.Cloudflare), ("dns-google", Loc.T("DNS Google (8.8.8.8)"), DnsAdvice.Google) })
        {
            var offered = offer?.TweakId == id;
            card.Settings.Add(DeviceSetting.ForTweak(id, title,
                offered ? offer!.Value.Reason : DnsHint(dns, address), id == "dns-cloudflare" ? 23 : 24,
                offered ? RecommendPolicy.Suggest : RecommendPolicy.None));
        }

        // ADVANCED rows the skeptic kept — only when their condition was measured (never planned: ReadTweakRows skips Advanced).
        if (check?.TcpGame is { } game)
            card.Settings.Add(DeviceSetting.ForTweak("tcp-ack-frequency", Loc.T("Мгновенные TCP-подтверждения"),
                Trim(Loc.Tf("{0} держит TCP-соединение с сервером — может помочь. Только вручную; нужна перезагрузка", game)), 30));
        if (check is not null && NetFindings.Ipv6Penalty(check) is { } penalty)
            card.Settings.Add(DeviceSetting.ForTweak("ipv6-prefer-ipv4", Loc.T("Предпочитать IPv4"),
                penalty > 0 ? Loc.Tf("IPv6 медленнее IPv4 на {0}: Windows может выбирать IPv4. В играх обычно без разницы", NetStats.Ms(penalty))
                            : Loc.T("IPv6 теряет пакеты, IPv4 — нет: Windows может выбирать IPv4. В играх обычно без разницы"), 31));
        return card;
    }

    private static IReadOnlyList<string> ConnectionFacts(NetSnapshot s)
    {
        var facts = new List<string>();
        if (s.Best is { SpeedBps: { } bps } best)
            facts.Add(best.Medium == NetMedium.WiFi ? Loc.Tf("канал {0}", NetFindings.SpeedText(bps)) : NetFindings.SpeedText(bps));
        if (s.Gateway is { } gw) facts.Add(Loc.Tf("шлюз {0}", gw));
        facts.Add(s.HasIpv6 ? Loc.T("IPv4 и IPv6") : Loc.T("только IPv4"));
        if (s.Mtu is { } mtu) facts.Add($"MTU {mtu}");
        return facts;
    }

    private static Recommendation? MeasureRequest(NetCheckResult? check, NetSnapshot s)
    {
        if (s.BestInterface is null && !s.ViaVpn) return null;
        var link = new DeviceLink(DeviceLinkKind.Tool, check is null ? Loc.T("ЗАМЕРИТЬ") : Loc.T("ЗАМЕРИТЬ СНОВА"), ConnectionKey, "net-ping");
        if (check is null)
            return new Recommendation(DeviceValue.Of(Loc.T("замер")),
                Loc.T("30 с пинга до роутера и интернета, DNS и MTU — без нагрузки на сеть. Предложено будет только то, что поможет"),
                RecommendPolicy.Suggest, link) { NeedsMeasurement = true };
        if (!string.Equals(check.Fingerprint, s.Fingerprint, StringComparison.Ordinal))
            return new Recommendation(DeviceValue.Of(Loc.T("повтор")),
                Loc.T("Сеть настроена после замера — замерьте ещё раз, и MONOLITH покажет «было → стало»"),
                RecommendPolicy.Suggest, link) { NeedsMeasurement = true };
        return null;
    }

    /// <summary>«роутер &lt;1 мс · интернет 2,4 мс · потери 0 %».</summary>
    public static string CheckSummary(NetCheckResult r)
    {
        var parts = new List<string>();
        if (r.Gateway is { } gw) parts.Add(gw.Stats.HasData ? Loc.Tf("роутер {0}", NetStats.Ms(gw.Stats.P50)) : Loc.T("роутер не отвечает на ping"));
        if (r.BestInternet is { } inet) parts.Add(Loc.Tf("интернет {0}", NetStats.Ms(inet.Stats.P50)));
        var all = r.Internet4.Select(t => t.Stats).Where(st => st.Sent > 0).ToList();
        if (all.Count > 0) parts.Add(Loc.Tf("потери {0}", NetStats.Percent(all.Min(st => st.LossPercent))));
        if (NetFindings.Ipv6Penalty(r) is { } p) parts.Add(p > 0 ? $"IPv6 +{NetStats.Ms(p)}" : Loc.T("IPv6 с потерями"));
        return parts.Count == 0 ? Loc.T("нет ответа") : string.Join(" · ", parts);
    }

    private static string DnsText(NetSnapshot s)
    {
        if (s.DnsServers.Count == 0) return Loc.T("не задан");
        var first = s.DnsServers[0];
        if (string.Equals(first, s.Gateway, StringComparison.OrdinalIgnoreCase)) return Loc.Tf("роутер ({0})", first);
        return first switch
        {
            "1.1.1.1" or "1.0.0.1" => "Cloudflare (1.1.1.1)",
            "8.8.8.8" or "8.8.4.4" => "Google (8.8.8.8)",
            "9.9.9.9" => "Quad9 (9.9.9.9)",
            _ => first,
        };
    }

    private static string DnsHint(DnsBenchResult? dns, string address)
    {
        if (dns?.Servers.FirstOrDefault(x => x.Address == address) is { MedianMs: { } ms } && dns.Current is { MedianMs: { } cur })
            return ms < cur
                ? Loc.Tf("Замер: {0} против {1} у вашего DNS — разница мала, менять не нужно. На пинг не влияет", NetStats.Ms(ms), NetStats.Ms(cur))
                : Loc.Tf("Замер: {0} против {1} у вашего DNS — ваш быстрее. На пинг не влияет", NetStats.Ms(ms), NetStats.Ms(cur));
        return Loc.T("Только если замер DNS покажет, что он заметно быстрее вашего. На пинг в играх не влияет");
    }

    private static Recommendation Advice(NetFinding f) => new(DeviceValue.Of(f.Value), AdviceText(f), RecommendPolicy.Advice, f.Link);

    /// <summary>The plan's finding line: the measured value, then what to do («Роутер: 5 % · Важно: …»).</summary>
    public static string AdviceText(NetFinding f) =>
        f.Value.Length == 0 || f.Reason.Contains(f.Value, StringComparison.OrdinalIgnoreCase) ? f.Reason : $"{char.ToUpper(f.Value[0], Ru)}{f.Value[1..]} · {f.Reason}";

    /// <summary>The ping row's short «сейчас»: the internet latency, and the loss when there is any.</summary>
    public static string PingCurrent(NetCheckResult r)
    {
        if (r.BestInternet?.Stats is not { } st) return Loc.T("нет ответа");
        return Loc.Tf("интернет {0}", NetStats.Ms(st.P50)) + (st.Lost > 0 ? Loc.Tf(" · потери {0}", NetStats.Percent(st.LossPercent)) : "");
    }

    /// <summary>A finding as an Info row: the value is «сейчас», the advice is the plan's «НАХОДКИ» row.</summary>
    public static DeviceSetting FindingRow(NetFinding f, int order) => new()
    {
        Id = "finding:" + f.Id, Title = f.Title, Hint = Trim(f.Hint), Order = order, Control = SettingControl.Info,
        Current = DeviceValue.Of(f.Value), Link = f.Link, StateTag = f.Severity >= NetSeverity.Warning ? Loc.T("ВАЖНО") : null,
        Recommended = Advice(f),
    };

    // ───────────────────────────── Adapter ─────────────────────────────

    public static DeviceCard BuildAdapter(NetAdapterInfo a, NetSnapshot s, DeviceResultStore results, IReadOnlyList<NetFinding> findings, int index)
    {
        var card = new DeviceCard
        {
            Key = "net:" + a.InterfaceGuid.ToString("D"), Kind = DeviceKind.Network, Title = NetFindings.Short(a.Description), Order = Order + index,
            Subtitle = a.Medium == NetMedium.WiFi
                ? "Wi-Fi · " + (a.RadioOff == true ? Loc.T("радио выключено") : a.Connected ? Loc.T("подключено") : Loc.T("не подключено"))
                : "Ethernet · " + (a.Connected ? Loc.T("подключено") : Loc.T("кабель не подключён")),
            Facts = AdapterFacts(a),
            GlyphOverride = a.Medium == NetMedium.WiFi ? null : Glyphs.Ethernet,
        };

        var gameBlock = s.RunningGame is { } game ? Loc.Tf("Идёт игра ({0}) — адаптер сейчас не перезапускается.", game) : null;
        if (a.Medium == NetMedium.Ethernet && a.Property("*SpeedDuplex") is { } sd && Number(sd) is { } current && sd.ValidValues.Contains("0"))
        {
            // Research (network_adapter.md): MONOLITH only repairs a forced speed/duplex back to auto-negotiation — it never
            // forces a speed. Already auto: a read-only row; forced: the choice is «auto» or leaving it as it is.
            var setting = current == 0
                ? new DeviceSetting
                {
                    Id = "nic:*SpeedDuplex", Title = Loc.T("Скорость и дуплекс"), Order = 0, Control = SettingControl.Info,
                    Hint = Loc.T("Автосогласование — верно почти всегда. Фиксированная скорость задаётся только вместе с портом роутера, в Диспетчере устройств"),
                    Default = DeviceValue.Of(0), Format = v => SpeedDuplexLabel(v.Number),
                    JournalTarget = DeviceTargets.NicProperty(a.InterfaceGuid, "*SpeedDuplex"),
                    ReadCurrent = () => ReadNumber(a.InterfaceGuid, "*SpeedDuplex"), Current = DeviceValue.Of(current),
                }
                : new DeviceSetting
                {
                    Id = "nic:*SpeedDuplex", Title = Loc.T("Скорость и дуплекс"), Order = 0, Control = SettingControl.Choice,
                    Options = [new DeviceOption(DeviceValue.Of(0), SpeedDuplexLabel(0)), new DeviceOption(DeviceValue.Of(current), SpeedDuplexLabel(current))],
                    Hint = Loc.T("Автосогласование — верно почти всегда; MONOLITH только возвращает его, другую скорость не задаёт"),
                    Default = DeviceValue.Of(0), Format = v => SpeedDuplexLabel(v.Number), AppliesWhen = Loc.T("ПЕРЕЗАПУСК АДАПТЕРА"),
                    JournalTarget = DeviceTargets.NicProperty(a.InterfaceGuid, "*SpeedDuplex"),
                    ReadCurrent = () => ReadNumber(a.InterfaceGuid, "*SpeedDuplex"),
                    Apply = NicApply(a, "*SpeedDuplex", Loc.T("Скорость и дуплекс"), v => SpeedDuplexLabel(v.Number), results),
                    Current = DeviceValue.Of(current), DisabledReason = gameBlock,
                };
            if (NetFindings.SpeedDuplexNeedsRepair(a))
                setting.Recommended = new Recommendation(DeviceValue.Of(0),
                    Trim(a.FullDuplex == false
                        ? Loc.Tf("Задано вручную ({0}) и связь в полудуплексе. Адаптер перезапустится, связь пропадёт на 1–6 с", SpeedDuplexLabel(current))
                        : Loc.Tf("Скорость задана вручную ({0}) — сеть ограничена. Адаптер перезапустится, связь пропадёт на 1–6 с", SpeedDuplexLabel(current))),
                    RecommendPolicy.Suggest);
            card.Settings.Add(setting);
        }

        // EEE (ADVANCED, skeptic): manual only, never planned, and only when link drops were recorded on this adapter.
        if (a.Medium == NetMedium.Ethernet && a.LinkDrops is >= NetFindings.LinkDropThreshold)
        {
            var order = 1;
            foreach (var kw in new[] { "*EEE", "EEE", "AdvancedEEE" })
            {
                if (a.Property(kw) is not { } p || !p.ValidValues.Contains("0") || !p.ValidValues.Contains("1") || Number(p) is not { } v) continue;
                var title = kw switch { "*EEE" => Loc.T("Энергосберегающий Ethernet (EEE)"), "EEE" => Loc.T("Энергосберегающий Ethernet (Realtek)"), _ => Loc.T("Расширенный EEE (Realtek)") };
                card.Settings.Add(new DeviceSetting
                {
                    Id = "nic:" + kw, Title = title, Order = order++, Control = SettingControl.Toggle,
                    Hint = Loc.T("Выключение — обходной путь при обрывах на Intel I225/I226 и Killer. Сначала обновите драйвер"),
                    Default = p.DefaultValue is { } d && long.TryParse(d, out var dn) ? DeviceValue.Of(dn) : null, AppliesWhen = Loc.T("ПЕРЕЗАПУСК АДАПТЕРА"),
                    JournalTarget = DeviceTargets.NicProperty(a.InterfaceGuid, kw), ReadCurrent = () => ReadNumber(a.InterfaceGuid, kw),
                    Apply = NicApply(a, kw, title, x => x.Number != 0 ? Loc.T("вкл") : Loc.T("выкл"), results), Current = DeviceValue.Of(v), DisabledReason = gameBlock,
                });
            }
        }

        var fo = 100;
        foreach (var f in findings.OrderByDescending(f => f.Severity))
            card.Settings.Add(FindingRow(f, fo++));
        return card;
    }

    private static IReadOnlyList<string> AdapterFacts(NetAdapterInfo a)
    {
        var facts = new List<string>();
        if (a.Connected && a.SpeedBps is { } bps) facts.Add(NetFindings.SpeedText(bps));
        if (a.Connected && a.Medium == NetMedium.Ethernet && a.FullDuplex is { } full) facts.Add(full ? Loc.T("полный дуплекс") : Loc.T("полудуплекс"));
        if (a.Medium == NetMedium.Ethernet && a.LinkDrops is { } drops) facts.Add(drops == 0 ? Loc.T("обрывов за неделю нет") : Loc.Tf("обрывов за неделю: {0}", drops));
        if (!string.IsNullOrWhiteSpace(a.DriverProvider))
        {
            var year = a.DriverDate is { Length: >= 4 } d ? d[..4] : null;
            facts.Add(Loc.Tf("драйвер {0} {1}{2}", a.DriverProvider, a.DriverVersion, year is null ? "" : $" ({year})").Trim());
        }
        return facts;
    }

    /// <summary>NDIS *SpeedDuplex values (0 auto, 1–6 the standard 10/100/1000 half/full, ≥ 1000 = Mbit/s).</summary>
    public static string SpeedDuplexLabel(long v) => v switch
    {
        0 => Loc.T("автосогласование"),
        1 => Loc.T("10 Мбит/с, полудуплекс"),
        2 => Loc.T("10 Мбит/с, дуплекс"),
        3 => Loc.T("100 Мбит/с, полудуплекс"),
        4 => Loc.T("100 Мбит/с, дуплекс"),
        5 => Loc.T("1 Гбит/с, полудуплекс"),
        6 => Loc.T("1 Гбит/с, дуплекс"),
        >= 1000 => Loc.Tf("{0} Гбит/с, дуплекс", v >= 1000 && v % 1000 == 0 ? (v / 1000).ToString(Ru) : (v / 1000.0).ToString("0.#", Ru)),
        _ => v.ToString(Ru),
    };

    private static long? Number(NicPropertyInfo p) => long.TryParse(p.Value ?? p.DefaultValue, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DeviceValue? ReadNumber(Guid adapter, string keyword) =>
        DeviceWriters.Nic?.Read(adapter, keyword) is { } p && Number(p) is { } n ? DeviceValue.Of(n) : null;

    private static string Trim(string text) => text.Length <= 120 ? text : text[..119] + "…";

    // ───────────────────────────── Applying an adapter property ─────────────────────────────

    /// <summary>
    /// One whitelisted NDIS property through ChangeSession.SetNicAdvancedProperty (journal first). Never during a game;
    /// always says first that the adapter restarts. After a speed/duplex change it waits for the link and flags a
    /// half-duplex result (skeptic: a hard-set switch port makes «auto» fall back to half duplex).
    /// </summary>
    internal static Func<ChangeSession, DeviceValue, CancellationToken, Task<ApplyOutcome>> NicApply(NetAdapterInfo a, string keyword, string title,
        Func<DeviceValue, string> describe, DeviceResultStore results) => async (session, value, ct) =>
    {
        var name = NetFindings.Short(a.Description);
        if (NetGames.RunningGame() is { } game)
        {
            session.Errors.Add(Loc.Tf("{0}: идёт игра ({1}) — во время игры MONOLITH не перезапускает сетевой адаптер.", title, game));
            return ApplyOutcome.Blocked;
        }
        if (DeviceWriters.Nic?.Read(a.InterfaceGuid, keyword) is { } live && Number(live) == value.Number) return ApplyOutcome.NoChange;
        if (keyword == "*SpeedDuplex" && value.Number != 0)
        {
            session.Errors.Add(Loc.Tf("{0}: MONOLITH только возвращает автосогласование — фиксированную скорость он не задаёт.", title));
            return ApplyOutcome.Blocked;
        }
        var ok = await Ui.ConfirmAsync("Перезапустить сетевой адаптер?",
            Loc.Tf("{0}: «{1}» → {2}. Драйвер перезапустит адаптер: связь пропадёт на 1–6 секунд, загрузки, голосовой чат " +
            "и онлайн-матч прервутся. Вернуть прежнее значение можно кнопкой «ОТКАТИТЬ».", name, title, describe(value)), confirmText: Loc.T("ПЕРЕЗАПУСТИТЬ"));
        if (!ok) return ApplyOutcome.Declined;
        var target = value.Key;
        var applied = await Task.Run(() => session.SetNicAdvancedProperty(a.InterfaceGuid, keyword, target, DeviceTargets.Source, $"{name}: {title}"),
            CancellationToken.None);
        if (!applied) return ApplyOutcome.Failed;
        if (keyword == "*SpeedDuplex") await CheckLinkAsync(a.InterfaceGuid, name, results).ConfigureAwait(false);
        return ApplyOutcome.Applied;
    };

    /// <summary>The link of an adapter after a change (tests replace it, with a shorter poll).</summary>
    internal static Func<Guid, (bool Connected, bool? FullDuplex, long? SpeedBps)?> LinkReader = NetSystem.ReadLink;
    internal static TimeSpan LinkPoll = TimeSpan.FromSeconds(1);

    private static async Task CheckLinkAsync(Guid adapter, string name, DeviceResultStore results)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(LinkPoll).ConfigureAwait(false);
            if (LinkReader(adapter) is not { Connected: true } link) continue;
            if (link.FullDuplex == false)
            {
                results.Set(NetKeys.DuplexRegression(adapter), true);
                Log.Warn("{0}: после автосогласования связь в полудуплексе — порт роутера, видимо, задан вручную.", name);
                Ui.Toast("Связь в полудуплексе: порт роутера, видимо, задан вручную. Откатите «Скорость и дуплекс».", ToastKind.Warning, name);
            }
            else
            {
                results.Remove(NetKeys.DuplexRegression(adapter));
                Log.Info("{0}: связь восстановлена — {1}, полный дуплекс.", name, link.SpeedBps is { } b ? NetFindings.SpeedText(b) : Loc.T("скорость неизвестна"));
            }
            return;
        }
        Log.Warn("{0}: связь не вернулась за 20 с после изменения скорости и дуплекса.", name);
        Ui.Toast("Связь не вернулась за 20 секунд — проверьте кабель или откатите «Скорость и дуплекс».", ToastKind.Warning, name);
    }

    // ───────────────────────────── Refresh after a measurement ─────────────────────────────

    /// <summary>
    /// Puts a freshly built connection card into the live one (UI thread): values, recommendations and rows that appear or
    /// go with the new results. The live rows with tools are kept (they hold the results on screen). Then the plan is
    /// rebuilt: the page rebuilds it when a hooked row's recommendation changes, so the measurement row is nudged.
    /// </summary>
    internal static void Sync(DeviceCard live, DeviceCard fresh)
    {
        if (!live.Facts.SequenceEqual(fresh.Facts)) live.Facts = fresh.Facts;
        if (!live.Notes.SequenceEqual(fresh.Notes)) live.Notes = fresh.Notes;
        for (var i = 0; i < fresh.Settings.Count; i++)
        {
            var f = fresh.Settings[i];
            var at = -1;
            for (var j = 0; j < live.Settings.Count; j++)
                if (live.Settings[j].Id == f.Id) { at = j; break; }
            if (at < 0)
            {
                live.Settings.Insert(Math.Min(i, live.Settings.Count), f);
                continue;
            }
            if (at != i) live.Settings.Move(at, i);
            var l = live.Settings[i];
            if (l.Tool is null && (l.Title != f.Title || l.Hint != f.Hint))
            {
                live.Settings[i] = f; // text of a finding / tweak row changed with the new results
                continue;
            }
            if (l.Current != f.Current) l.Current = f.Current;
            if (l.Recommended != f.Recommended) l.Recommended = f.Recommended;
            if (l.StateTag != f.StateTag) l.StateTag = f.StateTag;
            if (l.DisabledReason != f.DisabledReason) l.DisabledReason = f.DisabledReason;
        }
        while (live.Settings.Count > fresh.Settings.Count) live.Settings.RemoveAt(live.Settings.Count - 1);

        if (live.Settings.FirstOrDefault(x => x.Tool is not null) is { } anchor)
        {
            var keep = anchor.Recommended;
            anchor.Recommended = Nudge;
            anchor.Recommended = keep;
        }
    }

    private static readonly Recommendation Nudge = new(DeviceValue.Of("—"), "", RecommendPolicy.None);
}
