using System.Globalization;
using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Network;

/// <summary>
/// Turns the snapshot and the measurements into findings (research network_measure.md §3.2, adapted to 2 echoes per
/// second). Pure: no I/O. Only real, measured problems become findings; an «Ok» never does. Findings never apply anything
/// — the plan's changes come from the rows (Optimize tweaks, speed/duplex), each with its own evidence rule.
/// </summary>
public static class NetFindings
{
    private static CultureInfo Ru => Loc.Culture;

    // ───────────────────────────── Scan-time (adapter and route) ─────────────────────────────

    /// <summary>Link drops on an adapter: two or more in a week (one may be an unplugged cable).</summary>
    public const int LinkDropThreshold = 2;

    public static IReadOnlyList<NetFinding> ForSnapshot(NetSnapshot s, Func<Guid, bool>? duplexRegression = null)
    {
        var list = new List<NetFinding>();
        if (s.Best is { Medium: NetMedium.WiFi } && s.Adapters.Any(a => a.Medium == NetMedium.Ethernet))
            list.Add(new NetFinding("wifi-instead-of-cable", NetSeverity.Warning, Loc.T("Интернет идёт через Wi-Fi, а сетевой порт есть"), "Wi-Fi",
                Loc.T("Кабель до роутера — самое заметное снижение скачков пинга и потерь"),
                Loc.T("Важно: у ПК есть сетевой порт. Кабель до роутера убирает большую часть скачков пинга и потерь Wi-Fi.")));
        if (s.ViaVpn)
            list.Add(new NetFinding("vpn", NetSeverity.Info, Loc.T("Трафик идёт через VPN"), Short(s.BestDescription ?? "VPN"),
                Loc.T("VPN ведёт трафик через свой сервер и добавляет задержку"),
                Loc.T("VPN добавляет путь до своего сервера. Для игр выключайте его или исключите игру из туннеля.")));
        if (s.BestInterface is null && !s.ViaVpn && s.Adapters.Count > 0)
            list.Add(new NetFinding("offline", NetSeverity.Info, Loc.T("Нет подключения к интернету"), Loc.T("нет маршрута"),
                Loc.T("Ни один адаптер не ведёт в интернет"), Loc.T("Подключите кабель или Wi-Fi и нажмите «ОБНОВИТЬ».")));

        foreach (var a in s.Adapters.Where(a => a.Medium == NetMedium.Ethernet))
        {
            if (a.LinkDrops is >= LinkDropThreshold and var drops)
                list.Add(new NetFinding("link-drops", NetSeverity.Warning, Loc.T("Обрывы связи на адаптере"), Loc.Tf("{0} за 7 дней", drops),
                    Loc.T("По журналу Windows: отключения не при выключении и не во сне"),
                    Loc.Tf("Важно: связь пропадала {0} раз за неделю. Сначала обновите драйвер адаптера, затем проверьте кабель и порт.", drops),
                    new DeviceLink(DeviceLinkKind.Navigate, Loc.T("К ДРАЙВЕРАМ"), "drivers"), a.InterfaceGuid));
            if (!a.Connected) continue;
            var sd = a.Property("*SpeedDuplex");
            var forced = sd is not null && (sd.Value ?? sd.DefaultValue) is { } v && v != "0";
            if (a.FullDuplex == false && !forced)
                list.Add(new NetFinding("half-duplex", NetSeverity.Critical, Loc.T("Связь в полудуплексе"), Loc.T("полудуплекс"),
                    Loc.T("Порт роутера или коммутатора задан вручную, или кабель повреждён"),
                    Loc.T("Важно: полудуплекс даёт потери и скачки. Проверьте кабель и порт; на коммутаторе включите автосогласование.")
                    , null, a.InterfaceGuid));
            if (a.SpeedBps is { } bps && bps < 1_000_000_000 && !forced && SupportsGigabit(sd))
                list.Add(new NetFinding("slow-link", NetSeverity.Warning, Loc.Tf("Кабель работает на {0}", SpeedText(bps)), SpeedText(bps),
                    Loc.T("Адаптер умеет 1 Гбит/с, но связь согласовалась на меньшей скорости"),
                    Loc.T("Важно: так бывает с повреждённым или 4-жильным кабелем, плохим портом или старым коммутатором. " +
                    "На пинг почти не влияет, но загрузки упираются в эту скорость."), null, a.InterfaceGuid));
            if (duplexRegression?.Invoke(a.InterfaceGuid) == true && a.FullDuplex != true)
                list.Add(new NetFinding("duplex-regression", NetSeverity.Critical, Loc.T("После автосогласования — полудуплекс"), Loc.T("полудуплекс"),
                    Loc.T("Порт роутера или коммутатора, видимо, задан вручную"),
                    Loc.T("Важно: откатите «Скорость и дуплекс» кнопкой «ОТКАТИТЬ» в строке адаптера или настройте порт на автосогласование."),
                    null, a.InterfaceGuid));
        }
        return list;
    }

