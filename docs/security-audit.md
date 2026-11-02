# MONOLITH — security audit and hardening (02.11.2026)

Owner request: «Добавь к этой программе защиту от взлома». Interpretation: (1) another program or an unprivileged local user must not be able
to use MONOLITH's administrator rights to escalate privileges or damage the PC; (2) the update/download channel cannot be hijacked;
(3) the exe, settings and journal cannot be silently tampered with; (4) a hostile file (profile, settings, journal, replay arguments, winget/WMI
output, registry values) cannot make the app run code or write outside what the user approved. Not in scope: packers, obfuscators,
anti-debug tricks (they raise antivirus false positives and protect nothing).

Method: read-only review with an attacker mindset over the whole source tree (the earlier `docs/review-findings.md` and rules 1–10 of
CLAUDE.md are the baseline), a PoC only where it is harmless (PowerShell module path), fixes with unit tests, a hostile-input corpus
(`Core/Security/SecurityCorpus`) that the self-test stage «БЕЗОПАСНОСТЬ» runs against the parsers.

## 1. Threat model

**Attacker** = any program that runs as the same Windows user at medium integrity (malware, a booby-trapped installer, a script) or another
standard user on the PC. It can write everything the user can write: Desktop, Downloads, `%LocalAppData%`, `%TEMP%`, HKCU, the user's Documents
(`WindowsPowerShell\Modules`), environment variables of the user, and can start MONOLITH.exe with any arguments. It **cannot** write
`%ProgramFiles%`, `%ProgramData%\MONOLITH` (ACL-locked), HKLM, or touch an elevated process directly.
**Goal** = get code running with the administrator token that MONOLITH obtains after the user presses «Да» in UAC, or make MONOLITH
change the system in ways the user never saw.
**Out of scope**: a process that already runs elevated or as SYSTEM; physical access; kernel/driver attacks; the user knowingly approving a
change after reading the confirmation dialog.
Note: Microsoft does not treat UAC as a security boundary, so some residual risks below are inherent to «normal user starts the app, app asks
for rights»; the fixes remove every case where MONOLITH made it *easier* than it has to be.

## 2. Attack-surface inventory

| # | Boundary / input | Who can write it | Read by | Defense (before → now) |
|---|---|---|---|---|
| 1 | `MONOLITH.exe` itself (path chosen by UAC) | user, if the folder is user-writable (Desktop\Ликорис, Downloads) | UAC → elevated copy | none → integrity gate (S-01) |
| 2 | Single-file native libs `%TEMP%\.net\MONOLITH\<id>\*.dll` | same user | elevated process (WPF natives) | none → signature check before every elevated start (S-02) |
| 3 | DLL search order (cwd, exe folder) | same user | any `LoadLibrary` by plain name | default → `SetDefaultDllDirectories`, `DefaultDllImportSearchPaths(System32)` (S-10) |
| 4 | Command line of the elevated copy (`--elevated`, `--replay-*`, `--e2e`, `--apply-update`, `--display-guard`) | anyone who can start the exe (UAC then asks the user) | `App.OnStartup` | allow-list of commands → + page/parameter validation, `--e2e` gated, updater handshake (S-06, S-09, S-12) |
| 5 | `settings.json` (also holds the cached update release) | user | every start (elevated too) | JSON only → + `SettingsValidator`, bounded read (S-08, S-13) |
| 6 | Caches/markers in `%LocalAppData%` (`apps-installed.json`, games cache, NVIDIA lookup XML, EcoQoS marker, custom selection) | user | elevated app | try/catch → bounded `SafeIo` reads, DTD-free XML (S-13, S-23) |
| 7 | Imported profile file | user / downloaded | Optimize page | strict format, 256 KB, id regex (already good) → in the corpus |
| 8 | Undo journal `%ProgramData%\MONOLITH\journal.json` | Administrators/SYSTEM only (ACL, re-secured at every elevated start) | elevated app | `JournalTrust` (rebuilds every undo command and compares), `IsForbiddenRestoreTarget` → list extended (S-18) |
| 9 | `%ProgramData%\MONOLITH\Downloads` (driver/app installers) | Administrators only | elevated installer launch | host allow-list + redirect check + vendor Authenticode → + size cap, `PsText` for the signature script (S-15, S-16) |
| 10 | `winget` alias `%LocalAppData%\Microsoft\WindowsApps\winget.exe` | user | elevated `WingetClient` | none → only the Windows app-execution alias (reparse point) is run elevated (S-04) |
| 11 | Developer tools on `PATH` (npm.cmd, yarn.cmd, pip, python, dotnet) | user (their folders) | elevated Cleaner (`CleanToolsAsync`) | none → refused when the tool is user-writable (S-05) |
| 12 | Child processes: environment, PowerShell module path, output | user env (HKCU\Environment), tool output | every `ProcessRunner` call | none → cleaned env, trusted `PSModulePath`, 8 M-char output cap (S-03, S-11, S-15) |
| 13 | Temp files (`winget export`, UnelevatedWinget log/exit) | user (%TEMP%) | elevated app | `%TEMP%` → private folder (admin-only when elevated) for export (S-17); log/exit files left (S-20) |
| 14 | Update channel: GitHub API answer, release assets, staged file in `%LocalAppData%\MONOLITH\Updates`, `--apply-update` | network / user | `Core/Updates` | host allow-list + SHA-256 → + owner signature, inner-version check, handshake (S-06…S-08) |
| 15 | Named objects: mutex `Local\MONOLITH.SingleInstance`, display-guard events (random GUID) | same user | startup | default ACL → documented (S-19) |
| 16 | URLs/URIs opened (`UnelevatedShell.OpenAsync`, launcher URIs) | catalogs, game cache | shell | https/ms-windows-store allow-list, always de-elevated (already good) → in the corpus |
| 17 | Registry values read (startup entries, services, manifests), WMI/winget/PowerShell output | any program | parsers | tolerant parsers → depth limits (VDF), DTD-free XML, `JsonDocument` depth 64 |
| 18 | Logs `%LocalAppData%\MONOLITH\Logs` | user | humans | contains paths, package ids, no secrets/tokens; no crash dumps are enabled (S-25) |

