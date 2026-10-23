# Trust Team Hub's signing certificate for this Windows user, so Revit loads
# the add-in (and every update of it) without the "Unsigned Add-In" question.
# Only add-ins signed with this exact certificate are affected. Windows asks
# you to confirm. To undo: run certmgr.msc and delete "Team Hub Revit Add-in"
# from "Trusted Root Certification Authorities" and "Trusted Publishers".
$ErrorActionPreference = 'Stop'
$cer = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'TeamHub-Revit.cer'
if (-not (Test-Path $cer)) { Write-Host "TeamHub-Revit.cer not found next to this script."; exit 1 }
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
Write-Host "Done: Revit will load Team Hub without asking."
