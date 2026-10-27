# Network adapter properties (wired + Wi-Fi): what helps ping and what does not

Research for the owner request «сетевые настройки автоматически подбирают наилучшую конфигурацию для низкого пинга, или пункт, где можно сделать это вручную». Scope: NIC advanced properties (Realtek PCIe GbE/2.5G/5G, Intel I225/I226, Killer E3x00, Qualcomm FastConnect, Intel Wi-Fi 6E/7) and adapter power management. Related areas (DNS, TCP, bufferbloat/router, power plan) are covered elsewhere and only referenced.

**Verdict.** NIC properties cannot lower online ping in a way a player notices. Online RTT is tens of milliseconds. Every NIC knob acts on microseconds. Microsoft says so directly: OS-induced latency "is usually measured in microseconds", transit is "measured in milliseconds", and the low-latency tuning "doesn't reduce the time a packet spends in transit" ([MS NIC tuning](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics)). The adapter area pays off in three places:
1. **Detecting real faults.** These are Wi-Fi used instead of a cable, a 100 Mbit or half-duplex link (bad cable, port or forced setting), link drops, and a generic driver.
2. **Repairing harmful leftovers.** The main example is a speed/duplex value forced by an "optimizer".
3. **Fixing a link that drops.** Turning off EEE is the documented workaround on Intel I225-V/I226-V and Killer E3100.

Everything else is either placebo for ping or makes things worse: interrupt moderation off, buffers, offloads, flow control, jumbo frames, and the Wi-Fi vendor knobs. In our classifications this means **ADVANCED, INFO-ONLY or DO-NOT-INCLUDE.**

**On this PC the automatic mode would change nothing.** The link is 1 Gbps full duplex. There are no errors and no link drops since install. The receive and transmit buffers are already at the maximum. The driver exposes no EEE, flow-control or offload keywords at all. The honest result for this PC is «Сетевой адаптер настроен правильно».

---

## Summary table

| id | Title (RU UI) | Class | Effect on ping / jitter / loss | Admin | Link drop on apply |
|---|---|---|---|---|---|
| `net-adapter-audit` | Проверка сетевого адаптера | MEASUREMENT | None by itself. Finds real causes: Wi-Fi instead of cable, 10/100 Mbit, half duplex, errors, link drops, generic driver | no (read) | — |
| `wifi-link-audit` | Проверка Wi-Fi: диапазон и сигнал | MEASUREMENT | None by itself. Finds 2.4 GHz / weak signal; the main advice is "use a cable" | no | — |
| `net-adapter-before-after` | Замер пинга до и после | MEASUREMENT | None. Proves or disproves any manual change | no | — |
| `net-speed-duplex-auto` | Скорость и дуплекс — автосогласование | SAFE-OPTIONAL (repair, only when forced) | Removes loss from a duplex mismatch (large). On a healthy link: none | yes | yes, 1–6 s |
| `net-eee-off` | Отключить энергосберегающий Ethernet (EEE) | SAFE-OPTIONAL (only when link drops are found) | Ping: none (EEE wake time is microseconds). Removes disconnects on affected PHYs | yes | yes, 1–6 s |
| `net-interrupt-moderation-off` | Модерация прерываний — выключить | ADVANCED (manual only) | Microseconds per packet. Not visible in online ping. More CPU/DPC under downloads | yes | yes, 1–6 s |
| `net-receive-buffers-max` | Буферы приёма — максимум | ADVANCED (manual, raise-only) | Ping: none. Can reduce NIC drops under heavy downloads | yes | yes, 1–6 s |
| `net-allow-turn-off-device` | Разрешить отключение устройства для экономии энергии | INFO-ONLY (was SAFE-OPTIONAL for Wi-Fi in general_tweaks.md, see Corrections) | None during play. Only the after-sleep / idle behaviour changes | read: yes | — |
| `net-realtek-power-saving` | Энергосбережение Realtek (Green Ethernet, Power Saving Mode, Gigabit Lite) | INFO-ONLY | None on an active link | no | — |
| `net-wake-on-lan` | Пробуждение по сети (Wake on LAN) | INFO-ONLY | None | no | — |
| `net-driver-generic` | Стандартный драйвер Microsoft для сетевой карты | INFO-ONLY → Drivers page | No proven ping effect. Features and vendor fixes are missing | no | — |
| `net-offloads-off` | Отключение разгрузок (LSO, контрольные суммы, RSS) | DO-NOT-INCLUDE (agrees with gaming_tweaks.md §3) | None or worse. Microsoft's low-latency guidance says to keep them on | — | — |
| `net-flow-control-off` | Отключить управление потоком (Flow Control) | DO-NOT-INCLUDE | None proven. Can add drops | — | — |
| `net-jumbo-frames` | Jumbo-кадры | DO-NOT-INCLUDE | None for internet traffic (MTU 1500). Risk of black holes / link loss | — | — |
| `net-misc-props` | Прочие свойства (Priority & VLAN, MAC-адрес, ARP/NS offload) | DO-NOT-INCLUDE | None | — | — |
| `wifi-preferred-band` | Предпочитаемый диапазон Wi-Fi | ADVANCED (only when the audit finds 2.4 GHz while 5/6 GHz is usable) | Can cut jitter/loss when 2.4 GHz is congested. Can hurt at range | yes | yes, reconnect |
| `wifi-roaming-aggressiveness` | Агрессивность роуминга | DO-NOT-INCLUDE (agrees with general_tweaks.md §8) | None proven. Mis-set it causes sticky or flapping clients | — | — |
| `wifi-mimo-power-save` | Энергосбережение MIMO (SMPS) | DO-NOT-INCLUDE (agrees) | None proven | — | — |
| `wifi-throughput-booster` | Усилитель пропускной способности | DO-NOT-INCLUDE | Upload throughput only. Intel: hurts other clients | — | — |
| `wifi-uapsd` | Поддержка U-APSD | DO-NOT-INCLUDE | None. Intel notes AP interoperability issues | — | — |
| `wifi-width-mode-manual` | Ширина канала / режим Wi-Fi вручную | DO-NOT-INCLUDE | Usually worse (20 MHz, legacy modes) | — | — |
| `wifi-background-scan-off` | Отключить фоновое сканирование Wi-Fi | DO-NOT-INCLUDE | Anecdotal spike reduction. Breaks auto-reconnect | — | — |
| `wifi7-mlo` | Wi-Fi 7 MLO | INFO-ONLY | Possible jitter/latency gains under contention. Needs a Wi-Fi 7 router and a current driver | — | — |

