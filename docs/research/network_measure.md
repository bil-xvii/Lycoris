# Lycoris: measurement-based network auto-tuning ("Проверка сети")

Area: what Lycoris can **measure** about a gamer's connection, how to turn the measurements into **findings**, and
which **actions** (only evidence-backed, journaled catalog items) an auto-tune may propose, with a before/after re-check.
Date: 27.10.2026. Test PC: Windows 11 Pro build 26300, Ryzen 7 9800X3D, wired Realtek 1 Gbit/s, Israel.

Bottom line, stated up front:

- **Almost nothing on a Windows PC lowers the ping to a game server.** The game ping is set by distance, the ISP's route
  and the server's tick. Modern shooters (CS2, Call of Duty) use UDP. Registry "ping tweaks" were classified as
  placebo or ADVANCED earlier (general_tweaks.md §8, gaming_tweaks.md §2 F, §3). **I agree with all of those
  classifications and found no new evidence to change them.**
- What *does* hurt a game connection, and what Lycoris can detect, is: Wi-Fi instead of cable, a weak or 2.4 GHz
  Wi-Fi link, Wi-Fi power saving, a damaged cable (100 Mbit/s or half-duplex link, receive errors), **bufferbloat**
  (latency that rises when something downloads or uploads), background downloads, a VPN in the path, and packet loss
  in the LAN or at the ISP.
- So "auto-tune" means: **measure → explain → offer only the catalog items that fix a measured problem → re-measure
  and show the real difference**, including "no measurable change". On a healthy PC the correct result is "nothing to
  change", and this test PC is such a case (all checks green; see §2).

---

## 0. Summary table

| id | Russian UI title | Class | Effect on ping / jitter / loss | Admin |
|---|---|---|---|---|
| `net-measure-path` | Тип подключения и маршрут | MEASUREMENT | none (diagnostic): finds Wi-Fi-instead-of-cable, VPN, wrong default route | no |
| `net-measure-gateway` | Задержка до роутера | MEASUREMENT | none; finds LAN/Wi-Fi jitter and loss | no |
| `net-measure-internet` | Задержка до интернета | MEASUREMENT | none; separates LAN problems from ISP problems | no |
| `net-measure-bufferbloat` | Задержка под нагрузкой (bufferbloat) | MEASUREMENT | none; finds the most common real cause of lag spikes | no |
| `net-measure-dns` | Скорость DNS-серверов | MEASUREMENT | none; DNS affects lookups (launchers, sites), **not** in-game ping | no |
| `net-measure-mtu` | Проверка MTU на пути | MEASUREMENT | none; finds PMTU black holes (rare) | no |
| `net-measure-wifi` | Качество Wi-Fi | MEASUREMENT | none; signal, band, link rate | no |
| `net-measure-ethernet` | Состояние кабельного подключения | MEASUREMENT | none; link speed, duplex, receive errors | no |
| `net-measure-background` | Фоновый сетевой трафик | MEASUREMENT | none; finds downloads that cause bufferbloat while playing | partly (per-process: yes) |
| `net-measure-game-regions` | Пинг до регионов Steam (CS2) | MEASUREMENT | none; shows which Valve region is closest | no |
| `net-autotune` | Подобрать сетевые настройки автоматически | SAFE-OPTIONAL | equals the size of the problem it finds; on a healthy PC: none | yes (to apply) |
| `wifi-power-saving-off` (existing) | Wi-Fi без энергосбережения | SAFE-DEFAULT (agree) | Wi-Fi only: can remove power-save jitter; proven by re-measure | yes |
| `tcp-autotuning-normal` (existing) | Автонастройка окна TCP — стандартная | SAFE-DEFAULT repair (agree) | downloads only; none on game ping | yes |
| `dns-cloudflare` / `dns-google` (existing) | DNS-серверы Cloudflare / Google | SAFE-OPTIONAL (agree), offered only when the benchmark proves it faster | lookups only; none on game ping | yes |
| `delivery-optimization-local` (existing) | Обновления без P2P-раздачи | SAFE-DEFAULT (agree) | upload to LAN peers only; not a bandwidth limiter | yes |
| `nic-speedduplex-auto` (proposed) | Скорость и дуплекс сетевой карты — автоматически | SAFE-OPTIONAL repair, only when forced **and** the link is degraded | fixes duplex mismatch / 100 Mbit/s caused by a forced setting | yes |
| `info-use-cable` | Подключите кабель вместо Wi-Fi | INFO-ONLY | the largest real jitter/loss reduction available to most users | – |
| `info-wifi-band` | Wi-Fi 5/6 ГГц вместо 2,4 ГГц | INFO-ONLY | less interference and jitter on Wi-Fi | – |
| `info-router-sqm` | SQM/QoS на роутере против bufferbloat | INFO-ONLY | the only real fix for bufferbloat (router side) | – |
| `info-check-cable` | Проверьте сетевой кабель и порт | INFO-ONLY | fixes 100 Mbit/s links, half duplex, CRC errors | – |
| `info-pause-downloads` | Фоновые загрузки во время игры | INFO-ONLY | removes self-inflicted bufferbloat | – |
| `info-vpn` | VPN или прокси увеличивают пинг | INFO-ONLY | removes the VPN detour | – |
| `info-isp` | Потери за роутером — вопрос к провайдеру | INFO-ONLY | none on the PC; gives the user evidence for the ISP | – |
| `dni-auto-mtu` | Автоподбор MTU | DO-NOT-INCLUDE | none on ping; risk of breaking connections | – |
| `dni-nic-autotune` | Автоподбор параметров сетевой карты | DO-NOT-INCLUDE | below measurement noise; NIC-dependent risk | – |
| `dni-silent-dns` | Автоматическая смена DNS без подтверждения | DO-NOT-INCLUDE | none on ping; breaks local filtering/VPN DNS | – |
| `dni-relay-block` | Блокировка серверов Steam через брандмауэр | DO-NOT-INCLUDE | forces a region; edits the firewall; can break matchmaking | – |
| `dni-wlan-autoconfig-off` | Отключить фоновое сканирование Wi-Fi | DO-NOT-INCLUDE | breaks reconnect/roaming; reverts on reboot | – |

---

## 1. Principles for the network check

1. **Measure before changing anything.** The check is read-only and allowed without admin rights. Only applying a
   finding's action needs admin rights. Applying always goes through `ChangeSession` (journal, restore point).
2. **Nothing is applied silently.** Auto-tune builds a plan of existing catalog items. Recommended rows are pre-ticked.
   The user presses one button, and then the same probes run again.
3. **No custom servers, no telemetry.** Targets:
   - the user's own default gateway;
   - public anycast resolvers: 1.1.1.1 Cloudflare, 8.8.8.8 Google, 9.9.9.9 Quad9;
   - Cloudflare's public speed endpoint, only for the optional load test;
   - Valve's public relay list, only for the optional region check.

   Results stay on the PC (`%LocalAppData%\MONOLITH\netcheck\*.json`). They are never posted anywhere, including
   Cloudflare's `__results` logging URL, which their own engine calls by default.