Not present (checked): named pipes, TCP/UDP listeners, COM servers, shell extensions, URL-protocol registration, `BinaryFormatter`/type-name
JSON deserialization (only `System.Text.Json` into fixed types), certificate-validation bypass, plain-HTTP requests, credentials or API keys.

## 3. Findings

Severity = realistic impact for the attacker model above. Status: **fixed** (code + tests), **mitigated** (reduced, residual stated), **open** (needs the owner).

| ID | Sev | Finding | Exploit scenario | Fix / status |
|---|---|---|---|---|
| S-01 | High | UAC binds to the exe **path**, not the bytes. MONOLITH.exe in a user-writable folder can be swapped before the user clicks «Да». | Malware renames the running `MONOLITH.exe` (allowed for a running image) and drops its own file; the user clicks the title-bar button, UAC shows the path, the attacker's file runs elevated. Also: elevated copy starts child copies (`TryRelaunchSameRights`, display guard) without UAC. | **Mitigated.** `SelfIntegrity`: SHA-256 at start, re-hashed right before every elevated relaunch/guard start — a changed file is refused; network/Temp/removable locations are refused; a signed exe whose signature no longer matches is refused; a one-time warning (and log line) tells the user to keep MONOLITH in `C:\Program Files\Lycoris`. Residual: the instant between our check and consent.exe opening the file, and an unsigned exe in a writable folder. **Owner:** install to Program Files + sign (§5). |
| S-02 | High | Single-file build (`IncludeNativeLibrariesForSelfExtract`) unpacks 5 WPF natives into `%TEMP%\.net\MONOLITH\<id>` — writable by the same user — and the elevated process loads them. | Malware replaces `wpfgfx_cor3.dll` there; next elevated start runs it as admin. | **Mitigated.** `BundleGuard` verifies Authenticode (WinVerifyTrust, offline) of every extracted DLL before each elevated start; a non-valid file refuses elevation with a clear message. Verified on the published exe: all 5 files are Microsoft-signed. Residual: first run before any check, and natives already loaded by the unelevated parent. **Proper fix (owner/decision):** ship multi-file in Program Files (no self-extract) — see §5. |
| S-03 | High | Elevated `powershell.exe` auto-loads modules from `PSModulePath`; the default puts `Documents\WindowsPowerShell\Modules` (user-writable) **first**. Scripts call `Get-NetAdapter`, `Get-AuthenticodeSignature`, `Get-Command`… | Malware drops `Documents\WindowsPowerShell\Modules\Microsoft.PowerShell.Security\…psm1`; the next elevated signature check or NIC change runs it as admin. PoC (harmless): `$env:PSModulePath` of a default session starts with the user's Documents folder. | **Fixed.** Every `powershell.exe` child gets `PSModulePath` = System32 + Program Files modules only (order matters: the machine default order makes PS re-add the user folder — covered by a test that runs a real PowerShell), plus the cleaned environment (S-11). |
| S-04 | High | `WingetClient` ran `%LocalAppData%\Microsoft\WindowsApps\winget.exe` from the elevated process. The folder is user-writable; a regular file there replaces the alias. | Malware overwrites the alias with its own exe; the Apps page (list/export) executes it as admin. | **Fixed.** When elevated, only a reparse point (the Windows app-execution alias) is accepted; a plain file is refused (log line). |
| S-05 | High | Cleaner dev-cache items run `npm.cmd`/`yarn.cmd`/`pip`/`python`/`dotnet` found on `PATH` with the admin token (PATH dirs are usually user-writable). | `%AppData%\npm\npm.cmd` replaced → «npm: проверка кэша» runs it elevated. | **Fixed.** `PathTrust` (ACL walk) – an elevated Cleaner skips a tool that lies where a non-admin can write, with an explanation. |
| S-06 | High | Updater (`--apply-update <old exe> <pid>`) was a confused deputy: any caller could point it at any `.exe`; an elevated updater would rename and overwrite it. The staged updater lives in a user folder and (elevated route) is copied after the handle was released. | Malware starts the (UAC-prompting) updater with a victim path in Program Files, or swaps the staged file between the old copy's check and the elevated copy. | **Fixed.** Target must be a MONOLITH program (ProductName, size) outside `%WINDIR%`; the pid must be the target's process or gone; the old copy passes the SHA-256 of the staged file as 4th argument, the elevated updater **requires** it, hashes itself and holds its file (`FileShare.Read`) while exchanging files; the new program must be strictly newer. Exit code 13 + message. Tests in `UpdateSecurityTests`. |
| S-07 | High | Update authenticity: the SHA-256 comes from the same server as the file; with an unsigned running exe an unsigned update is accepted. A hijacked GitHub account = arbitrary code on every PC. | Attacker publishes release 9.9.9 with a matching `.sha256`. | **Mitigated / open.** Implemented: `MONOLITH.exe.sig` (ECDSA P-256 over version + file name + SHA-256), `ReleaseSignature`, pinned `PinnedPublicKey`, mandatory in the downloader once set, `tools/release-key.ps1` + `release.ps1` signing step, cross-tested PowerShell↔C#. **Open:** `PinnedPublicKey` is empty until the owner runs `tools\release-key.ps1 -New` and commits the printed line (§5); until then the log says the channel is unauthenticated. |
| S-08 | Medium | `settings.json` holds the cached release (tag, URLs) and is user-writable → forged «available» release; or a real **old** release offered as new (rollback to a vulnerable version, still matching its own hash/signature). | Malware edits the cache: tag 99.0.0 with URLs of release v1.0.0. | **Fixed.** The version stored inside the downloaded exe must equal the release version and be newer than the running build; asset URLs are pinned to the releases repository path (already). The updater re-checks «strictly newer». |
| S-09 | Medium | `--replay-page/--replay-command/--replay-param` : command allow-list existed, but page key and parameter were free text. A foreign process can `runas` MONOLITH with replay arguments; UAC says «MONOLITH». | A page key / parameter with control characters or 1 MB of text reaches a command. | **Fixed (validation).** Page = `[a-z][a-z0-9-]{0,31}`, parameter ≤ 200 printable chars, command from the allow-list (unchanged, reflection only finds `{Name}Command` of an allow-listed name). **Accepted residual:** a same-user process can still make the UAC prompt appear; the command then runs through its own confirmation dialog — cannot be closed without a secret the attacker cannot read, which does not exist between same-user processes. |
| S-10 | Medium | Plain-name `LoadLibrary` searched the exe folder and the current directory (DLL side-loading, e.g. `version.dll` beside MONOLITH.exe). | Malware drops `version.dll` into Desktop\Ликорис. | **Fixed.** Module initializer (before any other code): `SetDefaultDllDirectories(SYSTEM32 \| USER_DIRS)` + only the runtime's own native folders added back; `DefaultDllImportSearchPaths(System32)` for all P/Invokes; image-load policy (no remote images, prefer System32). `ProhibitDynamicCode`, strict-handle and signature-only policies are deliberately **not** used (JIT, WPF and GPU drivers would break). Verified: app starts (Debug and single-file), self-test passes. |
| S-11 | Medium | User-controlled environment inherited by elevated children (`DOTNET_STARTUP_HOOKS`, `COR_PROFILER`, `NODE_OPTIONS`, `PYTHONSTARTUP`…). | HKCU\Environment poisoned. | **Fixed for children** (`EnvironmentGuard.Clean`). For the process itself only a log warning is possible (hooks load before our code); ShellExecute-elevated processes get the account's environment from Windows, not from the caller. |
| S-12 | Medium | `--e2e <dir>` (real tweak apply/revert + self-elevation) was reachable in release builds. | Malware starts it; the user sees a plausible UAC prompt. | **Fixed.** Only Debug builds, or `MONOLITH_E2E=1`. |
| S-13 | Medium | Settings and caches were parsed without size/shape limits; values reached an elevated process (close list, game folders, interval). | Settings with 1 GB of text, a UNC folder, 1 ms refresh interval, the antivirus on the Game Boost close list. | **Fixed.** `SafeIo` (≤ 4 MB, no links, `FileShare.Read`), `SettingsValidator` (known enums, clamped numbers, ≤ 500 items, plain text, local absolute folders only), third-party AV processes added to the protected-process list. |
| S-14 | Medium | VDF (Steam) parser recursed without limit → uncatchable stack overflow = crash of the elevated app by a planted `libraryfolders.vdf`. | 300 000 `{`. | **Fixed.** Depth limit 64; Steam/Epic/GOG/manifest files read through `SafeIo`. |
| S-15 | Low | Unbounded memory/disk: child output captured without limit, `HttpClient` buffer 2 GB, downloads without cap. | A hostile tool/server streams forever. | **Fixed.** 8 M chars per stream, 8 MB response buffer, 6 GB download cap. |
| S-16 | Low | The only script with a dynamic path (`Get-AuthenticodeSignature -LiteralPath '…'`) doubled only `'`; PowerShell also treats U+2018/2019/201A/201B as quotes. | A file name with a typographic quote ends the literal. | **Fixed.** `PsText.Literal` doubles all quote variants and refuses control characters; tested against a real PowerShell. All other scripts are constants or built from whitelisted GUIDs/numbers (`NicWriter.Script`, journal rebuild-and-compare). |
| S-17 | Low | `winget export -o %TEMP%\monolith_wg_*.json` — predictable folder, read back by the elevated app. | Swap between write and read → fake «installed» list. | **Fixed.** Private temp folder (admin-only when elevated), bounded read. |
| S-18 | Low | `IsForbiddenRestoreTarget` missed environment, boot-time, KnownDLLs, AutoRun, provider and COM-server locations. | Needs a forged journal, i.e. an already-compromised ACL; defense in depth. | **Fixed.** Extended (the classic-context-menu CLSID the tweak owns is excepted, tested). |
| S-19 | Low | Single-instance mutex `Local\MONOLITH.SingleInstance` and window activation by process name can be squatted → MONOLITH refuses to start / offers to end a windowless process named MONOLITH. | DoS only (user is asked before anything is ended). | **Open, accepted.** A mutex ACL cannot separate same-user processes; elevated and normal copies must both see it. |
| S-20 | Low | `UnelevatedWinget` log/exit files live in the user's `%TEMP%` (the de-elevated cmd writes them). | Forged exit code «0» → the Apps page believes an install worked. Package ids are validated by regex, nothing is executed from them. | **Open, accepted.** |
| S-21 | Info | Already good: profile import (strict, 256 KB, id regex), `JournalTrust` rebuild-and-compare, `PathGuard` (no reparse points, no protected folders), admin-only `%ProgramData%\MONOLITH` with link removal, browsers/games de-elevated, HTTPS-only vendor downloads with redirect re-check and Authenticode signer match, no polymorphic JSON. | — | Kept; their hostile cases are in the corpus. |
| S-22 | Medium | The elevated copy relaunches itself without UAC (language change) and starts the display guard from `AppPaths.ExecutablePath`. | Same swap as S-01, without even a UAC prompt. | **Fixed.** Same integrity gate before both. |
| S-23 | Low | `NvidiaLookup` parsed vendor/cached XML with default settings (DTD allowed, entity expansion up to 10 M chars). | Hostile cached copy. | **Fixed.** `SafeXml` (no DTD, no resolver, size cap). |
| S-24 | Info | Other process mitigations considered and rejected: dynamic-code prohibition (JIT/WPF), signature-only images (GPU user-mode drivers), extension-point disable (IMEs), child-process restriction (we spawn tools). | — | Documented. |
| S-25 | Info | Logs contain local paths and package ids, no tokens/passwords; no crash dumps configured; the unhandled-exception handler logs messages only. | — | Accepted. |

