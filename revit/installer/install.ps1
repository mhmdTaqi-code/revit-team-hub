# Team Hub for Revit — installs for the current Windows user (no admin needed).
# Supports Revit 2025 and 2026 (both .NET 8). Run from the folder that has TeamHub.Revit.dll.
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $env:APPDATA 'TeamHub\revit\bin'
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $src 'TeamHub.Revit.dll') $bin -Force
$dll = Join-Path $bin 'TeamHub.Revit.dll'

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Team Hub</Name>
    <Assembly>$dll</Assembly>
    <AddInId>6f0e5c8a-3b1e-4d7c-9a55-2c1e9b7d4a10</AddInId>
    <FullClassName>TeamHub.Revit.App</FullClassName>
    <VendorId>TEAMHUB</VendorId>
    <VendorDescription>Team Hub — live team work on one Revit project</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

$installed = @()
foreach ($v in 2025, 2026) {
  $dir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
  New-Item -ItemType Directory -Force $dir | Out-Null
  Set-Content -Path (Join-Path $dir 'TeamHub.addin') -Value $manifest -Encoding UTF8
  if (Test-Path "C:\Program Files\Autodesk\Revit $v") { $installed += $v }
}
Write-Host "Team Hub installed for Revit 2025 and 2026."
if ($installed.Count) { Write-Host ("Revit found on this PC: " + ($installed -join ', ')) }
$cer = Join-Path $src 'TeamHub-Revit.cer'
$trusted = (Test-Path $cer) -and (Get-ChildItem Cert:\CurrentUser\TrustedPublisher | Where-Object { $_.Thumbprint -eq (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint })
if ((Test-Path $cer) -and -not $trusted) {
  Write-Host ""
  Write-Host "The add-in is signed by Team Hub. Trust that signature so Revit never asks again"
  Write-Host "(only files signed with this certificate are affected; Windows will ask you to confirm)."
  if ((Read-Host "Trust it? (Y/N)") -match '^[Yy]') { & (Join-Path $src 'trust-certificate.ps1') }
}
Write-Host "Open Revit. If it still asks about the add-in, choose 'Always Load'."
