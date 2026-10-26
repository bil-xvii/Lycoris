# MONOLITH autosave: commits every change in the repository and pushes it to GitHub.
# Called automatically by the Claude Code Stop hook (.claude/settings.json) at the end of every turn.
# Safe to run by hand at any time. Never fails loudly: all output goes to .git/autosave.log.

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$gitDir = Join-Path $repo '.git'
if (-not (Test-Path $gitDir)) { exit 0 }

$logFile = Join-Path $gitDir 'autosave.log'
function Write-AutosaveLog([string]$msg) {
    try { Add-Content -Path $logFile -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $msg) -Encoding UTF8 } catch {}
}

# One run at a time (a stale lock older than 10 minutes is ignored)
$lock = Join-Path $gitDir 'autosave.lock'
if ((Test-Path $lock) -and ((Get-Item $lock).LastWriteTime -gt (Get-Date).AddMinutes(-10))) { exit 0 }
try { Set-Content -Path $lock -Value $PID -ErrorAction Stop } catch { exit 0 }

try {
    $git = (Get-Command git -ErrorAction SilentlyContinue).Source
    if (-not $git) { $git = 'C:\Program Files\Git\cmd\git.exe' }
    if (-not (Test-Path $git)) { Write-AutosaveLog 'git not found'; exit 0 }

    & $git -C $repo add -A 2>&1 | Out-Null
    & $git -C $repo diff --cached --quiet
    if ($LASTEXITCODE -ne 0) {
        $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
        $out = & $git -C $repo commit -q -m "autosave: $stamp" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" 2>&1
        Write-AutosaveLog "commit: $($out -join ' ')"
    }

    $remote = & $git -C $repo remote 2>$null
    if ($remote -contains 'origin') {
        $out = & $git -C $repo push -q origin HEAD 2>&1
        Write-AutosaveLog "push exit=$LASTEXITCODE $($out -join ' ')"
    }
}
catch {
    Write-AutosaveLog "error: $_"
}
finally {
    Remove-Item $lock -Force -ErrorAction SilentlyContinue
}
exit 0