## 4. What was implemented (map)

`Core/Security/*` — `PathTrust` (who can write a path), `SelfIntegrity` + `BundleGuard` (hash, signature, location, unpacked natives, elevation gate),
`Hardening` (DLL search, image-load policy, module initializer), `EnvironmentGuard`, `PsText`, `SafeIo`, `SafeXml`, `SettingsValidator`,
`Authenticode` (WinVerifyTrust), `ReleaseSignature` (ECDSA verify/sign, statement, rollback rule), `SecurityCorpus` (hostile inputs).
Wired into `Elevation`, `ProcessRunner`, `AppSettings`, `WingetClient`, `CleanRunner`, `VendorDownloads`, `AdminIntent`, `App`, `DisplayWriter`,
`ChangeJournal`, `Core/Updates/*`. Tests: `tests/Monolith.Tests/Security/*` (≈ 150 cases). Self-test stage «БЕЗОПАСНОСТЬ» (`Shell/SelfTest.cs`).
Diagnostics: at start the log gets the exe SHA-256, signature state, writable-folder warning, DLL-hardening result and injecting environment variables.

## 5. Update channel requirements (for `Core/Updates`; items 1–8 are implemented, 9–12 are open)

1. HTTPS only, default port, no credentials, **host allow-list checked before every redirect hop** (done), asset path pinned to
   `/bil-xvii/MONOLITH-releases/releases/download/` (done), size window (done).
