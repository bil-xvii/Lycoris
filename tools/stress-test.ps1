# Стресс-краш-тест Lycoris (отладочная сборка, временная папка данных через LYCORIS_TEST_ROOT — данные владельца не трогаются):
#  1) 10 запусков подряд (снимок «Панели» и «Главной») — ни одного падения, время и память без роста;
#  2) два экземпляра: второй должен передать управление первому и выйти, первый — жить дальше;
#  3) жёсткое завершение первого во время загрузки, затем новый запуск — чистый старт без ошибок.
param([string]$Exe = (Join-Path $PSScriptRoot '..\src\Lycoris\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Lycoris.exe'))
$ErrorActionPreference = 'Stop'; $Exe = (Resolve-Path $Exe).Path
$root = Join-Path $env:TEMP ("lycoris-stress-" + [guid]::NewGuid().ToString('N').Substring(0, 6)); New-Item -ItemType Directory -Force $root | Out-Null
function Start-L([string]$a){ $psi = New-Object Diagnostics.ProcessStartInfo $Exe; $psi.Arguments = $a; $psi.UseShellExecute = $false; $psi.EnvironmentVariables['LYCORIS_TEST_ROOT'] = $root; [Diagnostics.Process]::Start($psi) }
$fail = 0
"== 1) 10 запусков подряд"
foreach ($i in 1..10) {
  $sw = [Diagnostics.Stopwatch]::StartNew(); $p = Start-L "--snapshot `"$root\s$i`" --pages dashboard,home --wait 1500 --no-elevate"
  $peak = 0; while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 120) { try { $p.Refresh(); $peak = [Math]::Max($peak, $p.WorkingSet64) } catch {}; Start-Sleep -Milliseconds 200 }
  if (-not $p.HasExited) { $p.Kill(); $v = 'ЗАВИС' } elseif ($p.ExitCode -ne 0) { $v = "КОД $($p.ExitCode)" } else { $v = 'ок' }
  if ($v -ne 'ок') { $fail++ }
  '{0,2}. {1,-8} {2,5:N1} с, пик памяти {3:N0} МБ' -f $i, $v, $sw.Elapsed.TotalSeconds, ($peak / 1MB)
}
"== 2) два экземпляра"
$a = Start-L "--no-elevate"; Start-Sleep 8
$b = Start-L "--no-elevate"; $bExit = $b.WaitForExit(30000)
Start-Sleep 2
$v2 = if (-not $bExit) { $b.Kill(); 'второй не вышел (ПРОВАЛ)' } elseif ($a.HasExited) { 'первый закрылся (ПРОВАЛ)' } else { "второй вышел (код $($b.ExitCode)), первый работает — ок" }
if ($v2 -match 'ПРОВАЛ') { $fail++ }; $v2
"== 3) жёсткое завершение во время работы и повторный запуск"
if (-not $a.HasExited) { $a.Kill(); $a.WaitForExit(10000) | Out-Null }
$c = Start-L "--snapshot `"$root\after-kill`" --pages dashboard,home,restore --wait 1500 --no-elevate"
$cOk = $c.WaitForExit(120000)
$v3 = if (-not $cOk) { $c.Kill(); 'ЗАВИС' } elseif ($c.ExitCode -ne 0) { "КОД $($c.ExitCode)" } else { 'чистый старт — ок' }
if ($v3 -notmatch 'ок') { $fail++ }; $v3
$errs = @(Get-ChildItem "$root\user\Logs" -Filter *.log -EA SilentlyContinue | Select-String '\[ERROR\]|Необработанная ошибка' ).Count
"ошибок в журнале программы: $errs"; if ($errs) { $fail++; Get-ChildItem "$root\user\Logs" -Filter *.log | Select-String '\[ERROR\]|Необработанная ошибка' | Select-Object -First 5 | % { '   ' + $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) } }
"ИТОГ: провалов $fail"; if (-not $fail) { Remove-Item $root -Recurse -Force -EA SilentlyContinue } else { "папка для разбора: $root" }
exit [int]($fail -gt 0)