Restart time: the 1–6 s range comes from Microchip ("typically a second or more") and Intel's IEEE time-to-link measurements (≈2.5–5.7 s). It was **not measured here**, because changes were forbidden.

---

## 1. What was verified on this PC (read-only, 27.10.2026)

| Fact | Value | How read |
|---|---|---|
| Board | MSI PRO X870-P WIFI (MS-7E47) | `Win32_BaseBoard` |
| OS installed | 26.10.2026 17:20 (one day before this research) | `Win32_OperatingSystem.InstallDate` |
| Ethernet chip | **Realtek RTL8126 (5GbE)** `PCI\VEN_10EC&DEV_8126&SUBSYS_7E471462` | `Get-PnpDeviceProperty` |
| Ethernet driver | **Microsoft inbox `rt640x64.inf` / `rt640x64.sys` 9.1.412.2015** (file description "Realtek 8125/8136/8168/8169 NDIS 6.40") | `Get-NetAdapter`, `Win32_PnPSignedDriver`, file version |
| INF section | `RTL8125Series.ndi.NT`, matched by the generic line `%RTL81xx.DeviceDesc% = RTL8125Series.ndi,, PCI\VEN_10EC&DEV_8126` | `C:\Windows\INF\rt640x64.inf` lines 530/550 |
| What that section exposes | `*SpeedDuplex` (0,1,2,3,4,6,2500, so **no 5G option**), `*PriorityVLANTag`, `*InterruptModeration` (0/1, =1), `*ReceiveBuffers` 32–512 (=512, max), `*TransmitBuffers` 32–128 (=128, max), `NetworkAddress` | `Get-NetAdapterAdvancedProperty -AllProperties` + INF `[Speed2.5G.reg]`, `[TxRxBuf.reg]` |
| What it deletes on install | `[del.default.reg]` removes `*EEE`, `EEE`, `AdvancedEEE`, `EnableGreenEthernet`, `GigaLite`, `PowerSavingMode`, `AutoDisableGigabit`, `*FlowControl`, `*JumboPacket`, all checksum/LSO keywords, `*RSS`, `*NumRssQueues`, WoL, `*PMARPOffload`, `*PMNSOffload`, `*SelectiveSuspend`, `*ModernStandbyWoLMagicPacket` | INF |
| Offload/RSS via cmdlets | Ethernet absent from `Get-NetAdapterRss/Lso/ChecksumOffload` output. So **no RSS: all receive DPCs on one core** | cmdlets |
| Hidden values | `ASPM`=0, `CLKREQ`=0, `ForceMode`=0 (no DisplayName) | Class key `…\{4d36e972-e325-11ce-bfc1-08002be10318}\0001` |
| Link | Up, 1 Gbps, full duplex (`MSFT_NetAdapter.Speed`=1000000000, `FullDuplex`=True, `MediaDuplexState`=2). The router port is most likely 1GbE, so 1 G is normal here | CIM, no admin |
| Errors | `ReceivedPacketErrors`=0, `OutboundPacketErrors`=0. `ReceivedDiscardedPackets`=12 411 of 5.4 M, did not grow during the ping test. NDIS counts unknown-protocol frames as discards, so this is **not** a buffer-overflow signal | `Get-NetAdapterStatistics` |
| Link drops | None. `NetworkProfile/Operational` 10000 (connected) only at the 10 boots, no 10001. No `rt640x64` events | event logs |
| Power management | `Get-NetAdapterPowerManagement` / CIM `MSFT_NetAdapterPowerManagementSettingData` **fails non-elevated** ("A device attached to the system is not functioning", Win32 error 31) for both adapters. Realtek Class key has no `PnPCapabilities` (= 0) | cmdlets, registry |
| Wi-Fi | Qualcomm FastConnect 7800 Wi-Fi 7 HBS, driver **3.0.0.909 (2023-07-04)**, WDF/WiFiCx (`Device Parameters\WDF`: `IdleInWorkingState`=1, `WakeFromSleepState`=1), Class key `PnPCapabilities`=16. Radio "Software Off", disconnected. Secondary interface «Беспроводная сеть 4» (HBS) plus a non-present ghost «Беспроводная сеть 2» | `netsh wlan show drivers/interfaces`, registry |
| Wi-Fi properties | `roamPolicy` (1–5, =3 Medium), `StaPreferredBand` (1 none / 2 2.4 / 3 5 / 4 6 GHz, =1), `StaWirelessMode` (=0 all incl. 11be), `PreferredChanWidth2G/5G` (=Auto), `*LsoV2IPv4/6`=1, `*RSS`=0, checksum=3, `*WakeOnMagicPacket`/`*WakeOnPattern`=1, **`*IdleRestriction`=1**. There are **no** MIMO power save, throughput booster or U-APSD keywords: those are Intel-only | `-AllProperties` |
| Cmdlet surface | `Set-NetAdapterPowerManagement` on build 26300 has **no `-AllowComputerToTurnOffDevice`** parameter (only ArpOffload, D0PacketCoalescing, DeviceSleepOnDisconnect, NSOffload, RsnRekeyOffload, SelectiveSuspend, WakeOnMagicPacket, WakeOnPattern) | `(Get-Command …).Parameters` + Microsoft Learn |

**Ping baseline over the wired link.** These were measured with `System.Net.NetworkInformation.Ping` (no admin) and a Stopwatch.

| Target | Rate | Loss | p50 | p95 | max | mean abs Δ (jitter) |
|---|---|---|---|---|---|---|
| 192.168.1.1 (router) | 20/s × 100 | **5 %** | 0.94 ms | 2.76 ms | 7.74 ms | 0.70 ms |
| 1.1.1.1 | 10/s × 60 | **8 %** | 2.40 ms | 3.40 ms | 4.28 ms | 0.30 ms |
| 8.8.8.8 | 10/s × 60 | **17 %** | 3.78 ms | 4.61 ms | 6.00 ms | 0.24 ms |
| 192.168.1.1 | **2/s × 40** | **0 %** | 0–2 ms (integer API) | | | |
| 1.1.1.1 | **2/s × 40** | **0 %** | | | | |

