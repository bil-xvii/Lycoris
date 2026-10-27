# MONOLITH: verified winget catalog for the Apps & Launchers page

**How this was checked (2026-10-26):** every ID below was run through `winget show --id <ID> -e --source <src> --accept-source-agreements --disable-interactivity` on the dev machine. That machine runs winget **v1.11.510** (App Installer 1.26.509.0, which shows an upgrade to 1.29.380 as available), with sources `winget` and `msstore`. I also probed which install scopes each package offers with `--scope user` and `--scope machine`. Nothing was installed or changed.

**"Official" means:** the manifest's `Publisher` is the vendor **and** the `Installer Url` points to a domain or GitHub repo the vendor controls. winget-pkgs manifests are mostly written by the community or bots, but every installer is pinned by SHA256. Store (`msstore`) entries are published by the vendor itself.

**Scope column:**
- `machine` / `user` / `both`: the scope(s) the manifest declares.
- `any`: no scope is declared. **Don't pass `--scope`** for these. The filter excludes them and you get `0x8A150010 NO_APPLICABLE_INSTALLER`; the probe confirmed this happens.

## 1. Game launchers

| Name | winget Id | Publisher (manifest) | Verified | Notes |
|---|---|---|---|---|
| Steam | `Valve.Steam` | Valve Corporation | yes, official | 2.10.91.91, nullsoft, machine |
| Epic Games Launcher | `EpicGames.EpicGamesLauncher` | Epic Games, Inc. | yes, official | MSI (wix), machine |
| GOG GALAXY | `GOG.Galaxy` | GOG.com | yes, official | inno, machine |
| EA app | `ElectronicArts.EADesktop` | Electronic Arts | yes, official | burn, machine |
| Ubisoft Connect | `Ubisoft.Connect` | Ubisoft | yes, official | machine |
| Battle.net | `Blizzard.BattleNet` | Blizzard Entertainment | yes, official | exe bootstrapper, machine |
| Rockstar Games Launcher | `RockstarGames.Launcher` | Rockstar Games Inc. | yes, official | scope `any` |
| Amazon Games | `Amazon.Games` | Amazon.com Services, Inc. | yes, official | user |
| Wargaming.net Game Center | `Wargaming.GameCenter` | Wargaming.net | yes, official | scope `any`. **Lesta Game Center and VK Play are not in winget** |
| League of Legends | `RiotGames.LeagueOfLegends.EUW` (also `.EUNE .RU .NA .BR .JP .KR .LA1 .LA2 .OC1 .TR .TW2 .VN2 .SG2 .PBE`) | Riot Games, Inc | yes, official | One ID per region, user scope. **There is no standalone Riot Client package.** Installs the Vanguard kernel anti-cheat, which needs a reboot |
| VALORANT | `RiotGames.Valorant.EU` (also `.AP .BR .KR .LATAM .NA`) | Riot Games, Inc | yes, official | user; Vanguard |
| Xbox app | msstore `9MV0B5HZVK9Z` | Microsoft Corporation | yes, official | Also needs Gaming Services `9MWPM2CQNLHN` |
| Minecraft Launcher | `Mojang.MinecraftLauncher` | Mojang | yes, official | `MinecraftInstaller.msi` from launcher.mojang.com, machine. The msstore id `9PGW18NPBZV5` returns 0x8A150014 through winget |
| Prism Launcher | `PrismLauncher.PrismLauncher` | Prism Launcher Contributors | yes (OSS, official repo) | user; needs Java (Temurin) |
| CurseForge | `Overwolf.CurseForge` | Overwolf | yes, official | both |
| osu!(lazer) | `ppy.osu` | ppy Pty Ltd | yes, official | user; osu!stable is not packaged |
| Roblox Player | `Roblox.Roblox` | Roblox Corporation | yes (**caution**) | Manifest type is `portable`. winget only drops `RobloxPlayerInstaller.exe` into WinGet\Packages; it still has to be run. Better to open roblox.com/download |
| Playnite | `Playnite.Playnite` | Josef Nemec | yes (OSS) | user |
| Heroic | `HeroicGamesLauncher.HeroicGamesLauncher` | Heroic Games Launcher | yes (OSS) | both |
| itch | `ItchIo.Itch` | itch corp. | yes, official | user |
| GeForce NOW | `Nvidia.GeForceNow` | NVIDIA Corporation | yes, official | user |

## 2. Communication

