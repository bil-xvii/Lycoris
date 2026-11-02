param([string]$PublishDirectory = (Join-Path $PSScriptRoot '..\publish'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $root 'src\Lycoris\Lycoris.csproj')
$version = $project.Project.PropertyGroup.Version | Select-Object -First 1
$exe = Join-Path $PublishDirectory 'Lycoris.exe'
if (-not (Test-Path $exe)) { throw "Не найден $exe. Сначала выполните publish." }
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  $candidate = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
  if (Test-Path $candidate) { $iscc = $candidate }
}
if (-not $iscc) { Write-Host 'Inno Setup 6 (iscc.exe) не найден. Установите его самостоятельно (winget id: JRSoftware.InnoSetup), затем повторите.'; exit 0 }
$script = Join-Path $root 'packaging\installer\lycoris.iss'
& $iscc "/DAppVersion=$version" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }
$output = Join-Path $PublishDirectory "Lycoris-Setup-$version.exe"
(Get-FileHash $output -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $output -Leaf) | Set-Content "$output.sha256" -Encoding ascii
Write-Host "Создано: $output"
