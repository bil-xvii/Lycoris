# Hardware verification of the device writers (02.11.2026)

Authorised by the owner on his own PC (Windows 11 build 26300, RTX 5070 Ti, X27U W3 2560x1440@239.97 Hz + a rotated Odyssey G5,
Logitech G HUB, Razer Seiren V3 Mini microphone). Not elevated, desktop user, console session. Opt-in xUnit category
`tests/Lycoris.Tests/Hardware/*` (skipped unless `LYCORIS_HW_TESTS=1`; never part of the default run or `--selftest`).

Run: `$env:LYCORIS_HW_TESTS='1'; dotnet test tests/Lycoris.Tests --filter "FullyQualifiedName~Hardware"`
(`LYCORIS_HW_REPORT=<file>` also appends the numbers to a file; the guard-kill case needs `LYCORIS_HW_KILL=1` and ends the test host on purpose).

How it is isolated: the REAL writers (`DeviceWriters`) are driven through `ChangeSession` with `ChangeJournal.ForTests(<temp file>)`
(`%ProgramData%\MONOLITH` is never touched), `BypassElevationForTests`, and an identity `RegistryHelper.Redirect` (real HKCU Control
Panel values are snapshotted/restored for real, but no `.reg` export goes to ProgramData). Every case: read original -> change through
ChangeSession (state held 0.4-2.4 s) -> undo from the journal -> verify equal -> `finally` restores and re-verifies.

## Results

| # | Device | Result | Numbers |
|---|---|---|---|
| 1 | SPI (10 parameters) | all OK: set, live read-back, registry value, journal undo, registry equals original | held ~0.7 s each; mouse-speed 10->11, wheel-lines 3->4, wheel-chars 3->4, routing 2->0, double-click 900->899, trails 0->2, sonar 0->1, vanish 1->0, kb-delay 3->2, kb-speed 0->1. Registry strings/DWORD identical before/after. Sonar/vanish live in UserPreferencesMask (read via SPI only). Button swap deliberately not run. |
| 1b | Keyboard mapping | correct | delay 3 = 1000 ms, speed 0 = 2.5/s (Windows: 250/500/750/1000 ms; 2.5..30/s). The app shows exactly these. |
| 2 | Display mode 239.97 -> 144 Hz, real `DisplaySafeApply` + real `--display-guard` | OK | guard armed + countdown shown after ~0.46 s; keep: live 144 (143.973 exact), saved (ENUM_REGISTRY) 144; undo from journal: live 239.970 exact, saved 240; total 2.06 s per leg. Decline path: reverted in 2.3 s, journal empty, GDI live+saved equal to the start. |
| 2b | Guard safety path | OK | change pending, host killed 1.5 s after the switch: the guard restored 2560x1440@240 0.3 s after the kill, then exited (no stray process), saved mode untouched. |
| 3 | HDR (X27U) | OK | supported, was off, not policy-limited; on via guard in 2.4 s, undo 2.4 s, off again. |
| 4 | DDC/CI | OK | X27U: brightness 100 (0..100), contrast 50 (0..100), two reads equal, MCCS caps string read (vcp 10, 12 present); Odyssey G5: 30 (0..50), contrast 50 (0..50), caps read. Write: brightness 100->99 in 244 ms, undo 323 ms, back to 100. Only the primary monitor was written. |
| 5 | Capture endpoint (Razer Seiren V3 Mini) | OK | level 0.6902 (0x3F30B0AC) -> 0.7102 -> undo bit-identical; mute false->true->false; level untouched by the mute undo. Render endpoints untouched. |
| 6 | Network, read-only | plausible | Ethernet (Realtek 8126, inbox driver 2015) 1000 Mbit/s full duplex, *SpeedDuplex=0 valid [0,1,2,3,4,6,2500], *ReceiveBuffers 512 � identical to Get-NetAdapterAdvancedProperty. No EEE keyword on this driver (none offered). Ping: router 0.9-3.2 ms (p50 1.0), 1.1.1.1 2.3-2.5 ms, 5/5 each, 2 pings/s total. DNS: router 0.8 ms, Quad9 3.0, Cloudflare 6.4, Google 49.1; 0 % timeouts. No NIC writes. |
| 6b | Polling rate / DPI / DPC | NOT verified | raw-input polling needs mouse movement by the owner; DPC latency needs the kernel ETW session (admin). |
| 7 | GPU, driver, monitors, vendors, power, games, drivers hardware analysis | match reality | see below |

### Case 7: app versus Win32_PnPEntity / registry / powercfg
GPUs (RTX 5070 Ti 32.0.16.2101 / package 621.01, 2026-10-19, VRAM 16303 MB; AMD iGPU 32.0.11024.2), board MSI PRO X870-P WIFI + BIOS 1.A92,
CPU 9800X3D 8c/16t, 2x16 GB DDR5-6000, disks, runtimes, monitor names from EDID (X27U W3, Odyssey G5), G HUB 2026.7.992920 / Razer Synapse
4.0.896 / INZONE Hub 1.0.20.0 / OpenRGB 1.1.0 installed+running, Logi Options+/SteelSeries/iCUE/MSI Center not installed � all equal to the
registry/WMI. Power plan: active High performance (8c5e7fda�), 3 plans. Games: Steam 4 (the two libraries C: and D:\SteamLibrary; the redistributable
is correctly skipped) + Battle.net 1. Audio devices: 5 of the 6 Win32_SoundDevice rows (the NVIDIA virtual audio device is dropped on purpose).
Mismatch list: none that is a bug. Cosmetic: Win32_SoundDevice calls the Razer mic "����� ���������� USB" (the endpoint name "Razer Seiren V3 Mini" is
what the audio card shows); the system has three Qualcomm Wi-Fi interface entries, the page shows one.

## Bugs found / fixed
1. `mouse-trails` allowed a target of 1, but Windows treats 1 as "off" and reads it back as 0, so such a change always failed its read-back
   ("����� ������ ��������� 0, ��������� 1"). The page never offers 1 (0, 2..7), so it was latent. Fix: `SpiCatalog` `AllowedTargets {0,2..7}`;
   unit test `DeviceJournalTests.Value_outside_the_allowed_targets_is_refused("mouse-trails", 1)`.
2. Test-only seams for the guard, both inert in production: `DisplayWriter.GuardExecutableForTests`, and the guard accepts an xUnit `testhost`
   parent only when `LYCORIS_HW_TESTS=1` (otherwise the guard's parent-name check would end it at once).

No defect in the display protocol, HDR, DDC, audio or journal undo was found; restoring the persisted mode returned the exact 239.970 Hz.

## Not verified / residual risk
- Mouse button swap (needs a real click on the countdown; could lock the owner out).
- Raw-input polling rate/DPI (needs hand movement), DPC latency (admin ETW), NIC property writes and adapter restarts (forbidden by the brief).
- `UserPreferencesMask` bits (sonar / vanish) were verified through SPI, not byte-compared in the registry.
- Fullscreen D3D / RDP block paths, a cable that cannot carry a mode (�5 e), and a reboot test (�5 a) were not run.
- The countdown window itself was replaced by a hook (it needs a human click); its timing and revert paths were exercised with real writers.
- Owner's values noted at the start: double-click 900 ms (the UI maximum), keyboard delay 3 / speed 0 (the slowest). Left as they were.