| Name | Id | Publisher | Verified | Notes |
|---|---|---|---|---|
| Discord | `Discord.Discord` | Discord Inc. | yes, official | user-only exe. Manifest says it needs explicit upgrade, so `upgrade --all` skips it (it updates itself) |
| Telegram Desktop | `Telegram.TelegramDesktop` | Telegram FZ-LLC | yes, official | user = inno. `--scope machine` picks a **portable zip**, so never force machine |
| TeamSpeak 3 | `TeamSpeakSystems.TeamSpeakClient` | TeamSpeak Systems GmbH | yes, official | 3.6.2. TS5/TS6 only exist as betas (`...Beta.5`, `...Beta.6`) |
| Mumble | `Mumble.Mumble.Client` | Mumble VoIP | yes (OSS) | machine |
| Zoom Workplace | `Zoom.Zoom` | Zoom | yes, official | MSI, machine |
| WhatsApp | msstore `9NKSQGP7F2NH` | WhatsApp Inc. | yes, official | Store |
| Signal | `OpenWhisperSystems.Signal` | Signal Messenger, LLC | yes, official | user |
| Viber | `Rakuten.Viber` | Rakuten | yes, official | MSI, machine |
| Element / Slack / Teams | `Element.Element` / `SlackTechnologies.Slack` / `Microsoft.Teams` | vendors | yes | Optional, more work-oriented |
| Skype | n/a | n/a | **excluded** | Retired in May 2025 |
| Vesktop | `Vencord.Vesktop` | Vencord | yes, **excluded** | Third-party Discord client mod (grey area under Discord's ToS) |

## 3. Browsers

| Name | Id | Publisher | Verified | Notes |
|---|---|---|---|---|
| Chrome | `Google.Chrome` | Google LLC | yes, official | Enterprise MSI, machine |
| Firefox (ru) | `Mozilla.Firefox.ru` | Mozilla | yes, official | Use for the Russian UI. `Mozilla.Firefox` is en-US |
| Brave | `Brave.Brave` | Brave Software Inc | yes, official | 155.1.97.56; both |
| Opera GX / Opera | `Opera.OperaGX` / `Opera.Opera` | Opera Software | yes, official | both |
| Edge | `Microsoft.Edge` | Microsoft Corporation | yes | **Preinstalled system component.** Don't offer install or uninstall; EdgeUpdate handles updates |
| Vivaldi | `Vivaldi.Vivaldi` | Vivaldi Technologies AS. | yes, official | both |
| Yandex Browser | `Yandex.Browser` | Yandex | yes, official | user; the URL is the international (`/int/.../en/`) build |
| LibreWolf / Zen | `LibreWolf.LibreWolf` / `Zen-Team.Zen-Browser` | community | yes (OSS) | machine |
| Chromium | `Hibbiki.Chromium` | The Chromium Authors | yes, **excluded** | Unofficial build with no auto-update |

## 4. Gaming runtimes

| Name | Id | Publisher | Verified | Notes |
|---|---|---|---|---|
| VC++ 2005 x86 / x64 | `Microsoft.VCRedist.2005.x86` / `.x64` | Microsoft Corporation | yes, official | 8.0.61001 / 8.0.61000 |
| VC++ 2008 | `Microsoft.VCRedist.2008.x86` / `.x64` | Microsoft | yes | 9.0.30729.6161 |
| VC++ 2010 | `Microsoft.VCRedist.2010.x86` / `.x64` | Microsoft | yes | 10.0.40219 |
| VC++ 2012 | `Microsoft.VCRedist.2012.x86` / `.x64` | Microsoft | yes | 11.0.61030.0 |
| VC++ 2013 | `Microsoft.VCRedist.2013.x86` / `.x64` | Microsoft | yes | 12.0.40664.0 |
| VC++ 2015–2022 (v14) | `Microsoft.VCRedist.2015+.x86` / `.x64` | Microsoft | yes | 14.51.36247.0. The dev box has 14.36 and shows as upgradable |
| DirectX End-User Runtime (June 2010) | `Microsoft.DirectX` | Microsoft | yes, **see note** | **The default pick on Win10 1809+/Win11 is MSIX** `Microsoft.DirectXRuntime_8wekyb3d8bbwe` (a UWP framework package; verified on this machine). My reasoning, not tested: classic Win32 games won't find D3DX9/XAudio2_7 there. **Always add `--installer-type exe`**, which runs `dxwebsetup.exe /Q`. Its extra success code is 2852126720 (0xAA000000) |
| .NET Desktop Runtime 6 / 8 / 9 / 10 | `Microsoft.DotNet.DesktopRuntime.6` / `.8` / `.9` / `.10` | Microsoft | yes | 6.0.36 (end of life since Nov 2024), 8.0.33 and 9.0.22 (**both end of life 10 Nov 2026**), 10.0.14 (LTS to Nov 2028). For x86 add `-a x86`; this was verified to give the win-x86 installer |
| .NET Framework 4.8.1 | `Microsoft.DotNet.Framework.Runtime` | Microsoft | yes | Already built into Win11 22H2+. Offer on Win10 only. Scope `any` |
| XNA Framework 4.0 Refresh | `Microsoft.XNARedist` | Microsoft | yes | 4.0.30901.0, scope `any` |
| OpenAL | `CreativeTechnology.OpenAL` | Creative Technology Ltd. | yes, official | 1.1 (oalinst), legacy games |
| Java 21 / 17 / 8 (JRE) | `EclipseAdoptium.Temurin.21.JRE` / `.17.JRE` / `.8.JRE` | Eclipse Adoptium | yes | MSI, machine. Minecraft 1.20.5–1.21.x needs Java 21, 1.18–1.20.4 needs 17, old modpacks need 8; Minecraft 26.1+ needs Java 25 (`.25.JRE` also exists, not in the catalog yet). **The official Minecraft launcher bundles its own Java** and CurseForge installs its own too, so this is needed for Prism Launcher (§14) |
| Gaming Services | msstore `9MWPM2CQNLHN` | Microsoft | yes | Needed for Xbox / Game Pass PC games |
| PhysX | `Nvidia.PhysX` | NVIDIA Corporation | yes | NVIDIA GPUs only; the driver usually includes it |
| WebView2 Runtime | `Microsoft.EdgeWebView2Runtime` | Microsoft | yes | Preinstalled on Win11 |
| Vulkan Runtime | `KhronosGroup.VulkanRT` | **LunarG Inc.** | yes, **not recommended** | GPU drivers ship the Vulkan loader. Don't install by default |
| Not recommended | `Oracle.JavaRuntimeEnvironment` (Oracle licence), `Microsoft.OpenCLGLVulkanCompatibilityPack` (for ARM/D3D12-only systems), `Microsoft.VCLibs.Desktop.14` | — | yes | Exclude |

## 5. Gamer tools

| Name | Id | Publisher | Verified | Notes |
|---|---|---|---|---|
| MSI Afterburner | `Guru3D.Afterburner` | MSI Co., LTD | yes | 4.6.6. Installer comes from the Guru3D mirror (ftp.nluug.nl), hash-pinned |
| RTSS | `Guru3D.RTSS` | Unwinder | yes | 7.3.7; overlay/injection |
| HWiNFO | `REALiX.HWiNFO` | Martin Malik, REALiX s.r.o. | yes, official | 8.54 |
| CPU-Z / HWMonitor | `CPUID.CPU-Z` / `CPUID.HWMonitor` | CPUID, Inc. | yes, official | 3.01 / 1.68 |
| GPU-Z | `TechPowerUp.GPU-Z` | TechPowerUp | yes, official | 2.71.0 |
| CrystalDiskInfo / CrystalDiskMark | `CrystalDewWorld.CrystalDiskInfo` / `.CrystalDiskMark` | Crystal Dew World | yes | SourceForge, which is the vendor's channel |
| OCCT | `OCBase.OCCT.Personal` | OCCT | yes, official | Portable; stress test; personal-use licence |
| Process Lasso | `BitSum.ProcessLasso` | Bitsum | yes, official | **Clashes with MONOLITH's own priority and affinity logic**; flag it |
| NVIDIA Profile Inspector | `Orbmu2k.nvidiaProfileInspector` | Orbmu2k | yes | Portable; expert only |
| DDU | `Wagnardsoft.DisplayDriverUninstaller` | Wagnardsoft | yes, official | Expert only: removes drivers, Safe Mode recommended |
| Special K | `SpecialK.SpecialK` | The Special K Group | yes | Injects into games; anti-cheat risk |
| LatencyMon | `Resplendence.LatencyMon` | Resplendence | yes, official | 7.31 |
| Rufus / Ventoy | `Rufus.Rufus` / `Ventoy.Ventoy` | pbatard / Ventoy | yes | Portable; formats USB drives |
| FanControl | `Rem0o.FanControl` | Remi Mercier Software Inc | yes | v284 (portable). V238 and later use the PawnIO driver instead of WinRing0 (CVE-2020-14979). FACEIT may block PawnIO |
| LibreHardwareMonitor | `LibreHardwareMonitor.LibreHardwareMonitor` | LibreHardwareMonitor | yes | Portable; kernel driver |
| CapFrameX / PresentMon | `CXWorld.CapFrameX` / `Intel.PresentMon` | DevTechProfile / Intel | yes | Frametime capture |
| FurMark 2 | `Geeks3D.FurMark.2` | Geeks3D | yes | GPU stress test |
| NVCleanstall | `TechPowerUp.NVCleanstall` | TechPowerUp | yes | |
| Intel DSA | `Intel.IntelDriverAndSupportAssistant` | Intel | yes, official | |
| NVIDIA App | msstore **`XP8CLZL93F5Z4P`** | NVIDIA Corp. | yes, official | **Not in the winget source** (`Nvidia.NVIDIAApp` and `Nvidia.GeForceExperience` both return 0x8A150014). Win32 exe served from the Store CDN |
| NVIDIA Control Panel | msstore `9NF8H0H7WMLT` | NVIDIA Corp. | yes | DCH drivers normally install it |
| Sysinternals Process Explorer / Autoruns | `Microsoft.Sysinternals.ProcessExplorer` / `.Autoruns` | Sysinternals | yes, official | Portable |
| AIDA64 Extreme | `FinalWire.AIDA64.Extreme` | FinalWire Ltd. | yes | **Paid** (trial) |
| AMD Adrenalin | — | — | **no** | `AMD.AMDSoftwareCloudEdition` exists but is the **Azure NV-series VM driver. Never offer it** |
| NVIDIA Broadcast, DS4Windows | — | — | **no** | Not in winget or msstore |

## 6. Streaming, recording and peripherals

| Name | Id | Publisher | Verified | Notes |
|---|---|---|---|---|
| OBS Studio | `OBSProject.OBSStudio` | OBS Project | yes, official | machine = nullsoft, user = portable zip |
| Streamlabs Desktop | `Streamlabs.Streamlabs` | General Workings, Inc. | yes, official | machine |
| Voicemeeter / Banana / Potato | `VB-Audio.Voicemeeter` / `.Banana` / `.Potato` | VB-Audio Software | yes, official | Installs a virtual audio driver and **needs a reboot**; donationware |
| SteelSeries GG | `SteelSeries.GG` | SteelSeries ApS | yes, official | 122.0.0 |
| Logitech G HUB | `Logitech.GHUB` | Logitech, Inc. | yes, official | |
| Razer Synapse 4 | `RazerInc.RazerInstaller.Synapse4` | Razer Inc. | yes, official | Web installer, scope `any`. Synapse 3 is `...Synapse3` |
| Corsair iCUE 5 | `Corsair.iCUE.5` | Corsair | yes, official | |
| Elgato Stream Deck | `Elgato.StreamDeck` | Corsair Memory, Inc. | yes, official | |
| Medal | `MedalB.V.Medal` | Medal B.V. | yes, official | user |
| Sunshine / Moonlight | `LizardByte.Sunshine` / `MoonlightGameStreamingProject.Moonlight` | LizardByte / Moonlight | yes (OSS) | Sunshine installs a **service and opens network ports** |
| Audacity / HandBrake | `Audacity.Audacity` / `HandBrake.HandBrake` | vendors | yes | Audacity scope is `any` |

## 7. Utilities

| Name | Id | Publisher | Verified | Notes |
|---|---|---|---|---|
| 7-Zip | `7zip.7zip` | Igor Pavlov | yes, official | 26.05 |
| WinRAR | `RARLab.WinRAR` | win.rar GmbH | yes, official | Trialware; picks the ru installer on ru systems |
| NanaZip | `M2Team.NanaZip` | Kenji Mouri | yes | MSIX |
| PowerToys | `Microsoft.PowerToys` | Microsoft | yes, official | Separate user and machine bootstrappers |
| Everything | `voidtools.Everything` | voidtools | yes, official | MSI |
| ShareX / Notepad++ / VLC | `ShareX.ShareX` / `Notepad++.Notepad++` / `VideoLAN.VLC` | vendors | yes, official | |
| K-Lite Codec Pack | `CodecGuide.K-LiteCodecPack.Standard` (also `.Basic .Full .Mega`) | KLCP | yes, official | 20.1.0; changes file associations |
| qBittorrent | `qBittorrent.qBittorrent` | The qBittorrent project | yes, official | |
| AnyDesk / TeamViewer | `AnyDesk.AnyDesk` / `TeamViewer.TeamViewer` | vendors | yes, official | **Remote-access tools (a common scam vector).** Never pre-select |
| Spotify | `Spotify.Spotify` or msstore `9NCBCSZSJRSB` | Spotify AB | yes, official | Manifest has `ElevationRequirement: elevationProhibited`, user scope. **Run elevated it fails with 0x8A150056** |
| Revo / BCUninstaller | `RevoUninstaller.RevoUninstaller` / `Klocman.BulkCrapUninstaller` | vendors | yes | |
| WizTree / TreeSize Free | `AntibodySoftware.WizTree` / `JAMSoftware.TreeSize.Free` | vendors | yes, official | |
| PotPlayer / AIMP / Yandex Music | `Daum.PotPlayer` / `AIMP.AIMP` / `Yandex.Music` | Kakao / A. Izmaylov / Yandex | yes | |
| Rainmeter / LocalSend / Greenshot | `Rainmeter.Rainmeter` / `LocalSend.LocalSend` / `Greenshot.Greenshot` | vendors | yes | |
| BleachBit | `BleachBit.BleachBit` | BleachBit | yes, **exclude by default** | Overlaps MONOLITH's own cleaner and can delete user data |
| Microsoft PC Manager | msstore `9PM860492SZD` | Microsoft | yes, excluded | Competing optimizer. Not in the winget source |

## 8. JSON catalog (verified entries only)

`scope`: `machine` | `user` | `both` | `any` (`any` means never pass `--scope`).

`flags` meanings:

| Flag | Meaning |
|---|---|
| `no-elevation` | Run winget unelevated |
| `explicit-upgrade` | Skipped by `upgrade --all` |
| `kernel-driver` | Installs a kernel driver |
| `anticheat` | Installs anti-cheat, or may trigger it |
| `reboot` | Needs a reboot |
| `expert` | Only for experienced users |
| `paid` / `trialware` | Paid or trial software |
| `eol` | Runtime past end of life |
| `preinstalled` | Ships with Windows |
| `win10-only` | Only offer on Windows 10 |
| `portable` | Portable package |
| `remote-access` | Remote-access tool |
| `force-exe` | Install with `--installer-type exe` |
| `region` | Pick the regional ID variant |
| `nvidia-only` | NVIDIA GPUs only |

```json
[
{"id":"Valve.Steam","name":"Steam","category":"launchers","source":"winget","description_ru":"Крупнейший магазин и лаунчер игр для ПК","website":"https://store.steampowered.com/about/","scope":"machine","flags":[]},
{"id":"EpicGames.EpicGamesLauncher","name":"Epic Games Launcher","category":"launchers","source":"winget","description_ru":"Магазин Epic Games с еженедельными бесплатными играми","website":"https://epicgames.com/download","scope":"machine","flags":[]},
{"id":"GOG.Galaxy","name":"GOG GALAXY","category":"launchers","source":"winget","description_ru":"Лаунчер GOG: игры без DRM и объединённая библиотека","website":"https://www.gog.com/galaxy","scope":"machine","flags":[]},
{"id":"ElectronicArts.EADesktop","name":"EA app","category":"launchers","source":"winget","description_ru":"Официальный лаунчер игр Electronic Arts","website":"https://www.ea.com/ea-app","scope":"machine","flags":[]},
{"id":"Ubisoft.Connect","name":"Ubisoft Connect","category":"launchers","source":"winget","description_ru":"Лаунчер и магазин игр Ubisoft","website":"https://ubisoftconnect.com/","scope":"machine","flags":[]},
{"id":"Blizzard.BattleNet","name":"Battle.net","category":"launchers","source":"winget","description_ru":"Лаунчер игр Blizzard и Activision","website":"https://download.battle.net/en-us/desktop","scope":"machine","flags":[]},
{"id":"RockstarGames.Launcher","name":"Rockstar Games Launcher","category":"launchers","source":"winget","description_ru":"Лаунчер игр Rockstar (GTA, RDR)","website":"https://socialclub.rockstargames.com/rockstar-games-launcher","scope":"any","flags":[]},
{"id":"Amazon.Games","name":"Amazon Games","category":"launchers","source":"winget","description_ru":"Лаунчер Amazon и Prime Gaming","website":"https://gaming.amazon.com/home","scope":"user","flags":[]},
{"id":"Wargaming.GameCenter","name":"Wargaming.net Game Center","category":"launchers","source":"winget","description_ru":"Лаунчер игр Wargaming","website":"https://wargaming.net/wgc","scope":"any","flags":[]},
{"id":"RiotGames.LeagueOfLegends.EUW","name":"League of Legends (EUW)","category":"launchers","source":"winget","description_ru":"League of Legends вместе с Riot Client, выбор региона","website":"https://www.leagueoflegends.com/","scope":"user","flags":["region","anticheat","kernel-driver","reboot"]},
{"id":"RiotGames.Valorant.EU","name":"VALORANT (EU)","category":"launchers","source":"winget","description_ru":"Тактический шутер Riot вместе с Riot Client и Vanguard","website":"https://playvalorant.com/","scope":"user","flags":["region","anticheat","kernel-driver","reboot"]},
{"id":"9MV0B5HZVK9Z","name":"Xbox","category":"launchers","source":"msstore","description_ru":"Приложение Xbox и PC Game Pass","website":"https://apps.microsoft.com/detail/9MV0B5HZVK9Z","scope":"user","flags":[]},
{"id":"Mojang.MinecraftLauncher","name":"Minecraft Launcher","category":"launchers","source":"winget","description_ru":"Официальный лаунчер Minecraft от Mojang","website":"https://www.minecraft.net/download","scope":"machine","flags":[]},
{"id":"PrismLauncher.PrismLauncher","name":"Prism Launcher","category":"launchers","source":"winget","description_ru":"Открытый лаунчер Minecraft с модами и несколькими сборками","website":"https://prismlauncher.org/","scope":"user","flags":[]},
{"id":"Overwolf.CurseForge","name":"CurseForge","category":"launchers","source":"winget","description_ru":"Менеджер модов и сборок для игр","website":"https://curseforge.overwolf.com/","scope":"both","flags":[]},
{"id":"ppy.osu","name":"osu!(lazer)","category":"launchers","source":"winget","description_ru":"Ритм-игра osu!","website":"https://osu.ppy.sh/","scope":"user","flags":[]},
{"id":"Roblox.Roblox","name":"Roblox Player","category":"launchers","source":"winget","description_ru":"Клиент Roblox (установщик запускается отдельно)","website":"https://www.roblox.com/download","scope":"both","flags":["portable"]},
{"id":"Playnite.Playnite","name":"Playnite","category":"launchers","source":"winget","description_ru":"Единая библиотека всех лаунчеров","website":"https://playnite.link/","scope":"user","flags":[]},
{"id":"HeroicGamesLauncher.HeroicGamesLauncher","name":"Heroic","category":"launchers","source":"winget","description_ru":"Открытый клиент для Epic, GOG и Amazon","website":"https://heroicgameslauncher.com/","scope":"both","flags":[]},
{"id":"ItchIo.Itch","name":"itch","category":"launchers","source":"winget","description_ru":"Клиент инди-магазина itch.io","website":"https://itch.io/app","scope":"user","flags":[]},
{"id":"Nvidia.GeForceNow","name":"GeForce NOW","category":"launchers","source":"winget","description_ru":"Облачный гейминг NVIDIA","website":"https://www.nvidia.com/en-us/geforce-now","scope":"user","flags":[]},
{"id":"Discord.Discord","name":"Discord","category":"communication","source":"winget","description_ru":"Голосовой и текстовый чат для геймеров","website":"https://discord.com/download","scope":"user","flags":["no-elevation","explicit-upgrade"]},
{"id":"Telegram.TelegramDesktop","name":"Telegram Desktop","category":"communication","source":"winget","description_ru":"Мессенджер Telegram для ПК","website":"https://desktop.telegram.org/","scope":"user","flags":["no-elevation"]},
{"id":"TeamSpeakSystems.TeamSpeakClient","name":"TeamSpeak 3","category":"communication","source":"winget","description_ru":"Голосовая связь с низкой задержкой","website":"https://www.teamspeak.com/","scope":"both","flags":[]},
{"id":"Mumble.Mumble.Client","name":"Mumble","category":"communication","source":"winget","description_ru":"Открытый голосовой чат с низкой задержкой","website":"https://www.mumble.info/","scope":"machine","flags":[]},
{"id":"Zoom.Zoom","name":"Zoom Workplace","category":"communication","source":"winget","description_ru":"Видеозвонки и конференции","website":"https://zoom.us/","scope":"machine","flags":[]},
{"id":"9NKSQGP7F2NH","name":"WhatsApp","category":"communication","source":"msstore","description_ru":"Мессенджер WhatsApp для Windows","website":"https://apps.microsoft.com/detail/9NKSQGP7F2NH","scope":"user","flags":[]},
{"id":"OpenWhisperSystems.Signal","name":"Signal","category":"communication","source":"winget","description_ru":"Защищённый мессенджер","website":"https://www.signal.org/","scope":"user","flags":["no-elevation"]},
{"id":"Rakuten.Viber","name":"Viber","category":"communication","source":"winget","description_ru":"Мессенджер и звонки Viber","website":"https://www.viber.com/","scope":"machine","flags":[]},
{"id":"Google.Chrome","name":"Google Chrome","category":"browsers","source":"winget","description_ru":"Браузер Google","website":"https://www.google.com/chrome/","scope":"machine","flags":[]},
{"id":"Mozilla.Firefox.ru","name":"Mozilla Firefox (ru)","category":"browsers","source":"winget","description_ru":"Браузер Firefox на русском языке","website":"https://www.mozilla.org/ru/firefox/","scope":"machine","flags":[]},
{"id":"Brave.Brave","name":"Brave","category":"browsers","source":"winget","description_ru":"Браузер со встроенной блокировкой рекламы","website":"https://brave.com/download","scope":"both","flags":[]},
{"id":"Opera.OperaGX","name":"Opera GX","category":"browsers","source":"winget","description_ru":"Игровой браузер с лимитами CPU и RAM","website":"https://www.opera.com/gx","scope":"both","flags":[]},
{"id":"Opera.Opera","name":"Opera","category":"browsers","source":"winget","description_ru":"Браузер Opera","website":"https://www.opera.com/browsers/opera","scope":"both","flags":[]},
{"id":"Microsoft.Edge","name":"Microsoft Edge","category":"browsers","source":"winget","description_ru":"Системный браузер Windows (уже установлен)","website":"https://www.microsoft.com/edge","scope":"machine","flags":["preinstalled"]},
{"id":"Vivaldi.Vivaldi","name":"Vivaldi","category":"browsers","source":"winget","description_ru":"Гибко настраиваемый браузер","website":"https://vivaldi.com/desktop/","scope":"both","flags":[]},
{"id":"Yandex.Browser","name":"Яндекс Браузер","category":"browsers","source":"winget","description_ru":"Браузер Яндекса","website":"https://browser.yandex.com/","scope":"user","flags":["no-elevation"]},
{"id":"LibreWolf.LibreWolf","name":"LibreWolf","category":"browsers","source":"winget","description_ru":"Приватная сборка Firefox","website":"https://librewolf.net/","scope":"machine","flags":[]},
{"id":"Zen-Team.Zen-Browser","name":"Zen Browser","category":"browsers","source":"winget","description_ru":"Минималистичный браузер на базе Firefox","website":"https://zen-browser.app/","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2005.x86","name":"Visual C++ 2005 (x86)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2005 для старых игр","website":"https://www.microsoft.com/en-US/download/details.aspx?id=26347","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2005.x64","name":"Visual C++ 2005 (x64)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2005 x64","website":"https://www.microsoft.com/en-US/download/details.aspx?id=26347","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2008.x86","name":"Visual C++ 2008 (x86)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2008","website":"https://www.microsoft.com/en-US/download/details.aspx?id=26368","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2008.x64","name":"Visual C++ 2008 (x64)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2008 x64","website":"https://www.microsoft.com/en-US/download/details.aspx?id=26368","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2010.x86","name":"Visual C++ 2010 (x86)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2010","website":"https://www.microsoft.com/download/details.aspx?id=26999","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2010.x64","name":"Visual C++ 2010 (x64)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2010 x64","website":"https://www.microsoft.com/download/details.aspx?id=26999","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2012.x86","name":"Visual C++ 2012 (x86)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2012","website":"https://www.microsoft.com/en-us/download/details.aspx?id=30679","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2012.x64","name":"Visual C++ 2012 (x64)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2012 x64","website":"https://www.microsoft.com/en-us/download/details.aspx?id=30679","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2013.x86","name":"Visual C++ 2013 (x86)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2013","website":"https://support.microsoft.com/help/4032938","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2013.x64","name":"Visual C++ 2013 (x64)","category":"runtimes","source":"winget","description_ru":"Библиотеки VC++ 2013 x64","website":"https://support.microsoft.com/help/4032938","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2015+.x86","name":"Visual C++ 2015-2022 (x86)","category":"runtimes","source":"winget","description_ru":"Актуальные библиотеки VC++ v14 x86","website":"https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist","scope":"machine","flags":[]},
{"id":"Microsoft.VCRedist.2015+.x64","name":"Visual C++ 2015-2022 (x64)","category":"runtimes","source":"winget","description_ru":"Актуальные библиотеки VC++ v14 x64","website":"https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist","scope":"machine","flags":[]},
{"id":"Microsoft.DirectX","name":"DirectX End-User Runtime","category":"runtimes","source":"winget","description_ru":"D3DX9/10/11, XAudio 2.7, XInput 1.3 для старых игр","website":"https://www.microsoft.com/download/details.aspx?id=35","scope":"any","flags":["force-exe"]},
{"id":"Microsoft.DotNet.DesktopRuntime.10","name":".NET Desktop Runtime 10","category":"runtimes","source":"winget","description_ru":"Среда .NET 10 для настольных приложений (LTS)","website":"https://dotnet.microsoft.com","scope":"machine","flags":[]},
{"id":"Microsoft.DotNet.DesktopRuntime.9","name":".NET Desktop Runtime 9","category":"runtimes","source":"winget","description_ru":"Среда .NET 9 (поддержка до 10.11.2026)","website":"https://dotnet.microsoft.com","scope":"machine","flags":["eol"]},
{"id":"Microsoft.DotNet.DesktopRuntime.8","name":".NET Desktop Runtime 8","category":"runtimes","source":"winget","description_ru":"Среда .NET 8 (поддержка до 10.11.2026)","website":"https://dotnet.microsoft.com","scope":"machine","flags":["eol"]},
{"id":"Microsoft.DotNet.DesktopRuntime.6","name":".NET Desktop Runtime 6","category":"runtimes","source":"winget","description_ru":"Среда .NET 6 для старых программ (без поддержки)","website":"https://dotnet.microsoft.com/download/dotnet/6.0","scope":"machine","flags":["eol"]},
{"id":"Microsoft.DotNet.Framework.Runtime","name":".NET Framework 4.8.1","category":"runtimes","source":"winget","description_ru":".NET Framework 4.8.1 (встроен в Windows 11)","website":"https://dotnet.microsoft.com/download/dotnet-framework/net481","scope":"any","flags":["win10-only"]},
{"id":"Microsoft.XNARedist","name":"XNA Framework 4.0","category":"runtimes","source":"winget","description_ru":"Среда XNA для инди-игр (Terraria и др.)","website":"https://www.microsoft.com/download/details.aspx?id=20914","scope":"any","flags":[]},
{"id":"CreativeTechnology.OpenAL","name":"OpenAL","category":"runtimes","source":"winget","description_ru":"Звуковая библиотека OpenAL для старых игр","website":"https://openal.org/","scope":"any","flags":[]},
{"id":"EclipseAdoptium.Temurin.21.JRE","name":"Java 21 (Temurin JRE)","category":"runtimes","source":"winget","description_ru":"Java 21 для Minecraft 1.20.5–1.21 и модов","website":"https://adoptium.net/","scope":"machine","flags":[]},
{"id":"EclipseAdoptium.Temurin.17.JRE","name":"Java 17 (Temurin JRE)","category":"runtimes","source":"winget","description_ru":"Java 17 для Minecraft 1.18–1.20.4","website":"https://adoptium.net/","scope":"machine","flags":[]},
{"id":"EclipseAdoptium.Temurin.8.JRE","name":"Java 8 (Temurin JRE)","category":"runtimes","source":"winget","description_ru":"Java 8 для старых модпаков","website":"https://adoptium.net/","scope":"machine","flags":[]},
{"id":"9MWPM2CQNLHN","name":"Gaming Services","category":"runtimes","source":"msstore","description_ru":"Игровые службы для Xbox и Game Pass","website":"https://apps.microsoft.com/detail/9MWPM2CQNLHN","scope":"user","flags":[]},
{"id":"Nvidia.PhysX","name":"NVIDIA PhysX","category":"runtimes","source":"winget","description_ru":"Физический движок PhysX для старых игр","website":"https://www.nvidia.com/en-us/drivers/physx/physx-9-26-0703-driver/","scope":"machine","flags":["nvidia-only"]},
{"id":"Microsoft.EdgeWebView2Runtime","name":"WebView2 Runtime","category":"runtimes","source":"winget","description_ru":"Веб-движок для лаунчеров и приложений","website":"https://developer.microsoft.com/microsoft-edge/webview2/","scope":"both","flags":["preinstalled"]},
{"id":"Guru3D.Afterburner","name":"MSI Afterburner","category":"gamertools","source":"winget","description_ru":"Разгон GPU, кривые вентиляторов, мониторинг","website":"https://www.msi.com/Landing/afterburner","scope":"machine","flags":["expert"]},
{"id":"Guru3D.RTSS","name":"RivaTuner Statistics Server","category":"gamertools","source":"winget","description_ru":"Оверлей FPS и ограничитель кадров","website":"https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/","scope":"machine","flags":["anticheat"]},
{"id":"REALiX.HWiNFO","name":"HWiNFO","category":"gamertools","source":"winget","description_ru":"Подробная информация о железе и датчиках","website":"https://www.hwinfo.com/","scope":"machine","flags":[]},
{"id":"CPUID.CPU-Z","name":"CPU-Z","category":"gamertools","source":"winget","description_ru":"Информация о процессоре, памяти и плате","website":"https://www.cpuid.com/softwares/cpu-z.html","scope":"machine","flags":[]},
{"id":"CPUID.HWMonitor","name":"HWMonitor","category":"gamertools","source":"winget","description_ru":"Температуры, напряжения и частоты","website":"https://www.cpuid.com/softwares/hwmonitor.html","scope":"machine","flags":[]},
{"id":"TechPowerUp.GPU-Z","name":"GPU-Z","category":"gamertools","source":"winget","description_ru":"Информация о видеокарте","website":"https://www.techpowerup.com/gpuz/","scope":"machine","flags":[]},
{"id":"CrystalDewWorld.CrystalDiskInfo","name":"CrystalDiskInfo","category":"gamertools","source":"winget","description_ru":"Здоровье и SMART дисков","website":"https://crystalmark.info/en/software/crystaldiskinfo/","scope":"machine","flags":[]},
{"id":"CrystalDewWorld.CrystalDiskMark","name":"CrystalDiskMark","category":"gamertools","source":"winget","description_ru":"Тест скорости дисков","website":"https://crystalmark.info/en/software/crystaldiskmark/","scope":"machine","flags":[]},
{"id":"OCBase.OCCT.Personal","name":"OCCT","category":"gamertools","source":"winget","description_ru":"Стресс-тест стабильности CPU, GPU и RAM","website":"https://www.ocbase.com/","scope":"both","flags":["portable","expert"]},
{"id":"Resplendence.LatencyMon","name":"LatencyMon","category":"gamertools","source":"winget","description_ru":"Анализ задержек DPC/ISR (заикания звука и кадров)","website":"https://www.resplendence.com/latencymon","scope":"machine","flags":[]},
{"id":"Intel.PresentMon","name":"Intel PresentMon","category":"gamertools","source":"winget","description_ru":"Замер FPS, frametime и задержек","website":"https://game.intel.com/intel-presentmon/","scope":"machine","flags":[]},
{"id":"CXWorld.CapFrameX","name":"CapFrameX","category":"gamertools","source":"winget","description_ru":"Запись и анализ frametime","website":"https://www.capframex.com/","scope":"both","flags":["portable"]},
{"id":"Rem0o.FanControl","name":"FanControl","category":"gamertools","source":"winget","description_ru":"Гибкое управление вентиляторами","website":"https://getfancontrol.com/","scope":"both","flags":["portable","kernel-driver","anticheat"]},
{"id":"LibreHardwareMonitor.LibreHardwareMonitor","name":"LibreHardwareMonitor","category":"gamertools","source":"winget","description_ru":"Открытый мониторинг датчиков","website":"https://github.com/LibreHardwareMonitor/LibreHardwareMonitor","scope":"both","flags":["portable","kernel-driver"]},
{"id":"Orbmu2k.nvidiaProfileInspector","name":"NVIDIA Profile Inspector","category":"gamertools","source":"winget","description_ru":"Расширенные профили драйвера NVIDIA","website":"https://github.com/Orbmu2k/nvidiaProfileInspector","scope":"both","flags":["portable","expert","nvidia-only"]},
{"id":"Wagnardsoft.DisplayDriverUninstaller","name":"Display Driver Uninstaller","category":"gamertools","source":"winget","description_ru":"Полное удаление видеодрайверов (лучше в безопасном режиме)","website":"https://www.wagnardsoft.com/display-driver-uninstaller-ddu","scope":"machine","flags":["expert"]},
{"id":"TechPowerUp.NVCleanstall","name":"NVCleanstall","category":"gamertools","source":"winget","description_ru":"Установка драйвера NVIDIA без лишних компонентов","website":"https://www.techpowerup.com/download/techpowerup-nvcleanstall/","scope":"machine","flags":["expert","nvidia-only"]},
{"id":"SpecialK.SpecialK","name":"Special K","category":"gamertools","source":"winget","description_ru":"Моддинг-фреймворк: HDR, лимитер кадров (внедряется в игры)","website":"https://special-k.info/","scope":"both","flags":["expert","anticheat"]},
{"id":"Geeks3D.FurMark.2","name":"FurMark 2","category":"gamertools","source":"winget","description_ru":"Стресс-тест видеокарты","website":"https://geeks3d.com/furmark/","scope":"both","flags":["expert"]},
{"id":"BitSum.ProcessLasso","name":"Process Lasso","category":"gamertools","source":"winget","description_ru":"Автоматическое управление приоритетами процессов","website":"https://bitsum.com/","scope":"machine","flags":["expert"]},
{"id":"Rufus.Rufus","name":"Rufus","category":"gamertools","source":"winget","description_ru":"Создание загрузочных USB-флешек","website":"https://rufus.ie/","scope":"both","flags":["portable"]},
{"id":"Intel.IntelDriverAndSupportAssistant","name":"Intel Driver & Support Assistant","category":"gamertools","source":"winget","description_ru":"Обновление драйверов Intel","website":"https://www.intel.com/content/www/us/en/support/detect.html","scope":"machine","flags":[]},
{"id":"XP8CLZL93F5Z4P","name":"NVIDIA App","category":"gamertools","source":"msstore","description_ru":"Драйверы, настройки, DLSS и запись для видеокарт NVIDIA","website":"https://apps.microsoft.com/detail/XP8CLZL93F5Z4P","scope":"any","flags":["nvidia-only"]},
{"id":"9NF8H0H7WMLT","name":"NVIDIA Control Panel","category":"gamertools","source":"msstore","description_ru":"Панель управления NVIDIA (если её нет после установки драйвера)","website":"https://apps.microsoft.com/detail/9NF8H0H7WMLT","scope":"user","flags":["nvidia-only"]},
{"id":"Microsoft.Sysinternals.ProcessExplorer","name":"Process Explorer","category":"gamertools","source":"winget","description_ru":"Расширенный диспетчер процессов от Microsoft","website":"https://learn.microsoft.com/sysinternals/downloads/process-explorer","scope":"both","flags":["portable"]},
{"id":"Microsoft.Sysinternals.Autoruns","name":"Autoruns","category":"gamertools","source":"winget","description_ru":"Полный контроль автозагрузки","website":"https://learn.microsoft.com/sysinternals/downloads/autoruns","scope":"both","flags":["portable","expert"]},
{"id":"OBSProject.OBSStudio","name":"OBS Studio","category":"streaming","source":"winget","description_ru":"Запись и стриминг экрана","website":"https://obsproject.com/","scope":"machine","flags":[]},
{"id":"Streamlabs.Streamlabs","name":"Streamlabs Desktop","category":"streaming","source":"winget","description_ru":"Стриминг с готовыми оверлеями и алертами","website":"https://streamlabs.com/","scope":"machine","flags":[]},
{"id":"MedalB.V.Medal","name":"Medal","category":"streaming","source":"winget","description_ru":"Запись игровых клипов","website":"https://medal.tv/","scope":"user","flags":[]},
{"id":"VB-Audio.Voicemeeter","name":"Voicemeeter","category":"streaming","source":"winget","description_ru":"Виртуальный аудиомикшер","website":"https://vb-audio.com/Voicemeeter/","scope":"machine","flags":["kernel-driver","reboot"]},
{"id":"VB-Audio.Voicemeeter.Banana","name":"Voicemeeter Banana","category":"streaming","source":"winget","description_ru":"Расширенный виртуальный аудиомикшер","website":"https://vb-audio.com/Voicemeeter/banana.htm","scope":"machine","flags":["kernel-driver","reboot"]},
{"id":"SteelSeries.GG","name":"SteelSeries GG","category":"streaming","source":"winget","description_ru":"ПО для периферии SteelSeries и Sonar","website":"https://steelseries.com/gg","scope":"machine","flags":[]},
{"id":"Logitech.GHUB","name":"Logitech G HUB","category":"streaming","source":"winget","description_ru":"ПО для игровой периферии Logitech","website":"https://www.logitechg.com/innovation/g-hub.html","scope":"machine","flags":[]},
{"id":"RazerInc.RazerInstaller.Synapse4","name":"Razer Synapse 4","category":"streaming","source":"winget","description_ru":"ПО для периферии Razer","website":"https://www.razer.com/synapse-4","scope":"any","flags":[]},
{"id":"Corsair.iCUE.5","name":"Corsair iCUE 5","category":"streaming","source":"winget","description_ru":"ПО для периферии и подсветки Corsair","website":"https://www.corsair.com/icue","scope":"machine","flags":[]},
{"id":"Elgato.StreamDeck","name":"Elgato Stream Deck","category":"streaming","source":"winget","description_ru":"ПО для пультов Stream Deck","website":"https://www.elgato.com/s/downloads","scope":"machine","flags":[]},
{"id":"LizardByte.Sunshine","name":"Sunshine","category":"streaming","source":"winget","description_ru":"Сервер стриминга игр с ПК (для Moonlight)","website":"https://github.com/LizardByte/Sunshine","scope":"machine","flags":["expert"]},
{"id":"MoonlightGameStreamingProject.Moonlight","name":"Moonlight","category":"streaming","source":"winget","description_ru":"Клиент стриминга игр","website":"https://moonlight-stream.org/","scope":"user","flags":[]},
{"id":"7zip.7zip","name":"7-Zip","category":"utilities","source":"winget","description_ru":"Бесплатный архиватор","website":"https://7-zip.org/","scope":"machine","flags":[]},
{"id":"RARLab.WinRAR","name":"WinRAR","category":"utilities","source":"winget","description_ru":"Архиватор RAR и ZIP","website":"https://www.win-rar.com/","scope":"machine","flags":["trialware"]},
{"id":"M2Team.NanaZip","name":"NanaZip","category":"utilities","source":"winget","description_ru":"Современный форк 7-Zip для Windows 11","website":"https://github.com/M2Team/NanaZip","scope":"both","flags":[]},
{"id":"Microsoft.PowerToys","name":"PowerToys","category":"utilities","source":"winget","description_ru":"Набор утилит Microsoft для продвинутых пользователей","website":"https://github.com/microsoft/PowerToys","scope":"both","flags":[]},
{"id":"voidtools.Everything","name":"Everything","category":"utilities","source":"winget","description_ru":"Мгновенный поиск файлов","website":"https://www.voidtools.com/","scope":"machine","flags":[]},
{"id":"ShareX.ShareX","name":"ShareX","category":"utilities","source":"winget","description_ru":"Скриншоты и запись экрана","website":"https://getsharex.com/","scope":"both","flags":[]},
{"id":"Notepad++.Notepad++","name":"Notepad++","category":"utilities","source":"winget","description_ru":"Текстовый редактор","website":"https://notepad-plus-plus.org/","scope":"machine","flags":[]},
{"id":"VideoLAN.VLC","name":"VLC","category":"utilities","source":"winget","description_ru":"Универсальный медиаплеер","website":"https://www.videolan.org/vlc/","scope":"machine","flags":[]},
{"id":"Daum.PotPlayer","name":"PotPlayer","category":"utilities","source":"winget","description_ru":"Мощный видеоплеер","website":"https://potplayer.tv/","scope":"machine","flags":[]},
{"id":"AIMP.AIMP","name":"AIMP","category":"utilities","source":"winget","description_ru":"Аудиоплеер","website":"https://www.aimp.ru","scope":"machine","flags":[]},
{"id":"CodecGuide.K-LiteCodecPack.Standard","name":"K-Lite Codec Pack Standard","category":"utilities","source":"winget","description_ru":"Набор кодеков и MPC-HC","website":"https://codecguide.com/","scope":"machine","flags":[]},
{"id":"qBittorrent.qBittorrent","name":"qBittorrent","category":"utilities","source":"winget","description_ru":"Открытый торрент-клиент без рекламы","website":"https://www.qbittorrent.org/","scope":"machine","flags":[]},
{"id":"Spotify.Spotify","name":"Spotify","category":"utilities","source":"winget","description_ru":"Музыкальный стриминг","website":"https://www.spotify.com/download/windows/","scope":"user","flags":["no-elevation"]},
{"id":"Yandex.Music","name":"Яндекс Музыка","category":"utilities","source":"winget","description_ru":"Музыкальный сервис Яндекса","website":"https://music.yandex.com/","scope":"user","flags":["no-elevation"]},
{"id":"RevoUninstaller.RevoUninstaller","name":"Revo Uninstaller","category":"utilities","source":"winget","description_ru":"Удаление программ вместе с остатками","website":"https://www.revouninstaller.com/","scope":"machine","flags":["expert"]},
{"id":"Klocman.BulkCrapUninstaller","name":"Bulk Crap Uninstaller","category":"utilities","source":"winget","description_ru":"Массовое удаление программ","website":"https://www.bcuninstaller.com/","scope":"machine","flags":["expert"]},
{"id":"AntibodySoftware.WizTree","name":"WizTree","category":"utilities","source":"winget","description_ru":"Быстрый анализ занятого места на диске","website":"https://diskanalyzer.com/","scope":"machine","flags":[]},
{"id":"JAMSoftware.TreeSize.Free","name":"TreeSize Free","category":"utilities","source":"winget","description_ru":"Анализ размеров папок","website":"https://www.jam-software.com/treesize_free","scope":"both","flags":[]},
{"id":"Rainmeter.Rainmeter","name":"Rainmeter","category":"utilities","source":"winget","description_ru":"Виджеты рабочего стола","website":"https://www.rainmeter.net/","scope":"machine","flags":[]},
{"id":"AnyDesk.AnyDesk","name":"AnyDesk","category":"utilities","source":"winget","description_ru":"Удалённый доступ (давайте его только тем, кому доверяете)","website":"https://anydesk.com/","scope":"machine","flags":["remote-access"]},
{"id":"TeamViewer.TeamViewer","name":"TeamViewer","category":"utilities","source":"winget","description_ru":"Удалённый доступ (давайте его только тем, кому доверяете)","website":"https://www.teamviewer.com/","scope":"machine","flags":["remote-access"]}
]
```

## 9. Command lines for the app

Use `ProcessStartInfo.ArgumentList`; don't build a single quoted string.

```text
# install (winget source)
winget install --id <ID> --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity [--architecture x86] [--installer-type exe] [--log "<path>"]
# install (Store)
winget install --id 9MV0B5HZVK9Z --exact --source msstore --accept-package-agreements --accept-source-agreements --disable-interactivity
# install, but skip if already installed (returns 0x8A150061 instead of attempting an upgrade)
winget install ... --no-upgrade
# upgrade one package
winget upgrade --id <ID> --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
# upgrade everything
winget upgrade --all --silent --accept-package-agreements --accept-source-agreements --disable-interactivity   # add --include-unknown only on explicit user opt-in
# installed check for one package: exit 0 = installed, 0x8A150014 = not installed (verified)
winget list --id <ID> --exact --accept-source-agreements --disable-interactivity
# list pending upgrades (read-only)
winget list --upgrade-available --accept-source-agreements --disable-interactivity
# export installed packages as JSON
winget export -o "%TEMP%\monolith_wg.json" --source winget --include-versions --accept-source-agreements --disable-interactivity
# uninstall (undo center)
winget uninstall --id <ID> --exact --silent --disable-interactivity --accept-source-agreements
```

**Process rules (all observed on this machine):**
- **Run winget calls one at a time.** Four parallel `winget show` runs produced spurious `0x8A150001` (internal error) for `Brave.Brave` and `FinalWire.AIDA64.Extreme`. Both succeeded when re-run sequentially. Use a `SemaphoreSlim(1)` queue and retry once on `0x8A150001`.
- Stdout is UTF-8 (Cyrillic names decoded correctly with a UTF-8 reader), so set `StandardOutputEncoding = Encoding.UTF8`.
- Stdout contains spinner characters (`-\|/`) and progress-bar lines even when redirected. Filter out lines that match `^\s*[-\\|/]\s*$`.
- winget lives at `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe` (an App Execution Alias). Check `winget --version` and require a recent client.
- `Process.ExitCode` is a signed int. Compare with `unchecked((int)0x8A15002B)`, which equals `-1978335189`.

## 10. Detecting what's installed, most to least reliable

1. **WinGet COM API.** NuGet package `Microsoft.WindowsPackageManager.ComInterop` (latest stable is 1.29.380; it targets net8.0-windows10.0.26100, so it works from .NET 10).
   - Main types: `PackageManager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages)`, `CreateCompositePackageCatalog`, `FindPackagesOptions` with a `PackageMatchFilter`, then read `CatalogPackage.InstalledVersion` and `IsUpdateAvailable`, and install with `InstallPackageAsync`, which reports progress.
   - **Open questions:** whether out-of-process activation works from an *elevated* caller, and whether a newer interop works against an older client. This machine's client is 1.11.510. Microsoft's PowerShell module uses in-process COM for the SYSTEM context and needs PowerShell 7 with MTA. Test both cases before relying on it.
2. **`winget export --include-versions`.** One call, JSON schema `https://aka.ms/winget-packages.schema.2.0.json`, structured as `Sources[].SourceDetails` and `Sources[].Packages[].PackageIdentifier` / `Version` (verified). It only covers packages that match a source; everything else goes to stdout as "Installed package is not available from any source".
3. **`winget list --id X -e` exit code.** Robust for a single package but costs about 1–2 s per call. Good for checking a package right after installing it.
4. **Never parse the `winget list` or `winget search` tables.** Columns get truncated with `…` (seen here: `Microsoft.DotNet.Framework.DeveloperPa…`), headers are localized, and spinner noise gets mixed in.
5. The Microsoft.WinGet.Client PowerShell module (`Get-WinGetPackage`, `Install-WinGetPackage`, `Update-WinGetPackage`) is **not installed** on this machine and would need PSGallery. Don't depend on it.

For a fast UI, cross-check against the Uninstall registry keys (HKLM, WOW6432Node and HKCU `...\CurrentVersion\Uninstall`).

## 11. Exit codes (HRESULT, then decimal)

| Code | Decimal | Meaning | What MONOLITH should do |
|---|---|---|---|
| 0x0 | 0 | OK | success |
| 0x8A15002B | -1978335189 | UPDATE_NOT_APPLICABLE: no newer version. Also what `install` returns for an already-installed, current package | treat as "up to date" |
| 0x8A150061 | -1978335135 | PACKAGE_ALREADY_INSTALLED (with `--no-upgrade`) | treat as success |
| 0x8A15010D / 0x8A15010E | -1978334963 / -1978334962 | Another version installed / a newer version is already installed (downgrade) | info |
| 0x8A150014 | -1978335212 | NO_APPLICATIONS_FOUND (bad ID, or not installed for `list`) | |
| 0x8A150016 | -1978335210 | Multiple packages found | always use `-e --source` |
| 0x8A150010 | -1978335216 | NO_APPLICABLE_INSTALLER (wrong `--scope`, `--architecture` or `--installer-type`) | retry without `--scope` |
| 0x8A150056 | -1978335146 | INSTALLER_PROHIBITS_ELEVATION (e.g. Spotify) | re-run unelevated |
| 0x8A150019 | -1978335207 | Command requires admin | use the elevated helper |
| 0x8A150006 | -1978335226 | ShellExecute install failed (installer returned non-zero) | show the log |
| 0x8A150008 / 0x8A150107 | -1978335224 / -1978334969 | Download failed / no network | retry later |
| 0x8A150011 | -1978335215 | Installer hash mismatch | **never** pass `--ignore-security-hash` |
| 0x8A150041 / 0x8A150046 | -1978335167 / -1978335162 | Package / source agreements not accepted | |
| 0x8A150045 | -1978335163 | Failed to open source | run `winget source update` |
| 0x8A150007 | -1978335225 | Manifest version too new for this client | update App Installer |
| 0x8A15004F / 0x8A150050 / 0x8A150068 | -1978335153 / -1978335152 / -1978335128 | Not newer / unknown installed version / pinned | skip |
| 0x8A150101 / 0x8A150103 / 0x8A150111 | -1978334975 / -1978334973 / -1978334959 | App or files in use | ask the user to close the app |
| 0x8A150102 | -1978334974 | Another install is in progress | wait and retry |
| 0x8A150109 / 0x8A15010A / 0x8A15010B | -1978334967 / -1978334966 / -1978334965 | Reboot needed to finish / needed before install / reboot started | mark "reboot pending"; never pass `--allow-reboot` |
| 0x8A15010C | -1978334964 | Cancelled by user | |
| 0x8A15010F / 0x8A15003A / 0x8A15001B / 0x8A15001C | — | Blocked by policy or Group Policy / Store client or app blocked | |
| 0x8A150105 / 0x8A150113 | -1978334971 / -1978334957 | Disk full / system not supported | |
| 0x8A150083 | -1978335101 | Couldn't get the Store licence | |
| 0x8A150001 | -1978335231 | Internal error (seen here with parallel runs) | serialize and retry once |

## 12. `upgrade --all` behaviour

- It skips packages marked as needing explicit upgrade (Discord), pinned packages, and packages whose version is unknown unless you pass `-u/--include-unknown`.
- **`Microsoft.AppInstaller` appears in the upgrade list** (1.26.509.0 → 1.29.380 on this machine). Updating it replaces winget while it's running.
- Recommended "Update all" flow for MONOLITH:
  1. Create a restore point.
  2. Get the list of pending upgrades from COM or from export plus per-ID checks.
  3. Upgrade one ID at a time.
  4. Do App Installer last, or leave it to the Store.
  5. Send user-scope IDs to the unelevated runner.
- Pass `--uninstall-previous` and `--force` only when asked.

## 13. Elevated vs non-elevated

- **Non-elevated:** each machine-scope installer (all VC++ redists, Steam, Chrome, Epic MSI and so on) brings up its own UAC prompt, even with `--silent`.
- **Elevated:**
  - Packages marked `elevationProhibited` fail (Spotify returns 0x8A150056).
  - User-scope installers (Discord, Telegram, Riot, Yandex Browser, osu!) and MSIX/Store apps install into the **elevating account's** profile. If the UAC prompt was answered with a different admin account, the real user never sees the app.
  - Portable packages go to `%ProgramFiles%\WinGet\Packages` instead of `%LOCALAPPDATA%\Microsoft\WinGet\Packages`, with links in the matching `...\WinGet\Links`.
- **SYSTEM:** the alias isn't on PATH, and msstore and user-scope packages don't work.
- **Recommended architecture:** the UI runs `asInvoker` (non-elevated) and handles `user`, `any` and msstore packages. One elevated broker handles `machine` packages, so the user sees a single UAC prompt. If the app has to run elevated, start user-scope winget jobs de-elevated, for example through a scheduled task with `RunLevel Limited` and an interactive logon, or through the shell's `IShellDispatch2.ShellExecute`.
- **Don't pass `--scope` by default.** About 15 manifests in this catalog declare no scope and fail with a scope filter. Telegram with `--scope machine` turns into a portable zip.

## 14. Install advice (MONOLITH)

Owner request (27.10.2026): show what to install on this PC, what is recommended, what is not needed yet and what is not
needed at all. Code: `Modules/Apps/AppAdvisor.cs` (pure rules), `Modules/Apps/PeripheralScan.cs` (read-only device scan),
tests `tests/Monolith.Tests/AppAdvisorTests.cs`. The advice never blocks anything: every package can still be installed.

### Levels

| Level | Tag on the card | Look | Meaning |
|---|---|---|---|
| Required | **НУЖНО** | `Tag.Solid` (white block) | Games or common programs fail without it on this PC |
| Recommended | **РЕКОМЕНДУЕТСЯ** | `Tag` (outline) | Worth having on this PC; counted in «РЕКОМЕНДУЕМ» |
| Optional | **ПО ЖЕЛАНИЮ** | `Tag.Muted` | Install if you want it; also the fallback whenever a needed fact is unknown |
| NotNow | **ПОКА НЕ НУЖНО** | `Tag.Muted` | Only for a specific situation (a program asks, a problem to diagnose, expert use) |
| NotNeeded | **НЕ НУЖНО** | `Tag.Muted`, card at 0.55 opacity (full on hover) | Useless or counter-productive on this PC |

The Drivers page may reuse «РЕКОМЕНДУЕТСЯ» / «ПОКА НЕ НУЖНО»; the strings are fixed by a test.
Every level except ПО ЖЕЛАНИЮ carries one plain Russian sentence (the tag's tooltip and one muted caption line under the
description); ПО ЖЕЛАНИЮ shows its reason, if any, only in the tooltip.

### Inputs (`AdviceContext`)

Every input has a "known" flag; an unknown input never gives a confident claim — the rule falls back to ПО ЖЕЛАНИЮ with a
neutral sentence.
- **Graphics cards** (`GpuKnown`, `GpuVendors`): `SystemProfile` (Win32_VideoController, vendor from the PCI id `VEN_10DE` /
  `VEN_1002` / `VEN_8086`, so a card without a driver still counts). Known when the "gpu" section was read and listed at least
  one adapter. An incomplete profile (slow WMI after a cold boot) is read again on «Проверить» and on return to the page.
- **Windows build and UBR**: `SystemProfile` (CurrentBuildNumber, UBR); the UBR counts only when the "os" section was read.
- **Installed programs** (`InstalledKnown`, `InstalledIds`): every id of every installed catalog entry from the detection
  snapshot (§10), regional variants included, also components found outside winget ("unmanaged"). Known after the first
  detection pass or the cached one.
- **Store apps checked** (`StoreKnown`): the fast detection pass comes before the one-by-one Microsoft Store checks; until a
  full result (or the cache) is applied, a Store app missing from the list (the Xbox app) may still be installed.
- **Peripherals** (`PeripheralsKnown`, `Peripherals`): `PeripheralScan` reads `Win32_PnPEntity.DeviceID` and `PNPClass`
  (read-only WMI, off the UI thread), parses the USB-IF vendor and product id with the Drivers page's `HardwareRules.UsbId`
  (USB/HID `VID_xxxx`, Bluetooth `VID&0002xxxx` classic and `VID&02xxxx` LE — vendor id source 2 = USB-IF; source 1 =
  Bluetooth SIG ids are ignored) and skips devices that are not connected (`Present = false`, `CM_PROB_PHANTOM` 45).
  A maker counts only for nodes of a peripheral class — the Drivers page's list HIDClass, Keyboard, Mouse, MEDIA, Camera,
  Image — so a Corsair flash drive (`USB\VID_1B1C&PID_1A03`, class USB/WPD), hubs and composite parents do not count.
  Logitech counts input devices only (HIDClass, Keyboard, Mouse: G HUB is not for its webcams); Elgato counts only Stream
  Deck product ids (capture cards, Cam Link, Facecam and Wave mics share vendor id 0FD9 but don't use the Stream Deck app).
  A node without a class (no driver) is not ruled out. Known = false on any error or when no device carries a USB vendor id
  at all. Vendor ids: Logitech `046D` and Blue Microphones `B58E` (Logitech; Yeti mics are configured in G HUB), Razer `1532`,
  Corsair `1B1C`, SteelSeries `1038`, Elgato `0FD9` with Stream Deck PIDs 0060, 0063, 006C, 006D, 0080, 0084, 0086, 008F,
  0090, 009A, 00A5, 00AA, 00B3, 00B8, 00B9, 00BA, 00C6.
- **Today** (`DateOnly`, injected) for the end-of-support dates.

The advice is recomputed (pure, cheap) whenever an input changes: profile loaded, detection pass, device scan (page load,
«Проверить», return after 3 minutes).

### Order of evaluation

1. Windows components (Edge) get no advice at all. An installed entry shows no advice on its card (the need is still judged,
   "as if not installed", for the gamer kit's installed / needed counter).
2. **One program per job** (only when installed programs are known): another member of the same role is installed and this
   one is not → НЕ НУЖНО «Уже установлен X — он делает то же самое.» Roles: archiver {7-Zip, WinRAR, NanaZip}, video player
   {VLC, PotPlayer, K-Lite}, disk analyzer {WizTree, TreeSize Free}, uninstaller {Revo, Bulk Crap Uninstaller}, sensor monitor
   {HWiNFO, HWMonitor, LibreHardwareMonitor}, frametime {PresentMon, CapFrameX}, Voicemeeter {Voicemeeter, Banana}, streaming
   {OBS Studio, Streamlabs}. Browsers (all except Edge, which every PC has) → ПОКА НЕ НУЖНО «Уже установлен X — второй браузер
   ставьте, только если он вам нужен.»
3. The entry's own rule (table below).
4. Anything else → ПО ЖЕЛАНИЮ.
5. **Hard safety rule:** an entry flagged KernelDriver, Anticheat, RemoteAccess, Expert, Trialware, Paid or Eol is never НУЖНО
   or РЕКОМЕНДУЕТСЯ — a rule result above is lowered to ПО ЖЕЛАНИЮ (`AppAdvisor.NeverSuggested`). Tested over all entries and
   2 772 input combinations, and the clamp itself with synthetic flagged copies of Steam, Visual C++ 2015+, .NET 10, G HUB and
   Gaming Services (each of the 7 flags → ПО ЖЕЛАНИЮ, out of the kit).
   **Gaming Services is a deliberate, documented case:** it installs Microsoft's own drivers xvdd.sys ("XVD Disk Driver",
   virtual disks of Xbox game packages) and gameflt.sys ("Gaming Install Filter"). They are the component itself, signed by
   Microsoft and required by the Xbox app, not a third-party driver risk like Vanguard, WinRing0 or a virtual audio driver, so
   the entry carries no KernelDriver flag and is НУЖНО when the Xbox app is installed (Game Pass games do not install or start
   without it); without the Xbox app it is ПОКА НЕ НУЖНО.

### Rules

| Entry | Condition | Level |
|---|---|---|
| Visual C++ 2015–2022 x64 and x86 | always | НУЖНО |
| Visual C++ 2005–2013 x86/x64 | always — out of Microsoft support, and the reason says so («Microsoft её больше не обновляет, но замены для таких игр нет»); no Eol flag on purpose, see fact 10 | РЕКОМЕНДУЕТСЯ |
| DirectX End-User Runtime (June 2010) | always (Windows ships no D3DX9, XAudio 2.7, XInput 1.3) | РЕКОМЕНДУЕТСЯ |
| WebView2 Runtime | not installed (installed programs known) / unknown / installed | НУЖНО / ПО ЖЕЛАНИЮ / no advice |
| Gaming Services | Xbox app installed / not installed (Store apps checked) / installed programs or Store apps not checked yet | НУЖНО / ПОКА НЕ НУЖНО / ПО ЖЕЛАНИЮ |
| .NET Desktop Runtime 10 | before its end of support (14.11.2028) / after | РЕКОМЕНДУЕТСЯ («её просят новые программы, а другие версии .NET её не заменяют») / ПОКА НЕ НУЖНО |
| .NET Desktop Runtime 8 and 9 | before 10.11.2026 / from that date (reason names the date) | ПО ЖЕЛАНИЮ / ПОКА НЕ НУЖНО |
| .NET Desktop Runtime 6 | ended 12.11.2024 | ПОКА НЕ НУЖНО |
| .NET Framework 4.8.1 | Windows 10 20H2+ or Windows 11 21H2 (where the catalog offers it) / build too old / 22H2+ (built in) / unknown | РЕКОМЕНДУЕТСЯ / НЕ НУЖНО / НЕ НУЖНО / ПО ЖЕЛАНИЮ |
| XNA Framework 4.0, OpenAL | only if a game asks (Steam installs them per game) | ПОКА НЕ НУЖНО |
| Java 21 / 17 / 8 | Prism Launcher installed | РЕКОМЕНДУЕТСЯ / ПО ЖЕЛАНИЮ / ПОКА НЕ НУЖНО |
| Java 21 / 17 / 8 | CurseForge only, or neither (official launcher) / unknown | ПОКА НЕ НУЖНО / ПО ЖЕЛАНИЮ |
| NVIDIA PhysX, NVIDIA Control Panel, Profile Inspector, NVCleanstall | no NVIDIA card («Видеокарта NVIDIA не найдена.») / NVIDIA present / cards unknown | НЕ НУЖНО / ПОКА НЕ НУЖНО / ПО ЖЕЛАНИЮ |
| Logitech G HUB, Razer Synapse 4, Corsair iCUE 5, SteelSeries GG, Elgato Stream Deck | a device of that maker connected (Elgato: a Stream Deck) / scan unknown | РЕКОМЕНДУЕТСЯ / ПО ЖЕЛАНИЮ |
| Logitech G HUB, Razer Synapse 4 | scan known, no device of that maker | НЕ НУЖНО |
| SteelSeries GG | scan known, no SteelSeries device — the Sonar mixer is free and works with any headset (fact 7) | ПО ЖЕЛАНИЮ |
| Corsair iCUE 5 | scan known, no Corsair USB device — iCUE also drives Corsair RGB memory, which is on SMBus, not USB (fact 7) | ПО ЖЕЛАНИЮ |
| Elgato Stream Deck | scan known, no Stream Deck — the app is also needed for Stream Deck Mobile on a phone (fact 7) | ПОКА НЕ НУЖНО |
| 7-Zip | Windows 10, Windows 11 21H2, 22H2 before 22621.2361 / 22H2 ≥ 2715, 23H2 ≥ 2506, 24H2+ / 22H2 2361–2714 (toggle-gated), 23H2 before 2506 (pre-release), Insider 2262x, UBR unknown | РЕКОМЕНДУЕТСЯ / ПО ЖЕЛАНИЮ «Windows уже открывает RAR и 7z» / ПО ЖЕЛАНИЮ (neutral) |
| WinRAR | no other archiver (paid after the trial; 7-Zip is free) | ПОКА НЕ НУЖНО |
| Process Lasso | always (both it and MONOLITH's game mode change process priorities, §5) | НЕ НУЖНО |
| Autoruns | expert; the basics are on «Автозапуск и службы» | ПОКА НЕ НУЖНО |
| OCCT, FurMark 2 | stress tests: only to check stability after overclocking or a suspected fault | ПОКА НЕ НУЖНО |
| LatencyMon | only for audio/video stutters | ПОКА НЕ НУЖНО |
| DDU | only for a full graphics driver reinstall | ПОКА НЕ НУЖНО |
| Special K | injects into games (anti-cheat risk) | ПОКА НЕ НУЖНО |
| Rufus | bootable USB only | ПОКА НЕ НУЖНО |
| Revo Uninstaller, Bulk Crap Uninstaller | only for programs that do not uninstall normally | ПОКА НЕ НУЖНО |
| K-Lite Codec Pack | changes file associations; VLC plays almost everything | ПОКА НЕ НУЖНО |
| Voicemeeter, Voicemeeter Banana | virtual audio driver + reboot, only for audio mixing | ПОКА НЕ НУЖНО |
| Sunshine | installs a service and opens network ports | ПОКА НЕ НУЖНО |
| AnyDesk, TeamViewer | only while someone you trust helps you — scammers often ask to install them | ПОКА НЕ НУЖНО |
| Steam | the largest PC store | РЕКОМЕНДУЕТСЯ |
| Discord | the main voice chat of players | РЕКОМЕНДУЕТСЯ |
| everything else | — | ПО ЖЕЛАНИЮ |

### Where it shows (no new rows, pages or hero blocks)

- **Card:** the advice tag is the first tag (only when the state is known and the program is not installed); one muted,
  trimmed caption line with the reason for every level except ПО ЖЕЛАНИЮ; НЕ НУЖНО cards dimmed. Cards are not reordered.
  The former «NVIDIA НЕ НАЙДЕНА» badge is replaced by this advice.
- **Tab «РЕКОМЕНДУЕМ · N»** next to «ВСЕ»: not installed, installable НУЖНО + РЕКОМЕНДУЕТСЯ cards; empty groups hidden.
  Before the first detection it says the advice appears after the check (no "everything installed" claim); without winget it
  says that nothing can be checked until «Установщик приложений» is installed.
- **«Набор геймера»:** the same row and button; its set is computed: runtime-category entries that are НУЖНО or
  РЕКОМЕНДУЕТСЯ for this PC (`AppAdvisor.Kit`). So no end-of-life .NET 6/8/9, WebView2 only once installed programs are
  known, Gaming Services only with the Xbox app, .NET Framework 4.8.1 only where offered, Java 21 only with Prism Launcher.
  The tooltip names what is in the kit and which runtimes are left out.
- **Install confirmation:** ПОКА НЕ НУЖНО / НЕ НУЖНО packages of the plan are listed first with their reason
  («НЕ НУЖНО — NVIDIA PhysX: Видеокарта NVIDIA не найдена.»), with one lead sentence; the install is never blocked. For those
  packages the warning lines that would repeat the reason (expert tool, end of support, paid/trial) are left out; the
  ВНИМАНИЕ lines (remote access, kernel driver, anti-cheat) always stay. The direct vendor-download confirmation started from a
  card (LatencyMon and other website-only entries) lists the advice the same way.

### Verified facts (checked 27.10.2026)

1. **.NET end of support** (Microsoft policy page): .NET 10 LTS — 14 Nov 2028; .NET 9 STS and .NET 8 LTS — both
   10 Nov 2026; .NET 6 — 12 Nov 2024. https://dotnet.microsoft.com/platform/support/policy/dotnet-core,
   https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/
2. **Native RAR / 7z in File Explorer** (libarchive; password-protected archives are not supported) first shipped with the
   "Moment 4" update: the optional preview KB5030310 (26 Sep 2023, 22621.2361) and the October security update KB5031354
   (22621.2428), both only with "Get the latest updates as soon as they're available" switched on (Impress Forest: the team
   confirmed it works with KB5030310). Then the preview KB5031455 (31 Oct 2023, 22621.2506 / 22631.2506 — the first 23H2 release
   build); for everyone with the November 2023 security update KB5032190 (22621.2715 / 22631.2715, "Moment 4 features enabled by
   default"). 24H2 (26100) and 25H2 (26200) include it. So MONOLITH claims "no native support" only below 22621.2361, "supported"
   from 22621.2715 / 22631.2506 / any later build, and says nothing confident in between (or for pre-release 22631 builds).
   **Correction to the task text:** "22621 UBR ≥ 2506" is not enough to claim support on 22H2, and below 2506 the feature may
   already be on. https://forest.watch.impress.co.jp/docs/news/1534610.html,
   https://www.neowin.net/news/windows-11-moment-4-features-are-now-available-to-all-in-the-latest-non-security-update,
   https://www.bleepingcomputer.com/tag/kb5031455/,
   https://www.techspot.com/news/100663-windows-11-extends-support-archive-formats-thanks-open.html,
   https://www.elevenforum.com/t/kb5032190-windows-11-cumulative-update-build-22621-2715-22h2-and-22631-2715-23h2.19801,
   https://windowslatest.com/2023/11/14/windows-11-kb5032190-cumulative-update
3. **Minecraft and Java:** the official launcher uses its bundled Java runtime; 1.20.5–1.21.x need Java 21, 1.18–1.20.4
   need 17, Minecraft 26.1+ needs Java 25 (Temurin 25 is not in the catalog yet).
   https://hopper.minecraft.net/help/installing-java, https://minecraft.wiki/w/Java_Edition_26.1-snapshot-1,
   https://docs.fabricmc.net/players/installing-java/windows
4. **CurseForge installs the Java the game needs itself** (CurseForge support, "Getting started").
   **Correction to the task text:** CurseForge alone does not make a separate JRE recommended — Java stays ПОКА НЕ НУЖНО.
   https://support.curseforge.com/support/solutions/articles/9000218572-getting-started
5. **Prism Launcher** runs Minecraft on an installed Java unless "Auto-download Mojang Java" is switched on in Settings → Java,
   so Java 21 stays РЕКОМЕНДУЕТСЯ for Prism users and the reason mentions the option.
   https://prismlauncher.org/wiki/getting-started/installing-java, https://prismlauncher.org/wiki/help-pages/java-settings
6. **PhysX ships inside the NVIDIA driver:** NVIDIA's standalone PhysX System Software 9.26.0703 is "the PhysX System Software
   bundled with NVIDIA Release 615 GA1 drivers". https://www.nvidia.com/en-us/drivers/physx/physx-9-26-0703-driver/
7. **Peripherals and their software.** USB vendor ids (usb.ids): 046d Logitech, b58e Blue Microphones, 1532 Razer USA, 1b1c
   Corsair, 1038 SteelSeries ApS, 0fd9 Elgato Systems; usb.ids lists 0fd9:0060/0063/006c/006d as Stream Deck models and
   python-elgato-streamdeck the newer ones (0080 MK.2, 0084 Plus, 0086 Pedal, 008f XL V2, 0090 Mini MK.2, 009a Neo, 00a5, 00aa
   Studio, 00b3, 00b8, 00b9, 00ba, 00c6 Plus XL). 0fd9 also covers Elgato capture cards and cameras, 1b1c Corsair flash drives
   (Flash Voyager), 046d Logitech webcams and office devices — hence the class and PID filters. The scan cannot see three uses
   of the makers' software, so those cases are not НЕ НУЖНО: **SteelSeries Sonar** (in GG) is free and works with any headset
   and mic; **Corsair iCUE** controls Vengeance RGB / Dominator memory over SMBus, without a USB device; **Stream Deck Mobile**
   on a phone needs the Stream Deck app on the PC. **G HUB** configures Blue Yeti microphones (Blue VO!CE, Logitech, 28 Sep
   2021), which carry the Blue vendor id b58e. The РЕКОМЕНДУЕТСЯ sentence for Logitech says G HUB is for Logitech G devices
   and Yeti mics (Logitech office mice use Logi Options+). https://github.com/usbids/usbids/blob/master/usb.ids,
   https://github.com/abcminiuser/python-elgato-streamdeck/blob/master/src/StreamDeck/ProductIDs.py,
   https://steelseries.com/gg/sonar, https://help.elgato.com/hc/en-us/articles/17162153663117-Elgato-Stream-Deck-Mobile-System-Requirements
8. **WebView2** is part of Windows 11 and is what the new Teams / Outlook and Microsoft 365 render with; Steam draws its UI with
   its own Chromium (CEF, steamwebhelper). **Correction to the task text:** no source shows that Steam, Battle.net or the
   EA app need WebView2, so the reason does not name them. https://developer.microsoft.com/microsoft-edge/webview2/,
   https://developer.valvesoftware.com/wiki/CEF
9. **Gaming Services drivers:** the package installs the "XVD Disk Driver" (xvdd.sys, shown as "Xvdd SCSI Miniport") and the
   "Gaming Install Filter" (gameflt.sys); the GamingServices service loads them (rule 5 above).
   https://learn.microsoft.com/en-us/answers/questions/4340819/gaming-service-startup-issue,
   https://windowsreport.com/xvdd-scsi-miniport/
10. **Visual C++ 2005–2013 are out of Microsoft support:** Visual Studio 2013 ended 9 Apr 2024, 2012 on 10 Jan 2023, 2010 on
    14 Jul 2020, 2008 on 10 Apr 2018, 2005 on 12 Apr 2016; Microsoft's redistributable page marks 2013 and older "no longer
    supported". They are still the only way to run games built with them, so they stay РЕКОМЕНДУЕТСЯ (and in the kit) and the
    reason says Microsoft no longer updates them. The Eol flag is deliberately not set: it would push them below the games'
    needs through the hard safety rule, while .NET 6 (Eol) has a supported successor programs can move to.
    https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist, https://learn.microsoft.com/lifecycle/faq/visual-c-faq,
    https://www.neowin.net/news/microsoft-reminds-us-that-visual-studio-2013-support-ends-on-april-9-2024/
11. Gaming Services for Xbox / Game Pass (§4), Process Lasso vs MONOLITH's priorities (§5), DirectX June 2010 contents (§4),
    remote-access scams (§7), Sunshine service and ports (§6) — from this report.

### Known limits

- Peripherals are matched by maker (Elgato: by Stream Deck model), not by model: a Logitech office mouse or its Unifying
  receiver also makes G HUB РЕКОМЕНДУЕТСЯ; the sentence says who the program is for.
- **Drivers page alignment (open):** «Драйверы → Подбор для вашего ПК» still words the same components its own way — the
  peripheral note says the maker's program is «по желанию», and .NET 10 next to another installed .NET is offered as optional
  «только если программа просит». The levels should come from `AppAdvisor.Assess` (same tag strings) so both pages agree; this
  is a change in `Modules/Drivers/Hardware/HardwareAdvisor.cs`, outside the Apps module. The Apps .NET 10 reason already
  states the Drivers page's fact (other .NET versions do not replace it).

## Sources
- https://learn.microsoft.com/en-us/windows/package-manager/winget/
- https://learn.microsoft.com/en-us/windows/package-manager/winget/install
- https://learn.microsoft.com/en-us/windows/package-manager/winget/upgrade
- https://learn.microsoft.com/en-us/windows/package-manager/winget/export
- https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md
- https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/m/Microsoft/DirectX/9.29.1974.0/Microsoft.DirectX.installer.yaml
- https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/s/Spotify/Spotify/1.3.6.1921.g7086c071a617/Spotify.Spotify.installer.yaml
- https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/d/Discord/Discord/1.0.9265/Discord.Discord.installer.yaml
- https://www.nuget.org/packages/Microsoft.WindowsPackageManager.ComInterop
- https://github.com/microsoft/winget-cli/issues/3049
- https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/
- https://www.osnews.com/story/141916/winring0-why-windows-is-flagging-your-pc-monitoring-and-fan-control-apps-as-a-threat/
- https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist

Raw verification output (read-only `winget show` results) is in `C:\Users\Lycoris\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\scratch-workspaces\138ba664-9926-43d3-a4fa-8eddf460b1ce\8084facf-449b-4162-ae87-c9482abc905a\scratch-2026-10-26-9af17d\res1.json` through `res4.json`. The scope probe results are in `scope.txt` and the sample export in `export.json`, both in the same folder.