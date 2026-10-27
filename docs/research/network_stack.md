# Network stack and "low ping" tweaks — research

Area: the Windows TCP/IP stack and the popular "lower your ping" tweaks. Owner request: «сетевые настройки должны
автоматически подбирать наилучшую конфигурацию для низкого пинга, или пункт, где это можно сделать вручную».

Scope note: NIC driver properties (interrupt moderation, EEE/green Ethernet, speed/duplex, buffers), Wi-Fi radio
settings and DNS-resolver choice are covered by the adapter/Wi-Fi/DNS research; they are only cross-referenced here.

**Bottom line.** In a modern game, the ping you see is set by distance to the server, your ISP's route, and queueing in
your router and modem. Games send their state over UDP: CS2 (Valve Steam Datagram Relay), Call of Duty (UDP 3074),
Dying Light (Steam networking), and Unreal Engine titles, whose default net driver is `UIpNetDriver`. The server tick
rate is fixed, typically 20–128 packets/s. Windows' TCP/IP knobs act on TCP, on throughput, or at the microsecond
scale. None of them shortens the milliseconds a UDP packet spends in transit. Microsoft says so about its own
low-latency NIC tuning: OS-induced latency "is usually measured in microseconds", while transit is "measured in
milliseconds", and the tuning "doesn't reduce the time a packet spends in transit" (Microsoft Learn, NIC tuning).

What MONOLITH can honestly do:

1. **Measure:** gateway versus internet RTT, jitter and loss; IPv4 versus IPv6; latency under load (bufferbloat); which
   transport the running game actually uses.
2. **Find real problems:** Wi-Fi instead of cable, adapter power saving, a bad link, background bulk transfers, and
   harmful leftovers from other "optimizers" (auto-tuning off, BBR2 congestion provider).
3. **Apply only evidence-backed fixes**, and only when the measurement or the drift check shows the problem.

Classifications below are binding per CLAUDE.md. Where an item already exists in `general_tweaks.md`,
`gaming_tweaks.md` or `TweakCatalog.Network.cs`, this report says whether it **agrees**. It never contradicts them
without new evidence.

---

## Summary

| # | Id | Item (RU title) | Class | Honest effect on in-game ping / jitter | Exists? |
|---|---|---|---|---|---|
| 1 | `tcp-autotuning-normal` | Автонастройка окна TCP — стандартная | **SAFE-DEFAULT** (repair) | None. Throughput of TCP downloads only | yes — agree |
| 2 | `tcp-ack-frequency` | Мгновенные TCP-подтверждения | **ADVANCED** | 0 for UDP games. For TCP games whose server uses Nagle: removes up to one delayed-ACK wait (≤40 ms on this build, 200 ms classic) on some messages | yes — agree; add a transport gate |
| 3 | `tcp-nodelay-registry` | TCPNoDelay / TcpDelAckTicks в реестре | **DO-NOT-INCLUDE** | None proven. TCPNoDelay is not a documented TCP-stack value; TcpDelAckTicks is Win2000/2003-era | agree (TCPNoDelay already refused) |
| 4 | `network-throttling-off` | Отключить NetworkThrottlingIndex | **ADVANCED** (never in a profile) | None. Cap is 10 000 packets/s and applies only during MMCSS playback; a game uses ~20–128 packets/s | yes — agree |
| 5 | `mmcss-system-responsiveness` | SystemResponsiveness = 0 | **DO-NOT-INCLUDE** | None. 0 is clamped to 20 by MMCSS | agree |
| 6 | `qos-reservable-bandwidth` | Снять «резерв 20 %» QoS | **DO-NOT-INCLUDE** | None (placebo) | agree |
| 7 | `qos-dscp-game-policy` | Приоритет DSCP для игр (QoS-политика) | **DO-NOT-INCLUDE** | None on a wired PC behind a typical home router. Marks only upload; ISPs ignore or bleach it | agree, with new evidence |
| 8 | `tcp-ecn` | ECN для TCP | **DO-NOT-INCLUDE** | None for UDP games | new |
| 9 | `rsc-off` | Отключить RSC | **DO-NOT-INCLUDE** | None (TCP only, µs scale; this NIC has no RSC) | agree |
| 10 | `rss-off` | Отключить RSS | **DO-NOT-INCLUDE** | None (µs scale; this NIC has no RSS) | agree |
| 11 | `tcp-chimney` | TCP Chimney Offload | **DO-NOT-INCLUDE** | None. Deprecated, not in the Win10/11 netsh global set | new |
| 12 | `udp-uro-off` | Отключить объединение UDP (URO) | **DO-NOT-INCLUDE** | None shown. Coalesces only datagrams that arrive together | new |
| 13 | `tcp-congestion-provider` | Алгоритм перегрузки TCP (BBR2/CTCP) | **DO-NOT-INCLUDE** to set. **SAFE-OPTIONAL** repair back to the Windows default | None for UDP. BBR2 on 24H2 can stall localhost TCP (Steam client) | new |
| 14 | `tcp-window-heuristics` | Эвристика масштабирования окна TCP | **INFO-ONLY** (drift check) | None (already disabled by default) | new |
| 15 | `tcp-legacy-registry` | Старые параметры TCP/IP в реестре | **DO-NOT-INCLUDE** | None. Ignored since Vista/2019 or irrelevant | new |
| 16 | `ipv6-disable` | Отключить IPv6 | **DO-NOT-INCLUDE** | None or negative | agree with Microsoft |
| 17 | `ipv6-prefer-ipv4` | Предпочитать IPv4, если IPv6 хуже | **ADVANCED**, gated by measurement | Only if the measured IPv6 path is worse. Here they are equal (2.1 vs 2.2 ms) | new |
| 18 | `teredo` | Teredo | **DO-NOT-INCLUDE** (status read-only) | None. Already disabled by default; Xbox party chat needs it | new |
| 19 | `netbios-llmnr-off` | Отключить NetBIOS и LLMNR | **DO-NOT-INCLUDE** as a ping tweak | None (security topic, not latency) | new |
| 20 | `nla-tweaks` | Отключить NLA / проверку подключения | **DO-NOT-INCLUDE** | None. Breaks the connectivity indicator and apps | new |
| 21 | `delivery-optimization-local` | Обновления без P2P-раздачи | **SAFE-DEFAULT** | None in the default LAN mode. Removes upload only if internet P2P was on | yes — agree |
| 22 | `background-transfers` | Фоновые загрузки во время игры | **MEASUREMENT** | **Real:** a saturating download or upload can add tens to hundreds of ms through bufferbloat | new |
| 23 | `game-mode-network` | Игровой режим и сеть | **INFO-ONLY** | None. Game Mode has no network component | new |
| 24 | `dns-over-https` | Шифрование DNS (DoH) | **ADVANCED** | None (privacy feature) | agree with general_tweaks |
| 25 | `hosts-file-tricks` | Трюки с файлом hosts | **DO-NOT-INCLUDE** | None. Stale IPs break matchmaking | agree with competitors.md |
| 26 | `net-baseline-latency` | Замер пинга, джиттера и потерь | **MEASUREMENT** | n/a (diagnosis) | new |
| 27 | `net-latency-under-load` | Пинг под нагрузкой (bufferbloat) | **MEASUREMENT** (opt-in) | n/a. Finds the one problem that really moves ping | new |
| 28 | `game-transport-detect` | Определение протокола игры (TCP/UDP) | **MEASUREMENT** | n/a. Decides whether item 2 can matter | new |

