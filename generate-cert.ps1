# Generates the self-signed certificate Topaz serves its HTTPS endpoints with.
# Run once before `docker compose up -d`.

$ErrorActionPreference = 'Stop'
$certDir = Join-Path $PSScriptRoot 'certs'
New-Item -ItemType Directory -Force $certDir | Out-Null

$crt = Join-Path $certDir 'topaz.crt'
$key = Join-Path $certDir 'topaz.key'
if ((Test-Path $crt) -and (Test-Path $key)) { Write-Host "Certificate already present."; return }

# SANs cover both the loopback the repro dials and the *.topaz.local.dev hostnames Topaz signs SAS tokens
# for, so the same certificate works whether or not you add a hosts entry.
$cert = New-SelfSignedCertificate `
    -Subject 'CN=topaz.local.dev' `
    -DnsName 'localhost', 'topaz.local.dev', '*.topaz.local.dev', '*.servicebus.topaz.local.dev' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears(2) `
    -KeyExportPolicy Exportable

$pem = [Convert]::ToBase64String($cert.RawData, 'InsertLineBreaks')
Set-Content $crt "-----BEGIN CERTIFICATE-----`n$pem`n-----END CERTIFICATE-----"

$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$pkcs8 = [Convert]::ToBase64String($rsa.ExportPkcs8PrivateKey(), 'InsertLineBreaks')
Set-Content $key "-----BEGIN PRIVATE KEY-----`n$pkcs8`n-----END PRIVATE KEY-----"

Write-Host "Wrote $crt and $key"
Write-Host ""
Write-Host "To run the data-plane half of the repro, trust it (elevated, one-time):" -ForegroundColor Yellow
Write-Host "  Import-Certificate -FilePath '$crt' -CertStoreLocation Cert:\LocalMachine\Root" -ForegroundColor Yellow