    /// <summary>The NIC can do 1 Gbit/s or more: *SpeedDuplex lists 1G full (6) or a speed ≥ 1000 Mbit/s.</summary>
    public static bool SupportsGigabit(NicPropertyInfo? speedDuplex) =>
        speedDuplex is not null && speedDuplex.ValidValues.Any(v => long.TryParse(v, out var n) && (n == 6 || n >= 1000));

    /// <summary>
    /// The plan's speed/duplex repair: only a forced 10/100 value (1–4), or any forced value with a measured half-duplex
    /// link (skeptic: a forced 1G/2.5G full-duplex value cannot cause a mismatch and may be the I225 flapping workaround).
    /// </summary>
    public static bool SpeedDuplexNeedsRepair(NetAdapterInfo a)
    {
        var sd = a.Property("*SpeedDuplex");
        if (sd is null || !sd.ValidValues.Contains("0")) return false;
        if ((sd.Value ?? sd.DefaultValue) is not { } v || v == "0") return false;
        return v is "1" or "2" or "3" or "4" || (a.Connected && a.FullDuplex == false);
    }

    // ───────────────────────────── Measured ─────────────────────────────

    /// <summary>Findings of «Замер пинга» (the probe set, the Wi-Fi link, the traffic, MTU).</summary>
    public static IReadOnlyList<NetFinding> ForCheck(NetCheckResult r, LoadTestResult? load = null, bool includeLoad = true)
    {
        var list = new List<NetFinding>();
        var gw = r.Gateway?.Stats;
        var inet = r.BestInternet?.Stats;
        var wifi = r.OnWifi;

        // F4 — loss in the home network (corroborated: ≥ 2 lost and ≥ 3 % at 2 echoes per second).
        if (gw is { HasData: true } && gw.Lost >= 2 && gw.LossPercent >= 3)
            list.Add(new NetFinding("lan-loss", gw.LossPercent >= 5 ? NetSeverity.Critical : NetSeverity.Warning, Loc.T("Потери в домашней сети"),
                Loc.Tf("роутер: {0}", NetStats.Percent(gw.LossPercent)), Loc.T("Роутер не ответил на часть запросов при 2 запросах в секунду"),
                wifi ? Loc.T("Важно: Wi-Fi теряет пакеты. Сядьте ближе к роутеру, перейдите на 5 ГГц или подключите кабель.")
                     : Loc.T("Важно: кабельное подключение теряет пакеты. Проверьте кабель и порт роутера.")));

        // F5 — spikes in the home network (wired: they must also show on the internet target; router ICMP is a slow path).
        if (gw is { HasData: true, P50: { } g50, P95: { } g95, Jitter: { } gj })
        {
            var spikesPropagate = inet is { P50: { } i50, P95: { } i95 } && i95 - i50 > 10;
            var wired = !wifi && (g50 > 2 || (g95 > 10 && spikesPropagate));
            var air = wifi && (g50 > 5 || g95 > 30 || gj > 5);
            if (wired || air)
                list.Add(new NetFinding("lan-spikes", wifi && g95 > 100 ? NetSeverity.Critical : NetSeverity.Warning, Loc.T("Скачки задержки в домашней сети"),
                    Loc.Tf("роутер: {0}, p95 {1}", NetStats.Ms(g50), NetStats.Ms(g95)), Loc.T("Задержка до роутера должна быть стабильно низкой"),
                    wifi ? Loc.T("Важно: Wi-Fi даёт скачки. Помогают кабель, 5/6 ГГц и место ближе к роутеру.")
                         : Loc.T("Важно: задержка до роутера по кабелю высокая. Проверьте кабель, порт и не перегружен ли роутер.")));
        }

        // F7 — loss at the ISP: two internet targets lose, the router does not.
        var lossy = r.Internet4.Where(t => t.Stats is { Sent: > 0 } st && st.Lost >= 2 && st.LossPercent >= 3).ToList();
        if (lossy.Count >= 2 && (gw is null || !gw.HasData || gw.LossPercent < 2))
        {
            var worst = lossy.Max(t => t.Stats.LossPercent);
            list.Add(new NetFinding("isp-loss", lossy.All(t => t.Stats.LossPercent >= 5) ? NetSeverity.Critical : NetSeverity.Warning,
                Loc.T("Потери за роутером"), Loc.Tf("до {0}", NetStats.Percent(worst)), Loc.T("Пакеты теряются у провайдера, домашняя сеть в порядке"),
                Loc.T("Важно: потери на двух узлах интернета при чистой домашней сети — вопрос к провайдеру. Повторите замер в другое время.")));
        }

        // F8 — high base latency (Cloudflare AIM bands).
        if (inet is { P50: > 50 and var base50 })
            list.Add(new NetFinding("high-base", NetSeverity.Info, Loc.T("Высокая базовая задержка"), NetStats.Ms(base50),
                Loc.T("Задержка до ближайшего узла интернета больше 50 мс"),
                Loc.T("Это маршрут провайдера или расстояние — на ПК это не исправить.")));

        // F9 — unstable latency at the ISP while the home network is calm.
        if (inet is { Jitter: > 10 and var ij } && (gw is null || gw.Jitter is < 2))
            list.Add(new NetFinding("isp-jitter", NetSeverity.Info, Loc.T("Нестабильная задержка у провайдера"), Loc.Tf("джиттер {0}", NetStats.Ms(ij)),
                Loc.T("Разброс задержки до интернета при спокойной домашней сети"),
                Loc.T("Скачки возникают за роутером. Если повторяются в разное время — сообщите провайдеру.")));

        // F14 — receive errors grew while measuring (discards are not errors).
        if (r.Traffic is { InErrors: > 0 and var errors, InPackets: > 0 and var packets } && errors * 100_000 > packets)
            list.Add(new NetFinding("rx-errors", NetSeverity.Warning, Loc.T("Ошибки приёма на адаптере"), Loc.Tf("{0} за замер", errors),
                Loc.T("Повреждённые кадры: кабель, разъём или помехи"),
                Loc.T("Важно: адаптер принимает повреждённые пакеты. Замените кабель или попробуйте другой порт роутера.")));

        // F15 — background transfers during the idle probes.
        if (r.Traffic is { } t && (t.DownMbps > 5 || t.UpMbps > 5))
        {
            var who = t.Top.Where(p => p.Bytes is null or > 0).Take(3).Select(p => p.Bytes is { } b ? $"{p.Name} ({Mb(b)})" : p.Name).ToList();
            var wu = t.WindowsUpdateJobs is > 0;
            list.Add(new NetFinding("background", load is null ? NetSeverity.Info : NetSeverity.Warning, Loc.T("Фоновые загрузки во время замера"),
                $"↓ {NetStats.Number(t.DownMbps)} · ↑ {NetStats.Mbps(t.UpMbps)}", Loc.T("Загрузки забивают канал и поднимают пинг в игре"),
                (wu ? Loc.T("Windows Update скачивает обновления. ") : "") +
                (who.Count > 0 ? Loc.Tf("Больше всего трафика: {0}. ", string.Join(", ", who)) : "") +
                Loc.T("Поставьте загрузки на паузу во время игры или ограничьте скорость в лаунчере."),
                wu ? new DeviceLink(DeviceLinkKind.Settings, Loc.T("ЛИМИТ ОБНОВЛЕНИЙ"), "ms-settings:delivery-optimization-advanced") : null));
        }

        // F16 / F17 — Wi-Fi signal and band.
        if (r.Wifi is { } w)
        {
            if (w.RssiDbm is { } rssi && rssi < -67)
                list.Add(new NetFinding("wifi-weak", rssi < -75 ? NetSeverity.Warning : NetSeverity.Info, Loc.T("Слабый сигнал Wi-Fi"), Loc.Tf("{0} дБм", rssi),
                    Loc.T("Для игр нужен сигнал не хуже −67 дБм"),
                    (rssi < -75 ? Loc.T("Важно: ") : "") + Loc.T("Сядьте ближе к роутеру, уберите преграды или подключите кабель.")));
            if (w.Is24GHz)
                list.Add(new NetFinding("wifi-24", NetSeverity.Info, Loc.T("Wi-Fi в диапазоне 2,4 ГГц"), Loc.Tf("канал {0}", w.Channel),
                    Loc.T("2,4 ГГц перегружен соседями и бытовой техникой"),
                    Loc.T("Переключитесь на 5/6 ГГц: на роутере дайте диапазонам разные имена сетей или включите band steering.")));
            if (w.Error is { } err)
                list.Add(new NetFinding("wifi-location", NetSeverity.Info, Loc.T("Сигнал Wi-Fi не прочитан"), Loc.T("нет доступа"),
                    Loc.T("Windows 11 скрывает данные Wi-Fi без доступа к расположению"), err + ".",
                    new DeviceLink(DeviceLinkKind.Settings, Loc.T("РАСПОЛОЖЕНИЕ"), "ms-settings:privacy-location")));
        }

        // F20 — a PMTU black hole (not merely a smaller path MTU).
        if (r.Mtu is { BlackHole: true, PathMtu: { } path } m && path < m.InterfaceMtu)
            list.Add(new NetFinding("mtu-blackhole", NetSeverity.Info, Loc.T("Большие пакеты пропадают в пути"), Loc.Tf("MTU пути {0}", path),
                Loc.T("Роутер не сообщает о слишком больших пакетах"),
                Loc.T("Некоторые загрузки могут зависать. Это настраивается на роутере (MSS clamping); Lycoris MTU не меняет.")));

        if (includeLoad && load is not null && LoadFinding(load) is { } lf) list.Add(lf);
        return list;
    }