---

## Verified on this PC (read-only, 27.10.2026; nothing was changed — the shell was not elevated)

Windows 11 Pro 26H2 build 26300. Wired Ethernet "Realtek(R) PCI(e) Ethernet Controller" at 1 Gbps full duplex,
router 192.168.1.1, DNS = router, native IPv6 from the ISP (`2a0d:6fc2:…`), network profile **Public**.

- **TCP templates** (`Get-NetTCPSetting`, `netsh int tcp show global/supplemental/heuristics`):
  - Auto-tuning **Normal** (Local) in every template.
  - Congestion provider **CUBIC** (Compat: NewReno); add-on provider "default".
  - ECN **disabled**; window-scaling heuristics **disabled**; RFC 1323 timestamps allowed.
  - InitialRTO 1000 ms, MinRTO 300 ms, ICW 10 MSS.
  - DelayedAckFrequency 2; **Delayed ACK timeout 40 ms** (netsh supplemental).
  - RACK, TLP, HyStart, PRR, Fast Open on; pacing off; max SYN retransmissions 4.
  - This is the stock Windows 11 profile. Nothing to repair.
- **Global offloads:** RSS enabled, RSC enabled, Chimney Disabled (`Get-NetOffloadGlobalSetting`; chimney is no
  longer even listed by `netsh int tcp show global`). UDP Receive/Send Offload (URO/USO) enabled
  (`netsh int udp show global`).
- **Ethernet NIC:** PCI `VEN_10EC&DEV_8126` (RTL8126 5 GbE chip) running Microsoft's inbox `rt640x64.inf`
  9.1.412.2015 (section RTL8125Series).
  - `Get-NetAdapterRss/Rsc/Lso/ChecksumOffload/Uso -Name Ethernet` all return "not found". The driver exposes **no**
    RSS, RSC, LSO, checksum or USO capability, so every "disable offload" tweak is a literal no-op on this NIC.
  - Exposed properties: Speed & Duplex = Auto, Priority & VLAN, Interrupt Moderation = Enabled, Receive Buffers 512,
    Transmit Buffers 128, Network Address.
  - Whether the vendor driver should replace the inbox one belongs to the adapter/driver research.
- **Per-interface TCP values:** `Tcpip\Parameters\Interfaces\{21CA98F6-…}` has no TcpAckFrequency or TCPNoDelay
  (default).
- **MMCSS:** SystemResponsiveness 20, NetworkThrottlingIndex 10 (defaults).
- **QoS:** no QoS policies, no `Policies\…\Psched`, no `Tcpip\QoS\Do not use NLA`; QoS Packet Scheduler (ms_pacer)
  bound (default).
- **IPv6:** `DisabledComponents` absent (enabled). Prefix policy default: `::/0` precedence 40 > `::ffff:0:0/96` 35,
  so IPv6 is preferred. Ethernet IPv6 MTU 1460, IPv4 1500.
- **Teredo:** `netsh int teredo show state` → Type **disabled**, offline. `Get-NetTeredoConfiguration` Type Default.
- **NetBIOS:** NetbiosOptions 0 (use DHCP setting). No LLMNR policy. NCSI active probing on.
- **DoH:** only the built-in known-server table. DNS is the router, so DoH is not in use.
- **hosts:** no active entries. **Delivery Optimization:** DownloadMode CdnOnly (SettingsProvider), so
  `delivery-optimization-local` is already applied.

### Baseline measurements (ICMP, .NET Ping, read-only)

| Target | Interval | Lost | RTT avg / max | Jitter (mean abs. diff) |
|---|---|---|---|---|
| 192.168.1.1 (router) | 100 ms × 40 | 0 | 0.2 / 1 ms | 0.28 ms |
| 1.1.1.1 | 100 ms × 40 | 6 | 2.0 / 3 ms | 0.15 ms |
| 8.8.8.8 | 100 ms × 40 | 14 | 3.0 / 4 ms | 0.08 ms |
| 9.9.9.9 | 100 ms × 40 | 11 | 2.4 / 4 ms | 0.39 ms |
| 1.1.1.1 | 1 s × 20 | 0 | 2.1 / 3 ms | — |
| 8.8.8.8 | 1 s × 20 | 3 | 3.1 / 4 ms | — |
| 2606:4700:4700::1111 | 1 s × 20 | 0 | 2.2 / 4 ms | — |
| 2001:4860:4860::8888 | 1 s × 20 | 0 | 3.2 / 4 ms | — |

What this shows:

- The local path is excellent: 0.2 ms to the router, single-digit ms to Israeli anycast nodes.
- The "loss" at 10 pings/s to public resolvers disappears at 1/s, and is absent over IPv6 to the same operator. That
  is ICMP rate-limiting at the anycast servers, not packet loss on the line.
- **Implication for MONOLITH:** never report loss from fast ICMP to public DNS anycast. Probe at ≤1 packet/s, use the
  gateway as a control, and confirm across several targets.
- IPv4 and IPv6 latency are identical here, so "prefer IPv4" would gain nothing.

