# Local assistant and Windows application removal, 2026-11-19

## Decision and scope

Extend the installed 1.0.30 source (`3a98c02`) on `codex/local-ai-cleanup`. Reuse the existing Ollama chat, service command workflow, removal preview, operation coordinator and durable removal audit. No new dependencies or downloads are required.

* Show chat immediately, retain deterministic diagnostics and provide trusted navigation to application, service, startup and file reviews.
* Accept only freshly verified installed local chat models; force Ollama's explicit local source over loopback, bound input/output/time and pause new inference during a recognized game. Model text cannot dispatch system changes.
* Add explicitly selected current-user Store application removal. Revalidate desktop identity, native and Appx registrations, removability and the dependency graph before each dispatch. Protect management/system/framework/optional packages and infrastructure. Dispatch only a principal app, never a resource or bundle directly; Windows can remove its verified same-family, same-version associated resources/bundle normally. Ambiguous families, including differently versioned bundles, remain excluded; the Windows Settings route remains available.
* Reuse reversible service disable/start/stop and existing Win32 uninstall; do not introduce a second service writer or assume all Microsoft products are unnecessary.
* Uninstall can remove application data and has no automatic undo. Native in-use removal is deferred where Windows confirms support. Pending or unknown results remain durable attempts and are not retried automatically.

Installed binary and exact source backup: `C:\Users\Lycoris\agent-tools\backups\lycoris-local-ai-cleanup-20261119-1314`. Rollback: close Lycoris when no operation is active, restore `Lycoris.before.exe` as `C:\Users\Lycoris\Desktop\Ярлыки\Lycoris\Lycoris.exe`, then reopen. No real application or service removal is part of validation.

## Evidence record

| Acceptance check | Status | Evidence |
| --- | --- | --- |
| Exact installed baseline | passed | Installed and source-published executable SHA256 `19A58EC23D65283B29EE9E6620C02546EFD872DA0CBCA95B9D9CDBB648A956A9`, version 1.0.30, source `3a98c02`. |
| Local model routing, streaming, selection and bounds | passed, isolated | Exact four owned source/test files over baseline: `lyc-check.ps1 'FullyQualifiedName~LocalModelClientTests|FullyQualifiedName~AssistantTests'`, 43/43, zero warnings. |
| Assistant maintenance, game pause, service/diagnostic regressions and localization | passed, isolated | Exact UI/core snapshot over baseline: 93/93 targeted tests. Shared-tree validation still required. |
| Real Ollama metadata and explicit local source | passed, read-only | 29 tags; remote tags have `remote_model`/`remote_host`; `/api/show` accepted `qwen3:8b:local`, rejected cloud-tag local source. No inference or download. |
| Actual assistant request in WPF window | passed | Final Release candidate, default `qwen2.5:3b-instruct`: typed the optional Store-app question, pressed Enter, received a completed streamed Russian answer describing optional current-user Store removal and the trusted removal link. The earlier overcautious response was corrected with explicit capability grounding. The QA copy closed normally. Game pause remains covered by fake tests, not this runtime check. |
| Assistant layout RU/EN | passed | `--snapshot` RU 1440×900 and EN 1120×720; chat open, real local model detected, input and controls visible. Independently inspected by the assistant reviewer. |
| Real current-user package graph | passed, read-only probe | 240 native and Appx registrations; zero identity differences; 147 dependency edges in both sources, zero graph differences. |
| Shared build and complete tests | passed | `dotnet build -c Debug -v minimal`: zero warnings/errors. `dotnet test --no-build --logger trx`: 5,254 passed, zero failed, 16 hardware/native tests skipped (5,270 total), 31 seconds. Logs `final-build.log`, `full-tests.log`, `full-tests.trx` in the backup/evidence directory. |
| Actual production Store preview | passed, read-only | Final compiled `ReadForUninstallAsync` and `PrepareAsync`, real desktop SID/native/Appx APIs, in-memory journal, throwing dispatch: 240/240 complete registrations and 13 eligible visible applications. Photos, Weather, Paint, Notepad, Feedback Hub and Clipchamp are included. No removal dispatched. `msix-production-readonly.log`. |
| RU and EN selftest | passed | Final Debug `Lycoris.exe --selftest <dir> --lang ru` and `--lang en`: both exit 0, all 20 stages OK, zero errors/binding errors/crashes; 276 s RU and 280 s EN. Reports in `selftest-ru` and `selftest-en`. Existing minor ellipsis warnings are outside the changed assistant/removal pages. |
| Final Release preview UI | passed, read-only | Candidate `Lycoris.exe --startup-timing <log> --page removal --no-elevate` (existing QA extra-copy mode). Clicked Store preview: 13 rows, selection 0/13, delete disabled, no select-all; data-loss/no-undo/deferred-removal text visible. No checkboxes selected or removal dispatched. |
| Release build | passed | `dotnet publish src/Lycoris/Lycoris.csproj -c Release -r win-x64 --self-contained true -o <evidence>/release-candidate`, exit 0; version 1.0.31, 94,246,342 bytes, SHA256 `1F6FDACB596FA9CD1EEF42638482AC98DFEC9029909C54796D3934004D975F3F`. Signing script exit 2: no signing certificate, so the local build remains unsigned; no certificate installed. |
| Independent final release gate | passed with limitations | `release_gate_reviewer` reviewed the actual final diff, logs, both selftests and exact Release candidate; no confirmed blocking defects. Native uninstall/service writes intentionally not run, game pause proven with fakes, local binary unsigned. |
| Published binary installed and opened | passed | User subsequently closed 1.0.30 and authorized installation. Confirmed no Lycoris processes, candidate and backup hashes unchanged. Staged beside the destination and atomically replaced the executable, preserving an additional exact old binary. Installed SHA256 matches the reviewed candidate. Opened the installed path normally (PID 12308); observed version 1.0.31, Assistant open, local model ready and zero active tasks in window 10423328. Receipt: `<evidence>/deployment-receipt.json`. |

## Handoff

Implementation, required checks and local deployment are complete. The installed destination now contains the exact reviewed 1.0.31 candidate and was opened successfully. The original `Lycoris.before.exe` and the additional atomic-replacement backup both retain the exact previous 1.0.30 hash. To roll back, close Lycoris and restore the backup to the installed path. No real cleanup has been performed on the owner's Windows installation by this task.

## Primary platform references

* [Ollama Chat API](https://docs.ollama.com/api/chat), [local/cloud model-reference implementation](https://github.com/ollama/ollama/blob/main/internal/modelref/modelref.go).
* [MSIX package lifetime](https://learn.microsoft.com/en-us/windows/msix/package/package-lifetime), [RemovalOptions](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.removaloptions), [inbox Store application removability](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/modern-inbox-store-apps-troubleshooting-guidance).
* [Microsoft WindowsAppSDK removal implementation](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/PackageManager/API/M.W.M.D.PackageDeploymentManager.cpp): `RemovePackageByFamilyName` documents automatic associated resource/bundle cleanup and dispatches principal package full names.
