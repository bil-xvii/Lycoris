# The owner's RELEASE SIGNING KEY for the update channel (docs/security-audit.md, "Update channel requirements").
#
# Why: the SHA-256 published next to Lycoris.exe comes from the same server as the file. Whoever takes over the GitHub account can
# replace both. A signature made with a key that is NOT on GitHub fixes that: installed copies carry the PUBLIC half
# (Core/Security/ReleaseSignature.cs, PinnedPublicKey) and install an update only when its signature verifies.
#
#   powershell -ExecutionPolicy Bypass -File tools\release-key.ps1 -New        # once: makes the key pair, prints the public key
#   powershell -ExecutionPolicy Bypass -File tools\release-key.ps1 -Sign -File publish\Lycoris.exe -Version 1.2.3
#                                                                              # (tools\release.ps1 calls this) writes <file>.sig
#   powershell -ExecutionPolicy Bypass -File tools\release-key.ps1 -Show       # prints the public key again
#
# The private key stays on this PC: %USERPROFILE%\.monolith-release\release-key.pk8 (readable only by your account). NEVER commit it,
# never upload it. Back it up somewhere offline: without it you cannot ship updates to copies that already carry the public half
# (they would need a manual re-install).
[CmdletBinding()]
param(
    [switch]$New,
    [switch]$Sign,
    [switch]$Show,
    [string]$File,
    [string]$Version,
    [string]$KeyFile = $(if ($env:MONOLITH_RELEASE_KEY) { $env:MONOLITH_RELEASE_KEY } else { Join-Path $env:USERPROFILE '.monolith-release\release-key.pk8' })
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

# ASN.1 header of a SubjectPublicKeyInfo for an uncompressed ECDSA P-256 point (id-ecPublicKey, prime256v1).
$SpkiPrefix = [byte[]](0x30,0x59,0x30,0x13,0x06,0x07,0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01,0x06,0x08,0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07,0x03,0x42,0x00,0x04)

function Get-PublicKeyBase64([System.Security.Cryptography.CngKey]$key) {
    $blob = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)   # magic(4) cbKey(4) X(32) Y(32)
    $xy = New-Object byte[] 64
    [Array]::Copy($blob, 8, $xy, 0, 64)
    [Convert]::ToBase64String($SpkiPrefix + $xy)
}

function Import-Key {
    if (-not (Test-Path $KeyFile)) { throw "No release key at $KeyFile - run: tools\release-key.ps1 -New" }
    $bytes = [Convert]::FromBase64String(([IO.File]::ReadAllText($KeyFile)).Trim())
    [System.Security.Cryptography.CngKey]::Import($bytes, [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
}

if ($New) {
    if (Test-Path $KeyFile) { throw "A key already exists at $KeyFile. Refusing to overwrite it (copies of the app carrying its public half would stop accepting your updates)." }
    $dir = Split-Path -Parent $KeyFile
    New-Item -ItemType Directory -Force $dir | Out-Null
    $p = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $p.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $null, $p)
    $pk8 = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    [IO.File]::WriteAllText($KeyFile, [Convert]::ToBase64String($pk8))
    # Only the current account (and SYSTEM) may read it.
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $KeyFile /inheritance:r /grant:r "${me}:(R,W)" 'SYSTEM:(R)' | Out-Null
    $pub = Get-PublicKeyBase64 $key
    Write-Host "Private key written to $KeyFile (back it up OFFLINE; never commit it)." -ForegroundColor Green
    Write-Host ''
    Write-Host 'Public key - put it into src\Monolith\Core\Security\ReleaseSignature.cs:'
    Write-Host "    public const string PinnedPublicKey = `"$pub`";"
    Write-Host ''
    Write-Host 'From the first release built with that line, every release MUST carry Lycoris.exe.sig (tools\release.ps1 adds it).'
    exit 0
}

if ($Show) {
    Write-Host (Get-PublicKeyBase64 (Import-Key))
    exit 0
}

if ($Sign) {
    if (-not $File -or -not (Test-Path $File)) { throw '-File <path to Lycoris.exe> is required' }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '-Version x.y.z is required' }
    $hash = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    # Exactly the text ReleaseSignature.SignedStatement builds: it binds the hash to the version AND the file name.
    $statement = "monolith-release`nversion=$Version`nfile=Lycoris.exe`nsha256=$hash`n"
    $key = Import-Key
    $ecdsa = New-Object System.Security.Cryptography.ECDsaCng($key)
    $ecdsa.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
    $sig = $ecdsa.SignData([Text.Encoding]::UTF8.GetBytes($statement))   # 64 bytes, r||s (IEEE P1363)
    $out = "$File.sig"
    [IO.File]::WriteAllText($out, [Convert]::ToBase64String($sig) + "`n", (New-Object Text.UTF8Encoding $false))
    Write-Host "Signed $File ($Version, sha256 $hash) -> $out"
    exit 0
}

Write-Host 'Use -New, -Sign -File <exe> -Version x.y.z, or -Show.'
exit 2