2. Hash from the release is **integrity only** — never treat it as authenticity (documented in code and log).
3. **Owner signature**: `MONOLITH.exe.sig` = ECDSA P-256 over `monolith-release\nversion=…\nfile=MONOLITH.exe\nsha256=…\n`; public key pinned in
   `ReleaseSignature.PinnedPublicKey`; the downloader refuses a release without a valid signature once the key is set (done; key = open item 9).
4. **No downgrade / replay**: the version inside the file equals the release and is newer than the running build; the updater re-checks (done).
   Optional later: a signed «minimum allowed version» in the manifest to retire a vulnerable build.
5. **Verify before launch from a place nobody else controls**: the old copy re-hashes the staged file through a `FileShare.Read` handle kept open until
   the process has started (done); the elevated updater requires the hash handshake and holds its own file (done). `%LocalAppData%\MONOLITH\Updates` stays
   user-writable by design — nothing in it is trusted without the hash/handle.
6. The updater only touches a **MONOLITH program outside `%WINDIR%` whose process matches the pid** (done); exit code 13 for refusals.
7. Never overwrite the running exe (done by design), keep `.old` for rollback (done).
8. The release cache in `settings.json` is untrusted: every field is re-validated at download time (done).
9. **Owner action:** `tools\release-key.ps1 -New`, commit the printed `PinnedPublicKey` line, back up the private key offline. From then on `tools\release.ps1` signs
   every release (and refuses to release without the key). Rotating the key requires a manual re-install of old copies — keep the key safe.