DNS lookup time (`Resolve-DnsName -DnsOnly`, 6 names × 3, includes PowerShell overhead):

| Resolver | Median | p90 |
|---|---|---|
| Router | 1.6 ms | 10.6 ms |
| 1.1.1.1 | 5.7 ms | 60.6 ms |
| 8.8.8.8 | 59.4 ms | 67 ms |

The router's cache is fastest. This confirms the existing decision that the DNS items are SAFE-OPTIONAL and never
default. DNS does not touch in-game ping in any case.

---

## What "automatic best configuration" can honestly mean

There is no stack configuration that is "best for ping" beyond the Windows defaults. This PC already runs them. An
honest auto mode is therefore **diagnose → fix only the found cause → re-measure**:

1. **Measure baseline** (`net-baseline-latency`): gateway RTT/jitter at 1 pps, two or three internet anycast targets
   over IPv4 and IPv6.
   - Gateway jitter above ~2 ms on a wired link points to a LAN or NIC problem.
   - Gateway high on Wi-Fi → recommend a cable; `wifi-power-saving-off` (existing).
2. **Check the link** (adapter research): Wi-Fi versus Ethernet, link speed and duplex, power management.
3. **Latency under load** (`net-latency-under-load`, opt-in because it uses bandwidth): idle versus loaded RTT.
   - A large delta means bufferbloat. The fix is in the router (SQM / "smart queue" / a bandwidth cap 5–10 % below the
     line rate) or in the launcher (limit Steam/Battle.net download speed, no downloads during gameplay).
   - Windows cannot fix the ISP-side queue. MONOLITH explains this and does not pretend otherwise.
4. **Background transfers during play** (`background-transfers`): name the process that moves the most bytes.
5. **Transport of the running game** (`game-transport-detect`): if the game keeps a TCP session to its server, offer
   `tcp-ack-frequency` (ADVANCED). If it uses UDP only, say that TCP tweaks cannot help.
6. **Drift repairs** (only when found):
   - `tcp-autotuning-normal` (exists);
   - `tcp-congestion-provider` repair: BBR2 or another non-default provider left by an optimizer → back to the Windows
     default;
   - heuristics enabled → report (INFO);
   - `delivery-optimization-local` (exists).
7. **Re-measure** and show the before/after numbers. If nothing changed, say so.

The manual place stays in Optimize → «Сеть» (existing category, one place per CLAUDE.md minimal-UI rule).
ADVANCED items stay opt-in there with their warnings. The measurements belong with the gaming readiness checks
(Dashboard) or a single network diagnostic panel. They must not be duplicated.

---

## 1. `tcp-autotuning-normal` — TCP receive-window auto-tuning → SAFE-DEFAULT (repair only). Agree.

- **What the myth says:** `netsh int tcp set global autotuninglevel=disabled` "fixes lag".
- **Mechanism:** the receive window grows to fill the bandwidth-delay product. Microsoft: "The default level is
  **Normal**". With it disabled, "for a 1Gbps connection that has a latency of 10 ms, the total achievable throughput
  is only 51 Mbps".
- **Effect on ping/jitter:** none. Game packets are small UDP datagrams and never reach a 64 KB window.
  - Turning auto-tuning off only slows Steam/Battle.net downloads.
  - It could in theory make one huge download fill the router queue less fast. That is a side effect of being slow,
    not a fix; the right fix for that is a download-rate limit.
- **This PC:** Normal in all templates, so the item reports "applied" and does nothing. The existing implementation
  (WMI `MSFT_NetTCPSetting`, policy and mixed-template refusal, netsh undo line) is correct.
- **Evidence:**
  - https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics
  - QoS "inbound TCP throughput level" policy events 16504–16508 (the ByPolicy case):
    https://learn.microsoft.com/en-us/windows-server/networking/technologies/qos/qos-policy-errors

## 2. `tcp-ack-frequency` — Nagle / delayed ACK (TcpAckFrequency per interface) → ADVANCED. Agree; add a gate.

- **Mechanism:**
  - Nagle (sender side) holds a small segment while earlier data is unacknowledged.
  - Delayed ACK (receiver side) waits for 2 segments or a timer before acknowledging.
  - Together they can stall a small message by one delayed-ACK timeout. Microsoft's nettracer blog describes exactly
    this interaction.
  - `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID}\TcpAckFrequency` = 1 makes Windows ACK
    every segment (KB 328890). Absent = 2. Effective after an adapter restart or reboot.
- **Who can benefit:**
  - Only games that carry real-time traffic over TCP **and** whose server uses Nagle. Examples: older MMOs (classic
    WoW-era clients, Tibia, older Lineage/Aion-era clients).
  - **Client-side** Nagle can only be disabled by the game itself (`TCP_NODELAY`). The registry cannot do it.
  - CS2 (Valve SDR, UDP), Call of Duty (UDP 3074), Dying Light (Steam networking) and Unreal-Engine-based AION 2
    (`UIpNetDriver` is UDP; NCSoft does not publish the protocol) do not carry gameplay over TCP. For them the effect
    is **0**.
- **Size of the effect when it applies:** at most one delayed-ACK interval on affected messages.
  - KB 328890 describes a 200 ms timer on the classic stack.
  - This Windows 11 build reports `Delayed ACK timeout 40 ms` (netsh supplemental), so the worst case here is ~40 ms on
    some server→client messages, not a constant ping reduction.
  - Leatrix Latency Fix (the tool that popularised the tweak) produced mixed user reports, including higher latency on
    a busy line.
- **Cost:** about twice as many ACK packets upstream; negligible on broadband.
- **Gate (new):** keep the item opt-in. Show "может помочь" only when `game-transport-detect` finds an established TCP
  connection from a running game process to a non-CDN remote endpoint. Otherwise show "игра работает по UDP — пункт
  не влияет".
- **Revert:** existing per-value journal (absent → delete).
- **Evidence:**
  - https://support.microsoft.com/kb/328890
  - https://learn.microsoft.com/id-id/archive/blogs/nettracer/tcp-delayed-ack-combined-with-nagle-algorithm-can-badly-impact-communication-performance
  - KB 935458 (Vista RTM ignored the value, fixed in SP1): https://support.microsoft.com/kb/935458
  - Leatrix discussion: https://www.wowinterface.com/forums/showthread.php?p=243842
  - Valve GameNetworkingSockets (UDP): https://github.com/ValveSoftware/GameNetworkingSockets
  - UE `UIpNetDriver`: https://dev.epicgames.com/documentation/unreal-engine/API/Plugins/OnlineSubsystemUtils/UIpNetDriver

