# QA: start a published Lycoris.exe with --startup-timing (read-only extra copy, ignores the instance lock), print the
# startup milestones, then sample idle CPU / memory for -IdleSec seconds on the Dashboard and end ONLY that process.
# Usage: powershell -File tools\perf-measure.ps1 -Exe <path\Lycoris.exe> [-IdleSec 60] [-Minimize]
param([Parameter(Mandatory)][string]$Exe, [int]$IdleSec = 60, [switch]$Minimize)
$t = Join-Path ([IO.Path]::GetTempPath()) ("lycoris-timing-" + [guid]::NewGuid().ToString('N') + '.txt')
$cores = [Environment]::ProcessorCount
$p = Start-Process $Exe -ArgumentList @('--startup-timing', $t, '--lang', 'ru') -PassThru
Start-Sleep 12
$p.Refresh()
$res = [ordered]@{}
if (Test-Path $t) { $res.timing = (Get-Content $t) -join '; ' }
if ($Minimize -and -not $p.HasExited) {
  Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);'
  [void][W.U]::ShowWindow($p.MainWindowHandle, 6)
  Start-Sleep 3
}
if (-not $p.HasExited) {
  $p.Refresh()
  $c0 = $p.TotalProcessorTime.TotalMilliseconds
  Start-Sleep $IdleSec
  $p.Refresh()
  $c1 = $p.TotalProcessorTime.TotalMilliseconds
  $res.idleCpuMsPerSec = [math]::Round(($c1 - $c0) / $IdleSec, 1)
  $res.idleCpuPctOfAllCores = [math]::Round(($c1 - $c0) / ($IdleSec * 1000 * $cores) * 100, 3)
  $res.wsMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
  $res.privMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
  $res.threads = $p.Threads.Count
  $res.handles = $p.HandleCount
  Stop-Process -Id $p.Id -Force
}
Remove-Item $t -ErrorAction SilentlyContinue
$res | ConvertTo-Json -Compress
