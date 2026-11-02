# Publishes a Lycoris release so that every installed copy shows "UPDATE x.y.z" (docs/releasing.md).
#
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -WhatIf     # dry run: checks + the exact plan, changes nothing
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1             # the real release
#
# What it does, in order (and refuses to continue when a check fails):
#   1. reads the version from <Version> in src\Monolith\Monolith.csproj (the single source of truth);
#   2. the working tree must be clean and HEAD must already be pushed to origin;
#   3. the version must be GREATER than the latest release of bil-xvii/Lycoris-releases (and the tag must not exist);
#   4. RELEASE_NOTES.md must have a "## x.y.z" section (it becomes the release notes the app shows);
#   5. dotnet test must pass;
#   6. tools\publish.ps1 builds (and signs, when a certificate exists) publish\Lycoris.exe; --selftest on it must exit 0;
#   7. writes publish\Lycoris.exe.sha256 ("<hash>  Lycoris.exe");
#   8. gh release create vX.Y.Z in bil-xvii/Lycoris-releases (public: this is where the app looks) and in bil-xvii/Lycoris
#      (private source repo, tag on this commit), both with Lycoris.exe + Lycoris.exe.sha256 and the notes.
# With -WhatIf only steps 1-4 run (read-only) and the remaining steps are printed instead of executed.
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$SkipTests,        # only together with -WhatIf (a real release always runs the tests)
    [switch]$NoSign
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$PublicRepo = 'bil-xvii/Lycoris-releases'
$SourceRepo = 'bil-xvii/Lycoris'
$dry = [bool]$WhatIfPreference

function Fail($msg) { Write-Host "REFUSED: $msg" -ForegroundColor Red; exit 1 }
# In a dry run a failed check is reported and the plan is still shown; a real run stops at once.
$script:problems = @()
function Problem($msg) { if ($dry) { $script:problems += $msg; Write-Host "WOULD REFUSE: $msg" -ForegroundColor Yellow } else { Fail $msg } }
function Step($msg) { Write-Host "== $msg" -ForegroundColor Cyan }
function Parse-Version($text) {
    if ($text -match '^v?(\d+)\.(\d+)\.(\d+)$') { return [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" }
    return $null
}

# 1. version
$csproj = Get-Content (Join-Path $root 'src\Monolith\Monolith.csproj') -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { Fail 'no <Version> in Monolith.csproj' }
$versionText = $Matches[1].Trim()
$version = Parse-Version $versionText
if (-not $version) { Fail "version '$versionText' is not x.y.z (pre-release labels are never published as updates)" }
$tag = "v$versionText"
Step "Version $versionText (tag $tag)"

# 2. clean tree, HEAD pushed
$dirty = git status --porcelain
if ($dirty) { Problem "the working tree is not clean:`n$($dirty -join "`n")" }
$head = (git rev-parse HEAD).Trim()
$onOrigin = git branch -r --contains $head 2>$null
if (-not $onOrigin) { Problem "HEAD $($head.Substring(0,8)) is not on origin yet - push first (the release tag must point at a pushed commit)" }
if ($onOrigin -and -not $dirty) { Write-Host "tree clean, HEAD $($head.Substring(0,8)) is on origin" }

# 3. greater than the latest published release
$gh = Get-Command gh -ErrorAction SilentlyContinue
$latest = $null
if (-not $gh) {
    if ($dry) { Write-Warning 'gh (GitHub CLI) not found: cannot compare with the latest release in this dry run' }
    else { Fail 'gh (GitHub CLI) is required: https://cli.github.com/' }
}
else {
    $out = & gh api "repos/$PublicRepo/releases/latest" --jq .tag_name 2>&1
    if ($LASTEXITCODE -eq 0) { $latest = Parse-Version ($out | Select-Object -First 1) }
    elseif (($out | Out-String) -match 'Not Found|404') { Write-Host 'no release published yet' }
    elseif ($dry) { Write-Warning "could not ask GitHub for the latest release ($($out | Out-String)) - continuing the dry run" }
    else { Fail "could not ask GitHub for the latest release: $($out | Out-String)" }
}
if ($latest) {
    if ($version -le $latest) { Problem "version $versionText is not greater than the latest release v$latest - raise <Version> in Monolith.csproj" }
    elseif ($version -gt $latest) { Write-Host "latest release is v$latest -> $versionText is newer" }
}
$existing = git tag --list $tag
if ($existing) { Problem "tag $tag already exists locally" }

# 4. notes
$notesFile = Join-Path $root 'RELEASE_NOTES.md'
$notes = ''
if (-not (Test-Path $notesFile)) { Problem 'RELEASE_NOTES.md is missing (add a "## x.y.z" section)' }
else {
    $lines = @(Get-Content $notesFile -Encoding UTF8)
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "^##\s+v?$([regex]::Escape($versionText))\b") { $start = $i; break } }
    if ($start -lt 0) { Problem "RELEASE_NOTES.md has no '## $versionText' section" }
    else {
        $end = $lines.Count
        for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^##\s') { $end = $i; break } }
        if ($end -gt $start + 1) { $notes = ($lines[($start + 1)..($end - 1)] -join "`n").Trim() }
        if (-not $notes) { Problem "the '## $versionText' section of RELEASE_NOTES.md is empty" }
    }
}
Step 'Release notes (what the app shows)'
Write-Host $notes

$exe = Join-Path $root 'publish\Lycoris.exe'
$sha = Join-Path $root 'publish\Lycoris.exe.sha256'
$notesTmp = Join-Path ([IO.Path]::GetTempPath()) "monolith-notes-$versionText.md"

if ($dry) {
    Step 'Dry run: the rest would be'
    if (-not $SkipTests) { Write-Host "  dotnet test tests\Monolith.Tests\Monolith.Tests.csproj   (must pass)" }
    Write-Host "  tools\publish.ps1 $(if ($NoSign) { '-NoSign' })   -> $exe"
    Write-Host "  $exe --selftest <temp>   (exit code must be 0)"
    Write-Host "  write $sha   ('<sha256>  Lycoris.exe')"
    Write-Host "  gh release create $tag --repo $PublicRepo --title 'Lycoris $versionText' --notes-file <notes> Lycoris.exe Lycoris.exe.sha256"
    Write-Host "  gh release create $tag --repo $SourceRepo --target $($head.Substring(0,8)) --title 'Lycoris $versionText' --notes-file <notes> Lycoris.exe Lycoris.exe.sha256"
    Write-Host 'WhatIf: nothing was built, tagged or published.' -ForegroundColor Green
    if ($script:problems.Count -gt 0) { Write-Host "$($script:problems.Count) check(s) would refuse a real release (see WOULD REFUSE above)." -ForegroundColor Yellow; exit 1 }
    exit 0
}

# 5. tests
Step 'Unit tests'
dotnet test (Join-Path $root 'tests\Monolith.Tests\Monolith.Tests.csproj') --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'unit tests failed' }