## 3. `tcp-nodelay-registry` — TCPNoDelay, TcpDelAckTicks, global delayed-ACK template changes → DO-NOT-INCLUDE

- `TCPNoDelay` DWORD under `Tcpip\Parameters` or an interface key is **not documented for the Windows TCP stack**
  (only MSMQ has a value of that name). Nagle is a per-socket option (`TCP_NODELAY`) that the game sets. Already
  refused in `gaming_tweaks.md` §F and in the catalog comment. Agree.
- `TcpDelAckTicks` is documented only for Windows 2000/XP/2003-era stacks. There is no Microsoft documentation for
  it on the Vista+ stack.
- `netsh int tcp set supplemental … delayedacktimeout/delayedackfrequency` exists. But:
  - The Internet template is read-only on clients for these fields (Set-NetTCPSetting doc; KB 2472264 uses a Custom
    template plus transport filters).
  - It changes ACK behaviour for every TCP connection on the PC.
  - It gives no benefit for UDP games.
- **Effect:** none proven.
- **Evidence:**
  - https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-nettcpsetting
  - https://support.microsoft.com/kb/2472264
  - https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-2000-server/cc938206(v=technet.10)

## 4. `network-throttling-off` — NetworkThrottlingIndex → ADVANCED (placebo for ping, never in a profile). Agree.

- **Mechanism:** `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\NetworkThrottlingIndex`.
  Default 10; `0xFFFFFFFF` disables. Reboot.
  - Russinovich: when MMCSS-scheduled playback runs, NDIS is told to "indicate … at most 10 packets per millisecond
    (10,000 packets per second)". On 1500-byte frames that is ~15 MB/s.
  - It exists because long network DPCs made audio glitch.
- **Effect on ping:** none. The cap is 10 000 packets/s and applies only while media plays; a game flow is 20–128
  packets/s. It can matter for **throughput** (a 1 Gbps download while music plays), not for latency.
- **Known regressions / risk:**
  - Removing the cap brings back the original trade-off: heavy receive DPCs competing with the audio engine.
  - The 2007 multi-NIC bug that multiplied the throttling was fixed in Vista SP1.
  - No modern controlled measurement of either benefit or harm was found. Claims of raised DPC latency are unsourced.
- **Keep:** ADVANCED, with the existing warning text.
- **Evidence:**
  - https://learn.microsoft.com/en-us/archive/blogs/markrussinovich/vista-multimedia-playback-and-network-throughput
  - https://wiert.me/2010/02/09/windows-vista7-solution-for-multimedia-flash-throttles-your-network-networkthrottlingindex/

## 5. `mmcss-system-responsiveness` — SystemResponsiveness → DO-NOT-INCLUDE. Agree.

- Microsoft: "values below 10 and above 100 are clamped to 20". The popular `0` is literally ignored, and 100
  disables MMCSS.
- It only governs CPU share for MMCSS-registered threads; it has nothing to do with networking.
- **Effect on ping:** none. **This PC:** 20 (default).
- **Evidence:** https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service

## 6. `qos-reservable-bandwidth` — "Windows reserves 20 % of bandwidth" → DO-NOT-INCLUDE. Agree.

- **Mechanism:** gpedit «Ограничить резервируемую пропускную способность» / `HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched\NonBestEffortLimit`.
- 20 % is the **maximum** that QoS-aware applications (RSVP/GQoS) may reserve. With no reservation active, or an
  unused one, all bandwidth goes to normal traffic. Raymond Chen calls it a "placebo setting". KB 316666 explains
  the same.
- **Effect:** none. **This PC:** no Psched policy.
- **Evidence:**
  - https://devblogs.microsoft.com/oldnewthing/20061123-14/?p=28923
  - https://www.tweakhound.com/BadTweaks_pdfback/316666%20-%20Windows%20XP%20Quality%20of%20Service%20%28QoS%29%20Enhancements%20and%20Behavior.pdf

## 7. `qos-dscp-game-policy` — Policy-based QoS (DSCP 46 for game .exe) → DO-NOT-INCLUDE. Agree, with new evidence.

- **Mechanism:**
  - gpedit → Computer Configuration → Windows Settings → Policy-based QoS, or
    `New-NetQosPolicy -Name … -AppPathNameMatchCondition cs2.exe -IPProtocolMatchCondition UDP -DSCPAction 46`. It is
    stored as a machine GPO-store policy under `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS`. Undo:
    `Remove-NetQosPolicy`.
  - **Non-domain PCs:**
    - `New-NetQosPolicy` documents `-NetworkProfile` (Domain/Public/Private/All); unspecified means "any type of
      connection".
    - Field guides (Cisco for Windows 10/11, innovaphone, Netwrix) nevertheless report that DSCP is not stamped on
      standalone PCs until `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\QoS\Do not use NLA` = 1 is set and the PC
      rebooted. Cisco documents a DWORD; community scripts use REG_SZ "1".
    - Microsoft's event 16512 confirms the switch's meaning: "Selective application of QoS policies based on domain
      network category has been disabled. QoS policies will be applied to all network interfaces."
    - Because of this, MONOLITH could not even know that marking works without a packet capture.
- **Why it does not lower ping on a home connection:**
  1. **Upload only.** Microsoft: "QoS policies must be applied on the sending computer to affect its outbound
     traffic." Server→client game updates are never marked by your PC. Their queue sits at the ISP/modem, beyond
     Windows.
  2. **ISPs and the path.** DiffServ is per domain; residential ISPs do not honour customer marks for priority.
     Measurements by Custura/Fairhurst et al. found DSCP remarked (mostly to 0) on about one in five paths. "Passed
     unchanged" does not mean "prioritised".
  3. **Home router.** Stock firmware generally queues WAN traffic FIFO regardless of DSCP. DSCP matters only with an
     SQM router such as OpenWrt cake `diffserv4`. Even there, most of the gain comes from flow isolation and AQM,
     which protect a game flow without any mark (OpenWrt SQM docs; forum measurements show diffserv adding little
     over piece_of_cake).
  4. **Wi-Fi.** DSCP→WMM access-category mapping (RFC 8325) could help the local radio uplink. This PC is wired.
