<#
.SYNOPSIS
  Localization progress and helper (docs/localization.md). Wrapper around tools/LocScan.

.DESCRIPTION
  .\tools\loc-scan.ps1                          per-module report: Cyrillic string literals that still need Loc
  .\tools\loc-scan.ps1 -Check Optimize          violations of one area (-Check --all = every area marked "translated")
  .\tools\loc-scan.ps1 -Missing Optimize        numbered Russian keys of the area that have no English entry yet
  .\tools\loc-scan.ps1 -Fill Optimize -File fill.json
                                                merge {"1": "English", "2": "..."} (numbers from the last -Missing) into the area file
  .\tools\loc-scan.ps1 -New Optimize -Sources "Modules/Optimize/**,Tweaks/*"
                                                create Core/Localization/en/Optimize.json

  Reads sources only; never touches the system. Exit code of -Check = number of problems.
#>
param(
    [string]$Check,
    [string]$Missing,
    [string]$Fill,
    [string]$New,
    [string]$File,
    [string]$Sources,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $project = Join-Path $root 'tools/LocScan/LocScan.csproj'
    $run = @('run', '--project', $project, '-c', 'Release', '--nologo', '-v', 'q', '--')
    if ($Help) { & dotnet @run help }
    elseif ($Check) { & dotnet @run check $Check; exit $LASTEXITCODE }
    elseif ($Missing) { & dotnet @run missing $Missing; exit $LASTEXITCODE }
    elseif ($Fill) { & dotnet @run fill $Fill $File; exit $LASTEXITCODE }
    elseif ($New) { & dotnet @run new $New $Sources; exit $LASTEXITCODE }
    else { & dotnet @run report }
}
finally {
    Pop-Location
}