The "loss" at 10–20 pings/s is **ICMP rate limiting** by the router and the public resolvers, not packet loss. At 2/s it vanished, and the NIC discard counter did not move. The rule for MONOLITH's measurement code is therefore: never probe faster than ~2 pings/s per target, and never call ICMP loss "packet loss" without a second target or a slow rate. The router answers ICMP in ~1 ms with spikes to ~8 ms. That is typical for a consumer router's CPU answering pings, and it is not caused by the NIC.

---

## 2. Common mechanism (all property items)

**Read (no admin, locale-independent).** Use WMI `root\StandardCimv2`. In C# use System.Management, like `ReadTcpAutotuning()`.
- `MSFT_NetAdapter`: InterfaceGuid, Name, InterfaceDescription, ComponentID, `NdisPhysicalMedium` (14 = 802.3, 9 = native 802.11), `MediaConnectState`, `Speed` (bps), `FullDuplex`, `MediaDuplexState` (0 unknown, 1 half, 2 full), DriverProvider, DriverVersionString, DriverDate, HardwareInterface, Virtual.
- `MSFT_NetAdapterAdvancedPropertySettingData` (what `Get-NetAdapterAdvancedProperty -AllProperties` returns): `RegistryKeyword`, `RegistryValue`, `ValidRegistryValues`, `DefaultRegistryValue`, `NumericParameterMinValue/MaxValue`, `DisplayName`/`DisplayValue`. **DisplayName and DisplayValue come from the driver's INF strings and may be localized: use them for display only and match by `RegistryKeyword`.**
- The backing store is `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\NNNN`. Find NNNN by `NetCfgInstanceId == InterfaceGuid`. Values are REG_SZ. `Ndi\params\<keyword>` holds type, default, min, max and the enum. This key is readable without admin (verified).
- Keywords **without** a DisplayName (`ASPM`, `CLKREQ`, `ForceMode`, `*IfType`, …) are driver-internal. MONOLITH never touches them.