- **Effect:** none expected on this PC (wired, router with stock queueing, ISP edge). No controlled measurement shows
  a gain on home internet.
- **Risk:** low technically (reversible). But an unprovable policy is exactly what MONOLITH promises not to ship.
- **Evidence:**
  - https://learn.microsoft.com/en-us/windows-server/networking/technologies/qos/qos-policy-faq
  - https://learn.microsoft.com/en-us/windows-server/networking/technologies/qos/qos-policy-errors (event 16512)
  - https://learn.microsoft.com/en-us/powershell/module/netqos/new-netqospolicy
  - https://www.cisco.com/c/en/us/support/docs/quality-of-service-qos/qos-configuration-monitoring/221868-enable-dscp-qos-tagging-on-windows-machi.html
  - https://dl.ifip.org/db/conf/tma/tma2017/mnm2017_paper13.pdf
  - https://datatracker.ietf.org/doc/html/draft-custura-tsvwg-dscp-considerations-00
  - https://openwrt.org/docs/guide-user/network/traffic-shaping/sqm
  - https://forum.openwrt.org/t/sqm-layer-cake-or-piece-of-cake-whats-the-real-difference/2164

## 8. `tcp-ecn` — ECN capability → DO-NOT-INCLUDE

- **Mechanism:** `netsh int tcp set global ecncapability=enabled` / `Set-NetTCPSetting -EcnCapability`. Client
  default is disabled (confirmed here); servers enable it for DCTCP.
- **Effect on ping:** none for UDP games. For TCP it only helps when the bottleneck runs an ECN-marking AQM (SQM
  router). There it can replace a drop with a mark for downloads.
- **Risk:** reports of ECN negotiation problems and slow connection setup through some middleboxes and appliances
  (Lantronix, saotn, Microsoft Q&A). No gaming upside to justify it.
- **Evidence:**
  - https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-nettcpsetting
  - https://www.saotn.org/explicit-congestion-notification-ecn-slows-down-outbound-connections
  - https://learn.microsoft.com/en-us/answers/questions/2236017/how-to-resolve-ecn-negotiation-failure-between-win

## 9. `rsc-off` — Receive Segment Coalescing → DO-NOT-INCLUDE. Agree (gaming_tweaks §3, general_tweaks §8).

- **Mechanism:** `netsh int tcp set global rsc=disabled` or `Disable-NetAdapterRsc`. RSC merges TCP segments of one
  flow within one receive indication.
- **Effect:** TCP only, so 0 for UDP games. Microsoft positions RSC as reducing CPU and latency for receive-heavy
  workloads. NVIDIA's "disable for latency-sensitive traffic" advice is for µs-level datacenter RDMA/HPC NICs.
- **This PC:** the inbox Realtek driver exposes no RSC at all, so a toggle changes nothing.
- **Evidence:**
  - https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-R2-and-2012/hh997024(v=ws.11)
  - https://docs.nvidia.com/networking/display/winofv55052000/receive+segment+coalescing+(rsc)

## 10. `rss-off` — Receive Side Scaling → DO-NOT-INCLUDE. Agree.

- **Mechanism:** `netsh int tcp set global rss=disabled` / `Disable-NetAdapterRss`. RSS spreads receive processing of
  different flows across cores; one game flow always lands on one queue anyway.
- **Effect:** µs-scale CPU placement; not visible in ms ping. Disabling it costs throughput on multi-queue NICs.
- **This PC:** the Ethernet driver has no RSS; Wi-Fi adapters report RSS disabled.
- **Evidence:** https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics

## 11. `tcp-chimney` — TCP Chimney Offload → DO-NOT-INCLUDE

- Microsoft: "Don't use the offload features IPsec Task Offload or TCP Chimney Offload. These technologies are
  deprecated".
- `netsh int tcp show global` on Windows 11 no longer lists chimney; `Get-NetOffloadGlobalSetting` shows Disabled.
  Old guides' `netsh int tcp set global chimney=…` lines are obsolete.
- **Effect:** none.
- **Evidence:** https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics

## 12. `udp-uro-off` — UDP Receive Segment Coalescing Offload (URO, Windows 11 24H2+) → DO-NOT-INCLUDE

- **Mechanism:** `netsh int udp set global uro=disabled`. Default enabled; this PC shows Receive/Send Offload enabled
  globally.
  - The NIC coalesces UDP datagrams of the same flow that arrive together (designed for QUIC).
  - NDIS disables it automatically if a filter driver does not support it.
- **Effect:** a game flow at 20–128 packets/s rarely has two datagrams in one indication. There is no added timer, and
  no measurement shows a ping effect.
- **This PC:** the inbox Ethernet driver exposes no URO/USO, so it is a no-op here.
- **Rule:** an undocumented-benefit toggle on a brand-new path is exactly what MONOLITH should not touch.
- **Evidence:** https://learn.microsoft.com/en-us/windows-hardware/drivers/network/udp-rsc-offload

## 13. `tcp-congestion-provider` — CUBIC / BBR2 / CTCP / NewReno → DO-NOT-INCLUDE to set; SAFE-OPTIONAL repair to default

- **Mechanism:** `netsh int tcp set supplemental template=internet congestionprovider=bbr2|ctcp|cubic|dctcp|default`.
  The help on this build lists `none|ctcp|dctcp|cubic|bbr2|default`. Defaults here: Internet/Datacenter (and Custom)
  CUBIC, Compat NewReno.
- **Effect on ping:** congestion control governs the **send** rate of TCP only.
  - UDP game traffic: 0.
  - Downloads are governed by the server's algorithm, not yours. Only uploads change.
- **Known breakage:**
  - On Windows 11 24H2, BBR2 can stall low-RTT TCP. Loopback connections establish but exchange no data (Habr).
    SpeedGuide users lost the local Steam client and Plex until they reverted.
  - Microsoft's own ICCRG presentation already noted much lower BBRv2 throughput on low-latency/loopback paths in the
    Insider implementation.
  - CTCP is a 2006-era loss/delay hybrid with no gaming relevance.
