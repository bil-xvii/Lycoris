# Краш-тест запуска Lycoris на испорченных файлах данных (только отладочная сборка, данные владельца не трогаются).
# Каждый сценарий: временная папка (LYCORIS_TEST_ROOT), в неё кладётся один испорченный файл, затем Lycoris открывает
# страницы, которые этот файл читают (--snapshot). Провал = процесс упал/завис, код выхода не 0 или снимков нет.
#   powershell -ExecutionPolicy Bypass -File tools\crash-test.ps1 [-Exe <Debug Lycoris.exe>]
param([string]$Exe = (Join-Path $PSScriptRoot '..\src\Lycoris\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Lycoris.exe'))
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$targets = [ordered]@{
  'user\settings.json'                         = 'home,settings'
  'machine\journal.json'                       = 'home,restore'
  'user\measurements.json'                     = 'home'
  'user\optimize-custom.json'                  = 'optimize'
  'user\Cache\games.json'                      = 'games'
  'machine\ecoqos-session.json'                = 'games'
  'user\Cache\apps-installed.json'             = 'apps'
  'machine\pending-restorepoint-frequency.json' = 'restore'
}
$rnd = New-Object Random 20261103
$garbage = New-Object byte[] 4096; $rnd.NextBytes($garbage)
$big = '[' + ((1..60000 | % { '{"x":"' + ('ж' * 20) + '"}' }) -join ',') + ']'
$cases = [ordered]@{
  'пустой'          = [byte[]]@()
  'обрезанный'      = [Text.Encoding]::UTF8.GetBytes('{"a": [1, 2')
  'null'            = [Text.Encoding]::UTF8.GetBytes('null')
  'не тот тип'      = [Text.Encoding]::UTF8.GetBytes('[{"Id": 5, "Conditions": "x", "Language": [1], "Entries": 7}]')
  'двоичный мусор'  = $garbage
  'глубина 5000'    = [Text.Encoding]::UTF8.GetBytes(('[' * 5000) + (']' * 5000))
  'огромный 3 МБ'   = [Text.Encoding]::UTF8.GetBytes($big)
}
$fail = 0; $n = 0
foreach ($t in $targets.Keys) {
  foreach ($c in $cases.Keys) {
    $n++
    $root = Join-Path $env:TEMP ("lycoris-crash-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $file = Join-Path $root $t
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    [IO.File]::WriteAllBytes($file, $cases[$c])
    $shots = Join-Path $root 'shots'
    $psi = New-Object Diagnostics.ProcessStartInfo $Exe
    $psi.Arguments = "--snapshot `"$shots`" --pages $($targets[$t]) --wait 1500 --no-elevate"
    $psi.UseShellExecute = $false; $psi.EnvironmentVariables['LYCORIS_TEST_ROOT'] = $root
    $sw = [Diagnostics.Stopwatch]::StartNew(); $p = [Diagnostics.Process]::Start($psi)
    $ok = $p.WaitForExit(120000)
    if (-not $ok) { try { $p.Kill() } catch {} }
    $pngs = @(Get-ChildItem $shots -Recurse -Filter *.png -EA SilentlyContinue).Count
    $verdict = if (-not $ok) { 'ЗАВИС' } elseif ($p.ExitCode -ne 0) { "КОД $($p.ExitCode)" } elseif ($pngs -eq 0) { 'НЕТ СНИМКОВ' } else { 'ок' }
    if ($verdict -ne 'ок') { $fail++ }
    '{0,3}. {1,-45} {2,-15} {3,-12} {4,5:N1} с' -f $n, $t, $c, $verdict, $sw.Elapsed.TotalSeconds
    if ($verdict -eq 'ок') { Remove-Item $root -Recurse -Force -EA SilentlyContinue } else { "     папка для разбора: $root" }
  }
}
"ИТОГ: сценариев $n, провалов $fail"
exit [int]($fail -gt 0)
