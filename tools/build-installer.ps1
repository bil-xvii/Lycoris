param([string]$PublishDirectory = (Join-Path $PSScriptRoot '..\publish'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $root 'src\Lycoris\Lycoris.csproj')
$version = $project.Project.PropertyGroup.Version | Select-Object -First 1
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).ProviderPath
if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) { throw "Не найдена папка публикации $publishPath." }
$exe = Join-Path $publishPath 'Lycoris.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Не найден $exe. Сначала выполните publish." }
$info = (Get-Item -LiteralPath $exe).VersionInfo
if ($info.ProductVersion -ne $version -or $info.FileVersion -ne "$version.0") {
  throw "Версия $exe не совпадает с проектом $version (ProductVersion=$($info.ProductVersion), FileVersion=$($info.FileVersion)). Сначала выполните publish текущей версии."
}
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  $candidate = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
  if (Test-Path $candidate) { $iscc = $candidate }
}
if (-not $iscc) { throw 'Inno Setup 6 (iscc.exe) не найден. Установите его самостоятельно (winget id: JRSoftware.InnoSetup), затем повторите.' }
$script = Join-Path $root 'packaging\installer\lycoris.iss'
& $iscc "/DAppVersion=$version" "/DPublishDirectory=$publishPath" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }
$output = Join-Path $publishPath "Lycoris-Setup-$version.exe"
(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $output -Leaf) | Set-Content -LiteralPath "$output.sha256" -Encoding ascii
Write-Host "Создано: $output"