- **Repair (new, SAFE-OPTIONAL):**
  - When a template's provider differs from the Windows default (for example BBR2 left by an "optimizer"), offer to
    restore it.
  - Journal the per-template original. Undo = the same netsh line with the original provider.
  - Refuse if a policy or a custom transport filter manages it.
  - Not exercised on this PC (already default). Validate on a test VM before shipping.
- **Evidence:**
  - https://habr.com/en/posts/860734
  - https://www.speedguide.net/forums/viewtopic.php?p=2444570
  - https://datatracker.ietf.org/meeting/112/materials/slides-112-iccrg-an-update-on-rledbat-and-bbrv2-00
  - https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-nettcpsetting

## 14. `tcp-window-heuristics` — window-scaling heuristics → INFO-ONLY

- **Mechanism:** `netsh int tcp set heuristics disabled|enabled` / `Set-NetTCPSetting -ScalingHeuristics`.
  - When enabled, Windows may restrict window scaling on networks it judges problematic.
  - Default and this PC: **disabled**. Old guides tell people to "disable" it, which is a no-op.
- **Effect on ping:** none.
- **Action:** read it during the drift check. If found enabled, mention it next to `tcp-autotuning-normal`. No catalog
  write until evidence shows harm.

## 15. `tcp-legacy-registry` — TcpWindowSize, GlobalMaxTcpWindowSize, Tcp1323Opts, DefaultTTL, MaxUserPort, TcpTimedWaitDelay, ServiceProvider "priorities", timestamps, InitialRTO/MinRTO, MaxSynRetransmissions → DO-NOT-INCLUDE

| Tweak | Why not |
|---|---|
| `TcpWindowSize`, `GlobalMaxTcpWindowSize`, `Tcp1323Opts` | Microsoft: since Windows Server 2019 the window can't be set in the registry; the old values are "no longer supported, and are ignored". The window is auto-tuned. |
| `DefaultTTL` 64 | Hop limit only; no latency effect. Here 128. |
| `MaxUserPort`, `TcpTimedWaitDelay` | Ephemeral-port exhaustion on servers. Dynamic range here 49152 + 16384 ports. |
| `ServiceProvider\*Priority` ("host resolution priority") | Reorders name-resolution providers only. DNS lookups are not in the game packet path. |
| Timestamps off, InitialRTO / MinRTO / MaxSynRetransmissions | Change TCP retransmission and setup on loss. They do not shorten RTT, and lower RTO values cause spurious retransmits. |

- **Effect:** none on game latency.
- **Evidence:** https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics

## 16. `ipv6-disable` — disable IPv6 → DO-NOT-INCLUDE (Microsoft guidance)

- Microsoft: "We don't recommend that you disable IPv6 or IPv6 components or unbind IPv6 from interfaces. If you do,
  some Windows components might not function."
- Incorrect `DisabledComponents=0xFFFFFFFF` adds a 5-second boot delay. Unbinding is "an unsupported Windows
  configuration".
- **This PC:** native IPv6 works; RTT to Cloudflare/Google is the same over IPv4 and IPv6.
- **Effect:** none or negative.
- **Evidence:** https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/configure-ipv6-in-windows

## 17. `ipv6-prefer-ipv4` — Prefer IPv4 over IPv6 → ADVANCED, only when measurement shows IPv6 is worse

- **Mechanism:** `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\DisabledComponents` = 0x20 (REG_DWORD).
  - Microsoft: "Recommended instead of disabling IPv6". It changes the prefix policy so `::ffff:0:0/96` outranks
    `::/0`.
  - Reboot required. Undo: restore the original value, or delete it if it was absent (absent here).
  - Only bit 0x20 may be set; any other bits are Microsoft's "disable" variants and are forbidden.
- **When:** `net-baseline-latency` shows IPv6 RTT clearly higher or IPv6 loss on dual-stack targets (for example
  ≥ +10 ms or loss at ≤1 pps confirmed on two targets). Typical of broken ISP IPv6 or tunnelled IPv6.
- **Effect:** affects only connections that resolve both families. Many game servers and relays are IPv4-only, so
  usually 0. On this PC: 0 (IPv6 = IPv4). Never default.
- **Risk:** a few IPv6-only services lose preference; nothing breaks (IPv6 stays enabled).
- **Evidence:** https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/configure-ipv6-in-windows

## 18. `teredo` → DO-NOT-INCLUDE (show status only)

- Microsoft: "ISATAP and Teredo are disabled by default in Windows". This PC confirms it (Type disabled, offline).
- "Disable Teredo for lower ping" is therefore a no-op.
- Enabling or "fixing" it is the Xbox app's job: Xbox party chat and some Xbox PC titles need Teredo, and the Xbox
  Networking page repairs it.
- `iphlpsvc` stays untouched (services.md: keep).
- **Effect:** none.
- **Evidence:**
  - https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/configure-ipv6-in-windows
  - https://labs.ripe.net/author/mirjam/usage-of-teredo-and-ipv6-for-p2p-on-windows-10-and-xbox-one

## 19. `netbios-llmnr-off` — NetBIOS over TCP/IP and LLMNR → DO-NOT-INCLUDE as a network/ping tweak

- **Mechanism:**
  - NetBIOS: `HKLM\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\Tcpip_{GUID}\NetbiosOptions` = 2
    (0 = DHCP default, here 0).
  - LLMNR: policy `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\EnableMulticast` = 0 (locks the setting).
- **Effect on ping:** none. Both are LAN name-resolution protocols used only when DNS fails.
- **Real topic:** security (LLMNR/NBT-NS poisoning). Microsoft is already "ramping down" both in favour of mDNS.
  Disabling them can break old NAS and printer name browsing.
- If ever offered, it needs its own privacy/security research. It must not appear under "lower ping".
- **Evidence:**
  - https://techcommunity.microsoft.com/blog/networkingblog/aligning-on-mdns-ramping-down-netbios-name-resolution-and-llmnr/3290816
  - https://www.ghacks.net/?p=178194

## 20. `nla-tweaks` — disabling Network Location Awareness, NCSI probing, network-profile "tricks" → DO-NOT-INCLUDE

- **Mechanism:** disabling `NlaSvc` / `netprofm`, or setting `NlaSvc\Parameters\Internet\EnableActiveProbing` = 0.
  Some guides also claim Private versus Public profile changes ping.
