#requires -Version 7.4
<#
.SYNOPSIS
Runs installer scenarios in the standard user's private CI desktop.
.EXAMPLE
The installer integration harness starts this worker with -RunName and loads its fixture profile.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{32}\z')][string]$RunName)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
    throw 'A disposable GitHub-hosted Windows runner is required.'
}
$scratch = Join-Path ([IO.Path]::GetPathRoot([Environment]::SystemDirectory)) "LycorisInstaller-$RunName"
if ($PSScriptRoot -ine $scratch) { throw 'Unexpected standard worker location.' }
$fixture = Get-Content -LiteralPath (Join-Path $scratch 'fixture.json') -Raw | ConvertFrom-Json
$state = Join-Path $scratch 'state'
$report = [ordered]@{
    Mode = $fixture.Mode
    SetupSha256 = $fixture.SetupSha256
    PayloadSha256 = $fixture.PayloadSha256
    Version = $fixture.Version
    StandardUser = $false
    PrivateDesktop = $false
    DesktopEnumeration = $null
    Status = 'Running'
    Phase = 'Verify private desktop and user'
    SelfTests = @()
    Cases = @()
    Error = $null
}
$exitCode = 1
$appId = '{7E6D9A23-748C-4D62-9E36-6632D452A1F0}_is1'
$uninstallKey = "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$appId"

function Assert-Hash {
    param([string]$Path, [string]$Expected, [string]$Label)
    if (-not [IO.File]::Exists($Path) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $Expected) {
        throw "$Label missing or SHA-256 mismatch."
    }
}

function Assert-NoMachineInstall {
    foreach ($view in [Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32) {
        $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        try {
            $key = $machine.OpenSubKey($uninstallKey)
            if ($null -ne $key) {
                $key.Dispose()
                throw "Unexpected HKLM uninstall registration in $view."
            }
        }
        finally { $machine.Dispose() }
    }
    if ([IO.File]::Exists($commonDesktopLink) -or [IO.File]::Exists($commonMenuLink)) {
        throw 'Unexpected all-users shortcut.'
    }
}

function Start-InstallerProcess {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([Diagnostics.Process])]
    param([string]$Path, [string[]]$Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $scratch
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if (-not $PSCmdlet.ShouldProcess($Path, 'Start the verified disposable package process')) {
        throw 'The disposable package process was declined.'
    }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Installer process did not start.' }
    $null = $process.Handle
    return $process
}

function Invoke-InstallerProcess {
    param([string]$Path, [string[]]$Arguments, [int]$TimeoutSeconds, [string]$Label, [switch]$PassThru)
    $process = Start-InstallerProcess -Path $Path -Arguments $Arguments
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw "$Label timed out." }
        if ($PassThru) { return $process.ExitCode }
        if ($process.ExitCode -ne 0) { throw "$Label exited with code $($process.ExitCode)." }
    }
    finally { $process.Dispose() }
}