    /// <summary>F10 — bufferbloat by grade: B = Info, C = Warning, D/F = Critical; A/A+ is not a finding.</summary>
    public static NetFinding? LoadFinding(LoadTestResult l, bool backgroundToo = false)
    {
        var severity = NetStats.GradeSeverity(l.Grade);
        if (severity == NetSeverity.Ok) return null;
        return new NetFinding("bufferbloat", severity, Loc.T("Пинг растёт под нагрузкой (bufferbloat)"), Loc.Tf("оценка {0} · {1}", l.Grade, NetStats.DeltaMs(l.Increase)),
            Loc.T("Очередь в роутере или у провайдера переполняется, когда канал занят"),
            (severity >= NetSeverity.Warning ? Loc.T("Важно: ") : "") +
            (backgroundToo ? Loc.T("сначала остановите фоновые загрузки, затем включите SQM / Smart Queue / QoS на роутере или ограничьте скорость на 5–10 % ниже тарифа.")
                : severity >= NetSeverity.Warning ? Loc.T("включите SQM / Smart Queue / QoS на роутере или ограничьте скорость на 5–10 % ниже тарифа.")
                : Loc.T("Включите SQM / Smart Queue / QoS на роутере или ограничьте скорость на 5–10 % ниже тарифа.")));
    }