- **Effect:** none on ping.
  - NCSI sends a single HTTP probe on network change.
  - The profile selects firewall rule sets only.
  - Disabling NLA breaks the connectivity indicator, the Store and apps that check "online", and firewall-profile
    selection.
  - `NlaSvc` and `netprofm` are listed as core network services in services.md.
- The only legitimate NLA-related switch is the QoS `Do not use NLA` value, which item 7 rejects.
- **Evidence:** https://learn.microsoft.com/en-us/troubleshoot/windows-client/networking/domain-joined-machines-cannot-detect-domain-profile ; services.md.

## 21. `delivery-optimization-local` — Delivery Optimization HTTP-only → SAFE-DEFAULT. Agree.

- **Default mode 1 (LAN):** Windows uploads update pieces only to LAN peers, so there is no internet upload to hurt
  ping.
- **Mode 3 (Internet):** it can upload to internet peers and use uplink. The existing item (Settings-backed
  `DownloadMode` 0) removes peer upload in either case.
- DO background bandwidth is adaptive by default (`DOPercentageMaxBackgroundBandwidth` 0 = "dynamically adjusts"),
  so a hard cap is not needed for ping.
- **This PC:** already CdnOnly.
- **Effect:** 0 in the default mode. It removes a possible uplink load only if internet P2P had been enabled.
- **Evidence:**
  - https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference
  - https://support.microsoft.com/help/4040227

## 22. `background-transfers` — background downloads and uploads during play → MEASUREMENT (the one that really matters)

