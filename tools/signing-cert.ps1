# One-time setup on YOUR OWN PC: creates a personal code-signing certificate "CN=Lycoris" and makes this PC trust it,
# so the UAC prompt shows «Проверенный издатель: Lycoris» instead of «Издатель: неизвестно».
# The trust is local: other PCs still see "unknown publisher" (that needs a certificate from a public CA, see docs/signing.md).
#
# Usage (PowerShell as administrator):
#   powershell -ExecutionPolicy Bypass -File tools\signing-cert.ps1           create (or reuse) and trust
#   powershell -ExecutionPolicy Bypass -File tools\signing-cert.ps1 -Remove   undo: remove the certificate and the trust
#
# What it changes, exactly:
#   1. Cert:\CurrentUser\My            — a self-signed certificate, Code Signing usage only, private key NOT exportable.
#   2. Cert:\LocalMachine\Root         — the public part of that certificate (no private key), so Windows trusts signatures made
#                                        with it on this PC. It is valid only for code signing (no web sites, no e-mail).
# Nothing else is touched. -Remove deletes exactly these two entries.
#Requires -RunAsAdministrator
param(
    [string]$Subject = 'CN=Lycoris',
    [int]$Years = 5,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$friendly = 'Lycoris code signing (this PC only)'

function Find-LycorisCerts([string]$store) {
    Get-ChildItem $store -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject -and $_.FriendlyName -eq $friendly }
}

if ($Remove) {
    $mine = @(Find-LycorisCerts 'Cert:\CurrentUser\My')
    # The Root copy is imported from a .cer, which carries no FriendlyName: match our own self-issued certificate by
    # subject = issuer = $Subject, or by the thumbprint of a certificate this script created.
    $thumbs = @($mine | ForEach-Object Thumbprint)
    $roots = @(Get-ChildItem Cert:\LocalMachine\Root | Where-Object {
        ($_.Subject -eq $Subject -and $_.Issuer -eq $Subject) -or ($thumbs -contains $_.Thumbprint) })
    foreach ($c in $roots) { Remove-Item -LiteralPath "Cert:\LocalMachine\Root\$($c.Thumbprint)"; "Доверие снято: $($c.Thumbprint)" }
    foreach ($c in $mine) { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($c.Thumbprint)" -DeleteKey; "Сертификат удалён: $($c.Thumbprint)" }
    if (-not $roots -and -not $mine) { 'Сертификата Lycoris на этом ПК нет — удалять нечего.' }
    return
}

$cert = Find-LycorisCerts 'Cert:\CurrentUser\My' |
    Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if ($cert) {
    "Сертификат уже есть: $($cert.Thumbprint) (действует до $($cert.NotAfter.ToString('dd.MM.yyyy')))."
} else {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -FriendlyName $friendly `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears($Years)
    "Создан сертификат: $($cert.Thumbprint) (действует до $($cert.NotAfter.ToString('dd.MM.yyyy')))."
}

if (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $cert.Thumbprint) {
    'Этот ПК уже доверяет сертификату.'
} else {
    $cer = Join-Path $env:TEMP "lycoris-signing-$($cert.Thumbprint).cer"
    try {
        Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null   # public part only
        Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    } finally {
        Remove-Item -LiteralPath $cer -Force -ErrorAction SilentlyContinue
    }
    'Готово: этот ПК доверяет подписи Lycoris.'
}

''
'Дальше: tools\publish.ps1 подписывает Lycoris.exe автоматически (или вручную: tools\sign.ps1).'
'Важно: закрытый ключ остаётся в вашей учётной записи и не экспортируется. Не давайте никому доступ к ней;'
'отменить всё можно командой: tools\signing-cert.ps1 -Remove'