    /// <summary>Receive buffers below the maximum while the adapter dropped packets under load (skeptic: audit only, Lycoris writes nothing).</summary>
    public static NetFinding? BuffersFinding(LoadTestResult l, NetAdapterInfo? best)
    {
        if (best?.Property("*ReceiveBuffers") is not { } rb || !int.TryParse(rb.Value ?? rb.DefaultValue, out var value)) return null;
        var max = rb.ValidValues.Select(v => v.StartsWith("max:", StringComparison.Ordinal) && int.TryParse(v[4..], out var m) ? m : 0).DefaultIfEmpty(0).Max();
        if (max <= value || l.InPacketsDelta is not { } packets || packets <= 0) return null;
        var lost = (l.InErrorsDelta ?? 0) + (l.InDiscardsDelta ?? 0);
        if (lost * 200 <= packets) return null; // under 0,5 %
        return new NetFinding("rx-buffers", NetSeverity.Info, Loc.T("Адаптер терял пакеты под нагрузкой"), Loc.Tf("буферы {0} из {1}", value, max),
            Loc.T("Мало буферов приёма: при больших загрузках кадры отбрасываются"),
            Loc.T("Увеличить можно в Диспетчере устройств → адаптер → Дополнительно → «Буферы приёма». Lycoris это не меняет."), null, best.InterfaceGuid);
    }