- **Why:** a single bulk TCP transfer that saturates the link fills the router or modem buffer. Every game packet then
  waits behind it. That is "latency under load" (bufferbloat), described by Gettys & Nichols in ACM Queue.
  - Example: an OpenWrt user's Waveform test showed +18 ms (download) and +38 ms (upload) under load without SQM, and
    +0 ms with cake.
  - Worse lines show hundreds of ms.
  - Typical sources: Steam/Battle.net updates (Steam has "Allow downloads during gameplay", per-game "Background
    downloads"; Battle.net has a download-bandwidth limit), OneDrive and cloud backup, browser downloads, torrents,
    Windows Update.
- **What MONOLITH can do (read-only):**
  - While a game runs, sample per-process network throughput. ETW `Microsoft-Windows-Kernel-Network` gives bytes
    sent/received per PID, the same source Resource Monitor uses. `GetPerTcpConnectionEStats` is an alternative.
  - Name the top consumers and link to the right switch: launcher setting, OneDrive pause, `ms-settings:windowsupdate`
    pause.
  - Existing `background-apps-consumer-off` (SAFE-OPTIONAL) covers Store apps only. That is fine for CPU, but rarely
    the bandwidth culprit.
- **Effect:** this is where real ms are won, and it does not need a system tweak.
- **Evidence:**
  - https://queue.acm.org/detail.cfm?id=2071893
  - https://openwrt.org/docs/guide-user/network/traffic-shaping/sqm
  - https://www.pcgamer.com/uk/steam-update-allows-background-downloading-while-playing-a-game

## 23. `game-mode-network` — "Game Mode prioritises network traffic" → INFO-ONLY

- Microsoft's description of Game Mode: it suppresses Windows Update driver installs and restart notifications during
  play, and focuses CPU/GPU resources on the game. No network prioritisation is documented anywhere.
- The `game-mode` tweak (gaming_tweaks.md) keeps its own classification. Its Details must not claim a ping effect.
- **Effect:** none on network latency.
- **Evidence:** https://news.xbox.com/en-us/2018/10/02/latest-october-2018-windows-update-gaming-features-3/

## 24. `dns-over-https` — DoH in Windows 11 → ADVANCED. Agree with general_tweaks §8.

- **What it is:** encryption of DNS queries ("protected from interception by untrusted third parties"). It works only
  for servers in the known-DoH table: Cloudflare, Google, Quad9 (present here).
  - "Encrypted only" fails resolution if the server is unreachable.
  - "Encrypted preferred" silently falls back to plain text.
- **Effect on ping:** none. DNS is resolved once per connection, outside the game packet path. HTTPS can add a few ms
  to the first lookup.
- **This PC:** DNS = router, so DoH is unavailable without first switching resolvers (`dns-cloudflare` / `dns-google`,
  SAFE-OPTIONAL). The measured router cache was the fastest resolver.
- **Evidence:** https://learn.microsoft.com/en-us/windows-server/networking/dns/doh-client-support

## 25. `hosts-file-tricks` — pinning game, launcher or matchmaking hosts in `hosts` → DO-NOT-INCLUDE. Agree.

- **Mechanism:** static name → IP lines in `%SystemRoot%\System32\drivers\etc\hosts`.
- **Effect on ping:** none on packets in flight. Game servers are chosen by matchmaking and relays such as Valve SDR,
  not by your resolver.
- **Risk:** pinned CDN or anycast IPs go stale and break launchers, updates and region selection. Defender flags
  hosts edits of Microsoft domains as `SettingsModifier:Win32/HostsFileHijack`.
- **This PC:** no active entries.
- **Evidence:** competitors.md §13; https://www.deskmodder.de/blog/2020/08/04/hosts-datei-mit-blockierten-microsoft-links-werden-vom-defender-als-win32-hostsfilehijack-angezeigt

## 26. `net-baseline-latency` — measurement (read-only, no admin)

- **Probes:** ICMP via `System.Net.NetworkInformation.Ping` (or `IcmpSendEcho2`) at **≤1 packet/s per target**.
  - Gateway as the LAN control.
  - Two or three anycast targets, IPv4 and IPv6 (for example Cloudflare 1.1.1.1 / 2606:4700:4700::1111, Google
    8.8.8.8 / 2001:4860:4860::8888).
- **Report:** min, median, p95 RTT; jitter as the mean absolute difference of consecutive samples; loss only when it
  is confirmed on the gateway or on ≥2 targets.
- **Pitfalls:**
  - This PC showed spurious "loss" at 10 pps from ICMP rate limiting.
  - ICMP is not prioritised like game UDP. Use it as a trend, not the in-game number.
- **Decision outputs:** gateway jitter or loss → LAN/Wi-Fi/NIC problem; IPv6 much worse than IPv4 → offer
  `ipv6-prefer-ipv4`; everything low → "сеть ПК в порядке, пинг определяется расстоянием до сервера".

## 27. `net-latency-under-load` — bufferbloat test (opt-in; consumes bandwidth)

- **Method:**
  1. Measure idle RTT to the gateway and an anycast target.
  2. Start a ~10–15 s download, then upload, from a large CDN over HTTPS. Sample RTT every 100–200 ms during each.
  3. Report the delta, like the Waveform or Cloudflare speed-test "loaded latency".
- **Rules:** ask first (it uses data and briefly loads the line), download to memory only (nothing is saved), and
  use a fixed allow-list of test hosts (HTTPS, certificate-validated).
- **Interpretation:** an added delay above ~30 ms means the router has no smart queue management.
  - Recommendation (no Windows change): enable SQM/"Smart Queue"/QoS in the router, or cap it 5–10 % below the line.
  - Limit launcher download speed; never download during play.
- MONOLITH cannot fix the ISP-side queue and must say so.

## 28. `game-transport-detect` — which protocol the running game uses (read-only)

- **Method:** `GetExtendedTcpTable` / `GetExtendedUdpTable` (iphlpapi, `TCP_TABLE_OWNER_PID_ALL`,
  `UDP_TABLE_OWNER_PID`) for the game's PID, or `Get-NetTCPConnection -OwningProcess`.
  - Established TCP connections to non-443/80 remote ports (excluding launcher, overlay and anti-cheat processes)
    point to a TCP game protocol.
  - UDP endpoints only point to a UDP game.
- **Use:** gate `tcp-ack-frequency`, and tell the user plainly whether TCP tweaks can matter for this game. Expected
  results for this PC's library:
  - CS2: UDP (SDR). Call of Duty: UDP 3074.
  - Dying Light: Steam networking, UDP.
  - AION 2: verify at runtime. Unreal default is UDP; NCSoft does not document it. Its servers are in Korea/Taiwan, so
    ping from Israel is distance-bound and no local setting changes it.

---

## Sources (all accessed 27.10.2026)

- Microsoft Learn — Network adapter performance tuning (auto-tuning, low-latency µs vs ms, deprecated TCP parameters, Chimney): https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics
- Microsoft Learn — Set-NetTCPSetting: https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-nettcpsetting
- Microsoft Learn — MMCSS (SystemResponsiveness clamping): https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
- Russinovich — Vista multimedia playback and network throughput: https://learn.microsoft.com/en-us/archive/blogs/markrussinovich/vista-multimedia-playback-and-network-throughput
- KB 328890 (TcpAckFrequency): https://support.microsoft.com/kb/328890 ; KB 935458: https://support.microsoft.com/kb/935458 ; KB 2472264: https://support.microsoft.com/kb/2472264
- Microsoft nettracer — delayed ACK + Nagle: https://learn.microsoft.com/id-id/archive/blogs/nettracer/tcp-delayed-ack-combined-with-nagle-algorithm-can-badly-impact-communication-performance
- Raymond Chen — QoS bandwidth reservation placebo: https://devblogs.microsoft.com/oldnewthing/20061123-14/?p=28923
- Microsoft Learn — QoS policy FAQ / events / New-NetQosPolicy: https://learn.microsoft.com/en-us/windows-server/networking/technologies/qos/qos-policy-faq ; https://learn.microsoft.com/en-us/windows-server/networking/technologies/qos/qos-policy-errors ; https://learn.microsoft.com/en-us/powershell/module/netqos/new-netqospolicy
- Cisco — Enable DSCP tagging on Windows 10/11 ("Do not use NLA"): https://www.cisco.com/c/en/us/support/docs/quality-of-service-qos/qos-configuration-monitoring/221868-enable-dscp-qos-tagging-on-windows-machi.html
- Custura, Venne, Fairhurst — DSCP modification pathologies (MNM 2017): https://dl.ifip.org/db/conf/tma/tma2017/mnm2017_paper13.pdf ; IETF draft: https://datatracker.ietf.org/doc/html/draft-custura-tsvwg-dscp-considerations-00
- OpenWrt SQM documentation: https://openwrt.org/docs/guide-user/network/traffic-shaping/sqm
- Gettys & Nichols — Bufferbloat: Dark Buffers in the Internet (ACM Queue): https://queue.acm.org/detail.cfm?id=2071893
- Microsoft Learn — Configure IPv6 for advanced users: https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/configure-ipv6-in-windows
- Microsoft Learn — UDP RSC offload (URO): https://learn.microsoft.com/en-us/windows-hardware/drivers/network/udp-rsc-offload
- Microsoft (archived TechNet) — RSC: https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-R2-and-2012/hh997024(v=ws.11) ; NVIDIA WinOF RSC: https://docs.nvidia.com/networking/display/winofv55052000/receive+segment+coalescing+(rsc)
- BBR2 on 24H2: https://habr.com/en/posts/860734 ; https://www.speedguide.net/forums/viewtopic.php?p=2444570 ; Microsoft ICCRG slides: https://datatracker.ietf.org/meeting/112/materials/slides-112-iccrg-an-update-on-rledbat-and-bbrv2-00
- Windows TCP team — Algorithmic improvements (PRR/TLP/RACK): https://techcommunity.microsoft.com/blog/networkingblog/algorithmic-improvements-boost-tcp-performance-on-the-internet/2347061
- ECN issues: https://www.saotn.org/explicit-congestion-notification-ecn-slows-down-outbound-connections ; https://learn.microsoft.com/en-us/answers/questions/2236017/how-to-resolve-ecn-negotiation-failure-between-win
- Microsoft Learn — DoH client: https://learn.microsoft.com/en-us/windows-server/networking/dns/doh-client-support
- NetBIOS/LLMNR ramp-down coverage: https://www.ghacks.net/?p=178194
- Delivery Optimization reference: https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference
- Xbox Wire — Game Mode October 2018: https://news.xbox.com/en-us/2018/10/02/latest-october-2018-windows-update-gaming-features-3/
- Valve GameNetworkingSockets: https://github.com/ValveSoftware/GameNetworkingSockets ; Unreal UIpNetDriver: https://dev.epicgames.com/documentation/unreal-engine/API/Plugins/OnlineSubsystemUtils/UIpNetDriver
