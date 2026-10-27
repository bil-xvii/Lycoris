using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Modules.Devices.Network;

/// <summary>
/// The four measurements of the «Сеть» group. They start only on a click (never from a scan or the self-test), run off
/// the UI thread, stop on «ОТМЕНА» or when the page is left, keep their results for the app session and change nothing
/// in the system (the per-connection byte counters of the background check are switched off again in a finally).
/// After a run the connection card is rebuilt from the results, so the plan and the findings follow the measurement.
/// </summary>
internal static class NetworkTools
{
    private const string Honesty = "ICMP до 1.1.1.1 и 8.8.8.8 — это не пинг до игрового сервера: узлы могут отвечать на ping с низким приоритетом.";

    // ───────────────────────────── Definitions ─────────────────────────────

    public static DeviceTool Ping(DeviceResultStore results)
    {
        var tool = new DeviceTool
        {
            Id = "net-ping", Title = "Замер пинга", ButtonText = "ЗАМЕРИТЬ И ПОДОБРАТЬ",
            Description = "Пинг, затем DNS и MTU. По результату — план во вкладке «Автонастройка».",
            Options = [new DeviceOption(DeviceValue.Of(30), "30 секунд"), new DeviceOption(DeviceValue.Of(120), "2 минуты")],
            Run = RunCheckAsync,
        };
        tool.SelectedOption = tool.Options[0];
        if (results.Get<NetCheckResult>(NetKeys.Check) is { } check)
            (tool.ResultText, tool.ResultDetail) = CheckTexts(check, results.Get<NetCheckResult>(NetKeys.CheckBefore), results.Get<LoadTestResult>(NetKeys.Load));
        return tool;
    }

    public static DeviceTool Load(DeviceResultStore results)
    {
        var tool = new DeviceTool
        {
            Id = "net-load", Title = "Тест под нагрузкой", ButtonText = "ЗАПУСТИТЬ ТЕСТ",
            Description = "Загрузка с speed.cloudflare.com, не дольше 8 с. Другие устройства в сети заметят замедление; результат никуда не отправляется.",
            Options = [new DeviceOption(DeviceValue.Of(300_000_000), "до 300 МБ"), new DeviceOption(DeviceValue.Of(100_000_000), "до 100 МБ")],
            Run = RunLoadAsync,
        };
        tool.SelectedOption = tool.Options[0];
        if (results.Get<LoadTestResult>(NetKeys.Load) is { } load) (tool.ResultText, tool.ResultDetail) = LoadTexts(load, results.Get<NetCheckResult>(NetKeys.Check));
        return tool;
    }

    public static DeviceTool Dns(DeviceResultStore results)
    {
        var tool = new DeviceTool
        {
            Id = "net-dns", Title = "Скорость DNS", ButtonText = "ПРОВЕРИТЬ",
            Description = "Ваш DNS против Cloudflare, Google и Quad9: 12 запросов имён к каждому.",
            Run = RunDnsAsync,
        };
        if (results.Get<DnsBenchResult>(NetKeys.Dns) is { } dns) (tool.ResultText, tool.ResultDetail) = DnsTexts(dns);
        return tool;
    }

    public static DeviceTool Valve(DeviceResultStore results)
    {
        var tool = new DeviceTool
        {
            Id = "net-valve", Title = "Регионы Valve", ButtonText = "ЗАМЕРИТЬ",
            Description = "4 запроса на регион по публичному списку Valve. В игре цифры могут отличаться на несколько мс.",
            Run = RunValveAsync,
        };
        if (results.Get<SdrResult>(NetKeys.Sdr) is { } sdr) (tool.ResultText, tool.ResultDetail) = ValveTexts(sdr);
        return tool;
    }

    // ───────────────────────────── «Замер пинга» ─────────────────────────────