**Write (admin).** Set-NetAdapterAdvancedProperty: "Changes are made directly into the registry". Without `-NoRestart` it restarts the adapter, because "Many advanced properties require restarting the network adapter" ([Set-NetAdapterAdvancedProperty](https://learn.microsoft.com/en-us/powershell/module/netadapter/set-netadapteradvancedproperty)). Microsoft's driver docs say the same of the Advanced tab: "The miniport driver is automatically restarted after a change" ([selective suspend keywords](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/standardized-inf-keywords-for-ndis-selective-suspend)).
- Use the same pattern as `DnsScript`: a PowerShell script through `RunJournaled`. It resolves the adapter by `InterfaceGuid` at run time, because names and ifIndex change. It then checks that the keyword exists **and** that the target value is in `ValidRegistryValues` (enum) or within min/max (int). Then it runs `Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword '<kw>' -RegistryValue '<v>' -ErrorAction Stop`.
- Journal target: `cmd:nicprop:{guid}:{keyword}`. Revert command: the same script with the original value string.
- JournalTrust needs a new rebuild rule. Decode the script, take the GUID, the keyword (from a hard whitelist) and the value (`^[0-9]{1,6}$`), rebuild it with the catalog function and compare, as is done for `dns4/6:` and `powermode:ac`.
- Do **not** write the Class key through `ChangeSession.SetRegistry` alone. The value would change without the restart, and the undo would not restart the adapter either.

**Side effects to show in the confirmation dialog.**
- The link drops for ~1–6 s on Ethernet; Wi-Fi re-associates and runs DHCP.
- Downloads, voice chat and **online matches drop**.
- Refuse to apply while a game from the Games library is running.
- Never batch several adapter restarts silently. Apply all keywords of one adapter with `-NoRestart` and then one `Restart-NetAdapter`, or accept one restart per keyword and say so.

**Revert precision.** The INF writes the base value at install (for example `HKR,,*InterruptModeration,0,"1"`), so the value normally exists and the undo restores the exact original string. If the value was absent (the driver used its INF default), the undo writes the `DefaultRegistryValue` explicitly. The effective value is the same. This is the same convention as the powercfg items, and the journal record says so. `Reset-NetAdapterAdvancedProperty` exists, but it needs the (localized) DisplayName and resets to the "factory default", which is not necessarily the original. Do not use it for undo.

**Drift.**
- A driver update or reinstall rewrites these values. The inbox INF's `del.default.reg` deletes them and `AddReg` re-creates the defaults. After a driver change the row shows "not applied", which is correct.
- The undo must treat a keyword that no longer exists (a different driver) as "nothing to restore" and must not fail.
- Physical NICs bound to a Hyper-V external switch carry no IP or gateway, so `PhysicalAdapters()` already skips them. Keep it that way.

---

## 3. What "automatic" and "manual" should mean (proposal)

**Automatic («Подобрать автоматически»)** = run `net-adapter-audit` + `wifi-link-audit` + `net-adapter-before-after` (baseline). Then offer only:
1. `net-speed-duplex-auto`, if `*SpeedDuplex` ≠ 0 (forced);
2. `net-eee-off`, if link drops were recorded on that adapter **and** it exposes an EEE keyword;
3. advice without a toggle: "connect a cable", "replace the cable/port" (100 Mbit / half duplex / errors), "install the vendor LAN or Wi-Fi driver" (link to the Drivers page), "switch the router/band to 5/6 GHz".

Nothing else is ever chosen automatically. Interrupt moderation, buffers and preferred band stay manual.

**Manual («Вручную»)** = the ADVANCED rows (`net-interrupt-moderation-off`, `net-receive-buffers-max`, `wifi-preferred-band`) as ordinary Optimize → Network tweak rows. Each row:
- applies per active physical adapter that actually exposes the keyword (otherwise «не поддерживается драйвером»);
- carries a "measure before/after" button;
- has an honest Warning text.

There is **no generic property editor**. That is Device Manager's job, and an editor would let users set the DO-NOT-INCLUDE values. This keeps the "one function, one place" rule: everything lives in Optimize → Network.

---

## 4. Items

### `net-adapter-audit`: Проверка сетевого адаптера → **MEASUREMENT**
- **What it checks (all no admin):**
  - the default-route adapter (Wi-Fi or Ethernet), and whether an Ethernet NIC exists while Wi-Fi carries the traffic;
  - negotiated speed against the NIC's capability (max of `*SpeedDuplex` ValidRegistryValues, or the model);
  - duplex; error counters; link drops (`Microsoft-Windows-NetworkProfile/Operational` 10001 not at shutdown, plus vendor events);
  - driver provider (Microsoft inbox vs vendor) and driver date.
- **Rules and honest wording:**
  - *Wi-Fi in use while an Ethernet port exists* → the single most effective "ping fix". Wi-Fi adds contention, retries and power-save latency.
  - *10/100 Mbit on a gigabit-capable NIC with auto-negotiation on* → likely a 2-pair or damaged cable, a bad port, or an old switch/PLC. 1000BASE-T always auto-negotiates over all four pairs (Microchip KSZ9567R datasheet: "For 1000BASE-T mode, auto-negotiation is always required"). The 100 Mbit link itself adds only ≤0.12 ms per 1500-byte frame (12 000 bit / 100 Mbit/s). The real harm is throughput: with an ISP plan above 100 Mbit/s, downloads saturate the LAN link and queue, so latency **under load** rises. Show it as a problem, not as "your ping is high because of this".
  - *1 Gbps on a 2.5/5G NIC* → info only (the router or switch port is 1 GbE). No ping effect. **This PC.**
  - *Half duplex* → red: collisions and loss, caused by a forced setting or a duplex mismatch.
  - *`ReceivedPacketErrors`/`OutboundPacketErrors` growing* → cable or EMI. Do **not** use `ReceivedDiscardedPackets`: it includes frames for unbound protocols (it stayed flat here during traffic).
- **Effect:** none by itself. It is the part that finds real problems.

### `wifi-link-audit`: Проверка Wi-Fi: диапазон и сигнал → **MEASUREMENT**
- **Read** through the Native Wi-Fi API (`WlanQueryInterface`: `wlan_intf_opcode_current_connection` → PHY type, rx/tx rate, signal quality; `wlan_intf_opcode_channel_number`; `wlan_intf_opcode_rssi`). Never parse `netsh wlan show interfaces`: it is localized (Russian here).
- **Rules:**
  - 2.4 GHz → suggest 5/6 GHz or a cable.
  - Weak signal → move closer or use a cable.
  - Periodic RTT spikes to the gateway on Wi-Fi only → background scans (info; see `wifi-background-scan-off`).
  - A Wi-Fi 7 adapter with an old driver → Drivers page.
- **This PC:** Wi-Fi is off (Software Off), so nothing to measure.

### `net-adapter-before-after`: Замер пинга до и после → **MEASUREMENT**
- ICMP to the gateway and 1–2 anycast resolvers (and, if another research item provides it, the game region) at **≤2 pings/s** for 30–60 s. Report p50, p95, max, jitter (mean |Δ|) and loss. Run it before and after any manual change and show the difference with its noise band.
- **Evidence here:** 20 pings/s to the router gave 5 % false loss and 10/s to 8.8.8.8 gave 17 %. At 2/s both gave 0 %.
- Latency under load (bufferbloat) is a separate research area; it is the measurement that really predicts in-game lag spikes.

### `net-speed-duplex-auto`: Скорость и дуплекс — автосогласование → **SAFE-OPTIONAL (repair)**
- **Keyword:** `*SpeedDuplex`. 0 = Auto Negotiation (default); 1/2 = 10 Mbit half/full; 3/4 = 100 Mbit half/full; 5/6 = 1 Gbit half/full; values ≥1000 = speed in Mbit/s ([MS enumeration keywords](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/enumeration-keywords)). Realtek uses `2500` (verified), and the official Realtek 5G driver presumably uses `5000`.
- **Applies only when forced** (≠ 0 and 0 ∈ ValidRegistryValues). Detect: `Applied` when 0. **Apply:** set "0", journaled with the original. Realtek's hidden `ForceMode` is left alone.
- **Why:** Intel says forcing is for "experienced network administrators" only, "Your link partner must match the setting you choose", otherwise "performance may suffer" ([Intel speed & duplex](https://edc.intel.com/content/www/us/en/design/products/ethernet/adapters-and-devices-user-guide/setting-speed-and-duplex/)). A forced port against an auto-negotiating partner ends in half duplex on the partner side, with late collisions and loss. Forcing 1 Gbit gains nothing, because 1000BASE-T negotiates anyway.
- **Why not SAFE-DEFAULT:** some users force 1 G on purpose to stop 2.5G link flapping (I225/I226 era). The Details text says so, and undo is one click.
- **Effect:** on a mismatched link, removes heavy loss and lag. On a healthy link, none. **This PC:** already 0, so not applicable.

### `net-eee-off`: Отключить энергосберегающий Ethernet (EEE) → **SAFE-OPTIONAL (troubleshooting)**
- **Keywords (whitelist; act only on those present with ValidRegistryValues ⊇ {0,1}):**
  - `*EEE` (Microsoft standard: 0 Disabled / 1 Enabled, default 1; [MS PM keywords](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/standardized-inf-keywords-for-power-management));
  - Realtek `EEE` and `AdvancedEEE` (names verified in the inbox INF's delete list);
  - Intel's keyword varies by driver. Discover it on real I225/I226 hardware before whitelisting it: it is **not verified** here (possibly `EEELinkAdvertisement`).
- **Effect on ping: none.** 802.3az wake time for 1000BASE-T is ≤16.5 µs ([IEEE 802.3 presentation](https://www.ieee802.org/3/NGBASET/public/sep12/bennett_01a_0912.pdf)). Intel itself only says "may introduce a small amount of network latency" ([Intel other power options](https://edc.intel.com/content/www/us/en/design/products/ethernet/adapters-and-devices-user-guide/other-power-options)).
- **Real value: link stability.** Intel's documented mitigation for I225-V speed and link problems is turning EEE off ([Intel 000093989](https://www.intel.com/content/www/us/en/support/articles/000093989.html)). In 2023 Intel shipped drivers that disable EEE at 1/2.5 Gbit for I225/I226/Killer E3100 disconnects ([Guru3D](https://www.guru3d.com/news-story/intel-patches-i226i225killer-e3100-2-5gbe-network-dropout-issues-by-disabling-eee.html), [AnandTech](https://www.anandtech.com/show/18755)). A dropped link costs 2–10 s, which means a disconnect in a match.
- **When to offer:** the audit found link drops on that adapter, or the user reports disconnects. It is not in profiles: on healthy links EEE is harmless and saves a little power (both ends must support it).
- **This PC:** the inbox driver does not expose any EEE keyword, and the link is stable, so not applicable. The driver's internal EEE behaviour cannot be controlled without the vendor driver.

### `net-interrupt-moderation-off`: Модерация прерываний — выключить → **ADVANCED (manual only)**
- **Keyword:** `*InterruptModeration`, 0 Disabled / 1 Enabled (default 1). The algorithm is device-dependent ([MS enumeration keywords](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/enumeration-keywords)).
- Only the standardized keyword is whitelisted. Intel's `ITR` / "Interrupt Moderation Rate" (Adaptive/Low/…/Off) is not touched: Intel says "Do not change this setting without verifying that the desired change will have a positive effect" ([Intel ITR](https://edc.intel.com/content/www/us/en/design/products/ethernet/adapters-and-devices-user-guide/interrupt-moderation-rate)).
- **Evidence for latency:**
  - Microsoft's low-latency list: "Disable the Interrupt Moderation setting for network card drivers that require the lowest possible latency… this configuration can use more CPU time". The same section says this latency is in **microseconds** and does not shorten transit ([MS NIC tuning](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics)).
  - Intel: "A higher ITR also means the driver has more latency in handling packets" ([Intel 000005593](https://www.intel.com/content/www/us/en/support/articles/000005593.html)).
  - Intel's adaptive algorithm already puts "almost completely small packets or minimal traffic" into its lowest-latency class ([Linux e1000e docs](https://www.kernel.org/doc/html/latest/networking/device_drivers/ethernet/intel/e1000e.html)). Game traffic (~64–256 small packets/s) is exactly that.
- **Expected effect:**
  - At most tens to a few hundred microseconds per packet. That is < 1 % of a 20–60 ms online RTT and below what a player or the game's ping meter can show.
  - Jitter changes are sub-millisecond.
  - Cost: during a download (~80 k packets/s at 1 Gbit) the interrupt and DPC rate rises. On this PC's inbox driver there is **no RSS**, so all of it lands on one core, a possible source of frame-time hitches while something downloads in the background.
- **Classification note:** gaming_tweaks.md §3 and general_tweaks.md §8 list this as DO-NOT-INCLUDE. The new evidence is that Microsoft and Intel document it as a legitimate latency trade-off, so it is not a myth like `SystemResponsiveness`. That supports ADVANCED in the manual panel with a Warning («Эффект — микросекунды, в онлайн-пинге не виден; больше нагрузка на процессор при загрузках»). It is never automatic and never in a profile. **Owner decision needed.** Keeping DO-NOT-INCLUDE costs nothing measurable for ping.
- **This PC:** exposed (=1). It is the only manual NIC knob the inbox driver offers.

### `net-receive-buffers-max`: Буферы приёма — максимум → **ADVANCED (manual, raise-only)**
- **Keywords:** `*ReceiveBuffers` (int; min/max from `Ndi\params`; Realtek inbox 32–512, step 8, default 512). `*TransmitBuffers` is never touched (Intel recommends the default if transmit performance is fine).
- **Evidence:** Microsoft: "Some network adapters set their receive buffers low… results in dropped packets… for receive-intensive scenarios, we recommend that you increase the receive buffer value to the maximum" ([MS NIC tuning](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics)). Intel: more receive buffers can improve receive performance at the cost of memory.
- **Myth to reject in the text:** "smaller buffers = lower ping". A receive ring holds packets only until the CPU services them. It adds no delay unless it is full, and when it is full the alternative is a drop.
- **Effect:** ping none; it can remove NIC drops during heavy downloads. Raise-only, toward max, so it never lowers.
- **This PC:** already at max, so not applicable.

### `net-allow-turn-off-device`: Разрешить отключение устройства для экономии энергии → **INFO-ONLY**
- **Read:** `MSFT_NetAdapterPowerManagementSettingData.AllowComputerToTurnOffDevice` (0 Unsupported, 1 Disabled, 2 Enabled). **Read-only in the CIM class** ([MS class docs](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/hh872363(v=vs.85))). It needs elevation on this PC (non-elevated read fails with error 31).
- **No supported write path:**
  - `Set-NetAdapterPowerManagement` has no such parameter. This was checked on Microsoft Learn ([cmdlet docs](https://learn.microsoft.com/en-us/powershell/module/netadapter/set-netadapterpowermanagement)) and on this build.
  - NDIS miniports: the registry route is `PnPCapabilities`=24 (0x18) in the Class key, from KB 2740020, written for Windows 7 SP1 / 2008 R2 and now retired ([KB 2740020](https://support.microsoft.com/kb/2740020)). Community reports on Windows 10/11 are mixed.
  - WDF/NetAdapterCx/WiFiCx drivers (this Qualcomm): the checkbox maps to WDF `IdleInWorkingState`/`WakeFromSleepState`. Microsoft: "Drivers and their installation files must not read or modify these values" — only the framework UI does ([WDF idle/wake](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/user-control-of-device-idle-and-wake-behavior)).
- **Effect on ping: none during play.**
  - NDIS suspends an adapter only after `*SSIdleTimeout` (default 5 s, 1–60) with **no** I/O at all. Any send or receive cancels it ([how NDIS detects idle](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/how-ndis-detects-idle-network-adapters), [SS keywords](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/standardized-inf-keywords-for-ndis-selective-suspend)). A running game never idles the adapter.
  - NetAdapterCx `*IdleRestriction`=1 ("Only idle when user isn't present") is already set on this Qualcomm.
  - What matters for Wi-Fi latency is 802.11 power save, and that is the power-plan item `wifi-power-saving-off` (already SAFE-DEFAULT; unchanged).
- **What to show:** the state (elevated), a one-line explanation, and a button that opens the device's property sheet in Device Manager for users fixing "no network after sleep". Skip on Modern Standby (S0ix) systems, where disabling device idle can break standby.

### `net-realtek-power-saving`: Энергосбережение Realtek (Green Ethernet, Power Saving Mode, Gigabit Lite) → **INFO-ONLY**
- **Keywords (from the inbox INF delete list):** `EnableGreenEthernet`, `PowerSavingMode`, `GigaLite`, `AutoDisableGigabit`, `S5WakeOnLan`, `WolShutdownLinkSpeed`. Realtek publishes no public description.
- Intel's documented analogues act only **without link** ("Reduce Power if Cable Disconnected", "Ultra Low Power Mode When Cable is Disconnected") or in standby ([Intel other power options](https://edc.intel.com/content/www/us/en/design/products/ethernet/adapters-and-devices-user-guide/other-power-options)).
- **Effect on an active link: none.** Show the current values read-only with the explanation. If a user has link problems, the actionable item is `net-eee-off` plus the vendor driver.
- **This PC:** none exposed.

### `net-wake-on-lan`: Пробуждение по сети (Wake on LAN) → **INFO-ONLY**
- **Keywords:** `*WakeOnMagicPacket`, `*WakeOnPattern` (default 1), `*ModernStandbyWoLMagicPacket` (NDIS only, default 0) ([MS PM keywords](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/standardized-inf-keywords-for-power-management)).
- They act only in sleep or off states. **No effect on ping.** "Disable WoL for gaming" guides are placebo. Show it, don't offer it.

### `net-driver-generic`: Стандартный драйвер Microsoft для сетевой карты → **INFO-ONLY (route to Drivers)**
- **Detect:** `MSFT_NetAdapter.DriverProvider`="Microsoft" on a Realtek, Intel or Killer PCI NIC (`ComponentID` VEN_10EC / 8086), or a WiFiCx driver older than ~2 years for a Wi-Fi 7 adapter.
- **This PC:** the RTL8126 5GbE runs the inbox `rt640x64` generic section. That means no 5G speed option, no EEE/flow-control/offload/RSS/WoL keywords and a 2015-dated NDIS 6.40 driver. The Qualcomm Wi-Fi driver is 3.0.0.909 (07/2023), while community listings show 3.1.0.1453 (03/2025) and 3.1.0.1647 (01/2026) ([ElevenForum](https://www.elevenforum.com/t/drivers-qualcomm-ethernet-wifi-bluetooth.11386/)). Windows Update does not auto-offer FastConnect 7800 updates ([MS Q&A](https://learn.microsoft.com/en-us/answers/questions/3946840/windows-11-does-not-automatically-update-drivers-f)).
- The vendor Realtek 5GbE NetAdapterCx package is `rt26cx21x64` (1126.x), per a community driver catalog ([Treexy](https://treexy.com/products/driver-fusion/database/network-adapters/realtek/pcie-5gbe-family-controller/)); the board vendor is MSI (subsystem 1462). This belongs to the Drivers page (`drivers.md`, VendorDownloads).
- **Honest effect:** no measured ping benefit. Benefits are RSS (receive work spread over cores), the full property set (EEE control), vendor bug fixes, and for Wi-Fi 7 the MLO support of newer drivers.

### `net-offloads-off`: Отключение разгрузок (LSO, контрольные суммы, RSS) → **DO-NOT-INCLUDE (agrees with gaming_tweaks.md §3, general_tweaks.md §8)**
- **Keywords:** `*LsoV2IPv4/6`, `*TCPChecksumOffloadIPv4/6`, `*UDPChecksumOffloadIPv4/6`, `*IPChecksumOffloadIPv4`, `*RSS`, RSC (`Get-NetAdapterRsc`).
- **Microsoft's own low-latency list** says the opposite of the "gaming" guides: "Enable static offloads… UDP Checksums, TCP Checksums, and Send Large Offload (LSO)" and "enable RSS". It also says enabling offloads "is usually beneficial" ([MS NIC tuning](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics)).
- LSO acts only on large TCP sends; game traffic is small UDP. Disabling these only moves work to the CPU. **Never.**

### `net-flow-control-off`: Отключить управление потоком (Flow Control) → **DO-NOT-INCLUDE**
- **Keyword:** `*FlowControl` (0 off / 1 Tx / 2 Rx / 3 Rx&Tx = client default / 4 auto) ([MS enumeration keywords](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/enumeration-keywords)).
- 802.3x pause frames are sent only when a receiver is overwhelmed, which on a home 1 G LAN almost never happens. Intel gives no performance or latency guidance for it.
- There is no evidence for a ping effect. Turning it off can turn rare congestion into drops.
- The real pause-frame pathology is a NIC flooding pause frames when a debugger is attached (Windows already disables flow control under a kernel debugger) or when a NIC hangs. That is a troubleshooting case for the switch, not a tweak.

### `net-jumbo-frames`: Jumbo-кадры → **DO-NOT-INCLUDE**
- **Keyword:** `*JumboPacket`. Intel: useful only "if more latency can be tolerated" (CPU savings). It needs support on every device in the path; at 10/100 Mbit "poor performance or loss of link" ([Intel 000005593](https://www.intel.com/content/www/us/en/support/articles/000005593.html)).
- Internet MTU is 1500, and game packets are ~100–300 bytes. There is no benefit, and mismatched MTU creates black holes.

### `net-misc-props`: Прочие свойства (Priority & VLAN, MAC-адрес, ARP/NS offload) → **DO-NOT-INCLUDE**
- `*PriorityVLANTag` only controls whether the NIC inserts or copies 802.1Q tags ([MS enumeration keywords](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/enumeration-keywords)). ISPs ignore or bleach 802.1p/DSCP (general_tweaks.md already rejects DSCP for games).
- `NetworkAddress` changes the MAC, which breaks DHCP reservations and ISP bindings.
- `*PMARPOffload`/`*PMNSOffload` act in sleep only. No effect on ping.

### `wifi-preferred-band`: Предпочитаемый диапазон Wi-Fi → **ADVANCED**
- **Keywords (vendor-specific, whitelist after hardware verification):**
  - Qualcomm `StaPreferredBand` — verified here: 1 No preference (default), 2 2.4 GHz, 3 5 GHz, 4 6 GHz.
  - Intel: "Preferred Band" (No preference / prefer 2.4 / prefer 5). Intel's keyword name is **not verified** here.
- **Evidence:** Intel describes it as a way to reduce interference from nearby devices; the default is No Preference ([Intel 000005585](https://www.intel.com/content/www/us/en/support/articles/000005585.html)).
- **Effect:** it only matters when one SSID is broadcast on several bands (band steering). Then it can move the PC off a congested 2.4 GHz channel, which helps jitter and loss. At range, 5/6 GHz is weaker and can be worse.
- **Offer only when** `wifi-link-audit` shows a 2.4 GHz connection and the same SSID is seen on 5/6 GHz with a good signal. The re-association drops Wi-Fi for several seconds. The router-side fix (separate SSIDs, band steering) is the better advice.

### `wifi-roaming-aggressiveness`: Агрессивность роуминга → **DO-NOT-INCLUDE (agrees with general_tweaks.md §8)**
- Qualcomm `roamPolicy` 1–5 (=3 here); Intel "Roaming Aggressiveness", where Intel calls "Medium (default): Recommended value" ([Intel 000005585](https://www.intel.com/content/www/us/en/support/articles/000005585.html)).
- It only moves the RSSI threshold at which the adapter starts scanning for another AP. On a single AP it does nothing. On mesh, a wrong value causes sticky clients or roam flapping. No ping evidence.

### `wifi-mimo-power-save`: Энергосбережение MIMO (SMPS) → **DO-NOT-INCLUDE (agrees)**
- Intel only (not on this Qualcomm). Default Auto SMPS. Intel mentions "No SMPS" only as a workaround for low throughput with legacy APs ([Intel 000005585](https://www.intel.com/content/www/us/en/support/articles/000005585.html)).
- No ping evidence. The AC power plan item already keeps the radio at max performance.

### `wifi-throughput-booster`: Усилитель пропускной способности → **DO-NOT-INCLUDE**
- Intel only, default Disabled. It is about upload throughput, and "the throughput for other clients in the same network may be negatively impacted" ([Intel 000005585](https://www.intel.com/content/www/us/en/support/articles/000005585.html)). No latency benefit.

### `wifi-uapsd`: Поддержка U-APSD → **DO-NOT-INCLUDE**
- Intel only, default Disabled. It is a power-save mode for VoIP-like traffic, and Intel notes "interoperability (IOT) issues with certain access points that result in reduced RX throughput" ([Intel 000005585](https://www.intel.com/content/www/us/en/support/articles/000005585.html)). Leave as is.

### `wifi-width-mode-manual`: Ширина канала / режим Wi-Fi вручную → **DO-NOT-INCLUDE**
- Qualcomm `PreferredChanWidth2G/5G` (Auto / 20 MHz only), `StaWirelessMode` (14 options); Intel "Channel width", "Wireless mode".
- Forcing 20 MHz or a legacy mode cuts capacity and can block 11ax/11be. Channel planning belongs on the router. No evidence of lower latency.

### `wifi-background-scan-off`: Отключить фоновое сканирование Wi-Fi → **DO-NOT-INCLUDE**
- **Mechanism people use:** `netsh wlan set autoconfig enabled=no interface=…` or the "WLAN Optimizer" freeware.
- Periodic ping spikes from background scans are widely reported, but the evidence is anecdotal ([gHacks](https://www.ghacks.net/?p=54335), [Intel community](https://community.intel.com/t5/Wireless/AX201-ping-spikes/m-p/1573530)). Turning autoconfig off stops automatic reconnects and is easy to forget.
- MONOLITH should only **detect** periodic spikes in `wifi-link-audit` and recommend a cable.

### `wifi7-mlo`: Wi-Fi 7 MLO → **INFO-ONLY**
- Windows 11 24H2 supports Wi-Fi 7 for consumer APs, including Multi-Link Operation ([Insider build 26063](https://blogs.windows.com/windows-insider/2024/02/22/announcing-windows-11-insider-preview-build-26063-canary-channel/); enterprise APs added 09/2025 per the [Windows IT Pro blog](https://techcommunity.microsoft.com/blog/windows-itpro-blog/introducing-wi-fi-7-for-enterprise-connectivity/4456751)). It needs a Wi-Fi 7 router with MLO enabled and a current client driver. FastConnect 7800 lists MLO in hardware ([Qualcomm brief](https://www.qualcomm.com/content/dam/qcomm-martech/dm-assets/documents/fastconnect-7800-product-brief_final19.pdf)).
- Measured studies show latency and jitter gains under contention ([arXiv 2305.02052](https://arxiv.org/abs/2305.02052)). Vendor numbers such as "−85 % latency" are marketing.
- There is no Windows-side toggle. Show "MLO active / not active" if the WLAN API exposes it, plus driver advice. A cable is still better for competitive play.

### Killer E3x00 notes
- The Killer E3000/E3100 are 2.5GbE controllers; the E3100 is named in the 2023 EEE disconnect mitigation together with I225/I226 ([Guru3D](https://www.guru3d.com/news-story/intel-patches-i226i225killer-e3100-2-5gbe-network-dropout-issues-by-disabling-eee.html)). The same `net-eee-off` and `net-speed-duplex-auto` rules apply.
- The Killer software's traffic prioritization only shapes this PC's own traffic and cannot fix congestion caused by other devices or the router. Older Killer products are end-of-support ([Intel 000059296](https://intel.com/content/www/us/en/support/articles/000059296.html)). MONOLITH should not try to control Killer software.

---

## 5. Corrections and agreements with existing research

- **Correction, general_tweaks.md §8 `net.wifipower` → "Device setting":** `Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice Disabled` **does not exist**. The parameter is absent from the Microsoft Learn syntax and from the cmdlet on build 26300, and `AllowComputerToTurnOffDevice` is read-only in the CIM class. Combined with "no ping effect during play" and the WDF rule that these values are framework-owned, I recommend **INFO-ONLY** instead of SAFE-OPTIONAL (see `net-allow-turn-off-device`). The power-plan part (`wifi-power-saving-off`, SAFE-DEFAULT AC) stays as is: I agree with it.
- **Change proposed, gaming_tweaks.md §3 / general_tweaks.md §8 "interrupt-moderation toggles: DO-NOT-INCLUDE":** move only `*InterruptModeration` to ADVANCED (manual, never auto, honest warning), based on Microsoft's and Intel's documented latency trade-off. Owner decision; keeping DO-NOT-INCLUDE is also defensible because the effect is microseconds.
- **Agree:** offloads / RSS / RSC → DO-NOT-INCLUDE. Microsoft's low-latency guidance explicitly says to keep them on.
- **Agree:** vendor Wi-Fi properties MIMO power save and roaming aggressiveness → DO-NOT-INCLUDE. Throughput booster, U-APSD, channel width and wireless mode are added to that list. The only exception is `wifi-preferred-band` as ADVANCED, gated by measurement.
- **Agree:** DSCP/QoS, NetworkThrottlingIndex, TcpAckFrequency classifications. This area adds nothing that changes them.

## 6. Open questions

1. Intel I225/I226 (e2f/e1i) and Intel Wi-Fi registry keywords for EEE and Preferred Band: verify on real hardware before whitelisting. Realtek official-driver keywords (`*EEE`, `EEE`, `AdvancedEEE`, `EnableGreenEthernet`, `GigaLite`, `PowerSavingMode`) are taken from the inbox INF's delete list; verify the value sets with the vendor driver installed.
2. Actual link-down time on restart for RTL8126 at 1 G, I226 at 2.5 G and FastConnect re-association. It was not measured here (changes forbidden). Measure on a test PC and put the real number into the confirmation text.
3. Whether the WLAN API exposes MLO link state on 24H2+ (for `wifi7-mlo`).
4. Link-drop detection: `NetworkProfile/Operational` 10001 is locale-independent but per network profile. Correlating it with `Kernel-General` 12/13 (boot/shutdown) and vendor event IDs needs testing on a PC that actually flaps.

## Sources

- Microsoft — Network adapter performance tuning: https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics
- Microsoft — Enumeration keywords (*SpeedDuplex, *FlowControl, *InterruptModeration, offloads): https://learn.microsoft.com/en-us/windows-hardware/drivers/network/enumeration-keywords
- Microsoft — Standardized INF keywords for power management (*EEE, *IdleRestriction, WoL): https://learn.microsoft.com/en-us/windows-hardware/drivers/network/standardized-inf-keywords-for-power-management
- Microsoft — NDIS selective suspend keywords (auto restart, *SSIdleTimeout 5 s): https://learn.microsoft.com/en-us/windows-hardware/drivers/network/standardized-inf-keywords-for-ndis-selective-suspend
- Microsoft — How NDIS detects idle adapters: https://learn.microsoft.com/en-us/windows-hardware/drivers/network/how-ndis-detects-idle-network-adapters
- Microsoft — Set-NetAdapterAdvancedProperty: https://learn.microsoft.com/en-us/powershell/module/netadapter/set-netadapteradvancedproperty
- Microsoft — Reset-NetAdapterAdvancedProperty: https://learn.microsoft.com/en-us/powershell/module/netadapter/reset-netadapteradvancedproperty
- Microsoft — Set-NetAdapterPowerManagement: https://learn.microsoft.com/en-us/powershell/module/netadapter/set-netadapterpowermanagement
- Microsoft — MSFT_NetAdapterPowerManagementSettingData: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/hh872363(v=vs.85)
- Microsoft — WDF user control of idle/wake: https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/user-control-of-device-idle-and-wake-behavior
- Microsoft KB 2740020 (PnPCapabilities, Windows 7 SP1 / 2008 R2, retired): https://support.microsoft.com/kb/2740020
- Intel — Advanced settings for Intel Ethernet adapters: https://www.intel.com/content/www/us/en/support/articles/000005593.html
- Intel — Interrupt Moderation Rate: https://edc.intel.com/content/www/us/en/design/products/ethernet/adapters-and-devices-user-guide/interrupt-moderation-rate
- Intel — Speed and duplex: https://edc.intel.com/content/www/us/en/design/products/ethernet/adapters-and-devices-user-guide/setting-speed-and-duplex/
- Intel — Other power options (EEE latency, cable-disconnected modes): https://edc.intel.com/content/www/us/en/design/products/ethernet/adapters-and-devices-user-guide/other-power-options
- Intel — I225-V speed issues, disable EEE: https://www.intel.com/content/www/us/en/support/articles/000093989.html
- Guru3D — I225/I226/Killer E3100 dropouts, EEE disabled: https://www.guru3d.com/news-story/intel-patches-i226i225killer-e3100-2-5gbe-network-dropout-issues-by-disabling-eee.html
- AnandTech — I226-V stopgap: https://www.anandtech.com/show/18755
- IEEE 802.3 NGBASE-T (EEE wake times): https://www.ieee802.org/3/NGBASET/public/sep12/bennett_01a_0912.pdf
- IEEE 802.3bz, Intel time-to-link: https://www.ieee802.org/3/bz/public/may15/cibula_3bz_02_0515.pdf
- Microchip KSZ9567R datasheet (1000BASE-T always auto-negotiates; link-up ≥1 s): https://www.alldatasheet.com/html-pdf/2133871/MICROCHIP/KSZ9567R/12621/22/KSZ9567R.html
- Linux kernel e1000e docs (InterruptThrottleRate, lowest-latency class): https://www.kernel.org/doc/html/latest/networking/device_drivers/ethernet/intel/e1000e.html
- Intel — Advanced settings for Intel wireless adapters: https://www.intel.com/content/www/us/en/support/articles/000005585.html
- Windows Insider build 26063 (Wi-Fi 7, MLO): https://blogs.windows.com/windows-insider/2024/02/22/announcing-windows-11-insider-preview-build-26063-canary-channel/
- Windows IT Pro — Wi-Fi 7 for enterprise: https://techcommunity.microsoft.com/blog/windows-itpro-blog/introducing-wi-fi-7-for-enterprise-connectivity/4456751
- Wi-Fi MLO experimental study: https://arxiv.org/abs/2305.02052
- Qualcomm FastConnect 7800 brief: https://www.qualcomm.com/content/dam/qcomm-martech/dm-assets/documents/fastconnect-7800-product-brief_final19.pdf
- ElevenForum — Qualcomm driver versions: https://www.elevenforum.com/t/drivers-qualcomm-ethernet-wifi-bluetooth.11386/
- Microsoft Q&A — FastConnect drivers not auto-updated: https://learn.microsoft.com/en-us/answers/questions/3946840/windows-11-does-not-automatically-update-drivers-f
- Treexy — Realtek 5GbE driver packages: https://treexy.com/products/driver-fusion/database/network-adapters/realtek/pcie-5gbe-family-controller/
- Intel — discontinued Killer products: https://intel.com/content/www/us/en/support/articles/000059296.html
- gHacks — WLAN background scans: https://www.ghacks.net/?p=54335
- Intel community — AX201 ping spikes: https://community.intel.com/t5/Wireless/AX201-ping-spikes/m-p/1573530
