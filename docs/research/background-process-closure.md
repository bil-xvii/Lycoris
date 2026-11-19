# Closing selected background applications

Decision, 2026-11-19. The existing close-list editor can only act through Game Boost. Add a standalone close action and an explicit CLI so an authorized caller can use Lycoris without automating its elevated window. This does not change Windows permissions: the installed 1.0.32 process was independently observed with a Full, High-integrity administrator token.

## Implementation contract

- Reuse one close executor for the editor, CLI and Game Boost's Close mode. Do not change EcoQoS or introduce another optimizer.
- Preview specific instances: name, PID, creation time and image path. Bind approval to the complete instance set, caller SID/session, policy and force choice. A fingerprint identifies that state; it is not an authorization credential.
- Read identity and protection through native process handles. Unknown identity, a different owner/session, Windows/security/driver/launcher components and protected developer hosts are refused. Never terminate a process tree or a replacement instance.
- Request normal window closure first. Force requires a separate affirmative GUI decision or explicit CLI force option. Verify the original process exit; report denied, stale and still-running targets truthfully.
- Standalone closure changes no power scheme, priority, standby cache, service, startup entry or saved close list. It has no system-setting undo. Do not load, recover or save the machine journal from its CLI.
- The CLI accepts bounded allow-listed arguments, defaults to preview and requires administrator rights, explicit approval and the exact current fingerprint for execution. Reject mixed modes before other startup handlers. It may run while the main GUI remains open.
- Use the existing operation coordinator. A separate cross-process gate is justified only if its lifetime is safe across asynchronous confirmation; exact-instance actions must remain safe when a different program exits or closes a target concurrently.

## Acceptance evidence required

1. Debug and Release builds have zero warnings/errors; focused regression tests and the full final test run pass.
2. Tests prove preview/no-confirmation cannot mutate, stale plans and PID reuse cannot widen selection, protected or unreadable identities are refused, and partial failures are not success.
3. Actual RU/EN UI and the packaged CLI show the new action. A CLI preview works beside the running owner GUI without changing settings or journal.
4. Before install/publication, an independent release gate reviews the final diff and actual validation.
5. On this PC, only explicitly selected optional Copilot processes may be used for the operational reduction check. Preserve Windows, gaming/peripheral infrastructure, active AI/development tools, browser and Vealo. Record matched before/after observations and any respawn; do not infer FPS or net RAM gains from process count or summed working sets.

Platform references: [Process termination semantics](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0), [window process identity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid), [window-message integrity boundary](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew).