# 6. build + selftest
Step 'Build'
$pubArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'publish.ps1'))
if ($NoSign) { $pubArgs += '-NoSign' }
& powershell @pubArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { Fail 'publish failed' }
Step 'Self-test of the published exe'
$st = Join-Path ([IO.Path]::GetTempPath()) "monolith-release-selftest-$versionText"
$p = Start-Process -FilePath $exe -ArgumentList '--selftest', "`"$st`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "--selftest of the published exe exited with $($p.ExitCode)" }

# 7. checksum
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($sha, "$hash  Lycoris.exe`n", (New-Object Text.UTF8Encoding $false))
Write-Host "SHA-256 $hash"

# 8. releases
[IO.File]::WriteAllText($notesTmp, $notes + "`n", (New-Object Text.UTF8Encoding $false))
foreach ($repo in @($PublicRepo, $SourceRepo)) {
    Step "gh release create $tag in $repo"
    $ghArgs = @('release', 'create', $tag, $exe, $sha, '--repo', $repo, '--title', "Lycoris $versionText", '--notes-file', $notesTmp)
    if ($repo -eq $SourceRepo) { $ghArgs += @('--target', $head) }
    if ($PSCmdlet.ShouldProcess($repo, "gh release create $tag")) {
        & gh @ghArgs
        if ($LASTEXITCODE -ne 0) { Fail "gh release create failed for $repo (the other repository may already have the release - check before retrying)" }
    }
}
git fetch --tags --quiet
Write-Host "Published $tag. Installed copies show 'UPDATE $versionText' within 12 hours (or at once on 'CHECK FOR UPDATES')." -ForegroundColor Green