function Get-InstalledSelfTestResult {
    param([ValidateSet('ru', 'en')][string]$Language, [string]$Directory, [int]$ExitCode)
    $result = [ordered]@{
        Language = $Language
        Status = 'Failed'
        ExitCode = $ExitCode
        WindowsWritesLocked = 'selftest'
        PayloadSha256 = $fixture.PayloadSha256
        ReportPath = "selftest/$Language/report.json"
        MarkdownPath = "selftest/$Language/report.md"
        MissingKeysPath = "selftest/$Language/missing-keys.txt"
        SnapshotCount = 0
        MissingKeys = $null
        BindingDetectorVerified = $false
        RowCounts = [ordered]@{ OK = 0; WARN = 0; FAIL = 0 }
        Sections = @()
        Error = $null
    }
    $json = Join-Path $Directory 'report.json'
    $markdown = Join-Path $Directory 'report.md'
    $missing = Join-Path $Directory 'missing-keys.txt'
    if (-not [IO.File]::Exists($json) -or -not [IO.File]::Exists($markdown) -or -not [IO.File]::Exists($missing)) {
        $result.Error = 'The installed self-test did not produce all report files.'
        return $result
    }
    try {
        $sections = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate
        if ($sections -isnot [array]) { throw 'Expected an array of self-test sections.' }
        $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($section in $sections) {
            if ($section -isnot [Collections.IDictionary] -or [string]::IsNullOrWhiteSpace($section['Key']) -or
                -not $keys.Add($section['Key']) -or $section['Status'] -cnotin @('OK', 'WARN', 'FAIL')) {
                throw 'The self-test report contains an invalid or duplicate section.'
            }
            $row = [ordered]@{ Key = $section['Key']; Title = $section['Title']; Status = $section['Status'] }
            foreach ($field in @('Errors', 'Warnings', 'BindingErrors', 'Unhandled', 'Dialogs')) {
                if ($section[$field] -isnot [array]) { throw "The self-test section lacks a $field array." }
                $row["${field}Count"] = $section[$field].Count
            }
            $row.Warnings = $section['Warnings']
            $result.Sections += $row
            $result.RowCounts[$row.Status]++
        }
        $result.SnapshotCount = @(Get-ChildItem -LiteralPath $Directory -Filter '*.png' -File).Count
        $result.MissingKeys = @(Get-Content -LiteralPath $missing).Count
        $result.BindingDetectorVerified = Select-String -LiteralPath $markdown -SimpleMatch -Quiet -Pattern 'Детектор ошибок привязок: работает (контрольная ошибка поймана)'
        $standardUser = Select-String -LiteralPath $markdown -Quiet -Pattern '^Время: .* · права администратора: нет$'
        $homeTitle = if ($Language -eq 'ru') { 'Главная' } else { 'Home' }
        $localizedHome = @($result.Sections | Where-Object { $_.Key -ceq 'home' -and $_.Title -ceq $homeTitle }).Count -eq 1
        $critical = @($result.Sections | Where-Object { $_.ErrorsCount -or $_.BindingErrorsCount -or $_.UnhandledCount })
        if ($ExitCode -ne 0 -or $result.Sections.Count -ne 20 -or $result.RowCounts.FAIL -ne 0 -or $critical.Count -ne 0) {
            throw 'Expected 20 sections with no failed section, logged error, binding error or unhandled exception.'
        }
        if (-not $standardUser -or -not $localizedHome -or -not $result.BindingDetectorVerified -or $result.SnapshotCount -lt 1) {
            throw 'The self-test did not verify its standard-user context, language, binding detector and snapshots.'
        }
        if ($Language -eq 'en' -and $result.MissingKeys -ne 0) { throw 'English translations are missing.' }
        $result.Status = if ($result.RowCounts.WARN -gt 0) { 'PassedWithWarnings' } else { 'Passed' }
    }
    catch { $result.Error = $_.Exception.Message }
    return $result
}

function Invoke-TestInstall {
    param([string]$CaseName, [bool]$DesktopShortcut)
    Assert-Hash -Path $setup -Expected $fixture.SetupSha256 -Label 'Setup executable'
    $arguments = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/LANG=english',
        "/LOG=$(Join-Path $state "$CaseName-setup.log")")
    if ($DesktopShortcut) { $arguments += '/TASKS=desktopicon' }
    Invoke-InstallerProcess -Path $setup -Arguments $arguments -TimeoutSeconds 90 -Label 'Per-user install'
    Assert-TestInstall -DesktopShortcut $DesktopShortcut
}