4. **Be gentle with ICMP.** Send at most 5 echo requests per second per target and at most 10 per second in total.
   On this PC, 20/s caused 13–25 % "loss" on every target and 6 % on the router, while 5/s gave 0 % (§2.2). That loss
   was the targets rate-limiting ICMP, not a real problem. Rate limiting of ICMP generation is explicitly allowed for
   routers (RFC 1812 §4.3.2.8) and required for ICMPv6 (RFC 4443 §2.4).
5. **Loss counts only when it is corroborated.** It must show on at least two independent internet targets, or on the
   gateway at ≤ 5 pps over ≥ 100 samples. Loss on one anycast target is that target's policy.
6. **Gateway spikes count only when they propagate.** Router echo replies are answered by the router's CPU (slow
   path), not its forwarding path. If the gateway shows spikes but 1.1.1.1 (whose packets cross the same router)
   does not, the router's ICMP responder is slow and forwarding is fine. This PC shows exactly that: gateway max
   11.6 ms, 1.1.1.1 max 3.8 ms in the same run.
7. **Heavy traffic only on an explicit click.** The bufferbloat test downloads and uploads real data. Before it starts:
   - The UI states the cap, for example «Тест скачает до 300 МБ и отправит до 100 МБ, ~15 секунд».
   - It is refused on metered connections (`NetworkInformation.GetInternetConnectionProfile().GetConnectionCost()`
     must be `Unrestricted`).
   - **It is refused while a game is running.** I made that mistake while researching: `cod.exe` was running during
     my load test. Lycoris must check its own game detection (Games module) first.
8. **Locale independence.** Use APIs, not parsed `ping.exe` / `netsh` output. On this Russian-UI PC `netsh wlan`
   printed English, and the language varies per system. Performance counters go through `PdhAddEnglishCounter`
   (CLAUDE.md).

---

## 2. What was verified on this PC (read-only, no setting changed)

### 2.1 Path and adapters

| Item | Value | Meaning |
|---|---|---|
| Active adapter | `Ethernet`: Realtek PCI\VEN_10EC&DEV_8126 (RTL8126, MSI board), Microsoft inbox driver `rt640x64.inf` 9.1.412.2015 | wired, good |
| Link | 1 Gbit/s, FullDuplex = True, `*SpeedDuplex` = 0 (Auto). This driver offers 10M…2.5G. | healthy. 1 Gbit/s is the router port's limit and does not affect ping. |
| Advanced properties exposed | SpeedDuplex, PriorityVLANTag=3, InterruptModeration=1, ReceiveBuffers=512, TransmitBuffers=128, NetworkAddress | No EEE / Green Ethernet / power-saving keywords with the inbox driver. There is nothing to check there. |
| Default route | 0.0.0.0/0 → 192.168.1.1 via ifIndex 4 (Ethernet), metric 25 | no VPN, no Wi-Fi detour |
| DNS | router 192.168.1.1 (and router IPv6), DHCP | |
| MTU | IPv4 NlMtu 1500. IPv6 NlMtu 1460 (router-advertised). | |
| Wi-Fi | Qualcomm FastConnect 7800 (802.11a/b/g/n/ac/ax/be), radio **software off**, disconnected | Wi-Fi checks could not be exercised here |
| NIC counters | ReceivedPacketErrors 0, Outbound errors/discards 0. ReceivedDiscardedPackets 12 411 of 5.4 M unicast (0.23 %). | no physical errors. Discards are not errors (§4.8). |
| TCP | all templates `AutoTuningLevelLocal` Normal, CUBIC, ECN disabled. RSS/RSC enabled. | defaults; `tcp-autotuning-normal` would change nothing |

### 2.2 Idle latency (System.Net.NetworkInformation.Ping, Stopwatch timing)

40 samples, 100 ms apart:

| Target | Min | Median | p95 | Max | Jitter (mean abs. consecutive diff) | Lost |
|---|---|---|---|---|---|---|
| 192.168.1.1 (router) | 0.85 | 1.02 | 2.97 | 11.64 | 0.59 | 0/40 |
| 1.1.1.1 | 2.16 | 2.41 | 3.23 | 3.79 | 0.35 | 0/40 |
| 8.8.8.8 | 3.71 | 3.82 | 4.77 | 5.61 | 0.30 | 1/40 |
| 9.9.9.9 | 2.52 | 3.02 | 3.58 | 4.60 | 0.26 | 4/40 |

Rate sensitivity: the same targets at different send intervals.

| Interval | Gateway | 1.1.1.1 | 8.8.8.8 | 9.9.9.9 | 149.112.112.112 |
|---|---|---|---|---|---|
| 20 ms | – | 8/50 | – | – | – |
| 50 ms | 3/50 | 12/60 | 13/60 | 10/60 | 15/60 |
| 200 ms | **0/100** | 1/50 | 0/50 | 0/50 | – |

This is why §1.4 limits probes to 5 pps per target.

`PingReply.RoundtripTime` has 1 ms resolution: 86 of 100 gateway replies read "0 ms". The app must therefore time each
echo with a `Stopwatch` and show LAN values as "<1 ms".

### 2.3 Latency under load (bufferbloat)

Load came from parallel HTTPS streams to `speed.cloudflare.com` (served from the TLV colo). ICMP to 1.1.1.1 ran at
10 pps, only while the load was active.

| Phase | Data | Throughput | 1.1.1.1 median | p95 | Max | Lost | Increase |
|---|---|---|---|---|---|---|---|
| idle | – | – | 2.3–2.4 | 2.5–4.2 | – | 0 | – |
| download, 8 streams × 50 MB | 400 MB in 4.0 s | ~804 Mbit/s | 2.6 | 3.3 | 4.1 | 0/30 | **+0.3 ms → A+** |
| upload, 4 streams × 10 MB | 60 MB in 6.0 s | ~80 Mbit/s | 2.4 | 3.4 | 4.1 | 0/48 | **+0.0 ms → A+** |

Cloudflare AIM "gaming" score from their open-source formula (§4.4): latency 2.4 ms → 20 points, loss 0 % → 10,
loaded increase 0.3 ms → 20, sum 50 ≥ 30 → **great**. The ISP/router has no bufferbloat.

Endpoint facts found on the way:
- `__down?bytes=N` returns 200 for N ≤ 99 999 999 and **403 for N ≥ 100 000 000**, although Cloudflare's own engine
  config still lists 1e8 / 2.5e8 steps. Keep chunks ≤ 50 MB and treat 403/429 as "test unavailable".
- Every response carries `Server-Timing: cfL4;desc="…rtt=2944&min_rtt=2928…"`. That is Cloudflare's own TCP RTT in µs,
  a useful ICMP-independent cross-check (here 2.7–2.9 ms vs ICMP 2.4 ms).

### 2.4 DNS resolver timing

