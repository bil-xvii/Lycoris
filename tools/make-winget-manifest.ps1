param([string]$PublishDirectory = (Join-Path $PSScriptRoot '..\publish'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $root 'src\Lycoris\Lycoris.csproj')
$version = $project.Project.PropertyGroup.Version | Select-Object -First 1
$hashFile = Join-Path $PublishDirectory 'Lycoris.exe.sha256'
if (-not (Test-Path $hashFile)) { throw "Не найден $hashFile. Сначала опубликуйте Lycoris.exe и Lycoris.exe.sha256." }
$line = (Get-Content $hashFile -Raw).Trim()
$hash = ($line -split '\s+')[0].ToUpperInvariant()
if ($hash -notmatch '^[0-9A-F]{64}$') { throw "Некорректный SHA-256 в $hashFile" }
$dir = Join-Path $root 'packaging\winget'
$outputDir = Join-Path $dir "generated\$version"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
(Get-Content (Join-Path $dir 'bil-xvii.Lycoris.yaml.in') -Raw).Replace('__VERSION__',$version) | Set-Content (Join-Path $outputDir "bil-xvii.Lycoris.yaml") -Encoding utf8
(Get-Content (Join-Path $dir 'bil-xvii.Lycoris.locale.yaml.in') -Raw).Replace('__VERSION__',$version) | Set-Content (Join-Path $outputDir "bil-xvii.Lycoris.locale.en-US.yaml") -Encoding utf8
(Get-Content (Join-Path $dir 'bil-xvii.Lycoris.installer.yaml.in') -Raw).Replace('__VERSION__',$version).Replace('__SHA256__',$hash) | Set-Content (Join-Path $outputDir "bil-xvii.Lycoris.installer.yaml") -Encoding utf8
Write-Host "Созданы winget-манифесты версии $version в $outputDir"
