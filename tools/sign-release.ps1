<#
.SYNOPSIS
    Authenticode-signs and timestamps a published 66mods Tweaker executable.

.DESCRIPTION
    An unsigned build shows a SmartScreen warning on every download, and an installer-shaped 75 MB
    self-extracting binary is exactly the shape heuristic antivirus engines flag. Signing removes the
    publisher warning and lets reputation accumulate against the certificate rather than each new file.

    The certificate is referenced by thumbprint from the Windows certificate store, so no password or
    key material is passed on the command line or stored in this repository. Import the certificate once
    (double-click the .pfx, choose Current User), then read its thumbprint with -ListCertificates.

    Timestamping is mandatory: without it every signature stops validating the day the certificate
    expires, including on copies users already downloaded.

.EXAMPLE
    .\tools\sign-release.ps1 -ListCertificates

.EXAMPLE
    .\tools\sign-release.ps1 -Thumbprint AABB...   -Path "artifacts\66mods-tweaker-0.9.1\66mods Tweaker.exe"
#>
[CmdletBinding()]
param(
    [string] $Thumbprint,
    [string] $Path = "artifacts\66mods-tweaker-0.9.1\66mods Tweaker.exe",
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $ListCertificates
)

$ErrorActionPreference = 'Stop'

if ($ListCertificates) {
    $certs = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue) +
             @(Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue)
    if ($certs.Count -eq 0) {
        Write-Host 'No code signing certificate is installed.' -ForegroundColor Yellow
        Write-Host 'Import the .pfx from your certificate authority first, then re-run this.'
        return
    }
    $certs | Select-Object Subject, NotAfter, Thumbprint | Format-List
    return
}

if (-not $Thumbprint) { throw 'Pass -Thumbprint, or run with -ListCertificates to find one.' }

$target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' $Path))
if (-not (Test-Path -LiteralPath $target)) { throw "Executable not found: $target" }

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK "Signing Tools" component.' }

Write-Host "Signing $target" -ForegroundColor Cyan
& $signtool.FullName sign /sha1 $Thumbprint /fd SHA256 /td SHA256 /tr $TimestampUrl /d '66mods Tweaker' $target
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }

# Verify as Windows will: the signature must chain to a trusted root and carry a countersignature.
& $signtool.FullName verify /pa /all $target
if ($LASTEXITCODE -ne 0) { throw "Signature verification failed with exit code $LASTEXITCODE." }

$signature = Get-AuthenticodeSignature -LiteralPath $target
Write-Host ''
Write-Host "Status     : $($signature.Status)" -ForegroundColor Green
Write-Host "Signer     : $($signature.SignerCertificate.Subject)"
Write-Host "Timestamp  : $(if ($signature.TimeStamperCertificate) { 'present' } else { 'MISSING - re-sign with a reachable timestamp server' })"
Write-Host "SHA-256    : $((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash)"
Write-Host ''
Write-Host 'Publish this exact file. Re-signing or rebuilding changes the hash and restarts reputation.' -ForegroundColor Yellow
