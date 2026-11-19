#requires -Version 7.4
<#
.SYNOPSIS
Validates a pinned Lycoris installer on a disposable GitHub-hosted Windows runner.
.DESCRIPTION
Creates a fresh local standard account, profile, private desktop and disposable machine journal
fixture. The Windows runner tears down the fixture directories and profile at job completion.
.EXAMPLE
./tools/validation/installer-integration.ps1 -Mode Candidate -Version $version -SetupPath $setup -SetupSha256 $setupHash -PayloadSha256 $payloadHash
.OUTPUTS
The worker result and bounded installer logs under RUNNER_TEMP\lycoris-installer-evidence.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('Baseline', 'Candidate')][string]$Mode,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+\z')][string]$Version,
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9A-Fa-f]{64}\z')][string]$SetupSha256,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9A-Fa-f]{64}\z')][string]$PayloadSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not [Environment]::Is64BitProcess -or $env:GITHUB_ACTIONS -cne 'true' -or
    $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:GITHUB_RUN_ID -notmatch '\A[0-9]+\z' -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'A disposable x64 GitHub-hosted Windows runner is required.'
}

function Assert-DirectPath {
    param([string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\')) { throw 'Expected a local absolute path.' }
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected reparse point in fixture path: $cursor"
        }
    }
}

function New-TestDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string]$Path,
        [Security.Principal.SecurityIdentifier]$UserSid,
        [Security.AccessControl.FileSystemRights]$UserRights
    )
    Assert-DirectPath -Path $Path
    if (Test-Path -LiteralPath $Path) { throw "Fixture directory already exists: $Path" }
    if (-not $PSCmdlet.ShouldProcess($Path, 'Create a disposable fixture directory and its isolated ACL')) {
        throw 'Fixture directory creation was declined.'
    }
    $null = New-Item -Path $Path -ItemType Directory
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    foreach ($sid in $admins, $system, $runnerSid) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($UserSid, $UserRights, $inherit, 'None', 'Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Copy-InstallerEvidence {
    if (-not [IO.Directory]::Exists($state)) { return }
    Assert-DirectPath -Path $state
    foreach ($file in Get-ChildItem -LiteralPath $state -File) {
        if ($file.Name -ne 'result.json' -and $file.Extension -ne '.log') { continue }
        Assert-DirectPath -Path $file.FullName
        if ($file.Length -gt 8MB) { throw 'Unexpected installer evidence size.' }
        Copy-Item -LiteralPath $file.FullName -Destination $evidence
    }
    $snapshots = Join-Path $state 'selftest'
    if (-not [IO.Directory]::Exists($snapshots)) { return }
    Assert-DirectPath -Path $snapshots
    $out = Join-Path $evidence 'selftest'
    $null = New-Item -Path $out -ItemType Directory
    foreach ($file in Get-ChildItem -LiteralPath $snapshots -File) {
        if ($file.Extension -ne '.png' -and $file.Name -notin @('report.md', 'report.json', 'missing-keys.txt')) { continue }
        Assert-DirectPath -Path $file.FullName
        if ($file.Length -gt 8MB) { throw 'Unexpected snapshot evidence size.' }
        Copy-Item -LiteralPath $file.FullName -Destination $out
    }
}

$setup = (Resolve-Path -LiteralPath $SetupPath).ProviderPath
$SetupSha256 = $SetupSha256.ToUpperInvariant()
$PayloadSha256 = $PayloadSha256.ToUpperInvariant()
if ($Mode -eq 'Baseline' -and
    ($Version -cne '1.0.27' -or $SetupSha256 -cne 'CACFA53642A47EFB42225290BD226E535DEB5BA213494EC297AEB4A5696ECCBF' -or
     $PayloadSha256 -cne '2EFE931FB33C1637AE047B1D3B89A4F7E68472D1E269F81040D81C1A36A0176F')) {
    throw 'The historical baseline requires the published 1.0.27 SHA-256 pins.'
}
if ($Mode -eq 'Candidate' -and [version]$Version -le [version]'1.0.27') { throw 'The candidate must be newer than the baseline.' }
Assert-DirectPath -Path $setup
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -cne $SetupSha256) { throw 'Installer SHA-256 mismatch.' }
if (-not $PSCmdlet.ShouldProcess('This disposable GitHub-hosted Windows runner', "Run the $Mode installer scenarios")) { return }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The hosted fixture launcher must be able to create a standard local account.'
    }
    $runnerSid = $identity.User
}
finally { $identity.Dispose() }
$runName = [guid]::NewGuid().ToString('N')
$accountName = 'lyci_' + $runName.Substring(0, 12)
$systemDrive = [IO.Path]::GetPathRoot([Environment]::SystemDirectory)
$profilePath = Join-Path $systemDrive "Users\$accountName"
$scratch = Join-Path $systemDrive "LycorisInstaller-$runName"
$state = Join-Path $scratch 'state'
$machineFixture = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Lycoris'
$evidence = Join-Path $env:RUNNER_TEMP 'lycoris-installer-evidence'
foreach ($path in $profilePath, $scratch, $machineFixture, $evidence) {
    Assert-DirectPath -Path $path
    if (Test-Path -LiteralPath $path) { throw "Fixture already exists: $path" }
}
if (Get-LocalUser -Name $accountName -ErrorAction SilentlyContinue) { throw 'Disposable account already exists.' }
$null = New-Item -Path $evidence -ItemType Directory

