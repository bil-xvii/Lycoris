# Builds the release: one self-contained MONOLITH.exe (no .NET install needed) into .\publish
# Usage:  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Shortcut]
param([switch]$Shortcut)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$out = Join-Path $root 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'src\Monolith\Monolith.csproj') -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $out 'MONOLITH.exe'
Get-ChildItem $out | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force
"Built: $exe  ({0:N1} MB)" -f ((Get-Item $exe).Length / 1MB)

if ($Shortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desktop 'MONOLITH.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $out
    $s.IconLocation = "$exe,0"
    $s.Description = 'MONOLITH - безопасная оптимизация Windows'
    $s.Save()
    "Shortcut: $lnk"
}