`Resolve-DnsName -DnsOnly -NoHostsFile -QuickTimeout`, 5 game/launcher names × 3 runs. Cmdlet overhead ≈ 0.6 ms.

| Resolver | Cached-name median (min) | Random NXDOMAIN median | Failures |
|---|---|---|---|
| Router 192.168.1.1 | **1.4 ms** (1.0) | 6.7 ms | 0 |
| Quad9 9.9.9.9 | 3.4 ms (2.7) | 7.8 ms | 0 |
| Cloudflare 1.1.1.1 | 4.4 ms (3.2) | 5.4 ms | 0 |
| Google 8.8.8.8 | **51.3 ms** (4.1) | 57.3 ms | 0 |

Google answers `www.google.com` in ~4 ms, but `steamcommunity.com` / `battle.net` in ~49–60 ms on every repeat. Its
nearby anycast node evidently does not serve these names from a local cache.

**Here `dns-google` would make lookups ~35× slower, and the router is already the fastest.** That is why the DNS
items must stay SAFE-OPTIONAL and be offered only when a benchmark on the user's own line proves a gain
(agrees with general_tweaks.md §8).

### 2.5 Path MTU

`Ping.Send` with `PingOptions.DontFragment = true`:

| Payload | Result |
|---|---|
| 1472 | OK to gateway and 1.1.1.1 |
| 1473 | `PacketTooBig`, refused locally by the 1500 interface MTU |

Binary search found a path MTU of **1500**. Single DF probes sometimes time out under rate limiting (8.8.8.8 at
1472), so every size needs 2–3 tries.

### 2.6 Valve SDR relays (CS2 regions)

`GET https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730` works without a key. It returned 53 POPs
with relay IPv4 addresses and port ranges, plus a `typical_pings` table. Relays answer ICMP. Medians of 6 echoes:

| POP | Median | POP | Median |
|---|---|---|---|
| fra (Frankfurt) | 54 ms | dvie (Datapacket Vienna) | 77 ms |
| par (Paris) | 54 ms | vie (Vienna) | 82 ms |
| ams (Amsterdam) | 63 ms | waw (Warsaw) | 81 ms |
| mad (Madrid) | 63 ms | sto (Stockholm) | 82 ms |
| dfra (Datapacket Frankfurt) | 64 ms | dfjr (Fujairah) | 156 ms |
| lhr (London) | 68 ms | dxb (Dubai) | 220 ms |

The geographically nearer Gulf POPs are slower than Europe on this ISP's routing. That is useful, honest
information, and nothing on the PC can change it.

### 2.7 Background traffic at the time of the check

| Measurement | Value |
|---|---|
| Interface throughput (2 s delta) | 0.41 Mbit/s down, 4.37 Mbit/s up |
| Established TCP connections by process | claude 34, **cod 12**, steamwebhelper 7, Battle.net 6, Discord 3 |
| Delivery Optimization | no active jobs |

**Verdict for this PC:** wired, healthy link, no errors, no bufferbloat, router DNS fastest, no VPN.
`net-autotune` would propose **no change**. The useful output is the report itself, plus the CS2 region table.

---

## 3. Auto-tune algorithm (`net-autotune`)

### 3.1 Pipeline

```
Inputs
  A. adapters & route   : GetAdaptersAddresses/GetIfTable2 (type, media, link speed, duplex, InErrors), GetBestRoute2 to 1.1.1.1,
                          MSFT_NetAdapterAdvancedPropertySettingData (*SpeedDuplex, valid values), VPN heuristics
  B. Wi-Fi link         : WlanQueryInterface (rssi, channel; current_connection if location consent), power-plan AC index
  C. current tweaks     : Detect() of wifi-power-saving-off, tcp-autotuning-normal, dns-*, delivery-optimization-local
  D. idle probes        : gateway + 1.1.1.1 + 8.8.8.8 + 9.9.9.9, 5 pps each, 30 s (quick) or 120 s (thorough)
  E. optional load test : download then upload, capped (default ≤ 300 MB / ≤ 100 MB, ≤ 8 s each), ICMP 10 pps during load
  F. optional DNS bench : current resolvers vs 1.1.1.1 / 8.8.8.8 / 9.9.9.9, raw UDP queries
  G. optional MTU probe : DF binary search 1200…(MTU−28)
  H. background traffic : interface delta during D; per-PID TCP bytes (admin) ; DO status
  I. optional regions   : Valve SDR POP ICMP (only if Steam/CS2 is installed)
        │
        ▼
Findings (severity Ok / Info / Warning, same model as Dashboard HealthCheck)
        │
        ▼
Plan = existing catalog items whose trigger finding is present (pre-ticked), + INFO cards with deep links
        │  user clicks ПРИМЕНИТЬ (admin; ChangeSession, restore point, journal)
        ▼
Re-measure with the identical probe set → before/after table with an honest verdict per metric
```

The existing `HealthCheck` model (`CheckSeverity Ok/Info/Warning`, `CheckActionKind ApplyTweak/FocusTweak/OpenUri/
Navigate`) already fits. A network finding is a `HealthCheck`. "Critical" maps to Warning with a higher sort weight.

### 3.2 Findings: conditions, severity, action

Thresholds are evidence-based where a source exists (Cloudflare AIM, Cisco Wi-Fi design, Intel, bufferbloat.net).
The rest are conservative engineering choices and are marked (eng.).