function Assert-TestInstall {
    param([bool]$DesktopShortcut)
    Assert-Hash -Path $installedApp -Expected $fixture.PayloadSha256 -Label 'Installed payload'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($installedApp)
    if ($version.ProductName -cne 'Lycoris' -or $version.ProductVersion -cne $fixture.Version -or
        $version.FileVersion -cne "$($fixture.Version).0") {
        throw 'Unexpected installed product or version.'
    }
    $entry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKey)
    if ($null -eq $entry) { throw 'HKCU uninstall registration is absent.' }
    try {
        if ($entry.GetValue('DisplayName') -cnotmatch '\ALycoris(?:\s|\z)' -or $entry.GetValue('DisplayVersion') -cne $fixture.Version -or
            ([string]$entry.GetValue('InstallLocation')).TrimEnd('\') -ine $appFolder -or
            -not [IO.File]::Exists($uninstaller)) {
            throw 'HKCU uninstall metadata does not describe the expected per-user installation.'
        }
    }
    finally { $entry.Dispose() }
    Assert-NoMachineInstall
    if ([IO.File]::Exists($desktopLink) -ne $DesktopShortcut -or -not [IO.File]::Exists($menuLink)) {
        throw 'The default or explicitly selected per-user shortcut is incorrect.'
    }
    if ($DesktopShortcut) {
        $shell = New-Object -ComObject WScript.Shell
        try {
            $shortcut = $shell.CreateShortcut($desktopLink)
            try { if ($shortcut.TargetPath -ine $installedApp) { throw 'Desktop shortcut points at the wrong executable.' } }
            finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        }
        finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}

function Invoke-TestUninstall {
    param([string]$CaseName, [ValidateSet('Silent', 'No', 'Yes')][string]$Choice)
    $arguments = @('/NORESTART', '/NOSTYLE', "/LOG=$(Join-Path $state "$CaseName-uninstall.log")")
    if ($Choice -eq 'Silent') { $arguments += '/VERYSILENT', '/SUPPRESSMSGBOXES' }
    $decision = if ($Choice -eq 'Silent') { $null } else { $Choice -eq 'Yes' }
    $process = Start-InstallerProcess -Path $uninstaller -Arguments $arguments
    $answered = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $observations = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $emptyPasses = 0
    $nextThreadSample = 0
    try {
        while ($timer.Elapsed.TotalSeconds -lt 65) {
            $sampleThreads = $timer.ElapsedMilliseconds -ge $nextThreadSample
            if ($sampleThreads) { $nextThreadSample = $timer.ElapsedMilliseconds + 4000 }
            $count = [InstallerNative]::AnswerUninstallDialogs($RunName, [uint32]$process.Id, $decision, $answered, $observations, $sampleThreads)
            if ($process.HasExited -and $count -eq 0) { $emptyPasses++ } else { $emptyPasses = 0 }
            if ($emptyPasses -ge 4) {
                return [pscustomobject]@{ ExitCode = $process.ExitCode; Dialogs = @($answered) }
            }
            Start-Sleep -Milliseconds 150
        }
        throw "Uninstaller timed out ($Choice). Answered dialogs: $($answered -join ', ')."
    }
    finally {
        $process.Dispose()
        try {
            $observations | Sort-Object | Set-Content -LiteralPath (Join-Path $state "$CaseName-dialogs.log") -Encoding utf8
        }
        catch { Write-Warning 'Could not persist owned uninstaller window observations.' }
    }
}

function Assert-TestUninstall {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ([IO.Directory]::Exists($appFolder) -and $timer.Elapsed.TotalSeconds -lt 18) {
        Start-Sleep -Milliseconds 200
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKey)
    if ($null -ne $key) {
        $key.Dispose()
        throw 'HKCU uninstall registration survived removal.'
    }
    if ([IO.Directory]::Exists($appFolder) -or [IO.File]::Exists($desktopLink) -or [IO.File]::Exists($menuLink)) {
        throw 'Installed files or per-user shortcuts survived removal.'
    }
    Assert-NoMachineInstall
}

function Assert-FreshFixture {
    if ([IO.Directory]::Exists($appFolder) -or [IO.Directory]::Exists($dataFolder) -or
        [IO.File]::Exists($desktopLink) -or [IO.File]::Exists($menuLink) -or [IO.File]::Exists($unrelatedMarker)) {
        throw 'The disposable user profile already contains an installer fixture or Lycoris data.'
    }
    $entry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKey)
    if ($null -ne $entry) {
        $entry.Dispose()
        throw 'The disposable user profile already contains a Lycoris uninstall registration.'
    }
    Assert-NoMachineInstall
}

