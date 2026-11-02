# Signs Lycoris.exe with Authenticode (SHA-256 + RFC 3161 timestamp), so Windows shows a publisher name instead of
# «Издатель: неизвестно». Certificate sources, first match wins:
#   -PfxPath <file>      a purchased certificate in a .pfx file (asks for its password)
#   -Thumbprint <hex>    a certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My (also hardware tokens / cloud HSM
#                        certificates that are visible in the store)
#   (default)            the personal "CN=Lycoris" certificate made by tools\signing-cert.ps1
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\sign.ps1 [-File publish\Lycoris.exe] [-Thumbprint ...] [-NoTimestamp]
# Exit code 0 = signed and the signature is valid on this PC; 2 = no certificate available (nothing changed); 1 = error.
param(
    [string]$File = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\Lycoris.exe'),
    [string]$Thumbprint,
    [string]$PfxPath,
    [string]$Subject = 'CN=Lycoris',
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'
$File = [IO.Path]::GetFullPath($File)
if (-not (Test-Path -LiteralPath $File)) { Write-Error "Нет файла: $File"; exit 1 }

# A single-file app reads parts of itself lazily: never modify Lycoris.exe while it runs.
$running = Get-Process -Name Lycoris -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq $File) }
if ($running) { Write-Error 'Lycoris.exe сейчас запущен из этого файла — закройте программу и повторите.'; exit 1 }

$cert = $null
if ($PfxPath) {
    $password = Read-Host "Пароль от $PfxPath" -AsSecureString
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $PfxPath).Path, $password)
} elseif ($Thumbprint) {
    $t = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    $cert = Get-ChildItem "Cert:\CurrentUser\My\$t", "Cert:\LocalMachine\My\$t" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cert) { Write-Error "Сертификат $t не найден в хранилищах «Личные»."; exit 1 }
} else {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        "Сертификата для подписи нет — файл не подписан. Один раз выполните tools\signing-cert.ps1 (от администратора)"
        "или укажите купленный сертификат: -PfxPath / -Thumbprint. Подробно: docs\signing.md"
        exit 2
    }
}
if (-not $cert.HasPrivateKey) { Write-Error 'У сертификата нет закрытого ключа — подписать им нельзя.'; exit 1 }

$params = @{ FilePath = $File; Certificate = $cert; HashAlgorithm = 'SHA256' }
if (-not $NoTimestamp) { $params.TimestampServer = $TimestampServer }
$result = Set-AuthenticodeSignature @params
if (-not $result.SignerCertificate) { Write-Error "Подпись не поставлена: $($result.StatusMessage)"; exit 1 }

$check = Get-AuthenticodeSignature -LiteralPath $File
"Подписано: $File"
"Издатель: $($check.SignerCertificate.Subject)  ·  отпечаток $($check.SignerCertificate.Thumbprint)"
if ($check.TimeStamperCertificate) { "Метка времени: $($check.TimeStamperCertificate.Subject)" }
if ($check.Status -eq 'Valid') {
    'Проверка подписи на этом ПК: действительна.'
    exit 0
}
"Подпись поставлена, но этот ПК ей пока не доверяет ($($check.Status): $($check.StatusMessage))."
'Для личного сертификата выполните tools\signing-cert.ps1 от администратора.'
exit 1