| # | Finding | Condition | Severity | Action (only these) |
|---|---|---|---|---|
| F1 | Игра идёт через Wi-Fi, а кабель есть | default route via 802.11 **and** a physical 802.3 adapter is present (disconnected) | Warning | `info-use-cable` |
| F2 | Трафик идёт через VPN | best route to 1.1.1.1 uses a tunnel/virtual adapter (IfType 53/131, or WireGuard/Wintun/TAP/OpenVPN in the description) | Info | `info-vpn` |
| F3 | Wi-Fi выбран вместо кабеля | both Ethernet and Wi-Fi up, default route via Wi-Fi | Warning | `info-use-cable` (never edit metrics automatically) |
| F4 | Потери в домашней сети | gateway loss ≥ 1 % at 5 pps over ≥ 100 samples (≥ 5 % = critical) | Warning | wired: `info-check-cable`; Wi-Fi: F12–F14 actions |
| F5 | Скачки задержки в домашней сети | wired: gateway median > 2 ms, or p95 > 10 ms **and** the spikes also show on 1.1.1.1 (eng.). Wi-Fi: median > 5 ms, p95 > 30 ms or jitter > 5 ms; p95 > 100 ms = critical (eng.) | Warning | Wi-Fi: `wifi-power-saving-off` if not applied, else `info-wifi-band` / `info-use-cable`. Wired: `info-check-cable` |
| F6 | Регулярные скачки | spikes > median + 5×MAD that repeat with a stable period (10–120 s, ≥ 3 times) on gateway **and** internet (eng.) | Warning | Wi-Fi: `wifi-power-saving-off` / `info-use-cable`. Name the period; scans and polling apps are typical, but this is a hypothesis. |
| F7 | Потери у провайдера | loss ≥ 1 % on ≥ 2 of 3 anycast targets, gateway clean (≥ 3 % = critical) | Warning | `info-isp` (export the report) |
| F8 | Высокая базовая задержка | median to the best anycast target > 50 ms (AIM bands: < 10 great, < 20 good, < 50 average) | Info | `info-isp` (route/region; not fixable on the PC) |
| F9 | Нестабильная задержка у провайдера | internet jitter > 10 ms (AIM jitter band) while gateway jitter < 2 ms | Info | `info-isp` |
| F10 | Bufferbloat | loaded median − idle median (worse of down/up): see §4.4 grades. B = Info; C = Warning; D/F = critical | per grade | `info-router-sqm`; if F15 is present, `info-pause-downloads` first |
| F11 | Скорость упирается в сетевую карту | measured download ≥ 90 % of the Ethernet link speed **and** link < 1 Gbit/s | Warning | combine with F12 |
| F12 | Кабель работает на 10/100 Мбит/с | Ethernet link < 1 Gbit/s, NIC supports ≥ 1 G (`*SpeedDuplex` valid values contain 6 or a vendor value ≥ 1000), `*SpeedDuplex` = 0 | Warning | `info-check-cable` |
| F12b | Скорость задана вручную | `*SpeedDuplex` ≠ 0 **and** (link < NIC max **or** half duplex) | Warning | `nic-speedduplex-auto` |
| F13 | Полудуплекс | Ethernet connected with FullDuplex = False | Warning (critical) | forced: `nic-speedduplex-auto`; otherwise `info-check-cable` |
| F14 | Ошибки приёма | `MIB_IF_ROW2.InErrors` grows during the check (> 1 per 100 000 packets) (eng.). **Ignore InDiscards** (here 12 411 discards with 0 errors) | Warning | `info-check-cable` |
| F15 | Фоновые загрузки | interface traffic during idle probes > 5 Mbit/s or > 10 % of measured capacity (eng.); top processes listed; DO job active | Warning (Info if no load test ran) | `info-pause-downloads` (never kill processes) |
| F16 | Слабый сигнал Wi-Fi | RSSI < −67 dBm (Cisco real-time cell edge) = Info; < −75 dBm = Warning | Info/Warning | `info-use-cable`, `info-wifi-band` |
| F17 | Wi-Fi в диапазоне 2,4 ГГц | channel ≤ 14 and the adapter supports 5 GHz (802.11a/ac/ax/be) | Info | `info-wifi-band` |
| F18 | Энергосбережение Wi-Fi | AC index of the Wi-Fi power setting ≠ 0 | Info | `wifi-power-saving-off` |
| F19 | Медленный DNS | current resolver cached-name median > best alternative + 20 ms **and** > 2× best, or ≥ 5 % timeouts. Skipped when a local filter/VPN/DoH is detected (eng.) | Info | the **measured faster** of `dns-cloudflare` / `dns-google`, unticked by default, with "на пинг в играх не влияет" |
| F20 | Проблема MTU | DF probes at (MTU − 28) time out silently while smaller sizes pass (black hole), not merely PMTU < 1500 | Info | text: router should clamp MSS. **No MTU change** (`dni-auto-mtu`) |
| F21 | Автонастройка TCP изменена | `tcp-autotuning-normal` detects NotApplied | Info | `tcp-autotuning-normal` (download speed, not ping) |
| F22 | Ближайшие регионы Steam | Valve POP table (§4.10) | Ok/Info | none (CS2 tip: «Макс. допустимый пинг матчмейкинга») |

Findings that are **never** produced, because they have no measurable basis: "NIC interrupt moderation adds
latency", "Nagle", "NetworkThrottlingIndex", "QoS reserve 20 %", "MTU 1492 lowers ping". Our timing resolution
(~0.1 ms, noise ~0.5 ms) cannot even observe the µs-level effects of interrupt moderation. Advertising them would
contradict gaming_tweaks.md §3.

### 3.3 Before / after re-measurement

1. Same probe set (targets, rate, duration, load caps), run right after apply. For items that need a reboot or an
   adapter restart (`nic-speedduplex-auto`, DNS flush), run it after the link is back up (poll `MediaConnectState`).
2. For each metric (median, p95, jitter, loss, loaded increase), compute the difference and a 95 % bootstrap CI:
   1000 resamples of the median difference (cheap, no NuGet).
3. Verdict per metric:
   - **«лучше»** when the CI excludes 0 and |Δ| ≥ 1 ms (latency) or ≥ 20 % (jitter / p95);
   - **«хуже»** under the mirrored condition;
   - **«без измеримой разницы»** otherwise.

   Loss needs ≥ 200 samples per side before a difference under 1 % is shown (eng.).
4. Expected outcome is stated up front:
   - `tcp-autotuning-normal`: "на пинг не влияет", so "без изменений" is the correct result;
   - `dns-*`: only the DNS row is compared;
   - `wifi-power-saving-off`: gateway jitter/p95 on Wi-Fi.
5. If an applied item made a metric **worse**, the result card offers per-row ОТКАТИТЬ, with the journal undo.
   It is never reverted automatically.
6. Both runs are saved with the plan in `netcheck\*.json`, so the user can compare later, for example at another
   time of day. Without a re-check at the same hour, ISP load (evenings) confounds the comparison. Say so in one
   muted line.

### 3.4 What stays INFO (Lycoris cannot or must not change it)

- **Router SQM / QoS** (fq_codel, CAKE). This is the real fix for bufferbloat, and it lives on the router.
  bufferbloat.net: tune the shaper until loaded latency stays low (their success target: under ~15–25 ms).
- **Cable vs Wi-Fi, 5/6 GHz vs 2.4 GHz, router placement, mesh.**
- **Cable or port replacement** (Intel: a gigabit adapter at 100 Mbit/s is almost always a cable/equipment issue).
- **Launcher download limits.** Steam «Ограничить скорость загрузки» / «Разрешить загрузки во время игры»;
  Battle.net bandwidth limit; Windows Update pause; Delivery Optimization bandwidth limits via
  `ms-settings:delivery-optimization-advanced`. Lycoris opens these pages and does not write the undocumented
  Settings-store values (agrees with gaming_tweaks.md: DO bandwidth policies are ADVANCED because they lock the UI).
- **ISP route / region** (F7–F9, F22), with an exportable plain-text report for the ISP.

---

## 4. Measurement items

### 4.1 `net-measure-path`: Тип подключения и маршрут (MEASUREMENT)

- **Read:**
  - `GetAdaptersAddresses` / `GetIfTable2`: IfType 6 Ethernet, 71 802.11, 53/131 virtual/tunnel; `MediaConnectState`,
    `TransmitLinkSpeed`, `PhysicalMediumType`.
  - `GetBestRoute2` (or `GetBestInterfaceEx`) for 1.1.1.1 gives the interface the game traffic will actually use.
  - The existing `TweakCatalog.PhysicalAdapters()` filter skips Hyper-V/WSL/VPN adapters.