function New-TestJunction {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Link, [string]$Target)
    $profilePrefix = "$profilePath\"
    foreach ($path in $Link, $Target) {
        if ([IO.Path]::GetFullPath($path) -ine $path -or
            -not $path.StartsWith($profilePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue)) {
            throw 'Expected a new absolute junction fixture inside the disposable profile.'
        }
        for ($parent = [IO.Path]::GetDirectoryName($path); $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
            $directory = Get-Item -LiteralPath $parent -Force
            if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Unexpected reparse point or non-directory ancestor in the junction fixture.'
            }
        }
    }
    if (-not $PSCmdlet.ShouldProcess($Link, 'Create a junction to a fresh disposable marker directory')) {
        throw 'Junction fixture creation was declined.'
    }
    $null = New-Item -Path $Target -ItemType Directory
    $marker = Join-Path $Target 'outside-marker.txt'
    [IO.File]::WriteAllText($marker, "$RunName/$([IO.Path]::GetFileName($Target))")
    $sha = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
    $null = New-Item -Path $Link -ItemType Junction -Target $Target
    $junction = Get-Item -LiteralPath $Link -Force
    if (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or
        $junction.LinkType -cne 'Junction' -or $junction.ResolveLinkTarget($false).FullName -ine $Target) {
        throw 'The uninstall fixture did not create the intended directory junction.'
    }
    Assert-Hash -Path (Join-Path $Link 'outside-marker.txt') -Expected $sha -Label 'Junction marker through its link'
    return [pscustomobject]@{ Path = $marker; Sha256 = $sha }
}