10. **Owner action:** Authenticode-sign `MONOLITH.exe` (docs/signing.md). Then UAC shows the publisher and `CheckSigners` pins «same signer as the running copy»
    (today an unsigned → unsigned update is allowed because there is nothing to compare).
11. Install location: updates of a copy in Program Files go through the elevated route (UAC); that is the safe place — recommend it in the update dialog.
12. Optional: two-person rule for the GitHub account (2FA with a hardware key; protected `main`), because the source repo and the releases repo are the root of trust.

## 6. What remains for the owner

1. **Install MONOLITH into `C:\Program Files\Lycoris\`** (copy the exe there once, make a desktop shortcut). This closes S-01 completely for that copy and makes the
   auto-update use the UAC route. The Desktop\Ликорис copy keeps working but MONOLITH warns once per run.
2. **Code-sign** MONOLITH.exe (`tools\sign.ps1`, docs/signing.md). A signed exe is also what lets UAC and the integrity gate say «publisher».
3. **Generate the release key** (`tools\release-key.ps1 -New`), commit the public line, keep the private file safe (S-07).
4. Decide on **multi-file publish** (exe + 5 native DLLs, no `IncludeNativeLibrariesForSelfExtract`) to remove the `%TEMP%\.net` extraction entirely (S-02). Cost: 6 files instead of 1.
5. Keep the GitHub accounts protected (2FA with a hardware key, no tokens in the repo; the key file never leaves the PC).

## 7. Residual risks

- A process running as the same user can still start a UAC prompt that says «MONOLITH» and try to win a race against the file check (S-01/S-09); with the program in
  Program Files and signed this needs the user to approve a prompt that shows the real publisher.
- The first elevated start of a freshly unpacked single-file build trusts the unpacked natives that the *unelevated* start already loaded (S-02).
- Updates are unauthenticated beyond the hash until the release key is pinned (S-07).
- DoS-class issues (mutex squatting, forged exit-code files) are accepted.
- Hostile *winget/WMI/registry* output can still produce wrong information in the UI; none of it is executed or used as a path without validation.