- **VPN heuristic:** the best interface is virtual/tunnel, or its description contains WireGuard, Wintun, TAP,
  OpenVPN, "VPN". The same keyword approach is Microsoft's default for Delivery Optimization VPN detection
  ("VPN", "Secure", "Virtual Private Network").
- **This PC:** best interface = Ethernet (ifIndex 4), no tunnel.
- **Effect:** none. Diagnostic only.

### 4.2 `net-measure-gateway`: Задержка до роутера (MEASUREMENT)

- **Mechanism:** `System.Net.NetworkInformation.Ping.SendPingAsync(gateway, 1000 ms, 32-byte buffer)` at 200 ms
  spacing, timed with `Stopwatch`. `RoundtripTime` is whole milliseconds only. The gateway comes from the best
  route's next hop.
- **Metrics:** min / median / p95 / max, jitter as the mean absolute difference of consecutive samples (the same
  definition as Cloudflare's engine), loss.
- **Duration:** quick 30 s (150 samples) or thorough 120 s (600 samples, also used for F6 periodicity).
- **Caveat:** router ICMP is answered in the slow path, so judge spikes only together with internet targets
  (§1.6). Some routers drop ICMP to themselves. Then the gateway row reads "роутер не отвечает на ping" and is not
  a finding.
- **Data:** ~150 B per echo. Negligible.

### 4.3 `net-measure-internet`: Задержка до интернета (MEASUREMENT)

- **Targets:**
  - 1.1.1.1, 8.8.8.8 and 9.9.9.9, round-robin at 5 pps each;
  - IPv6 equivalents only when the best IPv6 route exists. Games here are IPv4; IPv6 is optional.
- **Why three:** anycast targets differ in ICMP policy. 9.9.9.9 lost 4/40 at 10 pps while 1.1.1.1 lost 0. A median
  over targets plus the "≥ 2 of 3" loss rule removes single-target artefacts.
- **Optional HTTP cross-check:** `GET https://speed.cloudflare.com/__down?bytes=0` and read `Server-Timing cfL4 rtt`
  (µs). This gives a TCP RTT seen by Cloudflare's edge, independent of ICMP treatment.
- **Honesty:** this is the latency to a nearby anycast PoP (2–4 ms here), **not** to a game server. The UI says so.

### 4.4 `net-measure-bufferbloat`: Задержка под нагрузкой (MEASUREMENT)

- **What it finds:** queues in the modem/router/ISP that fill when the line is saturated. This is the most common
  real cause of "lag when someone downloads" (bufferbloat.net; RFC 7567 recommends AQM; RFC 8290 fq_codel).
- **Load source:** `https://speed.cloudflare.com/__down?bytes=N` (GET) and `__up` (POST). These are the defaults of
  Cloudflare's open-source, MIT-licensed `@cloudflare/speedtest` engine. There is no published third-party usage
  policy, so treat the endpoint as best-effort:
  - no SLA;
  - N ≥ 1e8 already returns 403 (§2.3);
  - an experimental "authorization token" option in the engine suggests access may tighten.

  On 403/429/timeout, show «тест под нагрузкой сейчас недоступен» and the manual alternative: open the Waveform or
  Cloudflare test in the browser via `UnelevatedShell.OpenAsync`.
- **Procedure.** Adaptive and capped; the defaults below are shown in the UI before start.
  1. Idle baseline from §4.3 (1.1.1.1 only, 10 pps for 5 s; 1.1.1.1 shares the Cloudflare PoP with the load server:
     both TLV here).
  2. **Download:** 6–8 parallel streams of 25–50 MB chunks. Stop at 8 s **or** at the cap (default 300 MB).
     Probe 1.1.1.1 every 100 ms only while ≥ 4 streams are in flight, skipping the first 0.5 s (TCP ramp-up).
     Cloudflare's own engine samples loaded latency every 400 ms and keeps 20 points; 10 pps for 2–8 s gives
     20–75 points.
  3. **Upload:** 4 streams × 10 MB POSTs, ≤ 8 s or ≤ 100 MB.
  4. On a 1 Gbit/s line the 300 MB cap lasts ~3 s. That is enough: a saturated bottleneck queue fills in
     milliseconds. On this PC 400 MB / 4 s gave 30 loaded samples.
- **Grade (letter):** increase = loaded median − idle median, for down and up separately. The overall grade is the
  worse of the two.

  | Grade | Increase | Severity |
  |---|---|---|
  | A+ | < 5 ms | Ok |
  | A | < 30 ms | Ok |
  | B | < 60 ms | Info |
  | C | < 200 ms | Warning |
  | D | < 400 ms | Warning (critical) |
  | F | ≥ 400 ms | Warning (critical) |

  This is the scale widely reported for Waveform's test. Waveform's page refused automated fetching, so the exact
  bands are from secondary sources (BufferSpeed, Speedtesthq) that agree on A+ < 5 and A < 30.
- **Gaming verdict (exact, primary source):** Cloudflare AIM, from `internalConfig.ts` of cloudflare/speedtest.

  | Metric | Points |
  |---|---|
  | latency | < 10 → 20, < 20 → 10, < 50 → 5, < 100 → 0, < 500 → −10, else −20 |
  | loadedLatencyIncrease | same bands as latency |
  | packetLoss | < 1 % → 10, < 5 % → 5, < 25 % → 0, < 50 % → −10, else −20 |

  Gaming = latency + loss + loadedIncrease. Thresholds [5, 15, 25, 30] map the sum to bad / poor / average / good /
  great. Show the class name in Russian.
- **Ethics and UX:**
  - one explicit button with the data cap in its tooltip;
  - refused on metered connections and while a game is running;
  - cancel button;
  - the user is told other people on the network will see a slowdown for ~15 s;
  - never scheduled or automatic;
  - results are never uploaded (no `__results` POST).
- **This PC:** A+ / A+, AIM gaming "great".

### 4.5 `net-measure-dns`: Скорость DNS-серверов (MEASUREMENT)

- **Mechanism (recommended):** raw DNS over UDP/53 with `System.Net.Sockets.UdpClient`.
  - Build an A-query with RD=1 and a random ID; time it with `Stopwatch`; 1 s timeout; one retry.
  - This is precise and needs no cache bypass.
  - Alternative: `DnsQueryEx` with `DNS_QUERY_REQUEST.pDnsServerList` + `DNS_QUERY_BYPASS_CACHE`.
  - Not `Resolve-DnsName` in a PowerShell process: it was used here only for verification and adds ~0.6 ms plus
    process start.
- **Sets:**
  1. "cached": 6–8 popular names relevant to the user: steamcommunity.com, api.steampowered.com, battle.net,
     www.google.com, and the launcher hosts of installed games; 3 rounds, first round discarded;
  2. "uncached": random-label NXDOMAIN under a stable zone, which measures recursion;
  3. failure/timeout rate.
- **Candidates:**
  - the current resolvers (from `GetAdaptersAddresses` DNS list, IPv4 and IPv6);
  - 1.1.1.1, 8.8.8.8, 9.9.9.9 for comparison. Quad9 is not a catalog item; showing it is information only.
- **Skip the recommendation** (benchmark still shown) when any of these holds:
  - the current resolver is a private address that is not the gateway (Pi-hole/AdGuard);
  - DoH is configured (`Get-DnsClientDohServerAddress` / registry);
  - the adapter has a connection-specific suffix from a domain, or a VPN is active.
- **Honesty line:** «DNS влияет на скорость открытия сайтов и лаунчеров, но не на пинг в игре». Agrees with
  general_tweaks.md §8.
- **This PC:** router 1.4 ms fastest; Google 51 ms. No recommendation.

### 4.6 `net-measure-mtu`: Проверка MTU на пути (MEASUREMENT)

- **Mechanism:**
  - `Ping.Send(target, 1000, new byte[size], new PingOptions(64, dontFragment: true))`;
  - binary search 1200…(interface MTU − 28) to 1.1.1.1, 2–3 tries per size;
  - `PacketTooBig` = refused locally, or an ICMP "fragmentation needed" came back; `TimedOut` = dropped or
    rate-limited.
- **Interpretation:**
  - PMTU < 1500 (for example 1492 on PPPoE) is normal; Windows does PMTU discovery (RFC 1191).
  - Only a **black hole** (large DF probes silently vanish at every retry while small ones pass) is worth reporting.
    It hangs some TCP transfers. It never changes game ping.
- **Action:** INFO only. The router should clamp TCP MSS. Lycoris does not change MTU (`dni-auto-mtu`).
- **This PC:** path MTU 1500 (IPv4).

### 4.7 `net-measure-wifi`: Качество Wi-Fi (MEASUREMENT)

- **Mechanism:** Native Wifi API (`wlanapi.dll`), no `netsh` parsing.
  - `WlanQueryInterface(wlan_intf_opcode_current_connection)` returns `WLAN_ASSOCIATION_ATTRIBUTES`: signal quality
    0–100, where 0 = −100 dBm and 100 = −50 dBm, linear (Microsoft); `dot11PhyType`; `ulRxRate`/`ulTxRate`.
  - `wlan_intf_opcode_rssi` (dBm) and `wlan_intf_opcode_channel_number`.
  - Link rate is also available from `MIB_IF_ROW2.TransmitLinkSpeed`.
- **Windows 11 24H2+ location gate:** `WlanQueryInterface` with **current_connection**, `WlanGetNetworkBssList`,
  `WlanGetAvailableNetworkList` and `WlanScan` return `ERROR_ACCESS_DENIED` without precise-location consent. The
  first blocked call may raise a one-time system prompt and shows the "location in use" tray icon (Microsoft,
  "Changes to API behavior for Wi-Fi access and location").
  - Microsoft's list does not include the `rssi` and `channel_number` opcodes, so they should work without consent.
    **This is unverified here: Wi-Fi was off.**
  - Design:
    1. Read rssi + channel + link speed first.
    2. Call current_connection only on the user's click «Подробнее о Wi-Fi».
    3. On `ERROR_ACCESS_DENIED`, show one line with a button to `ms-settings:privacy-location`.
  - Never call WlanScan.
  - SSID without location: `WlanConnectionProfileDetails.GetConnectedSsid()`.
- **Band:** channel ≤ 14 = 2.4 GHz. 5 vs 6 GHz cannot be told apart from the channel number alone (6 GHz channels
  overlap numerically). Use `dot11PhyType` / centre frequency when consent exists; otherwise say "5 или 6 ГГц".
- **Thresholds:** RSSI −67 dBm is Cisco's cell edge for real-time traffic (≈ quality 66 %). Below −75 dBm
  (≈ 50 %) = Warning. These are F16/F17.
- **This PC:** radio off, nothing measured. Must be tested on a Wi-Fi laptop before release.

### 4.8 `net-measure-ethernet`: Состояние кабельного подключения (MEASUREMENT)

- **Read:**
  - `MSFT_NetAdapter` (`root\StandardCimv2`): Speed, FullDuplex, MediaConnectState. Locale-independent; already
    used for TCP settings.
  - `MSFT_NetAdapterAdvancedPropertySettingData` `*SpeedDuplex`: current `RegistryValue`, `ValidRegistryValues`.
    NDIS standard: 0 Auto, 1/2 10M H/F, 3/4 100M H/F, 6 1G Full; vendors add values such as 2500.
  - `GetIfEntry2` → `InErrors`, `InDiscards`, `OutErrors` deltas during the check.
- **Interpretation:**
  - A gigabit-capable NIC at 100 Mbit/s with Auto is a cable/jack/port fault. Intel's article on gigabit adapters at
    100 Mbps calls it a physical issue "almost always" caused by the cable. Gigabit needs all four pairs; a damaged
    pair falls back to 100 Mbit/s (LANCOM).
  - Half duplex on a switched link means a duplex mismatch, typically one side forced and the other on
    auto-negotiation. Parallel detection then assumes half duplex. Cisco: both sides auto, or both fixed.
  - `InErrors` growth means CRC/physical errors.
  - `InDiscards` is **not** a fault signal: it counts frames the stack chose not to deliver, e.g. unknown protocols.
    Here 12 411 discards with 0 errors.
- **Ping effect, honestly:** a 100 Mbit/s link adds ~0.1 ms of serialization per full packet, which is irrelevant.
  It matters when the plan is faster than 100 Mbit/s (the NIC becomes the bottleneck, so bufferbloat appears on the
  PC side during downloads) and as a sign of a damaged cable that also loses packets.
- **This PC:** 1 Gbit/s full duplex, Auto, 0 errors.

### 4.9 `net-measure-background`: Фоновый сетевой трафик (MEASUREMENT)

- **Interface level (no admin):** delta of `MIB_IF_ROW2.InOctets/OutOctets` on the best interface during the idle
  probes. The Dashboard's existing PDH counters `\Network Interface(*)\Bytes Received/sec` also work.
- **Per process:**
  - **TCP (admin):**
    1. `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_CONNECTIONS)` for IPv4 and IPv6.
    2. For each established connection: `SetPerTcpConnectionEStats(TcpConnectionEstatsData, EnableCollection=TRUE)`,
       sample `GetPerTcpConnectionEStats` → `TCP_ESTATS_DATA_ROD_v0.DataBytesIn/Out` twice 2 s apart, then set
       `EnableCollection=FALSE`.

    This is per-connection, transient diagnostics state that disappears with the connection. It is not a system
    configuration, so it is not journaled, but it must be switched off again after sampling. Microsoft: Set… needs
    an elevated administrator. If Set fails, the Get data is garbage, so check `Rw.EnableCollection`.
  - **UDP (QUIC from browsers, game traffic)** is invisible to EStats. A later version can add an ETW real-time
    session on Microsoft-Windows-Kernel-Network (PID + bytes for TCP and UDP) via P/Invoke `StartTrace`/
    `ProcessTrace`. No TraceEvent NuGet (CLAUDE.md package rule).
  - Historical per-app usage: `ConnectionProfile.GetAttributedNetworkUsageAsync` exists, but it requires the
    `networkDataPlanProvisioning` capability (a packaged-app capability), so it is not usable for Lycoris.
- **Known updaters:**
  - Delivery Optimization: `Get-DeliveryOptimizationStatus` (worked without admin here, "no active jobs");
  - Steam `steamapps\downloading`, Battle.net Agent, Epic, OneDrive, Xbox/Gaming Services: process names only,
    shown as "возможно, идёт загрузка".
- **Action:** `info-pause-downloads`. The Games page booster stays the only place that closes apps
  (minimal-UI rule).
- **This PC:** 0.4 / 4.4 Mbit/s with CoD running. Not a finding.

### 4.10 `net-measure-game-regions`: Пинг до регионов Steam (MEASUREMENT)

- **Valve (public, machine-readable):** `GET https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730`.
  - Keyless. The Steam client itself fetches this config (it appears in CS2 client logs).
  - The Steamworks SDR docs tell game coordinators to download the network config and refresh it about hourly.
  - The Steamworks Web API reference does not document this endpoint, so treat its schema as unstable.
  - Parse `pops.<code>.desc` and `relays[].ipv4`; ICMP 6 echoes per POP to the first relay of the 8–12 POPs nearest
    by `geo` (5 pps).
  - Cache the config for 24 h in `%LocalAppData%`.
- **Accuracy:** CS2 measures relay ping with its own UDP protocol, so ICMP is an approximation. Here it gave a
  plausible ordering (Frankfurt/Paris 54 ms, Dubai 220 ms).
- **Other platforms:**
  - Blizzard publishes per-game server IPs only in a human support article (for WinMTR), not as an API, and it
    requires login. Hard-coding them would rot.
  - Activision (Call of Duty), Riot, EA, NCSoft (AION 2) and Techland have **no** public endpoint. For these, say:
    «Пинг до серверов этой игры смотрите в самой игре», e.g. CoD «Телеметрия → Задержка сервера».
- **Action:** information only. Region forcing via firewall rules is `dni-relay-block`.

---

## 5. Actions the auto-tune may propose (existing or proposed catalog items)

### 5.1 `wifi-power-saving-off` (existing), SAFE-DEFAULT: agree

- **Trigger:** F18; also F5/F6 on Wi-Fi when not applied.
- **Expected effect:** lower gateway jitter/p95 on Wi-Fi when the adapter was dozing between packets. It is proven
  or disproven by the re-check.
- **Mechanism:** unchanged (power-plan AC index only). Revert: journal.
- Agrees with general_tweaks.md §8.

### 5.2 `tcp-autotuning-normal` (existing), SAFE-DEFAULT repair: agree

- **Trigger:** F21.
- **Effect:** download throughput only; the re-check says "на пинг не влияет". No change on this PC (all
  templates Normal).

### 5.3 `dns-cloudflare` / `dns-google` (existing), SAFE-OPTIONAL: agree, with a stricter offer rule

- **Trigger:** F19 only, and only the provider that measured faster on the user's line.
- **This PC:** neither. The router was faster than both, and Google was ~50 ms.
- **Revert:** journal (DHCP or the previous list).

### 5.4 `delivery-optimization-local` (existing), SAFE-DEFAULT: agree

- Not a download limiter: mode 0 still downloads over HTTP at full speed. So it is **not** the action for F15.
  F15 points to `ms-settings:delivery-optimization-advanced` (Microsoft's documented bandwidth limits; default 0 =
  dynamic).

### 5.5 `nic-speedduplex-auto` (proposed), SAFE-OPTIONAL repair

- **Trigger:** F12b/F13 only. `*SpeedDuplex` ≠ 0 **and** a measured symptom (link below NIC max, or half duplex).
  A forced value with a healthy full-speed link is left alone, because some users force it on purpose for a buggy
  switch.
- **Mechanism:**
  - read `*SpeedDuplex` RegistryValue;
  - apply `Set-NetAdapterAdvancedProperty -Name <n> -RegistryKeyword '*SpeedDuplex' -RegistryValue 0`, with the
    adapter resolved by InterfaceGuid as in the DNS script;
  - undo is the same command with the original value.

  Side effect: the driver resets the adapter, so the **link drops for ~3–10 s**. Warn first; never during a game.
- **Evidence:**
  - IEEE 802.3 requires auto-negotiation for 1000BASE-T. Intel says Auto Negotiation is the correct setting for
    gigabit.
  - Cisco: both link partners must be auto, or both fixed; otherwise duplex mismatch.
- **Admin:** yes. **Risk:** low (returns to the driver default); link interruption.
- Coordinate with the NIC-properties research. If that report classifies NIC advanced properties differently, this
  narrow repair is the only one the auto-tune needs.

---

## 6. DO-NOT-INCLUDE for the auto-tune

| id | What | Why |
|---|---|---|
| `dni-auto-mtu` | Lowering MTU via `netsh interface ipv4 set subinterface … mtu=` | No effect on game ping. PMTUD already adapts. A wrong value fragments or breaks VPN/PPPoE paths. Black holes are a router MSS-clamping issue. |
| `dni-nic-autotune` | "Optimizing" interrupt moderation, buffers, RSS/RSC, offloads, EEE from measurements | Effects are µs, below what ICMP can resolve. NIC-dependent, raises CPU load, causes link resets. Already DO-NOT-INCLUDE (gaming_tweaks.md §3, general_tweaks.md §8): agree. |
| `dni-silent-dns` | Switching DNS automatically to the fastest resolver | No ping effect. Breaks Pi-hole/parental filters, VPN split DNS, local names. Must stay a visible SAFE-OPTIONAL choice (§5.3). |
| `dni-relay-block` | "CS2 server picker": Windows Firewall rules blocking Valve relay IPs | Edits the firewall to steer matchmaking. Relay IPs change with the config revision. Stale rules break matchmaking. Outside "evidence-backed reversible tweak". |
| `dni-wlan-autoconfig-off` | `netsh wlan set autoconfig enabled=no` to stop background scans | Stops auto-reconnect and roaming, and is lost on reboot (user reports). Evidence for scans causing periodic spikes is community-only. F6 reports the pattern instead. |
| (existing) | QoS DSCP marking, NonBestEffortLimit, TcpNoDelay, NetworkThrottlingIndex as ping fixes | Already classified (general_tweaks.md §8): agree. The auto-tune never proposes them. |

---

## 7. UI placement (minimal-UI rules)

- **One place:** Optimize, category «Сеть». The category header gets one action, **«ПРОВЕРИТЬ СЕТЬ»**. It runs the
  quick check (idle probes, path, Ethernet/Wi-Fi, background traffic, DNS; ~30 s, no heavy traffic). The result is
  shown as a compact results strip plus `HealthCheck`-style finding rows above the network tweaks.
  - Recommended tweaks are pre-ticked. ПРИМЕНИТЬ runs the plan and the re-check.
  - The manual mode is simply the existing per-tweak rows, plus an expander «Параметры проверки»:
    - duration: quick / thorough;
    - the load test with its data caps (button «ТЕСТ ПОД НАГРУЗКОЙ», tooltip with MB);
    - Steam regions.
- **Dashboard:** gaming readiness may show one network chip (last check result and age), with
  `CheckAction(Navigate/FocusTweak)` into Optimize. Measurements never run on Dashboard load.
- **Games page:** optional later idea: a 10-second quick probe before boost. Not in v1.
- **Read-only mode:** all measurements work; apply buttons are disabled by the global strip.

## 8. Implementation notes

- No new NuGet. APIs:
  - `System.Net.NetworkInformation.Ping`, `UdpClient`, `HttpClient` (load test; `SocketsHttpHandler` with
    `MaxConnectionsPerServer` = streams);
  - `System.Management` CIM (`MSFT_NetAdapter*`);
  - P/Invoke `iphlpapi` (`GetIfTable2`, `GetBestRoute2`, `GetExtendedTcpTable`, `Get/SetPerTcpConnectionEStats`)
    and `wlanapi`.
- Everything off the UI thread. Cancel tokens on every probe; the page `Deactivate` cancels the run.
- Probe scheduler: one `PeriodicTimer` at 100 ms, round-robin over targets, so each target gets ≤ 5 pps and the
  total is ≤ 10 pps. Timestamps come from `Stopwatch`.
- Statistics:
  - median and p95 by sorting;
  - jitter = mean |Δ| of consecutive samples (Cloudflare definition);
  - MAD for spike detection;
  - autocorrelation of spike timestamps for F6;
  - bootstrap CI for before/after.
- Logging: `Log.Info` one line per phase. Never log full IPs of the user's public address. The Cloudflare
  `cf-meta-ip` header contains it, so discard it.
- Tests: pure functions (grading, AIM points, finding rules, CI verdict) get xUnit tests with recorded sample
  arrays. Network I/O is not unit-tested.

## 9. Open questions

1. The Wi-Fi location gate for `wlan_intf_opcode_rssi` / `channel_number` on 24H2+/26H2 must be tested on a PC with
   Wi-Fi enabled (here the radio was off). The same applies to whether an **elevated** process triggers the consent
   prompt.
2. Cloudflare speed endpoint terms for desktop apps are not published. Re-check behaviour (403/429 thresholds)
   before release, and keep the browser fallback.
3. Steam `GetSDRConfig` is undocumented in the Web API reference. Parse defensively and cache.
4. Exact Waveform grade bands are confirmed only via secondary sources (A+ < 5 / A < 30 consistent, B–F bands from
   BufferSpeed). The AIM score is from primary source code.

## Sources

- Cloudflare speedtest engine (MIT): https://github.com/cloudflare/speedtest. Files read:
  `src/config/defaultConfig.ts` (endpoints, measurement sequence, `loadedLatencyThrottle` 400 ms,
  `loadedLatencyMaxPoints` 20, `logAimApiUrl` default), `src/config/internalConfig.ts` (AIM point bands and
  experience thresholds), `src/Results/ScoresCalculations.ts`, `src/Results/MeasurementCalculations.ts` (jitter
  definition), `README.md`.
- Live endpoint behaviour observed on this PC: `https://speed.cloudflare.com/__down?bytes=N` (200 up to 99 999 999,
  403 from 1e8), `Server-Timing cfL4` RTT, colo TLV.
- Waveform bufferbloat test: https://www.waveform.com/tools/bufferbloat (403 to automated fetch). Grade bands as
  reported by https://bufferspeed.com/compare/waveform and https://speedtesthq.com/guides/diagnostics/test-for-bufferbloat.
- bufferbloat.net, "What can I do about Bufferbloat": https://www.bufferbloat.net/projects/bloat/wiki/What_can_I_do_about_Bufferbloat/
  (router SQM: fq_codel/CAKE; loaded latency target).
- RFC 7567 (AQM recommendations), RFC 8290 (FQ-CoDel), RFC 1191 (PMTUD), RFC 1812 §4.3.2.8 and RFC 4443 §2.4
  (ICMP rate limiting).
- Microsoft, Wi-Fi access and location changes: https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes
- Microsoft, WLAN_ASSOCIATION_ATTRIBUTES (signal quality ↔ dBm): https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_association_attributes
- Microsoft, GetPerTcpConnectionEStats (admin, enable collection): https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getpertcpconnectionestats
- Microsoft, ConnectionProfile.GetAttributedNetworkUsageAsync (capability `networkDataPlanProvisioning`): https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.connectionprofile.getattributednetworkusageasync
- Microsoft, Delivery Optimization reference (bandwidth limits, default 0 = dynamic; VPN keywords): https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference
- Microsoft, ms-settings URIs (`ms-settings:delivery-optimization-advanced`, `ms-settings:privacy-location`, `ms-settings:network-*`): https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
- Microsoft, TCP/IP performance known issues (auto-tuning repair): https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/tcpip-performance-known-issues
- Intel, gigabit adapter linking at 100 Mbps: https://www.intel.com/content/www/us/en/support/articles/000058908/ethernet-products.html
- LANCOM, downshift (gigabit needs four pairs): https://www.lancom-systems.com/docs/LCOS/Menuref/EN/topics/2_23_30_7.html
- Cisco, Ethernet 10/100/1000 auto-negotiation and duplex mismatch: https://www.cisco.com/c/en/us/support/docs/lan-switching/ethernet/10561-3.html.
  Parallel detection explanation: https://puck.nether.net/lists/cisco-nsp/1916.html
- Cisco, Real-Time Traffic over WLAN design guide (−67 dBm cell edge): https://www3-realm.cisco.com/en/US/docs/solutions/Enterprise/Mobility/RToWLAN/CCVP_BK_R7805F20_00_rtowlan-srnd_chapter_010.html
- Valve, Steam Datagram Relay (network config distribution, ping to data centers): https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay.
  Endpoint `https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730` observed live. Client fetching it:
  https://steamcommunity.com/app/730/discussions/0/3109142236159062543
- Blizzard, Running WinMTR (per-game IPs in a support article): https://support.blizzard.com/article/000027780
- Community evidence on Wi-Fi background-scan spikes (not authoritative): https://community.intel.com/t5/Wireless/Frequent-ping-spikes/td-p/426779,
  https://www.ghacks.net/?p=54335
- Existing Lycoris research (agreed, not contradicted): docs/research/general_tweaks.md §8, docs/research/gaming_tweaks.md §2 F and §3.