try {
    Add-Type -LiteralPath (Join-Path $scratch 'installer-native.cs')
    [InstallerNative]::RequirePrivateDesktop($RunName)
    $report.PrivateDesktop = $true
    if ($fixture.Mode -cnotin @('Fixture', 'Baseline', 'Candidate') -or
        ($fixture.Mode -ne 'Fixture' -and $fixture.Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') -or
        ($fixture.Mode -eq 'Baseline' -and $fixture.Version -cne '1.0.27') -or
        ($fixture.Mode -eq 'Candidate' -and [version]$fixture.Version -le [version]'1.0.27')) {
        throw 'Invalid package fixture.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if ($identity.User.Value -cne $fixture.AccountSid -or $identity.Name -inotlike "*\lyci_$($RunName.Substring(0, 12))" -or
            $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or
            @($identity.Groups | Where-Object Value -eq 'S-1-5-32-544').Count -ne 0 -or
            -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::User)) {
            throw 'The worker is not running with the disposable standard-user token.'
        }
    }
    finally { $identity.Dispose() }
    if ($fixture.Mode -eq 'Fixture') {
        $report.Phase = 'Probe private desktop before launching a package'
        $observed = [InstallerNative]::ProbePrivateDesktop($RunName)
        $report.DesktopEnumeration = [ordered]@{
            ReturnedTrue = $observed.ReturnedTrue
            Win32Error = $observed.Win32Error
            CallbackCount = $observed.CallbackCount
        }
        if (-not $observed.ReturnedTrue -and ($observed.Win32Error -ne 0 -or $observed.CallbackCount -ne 0)) {
            throw "Unexpected failure before package launch (Win32 $($observed.Win32Error), callbacks $($observed.CallbackCount))."
        }
        $report.Phase = 'Verify the loaded standard-user profile'
    }
    $profileKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        "SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$($fixture.AccountSid)")
    if ($null -eq $profileKey) { throw 'The standard account profile was not loaded.' }
    try {
        $profilePath = [string]$profileKey.GetValue('ProfileImagePath')
        if ($profilePath -ine $fixture.ProfilePath -or -not [IO.Directory]::Exists($profilePath)) {
            throw 'The standard worker profile path does not match the fresh fixture.'
        }
    }
    finally { $profileKey.Dispose() }
    $env:USERPROFILE = $profilePath
    $env:USERNAME = 'lyci_' + $RunName.Substring(0, 12)
    $env:LOCALAPPDATA = Join-Path $profilePath 'AppData\Local'
    $env:APPDATA = Join-Path $profilePath 'AppData\Roaming'
    $env:ProgramData = [Environment]::GetFolderPath('CommonApplicationData')
    $localData = [Environment]::GetFolderPath('LocalApplicationData', 'Create')
    if ($localData -ine $env:LOCALAPPDATA) { throw 'The default local application folder is not the fixture profile.' }
    $report.StandardUser = $true
    if ($fixture.Mode -eq 'Fixture') {
        if ([IO.File]::Exists((Join-Path $scratch 'setup.exe'))) { throw 'The fixture-only worker received a package.' }
        $report.Phase = 'Done'
        $report.Status = 'Fixture ready'
        exit 0
    }

    $setup = Join-Path $scratch 'setup.exe'
    $appFolder = Join-Path $localData 'Programs\Lycoris'
    $installedApp = Join-Path $appFolder 'Lycoris.exe'
    $uninstaller = Join-Path $appFolder 'unins000.exe'
    $dataFolder = Join-Path $localData 'Lycoris'
    $userMarker = Join-Path $dataFolder 'installer-validation-marker.txt'
    $unrelatedMarker = Join-Path $localData "InstallerValidation-$RunName.txt"
    $desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory', 'Create')) 'Lycoris.lnk'
    $menuLink = Join-Path ([Environment]::GetFolderPath('Programs', 'Create')) 'Lycoris\Lycoris.lnk'
    if (-not $desktopLink.StartsWith("$profilePath\", [StringComparison]::OrdinalIgnoreCase) -or
        -not $menuLink.StartsWith("$profilePath\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The standard profile does not own its desktop and Programs folders.'
    }
    $commonDesktopLink = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Lycoris.lnk'
    $commonMenuLink = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Lycoris\Lycoris.lnk'
    $machineMarker = Join-Path $env:ProgramData 'Lycoris\journal.json'
    Assert-FreshFixture
    Assert-Hash -Path $machineMarker -Expected $fixture.JournalSha256 -Label 'Machine journal fixture'
    [IO.File]::WriteAllText($unrelatedMarker, $RunName)
    $unrelatedSha = (Get-FileHash -LiteralPath $unrelatedMarker -Algorithm SHA256).Hash

    $routes = if ($fixture.Mode -eq 'Baseline') { 'No', 'Yes' } else { 'Silent', 'No', 'Yes', 'YesNestedJunction', 'YesRootJunction' }
    $outsideMarkers = @()
    foreach ($route in $routes) {
        $report.Phase = "$route install"
        $desktopShortcut = $route -eq 'Yes'
        $choice = if ($route.StartsWith('Yes', [StringComparison]::Ordinal)) { 'Yes' } else { $route }
        $junctionLink = switch ($route) {
            'YesNestedJunction' { Join-Path $dataFolder 'installer-validation-link' }
            'YesRootJunction' { $dataFolder }
            default { $null }
        }
        Invoke-TestInstall -CaseName $route -DesktopShortcut $desktopShortcut
        if ($fixture.Mode -eq 'Candidate' -and $route -eq 'Silent') {
            foreach ($language in 'ru', 'en') {
                $report.Phase = "Installed executable locked self-test ($language)"
                $snapshots = Join-Path $state "selftest/$language"
                $testArguments = @('--selftest', $snapshots, '--lang', $language, '--no-elevate')
                Assert-Hash -Path $installedApp -Expected $fixture.PayloadSha256 -Label 'Payload before self-test'
                $testExit = Invoke-InstallerProcess -Path $installedApp -Arguments $testArguments -TimeoutSeconds 600 -Label "Installed self-test ($language)" -PassThru
                Assert-Hash -Path $installedApp -Expected $fixture.PayloadSha256 -Label 'Payload after self-test'
                Assert-Hash -Path $machineMarker -Expected $fixture.JournalSha256 -Label 'Machine journal after self-test'
                $report.SelfTests += Get-InstalledSelfTestResult -Language $language -Directory $snapshots -ExitCode $testExit
            }
        }
        if ($route -ne 'YesRootJunction') {
            $null = [IO.Directory]::CreateDirectory($dataFolder)
            [IO.File]::WriteAllText($userMarker, "$RunName/$route")
            $markerSha = (Get-FileHash -LiteralPath $userMarker -Algorithm SHA256).Hash
        }
        if ($null -ne $junctionLink) {
            $report.Phase = "$route junction fixture"
            $outside = Join-Path $localData "InstallerJunctionTarget-$RunName-$route"
            $outsideMarkers += New-TestJunction -Link $junctionLink -Target $outside
        }
        $report.Phase = "$route uninstall"
        $uninstall = Invoke-TestUninstall -CaseName $route -Choice $choice
        if ($choice -eq 'Silent') {
            if ($uninstall.Dialogs.Count -ne 0 -or $uninstall.ExitCode -ne 0) { throw 'Silent uninstall did not exit quietly.' }
        }
        elseif ($uninstall.Dialogs -notcontains 'data') {
            throw 'The actual data confirmation dialog was not observed and answered.'
        }
        Assert-Hash -Path $machineMarker -Expected $fixture.JournalSha256 -Label 'Machine journal after uninstall'
        Assert-Hash -Path $unrelatedMarker -Expected $unrelatedSha -Label 'Unrelated local file after uninstall'
        foreach ($marker in $outsideMarkers) {
            Assert-Hash -Path $marker.Path -Expected $marker.Sha256 -Label 'Outside junction marker after uninstall'
        }

        if ($fixture.Mode -eq 'Baseline' -and $route -eq 'No') {
            $logFile = Join-Path $state 'No-uninstall.log'
            if ($uninstall.ExitCode -eq 0 -or $uninstall.Dialogs.Count -ne 1 -or
                -not (Select-String -LiteralPath $logFile -Pattern 'InitializeUninstall returned False; aborting.' -SimpleMatch -Quiet)) {
                throw 'Baseline No did not reproduce the known initialization abort.'
            }
            Assert-TestInstall -DesktopShortcut $false
            Assert-Hash -Path $userMarker -Expected $markerSha -Label 'User marker after baseline cancellation'
            $report.Cases += [ordered]@{
                Choice = $route; Result = 'RED: No unexpectedly aborted uninstall'; Dialogs = $uninstall.Dialogs
                ExitCode = $uninstall.ExitCode; AppRemoved = $false; UserDataRemoved = $false; MachineJournalPreserved = $true
            }
            $report.Phase = 'Dispose of the baseline No installation in its disposable profile'
            $cleanup = Invoke-TestUninstall -CaseName 'BaselineNoCleanup' -Choice 'Silent'
            if ($cleanup.ExitCode -ne 0 -or $cleanup.Dialogs.Count -ne 0) { throw 'Baseline cleanup failed.' }
            Assert-TestUninstall
            continue
        }

        if ($uninstall.ExitCode -ne 0 -or ($choice -ne 'Silent' -and
            ($uninstall.Dialogs -notcontains 'confirm' -or $uninstall.Dialogs -notcontains 'complete'))) {
            throw "Uninstaller did not report completed removal ($route)."
        }
        Assert-TestUninstall
        $deleteExpected = $fixture.Mode -eq 'Candidate' -and $choice -eq 'Yes'
        if ($deleteExpected) {
            if ([IO.Directory]::Exists($dataFolder)) { throw 'Yes did not remove the local Lycoris data folder.' }
        }
        else { Assert-Hash -Path $userMarker -Expected $markerSha -Label 'User marker after uninstall' }
        $result = if ($fixture.Mode -eq 'Baseline') { 'RED: Yes unexpectedly preserved local user data' } else { 'GREEN' }
        $report.Cases += [ordered]@{
            Case = $route; Choice = $choice; Result = $result; Dialogs = $uninstall.Dialogs; ExitCode = $uninstall.ExitCode
            AppRemoved = $true; UserDataRemoved = $deleteExpected; MachineJournalPreserved = $true
            JunctionTargetPreserved = if ($null -ne $junctionLink) { $true } else { $null }
        }
    }
    if ($fixture.Mode -eq 'Candidate') {
        $report.Phase = 'Validate packaged self-tests'
        if ($report.SelfTests.Count -ne 2 -or @($report.SelfTests | Where-Object Status -eq 'Failed').Count -ne 0) {
            throw 'One or more packaged self-tests failed. See SelfTests and the per-language reports.'
        }
    }
    $report.Phase = 'Done'
    $report.Status = if ($fixture.Mode -eq 'Baseline') { 'Expected release defects reproduced' } else { 'Passed' }
    $exitCode = 0
}
catch {
    $report.Error = $_.Exception.Message
    $report.Status = 'Failed'
}
finally {
    $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $state 'result.json') -Encoding utf8
}
exit $exitCode
