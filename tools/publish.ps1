# Builds the release: one self-contained MONOLITH.exe (no .NET install needed) into .\publish
# and signs it when a signing certificate is available (tools\sign.ps1; set one up with tools\signing-cert.ps1).
# Usage:  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Shortcut] [-NoSign]
param([switch]$Shortcut, [switch]$NoSign)

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
# One source of truth for the version: <Version> in Monolith.csproj (the same number the app shows and the update check compares).
$csprojText = Get-Content (Join-Path $root 'src\Monolith\Monolith.csproj') -Raw
$version = if ($csprojText -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '?' }
$info = (Get-Item $exe).VersionInfo
"Version: $version  (exe file version $($info.FileVersion), product $($info.ProductVersion), company '$($info.CompanyName)')"

if (-not $NoSign) {
    # Exit codes of sign.ps1: 0 signed and valid, 2 no certificate (left unsigned), 1 error.
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'sign.ps1') -File $exe
    switch ($LASTEXITCODE) {
        0 { 'Signed.' }
        2 { 'Not signed: no signing certificate on this PC (UAC will show «Издатель: неизвестно»). See docs\signing.md.' }
        default { Write-Warning "Signing failed (code $LASTEXITCODE): the exe is built but not trusted as signed." }
    }
}

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