    /// <summary>IPv6 clearly worse than IPv4 on both pairs (≥ +10 ms, or ≥ 5 % loss where IPv4 is clean); null otherwise.</summary>
    public static double? Ipv6Penalty(NetCheckResult r)
    {
        var v4 = r.Internet4.ToDictionary(t => t.Target.Label, t => t.Stats);
        var pairs = r.Internet6
            .Select(t => (V6: t.Stats, V4: v4.GetValueOrDefault(t.Target.Label.Replace(" IPv6", "", StringComparison.Ordinal))))
            .Where(p => p.V4 is { HasData: true })
            .ToList();
        if (pairs.Count < 2) return null;
        var slower = pairs.All(p => p.V6 is { P50: { } a } && p.V4!.P50 is { } b && a >= b + 10);
        var lossy = pairs.All(p => p.V6.Sent > 0 && p.V6.LossPercent >= 5 && p.V4!.LossPercent < 2);
        if (!slower && !lossy) return null;
        return slower ? pairs.Average(p => p.V6.P50!.Value - p.V4!.P50!.Value) : 0;
    }

    /// <summary>Closest Valve regions as an INFO row (nothing on the PC changes it).</summary>
    public static NetFinding? RegionsFinding(SdrResult r)
    {
        var top = r.Closest(3);
        if (top.Count == 0) return null;
        return new NetFinding("valve-regions", NetSeverity.Info, Loc.T("Ближайшие регионы Valve"), $"{RegionName(top[0].Pop)} {NetStats.Ms(top[0].Stats.P50)}",
            Loc.T("Пинг до ретрансляторов Steam (CS2, Dota 2): примерно как в игре"),
            Loc.Tf("Ближе всего: {0}. Регион задаёт расстояние, не ПК.", string.Join(", ", top.Select(x => $"{RegionName(x.Pop)} {NetStats.Ms(x.Stats.P50)}"))));
    }

    public static string RegionName(SdrPop p)
    {
        var d = p.Description;
        var paren = d.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? d[..paren] : d;
    }

    // ───────────────────────────── Text helpers ─────────────────────────────

    public static string SpeedText(long bps) => bps switch
    {
        >= 1_000_000_000 => Loc.Tf("{0} Гбит/с", (bps / 1e9).ToString(bps % 1_000_000_000 == 0 ? "0" : "0.#", Ru)),
        _ => Loc.Tf("{0} Мбит/с", (bps / 1e6).ToString("0", Ru)),
    };

    public static string Mb(long bytes) => Loc.Tf("{0} МБ", bytes >= 10_000_000 ? (bytes / 1_000_000).ToString() : (bytes / 1e6).ToString("0.0", Ru));

    /// <summary>«Realtek PCI(e) Ethernet» from «Realtek(R) PCI(e) Ethernet Controller».</summary>
    public static string Short(string description)
    {
        var s = description.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("®", "").Replace("™", "").Replace("High Band Simultaneous (HBS)", "HBS", StringComparison.OrdinalIgnoreCase);
        foreach (var tail in new[] { " Network Adapter", " Network Connection", " Family Controller", " Controller", " Adapter" })
            if (s.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) s = s[..^tail.Length];
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length == 0 ? description : s;
    }
}