    private static Task RunCheckAsync(DeviceToolRun run, CancellationToken ct)
    {
        var seconds = (int)Math.Clamp(run.Tool.SelectedOption?.Value.Number ?? 30, 10, 300);
        return Task.Run(async () =>
        {
            run.Report(0, "Подготовка…");
            var s = NetSystem.ReadSnapshot();
            if (s.BestInterface is null && !s.ViaVpn)
            {
                run.Fail("Нет подключения к сети — замерять нечего.");
                return;
            }
            var duration = TimeSpan.FromSeconds(seconds);
            var c0 = NetSystem.Counters(s.BestInterface);
            var conns = NetSystem.StartTraffic(out var counting);
            IReadOnlyList<ProcessTraffic> top;
            IReadOnlyList<NetTargetResult> pings;
            var dnsTask = DnsBench.RunAsync(s, null, ct);
            try
            {
                pings = await NetPing.RunAsync(NetPing.Targets(s), duration, NetPing.Interval,
                    p => run.Report(p * 0.8, $"Пинг: {(int)(p * seconds)} из {seconds} с…"), ct).ConfigureAwait(false);
            }
            finally
            {
                top = NetSystem.FinishTraffic(conns, counting); // always switches the counters off
            }
            var c1 = NetSystem.Counters(s.BestInterface);
            var wifi = s.BestIsWifi && s.BestInterface is { } g ? NetNative.WifiLink(g) : null;

            run.Report(0.82, "DNS…");
            DnsBenchResult? dns = null;
            try { dns = await dnsTask.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Сеть: DNS не замерен — {ex.Message}"); }

            run.Report(0.88, "MTU…");
            MtuResult? mtu = null;
            try { mtu = await MtuSearch.RunAsync(IPAddress.Parse("1.1.1.1"), s.Mtu ?? 1500, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is PingException or InvalidOperationException) { }

            run.Report(0.95, "Фоновые загрузки…");
            var jobs = await NetSystem.DeliveryOptimizationJobsAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var result = new NetCheckResult
            {
                At = DateTime.Now, Duration = duration, Targets = pings, Wifi = wifi, Mtu = mtu, Dns = dns, OnWifi = s.BestIsWifi,
                Traffic = Traffic(c0, c1, duration, top, counting, jobs), TcpGame = NetGames.TcpGame(conns), Fingerprint = s.Fingerprint,
            };
            var previous = run.Results.Get<NetCheckResult>(NetKeys.Check);
            var before = run.Results.Get<NetCheckResult>(NetKeys.CheckBefore);
            if (previous is not null && previous.Fingerprint != result.Fingerprint) run.Results.Set(NetKeys.CheckBefore, previous);
            else if (before is not null && before.Fingerprint == result.Fingerprint) run.Results.Remove(NetKeys.CheckBefore);
            run.Results.Set(NetKeys.Check, result);
            if (dns is not null) run.Results.Set(NetKeys.Dns, dns);
            NetworkEvidence.Ipv6Worse = NetFindings.Ipv6Penalty(result) is not null;

            var (text, detail) = CheckTexts(result, run.Results.Get<NetCheckResult>(NetKeys.CheckBefore), run.Results.Get<LoadTestResult>(NetKeys.Load));
            run.Complete(text, detail);
            Log.Info($"Сеть: замер пинга — {CheckLogLine(result)}");
            await RefreshAsync(run, s).ConfigureAwait(false);
        }, ct);
    }

    internal static TrafficInfo? Traffic(NetInterfaceCounters? c0, NetInterfaceCounters? c1, TimeSpan duration, IReadOnlyList<ProcessTraffic> top,
        bool counting, int? jobs)
    {
        if (c0 is null || c1 is null) return new TrafficInfo { Top = top, HasBytes = counting, WindowsUpdateJobs = jobs };
        var secs = Math.Max(1, duration.TotalSeconds);
        return new TrafficInfo
        {
            DownMbps = Math.Max(0, c1.BytesIn - c0.BytesIn) * 8 / secs / 1e6, UpMbps = Math.Max(0, c1.BytesOut - c0.BytesOut) * 8 / secs / 1e6,
            Top = top, HasBytes = counting, WindowsUpdateJobs = jobs,
            InErrors = Math.Max(0, c1.InErrors - c0.InErrors), InPackets = Math.Max(0, c1.InPackets - c0.InPackets),
            InDiscards = Math.Max(0, c1.InDiscards - c0.InDiscards),
        };
    }

    /// <summary>The result line and the detail lines of «Замер пинга» (pure).</summary>
    public static (string Text, string Detail) CheckTexts(NetCheckResult r, NetCheckResult? before, LoadTestResult? load)
    {
        var problems = NetFindings.ForCheck(r, load).Count(f => f.Severity >= NetSeverity.Warning);
        var text = problems > 0
            ? $"Найдено проблем: {problems} — они в строках ниже и во вкладке «Автонастройка»."
            : $"Сеть ПК в порядке: {NetworkCards.CheckSummary(r)}. Пинг в игре задают расстояние до сервера и провайдер.";

        var lines = new List<string>();
        foreach (var t in r.Targets)
        {
            var st = t.Stats;
            var name = $"{t.Target.Label} {t.Target.Address}";
            lines.Add(!st.HasData
                ? t.Target.Role == NetTargetRole.Gateway ? $"{name}: не отвечает на ping — часть роутеров так настроена, это не ошибка" : $"{name}: нет ответа"
                : $"{name}: {NetStats.Ms(st.P50)} (p95 {NetStats.Ms(st.P95)}, макс {NetStats.Ms(st.Max)}), джиттер {NetStats.Ms(st.Jitter)}, потери {st.Lost} из {st.Sent}");
        }
        if (r.Dns is { } dns)
            lines.Add("DNS: " + string.Join(" · ", dns.Servers.Select(x => $"{x.Label} {(x.MedianMs is { } ms ? NetStats.Ms(ms) : "нет ответа")}")));
        var tail = new List<string>();
        if (r.Mtu is { } m) tail.Add(m.PathMtu is { } p ? $"MTU пути {p}" : "MTU пути не определён");
        if (r.Traffic is { } tr)
        {
            var top = tr.Top.Take(3).Select(x => x.Bytes is { } b ? $"{x.Name} {NetFindings.Mb(b)}" : $"{x.Name} ({x.Connections} соед.)").ToList();
            tail.Add($"фон ↓ {NetStats.Mbps(tr.DownMbps)} · ↑ {NetStats.Mbps(tr.UpMbps)}" + (top.Count > 0 ? $" ({string.Join(", ", top)})" : ""));
        }
        if (r.Wifi is { } w && (w.RssiDbm is not null || w.Channel is not null))
            tail.Add($"Wi-Fi {w.Band}" + (w.Channel is { } ch ? $", канал {ch}" : "") + (w.RssiDbm is { } rssi ? $", {rssi} дБм" : ""));
        if (tail.Count > 0) lines.Add(char.ToUpper(tail[0][0]) + string.Join(" · ", tail)[1..]);
        if (before is not null && Compare(before, r) is { } cmp) lines.Add(cmp);
        lines.Add(Honesty);
        return (text, string.Join("\n", lines));
    }

    /// <summary>«Было → стало» with an honest verdict per metric (pure).</summary>
    public static string? Compare(NetCheckResult before, NetCheckResult after)
    {
        var parts = new List<string>();
        if (before.Gateway is { Stats.HasData: true } bg && after.Gateway is { Stats.HasData: true } ag)
        {
            parts.Add($"роутер {NetStats.Ms(bg.Stats.P50)} → {NetStats.Ms(ag.Stats.P50)} ({NetStats.VerdictText(NetStats.CompareMedians(bg.Received, ag.Received))})");
            parts.Add($"джиттер до роутера {NetStats.Ms(bg.Stats.Jitter)} → {NetStats.Ms(ag.Stats.Jitter)} ({NetStats.VerdictText(NetStats.CompareSpread(bg.Stats.Jitter, ag.Stats.Jitter))})");
        }
        if (before.BestInternet is { } bi && after.Targets.FirstOrDefault(t => t.Target.Address == bi.Target.Address) is { Stats.HasData: true } ai)
        {
            parts.Add($"интернет {NetStats.Ms(bi.Stats.P50)} → {NetStats.Ms(ai.Stats.P50)} ({NetStats.VerdictText(NetStats.CompareMedians(bi.Received, ai.Received))})");
            parts.Add($"p95 {NetStats.Ms(bi.Stats.P95)} → {NetStats.Ms(ai.Stats.P95)} ({NetStats.VerdictText(NetStats.CompareSpread(bi.Stats.P95, ai.Stats.P95))})");
            parts.Add($"потери {NetStats.Percent(bi.Stats.LossPercent)} → {NetStats.Percent(ai.Stats.LossPercent)} ({NetStats.VerdictText(NetStats.CompareLoss(bi.Stats, ai.Stats))})");
        }
        if (parts.Count == 0) return null;
        return $"Было → стало ({before.At:HH:mm} → {after.At:HH:mm}): {string.Join("; ", parts)}. Короткий замер на Wi-Fi часто не видит разницы, " +
               "а вечером провайдер загружен сильнее — сравнивайте замеры в одно время суток. Изменения, которым нужна перезагрузка, здесь ещё не видны.";
    }

    private static string CheckLogLine(NetCheckResult r) =>
        string.Join("; ", r.Targets.Select(t => $"{t.Target.Label}: p50 {NetStats.Ms(t.Stats.P50)}, потери {t.Stats.Lost}/{t.Stats.Sent}"));

    // ───────────────────────────── «Тест под нагрузкой» ─────────────────────────────

    private static Task RunLoadAsync(DeviceToolRun run, CancellationToken ct)
    {
        var cap = Math.Clamp(run.Tool.SelectedOption?.Value.Number ?? 300_000_000, 50_000_000, 300_000_000);
        return Task.Run(async () =>
        {
            if (NetGames.RunningGame() is { } game)
            {
                run.Fail($"Идёт игра ({game}): тест займёт весь канал и испортит матч. Запустите его после игры.");
                return;
            }
            if (MeteredReason() is { } why)
            {
                run.Fail(why);
                return;
            }
            run.Report(0, "Пинг без нагрузки…");
            var s = NetSystem.ReadSnapshot();
            LoadTestResult result;
            try
            {
                result = await LoadTest.RunAsync(cap, () => NetSystem.Counters(s.BestInterface), (p, msg) => run.Report(p, msg), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                run.Fail("Тест под нагрузкой сейчас недоступен: speed.cloudflare.com не ответил. Можно открыть его в браузере.");
                return;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                run.Fail("Тест под нагрузкой сейчас недоступен: сервер не ответил вовремя.");
                return;
            }
            run.Results.Set(NetKeys.Load, result);
            var (text, detail) = LoadTexts(result, run.Results.Get<NetCheckResult>(NetKeys.Check));
            run.Complete(text, detail);
            Log.Info($"Сеть: тест под нагрузкой — оценка {result.Grade}, {NetStats.DeltaMs(result.Increase)}, {NetStats.Mbps(result.Mbps)}.");
            await RefreshAsync(run, s).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Why the load test must not run on this connection (metered, roaming, over the limit, offline), or null.</summary>
    private static string? MeteredReason()
    {
        try
        {
            var profile = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return "Нет подключения к интернету.";
            var cost = profile.GetConnectionCost();
            if (cost.NetworkCostType is global::Windows.Networking.Connectivity.NetworkCostType.Fixed or global::Windows.Networking.Connectivity.NetworkCostType.Variable
                || cost.Roaming || cost.OverDataLimit)
                return "Подключение лимитное: тест скачал бы сотни мегабайт, поэтому MONOLITH его не запускает.";
        }
        catch (Exception ex)
        {
            Log.Warn($"Сеть: тип подключения не прочитан — {ex.Message}");
        }
        return null;
    }

    public static (string Text, string Detail) LoadTexts(LoadTestResult l, NetCheckResult? check)
    {
        var text = $"Оценка {l.Grade}: под нагрузкой пинг {NetStats.DeltaMs(l.Increase)} ({NetStats.Ms(l.IdleMedian)} → {NetStats.Ms(l.LoadedMedian)}).";
        var detail = $"Скачано {NetFindings.Mb(l.Bytes)}, {NetStats.Mbps(l.Mbps)}; замеров под нагрузкой: {l.LoadedSamples}." +
                     (NetStats.GradeSeverity(l.Grade) == NetSeverity.Ok ? " Очереди в роутере не раздуваются." : " Лечится на роутере (SQM), не на ПК.");
        return (text, detail);
    }

    // ───────────────────────────── «Скорость DNS» ─────────────────────────────

    private static Task RunDnsAsync(DeviceToolRun run, CancellationToken ct) => Task.Run(async () =>
    {
        run.Report(0, "Подготовка…");
        var s = NetSystem.ReadSnapshot();
        var result = await DnsBench.RunAsync(s, new Progress<double>(p => run.Report(p, "Запросы имён…")), ct).ConfigureAwait(false);
        run.Results.Set(NetKeys.Dns, result);
        var (text, detail) = DnsTexts(result);
        run.Complete(text, detail);
        Log.Info("Сеть: DNS — " + string.Join(", ", result.Servers.Select(x => $"{x.Label} {NetStats.Ms(x.MedianMs)}")));
        await RefreshAsync(run, s).ConfigureAwait(false);
    }, ct);

    public static (string Text, string Detail) DnsTexts(DnsBenchResult r)
    {
        var fastest = r.Servers.Where(x => x.Answered).OrderBy(x => x.MedianMs).FirstOrDefault();
        var text = fastest is null ? "Ни один DNS-сервер не ответил." : $"Быстрее всех: {fastest.Label} — {NetStats.Ms(fastest.MedianMs)}.";
        var lines = r.Servers.Select(x => $"{x.Label}: {(x.MedianMs is { } ms ? NetStats.Ms(ms) : "нет ответа")}" +
                                          (x.TimeoutPercent > 0 ? $", без ответа {NetStats.Percent(x.TimeoutPercent)}" : "")).ToList();
        var detail = string.Join(" · ", lines) + ".";
        if (r.SkipReason is { } skip) detail += $"\nСменить DNS MONOLITH не предлагает: {skip}.";
        else if (DnsAdvice.Offer(r) is { } offer) detail += $"\n{offer.Reason}. Предложение — в «Автонастройке», без галочки.";
        else detail += "\nВаш DNS не медленнее публичных — менять не нужно.";
        return (text, detail + "\nТолько поиск имён (лаунчеры, сайты): на пинг в игре не влияет.");
    }

    // ───────────────────────────── «Регионы Valve» ─────────────────────────────

    private static Task RunValveAsync(DeviceToolRun run, CancellationToken ct) => Task.Run(async () =>
    {
        run.Report(0, "Список регионов Valve…");
        var pops = run.Results.Get<IReadOnlyList<SdrPop>>(NetKeys.SdrConfig);
        if (pops is null)
        {
            try
            {
                pops = await ValveRegions.FetchAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                run.Fail("Список регионов Valve сейчас недоступен — попробуйте позже.");
                return;
            }
            if (pops.Count == 0)
            {
                run.Fail("Valve не вернул список регионов.");
                return;
            }
            run.Results.Set(NetKeys.SdrConfig, pops);
        }
        var result = await ValveRegions.MeasureAsync(pops, p => run.Report(p, $"Регионы: {(int)(p * pops.Count)} из {pops.Count}…"), ct).ConfigureAwait(false);
        run.Results.Set(NetKeys.Sdr, result);
        var (text, detail) = ValveTexts(result);
        run.Complete(text, detail);
        Log.Info("Сеть: регионы Valve — " + string.Join(", ", result.Closest(5).Select(x => $"{x.Pop.Code} {NetStats.Ms(x.Stats.P50)}")));
        await RefreshAsync(run, NetSystem.ReadSnapshot()).ConfigureAwait(false);
    }, ct);

    public static (string Text, string Detail) ValveTexts(SdrResult r)
    {
        var top = r.Closest(6);
        if (top.Count == 0) return ("Ретрансляторы Valve не ответили на ping.", "Возможно, ICMP закрыт у провайдера — в игре регионы всё равно доступны.");
        return ($"Ближе всего: {NetFindings.RegionName(top[0].Pop)} — {NetStats.Ms(top[0].Stats.P50)}.",
            string.Join(" · ", top.Select(x => $"{NetFindings.RegionName(x.Pop)} ({x.Pop.Code}) {NetStats.Ms(x.Stats.P50)}")) +
            ".\nЭто ICMP до ретрансляторов: в игре цифры могут отличаться на несколько мс. Регион задаёт расстояние — на ПК его не изменить.");
    }

    // ───────────────────────────── Refresh ─────────────────────────────

    /// <summary>Rebuilds the connection card from the results and puts it into the live card on the UI thread.</summary>
    private static async Task RefreshAsync(DeviceToolRun run, NetSnapshot s)
    {
        try
        {
            var sp = await SystemProfile.LoadAsync().ConfigureAwait(false);
            var fresh = NetworkCards.BuildConnection(s, run.Results);
            DeviceScan.ReadTweakRows([fresh], sp);
            DeviceUi.Post(() => NetworkCards.Sync(run.Card, fresh));
        }
        catch (Exception ex)
        {
            Log.Warn($"Сеть: строки не обновлены — {ex.Message}. Нажмите «ОБНОВИТЬ».");
        }
    }
}