$password = [Security.SecureString]::new()
$passwordChars = ('aA9!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))).ToCharArray()
try {
    foreach ($character in $passwordChars) { $password.AppendChar($character) }
    $password.MakeReadOnly()
}
finally { [Array]::Clear($passwordChars, 0, $passwordChars.Length) }

$account = $null
try {
    Add-Type -LiteralPath (Join-Path $PSScriptRoot 'installer-native.cs')
    $account = New-LocalUser -Name $accountName -Password $password -Description 'Disposable Lycoris installer validation account'
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    if (-not (Get-LocalGroupMember -SID $users | Where-Object SID -eq $account.SID)) {
        Add-LocalGroupMember -SID $users -Member $account
    }
    New-TestDirectory -Path $scratch -UserSid $account.SID -UserRights ReadAndExecute
    New-TestDirectory -Path $state -UserSid $account.SID -UserRights Modify
    New-TestDirectory -Path (Join-Path $scratch 'temp') -UserSid $account.SID -UserRights Modify
    # Only this fresh fixture is writable by the test user, so accidental machine-data deletion can be detected.
    New-TestDirectory -Path $machineFixture -UserSid $account.SID -UserRights Modify
    $journal = Join-Path $machineFixture 'journal.json'
    @{ Batches = @(); Records = @(); InstallerValidation = $runName } | ConvertTo-Json -Compress |
        Set-Content -LiteralPath $journal -Encoding utf8

    $worker = Join-Path $scratch 'installer-worker.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'installer-worker.ps1') -Destination $worker
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'installer-native.cs') -Destination $scratch
    Copy-Item -LiteralPath $setup -Destination (Join-Path $scratch 'setup.exe')
    [ordered]@{
        Mode = $Mode; Version = $Version; SetupSha256 = $SetupSha256; PayloadSha256 = $PayloadSha256
        AccountSid = $account.SID.Value; ProfilePath = $profilePath
        JournalSha256 = (Get-FileHash -LiteralPath $journal -Algorithm SHA256).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'fixture.json') -Encoding utf8

    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    $workerExit = [InstallerNative]::RunWorker($runName, $accountName, $account.SID.Value, $password, $pwsh, $worker, $scratch, 1200000)
    $resultPath = Join-Path $state 'result.json'
    if (-not [IO.File]::Exists($resultPath)) { throw "Standard worker returned $workerExit without an evidence report." }
    Assert-DirectPath -Path $resultPath
    if ((Get-Item -LiteralPath $resultPath).Length -gt 64KB) { throw 'Unexpected worker report size.' }
    $raw = Get-Content -LiteralPath $resultPath -Raw
    Write-Output $raw
    $result = $raw | ConvertFrom-Json
    $expectedStatus = if ($Mode -eq 'Baseline') { 'Expected release defects reproduced' } else { 'Passed' }
    $expectedCases = if ($Mode -eq 'Baseline') { 2 } else { 5 }
    if ($workerExit -ne 0 -or $result.Status -cne $expectedStatus -or -not $result.StandardUser -or
        $result.Cases.Count -ne $expectedCases -or ($Mode -eq 'Candidate' -and $null -eq $result.SelfTest)) {
        throw "Installer validation failed ($Mode, worker exit $workerExit). Review the above report."
    }
}
finally {
    try {
        Copy-InstallerEvidence
    }
    finally {
        try {
            if ($null -ne $account) { Remove-LocalUser -SID $account.SID -Confirm:$false }
        }
        finally { $password.Dispose() }
    }
}
